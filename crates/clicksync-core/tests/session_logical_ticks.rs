use clicksync_core::{
    AppendOutcome, RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SessionMetadata,
    SharedByteBudget,
};

const MAX_SIGNED_TICKS: u64 = i64::MAX as u64;

fn session() -> Session {
    let config = SessionConfig {
        max_session_events: 32,
        warning_event_threshold: 16,
        chunk_capacity: 2,
        managed_memory_budget_bytes: 32 * 1024,
        timestamp_frequency: 10_000_000,
    };
    let budget = SharedByteBudget::new(config.managed_memory_budget_bytes);
    Session::new(config, budget, SessionMetadata::default()).unwrap()
}

fn append(session: &mut Session, raw_ticks: u64) {
    let sequence = session.event_count() as u64 + 1;
    assert_eq!(
        session
            .append(RawMouseRecord::new(
                1,
                0,
                raw_ticks,
                sequence,
                sequence,
                RawMouseMovementMode::Relative,
            ))
            .unwrap(),
        AppendOutcome::Appended
    );
}

fn logical_ticks(session: &mut Session) -> Vec<u64> {
    session
        .snapshot()
        .unwrap()
        .events()
        .map(|event| event.logical_ticks)
        .collect()
}

#[test]
fn logical_timestamp_overflow_does_not_poison_segment_anchor() {
    let mut session = session();
    session.begin_collecting(0, true, true).unwrap();
    append(&mut session, MAX_SIGNED_TICKS - 4);
    session.pause();
    session.begin_collecting(0, false, false).unwrap();
    for raw_ticks in [5, 3, 2, 4, 5] {
        append(&mut session, raw_ticks);
    }

    assert_eq!(
        logical_ticks(&mut session),
        [
            MAX_SIGNED_TICKS - 4,
            MAX_SIGNED_TICKS - 4,
            MAX_SIGNED_TICKS - 1,
            MAX_SIGNED_TICKS - 1,
            MAX_SIGNED_TICKS,
            MAX_SIGNED_TICKS,
        ]
    );
    assert_eq!(session.event_count(), 6);
    assert_eq!(session.summary_sum_x(), 6);
    let snapshot = session.snapshot().unwrap();
    let events: Vec<_> = snapshot.events().collect();
    assert_eq!(
        events
            .iter()
            .map(|event| event.raw_capture_ticks)
            .collect::<Vec<_>>(),
        [MAX_SIGNED_TICKS - 4, 5, 3, 2, 4, 5]
    );
    for (index, event) in events.iter().enumerate() {
        assert_eq!(event.capture_sequence, index as u64 + 1);
        assert_eq!(event.timing_sequence, index as u64 + 1);
        assert_eq!(event.session_segment_id, if index == 0 { 1 } else { 2 });
    }
}

#[test]
fn logical_timestamp_repeated_resumes_at_signed_maximum_keep_appending() {
    let mut session = session();
    session.begin_collecting(0, true, true).unwrap();
    append(&mut session, MAX_SIGNED_TICKS);
    for _ in 0..3 {
        session.pause();
        session.begin_collecting(0, false, false).unwrap();
        append(&mut session, MAX_SIGNED_TICKS);
    }

    assert_eq!(logical_ticks(&mut session), [MAX_SIGNED_TICKS; 4]);
    assert_eq!(session.event_count(), 4);
    assert_eq!(session.summary_sum_x(), 4);
    let snapshot = session.snapshot().unwrap();
    assert_eq!(snapshot.segments().len(), 4);
    for (index, event) in snapshot.events().enumerate() {
        assert_eq!(event.session_segment_id, index as u32 + 1);
    }
}

#[test]
fn logical_timestamp_near_limit_subtraction_uses_resume_anchor() {
    let mut session = session();
    session.begin_collecting(0, true, true).unwrap();
    append(&mut session, 100);
    session.pause();
    session
        .begin_collecting(MAX_SIGNED_TICKS - 20, false, false)
        .unwrap();
    for raw_ticks in [
        MAX_SIGNED_TICKS,
        MAX_SIGNED_TICKS - 21,
        MAX_SIGNED_TICKS - 1,
    ] {
        append(&mut session, raw_ticks);
    }

    assert_eq!(logical_ticks(&mut session), [100, 120, 120, 120]);
}
