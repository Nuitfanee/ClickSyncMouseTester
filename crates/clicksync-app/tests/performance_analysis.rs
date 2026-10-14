use clicksync_app::{
    backend::{CapturePhase, SessionPrefix},
    chart::{ChartRequest, ChartResult, ChartWorker, ViewMode},
    product::{
        Settings,
        performance_analysis::{PlotAnalysis, PlotOptions},
    },
    product_files::{self, FileRequest, FileResult},
};
use clicksync_chart::{Axis, Metric};
use clicksync_core::{
    RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SharedByteBudget,
};
use std::{
    sync::Arc,
    thread,
    time::{Duration, Instant},
};

fn input() -> Arc<SessionPrefix> {
    let config = SessionConfig {
        timestamp_frequency: 1000,
        ..Default::default()
    };
    let mut session = Session::new(
        config.clone(),
        SharedByteBudget::new(config.managed_memory_budget_bytes),
        Default::default(),
    )
    .unwrap();
    session.begin_collecting(1000, true, false).unwrap();
    for (i, (ticks, dx)) in [
        (1000, 1),
        (1001, 2),
        (1050, 30),
        (1002, 4),
        (1002, 5),
        (1003, 6),
    ]
    .into_iter()
    .enumerate()
    {
        session
            .append(RawMouseRecord::new(
                dx,
                -dx,
                ticks,
                i as u64 + 1,
                i as u64 + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    session.pause();
    session.begin_collecting(2000, false, false).unwrap();
    session
        .append(RawMouseRecord::new(
            10,
            5,
            2000,
            7,
            7,
            RawMouseMovementMode::Relative,
        ))
        .unwrap();
    session.stop();
    let snapshot = Arc::new(session.snapshot().unwrap());
    Arc::new(SessionPrefix {
        generation: 1,
        received_sequence: 21,
        received_records: 21,
        phase: CapturePhase::Stopped,
        complete: true,
        datasets: vec![(1, snapshot.clone()), (2, snapshot.clone()), (3, snapshot)],
    })
}
fn request(input: Arc<SessionPrefix>, metric: usize, histogram: bool, id: u64) -> ChartRequest {
    ChartRequest {
        id,
        source_version: 4,
        parameters: Settings::default().parameters(metric, histogram, false, 1),
        dataset_cpi: vec![(1, Some(800.0)), (2, Some(1600.0)), (3, Some(3200.0))],
        secondary_metric: clicksync_app::product::secondary_metric(metric, histogram),
        kind: clicksync_app::product::plot_kind(metric, 0, histogram),
        prefix: input,
        view: ViewMode::FullSession,
        analysis: Some(PlotAnalysis {
            range: Some((1.0, 3.0)),
            options: PlotOptions {
                lines: true,
                stems: true,
                trend: true,
                gaps: true,
                gap_dataset: None,
            },
        }),
    }
}
fn answer(worker: &ChartWorker) -> Arc<ChartResult> {
    let start = Instant::now();
    loop {
        if let Some(result) = worker.take_result() {
            return result.unwrap();
        }
        assert!(start.elapsed() < Duration::from_secs(10));
        thread::sleep(Duration::from_millis(1));
    }
}

#[test]
fn range_filters_actual_times_keeps_complete_json_and_never_connects_an_excluded_report() {
    let worker = ChartWorker::spawn();
    worker.submit(request(input(), 0, false, 1)).unwrap();
    let result = answer(&worker);
    let indices: Vec<_> = result.datasets[0]
        .geometry
        .iter()
        .flat_map(|block| block.samples.iter())
        .map(|sample| sample.source_index)
        .collect();
    assert_eq!(indices, [1, 3, 4, 5]);
    let lines = &result.datasets[0].scene.blocks[0];
    assert!(
        !lines
            .instances
            .iter()
            .any(|instance| instance.a[0] == 1.0 && instance.b[0] == 2.0),
        "a selected range must not connect across an excluded raw-time report"
    );
    let FileResult::Query(summary) = product_files::execute(FileRequest::Query {
        result: result.clone(),
        key: 1,
        range: 0..7,
    })
    .unwrap() else {
        panic!()
    };
    assert_eq!(summary.record_count, 4);
    assert_eq!(summary.sum_x, 17);
    assert_eq!(summary.distribution.unwrap().sample_count, 4);
    assert_eq!(result.export_events(1).unwrap().count(), 7);
    assert!(
        result.datasets[0]
            .scene
            .blocks
            .iter()
            .any(|block| block.key.kind & (1 << 17) != 0)
    );
    let gaps = clicksync_app::product::performance_analysis::report_gaps(
        result.snapshot(1).unwrap(),
        clicksync_chart::TimeBasis::RawCapture,
        None,
    );
    assert_eq!(gaps, [[1.0, 50.0]]);
}

#[test]
fn histogram_links_an_actual_report_and_duplicate_cross_group_times_are_explicitly_unmapped() {
    let worker = ChartWorker::spawn();
    worker.submit(request(input(), 0, true, 2)).unwrap();
    let result = answer(&worker);
    let bin = result.datasets[0]
        .histogram
        .iter()
        .find(|bin| bin.count > 0)
        .unwrap();
    let link = result
        .linked(1, 0, std::slice::from_ref(&result), Some(*bin))
        .unwrap();
    assert!([1, 3, 4, 5].contains(&link.index));
    assert!(link.matches > 0);
    let duplicate = result.linked(1, 3, &[], None).unwrap();
    assert_eq!(duplicate.time_ms, 2.0);
    assert_eq!(duplicate.links, [(1, Some(3)), (2, None), (3, None)]);
    assert_eq!(duplicate.ambiguous, 2);
    for bin in &result.datasets[0].histogram {
        let last = result.datasets[0].histogram.last().unwrap().maximum == bin.maximum;
        let expected: Vec<_> = result.datasets[0]
            .geometry
            .iter()
            .flat_map(|b| &b.samples)
            .filter(|sample| {
                let value = sample.point.y.value();
                value >= bin.minimum && (value < bin.maximum || (last && value <= bin.maximum))
            })
            .collect();
        let actual = result.linked(1, 0, std::slice::from_ref(&result), Some(*bin));
        if let Some(first) = expected.first() {
            let actual = actual.unwrap();
            assert_eq!(actual.index, first.source_index);
            assert_eq!(actual.matches, expected.len());
            let (_, _, markers) = actual.markers.iter().find(|(_, key, _)| *key == 1).unwrap();
            assert_eq!(markers.len(), 1);
            assert_eq!(markers[0].x.value(), (bin.minimum + bin.maximum) / 2.0);
            assert_eq!(markers[0].y.value(), bin.value);
        } else {
            assert!(actual.is_err());
        }
    }
}

#[test]
fn indexed_markers_preserve_both_axes_and_raw_time_range_holes() {
    let worker = ChartWorker::spawn();
    let mut target = request(input(), 0, false, 20);
    target.secondary_metric = Some(Metric::Count(Axis::Y));
    worker.submit(target).unwrap();
    let result = answer(&worker);
    for index in 0..7 {
        let linked = result
            .linked(1, index, std::slice::from_ref(&result), None)
            .unwrap();
        for (_, key, points) in &linked.markers {
            let dataset = result
                .datasets
                .iter()
                .find(|d| d.device_key == *key)
                .unwrap();
            let mapped = linked.links.iter().find(|(k, _)| k == key).unwrap().1;
            let expected: Vec<_> = dataset
                .geometry
                .iter()
                .filter_map(|block| {
                    block
                        .samples
                        .iter()
                        .find(|s| Some(s.source_index) == mapped)
                        .map(|s| s.point)
                })
                .take(2)
                .collect();
            assert_eq!(*points, expected);
        }
    }
}

#[test]
fn histogram_links_include_reports_clamped_into_fixed_end_bins() {
    let worker = ChartWorker::spawn();
    let mut target = request(input(), 0, true, 21);
    target.parameters.histogram = Some(clicksync_chart::HistogramSpec {
        policy: clicksync_chart::HistogramPolicy::Fixed(clicksync_chart::HistogramLayout {
            minimum: -0.5,
            maximum: 1.5,
            width: 1.0,
            bins: 2,
        }),
        scale: clicksync_chart::HistogramScale::Count,
    });
    worker.submit(target).unwrap();
    let result = answer(&worker);
    let bin = *result.datasets[0].histogram.last().unwrap();
    assert_eq!(bin.count, 4);
    let linked = result
        .linked(1, 0, std::slice::from_ref(&result), Some(bin))
        .unwrap();
    assert_eq!(linked.matches, bin.count);
    let (_, _, markers) = linked.markers.iter().find(|(_, key, _)| *key == 1).unwrap();
    assert_eq!(markers.len(), 1);
    assert_eq!(markers[0].x.value(), 1.0);
    assert_eq!(markers[0].y.value(), 4.0);
}

#[test]
fn all_three_cpi_values_are_independent_and_managed_leases_survive_fixed_output_then_release() {
    let budget = SharedByteBudget::new(64 * 1024 * 1024);
    let mut worker = ChartWorker::spawn_with_budget(budget.clone());
    worker.submit(request(input(), 13, false, 3)).unwrap();
    let result = answer(&worker);
    assert_eq!(result.request.parameters_for(1).cpi, Some(800.0));
    assert_eq!(result.request.parameters_for(3).cpi, Some(3200.0));
    assert!(budget.reserved_bytes() > 0);
    worker.shutdown();
    assert!(
        budget.reserved_bytes() > 0,
        "fixed result must retain its CPU budget after visible cache retirement"
    );
    drop(result);
    assert_eq!(budget.reserved_bytes(), 0);
}

#[test]
fn analysis_budget_failure_is_reported_without_partial_publication_or_increasing_the_limit() {
    let budget = SharedByteBudget::new(1024);
    let mut worker = ChartWorker::spawn_with_budget(budget.clone());
    worker.submit(request(input(), 13, false, 4)).unwrap();
    let started = Instant::now();
    loop {
        if let Some(result) = worker.take_result() {
            assert!(result.err().unwrap().contains("budget exceeded"));
            break;
        }
        assert!(started.elapsed() < Duration::from_secs(5));
        thread::sleep(Duration::from_millis(1));
    }
    worker.shutdown();
    assert_eq!(budget.reserved_bytes(), 0);
    assert_eq!(budget.limit_bytes(), 1024);
}

#[test]
fn full_json_uses_the_selected_group_and_rejects_unordered_ticks() {
    let mut worker = ChartWorker::spawn();
    let mut target = request(input(), 0, false, 5);
    target.secondary_metric = Some(Metric::Count(Axis::Y));
    worker.submit(target).unwrap();
    let result = answer(&worker);
    let directory = std::env::temp_dir().join(format!("clicksync-analysis-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    let json = directory.join("group-c.json");
    let failure = product_files::execute(FileRequest::Export {
        path: json.clone(),
        snapshot: result.request.prefix.datasets[2].1.clone(),
        cpi: Some(3200.0),
    })
    .err()
    .unwrap();
    assert!(failure.contains("raw capture ticks out of order"));
    assert!(
        !json.exists(),
        "a failed export must not leave a partial destination"
    );
    let mut ordered = Session::default_with_metadata(Default::default()).unwrap();
    ordered.begin_collecting(100, true, false).unwrap();
    for index in 0..7 {
        ordered
            .append(RawMouseRecord::new(
                1,
                2,
                100 + index,
                index + 1,
                index + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    ordered.stop();
    product_files::execute(FileRequest::Export {
        path: json.clone(),
        snapshot: Arc::new(ordered.snapshot().unwrap()),
        cpi: Some(3200.0),
    })
    .unwrap();
    let FileResult::ImportedAnalysis { group, operation } =
        product_files::execute(FileRequest::ImportAnalysis {
            path: json.clone(),
            operation: 1,
            budget: SharedByteBudget::new(SessionConfig::default().managed_memory_budget_bytes),
            existing: Vec::new(),
        })
        .unwrap()
    else {
        panic!()
    };
    assert_eq!(operation, 1);
    assert_eq!(group.snapshot.event_count(), 7);
    assert_eq!(group.cpi, Some(3200.0));
    worker.shutdown();
    std::fs::remove_file(json).unwrap();
    std::fs::remove_dir(directory).unwrap();
}
