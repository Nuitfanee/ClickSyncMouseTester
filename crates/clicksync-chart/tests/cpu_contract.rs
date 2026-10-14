mod common;

use clicksync_chart::{
    Axis, Change, ChartEngine, ChartError, Coordinate, DatasetId, FixedPrefix, HistogramLayout,
    HistogramPolicy, HistogramScale, HistogramSpec, Integrity, Metric, RebuildReason, TimeBasis,
    query,
};

#[test]
fn cross_chunk_connectors_are_unique_breaks_are_real_and_zero_length_is_preserved() {
    let snapshots = common::replay(2, 20, 8000, true);
    let snapshot = snapshots.last().unwrap();
    let mut engine = ChartEngine::new(
        common::parameters(Metric::Trajectory, TimeBasis::RawCapture),
        3,
    )
    .unwrap();
    engine
        .finish(FixedPrefix::new(DatasetId(1), snapshot))
        .unwrap();
    let pairs: Vec<_> = engine
        .geometry()
        .blocks()
        .iter()
        .flat_map(|b| b.segments())
        .collect();
    assert_eq!(pairs.len(), snapshot.event_count() - 2);
    assert!(
        pairs
            .iter()
            .all(|(a, b)| a.source_index + 1 == b.source_index && a.segment_id == b.segment_id)
    );
    assert!(pairs.iter().any(|(a, b)| a.point == b.point));
    assert!(
        engine
            .geometry()
            .blocks()
            .iter()
            .any(|b| b.connector.is_some())
    );
}

#[test]
fn interval_rules_keep_basis_distinction_and_drop_nonpositive_ticks() {
    let mut session = common::session(4, "interval-basis");
    session.begin_collecting(1000, true, true).unwrap();
    for (i, ticks) in [1000, 1125, 1250, 1250].into_iter().enumerate() {
        session.append(common::record(i, ticks)).unwrap();
    }
    session.pause();
    session.begin_collecting(90_000, false, true).unwrap();
    for (i, ticks) in [90_125, 90_250, 90_375, 90_500].into_iter().enumerate() {
        session.append(common::record(i + 4, ticks)).unwrap();
    }
    let snapshot = session.snapshot().unwrap();
    let prefix = FixedPrefix::new(DatasetId(1), &snapshot);
    let raw = query(
        prefix,
        0..8,
        &common::parameters(Metric::Interval, TimeBasis::RawCapture),
    )
    .unwrap();
    let logical = query(
        prefix,
        0..8,
        &common::parameters(Metric::Interval, TimeBasis::Logical),
    )
    .unwrap();
    assert!(
        !raw.samples
            .iter()
            .any(|s| s.source_index == 3 || s.source_index == 4)
    );
    assert!(logical.samples.iter().any(|s| s.source_index == 4));
    assert!(raw.samples.iter().all(|s| s.point.y.value() > 0.0));
}

#[test]
fn query_uses_old_fixed_prefix_and_exact_integers_with_trajectory_rebasing() {
    let mut session = common::session(4, "query");
    session.begin_collecting(1000, true, true).unwrap();
    for i in 0..6 {
        session
            .append(common::record(i, 1000 + i as u64 * 1000))
            .unwrap();
    }
    let old = session.snapshot().unwrap();
    session.reset().unwrap();
    session.begin_collecting(1000, false, true).unwrap();
    session.append(common::record(99, 1000)).unwrap();
    let prefix = FixedPrefix::new(DatasetId(3), &old)
        .with_end(5)
        .unwrap()
        .with_integrity(Integrity::Incomplete);
    let result = query(
        prefix,
        2..5,
        &common::parameters(Metric::Trajectory, TimeBasis::RawCapture),
    )
    .unwrap();
    let origin = old.event_at(1).unwrap();
    assert_eq!(result.records.len(), 3);
    assert_eq!(result.data_version.end, 5);
    assert_eq!(result.data_version.integrity, Integrity::Incomplete);
    for sample in result.samples {
        let e = old.event_at(sample.source_index).unwrap();
        assert_eq!(
            sample.point.x,
            Coordinate::Integer(e.session_cumulative_x - origin.session_cumulative_x)
        );
        assert_eq!(
            sample.point.y,
            Coordinate::Integer(e.session_cumulative_y - origin.session_cumulative_y)
        );
    }
    assert_eq!(
        Coordinate::Integer(9_007_199_254_740_993)
            .relative_to(Coordinate::Integer(9_007_199_254_740_992)),
        1.0
    );
    assert_eq!(
        Coordinate::Time {
            ticks: 9_007_199_254_740_993,
            frequency: 1_000_000
        }
        .relative_to(Coordinate::Time {
            ticks: 9_007_199_254_740_992,
            frequency: 1_000_000
        }),
        0.001
    );
}

