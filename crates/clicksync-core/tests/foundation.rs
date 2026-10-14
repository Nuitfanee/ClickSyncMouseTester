use clicksync_core::session::{CaptureEvidence, CaptureIntegrity};
use clicksync_core::{
    AppendOutcome, ExchangeDocument, MousePerformancePacketKind, RawMouseMovementMode,
    RawMouseRecord, Session, SessionConfig, SessionError, SessionMetadata, SessionStatus,
    SharedByteBudget, convert_ticks,
};
use serde::Deserialize;

fn config() -> SessionConfig {
    SessionConfig {
        max_session_events: 64,
        warning_event_threshold: 32,
        chunk_capacity: 2,
        managed_memory_budget_bytes: 32 * 1024,
        timestamp_frequency: 10_000_000,
    }
}

fn record(tick: u64, sequence: u64, x: i32, y: i32, mode: RawMouseMovementMode) -> RawMouseRecord {
    RawMouseRecord {
        raw_delta_x: x,
        raw_delta_y: y,
        button_flags: 0,
        raw_mouse_flags: 0,
        button_data: 0,
        extra_information: 0,
        raw_capture_ticks: tick,
        capture_sequence: sequence,
        timing_sequence: sequence,
        movement_mode: mode,
    }
}

#[test]
fn deterministic_replay_keeps_order_and_segment_boundaries() {
    let settings = config();
    let budget = SharedByteBudget::new(settings.managed_memory_budget_bytes);
    let mut session = Session::new(settings, budget, SessionMetadata::default()).unwrap();
    session.begin_collecting(1_000, true, false).unwrap();
    assert_eq!(
        session
            .append(record(1_000, 10, 4, 0, RawMouseMovementMode::Relative))
            .unwrap(),
        AppendOutcome::Appended
    );
    session
        .append(record(1_100, 11, 0, 3, RawMouseMovementMode::Relative))
        .unwrap();
    session.pause();
    session.begin_collecting(2_000, false, false).unwrap();
    session
        .append(record(2_000, 20, 8, 8, RawMouseMovementMode::Absolute))
        .unwrap();
    let snapshot = session.snapshot().unwrap();
    let events: Vec<_> = snapshot.events().collect();
    assert_eq!(events.len(), 3);
    assert_eq!(events[0].logical_ticks, 0);
    assert_eq!(events[1].logical_ticks, 100);
    assert_eq!(events[2].logical_ticks, 100);
    assert_eq!(events[2].raw_relative_ticks, 1_000);
    assert_eq!(events[2].delta_x, 0);
    assert_eq!(events[2].delta_y, 0);
    assert_eq!(events[2].session_segment_id, 2);
    assert_eq!(events[0].packet_kind, MousePerformancePacketKind::Motion);
    assert_eq!(snapshot.segments().len(), 2);
}

#[test]
fn event_cap_pauses_and_does_not_accept_more_records() {
    let mut settings = config();
    settings.max_session_events = 2;
    let budget = SharedByteBudget::new(settings.managed_memory_budget_bytes);
    let mut session = Session::new(settings, budget, SessionMetadata::default()).unwrap();
    session.begin_collecting(0, true, false).unwrap();
    session
        .append(record(0, 1, 1, 0, RawMouseMovementMode::Relative))
        .unwrap();
    session
        .append(record(1, 2, 1, 0, RawMouseMovementMode::Relative))
        .unwrap();
    let before_loss = session.snapshot().unwrap();
    assert_eq!(before_loss.integrity(), CaptureIntegrity::Complete);
    assert_eq!(
        session
            .append(record(2, 3, 1, 0, RawMouseMovementMode::Relative,))
            .unwrap(),
        AppendOutcome::PausedAtLimit
    );
    assert_eq!(session.event_count(), 2);
    assert_eq!(session.status(), SessionStatus::Paused);
    assert!(session.can_continue());
    assert_eq!(
        session
            .append(record(3, 4, 1, 0, RawMouseMovementMode::Relative,))
            .unwrap(),
        AppendOutcome::IgnoredWhileStopped
    );
    assert_eq!(session.integrity(), CaptureIntegrity::Incomplete);
    assert_eq!(session.capture_evidence().ignored_after_limit, 2);
    session.set_capture_evidence(CaptureEvidence::complete());
    session.stop();
    let truncated = session.snapshot().unwrap();
    assert_eq!(truncated.integrity(), CaptureIntegrity::Incomplete);
    assert_eq!(truncated.capture_evidence().ignored_after_limit, 2);
    assert_eq!(before_loss.integrity(), CaptureIntegrity::Complete);
    assert!(before_loss.events().eq(truncated.events()));
    session.reset().unwrap();
    assert_eq!(session.capture_evidence(), CaptureEvidence::complete());
    assert_eq!(truncated.integrity(), CaptureIntegrity::Incomplete);
    assert_eq!(
        truncated.clone().capture_evidence(),
        truncated.capture_evidence()
    );
}

