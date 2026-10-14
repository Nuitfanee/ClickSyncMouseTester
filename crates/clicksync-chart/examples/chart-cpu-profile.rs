//! Production CPU stages and thread-local allocations, not a display/input benchmark.
use clicksync_chart::{
    Axis, ChartEngine, ChartParameters, DatasetId, FixedPrefix, Metric,
    gpu::{PlotKind, PreparedScene},
    query::hit::HitIndex,
};
use clicksync_core::{RawMouseMovementMode, RawMouseRecord, Session};
use std::{hint::black_box, time::Instant};

fn distribution(mut values: Vec<f64>) -> serde_json::Value {
    values.sort_by(f64::total_cmp);
    let rank = |p: usize| values[(values.len() * p).div_ceil(100).saturating_sub(1)];
    serde_json::json!({
        "n": values.len(), "p50": rank(50), "p95": rank(95), "p99": rank(99),
        "max": values.last(),
    })
}

fn append(session: &mut Session, from: usize, to: usize) {
    for i in from..to {
        session
            .append(RawMouseRecord::new(
                (8.0 * (i as f64 * 0.004).sin()).round() as i32,
                (5.0 * (i as f64 * 0.003).cos()).round() as i32,
                100 + i as u64 * 1250,
                i as u64 + 1,
                i as u64 + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
}

fn main() {
    let count = std::env::args()
        .nth(1)
        .map_or(65_536, |s| s.parse::<usize>().unwrap());
    assert!((1024..=1_000_000).contains(&count));
    let mut workloads = Vec::new();
    for metric in [
        Metric::Trajectory,
        Metric::Count(Axis::X),
        Metric::Velocity(Axis::X),
    ] {
        for allocations_enabled in [false, true] {
            let mut session = Session::default_with_metadata(Default::default()).unwrap();
            session.begin_collecting(100, true, false).unwrap();
            append(&mut session, 0, count);
            let mut parameters = ChartParameters::new(metric);
            parameters.cpi = Some(800.0);
            let mut engine = ChartEngine::new(parameters, 8192).unwrap();
            let first = session.snapshot().unwrap();
            let cold = Instant::now();
            engine
                .advance(FixedPrefix::new(DatasetId(1), &first))
                .unwrap();
            let kind = if matches!(metric, Metric::Velocity(_)) {
                PlotKind::Lines
            } else {
                PlotKind::Points
            };
            let mut scene = PreparedScene::from_engine(&engine, kind).unwrap();
            let mut hit = HitIndex::prepare(engine.geometry().blocks(), None);
            let cold_ms = cold.elapsed().as_secs_f64() * 1000.0;
            let mut stages: [Vec<f64>; 5] = std::array::from_fn(|_| Vec::with_capacity(128));
            let mut allocations: [Vec<f64>; 3] = std::array::from_fn(|_| Vec::with_capacity(128));
            let mut work = [0usize; 3];
            for frame in 0..128 {
                let from = count + frame * 64;
                let mut durations = [0.0; 5];
                let mut operation = || {
                    let start = Instant::now();
                    append(&mut session, from, from + 64);
                    durations[0] = start.elapsed().as_secs_f64() * 1000.0;
                    let start = Instant::now();
                    let prefix = session.snapshot().unwrap();
                    durations[1] = start.elapsed().as_secs_f64() * 1000.0;
                    let start = Instant::now();
                    let delta = engine
                        .advance(FixedPrefix::new(DatasetId(1), &prefix))
                        .unwrap();
                    work[0] += delta.work.new_record_reads;
                    work[1] += delta.work.smoothing_evaluations;
                    work[2] += delta.work.geometry_samples_written;
                    durations[2] = start.elapsed().as_secs_f64() * 1000.0;
                    let start = Instant::now();
                    scene =
                        PreparedScene::from_engine_reusing(&engine, kind, Some(&scene)).unwrap();
                    durations[3] = start.elapsed().as_secs_f64() * 1000.0;
                    let start = Instant::now();
                    hit = HitIndex::prepare(engine.geometry().blocks(), Some(&hit));
                    durations[4] = start.elapsed().as_secs_f64() * 1000.0;
                    black_box((&scene, &hit));
                };
                if allocations_enabled {
                    let info = allocation_counter::measure(operation);
                    allocations[0].push(info.count_total as f64);
                    allocations[1].push(info.bytes_total as f64);
                    allocations[2].push(info.bytes_max as f64);
                } else {
                    operation();
                }
                for (values, value) in stages.iter_mut().zip(durations) {
                    values.push(value);
                }
            }
            assert_eq!(work[0], 128 * 64, "incremental input reads changed");
            let stages: serde_json::Map<String, _> = [
                "session_append",
                "snapshot",
                "compute",
                "prepared_scene",
                "hit_index",
            ]
            .into_iter()
            .map(String::from)
            .zip(stages.map(distribution))
            .collect();
            workloads.push(serde_json::json!({
                "metric": format!("{metric:?}"), "initial_reports": count, "prefixes": 128,
                "reports_per_prefix": 64, "allocation_observer": allocations_enabled,
                "cold_ms": cold_ms,
                "stages_ms": stages,
                "allocations_per_prefix": allocations_enabled.then(|| distribution(allocations[0].clone())),
                "allocated_bytes_per_prefix": allocations_enabled.then(|| distribution(allocations[1].clone())),
                "transient_peak_bytes_per_prefix": allocations_enabled.then(|| distribution(allocations[2].clone())),
                "new_record_reads": work[0], "smoothing_evaluations": work[1],
                "geometry_samples_written": work[2],
                "engine_cache_bytes": engine.cached_value_bytes(), "hit_index_bytes": hit.cached_index_bytes(),
                "session_allocations": session.allocation_stats(),
            }));
        }
    }
    println!(
        "{}",
        serde_json::json!({
            "scope": "same-thread production Session/ChartEngine/PreparedScene/HitIndex, synthetic 8 kHz input; no capture thread, GPUI or GPU",
            "allocation_scope": "thread-local allocator calls and requested bytes, not process RSS; timing runs with observer off and on",
            "percentiles": "nearest rank", "workloads": workloads,
        })
    );
}
