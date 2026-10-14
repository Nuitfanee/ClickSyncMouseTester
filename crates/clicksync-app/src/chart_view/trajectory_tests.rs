use super::*;
use crate::{
    backend::{CapturePhase, SessionPrefix},
    chart::{ChartRequest, ChartWorker, ViewMode},
};
use clicksync_chart::{ChartParameters, Coordinate, Metric, gpu::PlotKind};
use clicksync_core::{RawMouseMovementMode, RawMouseRecord, Session};
use std::time::{Duration, Instant};

fn viewport(x: f64, y: f64, width: f64, height: f64) -> Viewport {
    Viewport {
        origin: clicksync_chart::Point {
            x: Coordinate::Scalar(x),
            y: Coordinate::Scalar(y),
        },
        width,
        height,
    }
}

#[test]
fn native_shift_and_horizontal_wheels_reach_zoom_without_losing_axis_modifiers() {
    let mut view = ChartView::new(result(1)).unwrap();
    view.bounds = Bounds::new(point(px(50.0), px(20.0)), size(px(200.0), px(100.0)));
    let initial = viewport(-100.0, -50.0, 200.0, 100.0);
    let position = view.bounds.center();
    for delta in [
        gpui::ScrollDelta::Lines(point(3.0, 0.0)),
        gpui::ScrollDelta::Pixels(point(px(48.0), px(0.0))),
        gpui::ScrollDelta::Lines(point(0.0, 3.0)),
    ] {
        for control in [false, true] {
            view.set_viewport(initial, true);
            let zooms = view.report.zoom_events;
            view.scroll_zoom(&gpui::ScrollWheelEvent {
                position,
                delta,
                modifiers: gpui::Modifiers {
                    shift: !control,
                    control,
                    ..Default::default()
                },
                touch_phase: gpui::TouchPhase::Moved,
            });
            assert_eq!(view.report.zoom_events, zooms + 1);
            let zoomed = view.viewport();
            let factor = (-48.0_f64 * 0.002).exp();
            assert_eq!(
                zoomed.width,
                initial.width * if control { 1.0 } else { factor }
            );
            assert_eq!(
                zoomed.height,
                initial.height * if control { factor } else { 1.0 }
            );
        }
    }
    let before = view.viewport();
    let zooms = view.report.zoom_events;
    view.scroll_zoom(&gpui::ScrollWheelEvent {
        position,
        delta: gpui::ScrollDelta::Lines(point(0.0, 0.0)),
        modifiers: Default::default(),
        touch_phase: gpui::TouchPhase::Moved,
    });
    assert_eq!(view.viewport(), before);
    assert_eq!(view.report.zoom_events, zooms);
}

#[test]
fn incoming_motion_keeps_the_view_stable_until_an_equal_scale_expansion_is_needed() {
    let current = viewport(-100.0, -50.0, 200.0, 100.0);
    let inside = viewport(-80.0, -40.0, 160.0, 80.0);
    assert_eq!(stable_trajectory(current, inside), current);
    let outside = viewport(-250.0, -40.0, 500.0, 80.0);
    let expanded = stable_trajectory(current, outside);
    assert_eq!(expanded.width, 500.0);
    assert_eq!(expanded.width / expanded.height, 2.0);
    assert!(expanded.origin.x.value() <= -250.0);
    assert!(expanded.origin.x.value() + expanded.width >= 250.0);
    assert_eq!(stable_trajectory(expanded, inside), expanded);
    let edge = viewport(-100.0, -50.0, 202.0, 100.0);
    let expanded = stable_trajectory(current, edge);
    assert_eq!(expanded.width, 202.0);
    assert_eq!(expanded.height, 101.0);
    for aspect in [0.5, 1.0, 2.5] {
        let equal = equal_scale(expanded, aspect);
        assert!((equal.width / equal.height - aspect).abs() < 1e-10);
        assert_eq!(equal_scale(equal, aspect), equal);
    }
}