#[test]
fn shared_budget_failure_leaves_existing_reservation_intact() {
    let mut settings = config();
    settings.managed_memory_budget_bytes = 32 * 1024;
    let budget = SharedByteBudget::new(settings.managed_memory_budget_bytes);
    let mut first =
        Session::new(settings.clone(), budget.clone(), SessionMetadata::default()).unwrap();
    let mut second = Session::new(settings, budget.clone(), SessionMetadata::default()).unwrap();
    first.begin_collecting(0, true, false).unwrap();
    first
        .append(record(0, 1, 1, 0, RawMouseMovementMode::Relative))
        .unwrap();
    second.begin_collecting(0, true, false).unwrap();
    let used_before = budget.reserved_bytes();
    let _other_owner = budget.try_reserve(budget.available_bytes()).unwrap();
    let result = second.append(record(0, 1, 1, 0, RawMouseMovementMode::Relative));
    assert!(matches!(result, Err(SessionError::BudgetExceeded(_))));
    assert_eq!(budget.reserved_bytes(), budget.limit_bytes());
    assert_eq!(first.event_count(), 1);
    drop(_other_owner);
    assert_eq!(budget.reserved_bytes(), used_before);
}

#[test]
fn unknown_capture_evidence_stays_unknown_until_a_fresh_session() {
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    session.begin_collecting(1, true, false).unwrap();
    session
        .append(record(1, 1, 1, 0, RawMouseMovementMode::Relative))
        .unwrap();
    session.set_capture_evidence(CaptureEvidence::default());
    let unknown = session.snapshot().unwrap();
    session.set_capture_evidence(CaptureEvidence::complete());
    assert_eq!(session.integrity(), CaptureIntegrity::Unknown);
    session.reset().unwrap();
    assert_eq!(session.integrity(), CaptureIntegrity::Complete);
    assert_eq!(unknown.integrity(), CaptureIntegrity::Unknown);
}

#[test]
fn capture_evidence_only_revises_data_when_integrity_changes() {
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    session.begin_collecting(1, true, false).unwrap();
    session
        .append(record(1, 1, 1, 0, RawMouseMovementMode::Relative))
        .unwrap();
    let original = session.snapshot().unwrap();
    let revision = session.session_revision();
    let mut evidence = CaptureEvidence {
        generation: Some(1),
        received_sequence: Some(1),
        ..CaptureEvidence::complete()
    };
    session.set_capture_evidence(evidence);
    session.set_capture_evidence(evidence);
    assert_eq!(session.session_revision(), revision);
    evidence.received_sequence = Some(2);
    session.set_capture_evidence(evidence);
    assert_eq!(session.session_revision(), revision);
    evidence.integrity = CaptureIntegrity::Unknown;
    session.set_capture_evidence(evidence);
    assert_eq!(session.session_revision(), revision + 1);
    session.set_capture_evidence(evidence);
    assert_eq!(session.session_revision(), revision + 1);
    evidence.integrity = CaptureIntegrity::Incomplete;
    session.set_capture_evidence(evidence);
    let incomplete = session.snapshot().unwrap();
    assert_eq!(incomplete.session_revision(), revision + 2);
    assert!(original.events().eq(incomplete.events()));
    assert_eq!(original.integrity(), CaptureIntegrity::Complete);
    let allocations = session.allocation_stats();
    for _ in 0..1000 {
        session.set_capture_evidence(evidence);
    }
    assert_eq!(session.session_revision(), revision + 2);
    assert_eq!(session.allocation_stats(), allocations);
}

