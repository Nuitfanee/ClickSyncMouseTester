//! Continuous four-chart CPU pipeline; not display FPS or physical-input latency.
use clicksync_app::{
    backend::{CapturePhase, SessionPrefix},
    chart::{ChartRequest, ChartResult, ChartWorker, ViewMode},
    product::performance_analysis::{PlotAnalysis, PlotOptions},
};
use clicksync_chart::{Axis, ChartParameters, Metric, gpu::PlotKind};
use clicksync_core::{
    RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SharedByteBudget,
    session::CaptureIntegrity,
};
use std::{
    sync::Arc,
    thread,
    time::{Duration, Instant},
};

fn main() -> Result<(), String> {
    let styled = std::env::args().any(|arg| arg == "--styled");
    let config = SessionConfig {
        timestamp_frequency: 8_000,
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
    let mut worker = ChartWorker::spawn();
    let mut times = Vec::new();
    let mut latest: Vec<Arc<ChartResult>> = Vec::new();
    let mut revised = [0_usize; 4];
    let mut request_id = 1;
    let mut trajectory: Option<clicksync_app::chart_view::ChartView> = None;
    for frame in 0..=240 {
        if frame == 240 {
            session.stop();
        } else {
            for offset in 0..128 {
                let i = frame * 128 + offset;
                session
                    .append(RawMouseRecord::new(
                        (8.0 * (i as f64 * 0.004).sin()).round() as i32,
                        (5.0 * (i as f64 * 0.003).cos()).round() as i32,
                        8_000 + i as u64,
                        i as u64 + 1,
                        i as u64 + 1,
                        RawMouseMovementMode::Relative,
                    ))
                    .map_err(|e| e.to_string())?;
            }
        }
        let snapshot = Arc::new(session.snapshot().map_err(|e| e.to_string())?);
        let prefix = Arc::new(SessionPrefix {
            generation: 1,
            received_sequence: snapshot.event_count() as u64,
            received_records: snapshot.event_count() as u64,
            phase: if frame == 240 {
                CapturePhase::Stopped
            } else {
                CapturePhase::Collecting
            },
            complete: snapshot.integrity() == CaptureIntegrity::Complete,
            datasets: vec![(1, snapshot)],
        });
        let start = Instant::now();
        for (slot, metric) in [
            Metric::Trajectory,
            Metric::Velocity(Axis::Y),
            Metric::Velocity(Axis::X),
            Metric::Count(Axis::X),
        ]
        .into_iter()
        .enumerate()
        {
            let options = PlotOptions {
                lines: slot != 3,
                trend: slot == 3,
                ..Default::default()
            };
            worker.submit_view(
                0,
                slot,
                ChartRequest {
                    id: request_id,
                    source_version: 1,
                    prefix: prefix.clone(),
                    parameters: ChartParameters::new(metric),
                    dataset_cpi: vec![(1, Some(800.0))],
                    secondary_metric: (slot == 3).then_some(Metric::Count(Axis::Y)),
                    kind: match slot {
                        0 => PlotKind::Trajectory,
                        3 => PlotKind::Points,
                        _ => PlotKind::Lines,
                    },
                    view: if slot == 0 {
                        ViewMode::FullSession
                    } else {
                        ViewMode::Follow { window_ms: 3000.0 }
                    },
                    analysis: (styled || frame == 240).then_some(PlotAnalysis {
                        range: None,
                        options,
                    }),
                },
            )?;
            request_id += 1;
        }
        let mut results: [Option<Arc<ChartResult>>; 4] = Default::default();
        while results.iter().any(Option::is_none) {
            for slot in 0..4 {
                if results[slot].is_none()
                    && let Some((_, result)) = worker.take_view_completion(slot)
                {
                    let result = result?;
                    assert_eq!(
                        result.datasets[0].scene.state.processed_end,
                        prefix.received_sequence as usize
                    );
                    assert_eq!(
                        result.datasets[0]
                            .scene
                            .state
                            .data_version
                            .unwrap()
                            .integrity,
                        clicksync_chart::Integrity::Complete,
                        "a healthy live prefix must not be marked incomplete"
                    );
                    revised[slot] += result.datasets[0].work.geometry_samples_written;
                    results[slot] = Some(result);
                }
            }
            if start.elapsed() > Duration::from_secs(10) {
                return Err("live chart deadline".into());
            }
            thread::sleep(Duration::from_micros(100));
        }
        let results: Vec<_> = results.into_iter().map(Option::unwrap).collect();
        let completion_ms = start.elapsed().as_secs_f64() * 1000.0;
        let preview = trajectory.get_or_insert_with(|| {
            let mut view = clicksync_app::chart_view::ChartView::new(results[0].clone()).unwrap();
            view.set_theme(false, false);
            view.enable_trajectory();
            // Reapplying the same result preserves its viewport; seed a valid first fit.
            let mut viewport = results[0].workspace_viewport(1).unwrap();
            viewport.width = viewport.width.max(viewport.height * 2.0);
            viewport.height = viewport.width / 2.0;
            view.set_viewport(viewport, false);
            view
        });
        let previous = preview.viewport();
        preview.set_result(results[0].clone())?;
        let view = preview.viewport();
        let data = &results[0].datasets[0];
        let x = data.plot_origin.x.relative_to(view.origin.x);
        let y = data.plot_origin.y.relative_to(view.origin.y);
        assert!(view.width >= previous.width && view.height >= previous.height);
        assert!((view.width / view.height - 2.0).abs() < 1e-9);
        assert!(
            x + data.plot_bounds[0] >= -1e-9 && x + data.plot_bounds[1] <= view.width + 1e-9,
            "frame={frame}, x_offset={x}, bounds={:?}, viewport={view:?}",
            data.plot_bounds
        );
        assert!(
            y + data.plot_bounds[2] >= -1e-9 && y + data.plot_bounds[3] <= view.height + 1e-9,
            "frame={frame}, y_offset={y}, bounds={:?}, viewport={view:?}",
            data.plot_bounds
        );
        if frame == 240 {
            for result in &results[1..3] {
                assert_eq!(
                    result.datasets[0].work.geometry_samples_written, 0,
                    "stopping reran velocity for an unchanged prefix"
                );
            }
            if styled {
                for (live, stopped) in latest.iter().zip(&results) {
                    let instances = |r: &ChartResult| {
                        r.datasets[0]
                            .scene
                            .blocks
                            .iter()
                            .flat_map(|b| b.instances.iter().copied())
                            .collect::<Vec<_>>()
                    };
                    assert_eq!(
                        instances(live),
                        instances(stopped),
                        "stopping changed the plotted layers"
                    );
                    assert_eq!(live.workspace_viewport(1)?, stopped.workspace_viewport(1)?);
                }
            }
        } else {
            times.push(completion_ms);
        }
        latest = results;
    }
    times.sort_by(f64::total_cmp);
    println!(
        "{}",
        serde_json::json!({
            "scope": "four-chart CPU submit-to-completion, 8000 Hz samples / 128 reports per prefix; not display FPS",
            "styled_live": styled, "prefixes": times.len(), "reports": 30720,
            "trajectory_auto_fit_checks": 241,
            "median_ms": times[times.len()/2], "p95_ms": times[(times.len()-1)*95/100],
            "p99_ms": times[(times.len()*99).div_ceil(100)-1],
            "max_ms": times.last(), "geometry_samples_written": revised,
            "scheduler": worker.report(),
        })
    );
    worker.shutdown();
    Ok(())
}
