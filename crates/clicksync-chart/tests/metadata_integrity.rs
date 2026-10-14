use clicksync_chart::{
    Axis, ChartEngine, ChartParameters, DatasetId, FixedPrefix, Integrity, Metric,
    gpu::{PlotKind, PreparedScene},
};
use clicksync_core::{
    RawMouseMovementMode, RawMouseRecord, Session,
    session::{CaptureEvidence, CaptureIntegrity},
};
use std::sync::Arc;

#[test]
fn sealed_integrity_updates_reuse_every_sample_and_geometry_block() {
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    session.begin_collecting(100, true, false).unwrap();
    for i in 0..256 {
        session
            .append(RawMouseRecord::new(
                i % 7,
                i % 5,
                100 + i as u64 * 1250,
                i as u64 + 1,
                i as u64 + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    session.stop();
    let complete = session.snapshot().unwrap();
    let mut parameters = ChartParameters::new(Metric::Velocity(Axis::X));
    parameters.cpi = Some(800.0);
    let mut engine = ChartEngine::new(parameters, 32).unwrap();
    engine
        .finish(FixedPrefix::new(DatasetId(1), &complete))
        .unwrap();
    let before = PreparedScene::from_engine(&engine, PlotKind::Lines).unwrap();
    let geometry = engine.geometry().blocks().to_vec();
    session.set_capture_evidence(CaptureEvidence {
        integrity: CaptureIntegrity::Incomplete,
        overflow: 1,
        ..Default::default()
    });
    let incomplete = session.snapshot().unwrap();
    let prefix = FixedPrefix::new(DatasetId(1), &incomplete);
    assert_eq!(
        prefix
            .with_integrity(Integrity::Complete)
            .version()
            .integrity,
        Integrity::Incomplete
    );
    let delta = engine.advance(prefix).unwrap();
    assert_eq!(delta.work.new_record_reads, 0);
    assert_eq!(delta.work.geometry_samples_written, 0);
    assert!(delta.changes.is_empty() && delta.state.finished);
    assert_eq!(
        delta.state.data_version.unwrap().integrity,
        Integrity::Incomplete
    );
    assert!(complete.events().eq(incomplete.events()));
    assert_eq!(complete.integrity(), CaptureIntegrity::Complete);
    assert!(
        geometry
            .iter()
            .zip(engine.geometry().blocks())
            .all(|(a, b)| Arc::ptr_eq(a, b))
    );
    let after =
        PreparedScene::from_engine_reusing(&engine, PlotKind::Lines, Some(&before)).unwrap();
    assert!(
        before
            .blocks
            .iter()
            .zip(after.blocks)
            .all(|(a, b)| Arc::ptr_eq(a, &b))
    );
}

#[test]
fn missing_capture_evidence_cannot_be_upgraded_by_a_chart_wrapper() {
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    session.set_capture_evidence(CaptureEvidence::default());
    let snapshot = session.snapshot().unwrap();
    let prefix = FixedPrefix::new(DatasetId(1), &snapshot);
    assert_eq!(prefix.version().integrity, Integrity::Unknown);
    assert_eq!(
        prefix
            .with_integrity(Integrity::Complete)
            .version()
            .integrity,
        Integrity::Unknown
    );
}