#[test]
fn timestamp_conversion_matches_away_from_zero_rounding() {
    assert_eq!(convert_ticks(5, 2, 3).unwrap(), 8);
    assert_eq!(convert_ticks(4, 2, 3).unwrap(), 6);
    assert_eq!(convert_ticks(1, 10, 3).unwrap(), 0);
}

#[test]
fn exchange_parser_accepts_csharp_property_names() {
    let document = ExchangeDocument::from_json(
        r#"{
          "FormatVersion": 8,
          "ExportedAtUtc": "2026-10-03T00:00:00.0000000+00:00",
          "Mouse": {
            "DisplayName": "fixture",
            "DeviceId": "mouse-a",
            "ButtonCount": 5,
            "IsVirtual": false
          },
          "Session": {
            "EffectiveCpi": 800.0,
            "HostTimestampFrequency": 10000000
          },
          "Segments": [{
            "SegmentId": 1,
            "StartedAtRawCaptureTicks": 100,
            "BaseCaptureSequence": 1,
            "PacketRows": [[4, 0, 0, 0, 0, 0, 10, 0, 1, 1, 1]]
          }]
        }"#,
    )
    .unwrap();
    let settings = config();
    let budget = SharedByteBudget::new(settings.managed_memory_budget_bytes);
    let snapshot = document.replay(settings, budget).unwrap();
    assert_eq!(snapshot.event_count(), 1);
    assert_eq!(
        snapshot.event_at(0).unwrap().packet_kind,
        MousePerformancePacketKind::Motion
    );
}

#[test]
fn frozen_v8_fixture_replays_through_native_session() {
    let document = ExchangeDocument::from_json(include_str!(
        "../../../tests/fixtures/foundation-exchange-v8.json"
    ))
    .unwrap();
    let expected: ExpectedFixture = serde_json::from_str(include_str!(
        "../../../tests/fixtures/foundation-expected.json"
    ))
    .unwrap();
    let settings = SessionConfig::default();
    let budget = SharedByteBudget::new(settings.managed_memory_budget_bytes);
    let snapshot = document.replay(settings, budget).unwrap();
    let events: Vec<_> = snapshot.events().collect();

    assert_eq!(
        snapshot.metadata().display_name,
        expected.metadata.display_name
    );
    assert_eq!(snapshot.metadata().device_id, expected.metadata.device_id);
    assert_eq!(snapshot.metadata().vendor_id, expected.metadata.vendor_id);
    assert_eq!(snapshot.metadata().product_id, expected.metadata.product_id);
    assert_eq!(events.len(), expected.records.len());
    for (actual, expected) in events.iter().zip(expected.records.iter()) {
        assert_eq!(actual.raw_capture_ticks, expected.raw_capture_ticks);
        assert_eq!(actual.raw_relative_ticks, expected.raw_relative_ticks);
        assert_eq!(actual.logical_ticks, expected.logical_ticks);
        assert_eq!(actual.capture_sequence, expected.capture_sequence);
        assert_eq!(actual.timing_sequence, expected.timing_sequence);
        assert_eq!(actual.raw_delta_x, expected.delta_x);
        assert_eq!(actual.raw_delta_y, expected.delta_y);
        assert_eq!(actual.delta_x, expected.relative_delta_x);
        assert_eq!(actual.delta_y, expected.relative_delta_y);
        assert_eq!(actual.session_cumulative_x, expected.cumulative_x);
        assert_eq!(actual.session_cumulative_y, expected.cumulative_y);
        assert_eq!(actual.session_segment_id, expected.segment_id);
        assert_eq!(actual.packet_kind as u8, expected.packet_kind);
        assert_eq!(actual.movement_mode as u8, expected.movement_mode);
        assert_eq!(actual.button_flags, expected.button_flags);
        assert_eq!(actual.raw_mouse_flags, expected.raw_mouse_flags);
        assert_eq!(actual.button_data, expected.button_data);
        assert_eq!(actual.extra_information, expected.extra_information);
    }
}

