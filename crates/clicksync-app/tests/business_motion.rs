#![cfg(feature = "ui-restoration-probe")]

use clicksync_app::backend::{Backend, CapturePhase};
use clicksync_capture::observation::{MotionReport, MotionResult};
use clicksync_core::protocol::{ControlCommand, MAX_BATCH_RECORDS};
use clicksync_core::{RawMouseMovementMode, RawMouseRecord};
use std::time::{Duration, Instant};

fn report(key: u32, ticks: u64, dx: i32, dy: i32) -> MotionReport {
    MotionReport {
        device_key: key,
        record: RawMouseRecord::new(dx, dy, ticks, 0, 0, RawMouseMovementMode::Relative),
    }
}
fn result(backend: &Backend, id: u64) -> MotionResult {
    let end = Instant::now() + Duration::from_secs(3);
    loop {
        if let Some(result) = backend.take_motion(id) {
            return result;
        }
        assert!(
            Instant::now() < end,
            "motion result timed out: {}",
            serde_json::to_string(&backend.state()).unwrap()
        );
        std::thread::sleep(Duration::from_millis(1));
    }
}

#[test]
fn first_motion_binding_keeps_source_event_order_and_does_not_create_a_session() {
    let mut backend = Backend::spawn_in_process(true).unwrap();
    let initial = backend.state();
    let (id, arm) = backend
        .observe_motion(Some(1), false, Some(Duration::from_secs(10)))
        .unwrap();
    assert!(
        arm.recv_timeout(Duration::from_secs(3))
            .unwrap()
            .unwrap()
            .error
            .is_none()
    );
    assert!(backend.observe_motion(None, false, None).is_err());
    backend
        .replay_motion(vec![
            report(2, 100, 0, 0),
            report(1, 100, 7, 0),
            report(2, 100, 3, 4),
            report(1, 100, 8, 0),
            report(2, 101, 1, 1),
        ])
        .unwrap();
    let MotionResult::Selected(candidate) = result(&backend, id) else {
        panic!("binding did not select motion");
    };
    assert_eq!(
        (
            candidate.device_key,
            candidate.ticks,
            candidate.capture_sequence,
            candidate.timing_sequence
        ),
        (2, 100, 3, 2)
    );
    let after = backend.state();
    assert_eq!(
        (
            after.generation,
            after.received_sequence,
            after.captured,
            after.overflow
        ),
        (initial.generation, 0, 0, 0)
    );
    assert_eq!(after.phase, CapturePhase::Empty);
    assert_eq!(backend.take_inputs().dropped, 0);
    assert!(backend.latest_snapshot().is_none());
    backend.shutdown().unwrap();
}

#[test]
fn calibration_first_and_immediately_following_reports_reach_owner_before_ui_observes_candidate() {
    let mut backend = Backend::spawn_in_process(true).unwrap();
    let (id, arm) = backend.observe_motion(None, true, None).unwrap();
    let ack = arm.recv_timeout(Duration::from_secs(3)).unwrap().unwrap();
    let mut first = report(2, 200, 13, -7);
    first.record.button_flags = 0x400;
    first.record.button_data = (-120i16) as u16;
    first.record.raw_mouse_flags = 2;
    first.record.extra_information = 0xfef1_0012;
    backend
        .replay_motion(vec![
            report(1, 199, 0, 0),
            first.clone(),
            report(1, 200, 99, 99),
            report(2, 200, 17, -9),
            report(2, 201, -21, 11),
        ])
        .unwrap();
    let MotionResult::Selected(candidate) = result(&backend, id) else {
        panic!("calibration did not latch");
    };
    assert_eq!(
        (
            candidate.device_key,
            candidate.ticks,
            candidate.capture_sequence,
            candidate.timing_sequence
        ),
        (2, 200, 2, 1)
    );
    let frozen = clicksync_app::chart::stop_and_freeze(&backend, Duration::from_secs(3)).unwrap();
    assert_eq!(frozen.generation, ack.generation);
    assert_eq!(frozen.received_sequence, 3);
    assert_eq!(frozen.datasets.len(), 1);
    assert_eq!(frozen.datasets[0].0, 2);
    let snapshot = &frozen.datasets[0].1;
    let rows = snapshot.events().collect::<Vec<_>>();
    assert_eq!(rows.len(), 3);
    assert_eq!(
        rows.iter()
            .map(|event| (
                event.raw_capture_ticks,
                event.raw_delta_x,
                event.raw_delta_y,
                event.capture_sequence,
                event.timing_sequence
            ))
            .collect::<Vec<_>>(),
        vec![
            (200, 13, -7, 2, 1),
            (200, 17, -9, 4, 2),
            (201, -21, 11, 5, 3)
        ]
    );
    assert_eq!(
        (
            rows[0].button_flags,
            rows[0].button_data,
            rows[0].raw_mouse_flags,
            rows[0].extra_information
        ),
        (
            first.record.button_flags,
            first.record.button_data,
            2,
            first.record.extra_information
        )
    );
    assert!(frozen.complete && snapshot.finalized());
    backend.shutdown().unwrap();
}

#[test]
fn revoke_cancel_and_timeout_cannot_resurrect_observation_or_accept_a_first_packet() {
    let mut backend = Backend::spawn_in_process(true).unwrap();
    let (id, arm) = backend.observe_motion(None, true, None).unwrap();
    arm.recv_timeout(Duration::from_secs(3)).unwrap().unwrap();
    backend.set_capture_permitted(false);
    backend.set_capture_permitted(true);
    backend.replay_motion(vec![report(2, 100, 7, 3)]).unwrap();
    assert_eq!(result(&backend, id), MotionResult::Revoked);
    assert_eq!(backend.state().received_sequence, 0);
    backend
        .execute(
            ControlCommand::Reset { generation: 2 },
            Duration::from_secs(3),
        )
        .unwrap();
    let (id, arm) = backend
        .observe_motion(None, false, Some(Duration::from_millis(2)))
        .unwrap();
    arm.recv_timeout(Duration::from_secs(3)).unwrap().unwrap();
    assert_eq!(result(&backend, id), MotionResult::TimedOut);
    let (id, arm) = backend.observe_motion(None, false, None).unwrap();
    arm.recv_timeout(Duration::from_secs(3)).unwrap().unwrap();
    backend.cancel_motion(id);
    backend.replay_motion(vec![report(1, 200, 4, 6)]).unwrap();
    assert!(backend.take_motion(id).is_none());
    assert_eq!(backend.state().received_sequence, 0);
    backend.shutdown().unwrap();
}

#[test]
fn calibration_queue_and_batch_boundaries_preserve_all_accepted_records_and_overflow_semantics() {
    let mut backend = Backend::spawn_in_process(true).unwrap();
    let (id, arm) = backend.observe_motion(None, true, None).unwrap();
    arm.recv_timeout(Duration::from_secs(3)).unwrap().unwrap();
    let reports = (0..(MAX_BATCH_RECORDS * 3 + 1))
        .map(|index| report(1, 100 + index as u64, 1, 2))
        .collect();
    backend.replay_motion(reports).unwrap();
    assert!(matches!(result(&backend, id), MotionResult::Selected(_)));
    let frozen = clicksync_app::chart::stop_and_freeze(&backend, Duration::from_secs(3)).unwrap();
    assert_eq!(frozen.received_sequence, (MAX_BATCH_RECORDS * 3 + 1) as u64);
    assert_eq!(
        frozen.datasets[0].1.event_count(),
        MAX_BATCH_RECORDS * 3 + 1
    );
    assert!(frozen.complete);
    backend.shutdown().unwrap();
}
