use clicksync_app::backend::{Backend, CapturePhase};
use clicksync_core::protocol::{CaptureSource, ControlCommand};
use std::path::PathBuf;
use std::thread;
use std::time::{Duration, Instant};

const TEST_TIMEOUT: Duration = Duration::from_secs(6);

fn worker_path() -> PathBuf {
    if let Some(path) = std::env::var_os("CLICKSYNC_CAPTURE_EXE") {
        return PathBuf::from(path);
    }
    let executable = std::env::current_exe().expect("test executable path");
    let target = executable
        .parent()
        .and_then(|directory| directory.parent())
        .expect("test target directory");
    let path = target.join("clicksync-capture.exe");
    assert!(
        path.is_file(),
        "required capture worker is missing: {}",
        path.display()
    );
    path
}

fn wait_until(backend: &Backend, condition: impl Fn(&clicksync_app::backend::AppStatus) -> bool) {
    let deadline = Instant::now() + TEST_TIMEOUT;
    loop {
        let state = backend.state();
        if condition(&state) {
            return;
        }
        assert!(
            Instant::now() < deadline,
            "capture condition timed out; state={state:?}"
        );
        thread::sleep(Duration::from_millis(5));
    }
}

fn start(backend: &Backend, generation: u64, keys: &[u32], records: u64, rate: u32) {
    backend
        .execute(
            ControlCommand::Start {
                generation,
                device_keys: keys.to_vec(),
                source: CaptureSource::Replay {
                    records_per_device: records,
                    reports_per_second: rate,
                    real_time: true,
                },
                start_fresh: true,
            },
            TEST_TIMEOUT,
        )
        .expect("start acknowledged");
    wait_until(backend, |state| {
        state.generation == generation && state.phase == CapturePhase::Collecting
    });
}

#[test]
fn actual_worker_single_device_roundtrip_and_stop_cutoff() {
    let backend = Backend::spawn(&worker_path(), true).expect("capture backend");
    start(&backend, 1, &[1], 1200, 8000);
    wait_until(&backend, |state| state.received_records == 1200);
    let ack = backend.execute(ControlCommand::Stop, TEST_TIMEOUT).unwrap();
    assert!(ack.complete);
    assert_eq!(ack.cutoff, 1200);
    wait_until(&backend, |state| state.phase == CapturePhase::Stopped);
    let snapshot = backend.capture_snapshot(TEST_TIMEOUT).unwrap();
    assert_eq!(snapshot.event_count(), 1200);
    assert_eq!(snapshot.segments().len(), 1);
    for (index, event) in snapshot.events().enumerate() {
        assert_eq!(event.capture_sequence, (index + 1) as u64);
        assert_eq!(event.timing_sequence, (index + 1) as u64);
        assert_eq!(event.raw_delta_x, (index % 7) as i32 - 3);
        assert_eq!(event.raw_delta_y, (index % 5) as i32 - 2);
        assert_eq!(event.raw_capture_ticks, 1_000_000 + index as u64 * 1250);
    }
}

#[test]
fn dual_device_records_remain_in_their_owned_sessions() {
    let backend = Backend::spawn(&worker_path(), true).expect("capture backend");
    start(&backend, 1, &[1, 2], 800, 8000);
    wait_until(&backend, |state| state.received_records == 1600);
    let ack = backend.execute(ControlCommand::Stop, TEST_TIMEOUT).unwrap();
    assert_eq!(ack.cutoff, 1600);
    wait_until(&backend, |state| state.phase == CapturePhase::Stopped);
    assert_eq!(backend.state().dataset_records, vec![800, 800]);
    let snapshot = backend.capture_snapshot(TEST_TIMEOUT).unwrap();
    for (index, event) in snapshot.events().enumerate() {
        assert_eq!(event.capture_sequence, (index * 2 + 1) as u64);
        assert_eq!(event.timing_sequence, (index + 1) as u64);
        assert_eq!(event.raw_relative_ticks, index as u64 * 1250);
        assert_eq!(event.logical_ticks, index as u64 * 1250);
    }
}

