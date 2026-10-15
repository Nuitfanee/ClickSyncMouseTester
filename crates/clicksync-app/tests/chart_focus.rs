use clicksync_app::{
    backend::{CapturePhase, SessionPrefix},
    chart::{ChartRequest, ChartResult, ChartWorker, ViewMode},
    chart_view::ChartView,
    product::{METRICS, Settings, performance_analysis::PlotAnalysis, plot_kind, secondary_metric},
};
use clicksync_chart::{Coordinate, Metric, gpu::PlotKind};
use clicksync_core::{
    RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SharedByteBudget,
};
use std::{
    sync::Arc,
    thread,
    time::{Duration, Instant},
};

fn input(count: usize, offset: i32) -> Arc<SessionPrefix> {
    input_with_gap(count, offset, 5000)
}

fn input_with_gap(count: usize, offset: i32, gap: u64) -> Arc<SessionPrefix> {
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
    for i in 0..count {
        let x = match i {
            100 => 1_000_000,
            999 => -1_000_000,
            _ => i as i32 % 7 - 3 + offset,
        };
        let ticks = 1000 + i as u64 + if i >= 100 { gap } else { 0 };
        session
            .append(RawMouseRecord::new(
                x,
                -x - 20,
                ticks,
                i as u64 + 1,
                i as u64 + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    session.stop();
    Arc::new(SessionPrefix {
        generation: 1,
        received_sequence: count as u64,
        received_records: count as u64,
        phase: CapturePhase::Stopped,
        complete: true,
        datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
    })
}

fn request(prefix: Arc<SessionPrefix>, metric: usize, id: u64) -> ChartRequest {
    ChartRequest {
        id,
        source_version: 1,
        prefix,
        parameters: Settings::default().parameters(metric, false, true, 1),
        dataset_cpi: vec![(1, Some(800.0)), (2, Some(1600.0))],
        secondary_metric: secondary_metric(metric, false),
        kind: plot_kind(metric, 1, false),
        view: ViewMode::FullSession,
        analysis: Some(PlotAnalysis::default()),
    }
}

fn answer(worker: &ChartWorker, request: ChartRequest) -> Arc<ChartResult> {
    worker.submit(request).unwrap();
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        if let Some(result) = worker.take_result() {
            return result.unwrap();
        }
        assert!(Instant::now() < deadline);
        thread::sleep(Duration::from_millis(1));
    }
}

#[test]
fn focus_is_default_for_all_time_metrics_and_keeps_raw_geometry_statistics_and_export() {
    let worker = ChartWorker::spawn();
    let prefix = input(2000, 0);
    for (index, &(metric, _, _)) in METRICS.iter().enumerate() {
        let result = answer(&worker, request(prefix.clone(), index, index as u64 + 1));
        let global = result.full_workspace_viewport(1).unwrap();
        let focus = result.workspace_viewport(1).unwrap();
        assert!(
            focus.origin.y.value().is_finite() && focus.height.is_finite() && focus.height > 0.0
        );
        assert_eq!(
            result.datasets[0].value_band.is_some(),
            metric != Metric::Trajectory,
            "{metric:?}"
        );
        let mut view = ChartView::new(result.clone()).unwrap();
        view.set_theme(false, false);
        assert_eq!(view.viewport(), focus, "{metric:?}");
        if matches!(index, 0 | 1 | 5 | 17) {
            assert!(
                focus.height < global.height * 0.01,
                "{metric:?}: {focus:?} / {global:?}"
            );
        }
        if metric != Metric::Trajectory {
            assert_eq!(focus.origin.x.value(), 0.0, "{metric:?}");
            assert_eq!(focus.width, 1000.0, "{metric:?}");
        }
        if matches!(index, 0 | 1 | 17) {
            assert!(
                result.datasets[0]
                    .geometry
                    .iter()
                    .flat_map(|b| &b.samples)
                    .any(|s| s.point.y.value().abs() >= 1_000_000.0)
            );
        }
        assert_eq!(result.export_events(1).unwrap().count(), 2000);
        view.fit_trajectory();
        assert_eq!(view.viewport(), global);
        let statistics = result.datasets[0].statistics.as_ref().unwrap();
        assert!(statistics.sample_count > 0);
    }
}

#[test]
fn focus_covers_each_device_and_both_components_and_refits_selected_ranges() {
    let worker = ChartWorker::spawn();
    let a = input(2000, 0);
    let b = input(2000, 100);
    let mut prefix = (*a).clone();
    prefix.datasets.push((2, b.datasets[0].1.clone()));
    let mut target = request(Arc::new(prefix), 17, 1);
    let result = answer(&worker, target.clone());
    let focus = result.workspace_viewport(1).unwrap();
    for value in [-123.0, -23.0, -3.0, 3.0, 103.0] {
        let offset = Coordinate::Scalar(value).relative_to(focus.origin.y);
        assert!(
            (0.0..=focus.height).contains(&offset),
            "{value} / {focus:?}"
        );
    }
    assert!(focus.height < 300.0);
    target.id += 1;
    target.analysis.as_mut().unwrap().range = Some((6000.0, 6500.0));
    let selected = answer(&worker, target);
    assert!(selected.workspace_viewport(1).unwrap().height < 300.0);
    assert_eq!(
        selected.workspace_viewport(1).unwrap().origin.x,
        selected.full_workspace_viewport(1).unwrap().origin.x
    );
    assert_eq!(
        selected.workspace_viewport(1).unwrap().width,
        selected.full_workspace_viewport(1).unwrap().width
    );
    assert!(selected.datasets.iter().all(|d| {
        d.geometry
            .iter()
            .flat_map(|b| &b.samples)
            .all(|s| (6000.0..=6500.0).contains(&s.point.x.value()))
    }));
    assert_eq!(selected.export_events(1).unwrap().count(), 2000);
}

#[test]
fn focus_falls_back_for_small_samples_and_does_not_crop_histogram_or_trajectory() {
    let worker = ChartWorker::spawn();
    for count in [0, 1, 23] {
        let mut target = request(input(count, 0), 0, count as u64 + 1);
        target.source_version = count as u64 + 1;
        let result = answer(&worker, target.clone());
        assert!(result.datasets[0].value_band.is_none());
        assert_eq!(
            result.workspace_viewport(1).unwrap(),
            result.full_workspace_viewport(1).unwrap()
        );
    }
    let mut target = request(input(2000, 0), 0, 25);
    target.source_version = 25;
    target.kind = PlotKind::Histogram;
    target.parameters = Settings::default().parameters(0, true, true, 1);
    let result = answer(&worker, target);
    assert!(result.datasets[0].value_band.is_none());
    assert_eq!(
        result.workspace_viewport(1).unwrap(),
        result.full_workspace_viewport(1).unwrap()
    );
}

#[test]
fn live_focus_uses_the_follow_window_while_detail_uses_the_whole_selected_input() {
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
    for i in 0..10_000 {
        session
            .append(RawMouseRecord::new(
                if i < 9000 { 5000 } else { 2 },
                0,
                1000 + i,
                i + 1,
                i + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    let prefix = Arc::new(SessionPrefix {
        generation: 1,
        received_sequence: 10_000,
        received_records: 10_000,
        phase: CapturePhase::Collecting,
        complete: true,
        datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
    });
    let worker = ChartWorker::spawn();
    let mut target = request(prefix, 0, 1);
    target.view = ViewMode::Follow { window_ms: 100.0 };
    let live = answer(&worker, target.clone());
    let focus = live.workspace_viewport(1).unwrap();
    assert!(focus.origin.x.value() > 9000.0);
    assert_eq!(focus.width, live.viewport(1).unwrap().width);
    assert!(focus.height < 10.0, "{focus:?}");
    assert!((0.0..=focus.height).contains(&Coordinate::Integer(2).relative_to(focus.origin.y)));
    target.id += 1;
    target.view = ViewMode::FullSession;
    let detail = answer(&worker, target);
    let full = detail.workspace_viewport(1).unwrap();
    assert_eq!(full.origin.x.value(), 0.0);
    assert_eq!(full.width, 1000.0);
    for value in [2, 5000] {
        assert!(
            (0.0..=full.height).contains(&Coordinate::Integer(value).relative_to(full.origin.y))
        );
    }
    for (a, b) in live.datasets[0]
        .geometry
        .iter()
        .zip(&detail.datasets[0].geometry)
    {
        assert!(Arc::ptr_eq(a, b));
    }
}

#[test]
fn focus_caps_actual_time_extent_but_preserves_short_follow_fixed_and_filtered_views() {
    let worker = ChartWorker::spawn();
    for (index, count) in [0, 1, 999, 1000, 1001, 1002, 5000].into_iter().enumerate() {
        let mut target = request(input_with_gap(count, 0, 0), 0, index as u64 * 2 + 1);
        target.source_version = count as u64 + 1;
        let result = answer(&worker, target);
        let global = result.full_workspace_viewport(1).unwrap();
        let focus = result.workspace_viewport(1).unwrap();
        if count > 1001 {
            assert_eq!(focus.origin.x.value(), 0.0);
            assert_eq!(focus.width, 1000.0);
        } else {
            assert_eq!(focus.origin.x, global.origin.x);
            assert_eq!(focus.width, global.width);
        }
        let mut fixed = global;
        fixed.origin.x = Coordinate::Scalar(600.0);
        fixed.width = 57.0;
        let mut projected = result.request.clone();
        projected.id += 1;
        projected.view = ViewMode::Fixed(fixed);
        let projected = answer(&worker, projected);
        assert_eq!(projected.workspace_viewport(1).unwrap(), fixed);
    }
    let mut target = request(input_with_gap(5000, 0, 0), 17, 10001);
    target.source_version = 10001;
    target.analysis.as_mut().unwrap().range = Some((2000.0, 4500.0));
    let mut result = answer(&worker, target);
    let focus = result.workspace_viewport(1).unwrap();
    assert_eq!(focus.origin.x.value(), 2000.0);
    assert_eq!(focus.width, 1000.0);
    assert_eq!(
        result.request.analysis.as_ref().unwrap().range,
        Some((2000.0, 4500.0))
    );
    // The time cap does not depend on whether enough values exist for robust Y fitting.
    let fallback = Arc::get_mut(&mut result).unwrap();
    for dataset in &mut fallback.datasets {
        dataset.value_band = None;
    }
    let fallback = fallback.workspace_viewport(1).unwrap();
    assert_eq!(fallback.origin.x, focus.origin.x);
    assert_eq!(fallback.width, focus.width);
}

#[test]
fn manual_view_survives_style_changes_but_not_new_source_or_analysis_range() {
    let worker = ChartWorker::spawn();
    let mut target = request(input(2000, 0), 0, 1);
    let initial = answer(&worker, target.clone());
    let mut view = ChartView::new(initial).unwrap();
    view.set_theme(false, false);
    view.fit_trajectory();
    let manual = view.viewport();
    target.id += 1;
    target.analysis.as_mut().unwrap().options.trend = true;
    view.set_result(answer(&worker, target.clone())).unwrap();
    assert_eq!(view.viewport(), manual);
    target.id += 1;
    target.analysis.as_mut().unwrap().range = Some((6000.0, 6500.0));
    view.set_result(answer(&worker, target.clone())).unwrap();
    assert!(view.viewport().height < manual.height * 0.01);
    view.fit_trajectory();
    target.id += 1;
    target.source_version += 1;
    target.prefix = input(2000, 100);
    let replacement = answer(&worker, target);
    let expected = replacement.workspace_viewport(1).unwrap();
    view.set_result(replacement).unwrap();
    assert_eq!(view.viewport(), expected);
}