#[test]
fn snapshot_directory_is_shared_and_fixed_prefix_remains_immutable() {
    let settings = config();
    let budget = SharedByteBudget::new(settings.managed_memory_budget_bytes);
    let mut session = Session::new(settings, budget.clone(), SessionMetadata::default()).unwrap();
    session.begin_collecting(0, true, false).unwrap();
    for index in 0..5 {
        session
            .append(record(
                index,
                index + 1,
                1,
                0,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    let first = session.snapshot().unwrap();
    let second = session.snapshot().unwrap();
    assert!(first.shares_committed_directory_with(&second));
    assert!(first.tail_event_count() <= session.config().chunk_capacity);
    let reserved_before_clone = budget.reserved_bytes();
    let cloned = first.clone();
    assert_eq!(budget.reserved_bytes(), reserved_before_clone);
    for index in 5..12 {
        session
            .append(record(
                index,
                index + 1,
                1,
                0,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    let current = session.snapshot().unwrap();
    assert_eq!(first.event_count(), 5);
    assert_eq!(first.events().count(), 5);
    assert_eq!(cloned.event_at(4).unwrap().capture_sequence, 5);
    assert_eq!(current.event_count(), 12);
    assert_eq!(current.event_at(11).unwrap().session_cumulative_x, 12);
}

#[test]
fn reset_only_reuses_unobserved_chunks_and_clear_pool_releases_reservations() {
    let settings = config();
    let budget = SharedByteBudget::new(settings.managed_memory_budget_bytes);
    let mut session = Session::new(settings, budget.clone(), SessionMetadata::default()).unwrap();
    session.begin_collecting(0, true, false).unwrap();
    for index in 0..5 {
        session
            .append(record(
                index,
                index + 1,
                1,
                0,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    let snapshot = session.snapshot().unwrap();
    let old_generation = snapshot.generation();
    session.reset().unwrap();
    session.begin_collecting(100, false, false).unwrap();
    session
        .append(record(100, 10, 9, 0, RawMouseMovementMode::Relative))
        .unwrap();
    assert_eq!(session.allocation_stats().chunks_reused, 1);
    assert_ne!(old_generation, session.generation());
    assert_eq!(session.event_count(), 1);
    assert_eq!(snapshot.event_count(), 5);
    assert_eq!(snapshot.event_at(0).unwrap().session_cumulative_x, 1);
    assert_eq!(snapshot.event_at(0).unwrap().raw_delta_x, 1);
    assert_eq!(snapshot.event_at(4).unwrap().raw_capture_ticks, 4);
    drop(snapshot);
    session.reset().unwrap();
    let before_clear = budget.reserved_bytes();
    assert!(session.pool_bytes() > 0);
    session.clear_pool();
    assert!(budget.reserved_bytes() < before_clear);
    drop(session);
    assert_eq!(budget.reserved_bytes(), 0);
}

#[test]
fn failed_append_preserves_origin_and_existing_prefix() {
    let settings = config();
    let budget = SharedByteBudget::new(settings.managed_memory_budget_bytes);
    let mut session = Session::new(settings, budget.clone(), SessionMetadata::default()).unwrap();
    session.begin_collecting(0, true, false).unwrap();
    let blocked = budget.try_reserve(budget.available_bytes()).unwrap();
    assert!(matches!(
        session.append(record(20, 1, 7, 0, RawMouseMovementMode::Relative)),
        Err(SessionError::BudgetExceeded(_))
    ));
    assert_eq!(session.event_count(), 0);
    assert_eq!(session.summary_sum_x(), 0);
    drop(blocked);
    session
        .append(record(50, 2, 3, 0, RawMouseMovementMode::Relative))
        .unwrap();
    let snapshot = session.snapshot().unwrap();
    assert_eq!(snapshot.event_at(0).unwrap().raw_relative_ticks, 0);
    assert_eq!(snapshot.event_at(0).unwrap().logical_ticks, 0);
}

#[test]
fn snapshot_budget_failure_is_explicit_and_recoverable() {
    let settings = config();
    let budget = SharedByteBudget::new(settings.managed_memory_budget_bytes);
    let mut session = Session::new(settings, budget.clone(), SessionMetadata::default()).unwrap();
    session.begin_collecting(0, true, false).unwrap();
    session
        .append(record(1, 1, 1, 0, RawMouseMovementMode::Relative))
        .unwrap();
    let blocked = budget.try_reserve(budget.available_bytes()).unwrap();
    assert!(matches!(
        session.snapshot(),
        Err(SessionError::BudgetExceeded(_))
    ));
    assert_eq!(session.event_count(), 1);
    drop(blocked);
    assert_eq!(session.snapshot().unwrap().event_count(), 1);
}

#[test]
fn integer_boundaries_and_reordered_time_preserve_reference_semantics() {
    let settings = config();
    let budget = SharedByteBudget::new(settings.managed_memory_budget_bytes);
    let mut session = Session::new(settings, budget, SessionMetadata::default()).unwrap();
    session.begin_collecting(5, true, false).unwrap();
    session
        .append(record(
            10,
            8,
            i32::MAX,
            i32::MIN,
            RawMouseMovementMode::Relative,
        ))
        .unwrap();
    session
        .append(record(
            9,
            7,
            i32::MAX,
            i32::MIN,
            RawMouseMovementMode::Relative,
        ))
        .unwrap();
    let snapshot = session.snapshot().unwrap();
    assert_eq!(snapshot.event_at(1).unwrap().raw_relative_ticks, 0);
    assert_eq!(snapshot.event_at(1).unwrap().logical_ticks, 0);
    assert_eq!(snapshot.summary_sum_x(), i64::from(i32::MAX) * 2);
    assert_eq!(snapshot.summary_sum_y(), i64::from(i32::MIN) * 2);
    assert_eq!(snapshot.event_at(1).unwrap().capture_sequence, 7);
}

#[test]
fn shared_budget_atomic_reservations_never_overbook() {
    let budget = SharedByteBudget::new(8);
    let handles: Vec<_> = (0..32)
        .map(|_| {
            let budget = budget.clone();
            std::thread::spawn(move || budget.try_reserve(1))
        })
        .collect();
    let reservations: Vec<_> = handles
        .into_iter()
        .filter_map(|handle| handle.join().unwrap().ok())
        .collect();
    assert_eq!(reservations.len(), 8);
    assert_eq!(budget.reserved_bytes(), 8);
    drop(reservations);
    assert_eq!(budget.reserved_bytes(), 0);
}

#[derive(Deserialize)]
struct ExpectedFixture {
    metadata: ExpectedMetadata,
    records: Vec<ExpectedRecord>,
}

#[derive(Deserialize)]
struct ExpectedMetadata {
    display_name: String,
    device_id: String,
    vendor_id: Option<i32>,
    product_id: Option<i32>,
}

#[derive(Deserialize)]
struct ExpectedRecord {
    raw_capture_ticks: u64,
    raw_relative_ticks: u64,
    logical_ticks: u64,
    capture_sequence: u64,
    timing_sequence: u64,
    delta_x: i32,
    delta_y: i32,
    relative_delta_x: i32,
    relative_delta_y: i32,
    cumulative_x: i64,
    cumulative_y: i64,
    segment_id: u32,
    packet_kind: u8,
    movement_mode: u8,
    button_flags: u16,
    raw_mouse_flags: u16,
    button_data: u16,
    extra_information: u32,
}
