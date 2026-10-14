use super::*;
use clicksync_core::session::{CaptureEvidence, CaptureIntegrity};
use clicksync_core::{RawMouseMovementMode, RawMouseRecord, Session};

fn input() -> SessionSnapshot {
    input_with_evidence(CaptureEvidence::complete())
}

fn input_with_evidence(evidence: CaptureEvidence) -> SessionSnapshot {
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    session.begin_collecting(100, true, false).unwrap();
    for (index, tick) in [100, 200, 200, 150].into_iter().enumerate() {
        session
            .append(RawMouseRecord::new(
                1,
                0,
                tick,
                index as u64 + 1,
                index as u64 + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    session.pause();
    session.begin_collecting(10000, false, false).unwrap();
    session
        .append(RawMouseRecord::new(
            1,
            0,
            10000,
            5,
            5,
            RawMouseMovementMode::Relative,
        ))
        .unwrap();
    session.stop();
    session.set_capture_evidence(evidence);
    session.snapshot().unwrap()
}

#[test]
fn timestamp_mapping_uses_real_endpoints_and_handles_duplicate_or_reordered_reports() {
    let snapshot = input();
    assert_eq!(time_index(&snapshot, 0.01, false), Some(1));
    assert_eq!(time_index(&snapshot, 0.005, false), Some(3));
    assert!(time_index(&snapshot, 0.5, false).is_none());
    assert_eq!(index_range(&snapshot, Some((0.005, 0.01)), false), 1..4);
    assert!(
        convert_range(&snapshot, (0.005, 0.01), false).is_none(),
        "collapsed logical timestamps would add the next segment's first report"
    );
    assert!(convert_range(&snapshot, (0.0, 0.99), false).is_some());
}

#[test]
fn dataset_identity_includes_exact_input_and_independent_cpi() {
    let snapshot = Arc::new(input());
    assert!(same_group(
        &snapshot,
        Some(800.0),
        &snapshot.clone(),
        Some(800.0)
    ));
    assert!(!same_group(&snapshot, Some(800.0), &snapshot, Some(1600.0)));
    assert_eq!(
        group_identity(&snapshot, Some(800.0)),
        group_identity(&snapshot.clone(), Some(800.0))
    );
    assert_ne!(
        group_identity(&snapshot, Some(800.0)),
        group_identity(&snapshot, Some(1600.0))
    );
}

#[test]
fn dataset_identity_includes_all_capture_evidence_without_changing_raw_input() {
    let incomplete = CaptureEvidence {
        integrity: CaptureIntegrity::Incomplete,
        ignored_after_limit: 1,
        overflow: 1,
        disconnected: false,
        generation: Some(7),
        received_sequence: Some(10),
        queued: Some(10),
        cutoff: Some(11),
    };
    let snapshots = [
        CaptureEvidence::complete(),
        CaptureEvidence::default(),
        CaptureEvidence {
            integrity: CaptureIntegrity::Incomplete,
            ..CaptureEvidence::default()
        },
        incomplete,
        CaptureEvidence {
            ignored_after_limit: 2,
            ..incomplete
        },
        CaptureEvidence {
            overflow: 2,
            ..incomplete
        },
        CaptureEvidence {
            disconnected: true,
            ..incomplete
        },
        CaptureEvidence {
            generation: Some(8),
            ..incomplete
        },
        CaptureEvidence {
            received_sequence: Some(9),
            ..incomplete
        },
        CaptureEvidence {
            queued: Some(11),
            ..incomplete
        },
        CaptureEvidence {
            cutoff: Some(12),
            ..incomplete
        },
    ]
    .map(input_with_evidence);
    for (i, a) in snapshots.iter().enumerate() {
        for (j, b) in snapshots.iter().enumerate() {
            assert_eq!(a.generation(), b.generation());
            assert_eq!(a.timestamp_frequency(), b.timestamp_frequency());
            assert_eq!(a.metadata(), b.metadata());
            assert_eq!(a.segments(), b.segments());
            assert!(a.events().eq(b.events()));
            assert_eq!(same_group(a, Some(800.0), b, Some(800.0)), i == j);
            assert_eq!(
                group_identity(a, Some(800.0)) == group_identity(b, Some(800.0)),
                i == j
            );
        }
    }
}

#[test]
fn time_basis_conversion_keeps_membership_in_every_comparison_group() {
    let mut a = Session::default_with_metadata(Default::default()).unwrap();
    a.begin_collecting(100, true, false).unwrap();
    for (index, ticks) in [100, 200, 300].into_iter().enumerate() {
        a.append(RawMouseRecord::new(
            1,
            0,
            ticks,
            index as u64 + 1,
            index as u64 + 1,
            RawMouseMovementMode::Relative,
        ))
        .unwrap();
    }
    a.stop();
    let a = a.snapshot().unwrap();
    let mut b = Session::default_with_metadata(Default::default()).unwrap();
    b.begin_collecting(100, true, false).unwrap();
    for (index, ticks) in [100, 200].into_iter().enumerate() {
        b.append(RawMouseRecord::new(
            1,
            0,
            ticks,
            index as u64 + 1,
            index as u64 + 1,
            RawMouseMovementMode::Relative,
        ))
        .unwrap();
    }
    b.pause();
    b.begin_collecting(10000, false, false).unwrap();
    b.append(RawMouseRecord::new(
        1,
        0,
        10000,
        3,
        3,
        RawMouseMovementMode::Relative,
    ))
    .unwrap();
    b.stop();
    let b = b.snapshot().unwrap();
    assert!(convert_range(&a, (0.0, 0.025), false).is_some());
    assert_eq!(
        convert_group_range(&[&a, &b], (0.0, 0.025), false),
        None,
        "conversion must not introduce the comparison group's excluded paused report"
    );
    assert_eq!(
        convert_group_range(&[&a, &a], (0.0, 0.025), false),
        Some((0.0, 0.02))
    );
}
