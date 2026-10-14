use clicksync_app::backend::{AppStatus, Backend, CapturePhase};
use clicksync_core::protocol::{CaptureSource, ControlCommand};
use std::{
    thread,
    time::{Duration, Instant},
};

const DEADLINE: Duration = Duration::from_secs(8);

fn wait(backend: &Backend, condition: impl Fn(&AppStatus) -> bool) {
    let end = Instant::now() + DEADLINE;
    loop {
        let state = backend.state();
        if condition(&state) {
            return;
        }
        assert!(Instant::now() < end, "local capture deadline: {state:?}");
        thread::sleep(Duration::from_millis(2));
    }
}

fn start(generation: u64, keys: &[u32], count: u64, rate: u32) -> ControlCommand {
    ControlCommand::Start {
        generation,
        device_keys: keys.to_vec(),
        start_fresh: true,
        source: CaptureSource::Replay {
            records_per_device: count,
            reports_per_second: rate,
            real_time: true,
        },
    }
}

#[test]
fn repeated_activation_cannot_split_records_that_are_still_in_flight() {
    let backend = Backend::spawn_in_process(true).unwrap();
    let command = start(1, &[1], 100_000, 1000);
    backend.execute(command.clone(), DEADLINE).unwrap();
    wait(&backend, |s| s.received_records >= 20);
    backend.set_ingestion_delay(Duration::from_millis(30));
    assert!(backend.execute(ControlCommand::Resume, DEADLINE).is_err());
    let mut continuing = command;
    if let ControlCommand::Start { start_fresh, .. } = &mut continuing {
        *start_fresh = false;
    }
    assert!(backend.execute(continuing, DEADLINE).is_err());
    backend.set_ingestion_delay(Duration::ZERO);
    let ack = backend.execute(ControlCommand::Stop, DEADLINE).unwrap();
    wait(&backend, |s| s.phase == CapturePhase::Stopped);
    let snapshot = backend.capture_snapshot(DEADLINE).unwrap();
    assert_eq!(snapshot.event_count() as u64, ack.queued);
    assert_eq!(snapshot.segments().len(), 1);
    assert!(snapshot.events().all(|event| event.session_segment_id == 1));
}

#[test]
fn direct_single_and_dual_capture_preserve_integer_fields_and_exact_stop() {
    for keys in [vec![1], vec![1, 2]] {
        let mut backend = Backend::spawn_in_process(true).unwrap();
        assert_eq!(backend.capture_process_id(), Some(std::process::id()));
        backend
            .execute(start(1, &keys, 800, 8000), DEADLINE)
            .unwrap();
        wait(&backend, |s| s.received_records == 800 * keys.len() as u64);
        let ack = backend.execute(ControlCommand::Stop, DEADLINE).unwrap();
        wait(&backend, |s| s.phase == CapturePhase::Stopped);
        assert!(ack.complete);
        assert_eq!(ack.cutoff, ack.queued);
        let prefix = backend.capture_prefix(DEADLINE).unwrap();
        assert_eq!(prefix.received_sequence, ack.queued);
        for (side, (_, snapshot)) in prefix.datasets.iter().enumerate() {
            assert_eq!(snapshot.event_count(), 800);
            for (i, event) in snapshot.events().enumerate() {
                assert_eq!(event.raw_capture_ticks, 1_000_000 + i as u64 * 1250);
                assert_eq!(event.capture_sequence, (i * keys.len() + side + 1) as u64);
                assert_eq!(event.timing_sequence, i as u64 + 1);
                assert_eq!(event.raw_delta_x, (i % 7) as i32 - 3);
                assert_eq!(event.raw_delta_y, (i % 5) as i32 - 2);
            }
        }
        backend.shutdown().unwrap();
        assert!(backend.state().complete);
    }
}

#[test]
fn resume_waits_for_old_segment_to_drain_and_reset_keeps_old_reader() {
    let backend = Backend::spawn_in_process(true).unwrap();
    backend
        .execute(start(1, &[1], 4000, 1000), DEADLINE)
        .unwrap();
    wait(&backend, |s| s.received_records >= 50);
    backend.set_ingestion_delay(Duration::from_millis(30));
    thread::sleep(Duration::from_millis(60));
    let pause = backend.execute(ControlCommand::Pause, DEADLINE).unwrap();
    // Queue Resume immediately, before the UI has observed Paused.
    backend.execute(ControlCommand::Resume, DEADLINE).unwrap();
    backend.set_ingestion_delay(Duration::ZERO);
    wait(&backend, |s| s.received_records > pause.queued + 50);
    let stop = backend.execute(ControlCommand::Stop, DEADLINE).unwrap();
    wait(&backend, |s| s.phase == CapturePhase::Stopped);
    let old = backend.capture_snapshot(DEADLINE).unwrap();
    assert_eq!(old.event_count() as u64, stop.queued);
    assert_eq!(old.segments().len(), 2);
    for (i, event) in old.events().enumerate() {
        assert_eq!(
            event.session_segment_id,
            if (i as u64) < pause.queued { 1 } else { 2 }
        );
    }
    let last = old.event_at(old.event_count() - 1).unwrap();
    backend
        .execute(ControlCommand::Reset { generation: 2 }, DEADLINE)
        .unwrap();
    backend.execute(start(3, &[2], 80, 8000), DEADLINE).unwrap();
    wait(&backend, |s| s.generation == 3 && s.received_records == 80);
    backend.execute(ControlCommand::Stop, DEADLINE).unwrap();
    wait(&backend, |s| s.phase == CapturePhase::Stopped);
    assert_eq!(
        backend.capture_snapshot(DEADLINE).unwrap().event_count(),
        80
    );
    assert_eq!(old.event_at(old.event_count() - 1).unwrap(), last);
}