#[test]
fn low_rate_batch_flush_pause_resume_and_fixed_old_snapshot() {
    let backend = Backend::spawn(&worker_path(), true).expect("capture backend");
    start(&backend, 1, &[1], 1500, 1000);
    wait_until(&backend, |state| state.received_records >= 100);
    let pause = backend
        .execute(ControlCommand::Pause, TEST_TIMEOUT)
        .unwrap();
    wait_until(&backend, |state| state.phase == CapturePhase::Paused);
    assert!(pause.complete);
    assert_eq!(backend.state().received_sequence, pause.cutoff);
    let old = backend.capture_snapshot(TEST_TIMEOUT).unwrap();
    let old_count = old.event_count();
    let old_last = old.event_at(old_count - 1).unwrap();
    backend
        .execute(ControlCommand::Resume, TEST_TIMEOUT)
        .unwrap();
    wait_until(&backend, |state| {
        state.received_records > pause.cutoff + 100
    });
    let stop = backend.execute(ControlCommand::Stop, TEST_TIMEOUT).unwrap();
    wait_until(&backend, |state| state.phase == CapturePhase::Stopped);
    let current = backend.capture_snapshot(TEST_TIMEOUT).unwrap();
    assert_eq!(current.event_count() as u64, stop.cutoff);
    assert_eq!(current.segments().len(), 2);
    assert_eq!(old.event_count(), old_count);
    assert_eq!(old.event_at(old_count - 1).unwrap(), old_last);
    assert!(current.event_at(old_count).unwrap().logical_ticks >= old_last.logical_ticks);
}

#[test]
fn reset_and_restart_never_mix_generation_or_mutate_old_reader() {
    let backend = Backend::spawn(&worker_path(), true).expect("capture backend");
    start(&backend, 1, &[1], 500, 8000);
    wait_until(&backend, |state| state.received_records == 500);
    backend.execute(ControlCommand::Stop, TEST_TIMEOUT).unwrap();
    wait_until(&backend, |state| state.phase == CapturePhase::Stopped);
    let old = backend.capture_snapshot(TEST_TIMEOUT).unwrap();
    backend
        .execute(ControlCommand::Reset { generation: 2 }, TEST_TIMEOUT)
        .unwrap();
    wait_until(&backend, |state| {
        state.generation == 2 && state.phase == CapturePhase::Empty
    });
    start(&backend, 3, &[1], 300, 8000);
    wait_until(&backend, |state| state.received_records == 300);
    backend.execute(ControlCommand::Stop, TEST_TIMEOUT).unwrap();
    wait_until(&backend, |state| state.phase == CapturePhase::Stopped);
    let current = backend.capture_snapshot(TEST_TIMEOUT).unwrap();
    assert_eq!(old.event_count(), 500);
    assert_eq!(old.event_at(499).unwrap().capture_sequence, 500);
    assert_eq!(current.event_count(), 300);
    assert_eq!(current.event_at(0).unwrap().capture_sequence, 501);
    assert_eq!(current.event_at(0).unwrap().timing_sequence, 501);
    assert_eq!(backend.state().received_sequence, 300);
    assert_eq!(backend.state().generation, 3);
}

#[test]
fn overflow_is_explicit_and_control_remains_independent_of_slow_data() {
    let backend = Backend::spawn(&worker_path(), true).expect("capture backend");
    backend.set_ingestion_delay(Duration::from_millis(200));
    backend
        .execute(
            ControlCommand::Start {
                generation: 1,
                device_keys: vec![1, 2],
                source: CaptureSource::Replay {
                    records_per_device: 50_000,
                    reports_per_second: 8000,
                    real_time: false,
                },
                start_fresh: true,
            },
            TEST_TIMEOUT,
        )
        .unwrap();
    thread::sleep(Duration::from_millis(100));
    let started = Instant::now();
    let ack = backend
        .execute(ControlCommand::Stop, Duration::from_secs(2))
        .unwrap();
    assert!(started.elapsed() < Duration::from_secs(2));
    assert!(
        !ack.complete,
        "overflow must not masquerade as a normal stop"
    );
    assert!(ack.overflow > 0);
    backend.set_ingestion_delay(Duration::ZERO);
    wait_until(&backend, |state| state.phase == CapturePhase::Faulted);
    assert!(!backend.state().complete);
}

