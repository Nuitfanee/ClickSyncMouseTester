//! Opt-in performance measurement of production views; no input capture or settings writes.
use super::*;
use crate::backend::{CapturePhase, SessionPrefix};
use gpui::private::anyhow::{anyhow, ensure};
use std::time::Instant;

mod hover;

pub(in crate::ui) fn run(path: PathBuf, reports: usize) -> Result<(), Box<dyn std::error::Error>> {
    if !(1024..=1_000_000).contains(&reports) {
        return Err("profile size must be 1024..=1000000".into());
    }
    let prefixes = synthetic_prefixes(reports)?;
    run_window(path, prefixes)
}

fn synthetic_prefixes(
    reports: usize,
) -> Result<Vec<Arc<SessionPrefix>>, clicksync_core::SessionError> {
    let mut session = clicksync_core::Session::default_with_metadata(Default::default())?;
    session.begin_collecting(100, true, false)?;
    let mut prefixes = Vec::new();
    for index in 0..reports + 256 * 64 {
        session.append(clicksync_core::RawMouseRecord::new(
            (8.0 * (index as f64 * 0.004).sin()).round() as i32,
            (5.0 * (index as f64 * 0.003).cos()).round() as i32,
            100 + index as u64 * 1250,
            index as u64 + 1,
            index as u64 + 1,
            clicksync_core::RawMouseMovementMode::Relative,
        ))?;
        if index + 1 >= reports && (index + 1 - reports).is_multiple_of(64) {
            session.set_capture_evidence(clicksync_core::session::CaptureEvidence {
                generation: Some(1),
                received_sequence: Some((index + 1) as u64),
                ..session.capture_evidence()
            });
            let snapshot = Arc::new(session.snapshot()?);
            prefixes.push(Arc::new(SessionPrefix {
                generation: 1,
                received_sequence: (index + 1) as u64,
                received_records: (index + 1) as u64,
                phase: CapturePhase::Collecting,
                complete: snapshot.integrity()
                    == clicksync_core::session::CaptureIntegrity::Complete,
                datasets: vec![(1, snapshot)],
            }));
        }
    }
    session.pause();
    let last = prefixes.last().unwrap();
    session.set_capture_evidence(clicksync_core::session::CaptureEvidence {
        queued: Some(last.received_sequence),
        cutoff: Some(last.received_sequence),
        ..session.capture_evidence()
    });
    prefixes.push(Arc::new(SessionPrefix {
        phase: CapturePhase::Paused,
        datasets: vec![(1, Arc::new(session.snapshot()?))],
        ..(**last).clone()
    }));
    Ok(prefixes)
}

fn run_window(
    path: PathBuf,
    prefixes: Vec<Arc<SessionPrefix>>,
) -> Result<(), Box<dyn std::error::Error>> {
    let failure = Arc::new(Mutex::new(None::<String>));
    let saved = failure.clone();
    Application::new()
        .with_assets(design::Assets)
        .run(move |cx| {
            editor::TextInput::bind_keys(cx);
            if let Err(error) = design::install_fonts(cx) {
                *failure.lock().unwrap() = Some(error.to_string());
                cx.quit();
                return;
            }
            let failure_in_window = failure.clone();
            let result = cx.open_window(
                WindowOptions {
                    show: true,
                    ..shell::main_options(Bounds::centered(
                        None,
                        size(px(DEFAULT_WIDTH as f32), px(DEFAULT_HEIGHT as f32)),
                        cx,
                    ))
                },
                move |window, cx| {
                    let host = cx.new(|cx| {
                        let mut host = GpuiHost::new(
                            window,
                            Arc::new(Mutex::new(Default::default())),
                            None,
                            false,
                            cx,
                        );
                        host.settings = Settings::default();
                        host.settings.monitor_metrics = DEFAULT_MONITORS.to_vec();
                        host.workspace.enabled = true;
                        host.workspace.chart_window = true;
                        host.selected = vec![1];
                        host
                    });
                    let measured = host.clone();
                    let handle = window.window_handle();
                    window
                        .spawn(cx, async move |cx| {
                            let result = measure(handle, &measured, &prefixes, &path, cx).await;
                            if let Err(error) = result {
                                *failure_in_window.lock().unwrap() = Some(error.to_string());
                            }
                            let _ = cx.update(|_, cx| cx.quit());
                        })
                        .detach();
                    host
                },
            );
            if let Err(error) = result {
                *failure.lock().unwrap() = Some(error.to_string());
                cx.quit();
            }
        });
    if let Some(error) = saved.lock().unwrap().take() {
        return Err(error.into());
    }
    Ok(())
}

