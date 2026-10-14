use super::{CaptureState, DiscoveredDevice, RAWMOUSE};
use crate::{CaptureShared, CapturedRecord};
use clicksync_core::SessionMetadata;
use clicksync_core::protocol::{CAPTURE_QUEUE_RECORDS, WIRE_RECORD_BYTES};
use serde_json::{Value, json};
use std::io;
use std::mem::size_of;
use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::Instant;

fn require(condition: bool, message: &str) -> io::Result<()> {
    if condition {
        Ok(())
    } else {
        Err(io::Error::other(message))
    }
}

fn number(value: &Value, name: &str) -> io::Result<u64> {
    value[name]
        .as_u64()
        .ok_or_else(|| io::Error::other(format!("missing numeric workload field: {name}")))
}

fn distribution(values: &mut [u64]) -> Value {
    values.sort_unstable();
    let percentile =
        |fraction: usize| values[(values.len() * fraction).div_ceil(1000).saturating_sub(1)];
    json!({
        "p50": percentile(500), "p95": percentile(950),
        "p99": percentile(990), "p999": percentile(999),
        "max": values[values.len() - 1],
    })
}

fn save(path: &Path, report: &Value) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(path, serde_json::to_vec_pretty(report)?)
}

#[derive(Clone, Copy)]
struct RoundWorkload<'a> {
    keys: &'a [u32],
    records_per_device: u64,
    rate: u64,
}

fn round(
    state: &mut CaptureState,
    consumer: &mut rtrb::Consumer<CapturedRecord>,
    workload: RoundWorkload<'_>,
    ordinal: u64,
    relative_noise: f64,
    absolute_noise_ns: f64,
) -> io::Result<Value> {
    let RoundWorkload {
        keys,
        records_per_device,
        rate,
    } = workload;
    let records = records_per_device * keys.len() as u64;
    let mut samples = Vec::with_capacity(records as usize);
    let mut timings = [0u64; 2];
    let mut allocation_count = 0;
    let mut allocation_bytes = 0;
    for (mode, elapsed) in timings.iter_mut().enumerate() {
        state.start(ordinal * 2 + mode as u64 + 1, keys, true)?;
        let initial_source = state.capture_sequence;
        let initial_device_sequences: Vec<_> = keys
            .iter()
            .map(|&key| state.device_states[key as usize - 1].timing_sequence)
            .collect();
        let before = Instant::now();
        let allocations = allocation_counter::measure(|| {
            for index in 0..records {
                let device_slot = index as usize % keys.len();
                let key = keys[device_slot];
                let device_index = index / keys.len() as u64;
                let mouse = RAWMOUSE {
                    lLastX: (device_index % 7) as i32 - 3,
                    lLastY: (device_index % 5) as i32 - 2,
                    ..RAWMOUSE::default()
                };
                let ticks = 1_000_000 + device_index * 10_000_000 / rate;
                if mode == 1 {
                    let entry = Instant::now();
                    state.capture_mouse(key as usize * 10, mouse, ticks);
                    samples.push(entry.elapsed().as_nanos() as u64);
                } else {
                    state.capture_mouse(key as usize * 10, mouse, ticks);
                }
                let captured = consumer
                    .pop()
                    .expect("production dispatch accepted the report");
                assert_eq!(captured.generation, ordinal * 2 + mode as u64 + 1);
                assert_eq!(captured.session_sequence, index + 1);
                assert_eq!(captured.device_key, key);
                assert_eq!(captured.record.capture_sequence, initial_source + index + 1);
                assert_eq!(
                    captured.record.timing_sequence,
                    initial_device_sequences[device_slot] + device_index + 1
                );
                assert_eq!(captured.record.raw_capture_ticks, ticks);
                assert_eq!(captured.record.raw_delta_x, mouse.lLastX);
                assert_eq!(captured.record.raw_delta_y, mouse.lLastY);
            }
        });
        *elapsed = before.elapsed().as_nanos() as u64;
        allocation_count += allocations.count_total;
        allocation_bytes += allocations.bytes_total;
        require(
            allocations.count_total == 0 && allocations.bytes_total == 0,
            "production decoded dispatch allocated on its steady path",
        )?;
        require(
            state.shared.queued.load(Ordering::Acquire) == records
                && state.shared.captured.load(Ordering::Acquire) == records
                && state.shared.overflow.load(Ordering::Acquire) == 0,
            "standalone dispatch did not preserve the exact bounded prefix",
        )?;
        state.stop();
    }
    let hot_path = distribution(&mut samples);
    let observer_delta = timings[1] as i128 - timings[0] as i128;
    let uninstrumented_mean = timings[0] as f64 / records as f64;
    let observer_allowance = (uninstrumented_mean * relative_noise / 100.0).max(absolute_noise_ns);
    Ok(json!({
        "ordinal": ordinal, "records": records,
        "allocation_count": allocation_count, "allocation_bytes": allocation_bytes,
        "overflow": state.shared.overflow.load(Ordering::Acquire),
        "queue_high_watermark": state.shared.high_watermark.load(Ordering::Acquire),
        "elapsed_ns": timings[1], "uninstrumented_elapsed_ns": timings[0],
        "observed_diagnostic_elapsed_delta_ns": observer_delta,
        "diagnostic_elapsed_noise_exceeded": observer_delta.unsigned_abs() as f64 / records as f64 > observer_allowance,
        "hot_path_ns": hot_path,
    }))
}

