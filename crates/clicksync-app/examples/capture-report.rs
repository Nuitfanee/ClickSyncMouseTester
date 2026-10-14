use clicksync_app::backend::{
    AppStatus, Backend, CapturePhase, TRANSPORT_MANAGED_RESERVATION_BYTES,
};
use clicksync_core::protocol::{
    BATCH_FLUSH_INTERVAL_MS, CAPTURE_METADATA_RESERVATION_BYTES, CAPTURE_QUEUE_RECORDS,
    CaptureSource, CapturedRecord, ControlCommand, FRAME_HEADER_BYTES, MAX_BATCH_RECORDS,
    MAX_CAPTURE_DEVICE_FIELD_BYTES, MAX_CAPTURE_DEVICE_IDENTITIES, MAX_FRAME_PAYLOAD_BYTES,
    PROTOCOL_VERSION, WIRE_RECORD_BYTES,
};
use clicksync_core::{RawMouseMovementMode, RawMouseRecord, SessionConfig, SessionEvent};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};
use std::path::Path;
use std::thread;
use std::time::{Duration, Instant};

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Fixture {
    schema_version: u32,
    build_configuration: String,
    warmup_rounds: usize,
    measured_rounds: usize,
    duration_ms: u64,
    command_timeout_ms: u64,
    workload_timeout_ms: u64,
    observer_modes: Vec<ObserverMode>,
    replay_definition: ReplayDefinition,
    noise_policy: NoisePolicy,
    measurement_definitions: BTreeMap<String, String>,
    workloads: Vec<Workload>,
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct ObserverMode {
    id: String,
    poll_interval_ms: u64,
    retain_progress: bool,
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct ReplayDefinition {
    timestamp_frequency: u64,
    first_raw_ticks: u64,
    delta_x_period: u64,
    delta_x_offset: i32,
    delta_y_period: u64,
    delta_y_offset: i32,
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct NoisePolicy {
    relative_tolerance_percent: f64,
    observation_absolute_ms: f64,
    hot_path_absolute_ns: f64,
    action: String,
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Workload {
    id: String,
    device_keys: Vec<u32>,
    reports_per_second: u32,
    records_per_device: u64,
}

#[derive(Serialize)]
struct Report<'a> {
    schema_version: u32,
    stage: &'static str,
    source: &'static str,
    passed: bool,
    performance_acceptance: &'static str,
    high_rate_hardware: &'static str,
    fixture: &'a Fixture,
    configuration: serde_json::Value,
    workloads: Vec<WorkloadReport>,
    error: Option<String>,
}

#[derive(Serialize)]
struct WorkloadReport {
    id: String,
    cold_backend_start_ns: u64,
    warmup_rounds: Vec<Round>,
    measured_rounds: Vec<Round>,
    aggregate: BTreeMap<String, BTreeMap<String, Statistics>>,
}

#[derive(Serialize)]
struct Round {
    ordinal: usize,
    observer_mode: String,
    generation: u64,
    records: u64,
    start_ack_ns: u64,
    all_committed_ns: u64,
    observed_records_per_second: f64,
    stop_ack_ns: u64,
    stop_drained_ns: u64,
    stop_cutoff: u64,
    stop_queued: u64,
    reset_ack_ns: u64,
    reset_observed_ns: u64,
    snapshot_request_ns: u64,
    wire_record_payload_bytes: u64,
    verified_first_dataset_records: usize,
    status: AppStatus,
    progress: Vec<Observation>,
}

#[derive(Serialize)]
struct Observation {
    elapsed_ns: u64,
    received_records: u64,
    session_watermark: u64,
    captured_status_counter: u64,
    sent_status_counter: u64,
    managed_reserved_bytes: usize,
}

#[derive(Serialize)]
struct Statistics {
    count: usize,
    min: u64,
    p50: u64,
    p95: u64,
    p99: u64,
    p999: u64,
    max: u64,
    repeat_noise_exceeded: bool,
}

fn require(condition: bool, message: impl Into<String>) -> Result<(), String> {
    if condition {
        Ok(())
    } else {
        Err(message.into())
    }
}

fn validate_fixture(fixture: &Fixture) -> Result<(), String> {
    require(
        !cfg!(debug_assertions),
        "capture measurements require Release",
    )?;
    require(
        fixture.schema_version == 1
            && fixture.build_configuration == "release"
            && fixture.warmup_rounds == 1
            && fixture.measured_rounds == 5
            && fixture.duration_ms == 1000
            && fixture.command_timeout_ms > 0
            && fixture.command_timeout_ms <= 10_000
            && fixture.workload_timeout_ms > fixture.command_timeout_ms
            && fixture.workload_timeout_ms <= 30_000
            && fixture.observer_modes.len() == 2,
        "invalid fixed Capture measurement configuration",
    )?;
    let definition = &fixture.replay_definition;
    require(
        definition.timestamp_frequency == 10_000_000
            && definition.first_raw_ticks == 1_000_000
            && definition.delta_x_period == 7
            && definition.delta_x_offset == -3
            && definition.delta_y_period == 5
            && definition.delta_y_offset == -2,
        "unsupported frozen replay oracle",
    )?;
    require(
        fixture.observer_modes[0].id == "sampled"
            && fixture.observer_modes[0].poll_interval_ms == 1
            && fixture.observer_modes[0].retain_progress
            && fixture.observer_modes[1].id == "minimal"
            && fixture.observer_modes[1].poll_interval_ms == 10
            && !fixture.observer_modes[1].retain_progress,
        "observer modes differ from the fixed definitions",
    )?;
    require(
        fixture.noise_policy.relative_tolerance_percent == 20.0
            && fixture.noise_policy.observation_absolute_ms == 2.0
            && fixture.noise_policy.hot_path_absolute_ns == 50.0,
        "noise policy differs from the fixed definitions",
    )?;
    let mut combinations = HashSet::new();
    let mut ids = HashSet::new();
    for workload in &fixture.workloads {
        require(
            matches!(workload.device_keys.as_slice(), [1] | [1, 2])
                && matches!(workload.reports_per_second, 1000 | 8000)
                && workload.records_per_device == u64::from(workload.reports_per_second)
                && ids.insert(&workload.id)
                && combinations.insert((workload.device_keys.len(), workload.reports_per_second)),
            format!("invalid or duplicate fixed workload: {}", workload.id),
        )?;
    }
    require(
        combinations.len() == 4,
        "required workload matrix is incomplete",
    )
}

fn nanoseconds(duration: Duration) -> u64 {
    u64::try_from(duration.as_nanos()).unwrap_or(u64::MAX)
}

fn wait_until(
    backend: &Backend,
    timeout: Duration,
    poll_interval: Duration,
    condition: impl Fn(&AppStatus) -> bool,
) -> Result<AppStatus, String> {
    let deadline = Instant::now() + timeout;
    loop {
        let status = backend.state();
        require(
            status.phase != CapturePhase::Faulted && status.error.is_none(),
            format!("capture failed: {status:?}"),
        )?;
        if condition(&status) {
            return Ok(status);
        }
        require(
            Instant::now() < deadline,
            format!("public capture status timed out: {status:?}"),
        )?;
        thread::sleep(poll_interval);
    }
}

fn measure_round(
    backend: &Backend,
    fixture: &Fixture,
    workload: &Workload,
    mode: &ObserverMode,
    ordinal: usize,
) -> Result<Round, String> {
    let command_timeout = Duration::from_millis(fixture.command_timeout_ms);
    let workload_timeout = Duration::from_millis(fixture.workload_timeout_ms);
    let poll_interval = Duration::from_millis(mode.poll_interval_ms);
    let generation = ordinal as u64 * 2 + 1;
    let expected = workload.records_per_device * workload.device_keys.len() as u64;
    let started = Instant::now();
    let ack = backend.execute(
        ControlCommand::Start {
            generation,
            device_keys: workload.device_keys.clone(),
            source: CaptureSource::Replay {
                records_per_device: workload.records_per_device,
                reports_per_second: workload.reports_per_second,
                real_time: true,
            },
            start_fresh: true,
        },
        command_timeout,
    )?;
    let start_ack_ns = nanoseconds(started.elapsed());
    require(
        ack.complete
            && ack.generation == generation
            && ack.cutoff == 0
            && ack.timestamp_frequency == fixture.replay_definition.timestamp_frequency,
        "Start acknowledgment differs from the fixed workload",
    )?;
    let max_observations = usize::try_from(fixture.workload_timeout_ms / mode.poll_interval_ms + 2)
        .map_err(|error| error.to_string())?;
    let mut progress = Vec::with_capacity(if mode.retain_progress {
        max_observations
    } else {
        0
    });
    let mut previous_count = 0;
    let mut previous_observation = None;
    loop {
        let status = backend.state();
        require(
            status.phase != CapturePhase::Faulted && status.error.is_none(),
            format!("capture failed during replay: {status:?}"),
        )?;
        if status.generation == generation {
            require(
                status.received_records >= previous_count
                    && status.received_records <= expected
                    && status.received_sequence == status.received_records
                    && status.overflow == 0,
                format!("reordered, lost, or excess controlled records: {status:?}"),
            )?;
            previous_count = status.received_records;
            let observation = (status.received_records, status.captured, status.sent);
            if mode.retain_progress && previous_observation != Some(observation) {
                require(
                    progress.len() < max_observations,
                    "diagnostic observation capacity exhausted",
                )?;
                progress.push(Observation {
                    elapsed_ns: nanoseconds(started.elapsed()),
                    received_records: status.received_records,
                    session_watermark: status.received_sequence,
                    captured_status_counter: status.captured,
                    sent_status_counter: status.sent,
                    managed_reserved_bytes: status.managed_reserved_bytes,
                });
                previous_observation = Some(observation);
            }
            if status.received_records == expected {
                break;
            }
        }
        require(
            started.elapsed() < workload_timeout,
            format!("controlled replay did not commit within its deadline: {status:?}"),
        )?;
        thread::sleep(poll_interval);
    }
    let all_committed_ns = nanoseconds(started.elapsed());
    let stop_started = Instant::now();
    let stopped = backend.execute(ControlCommand::Stop, command_timeout)?;
    let stop_ack_ns = nanoseconds(stop_started.elapsed());
    require(
        stopped.complete
            && stopped.generation == generation
            && stopped.cutoff == expected
            && stopped.queued == expected
            && stopped.overflow == 0
            && stopped.queue_high_watermark <= CAPTURE_QUEUE_RECORDS,
        "Stop did not acknowledge the exact complete session cutoff",
    )?;
    let status = wait_until(backend, command_timeout, poll_interval, |status| {
        status.phase == CapturePhase::Stopped
            && status.generation == generation
            && status.received_records == expected
            && status.received_sequence == expected
            && status.captured == expected
            && status.sent == expected
    })?;
    let stop_drained_ns = nanoseconds(stop_started.elapsed());
    require(
        status.complete
            && status.overflow == 0
            && status.ignored_old_batches == 0
            && status.dataset_records
                == vec![workload.records_per_device as usize; workload.device_keys.len()]
            && status.managed_reserved_bytes
                <= SessionConfig::default().managed_memory_budget_bytes,
        format!("stopped session integrity or resource budget failed: {status:?}"),
    )?;
    let snapshot_started = Instant::now();
    let snapshot = backend.capture_snapshot(command_timeout)?;
    let snapshot_request_ns = nanoseconds(snapshot_started.elapsed());
    require(
        snapshot.event_count() == workload.records_per_device as usize
            && snapshot.timestamp_frequency() == fixture.replay_definition.timestamp_frequency
            && snapshot.segments().len() == 1
            && snapshot.metadata().device_id
                == format!("ClickSync-Replay-{}", workload.device_keys[0]),
        "snapshot belongs to the wrong workload, dataset, or session segment",
    )?;
    let definition = &fixture.replay_definition;
    let source_capture_prefix = expected * ordinal as u64;
    let source_timing_prefix = workload.records_per_device * ordinal as u64;
    for (index, event) in snapshot.events().enumerate() {
        let index = index as u64;
        let ticks = index * definition.timestamp_frequency / u64::from(workload.reports_per_second);
        require(
            event.raw_delta_x
                == (index % definition.delta_x_period) as i32 + definition.delta_x_offset
                && event.raw_delta_y
                    == (index % definition.delta_y_period) as i32 + definition.delta_y_offset
                && event.raw_capture_ticks == definition.first_raw_ticks + ticks
                && event.raw_relative_ticks == ticks
                && event.logical_ticks == ticks
                && event.capture_sequence
                    == source_capture_prefix + index * workload.device_keys.len() as u64 + 1
                && event.timing_sequence == source_timing_prefix + index + 1
                && event.button_flags == 0
                && event.raw_mouse_flags == 0
                && event.button_data == 0
                && event.extra_information == 0
                && event.movement_mode == RawMouseMovementMode::Relative
                && event.session_segment_id == 1,
            format!("integer replay oracle mismatch at first dataset record {index}: {event:?}"),
        )?;
    }
    let verified_first_dataset_records = snapshot.event_count();
    drop(snapshot);
    let reset_started = Instant::now();
    let reset = backend.execute(
        ControlCommand::Reset {
            generation: generation + 1,
        },
        command_timeout,
    )?;
    let reset_ack_ns = nanoseconds(reset_started.elapsed());
    require(
        reset.complete
            && reset.generation == generation + 1
            && reset.cutoff == 0
            && reset.queued == 0,
        "Reset acknowledgment failed",
    )?;
    wait_until(backend, command_timeout, poll_interval, |status| {
        status.generation == generation + 1
            && status.phase == CapturePhase::Empty
            && status.received_sequence == 0
            && status.received_records == 0
            && status.dataset_records.iter().all(|count| *count == 0)
    })?;
    Ok(Round {
        ordinal,
        observer_mode: mode.id.clone(),
        generation,
        records: expected,
        start_ack_ns,
        all_committed_ns,
        observed_records_per_second: expected as f64 * 1_000_000_000.0 / all_committed_ns as f64,
        stop_ack_ns,
        stop_drained_ns,
        stop_cutoff: stopped.cutoff,
        stop_queued: stopped.queued,
        reset_ack_ns,
        reset_observed_ns: nanoseconds(reset_started.elapsed()),
        snapshot_request_ns,
        wire_record_payload_bytes: expected * WIRE_RECORD_BYTES as u64,
        verified_first_dataset_records,
        status,
        progress,
    })
}

fn statistics(mut values: Vec<u64>, noise: &NoisePolicy) -> Statistics {
    values.sort_unstable();
    let percentile = |per_thousand: usize| {
        values[(values.len() * per_thousand)
            .div_ceil(1000)
            .saturating_sub(1)]
    };
    let p50 = percentile(500);
    let allowance = (p50 as f64 * noise.relative_tolerance_percent / 100.0)
        .max(noise.observation_absolute_ms * 1_000_000.0);
    Statistics {
        count: values.len(),
        min: values[0],
        p50,
        p95: percentile(950),
        p99: percentile(990),
        p999: percentile(999),
        max: values[values.len() - 1],
        repeat_noise_exceeded: (values[values.len() - 1] - values[0]) as f64 > allowance,
    }
}

fn aggregate(
    rounds: &[Round],
    fixture: &Fixture,
) -> BTreeMap<String, BTreeMap<String, Statistics>> {
    fixture
        .observer_modes
        .iter()
        .map(|mode| {
            let selected: Vec<_> = rounds
                .iter()
                .filter(|round| round.observer_mode == mode.id)
                .collect();
            type Metric = (&'static str, fn(&Round) -> u64);
            let metrics: [Metric; 7] = [
                ("start_ack_ns", |round| round.start_ack_ns),
                ("all_committed_ns", |round| round.all_committed_ns),
                ("stop_ack_ns", |round| round.stop_ack_ns),
                ("stop_drained_ns", |round| round.stop_drained_ns),
                ("reset_ack_ns", |round| round.reset_ack_ns),
                ("reset_observed_ns", |round| round.reset_observed_ns),
                ("snapshot_request_ns", |round| round.snapshot_request_ns),
            ];
            let metrics = metrics
                .into_iter()
                .map(|(name, extract)| {
                    (
                        name.to_string(),
                        statistics(
                            selected.iter().map(|round| extract(round)).collect(),
                            &fixture.noise_policy,
                        ),
                    )
                })
                .collect();
            (mode.id.clone(), metrics)
        })
        .collect()
}

fn save_report(path: &Path, report: &Report<'_>) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    std::fs::write(
        path,
        serde_json::to_vec_pretty(report).map_err(|error| error.to_string())?,
    )
    .map_err(|error| error.to_string())
}

fn measure_all(
    fixture: &Fixture,
    worker: &Path,
    output: &Path,
    report: &mut Report<'_>,
) -> Result<(), String> {
    validate_fixture(fixture)?;
    require(worker.is_file(), "actual Release capture worker is missing")?;
    for workload in &fixture.workloads {
        let cold_started = Instant::now();
        let mut backend = Backend::spawn(worker, true)?;
        report.workloads.push(WorkloadReport {
            id: workload.id.clone(),
            cold_backend_start_ns: nanoseconds(cold_started.elapsed()),
            warmup_rounds: Vec::new(),
            measured_rounds: Vec::new(),
            aggregate: BTreeMap::new(),
        });
        for trial in 0..fixture.warmup_rounds + fixture.measured_rounds {
            for (mode_index, mode) in fixture.observer_modes.iter().enumerate() {
                let ordinal = trial * fixture.observer_modes.len() + mode_index;
                let measured = measure_round(&backend, fixture, workload, mode, ordinal)?;
                eprintln!(
                    "{} {} trial={} warmup={} committed={} overflow={}",
                    workload.id,
                    mode.id,
                    trial,
                    trial < fixture.warmup_rounds,
                    measured.status.received_records,
                    measured.status.overflow
                );
                let current = report.workloads.last_mut().expect("current workload");
                if trial < fixture.warmup_rounds {
                    current.warmup_rounds.push(measured);
                } else {
                    current.measured_rounds.push(measured);
                }
                save_report(output, report)?;
            }
        }
        let current = report.workloads.last_mut().expect("current workload");
        current.aggregate = aggregate(&current.measured_rounds, fixture);
        backend.shutdown()?;
        save_report(output, report)?;
    }
    Ok(())
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let arguments: Vec<_> = std::env::args_os().skip(1).collect();
    if arguments.len() != 3 {
        return Err(
            "usage: capture-report <workload.json> <capture-worker.exe> <report.json>".into(),
        );
    }
    let fixture: Fixture = serde_json::from_slice(&std::fs::read(&arguments[0])?)?;
    let mut report = Report {
        schema_version: 1,
        stage: "Capture",
        source: "controlled_replay_over_production_transport",
        passed: false,
        performance_acceptance: "controlled replay baseline; hardware performance unverified",
        high_rate_hardware: "not verified",
        fixture: &fixture,
        configuration: serde_json::json!({
            "session": SessionConfig::default(),
            "protocol_version": PROTOCOL_VERSION,
            "frame_header_bytes": FRAME_HEADER_BYTES,
            "wire_record_bytes": WIRE_RECORD_BYTES,
            "captured_record_bytes": size_of::<CapturedRecord>(),
            "raw_mouse_record_bytes": size_of::<RawMouseRecord>(),
            "session_event_bytes": size_of::<SessionEvent>(),
            "capture_queue_records": CAPTURE_QUEUE_RECORDS,
            "capture_queue_payload_bytes": CAPTURE_QUEUE_RECORDS * size_of::<CapturedRecord>(),
            "max_batch_records": MAX_BATCH_RECORDS,
            "max_batch_payload_bytes": MAX_BATCH_RECORDS * WIRE_RECORD_BYTES,
            "max_frame_payload_bytes": MAX_FRAME_PAYLOAD_BYTES,
            "batch_flush_interval_ms": BATCH_FLUSH_INTERVAL_MS,
            "capture_metadata_reservation_bytes": CAPTURE_METADATA_RESERVATION_BYTES,
            "max_capture_device_field_bytes": MAX_CAPTURE_DEVICE_FIELD_BYTES,
            "max_capture_device_identities": MAX_CAPTURE_DEVICE_IDENTITIES,
            "app_transport_managed_reservation_bytes": TRANSPORT_MANAGED_RESERVATION_BYTES
        }),
        workloads: Vec::new(),
        error: None,
    };
    let output = Path::new(&arguments[2]);
    match measure_all(&fixture, Path::new(&arguments[1]), output, &mut report) {
        Ok(()) => report.passed = true,
        Err(error) => report.error = Some(error),
    }
    save_report(output, &report)?;
    if let Some(error) = report.error {
        return Err(error.into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn noise_policy() -> NoisePolicy {
        NoisePolicy {
            relative_tolerance_percent: 20.0,
            observation_absolute_ms: 2.0,
            hot_path_absolute_ns: 50.0,
            action: String::new(),
        }
    }

    #[test]
    fn trial_percentiles_use_nearest_rank_and_keep_the_slowest_round() {
        let result = statistics(
            vec![5_000_000, 1_000_000, 4_000_000, 2_000_000, 3_000_000],
            &noise_policy(),
        );
        assert_eq!(result.count, 5);
        assert_eq!(result.min, 1_000_000);
        assert_eq!(result.p50, 3_000_000);
        assert_eq!(result.p95, 5_000_000);
        assert_eq!(result.p99, 5_000_000);
        assert_eq!(result.p999, 5_000_000);
        assert_eq!(result.max, 5_000_000);
        assert!(result.repeat_noise_exceeded);
    }

    #[test]
    fn trial_noise_uses_the_greater_of_fixed_absolute_and_relative_allowances() {
        assert!(
            !statistics(
                vec![100_000, 200_000, 300_000, 400_000, 500_000],
                &noise_policy(),
            )
            .repeat_noise_exceeded
        );
        assert!(
            !statistics(
                vec![
                    1_000_000_000,
                    1_040_000_000,
                    1_080_000_000,
                    1_120_000_000,
                    1_160_000_000
                ],
                &noise_policy(),
            )
            .repeat_noise_exceeded
        );
    }
}