#[test]
fn overflow_drains_every_accepted_record_and_never_becomes_complete() {
    let mut backend = Backend::spawn_in_process(true).unwrap();
    backend.set_ingestion_delay(Duration::from_millis(200));
    let mut command = start(1, &[1, 2], 100_000, 8000);
    if let ControlCommand::Start {
        source: CaptureSource::Replay { real_time, .. },
        ..
    } = &mut command
    {
        *real_time = false;
    }
    backend.execute(command, DEADLINE).unwrap();
    thread::sleep(Duration::from_millis(100));
    let began = Instant::now();
    let ack = backend
        .execute(ControlCommand::Stop, Duration::from_secs(2))
        .unwrap();
    assert!(began.elapsed() < Duration::from_secs(2));
    assert!(!ack.complete);
    assert!(ack.overflow > 0);
    assert_eq!(ack.cutoff, ack.queued + 1);
    backend.set_ingestion_delay(Duration::ZERO);
    wait(&backend, |s| {
        s.received_sequence == ack.queued && s.phase == CapturePhase::Faulted
    });
    let prefix = backend.capture_prefix(DEADLINE).unwrap();
    assert!(!prefix.complete);
    assert_eq!(
        prefix
            .datasets
            .iter()
            .map(|(_, s)| s.event_count() as u64)
            .sum::<u64>(),
        ack.queued
    );
    for (_, snapshot) in &prefix.datasets {
        for (i, event) in snapshot.events().enumerate() {
            assert_eq!(event.timing_sequence, i as u64 + 1);
        }
    }
    backend.shutdown().unwrap();
    assert!(!backend.state().complete);
    assert!(
        backend
            .latest_prefix()
            .unwrap()
            .datasets
            .iter()
            .all(|(_, s)| s.finalized())
    );
}

#[test]
fn revoked_permission_rejects_activation_without_poisoning_next_session() {
    let backend = Backend::spawn_in_process(true).unwrap();
    backend.set_capture_permitted(false);
    assert!(backend.execute(start(1, &[1], 80, 1000), DEADLINE).is_err());
    assert_eq!(backend.state().received_records, 0);
    backend.set_capture_permitted(true);
    backend
        .execute(start(1, &[1], 1000, 1000), DEADLINE)
        .unwrap();
    wait(&backend, |s| s.received_records >= 20);
    backend.set_capture_permitted(false);
    let pause = backend.execute(ControlCommand::Pause, DEADLINE).unwrap();
    wait(&backend, |s| s.phase == CapturePhase::Paused);
    backend.set_capture_permitted(true);
    thread::sleep(Duration::from_millis(40));
    assert_eq!(backend.state().received_records, pause.queued);
    backend.execute(ControlCommand::Resume, DEADLINE).unwrap();
    wait(&backend, |s| s.received_records > pause.queued + 20);
}

#[test]
fn rejected_device_selection_retains_session_and_control_is_reusable() {
    let backend = Backend::spawn_in_process(true).unwrap();
    backend.execute(start(1, &[1], 80, 8000), DEADLINE).unwrap();
    wait(&backend, |s| s.received_records == 80);
    backend.execute(ControlCommand::Stop, DEADLINE).unwrap();
    wait(&backend, |s| s.phase == CapturePhase::Stopped);
    let old = backend.capture_snapshot(DEADLINE).unwrap();
    assert!(
        backend
            .execute(start(2, &[99], 80, 8000), DEADLINE)
            .is_err()
    );
    assert_eq!(
        backend.capture_snapshot(DEADLINE).unwrap().event_count(),
        80
    );
    backend.execute(start(2, &[2], 80, 8000), DEADLINE).unwrap();
    wait(&backend, |s| s.generation == 2 && s.received_records == 80);
    assert_eq!(old.event_count(), 80);
}

#[test]
fn one_hundred_control_cycles_and_shutdown_keep_a_sealed_prefix() {
    let mut backend = Backend::spawn_in_process(true).unwrap();
    backend
        .execute(start(1, &[1, 2], 100_000, 8000), DEADLINE)
        .unwrap();
    wait(&backend, |s| s.received_records >= 20);
    for _ in 0..100 {
        let pause = backend.execute(ControlCommand::Pause, DEADLINE).unwrap();
        backend.execute(ControlCommand::Resume, DEADLINE).unwrap();
        wait(&backend, |s| s.received_records > pause.queued);
    }
    backend.shutdown().unwrap();
    let state = backend.state();
    assert_eq!(state.phase, CapturePhase::Stopped);
    assert!(state.complete);
    let prefix = backend.latest_prefix().unwrap();
    assert_eq!(prefix.received_sequence, state.received_sequence);
    assert_eq!(prefix.phase, CapturePhase::Stopped);
    assert_eq!(prefix.datasets.len(), 2);
    assert_eq!(prefix.datasets[0].1.segments().len(), 101);
}
