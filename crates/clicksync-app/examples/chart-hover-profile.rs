//! CPU hover path only; this does not measure GPUI frame rate or desktop input latency.
use clicksync_app::{
    backend::{CapturePhase, SessionPrefix},
    chart::{ChartRequest, ChartResult, ChartWorker, ViewMode},
    product::performance_analysis::{PlotAnalysis, time_index, time_ms},
};
use clicksync_chart::{Axis, ChartParameters, Metric, Point, gpu::PlotKind};
use clicksync_core::{
    RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SharedByteBudget,
};
use std::{
    hint::black_box,
    sync::Arc,
    thread,
    time::{Duration, Instant},
};

fn legacy_markers(
    result: &ChartResult,
    charts: &[Arc<ChartResult>],
    index: usize,
) -> Vec<Vec<Point>> {
    let time = time_ms(result.snapshot(1).unwrap(), index, true).unwrap();
    let links: Vec<_> = result
        .request
        .prefix
        .datasets
        .iter()
        .map(|(key, snapshot)| {
            let mapped = if *key == 1 {
                Some(index)
            } else {
                let first = time_index(snapshot, time, true);
                if first.is_some_and(|first| {
                    ((first + 1)..snapshot.event_count()).any(|index| {
                        (time_ms(snapshot, index, true).unwrap() - time).abs() <= 0.000001
                    })
                }) {
                    None
                } else {
                    first
                }
            };
            (*key, mapped)
        })
        .collect();
    charts
        .iter()
        .flat_map(|chart| chart.datasets.iter())
        .map(|dataset| {
            let index = links
                .iter()
                .find(|(key, _)| *key == dataset.device_key)
                .unwrap()
                .1;
            dataset
                .geometry
                .iter()
                .filter_map(|block| {
                    block
                        .samples
                        .iter()
                        .find(|sample| Some(sample.source_index) == index)
                        .map(|s| s.point)
                })
                .take(2)
                .collect()
        })
        .collect()
}

fn timings(iterations: usize, mut query: impl FnMut(usize)) -> serde_json::Value {
    let mut times = Vec::with_capacity(iterations);
    for i in 0..iterations {
        let start = Instant::now();
        query(i);
        times.push(start.elapsed().as_secs_f64() * 1_000_000.0);
    }
    times.sort_by(f64::total_cmp);
    serde_json::json!({
        "iterations": iterations,
        "median_us": times[times.len() / 2],
        "p95_us": times[(times.len() - 1) * 95 / 100],
        "p99_us": times[(times.len() * 99).div_ceil(100) - 1],
        "max_us": times[times.len() - 1],
    })
}

fn main() -> Result<(), String> {
    let count = std::env::args()
        .nth(1)
        .map_or(Ok(65_536), |s| s.parse::<usize>())
        .map_err(|e| e.to_string())?;
    if !(16..=262_144).contains(&count) {
        return Err("sample count must be in 16..=262144".into());
    }
    let config = SessionConfig {
        timestamp_frequency: 8_000,
        max_session_events: count + 1,
        warning_event_threshold: count,
        ..Default::default()
    };
    let mut session = Session::new(
        config.clone(),
        SharedByteBudget::new(config.managed_memory_budget_bytes),
        Default::default(),
    )
    .map_err(|e| e.to_string())?;
    session
        .begin_collecting(8_000, true, false)
        .map_err(|e| e.to_string())?;
    for i in 0..count {
        session
            .append(RawMouseRecord::new(
                (i % 257) as i32 - 128,
                (i % 127) as i32 - 63,
                8_000 + i as u64,
                i as u64 + 1,
                i as u64 + 1,
                RawMouseMovementMode::Relative,
            ))
            .map_err(|e| e.to_string())?;
    }
    session.stop();
    let snapshot = Arc::new(session.snapshot().map_err(|e| e.to_string())?);
    let prefix = Arc::new(SessionPrefix {
        generation: 1,
        received_sequence: count as u64 * 2,
        received_records: count as u64 * 2,
        phase: CapturePhase::Stopped,
        complete: true,
        datasets: vec![(1, snapshot.clone()), (2, snapshot)],
    });
    // Standalone profile owns its budget; production limits are unchanged.
    let mut worker = ChartWorker::spawn_with_budget(SharedByteBudget::new(4 * 1024 * 1024 * 1024));
    let mut charts = Vec::new();
    for (slot, metric) in [
        Metric::Count(Axis::X),
        Metric::Count(Axis::X),
        Metric::Count(Axis::Y),
        Metric::Interval,
    ]
    .into_iter()
    .enumerate()
    {
        worker.submit(ChartRequest {
            id: slot as u64 + 1,
            source_version: 1,
            prefix: prefix.clone(),
            parameters: ChartParameters::new(metric),
            dataset_cpi: Vec::new(),
            secondary_metric: (slot == 0).then_some(Metric::Count(Axis::Y)),
            kind: PlotKind::Lines,
            view: ViewMode::FullSession,
            analysis: Some(PlotAnalysis::default()),
        })?;
        let start = Instant::now();
        loop {
            if let Some(result) = worker.take_result() {
                charts.push(result?);
                break;
            }
            if start.elapsed() > Duration::from_secs(60) {
                return Err("chart worker deadline exceeded".into());
            }
            thread::sleep(Duration::from_millis(1));
        }
    }
    let result = &charts[0];
    let dataset = &result.datasets[0];
    let viewport = result.viewport(1)?;
    let hit = |i: usize| {
        dataset
            .hit_index
            .nearest(
                viewport.origin,
                [viewport.width * (i as f64 / 4095.0), viewport.height * 0.55],
                [1200.0 / viewport.width, 700.0 / viewport.height],
            )
            .unwrap()
            .source_index
    };
    // Compare the indexed result with the former full-scan calculation outside the timing loop.
    for i in [0, 1, count / 2, count - 1] {
        let linked = result.linked(1, i, &charts, None)?;
        assert_eq!(linked.links, [(1, Some(i)), (2, Some(i))]);
        assert_eq!(
            linked
                .markers
                .iter()
                .map(|(_, _, p)| p.clone())
                .collect::<Vec<_>>(),
            legacy_markers(result, &charts, i)
        );
    }
    let indexed = timings(4096, |i| {
        let index = hit(i);
        black_box(result.linked(1, index, &charts, None).unwrap());
    });
    let previous_scan = timings(64, |i| {
        let index = hit(i * 65);
        black_box(legacy_markers(result, &charts, index));
    });
    println!(
        "{}",
        serde_json::json!({
            "scope": "CPU nearest-hit + exact cross-dataset/chart linkage; not desktop FPS",
            "reports_per_dataset": count,
            "datasets": 2,
            "presented_charts": charts.len(),
            "indexed": indexed,
            "previous_full_scan": previous_scan,
        })
    );
    worker.shutdown();
    Ok(())
}