fn result(source_version: u64) -> Arc<ChartResult> {
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    session.begin_collecting(100, true, false).unwrap();
    for index in 0..4_u64 {
        if index == 2 {
            session.pause();
            session.begin_collecting(10_000, false, false).unwrap();
        }
        session
            .append(RawMouseRecord::new(
                if index < 2 { -3 } else { 3 },
                if index < 2 { -2 } else { 2 },
                if index < 2 {
                    100 + index
                } else {
                    10_000 + index
                },
                index + 1,
                index + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    session.stop();
    let prefix = Arc::new(SessionPrefix {
        generation: 1,
        received_sequence: 4,
        received_records: 4,
        phase: CapturePhase::Stopped,
        complete: true,
        datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
    });
    let worker = ChartWorker::spawn();
    worker
        .submit(ChartRequest {
            id: source_version,
            source_version,
            prefix,
            parameters: ChartParameters::new(Metric::Trajectory),
            dataset_cpi: vec![(1, Some(800.0))],
            secondary_metric: None,
            kind: PlotKind::Trajectory,
            view: ViewMode::FullSession,
            analysis: None,
        })
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        if let Some(answer) = worker.take_result() {
            return answer.unwrap();
        }
        assert!(
            Instant::now() < deadline,
            "trajectory calculation timed out"
        );
        std::thread::sleep(Duration::from_millis(1));
    }
}

#[test]
fn source_replacement_releases_the_fixed_view_and_pause_does_not_connect_segments() {
    let first = result(1);
    let dataset = &first.datasets[0];
    assert_eq!(dataset.scene.instance_count(), 4);
    assert!(
        dataset
            .scene
            .blocks
            .iter()
            .flat_map(|block| block.instances.iter())
            .all(|instance| instance.kind == 0)
    );
    let samples: Vec<_> = dataset.geometry.iter().flat_map(|b| &b.samples).collect();
    assert_eq!(samples.len(), 4);
    assert_ne!(samples[1].segment_id, samples[2].segment_id);
    let mut view = ChartView::new(first).unwrap();
    view.set_theme(true, false);
    view.enable_trajectory();
    let frozen = viewport(-1000.0, -1000.0, 2000.0, 2000.0);
    view.set_viewport(frozen, true);
    view.set_result(result(1)).unwrap();
    assert_eq!(view.viewport(), frozen);
    let replacement = result(2);
    let expected = replacement.workspace_viewport(1).unwrap();
    view.set_result(replacement).unwrap();
    assert!(!view.fixed);
    assert_eq!(view.viewport(), expected);
}

#[test]
fn hovering_never_starts_a_drag_and_slow_drags_use_the_press_viewport() {
    let mut view = ChartView::new(result(1)).unwrap();
    view.enable_trajectory();
    view.bounds = Bounds {
        origin: point(px(50.0), px(20.0)),
        size: size(px(200.0), px(100.0)),
    };
    let mut initial = viewport(-100.0, -50.0, 200.0, 100.0);
    initial.origin.x = Coordinate::Integer(-100);
    view.set_viewport(initial, true);
    view.move_pointer(point(px(100.0), px(50.0)));
    view.move_pointer(point(px(110.0), px(60.0)));
    assert_eq!(view.viewport(), initial);
    assert!(!view.is_dragging());
    view.drag_start(point(px(100.0), px(50.0)));
    for step in 1..=20 {
        view.move_pointer(point(px(100.0 + step as f32 * 0.25), px(50.0)));
    }
    assert_eq!(view.viewport().origin.x.value(), -105.0);
    view.drag_end();
    view.move_pointer(point(px(130.0), px(60.0)));
    assert_eq!(view.viewport().origin.x.value(), -105.0);
}

#[test]
fn returning_to_the_press_restores_the_aspect_base_and_noop_drags_do_not_invalidate() {
    let mut view = ChartView::new(result(1)).unwrap();
    view.enable_trajectory();
    view.bounds = Bounds {
        origin: point(px(50.0), px(20.0)),
        size: size(px(200.0), px(100.0)),
    };
    view.set_viewport(viewport(-50.0, -50.0, 100.0, 100.0), true);
    view.fit_trajectory_aspect(2.0);
    let initial = view.viewport();
    let revision = view.viewport_revision;
    let press = point(px(100.0), px(50.0));
    view.drag_start(press);
    assert!(!view.move_pointer(press));
    assert_eq!(view.viewport_revision, revision);
    assert!(view.move_pointer(point(px(125.0), px(65.0))));
    view.fit_trajectory_aspect(2.0);
    assert!(view.move_pointer(press));
    view.fit_trajectory_aspect(2.0);
    assert_eq!(view.viewport(), initial);
    let revision = view.viewport_revision;
    assert!(!view.move_pointer(press));
    assert_eq!(view.viewport_revision, revision);
    assert_eq!(
        shift_view(Coordinate::Integer(3), 0.0),
        Coordinate::Integer(3)
    );
}

#[test]
fn completed_surface_reuse_requires_matching_pixels_but_allows_viewport_transforms() {
    let spec = RenderSpec::new(viewport(0.0, 0.0, 100.0, 100.0), 800, 600, 1);
    let mut next = spec.clone();
    next.request_id += 1;
    next.viewport.origin.x = Coordinate::Scalar(5.0);
    assert!(same_surface(&spec, &next));
    for change in [
        |s: &mut RenderSpec| s.width += 1,
        |s: &mut RenderSpec| s.height += 1,
        |s: &mut RenderSpec| s.device_epoch += 1,
        |s: &mut RenderSpec| s.y_down = !s.y_down,
        |s: &mut RenderSpec| s.radius *= 2.0,
        |s: &mut RenderSpec| s.line_width *= 2.0,
        |s: &mut RenderSpec| s.color[0] += 0.1,
        |s: &mut RenderSpec| s.clip[0] += 1,
        |s: &mut RenderSpec| s.style_version += 1,
    ] {
        let mut next = spec.clone();
        change(&mut next);
        assert!(!same_surface(&spec, &next));
    }
}

#[test]
fn zoom_keeps_the_pointer_anchor_and_axis_modifiers_keep_the_other_span() {
    assert_eq!(
        shift_view(Coordinate::Integer(1), 0.25),
        Coordinate::Scalar(1.25)
    );
    assert_eq!(
        shift_view(Coordinate::Integer(1_i64 << 54), 1.0),
        Coordinate::Integer((1_i64 << 54) + 1)
    );
    let mut view = ChartView::new(result(1)).unwrap();
    view.enable_trajectory();
    view.bounds = Bounds {
        origin: point(px(50.0), px(20.0)),
        size: size(px(200.0), px(100.0)),
    };
    view.set_viewport(viewport(-100.0, -50.0, 200.0, 100.0), true);
    view.zoom_at(0.5, point(px(100.0), px(40.0)), false, false);
    let zoomed = view.viewport();
    assert_eq!(zoomed.origin.x.value() + zoomed.width * 0.25, -50.0);
    assert_eq!(zoomed.origin.y.value() + zoomed.height * 0.2, -30.0);
    view.trajectory = false;
    view.zoom_at(0.5, point(px(100.0), px(40.0)), true, false);
    assert_eq!(view.viewport().height, zoomed.height);
    assert_eq!(view.viewport().width, zoomed.width * 0.5);
    view.zoom_at(0.5, point(px(30.0), px(40.0)), false, false);
    assert_eq!(view.viewport().width, zoomed.width * 0.5);
    assert_eq!(view.viewport().height, zoomed.height * 0.5);
}

#[test]
fn small_integer_trajectories_remain_inside_the_fitted_view_after_padding_is_quantized() {
    let result = result(1);
    let fitted = result.workspace_viewport(1).unwrap();
    for sample in result.datasets[0]
        .geometry
        .iter()
        .flat_map(|block| &block.samples)
    {
        let x = sample.point.x.relative_to(fitted.origin.x);
        let y = sample.point.y.relative_to(fitted.origin.y);
        assert!(
            x >= 0.0 && x <= fitted.width,
            "{sample:?} outside {fitted:?}"
        );
        assert!(
            y >= 0.0 && y <= fitted.height,
            "{sample:?} outside {fitted:?}"
        );
    }
}

#[test]
fn display_options_preserve_manual_view_but_range_changes_refit() {
    let mut source = result(1);
    {
        let result = Arc::get_mut(&mut source).unwrap();
        result.request.kind = PlotKind::Points;
        result.request.parameters.metric = Metric::Count(clicksync_chart::Axis::X);
        result.request.analysis = Some(Default::default());
    }
    let mut view = ChartView::new(source.clone()).unwrap();
    view.axis_gutter = 90.0;
    let zoomed = viewport(200.0, -3.0, 25.0, 6.0);
    view.set_viewport(zoomed, true);
    let mut changed = result(1);
    Arc::get_mut(&mut changed).unwrap().request = source.request.clone();
    Arc::get_mut(&mut changed)
        .unwrap()
        .request
        .analysis
        .as_mut()
        .unwrap()
        .options = crate::product::performance_analysis::PlotOptions {
        lines: true,
        stems: true,
        trend: true,
        gaps: true,
        gap_dataset: Some(1),
    };
    view.set_result(changed.clone()).unwrap();
    assert_eq!(view.axis_gutter, 90.0);
    assert_eq!(view.viewport(), zoomed);
    assert!(view.fixed);
    let mut ranged = changed.request.clone();
    ranged.analysis.as_mut().unwrap().range = Some((0.0, 1.0));
    let mut range_result = result(1);
    Arc::get_mut(&mut range_result).unwrap().request = ranged;
    view.set_result(range_result).unwrap();
    assert!(!view.fixed);
    assert_ne!(view.viewport(), zoomed);
    view.set_result(result(2)).unwrap();
    assert_eq!(view.axis_gutter, 48.0);
}

#[test]
fn viewport_projection_shares_geometry_and_indices_without_changing_the_input() {
    let source = result(1);
    let policy = ViewMode::Follow { window_ms: 3000.0 };
    let projected = source.with_view(99, policy);
    assert_eq!(projected.request.id, 99);
    assert_eq!(projected.request.view, policy);
    assert!(Arc::ptr_eq(
        &source.request.prefix,
        &projected.request.prefix
    ));
    assert!(Arc::ptr_eq(
        &source.datasets[0].scene,
        &projected.datasets[0].scene
    ));
    assert!(Arc::ptr_eq(
        &source.datasets[0].hit_index,
        &projected.datasets[0].hit_index
    ));
    assert_ne!(source.request.id, projected.request.id);
}

#[test]
fn linked_time_reveal_preserves_scale_and_time_zoom_does_not_switch_to_y() {
    let mut source = result(1);
    let result = Arc::get_mut(&mut source).unwrap();
    result.request.kind = PlotKind::Points;
    result.request.parameters.metric = Metric::Interval;
    let mut view = ChartView::new(source).unwrap();
    view.bounds = Bounds {
        origin: point(px(50.0), px(20.0)),
        size: size(px(400.0), px(200.0)),
    };
    view.set_viewport(viewport(2000.0, 0.12, 500.0, 0.01), true);
    assert!(view.reveal_time(Coordinate::Scalar(100.0)));
    let revealed = view.viewport();
    assert_eq!(revealed.width, 500.0);
    assert_eq!(revealed.height, 0.01);
    assert_eq!(revealed.origin.y, Coordinate::Scalar(0.12));
    assert_eq!((100.0 - revealed.origin.x.value()) / revealed.width, 0.25);
    assert!(!view.reveal_time(Coordinate::Scalar(100.0)));
    view.set_viewport(viewport(100.0, 0.12, 5.0, 0.01), true);
    view.zoom_at(0.5, view.bounds.center(), false, false);
    assert_eq!(view.viewport().width, 2.5);
    assert_eq!(view.viewport().height, 0.01);
    let before = view.viewport();
    view.sync_time_view(viewport(1500.0, -10.0, 100.0, 20.0));
    assert_eq!(view.viewport().origin.y, before.origin.y);
    assert_eq!(view.viewport().height, before.height);
    assert_eq!(view.viewport().origin.x.value(), 1500.0);
    assert_eq!(view.viewport().width, 100.0);
}
