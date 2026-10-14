mod common;

use clicksync_chart::{
    ChartEngine, DatasetId, FixedPrefix, HistogramPolicy, HistogramScale, HistogramSpec, Metric,
    TimeBasis,
};
use std::sync::Arc;

#[test]
fn complete_and_incremental_modes_converge_for_all_metrics_and_layouts() {
    let mut cases = Vec::new();
    for rate in [1000, 8000] {
        for capacity in [1, 4, 32] {
            let snapshots = common::replay(capacity, 97, rate, true);
            let final_prefix = FixedPrefix::new(DatasetId(1), snapshots.last().unwrap());
            for basis in [TimeBasis::RawCapture, TimeBasis::Logical] {
                for metric in common::metrics() {
                    for histogram in [
                        None,
                        Some(common::fixed_histogram()),
                        Some(HistogramSpec {
                            policy: HistogramPolicy::Automatic,
                            scale: HistogramScale::Percent,
                        }),
                    ] {
                        if metric == Metric::Trajectory && histogram.is_some() {
                            continue;
                        }
                        let mut p = common::parameters(metric, basis);
                        p.histogram = histogram;
                        let mut complete = ChartEngine::new(p.clone(), 7).unwrap();
                        complete.advance(final_prefix).unwrap();
                        complete.finish(final_prefix).unwrap();
                        let mut incremental = ChartEngine::new(p, 7).unwrap();
                        let mut reads = 0;
                        let mut context_reads = 0;
                        let mut derived_evaluations = 0;
                        let mut smoothing_evaluations = 0;
                        let mut revisited_samples = 0;
                        for snapshot in &snapshots {
                            let delta = incremental
                                .advance(FixedPrefix::new(DatasetId(1), snapshot))
                                .unwrap();
                            reads += delta.work.new_record_reads;
                            context_reads += delta.work.context_record_reads;
                            derived_evaluations += delta.work.derived_evaluations;
                            smoothing_evaluations += delta.work.smoothing_evaluations;
                            revisited_samples += delta.work.revisited_samples;
                        }
                        incremental.finish(final_prefix).unwrap();
                        assert_eq!(reads, final_prefix.len());
                        assert_eq!(
                            complete.geometry().samples().collect::<Vec<_>>(),
                            incremental.geometry().samples().collect::<Vec<_>>(),
                            "{rate} {capacity} {basis:?} {metric:?} {histogram:?}",
                        );
                        let full_bins = complete.histogram().map(|h| h.bins());
                        let live_bins = incremental.histogram().map(|h| h.bins());
                        assert_eq!(full_bins, live_bins);
                        assert_eq!(incremental.state().revisable_from, final_prefix.len());
                        assert_eq!(incremental.state().published_end, final_prefix.len());
                        assert!(incremental.state().finished);
                        assert_eq!(
                            complete.state().data_version,
                            incremental.state().data_version
                        );
                        cases.push(serde_json::json!({
                            "rate_hz": rate,
                            "chunk_capacity": capacity,
                            "time_basis": format!("{basis:?}"),
                            "metric": format!("{metric:?}"),
                            "histogram": format!("{histogram:?}"),
                            "input_end": final_prefix.len(),
                            "processed_end": incremental.state().processed_end,
                            "published_end": incremental.state().published_end,
                            "revisable_from": incremental.state().revisable_from,
                            "finished": incremental.state().finished,
                            "samples": incremental.geometry().samples().count(),
                            "bins": live_bins.as_ref().map_or(0, Vec::len),
                            "new_record_reads": reads,
                            "context_record_reads": context_reads,
                            "derived_evaluations": derived_evaluations,
                            "smoothing_evaluations": smoothing_evaluations,
                            "revisited_samples": revisited_samples,
                            "matches_complete": true,
                        }));
                    }
                }
            }
        }
    }
    common::write_report(
        "mode-consistency.json",
        serde_json::json!({
            "schema": "clicksync-chart-mode-consistency-v1",
            "cases": cases,
        }),
    );
}

#[test]
fn each_published_window_prefix_matches_one_shot_not_only_the_final_result() {
    let snapshots = common::replay(4, 180, 1000, true);
    for metric in [
        Metric::CountTrend(clicksync_chart::Axis::X),
        Metric::IntervalTrend,
        Metric::Frequency,
    ] {
        let p = common::parameters(metric, TimeBasis::RawCapture);
        let mut live = ChartEngine::new(p.clone(), 11).unwrap();
        for snapshot in &snapshots {
            let prefix = FixedPrefix::new(DatasetId(1), snapshot);
            live.advance(prefix).unwrap();
            let mut full = ChartEngine::new(p.clone(), 11).unwrap();
            full.finish(prefix).unwrap();
            assert_eq!(
                live.geometry().samples().collect::<Vec<_>>(),
                full.geometry().samples().collect::<Vec<_>>(),
                "{metric:?} {}",
                prefix.len()
            );
        }
    }
}