#[test]
fn unexpected_worker_disconnect_preserves_prefix_and_reports_incomplete() {
    let mut backend = Backend::spawn(&worker_path(), true).expect("capture backend");
    start(&backend, 1, &[1], 10_000, 1000);
    wait_until(&backend, |state| state.received_records >= 50);
    let prefix = backend.state().received_records;
    backend.terminate_worker().unwrap();
    wait_until(&backend, |state| state.phase == CapturePhase::Faulted);
    let state = backend.state();
    assert!(!state.complete);
    assert!(state.received_records >= prefix);
    let snapshot = backend.latest_snapshot();
    if let Some(snapshot) = snapshot {
        assert!(snapshot.event_count() as u64 <= state.received_records);
    }
}

#[test]
fn rejected_start_keeps_the_complete_session_and_control_connection_usable() {
    let backend = Backend::spawn(&worker_path(), true).expect("capture backend");
    start(&backend, 1, &[1], 300, 8000);
    wait_until(&backend, |state| state.received_records == 300);
    backend.execute(ControlCommand::Stop, TEST_TIMEOUT).unwrap();
    wait_until(&backend, |state| state.phase == CapturePhase::Stopped);
    let old = backend.capture_snapshot(TEST_TIMEOUT).unwrap();
    let rejected = backend.execute(
        ControlCommand::Start {
            generation: 2,
            device_keys: vec![99],
            source: CaptureSource::Replay {
                records_per_device: 100,
                reports_per_second: 1000,
                real_time: true,
            },
            start_fresh: true,
        },
        TEST_TIMEOUT,
    );
    assert!(rejected.is_err());
    wait_until(&backend, |state| {
        state.error.is_some() && !state.command_pending
    });
    assert_eq!(backend.state().phase, CapturePhase::Stopped);
    assert!(backend.state().complete);
    assert_eq!(backend.state().selected_device_keys, [1]);
    assert_eq!(
        backend
            .capture_snapshot(TEST_TIMEOUT)
            .unwrap()
            .event_count(),
        300
    );
    start(&backend, 2, &[1], 100, 8000);
    wait_until(&backend, |state| state.received_records == 100);
    backend.execute(ControlCommand::Stop, TEST_TIMEOUT).unwrap();
    wait_until(&backend, |state| state.phase == CapturePhase::Stopped);
    assert_eq!(
        backend
            .capture_snapshot(TEST_TIMEOUT)
            .unwrap()
            .event_count(),
        100
    );
    assert_eq!(old.event_count(), 300);
}

#[test]
fn nonfresh_start_continues_the_paused_session_instead_of_replacing_its_prefix() {
    let backend = Backend::spawn(&worker_path(), true).expect("capture backend");
    start(&backend, 1, &[1], 1000, 1000);
    wait_until(&backend, |state| state.received_records >= 100);
    backend
        .execute(ControlCommand::Pause, TEST_TIMEOUT)
        .unwrap();
    wait_until(&backend, |state| state.phase == CapturePhase::Paused);
    let old = backend.capture_snapshot(TEST_TIMEOUT).unwrap();
    let prefix = old.event_count();
    let first = old.event_at(0).unwrap();
    backend
        .execute(
            ControlCommand::Start {
                generation: 1,
                device_keys: vec![1],
                source: CaptureSource::Replay {
                    records_per_device: 1000,
                    reports_per_second: 1000,
                    real_time: true,
                },
                start_fresh: false,
            },
            TEST_TIMEOUT,
        )
        .unwrap();
    wait_until(&backend, |state| {
        state.received_records >= prefix as u64 + 50
    });
    backend.execute(ControlCommand::Stop, TEST_TIMEOUT).unwrap();
    wait_until(&backend, |state| state.phase == CapturePhase::Stopped);
    let snapshot = backend.capture_snapshot(TEST_TIMEOUT).unwrap();
    assert!(snapshot.event_count() > prefix);
    assert_eq!(snapshot.event_at(0).unwrap(), first);
    assert_eq!(snapshot.segments().len(), 2);
    assert_eq!(old.event_count(), prefix);
}