pub(super) fn report_if_requested() -> io::Result<()> {
    let Some(output) = std::env::var_os("CLICKSYNC_CAPTURE_STANDALONE_REPORT") else {
        return Ok(());
    };
    require(
        !cfg!(debug_assertions),
        "standalone measurements require Release",
    )?;
    let fixture = std::env::var_os("CLICKSYNC_CAPTURE_WORKLOAD")
        .ok_or_else(|| io::Error::other("standalone workload path is missing"))?;
    let fixture: Value = serde_json::from_slice(&std::fs::read(fixture)?)?;
    require(
        number(&fixture, "schema_version")? == 1
            && fixture["build_configuration"] == "release"
            && number(&fixture, "warmup_rounds")? == 1
            && number(&fixture, "measured_rounds")? == 5
            && number(&fixture, "duration_ms")? == 1000,
        "standalone fixed workload definition is invalid",
    )?;
    let loads = fixture["workloads"]
        .as_array()
        .ok_or_else(|| io::Error::other("workloads are missing"))?;
    require(loads.len() == 4, "standalone workload matrix is incomplete")?;
    let mut clock_samples: Vec<u64> = (0..10_000)
        .map(|_| {
            let start = Instant::now();
            start.elapsed().as_nanos() as u64
        })
        .collect();
    let clock_pair = distribution(&mut clock_samples);
    let mut report = json!({
        "schema_version": 1, "stage": "Capture",
        "source": "controlled_raw_input_dispatch", "passed": false,
        "actual_raw_packets": 0,
        "measurement_boundary": "Decoded production capture_mouse only; excludes GetRawInputData, QPC at WM_INPUT entry, hardware arrival, IPC and app ingestion. Total loop timings also include immediate dequeue and assertions.",
        "observer_note": "Per-dispatch timing includes its clock observation. Off/on total loop rounds and empty clock pairs are retained, not subtracted from tail percentiles.",
        "configuration": {
            "capture_queue_records": CAPTURE_QUEUE_RECORDS,
            "captured_record_bytes": size_of::<CapturedRecord>(),
            "wire_record_bytes": WIRE_RECORD_BYTES,
            "worker_transport_reserved_bytes": crate::worker::reserved_transport_bytes(),
            "clock_pair_ns": clock_pair,
        },
        "workloads": [],
    });
    let path = Path::new(&output);
    save(path, &report)?;
    for load in loads {
        let keys: Vec<u32> = load["device_keys"]
            .as_array()
            .ok_or_else(|| io::Error::other("device keys are missing"))?
            .iter()
            .map(|value| {
                value
                    .as_u64()
                    .and_then(|key| u32::try_from(key).ok())
                    .ok_or_else(|| io::Error::other("invalid workload device key"))
            })
            .collect::<io::Result<_>>()?;
        require(
            matches!(keys.as_slice(), [1] | [1, 2]),
            "invalid standalone selected devices",
        )?;
        let rate = number(load, "reports_per_second")?;
        let records_per_device = number(load, "records_per_device")?;
        require(
            matches!(rate, 1000 | 8000) && records_per_device == rate,
            "invalid standalone input rate/count",
        )?;
        let (producer, mut consumer) = rtrb::RingBuffer::new(CAPTURE_QUEUE_RECORDS);
        let mut state = CaptureState::new(producer, Arc::new(CaptureShared::default()));
        state.replace_directory(
            (1..=2)
                .map(|key| DiscoveredDevice {
                    handle: key * 10,
                    metadata: SessionMetadata {
                        device_id: format!("controlled-mouse-{key}"),
                        ..Default::default()
                    },
                    endpoint: Default::default(),
                })
                .collect(),
        )?;
        let relative_noise = fixture["noise_policy"]["relative_tolerance_percent"]
            .as_f64()
            .ok_or_else(|| io::Error::other("missing fixed relative noise allowance"))?;
        let absolute_noise_ns = fixture["noise_policy"]["hot_path_absolute_ns"]
            .as_f64()
            .ok_or_else(|| io::Error::other("missing fixed hot-path noise allowance"))?;
        let mut warmup = Vec::new();
        let mut measured = Vec::new();
        for ordinal in 0..6 {
            let result = round(
                &mut state,
                &mut consumer,
                RoundWorkload {
                    keys: &keys,
                    records_per_device,
                    rate,
                },
                ordinal,
                relative_noise,
                absolute_noise_ns,
            )?;
            if ordinal == 0 {
                warmup.push(result);
            } else {
                measured.push(result);
            }
        }
        report["workloads"]
            .as_array_mut()
            .expect("report workloads")
            .push(json!({
                "id": load["id"], "warmup_rounds": warmup, "measured_rounds": measured,
            }));
        save(path, &report)?;
    }
    report["passed"] = json!(true);
    save(path, &report)
}