#[test]
fn stable_geometry_is_shared_and_incremental_work_is_not_session_sized() {
    let snapshots = common::replay(256, 1200, 1000, false);
    let p = common::parameters(
        Metric::CountTrend(clicksync_chart::Axis::X),
        TimeBasis::RawCapture,
    );
    let mut engine = ChartEngine::new(p, 64).unwrap();
    engine
        .advance(FixedPrefix::new(DatasetId(1), &snapshots[1000]))
        .unwrap();
    let stable = Arc::clone(&engine.geometry().blocks()[0]);
    let delta = engine
        .advance(FixedPrefix::new(DatasetId(1), &snapshots[1001]))
        .unwrap();
    assert!(Arc::ptr_eq(&stable, &engine.geometry().blocks()[0]));
    assert_eq!(delta.work.new_record_reads, 1);
    assert!(delta.work.derived_evaluations <= 16, "{:?}", delta.work);
    assert!(delta.changed_blocks.start > 0);
    assert!(delta.state.revisable_from >= 990);
}

#[test]
fn duplicate_times_and_minimum_sample_expansion_revise_the_real_dependency_tail() {
    let mut session = common::session(3, "duplicates");
    session.begin_collecting(1000, true, true).unwrap();
    let mut p = common::parameters(
        Metric::CountTrend(clicksync_chart::Axis::X),
        TimeBasis::RawCapture,
    );
    p.options = clicksync_chart::AnalysisOptions::new(1.0, 9, 75.0, 7, 0.1, 60.0);
    let mut live = ChartEngine::new(p.clone(), 3).unwrap();
    for i in 0..20 {
        session.append(common::record(i, 1000)).unwrap();
        let snapshot = session.snapshot().unwrap();
        let prefix = FixedPrefix::new(DatasetId(1), &snapshot);
        live.advance(prefix).unwrap();
        assert_eq!(live.state().revisable_from, 0);
        let mut full = ChartEngine::new(p.clone(), 3).unwrap();
        full.finish(prefix).unwrap();
        assert_eq!(
            live.geometry().samples().collect::<Vec<_>>(),
            full.geometry().samples().collect::<Vec<_>>()
        );
    }
    session
        .append(common::record(20, 1000 + 1_000_000))
        .unwrap();
    let snapshot = session.snapshot().unwrap();
    let prefix = FixedPrefix::new(DatasetId(1), &snapshot);
    live.advance(prefix).unwrap();
    let mut full = ChartEngine::new(p, 3).unwrap();
    full.finish(prefix).unwrap();
    assert_eq!(
        live.geometry().samples().collect::<Vec<_>>(),
        full.geometry().samples().collect::<Vec<_>>()
    );
}

#[test]
fn velocity_prefixes_match_exact_one_shot_through_spikes_repeats_and_segments() {
    let mut checked_prefixes = 0;
    let mut maximum_smoothing_work = 0;
    for rate in [1000, 8000] {
        let mut session = common::session(13, "velocity-stress");
        session.begin_collecting(1000, true, true).unwrap();
        let mut snapshots = Vec::new();
        let step = common::FREQUENCY / rate;
        for i in 0..257 {
            let ticks = 1000 + (i - usize::from(i % 7 == 3)) as u64 * step;
            if [83, 191].contains(&i) {
                session.pause();
                session.begin_collecting(ticks, false, true).unwrap();
            }
            let mut record = common::record(i, ticks);
            if i % 31 == 17 {
                record.raw_delta_x = 1_000_000;
                record.raw_delta_y = -400_000;
            }
            session.append(record).unwrap();
            snapshots.push(session.snapshot().unwrap());
        }
        for basis in [TimeBasis::RawCapture, TimeBasis::Logical] {
            for metric in [
                Metric::Velocity(clicksync_chart::Axis::X),
                Metric::Velocity(clicksync_chart::Axis::Y),
                Metric::VelocityMagnitude,
                Metric::PathSpeed,
            ] {
                let parameters = common::parameters(metric, basis);
                let mut live = ChartEngine::new(parameters.clone(), 17).unwrap();
                for snapshot in &snapshots {
                    let prefix = FixedPrefix::new(DatasetId(2), snapshot);
                    let delta = live.advance(prefix).unwrap();
                    maximum_smoothing_work =
                        maximum_smoothing_work.max(delta.work.smoothing_evaluations);
                    let mut complete = ChartEngine::new(parameters.clone(), 17).unwrap();
                    complete.finish(prefix).unwrap();
                    assert_eq!(
                        live.geometry().samples().collect::<Vec<_>>(),
                        complete.geometry().samples().collect::<Vec<_>>(),
                        "{rate} {basis:?} {metric:?} {}",
                        prefix.len(),
                    );
                    checked_prefixes += 1;
                }
            }
        }
    }
    common::write_report(
        "velocity-prefixes.json",
        serde_json::json!({
            "schema": "clicksync-chart-velocity-prefixes-v1",
            "checked_prefixes": checked_prefixes,
            "maximum_smoothing_evaluations": maximum_smoothing_work,
            "matches_complete_bitwise": true,
        }),
    );
}