#[test]
fn window_queries_keep_context_then_clip_not_restart_the_ema() {
    let snapshots = common::replay(4, 40, 1000, true);
    let prefix = FixedPrefix::new(DatasetId(1), snapshots.last().unwrap());
    for metric in [
        Metric::CountTrend(Axis::X),
        Metric::Frequency,
        Metric::Velocity(Axis::X),
        Metric::PathSpeed,
    ] {
        let p = common::parameters(metric, TimeBasis::RawCapture);
        let all = query(prefix, 0..prefix.len(), &p).unwrap();
        let selected = query(prefix, 9..17, &p).unwrap();
        assert_eq!(
            selected.samples,
            all.samples
                .into_iter()
                .filter(|s| (9..17).contains(&s.source_index))
                .collect::<Vec<_>>()
        );
        assert_eq!(selected.context_range, 0..prefix.len());
    }
}

#[test]
fn fixed_histogram_clamps_and_percent_denominator_tracks_revisions() {
    let snapshots = common::replay(4, 40, 1000, false);
    let mut p = common::parameters(Metric::Count(Axis::X), TimeBasis::RawCapture);
    p.histogram = Some(common::fixed_histogram());
    let mut engine = ChartEngine::new(p.clone(), 4).unwrap();
    for snapshot in &snapshots {
        let delta = engine
            .advance(FixedPrefix::new(DatasetId(1), snapshot))
            .unwrap();
        assert!(delta.work.histogram_rebins <= delta.work.new_record_reads);
    }
    let prefix = FixedPrefix::new(DatasetId(1), snapshots.last().unwrap());
    engine.finish(prefix).unwrap();
    let histogram = engine.histogram().unwrap();
    assert_eq!(histogram.counts.iter().sum::<usize>(), 40);
    assert!(histogram.counts[0] > 0 && histogram.counts[7] > 0);
    assert!((histogram.bins().iter().map(|b| b.value).sum::<f64>() - 100.0).abs() < 1e-12);
    let selected = query(prefix, 4..9, &p).unwrap();
    assert_eq!(selected.histogram.unwrap().valid_samples, 5);
}

#[test]
fn automatic_layout_versions_and_rebuild_ranges_are_explicit() {
    let snapshots = common::replay(4, 20, 1000, false);
    let mut p = common::parameters(Metric::Count(Axis::X), TimeBasis::RawCapture);
    p.histogram = Some(HistogramSpec {
        policy: HistogramPolicy::Automatic,
        scale: HistogramScale::Count,
    });
    let mut engine = ChartEngine::new(p, 4).unwrap();
    let a = engine
        .advance(FixedPrefix::new(DatasetId(1), &snapshots[1]))
        .unwrap();
    let first_version = engine.histogram().unwrap().layout_version;
    let b = engine
        .advance(FixedPrefix::new(DatasetId(1), &snapshots[5]))
        .unwrap();
    assert!(engine.histogram().unwrap().layout_version > first_version);
    for delta in [a, b] {
        assert!(delta.changes.iter().any(|c| matches!(c, Change::Rebuild { source_range, reason: RebuildReason::AutomaticHistogramLayout } if source_range.start == 0)));
    }
}

