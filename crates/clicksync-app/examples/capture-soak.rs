//! Controlled in-process retention/roundtrip soak, not hardware or UI latency.
use clicksync_app::backend::{Backend, CapturePhase};
use clicksync_core::{
    RawMouseMovementMode, SessionConfig, SharedByteBudget,
    exchange::{MAX_IMPORT_BYTES, export_writer, import_reader},
    protocol::{CaptureSource, ControlCommand},
};
use std::{
    fs::{self, File},
    io::{BufReader, BufWriter, Write},
    path::PathBuf,
    thread,
    time::{Duration, Instant},
};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = std::env::args().skip(1);
    let directory = PathBuf::from(
        args.next()
            .ok_or("usage: capture-soak DIRECTORY SECONDS [CYCLES]")?,
    );
    let seconds: u64 = args.next().ok_or("missing seconds")?.parse()?;
    let cycles: u64 = args.next().map_or(Ok(1), |value| value.parse())?;
    if !(1..=1800).contains(&seconds) || !(1..=100).contains(&cycles) {
        return Err("seconds must be 1..=1800; cycles must be 1..=100".into());
    }
    fs::create_dir(&directory)?;
    let report_path = directory.join("report.json");
    let mut report = serde_json::json!({
        "passed": false, "seconds_per_cycle": seconds, "cycles": cycles,
        "devices": 2, "reports_per_second_per_device": 8000, "results": [],
        "boundary": "Controlled production in-process queue/owner, no GPUI, GPU or physical input. Replay ticks are synthetic and are NEVER subtracted from wall time. Status/backlog samples are non-atomic observations at 100 ms.",
    });
    fs::write(&report_path, serde_json::to_vec_pretty(&report)?)?;
    let mut samples = BufWriter::new(File::create(directory.join("status.jsonl"))?);
    let mut backend = Backend::spawn_in_process(true)?;
    let deadline = Duration::from_secs(30);
    let count = seconds * 8000;
    for cycle in 1..=cycles {
        let start = Instant::now();
        backend.execute(
            ControlCommand::Start {
                generation: cycle,
                device_keys: vec![1, 2],
                start_fresh: true,
                source: CaptureSource::Replay {
                    records_per_device: count,
                    reports_per_second: 8000,
                    real_time: true,
                },
            },
            deadline,
        )?;
        let start_command_ms = start.elapsed().as_secs_f64() * 1000.0;
        loop {
            let state = backend.state();
            serde_json::to_writer(
                &mut samples,
                &serde_json::json!({
                    "cycle": cycle, "elapsed_ms": start.elapsed().as_secs_f64() * 1000.0,
                    "estimated_backlog": state.captured.saturating_sub(state.received_records),
                    "status": state,
                }),
            )?;
            writeln!(samples)?;
            samples.flush()?;
            // The source ACK can precede owner application; a previous cycle's
            // equal count must never end this generation's measurement.
            if state.generation == cycle {
                if state.overflow != 0 || state.ignored_after_limit != 0 || state.error.is_some() {
                    return Err(format!("capture integrity fault: {state:?}").into());
                }
                if state.received_records == count * 2 {
                    break;
                }
            }
            if start.elapsed() > Duration::from_secs(seconds + 30) {
                return Err(format!("capture deadline: {state:?}").into());
            }
            thread::sleep(Duration::from_millis(100));
        }
        let stop = Instant::now();
        let ack = backend.execute(ControlCommand::Stop, deadline)?;
        while backend.state().phase != CapturePhase::Stopped {
            if stop.elapsed() > deadline {
                return Err(format!("stop drain deadline: {:?}", backend.state()).into());
            }
            thread::sleep(Duration::from_millis(1));
        }
        let stop_and_drain_ms = stop.elapsed().as_secs_f64() * 1000.0;
        let capture_elapsed_ms = start.elapsed().as_secs_f64() * 1000.0;
        let prefix = backend.capture_prefix(deadline)?;
        assert_eq!(prefix.generation, cycle);
        assert!(ack.complete && prefix.complete);
        assert_eq!(ack.cutoff, count * 2);
        assert_eq!(ack.queued, prefix.received_sequence);
        assert_eq!(prefix.received_records, count * 2);
        let mut exports = Vec::new();
        // Full scans and file I/O are outside the capture measurement interval.
        for (side, (key, snapshot)) in prefix.datasets.iter().enumerate() {
            assert_eq!(snapshot.event_count() as u64, count);
            for (i, event) in snapshot.events().enumerate() {
                assert_eq!(
                    event.capture_sequence,
                    (cycle - 1) * count * 2 + (i * 2 + side + 1) as u64
                );
                assert_eq!(event.timing_sequence, (cycle - 1) * count + i as u64 + 1);
                assert_eq!(event.raw_capture_ticks, 1_000_000 + i as u64 * 1250);
                assert_eq!(event.raw_delta_x, (i % 7) as i32 - 3);
                assert_eq!(event.raw_delta_y, (i % 5) as i32 - 2);
                assert_eq!(event.movement_mode, RawMouseMovementMode::Relative);
                assert_eq!(
                    (
                        event.button_flags,
                        event.raw_mouse_flags,
                        event.button_data,
                        event.extra_information
                    ),
                    (0, 0, 0, 0)
                );
            }
            let path = directory.join(format!("cycle-{cycle}-device-{key}.json"));
            let mut output = BufWriter::new(File::create(&path)?);
            export_writer(&mut output, snapshot, Some(800.0), "controlled-soak")?;
            output.flush()?;
            drop(output);
            let bytes = fs::metadata(&path)?.len();
            let roundtrip = if bytes <= MAX_IMPORT_BYTES {
                let imported = import_reader(
                    BufReader::new(File::open(&path)?),
                    SessionConfig {
                        timestamp_frequency: snapshot.timestamp_frequency(),
                        ..Default::default()
                    },
                    SharedByteBudget::new(SessionConfig::default().managed_memory_budget_bytes),
                )?;
                assert_eq!(imported.snapshot.event_count(), snapshot.event_count());
                assert!(snapshot.events().eq(imported.snapshot.events()));
                "all event fields exact, same frequency"
            } else {
                "not verified: file exceeds existing import byte limit"
            };
            exports
                .push(serde_json::json!({"device": key, "bytes": bytes, "roundtrip": roundtrip}));
        }
        report["results"]
            .as_array_mut()
            .unwrap()
            .push(serde_json::json!({
                "cycle": cycle, "capture_elapsed_ms": capture_elapsed_ms,
                "start_command_ms": start_command_ms, "stop_and_drain_ms": stop_and_drain_ms,
                "status": backend.state(), "exports": exports,
            }));
        fs::write(&report_path, serde_json::to_vec_pretty(&report)?)?;
    }
    backend.shutdown()?;
    report["passed"] = true.into();
    fs::write(&report_path, serde_json::to_vec_pretty(&report)?)?;
    Ok(())
}