fn pump(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    prefix: Arc<SessionPrefix>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<bool> {
    super::super::probe::ensure_complete_prefix(&prefix)?;
    handle.update(cx, |_, window, cx| {
        host.update(cx, |host, cx| {
            let events = host.runtime.borrow_mut().poll(host.receiver);
            let mut answers = events.monitors;
            if let Some(answer) = events.chart {
                answers.push((0, answer));
            }
            if host.tick_workspace(prefix, answers, events.summary, window, cx) {
                cx.notify();
            }
            ensure!(
                host.control_error.is_none(),
                "chart error: {:?}",
                host.control_error
            );
            let slots = if host.workspace.detail.is_some() {
                1
            } else {
                4
            };
            Ok((0..slots).all(|slot| {
                let views = host.workspace_plot_views(slot);
                !views.is_empty()
                    && views.iter().all(|view| {
                        let view = view.borrow();
                        view.ready()
                            && view
                                .presented_result()
                                .zip(host.workspace.targets[slot].as_ref())
                                .is_some_and(|(result, target)| result.request.id == target.id)
                    })
            }))
        })
    })?
}

async fn settle(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    prefix: Arc<SessionPrefix>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<f64> {
    let start = Instant::now();
    loop {
        if pump(handle, host, prefix.clone(), cx)? {
            return Ok(start.elapsed().as_secs_f64() * 1000.0);
        }
        if start.elapsed() > Duration::from_secs(60) {
            let state = handle.update(cx, |_, _, cx| {
                let host = host.read(cx);
                serde_json::json!({
                    "worker": host.runtime.borrow().worker.report(),
                    "views": host.plot_views().iter().map(|v| v.borrow().report()).collect::<Vec<_>>(),
                })
            })?;
            return Err(anyhow!("profile deadline: {state}"));
        }
        Timer::after(Duration::from_millis(4)).await;
    }
}

async fn measure(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    prefixes: &[Arc<SessionPrefix>],
    path: &std::path::Path,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    let (paused, prefixes) = prefixes.split_last().unwrap();
    let initial_ms = settle(handle, host, prefixes[0].clone(), cx).await?;
    Timer::after(Duration::from_millis(250)).await;
    let before = handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        (
            host.workspace.interaction.as_ref().unwrap().counts(),
            host.plot_views()
                .iter()
                .map(|v| v.borrow().report())
                .collect::<Vec<_>>(),
        )
    })?;
    let start = Instant::now();
    let mut ui_ms = Vec::new();
    let frames = Rc::new(RefCell::new(Vec::with_capacity(4096)));
    handle.update(cx, |_, window, _| {
        window.start_frame_timing();
        observe_frames(window, start, frames.clone())
    })?;
    let mut last_frames: [u64; 4] = std::array::from_fn(|slot| before.1[slot].completed_frames);
    let mut presentations: [Vec<f64>; 4] = Default::default();
    let mut widths: [std::collections::BTreeSet<u32>; 4] = Default::default();
    loop {
        let index = ((start.elapsed().as_secs_f64() * 125.0) as usize).min(prefixes.len() - 1);
        let step = Instant::now();
        pump(handle, host, prefixes[index].clone(), cx)?;
        ui_ms.push(step.elapsed().as_secs_f64() * 1000.0);
        handle.update(cx, |_, _, cx| {
            let host = host.read(cx);
            for (slot, view) in host.plot_views().iter().enumerate() {
                let report = view.borrow().report();
                widths[slot].insert(report.width);
                if report.completed_frames != last_frames[slot] {
                    presentations[slot].push(start.elapsed().as_secs_f64() * 1000.0);
                    last_frames[slot] = report.completed_frames;
                }
            }
        })?;
        if index == prefixes.len() - 1 {
            break;
        }
        Timer::after(Duration::from_millis(8)).await;
    }
    let seconds = start.elapsed().as_secs_f64();
    let live = handle.update(cx, |_, window, cx| {
        let timings = window.take_frame_timings().unwrap();
        ensure!(timings.omitted == 0, "actual frame timing buffer overflow");
        ensure!(!timings.samples.is_empty(), "no actual redraws observed");
        let host = host.read(cx);
        let after = host.workspace.interaction.as_ref().unwrap().counts();
        Ok::<_, gpui::private::anyhow::Error>(serde_json::json!({
            "seconds": seconds, "ui_pump_ms": distribution(ui_ms),
            "platform_frame_callback_intervals_ms": distribution(frames.borrow().windows(2).map(|p| p[1] - p[0]).collect()),
            "frames": frame_statistics(timings),
            "content_rebuilds": after[0] - before.0[0],
            "presented_results": (1..5).map(|i| after[i] - before.0[i]).collect::<Vec<_>>(),
            "presentation_intervals_ms": presentations.into_iter().map(|p|
                distribution(p.windows(2).map(|w| w[1] - w[0]).collect())).collect::<Vec<_>>(),
            "distinct_surface_widths": widths,
            "before_charts": before.1,
            "charts": host.plot_views().iter().map(|v| v.borrow().report()).collect::<Vec<_>>(),
            "bounds_work": host.plot_views().iter().map(|v|
                v.borrow().result_for_report().datasets.iter()
                    .map(|d| d.bounds_work).collect::<Vec<_>>()).collect::<Vec<_>>(),
            "worker": host.runtime.borrow().worker.report(),
        }))
    })??;
    let prefix = paused.clone();
    let paused_ms = settle(handle, host, prefix.clone(), cx).await?;
    let mut navigation = Vec::new();
    for metric in DEFAULT_MONITORS {
        let (monitors, completed, submitted, source, viewport) =
            handle.update(cx, |_, _, cx| {
                let host = host.read(cx);
                let slot = host
                    .settings
                    .monitor_metrics
                    .iter()
                    .position(|candidate| *candidate == metric)
                    .unwrap();
                let view = host.workspace.monitors[slot][0].borrow();
                (
                    host.workspace.monitors.clone(),
                    host.workspace.completed,
                    host.workspace.submitted[0],
                    view.presented_result().unwrap(),
                    view.viewport(),
                )
            })?;
        let command = Instant::now();
        handle.update(cx, |_, _, cx| {
            host.update(cx, |host, cx| {
                host.open_plot_detail(metric, false);
                cx.notify();
            })
        })?;
        let open_command_ms = command.elapsed().as_secs_f64() * 1000.0;
        settle(handle, host, prefix.clone(), cx).await?;
        let open_ms = command.elapsed().as_secs_f64() * 1000.0;
        handle.update(cx, |_, _, cx| {
            let host = host.read(cx);
            let view = host.chart.as_ref().unwrap().borrow();
            let result = view.presented_result().unwrap();
            ensure!(
                host.workspace.submitted[0] == submitted,
                "matching detail submitted historical computation"
            );
            ensure!(
                source
                    .datasets
                    .iter()
                    .zip(&result.datasets)
                    .all(|(old, new)| {
                        Arc::ptr_eq(&old.scene, &new.scene)
                            && Arc::ptr_eq(&old.hit_index, &new.hit_index)
                    })
                    && source.datasets.len() == result.datasets.len(),
                "matching detail replaced immutable CPU payloads"
            );
            ensure!(
                view.viewport() == viewport,
                "matching detail reset the overview viewport"
            );
            Ok::<_, gpui::private::anyhow::Error>(())
        })??;
        let command = Instant::now();
        handle.update(cx, |_, _, cx| {
            host.update(cx, |host, cx| {
                host.show_plot_overview();
                cx.notify();
            })
        })?;
        let return_command_ms = command.elapsed().as_secs_f64() * 1000.0;
        settle(handle, host, prefix.clone(), cx).await?;
        let return_ms = command.elapsed().as_secs_f64() * 1000.0;
        let reused = handle.update(cx, |_, _, cx| {
            let host = host.read(cx);
            let same = monitors
                .iter()
                .zip(&host.workspace.monitors)
                .all(|(old, new)| {
                    old.len() == new.len() && old.iter().zip(new).all(|(a, b)| Rc::ptr_eq(a, b))
                });
            ensure!(same, "navigation replaced the monitor views");
            ensure!(
                completed[1..] == host.workspace.completed[1..],
                "navigation recomputed unchanged monitor inputs"
            );
            ensure!(
                submitted == host.workspace.submitted[0],
                "detail recomputed an already completed matching chart"
            );
            Ok::<_, gpui::private::anyhow::Error>(same)
        })??;
        navigation.push(serde_json::json!({
            "metric": metric, "open_command_ms": open_command_ms, "open_ready_ms": open_ms,
            "return_command_ms": return_command_ms, "return_ready_ms": return_ms,
            "reused_monitors_without_recompute": reused,
            "reused_detail_cpu_result": true,
        }));
    }
    let interactions = measure_interactions(handle, host, prefix.clone(), cx).await?;
    let wheel_interactions = measure_wheel(handle, host, prefix.clone(), cx).await?;
    let multi_dataset_hover = hover::verify(handle, host, cx).await?;
    let report = serde_json::json!({
        "scope": "Production GPUI workspace/ChartWorker/D3D11; prebuilt synthetic 8 kHz prefixes. UI pump and observed layer completion intervals, NOT hardware input latency or display FPS.",
        "initial_reports": prefixes[0].received_records,
        "final_reports": prefix.received_records,
        "input_integrity": [
            { "name": "initial", "prefix": super::super::probe::prefix_report(&prefixes[0])? },
            { "name": "collecting", "prefix": super::super::probe::prefix_report(prefixes.last().unwrap())? },
            { "name": "paused", "prefix": super::super::probe::prefix_report(&prefix)? },
        ],
        "debug_assertions": cfg!(debug_assertions),
        "initial_ms": initial_ms, "paused_ms": paused_ms, "live": live, "navigation": navigation,
        "interactions": interactions, "wheel_interactions": wheel_interactions,
        "multi_dataset_hover": multi_dataset_hover,
    });
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(path, serde_json::to_vec_pretty(&report)?)?;
    // Read back only after measurement; capture cannot inflate the measured interval.
    handle.update(cx, |_, window, _| {
        window.request_windows_frame_capture()?;
        window.refresh();
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    let start = Instant::now();
    loop {
        Timer::after(Duration::from_millis(20)).await;
        if let Some(frame) = handle.update(cx, |_, window, _| window.take_windows_frame())?? {
            write_probe_frame(&path.with_extension("png"), frame, cx.background_executor()).await?;
            break;
        }
        ensure!(
            start.elapsed() < Duration::from_secs(5),
            "profile capture deadline"
        );
    }
    Ok(())
}

async fn measure_interactions(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    prefix: Arc<SessionPrefix>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<serde_json::Value> {
    let mut results = Vec::new();
    for slot in [0, 1] {
        let (view, viewport, before_worker, before_upload) =
            handle.update(cx, |_, window, cx| {
                window.start_frame_timing();
                let host = host.read(cx);
                let view = host.workspace_plot_views(slot)[0].clone();
                let viewport = view.borrow().viewport();
                let uploads = view.borrow().report().resources.unwrap().uploaded_bytes;
                (
                    view,
                    viewport,
                    host.runtime.borrow().worker.report().processed_targets,
                    uploads,
                )
            })?;
        let mut commands = Vec::with_capacity(32);
        let mut ready = Vec::with_capacity(32);
        for frame in 0..32 {
            let started = Instant::now();
            handle.update(cx, |_, _, cx| {
                host.update(cx, |host, cx| {
                    let direction = if frame % 2 == 0 { 1.0 } else { -1.0 };
                    view.borrow_mut()
                        .zoom(if frame % 2 == 0 { 1.04 } else { 1.0 / 1.04 });
                    view.borrow_mut().pan(direction * 4.0, 0.0);
                    host.sync_monitor_time(&view);
                    let bounds = view.borrow().bounds_for_trial();
                    let position = bounds.origin
                        + gpui::point(
                            bounds.size.width * (0.2 + frame as f32 / 64.0),
                            bounds.size.height * 0.45,
                        );
                    host.queue_analysis_pointer(view.clone(), position, cx);
                    if let Some(plots) = &host.workspace.interaction {
                        for slot in 0..4 {
                            plots.notify_plot(slot, cx);
                        }
                    }
                    cx.notify();
                })
            })?;
            commands.push(started.elapsed().as_secs_f64() * 1000.0);
            settle(handle, host, prefix.clone(), cx).await?;
            ready.push(started.elapsed().as_secs_f64() * 1000.0);
        }
        handle.update(cx, |_, _, cx| {
            host.update(cx, |host, cx| {
                view.borrow_mut().set_viewport(viewport, true);
                host.sync_monitor_time(&view);
                cx.notify();
            })
        })?;
        settle(handle, host, prefix.clone(), cx).await?;
        let (after, timings) = handle.update(cx, |_, window, cx| {
            let timings = window.take_frame_timings().unwrap();
            ensure!(
                timings.omitted == 0,
                "interaction frame timing buffer overflow"
            );
            ensure!(
                !timings.samples.is_empty(),
                "no interaction redraws observed"
            );
            let host = host.read(cx);
            ensure!(
                before_worker == host.runtime.borrow().worker.report().processed_targets,
                "pan/zoom/hover recomputed historical chart inputs"
            );
            ensure!(
                view.borrow().viewport() == viewport,
                "explicit viewport restoration failed"
            );
            let report = view.borrow().report();
            ensure!(
                report.resources.as_ref().unwrap().uploaded_bytes == before_upload,
                "warm pan/zoom/hover reuploaded immutable geometry"
            );
            Ok::<_, gpui::private::anyhow::Error>((report, timings))
        })??;
        results.push(serde_json::json!({
            "slot": slot, "commands_ms": distribution(commands), "layer_ready_ms": distribution(ready),
            "chart": after, "recomputed_targets": 0, "uploaded_bytes": 0,
            "frames": frame_statistics(timings),
            "boundary": "32 controlled pan/zoom/pointer operations, command start through observed layer readiness (4 ms polling); not physical input or scanout latency",
        }));
    }
    Ok(results.into())
}

async fn measure_wheel(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    prefix: Arc<SessionPrefix>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<serde_json::Value> {
    let mut results = Vec::new();
    for (slot, detail) in [(0, false), (1, false), (0, true)] {
        if detail {
            handle.update(cx, |_, _, cx| {
                host.update(cx, |host, cx| {
                    host.open_plot_detail(host.settings.monitor_metrics[0], false);
                    cx.notify();
                })
            })?;
        }
        settle(handle, host, prefix.clone(), cx).await?;
        Timer::after(Duration::from_millis(250)).await;
        let (view, viewport, counts, worker, uploaded) = handle.update(cx, |_, window, cx| {
            window.start_frame_timing();
            let host = host.read(cx);
            let view = host.workspace_plot_views(slot)[0].clone();
            let viewport = view.borrow().viewport();
            let uploaded = view.borrow().report().resources.unwrap().uploaded_bytes;
            (
                view,
                viewport,
                host.workspace.interaction.as_ref().unwrap().counts(),
                host.runtime.borrow().worker.report().processed_targets,
                uploaded,
            )
        })?;
        let mut responses = Vec::with_capacity(32);
        for step in 0..32 {
            let started = Instant::now();
            let zooms = view.borrow().report().zoom_events;
            let position = view.borrow().bounds_for_trial().center();
            handle.update(cx, |_, window, _| unsafe {
                use windows::Win32::{
                    Foundation::{LPARAM, POINT, WPARAM},
                    Graphics::Gdi::ClientToScreen,
                    UI::WindowsAndMessaging::{PostMessageW, WM_MOUSEWHEEL},
                };
                let mut point = POINT {
                    x: (f32::from(position.x) * window.scale_factor()).round() as i32,
                    y: (f32::from(position.y) * window.scale_factor()).round() as i32,
                };
                let hwnd = shell::hwnd(window);
                ClientToScreen(hwnd, &mut point).ok()?;
                let delta: i16 = if step % 2 == 0 { 120 } else { -120 };
                PostMessageW(
                    Some(hwnd),
                    WM_MOUSEWHEEL,
                    WPARAM((u32::from(delta as u16) << 16) as usize),
                    LPARAM(
                        ((u32::from(point.y as u16) << 16) | u32::from(point.x as u16)) as isize,
                    ),
                )
            })??;
            while view.borrow().report().zoom_events == zooms {
                ensure!(
                    started.elapsed() < Duration::from_secs(2),
                    "wheel handler deadline"
                );
                Timer::after(Duration::from_millis(4)).await;
            }
            ensure!(
                view.borrow().report().zoom_events == zooms + 1,
                "wheel did not reach the production chart handler"
            );
            settle(handle, host, prefix.clone(), cx).await?;
            responses.push(started.elapsed().as_secs_f64() * 1000.0);
        }
        let item = handle.update(cx, |_, window, cx| {
            let timings = window.take_frame_timings().unwrap();
            ensure!(timings.omitted == 0, "wheel frame timing overflow");
            ensure!(!timings.samples.is_empty(), "wheel produced no redraw");
            let host = host.read(cx);
            ensure!(
                host.runtime.borrow().worker.report().processed_targets == worker,
                "wheel recomputed historical inputs"
            );
            ensure!(
                view.borrow().report().resources.unwrap().uploaded_bytes == uploaded,
                "wheel uploaded unchanged geometry"
            );
            Ok::<_, gpui::private::anyhow::Error>(serde_json::json!({
                "slot": slot, "detail": detail,
                "response_ms": distribution(responses.clone()), "raw_response_ms": responses,
                "content_rebuilds": host.workspace.interaction.as_ref().unwrap().counts()[0] - counts[0],
                "frames": frame_statistics(timings),
                "recomputed_targets": 0, "uploaded_bytes": 0,
                "boundary": "Posted WM_MOUSEWHEEL through native/GPUI hit-test and handler to observed layer ready, 4 ms polling; no physical mouse or scanout",
            }))
        })??;
        results.push(item);
        handle.update(cx, |_, _, cx| {
            host.update(cx, |host, cx| {
                view.borrow_mut().set_viewport(viewport, true);
                host.sync_monitor_time(&view);
                if detail {
                    host.show_plot_overview();
                }
                cx.notify();
            })
        })?;
        settle(handle, host, prefix.clone(), cx).await?;
    }
    Ok(results.into())
}

fn frame_statistics(timings: gpui::FrameTimings) -> serde_json::Value {
    let milliseconds = |duration: Duration| duration.as_secs_f64() * 1000.0;
    let origin = timings.samples[0].started;
    serde_json::json!({
        "boundary": "Actual redraw branch on UI thread; draw includes chart D3D11 submission, present includes platform composition/Present, neither is GPU completion or scanout",
        "actual_redraw_intervals_ms": distribution(timings.samples.windows(2)
            .map(|p| milliseconds(p[1].started - p[0].started)).collect()),
        "actual_frame_cpu_ms": distribution(timings.samples.iter()
            .map(|p| milliseconds(p.draw + p.present + p.cleanup)).collect()),
        "ui_draw_cpu_ms": distribution(timings.samples.iter().map(|p| milliseconds(p.draw)).collect()),
        "platform_present_cpu_ms": distribution(timings.samples.iter().map(|p| milliseconds(p.present)).collect()),
        "frame_cleanup_cpu_ms": distribution(timings.samples.iter().map(|p| milliseconds(p.cleanup)).collect()),
        "omitted": timings.omitted,
        "raw_sample_columns": ["start_offset_us", "draw_us", "present_us", "cleanup_us"],
        "raw_samples": timings.samples.iter().map(|p| [
            (p.started - origin).as_secs_f64() * 1_000_000.0,
            p.draw.as_secs_f64() * 1_000_000.0,
            p.present.as_secs_f64() * 1_000_000.0,
            p.cleanup.as_secs_f64() * 1_000_000.0,
        ]).collect::<Vec<_>>(),
    })
}

fn distribution(mut values: Vec<f64>) -> serde_json::Value {
    values.sort_by(f64::total_cmp);
    if values.is_empty() {
        return serde_json::Value::Null;
    }
    serde_json::json!({
        "n": values.len(), "p50": values[values.len() / 2],
        "p95": values[(values.len() * 95).div_ceil(100)-1],
        "p99": values[(values.len() * 99).div_ceil(100)-1], "max": values.last(),
    })
}

fn observe_frames(window: &Window, start: Instant, frames: Rc<RefCell<Vec<f64>>>) {
    window.on_next_frame(move |window, _| {
        if start.elapsed() < Duration::from_secs(4) && frames.borrow().len() < 4096 {
            frames
                .borrow_mut()
                .push(start.elapsed().as_secs_f64() * 1000.0);
            // Observe existing draws, without forcing a refresh or animation frame.
            observe_frames(window, start, frames);
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use clicksync_core::{SessionStatus, session::CaptureIntegrity};

    #[test]
    fn synthetic_input_is_complete_while_collecting_and_pause_freezes_the_same_records() {
        let prefixes = synthetic_prefixes(1024).unwrap();
        assert!(
            prefixes.iter().all(|prefix| prefix.complete),
            "unfinished capture is not data loss"
        );
        let (paused, collecting) = prefixes.split_last().unwrap();
        assert_eq!(collecting.len(), 257);
        for prefix in collecting {
            super::super::super::probe::ensure_complete_prefix(prefix).unwrap();
            assert!(prefix.complete, "unfinished capture is not data loss");
            assert_eq!(prefix.phase, CapturePhase::Collecting);
            assert_eq!(prefix.received_sequence, prefix.received_records);
            let snapshot = &prefix.datasets[0].1;
            assert_eq!(snapshot.event_count() as u64, prefix.received_records);
            assert_eq!(snapshot.integrity(), CaptureIntegrity::Complete);
            assert_eq!(snapshot.status(), SessionStatus::Collecting);
        }
        let last = collecting.last().unwrap();
        super::super::super::probe::ensure_complete_prefix(paused).unwrap();
        assert!(paused.complete);
        assert_eq!(paused.phase, CapturePhase::Paused);
        assert_eq!(paused.datasets[0].1.status(), SessionStatus::Paused);
        assert!(paused.datasets[0].1.can_continue());
        assert!(!paused.datasets[0].1.finalized());
        assert_eq!(paused.received_records, last.received_records);
        assert_eq!(
            paused.datasets[0].1.events().collect::<Vec<_>>(),
            last.datasets[0].1.events().collect::<Vec<_>>()
        );
    }
}