#[test]
fn legacy_layout_padding_rounding_and_bin_limits_are_frozen() {
    use clicksync_chart::analysis::histogram::resolve_layout;
    assert_eq!(resolve_layout(&[f64::NAN, f64::INFINITY], false), None);
    assert_eq!(
        resolve_layout(&[-1.5, 1.5], true),
        Some(HistogramLayout {
            minimum: -2.5,
            maximum: 2.5,
            width: 1.0,
            bins: 5
        })
    );
    let singleton = resolve_layout(&[100.0], false).unwrap();
    assert_eq!(
        singleton,
        HistogramLayout {
            minimum: 90.0,
            maximum: 110.0,
            width: 2.5,
            bins: 8
        }
    );
    let wide = resolve_layout(&[-200.0, 200.0], true).unwrap();
    assert_eq!(wide.width, 5.0);
    assert_eq!(wide.bins, 81);
    assert_eq!(wide.maximum, 205.0);
}

#[test]
fn exact_velocity_is_published_live_and_sealed_segments_share_every_arc() {
    let snapshots = common::replay(4, 40, 1000, true);
    let mut engine = ChartEngine::new(
        common::parameters(Metric::PathSpeed, TimeBasis::RawCapture),
        4,
    )
    .unwrap();
    let before = snapshots.iter().find(|s| s.event_count() == 20).unwrap();
    engine
        .advance(FixedPrefix::new(DatasetId(1), before))
        .unwrap();
    assert!(!engine.geometry().blocks().is_empty());
    let sealed = engine.geometry().blocks().to_vec();
    let after = snapshots.iter().find(|s| s.event_count() == 21).unwrap();
    engine
        .advance(FixedPrefix::new(DatasetId(1), after))
        .unwrap();
    assert_eq!(engine.state().published_end, 21);
    assert_eq!(engine.state().revisable_from, 20);
    let last = snapshots.last().unwrap();
    engine
        .advance(FixedPrefix::new(DatasetId(1), last))
        .unwrap();
    for (old, current) in sealed.iter().zip(engine.geometry().blocks()) {
        assert!(std::sync::Arc::ptr_eq(old, current));
    }
    let final_delta = engine.finish(FixedPrefix::new(DatasetId(1), last)).unwrap();
    assert_eq!(final_delta.work.derived_evaluations, 0);
    assert_eq!(final_delta.work.smoothing_evaluations, 0);
    assert!(final_delta.changes.is_empty());
}

#[test]
fn prefix_identity_finish_and_empty_session_are_checked() {
    let snapshots = common::replay(4, 8, 1000, false);
    let mut engine = ChartEngine::new(
        common::parameters(Metric::Count(Axis::X), TimeBasis::RawCapture),
        4,
    )
    .unwrap();
    engine
        .advance(FixedPrefix::new(DatasetId(1), &snapshots[4]))
        .unwrap();
    assert_eq!(
        engine
            .advance(FixedPrefix::new(DatasetId(2), &snapshots[5]))
            .unwrap_err(),
        ChartError::WrongDataset
    );
    assert_eq!(
        engine
            .advance(FixedPrefix::new(DatasetId(1), &snapshots[3]))
            .unwrap_err(),
        ChartError::RegressedPrefix
    );
    let final_prefix = FixedPrefix::new(DatasetId(1), snapshots.last().unwrap());
    engine.finish(final_prefix).unwrap();
    assert!(engine.finish(final_prefix).unwrap().changes.is_empty());
    let mut empty = ChartEngine::new(
        common::parameters(Metric::Frequency, TimeBasis::RawCapture),
        4,
    )
    .unwrap();
    empty
        .finish(FixedPrefix::new(DatasetId(5), &snapshots[0]))
        .unwrap();
    assert_eq!(empty.state().processed_end, 0);
    assert!(empty.geometry().blocks().is_empty());
}

#[test]
fn independent_datasets_keep_independent_cutoffs_and_generations() {
    let a = common::replay(4, 12, 1000, false);
    let b = common::replay(3, 5, 8000, false);
    let pa = FixedPrefix::new(DatasetId(1), a.last().unwrap())
        .with_end(9)
        .unwrap();
    let pb = FixedPrefix::new(DatasetId(2), b.last().unwrap())
        .with_end(3)
        .unwrap();
    let params = common::parameters(Metric::Cumulative(Axis::Y), TimeBasis::RawCapture);
    assert_eq!(query(pa, 0..9, &params).unwrap().data_version.end, 9);
    assert_eq!(query(pb, 0..3, &params).unwrap().data_version.end, 3);
}
