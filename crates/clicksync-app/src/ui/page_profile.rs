//! Opt-in page redraw diagnostics; no appearance assertions.
use super::probe::{all_visible, wait};
use super::*;
use gpui::private::anyhow::{anyhow, ensure};
use serde_json::{Value, json};
use std::time::Instant;

#[derive(Clone, Copy, Debug, serde::Serialize)]
struct WindowState {
    active: bool,
    observed_active: bool,
    window_hwnd: usize,
    foreground_hwnd: usize,
    foreground_process_id: u32,
}

impl WindowState {
    fn observe(host: &GpuiHost, window: &Window) -> Self {
        use windows::Win32::UI::WindowsAndMessaging::{
            GetForegroundWindow, GetWindowThreadProcessId,
        };
        let foreground = unsafe { GetForegroundWindow() };
        let mut foreground_process_id = 0;
        unsafe { GetWindowThreadProcessId(foreground, Some(&mut foreground_process_id)) };
        Self {
            active: window.is_window_active(),
            observed_active: host.was_active,
            window_hwnd: host.lock.window_id(),
            foreground_hwnd: foreground.0 as usize,
            foreground_process_id,
        }
    }

    fn ready(self) -> bool {
        self.active && self.observed_active && self.native_foreground()
    }

    fn native_foreground(self) -> bool {
        self.window_hwnd != 0
            && self.window_hwnd == self.foreground_hwnd
            && self.foreground_process_id != 0
    }
}

pub(super) fn run(path: PathBuf) -> Result<(), Box<dyn std::error::Error>> {
    run_window(path).map_err(Into::into)
}
fn run_window(path: PathBuf) -> gpui::Result<()> {
    let directory = path
        .parent()
        .ok_or_else(|| anyhow!("integration report directory missing"))?
        .to_owned();
    std::fs::create_dir_all(&directory)?;
    let cursor = crate::capture_lock::cursor_observation()?;
    let report = Arc::new(Mutex::new(serde_json::json!({
        "passed":false,"error":null,
        "boundary":"Production page redraw measurements, not appearance regression or physical input latency."
    })));
    let saved = report.clone();
    let outcome = Arc::new(Mutex::new(WindowSmokeReport::default()));
    let saved_outcome = outcome.clone();
    Application::new()
        .with_assets(design::Assets)
        .run(move |cx| {
            editor::TextInput::bind_keys(cx);
            if let Err(error) = design::install_fonts(cx) {
                outcome_error(&outcome, error.to_string());
                cx.quit();
                return;
            }
            cx.on_window_closed(|cx| {
                if cx.windows().is_empty() {
                    cx.quit();
                }
            })
            .detach();
            let failure = outcome.clone();
            let opened = cx.open_window(
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
                        cx.observe_window_activation(window, |host: &mut GpuiHost, window, cx| {
                            host.activation_changed(window);
                            cx.notify();
                        })
                        .detach();
                        let mut host = GpuiHost::new(window, failure.clone(), None, true, cx);
                        host.custom_chrome = true;
                        host.persist_settings = false;
                        host.settings = Settings::default();
                        host.page = Page::Keys;
                        host
                    });
                    let runtime = host.read(cx).runtime.clone();
                    runtime::AppRuntime::bind_driver(&runtime, window, &host, cx);
                    GpuiHost::bind_main_close(window, &host, cx);
                    let handle = window.window_handle();
                    let measured = host.clone();
                    window
                        .spawn(cx, async move |cx| {
                            let result = exercise(handle, &measured, &report, cx).await;
                            match result {
                                Ok(()) => report.lock().unwrap()["passed"] = true.into(),
                                Err(error) => {
                                    {
                                        let _ = handle.update(cx, |_, window, _| {
                                            if let Some(timings) = window.take_frame_timings() {
                                                report.lock().unwrap()["failed_case_frames"] =
                                                    frame_statistics(timings);
                                            }
                                        });
                                    }
                                    report.lock().unwrap()["error"] = error.to_string().into();
                                    outcome_error(&failure, error.to_string());
                                }
                            }
                            let _ = handle.update(cx, |_, window, cx| {
                                measured.update(cx, |host, _| host.shutdown());
                                window.remove_window();
                            });
                        })
                        .detach();
                    host
                },
            );
            if let Err(error) = opened {
                outcome_error(&outcome, error.to_string());
                cx.quit();
            }
        });
    let after = crate::capture_lock::cursor_observation()?;
    let mut report = saved.lock().unwrap();
    report["cursor_restored"] =
        (cursor.clip == after.clip && cursor.showing == after.showing).into();
    report["bridge"] = serde_json::to_value(&saved_outcome.lock().unwrap().bridge)?;
    if let Some(error) = &saved_outcome.lock().unwrap().error {
        report["error"] = error.clone().into();
    }
    std::fs::write(path, serde_json::to_vec_pretty(&*report)?)?;
    ensure!(
        report["passed"] == true && report["cursor_restored"] == true && report["error"].is_null(),
        "page profile failed: {}",
        report["error"]
    );
    Ok(())
}

async fn seed_session(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<Arc<clicksync_core::SessionSnapshot>> {
    use crate::backend::{Backend, CapturePhase};
    wait(
        handle,
        host,
        |host| host.runtime.borrow().pending_action().is_none(),
        cx,
    )
    .await?;
    activate_smoke_capture_window(handle, host, cx).await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            if let Some(mut backend) = host.runtime.borrow_mut().backend.take() {
                backend.shutdown().map_err(|e| anyhow!(e))?;
            }
            host.runtime.borrow_mut().backend =
                Some(Backend::spawn_in_process(true).map_err(|e| anyhow!(e))?);
            host.navigate(Page::Polling);
            host.polling.capture_source = Some(clicksync_core::protocol::CaptureSource::Replay {
                records_per_device: 1024,
                reports_per_second: 8000,
                real_time: false,
            });
            cx.notify();
            Ok::<_, gpui::private::anyhow::Error>(())
        })
    })??;
    wait(
        handle,
        host,
        |host| {
            host.runtime.borrow().pending_action().is_none()
                && host.runtime.borrow().device_catalog.choice(1).is_some()
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, _| {
            host.polling_select(1);
            host.polling_primary();
        })
    })?;
    wait(
        handle,
        host,
        |host| {
            host.runtime
                .borrow()
                .backend
                .as_ref()
                .and_then(|b| b.latest_prefix())
                .is_some_and(|p| p.received_records == 1024)
                && host.runtime.borrow().pending_action().is_none()
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, _| host.capture_command("stop"))
    })?;
    wait(
        handle,
        host,
        |host| {
            host.last_phase == CapturePhase::Stopped
                && host.runtime.borrow().pending_action().is_none()
                && host
                    .runtime
                    .borrow()
                    .backend
                    .as_ref()
                    .and_then(|b| b.latest_prefix())
                    .is_some_and(|p| p.phase == CapturePhase::Stopped && p.complete)
        },
        cx,
    )
    .await?;
    let snapshot = handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        let prefix = host
            .runtime
            .borrow()
            .backend
            .as_ref()
            .unwrap()
            .latest_prefix()
            .unwrap();
        super::probe::ensure_complete_prefix(&prefix)?;
        ensure!(
            prefix.complete && prefix.received_records == 1024,
            "seed session incomplete"
        );
        Ok::<_, gpui::private::anyhow::Error>(prefix.datasets[0].1.clone())
    })??;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.navigate(Page::Keys);
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| {
            host.runtime.borrow().pending_action().is_none()
                && host
                    .runtime
                    .borrow()
                    .backend
                    .as_ref()
                    .and_then(|b| b.latest_prefix())
                    .is_some_and(|p| {
                        p.phase == CapturePhase::Empty
                            && p.generation >= host.page_generation
                            && !p.datasets.is_empty()
                            && p.datasets
                                .iter()
                                .all(|(_, snapshot)| snapshot.event_count() == 0)
                    })
        },
        cx,
    )
    .await?;
    Ok(snapshot)
}

fn distribution(mut samples: Vec<f64>) -> Value {
    samples.sort_by(f64::total_cmp);
    let quantile = |percent: usize| {
        samples
            .get((samples.len() * percent).div_ceil(100).saturating_sub(1))
            .copied()
    };
    json!({"n": samples.len(), "p95": quantile(95), "p99": quantile(99),
        "max": samples.last()})
}

pub(super) fn frame_statistics(timings: gpui::FrameTimings) -> Value {
    let origin = timings.samples.first().map(|sample| sample.started);
    let ms = |duration: Duration| duration.as_secs_f64() * 1000.0;
    json!({
        "omitted": timings.omitted,
        "cpu_ms": distribution(timings.samples.iter().map(|s| ms(s.draw + s.present + s.cleanup)).collect()),
        "draw_cpu_ms": distribution(timings.samples.iter().map(|s| ms(s.draw)).collect()),
        "present_cpu_ms": distribution(timings.samples.iter().map(|s| ms(s.present)).collect()),
        "cleanup_cpu_ms": distribution(timings.samples.iter().map(|s| ms(s.cleanup)).collect()),
        "raw_columns": ["start_offset_ms", "draw_ms", "present_ms", "cleanup_ms"],
        "raw": timings.samples.iter().map(|s| [ms(s.started - origin.unwrap()),
            ms(s.draw), ms(s.present), ms(s.cleanup)]).collect::<Vec<_>>(),
    })
}

fn owner_ready(status: Option<&crate::backend::AppStatus>, minimum_generation: u64) -> bool {
    status.is_some_and(|status| {
        status.generation >= minimum_generation
            && !status.command_pending
            && status.phase != crate::backend::CapturePhase::Draining
    })
}

fn backend_healthy(status: Option<&crate::backend::AppStatus>, minimum_generation: u64) -> bool {
    // Background Discover shares command_pending; user commands are checked through the runtime.
    status.is_some_and(|status| {
        status.generation >= minimum_generation
            && !matches!(
                status.phase,
                crate::backend::CapturePhase::Faulted | crate::backend::CapturePhase::Draining
            )
            && status.complete
            && status.overflow == 0
            && status.ignored_after_limit == 0
            && status.error.as_deref().is_none_or(str::is_empty)
    })
}

async fn exercise(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    report: &Arc<Mutex<Value>>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    report.lock().unwrap()["method"] = "page-ui-v5-window-readiness".into();
    report.lock().unwrap()["failure_kind"] = Value::Null;
    report.lock().unwrap()["boundary"] = json!(
        "One production window and runtime; direct production commands, not injected hardware input. \
         Command-to-interactive is observed every 10 ms: an upper bound including polling/scheduling delay, \
         not physical display latency. Actual redraw CPU includes draw/Present/cleanup, not GPU completion. \
         Business pages are reset/empty, charts use the same fixed 1024-record controlled session. \
         Foreground activation is established before each case, outside measured intervals; \
         no reactivation, screenshots or forced refresh within them. Startup carrier, cross-window handoff, \
         sustained capture and active business rounds remain covered separately, not measured here."
    );
    report.lock().unwrap()["ready_boundary"] = json!(
        "GPUI and observed active state plus native foreground HWND, target state, at least one root render, \
         no pending control or capture lock; \
         menu/stage transition settled, visible chart layers ready, current-generation owner publication \
         with no command pending or drain in progress. Health is checked only at that ready boundary \
         and at steady-state end, not against intermediate retired owner states. Steady-state checks \
         retain the accepted owner's generation and selected device scope, require healthy data and \
         no runtime pending action; background Discover's raw command_pending is recorded, not a fault. \
         All readiness polls retain individual predicates, blocked reasons, HWND/PID and raw status/errors. \
         Lost window activation fails the measurement instead of waiting for the readiness deadline. \
         Steady-state has window and owner boundary snapshots only, not continuous fault monitoring. \
         Each switch is one raw observation, \
         not a switch-latency percentile. Per-frame percentiles are empirical with sample count n."
    );
    report.lock().unwrap()["pages"] = json!([]);
    let snapshot = seed_session(handle, host, cx).await?;
    let notifications = Rc::new(std::cell::Cell::new(0u64));
    let observed = notifications.clone();
    let _subscription = handle.update(cx, |_, _, cx| {
        cx.observe(host, move |_, _| observed.set(observed.get() + 1))
    })?;
    for (name, page) in [
        ("keys-after-session", Page::Keys),
        ("polling", Page::Polling),
        ("sensitivity-setup", Page::Sensitivity),
        ("sensitivity-measure", Page::Sensitivity),
        ("sensitivity-return", Page::Sensitivity),
        ("calibration", Page::Calibration),
        ("performance-setup", Page::Performance),
        ("navigation", Page::Performance),
        ("navigation-return", Page::Performance),
        ("overview", Page::Performance),
        ("detail", Page::Performance),
        ("overview-return", Page::Performance),
    ] {
        report.lock().unwrap()["active_case"] = name.into();
        let before = handle.update(cx, |_, window, cx| {
            WindowState::observe(host.read(cx), window)
        })?;
        let activation_started = Instant::now();
        let activation = if before.ready() {
            Ok(())
        } else {
            activate_smoke_capture_window(handle, host, cx).await
        };
        let after = handle.update(cx, |_, window, cx| {
            WindowState::observe(host.read(cx), window)
        })?;
        let precondition = json!({
            "before": before, "after": after, "recovery_requested": !before.ready(),
            "elapsed_ms": activation_started.elapsed().as_secs_f64() * 1000.0,
            "error": activation.as_ref().err().map(ToString::to_string),
        });
        report.lock().unwrap()["active_precondition"] = precondition.clone();
        if activation.is_err() || !after.ready() {
            report.lock().unwrap()["failure_kind"] = "foreground_precondition".into();
            gpui::private::anyhow::bail!(
                "page {name} foreground precondition failed: {precondition}"
            );
        }
        let renders = handle.update(cx, |_, window, cx| {
            window.start_frame_timing();
            host.read(cx).outcome.lock().unwrap().frame_count
        })?;
        let started = Instant::now();
        let minimum_generation = handle.update(cx, |_, window, cx| {
            host.update(cx, |host, cx| {
                match name {
                    "navigation" => host.open_menu(),
                    "navigation-return" => {
                        host.menu.close(None);
                    }
                    "sensitivity-measure" => {
                        host.business_input.bindings = [Some(1), Some(2)];
                        host.sensitivity.target_dpi = "100".into();
                        host.sensitivity_stage(sensitivity::Step::Measure, window);
                    }
                    "sensitivity-return" => {
                        host.sensitivity_stage(sensitivity::Step::Setup, window)
                    }
                    "overview" => {
                        host.imported = Some((snapshot.clone(), Some(800.0)));
                        host.import_identity += 1;
                        host.workspace.chart_window = true;
                        host.custom_chrome = false;
                    }
                    "detail" => host.open_plot_detail(16, false),
                    "overview-return" => host.show_plot_overview(),
                    _ => host.navigate(page),
                }
                host.focus.focus(window);
                cx.notify();
                host.page_generation
                    .max(host.runtime.borrow().status().map_or(0, |s| s.generation))
            })
        })?;
        let mut observations = Vec::with_capacity(1500);
        let (ready, status, control_error, pending, readiness, window_state) = loop {
            Timer::after(Duration::from_millis(10)).await;
            let observation = handle.update(cx, |_, window, cx| {
                let host = host.read(cx);
                let menu_ready = if name == "navigation" {
                    host.menu.interactive()
                } else {
                    !host.menu.visible()
                };
                let stage_ready = name != "sensitivity-measure"
                    || host.sensitivity.step == sensitivity::Step::Measure;
                let target_ready = match name {
                    "sensitivity-return" => host.sensitivity.step == sensitivity::Step::Setup,
                    "detail" => host.workspace.detail == Some(16),
                    "overview" | "overview-return" => host.workspace.detail.is_none(),
                    _ => true,
                };
                let plots_ready = !host.workspace.chart_window
                    || (all_visible(host)
                        && host.plot_views().iter().all(|v| v.borrow().ready())
                        && host.workspace.motion.opacity(Instant::now()) == 1.0);
                let runtime = host.runtime.borrow();
                let status = runtime.status();
                let current = owner_ready(
                    status.as_ref(),
                    minimum_generation.max(host.page_generation),
                );
                let frame_count = host.outcome.lock().unwrap().frame_count;
                let window_state = WindowState::observe(host, window);
                let checks = [
                    ("window_active", window_state.active),
                    ("activation_observed", window_state.observed_active),
                    ("native_foreground", window_state.native_foreground()),
                    ("target_page", host.page == page),
                    ("capture_unlocked", !host.lock.locked()),
                    ("root_rendered", frame_count > renders),
                    ("control_idle", runtime.pending_action().is_none()),
                    ("owner_ready", current),
                    ("motion_settled", host.sensitivity.motion.settled()),
                    ("menu_ready", menu_ready),
                    ("stage_ready", stage_ready),
                    ("target_ready", target_ready),
                    ("plots_ready", plots_ready),
                ];
                let blocked: Vec<_> = checks
                    .iter()
                    .filter_map(|(name, ready)| (!ready).then_some(*name))
                    .collect();
                let readiness = json!({
                    "checks": checks.into_iter().collect::<std::collections::BTreeMap<_, _>>(),
                    "blocked": blocked,
                    "actual_page": format!("{:?}", host.page),
                    "target_page": format!("{page:?}"),
                    "root_renders_before": renders,
                    "root_renders": frame_count,
                    "window": window_state,
                });
                (
                    blocked.is_empty(),
                    status,
                    host.control_error.clone(),
                    runtime.pending_action(),
                    readiness,
                    window_state,
                )
            })?;
            let (ready, status, control_error, pending, readiness, window_state) = &observation;
            observations.push((
                started.elapsed().as_secs_f64() * 1000.0,
                *ready,
                status.clone(),
                control_error.clone(),
                *pending,
                readiness.clone(),
            ));
            // An ACK can precede owner publication; do not judge a retired state as the target page.
            if *ready
                || !window_state.ready()
                || started.elapsed() >= Duration::from_secs(15)
                || observations.len() == 1500
            {
                break observation;
            }
        };
        let response_ms = started.elapsed().as_secs_f64() * 1000.0;
        let healthy = backend_healthy(status.as_ref(), minimum_generation);
        let interactive = ready && healthy && control_error.is_none() && pending.is_none();
        let transition = handle.update(cx, |_, window, _| window.take_frame_timings().unwrap())?;
        let omitted = transition.omitted;
        let first_submit_ms = transition.samples.first().map(|s| {
            (s.started + s.draw + s.present)
                .saturating_duration_since(started)
                .as_secs_f64()
                * 1000.0
        });
        report.lock().unwrap()["pages"]
            .as_array_mut()
            .unwrap()
            .push(json!({
                "name": name, "command_to_interactive_observed_ms": interactive.then_some(response_ms),
                "transition_observed_ms": response_ms,
                "foreground_precondition": precondition,
                "minimum_generation": minimum_generation, "transition_status": status,
                "transition_control_error": control_error, "transition_pending_action": pending,
                "transition_readiness": readiness,
                "transition_observation_columns": ["elapsed_ms", "ready", "status", "control_error", "pending_action", "readiness"],
                "transition_observations": observations,
                "first_redraw_submission_observed_ms": first_submit_ms,
                "transition": frame_statistics(transition),
            }));
        if !interactive {
            report.lock().unwrap()["failure_kind"] = if !window_state.ready() {
                "window_activation_lost"
            } else if !ready {
                "readiness_timeout"
            } else {
                "backend_unhealthy"
            }
            .into();
        }
        ensure!(
            interactive,
            "page {name} not interactive: ready={ready}, healthy={healthy}, pending={pending:?}, control_error={control_error:?}, readiness={readiness}"
        );
        ensure!(omitted == 0, "page {name} transition timing overflow");
        // Let one-shot feedback settle; no refresh or paint requests are injected.
        Timer::after(Duration::from_millis(700)).await;
        let (submitted, steady_start_status, start_error, start_pending, start_window) = handle
            .update(cx, |_, window, cx| {
                window.start_frame_timing();
                let host = host.read(cx);
                (
                    host.business_submissions,
                    host.runtime.borrow().status(),
                    host.control_error.clone(),
                    host.runtime.borrow().pending_action(),
                    WindowState::observe(host, window),
                )
            })?;
        let before_notifications = notifications.get();
        let steady_started = Instant::now();
        Timer::after(Duration::from_secs(1)).await;
        let (steady, submissions, metrics, steady_end_status, end_error, end_pending, end_window) =
            handle.update(cx, |_, window, cx| {
                let host = host.read(cx);
                (
                    window.take_frame_timings().unwrap(),
                    host.business_submissions - submitted,
                    SurfaceMetrics::from_window(window),
                    host.runtime.borrow().status(),
                    host.control_error.clone(),
                    host.runtime.borrow().pending_action(),
                    WindowState::observe(host, window),
                )
            })?;
        let omitted = steady.omitted;
        let steady_ms = steady_started.elapsed().as_secs_f64() * 1000.0;
        let mut saved_report = report.lock().unwrap();
        let entry = saved_report["pages"]
            .as_array_mut()
            .unwrap()
            .last_mut()
            .unwrap();
        entry["steady"] = frame_statistics(steady);
        entry["steady_observed_ms"] = steady_ms.into();
        entry["steady_business_submissions"] = submissions.into();
        entry["steady_host_notifications"] = (notifications.get() - before_notifications).into();
        entry["surface"] = serde_json::to_value(metrics)?;
        if page == Page::Polling {
            entry["polling_render"] =
                handle.update(cx, |_, _, cx| host.read(cx).polling_render_stats(cx))?;
            entry["polling_readout"] =
                handle.update(cx, |_, _, cx| host.read(cx).polling_readout_stats(cx))?;
        }
        entry["steady_start_status"] = json!(steady_start_status);
        entry["steady_end_status"] = json!(steady_end_status);
        entry["steady_control_errors"] = json!([start_error, end_error]);
        entry["steady_pending_actions"] = json!([start_pending, end_pending]);
        entry["steady_start_window"] = json!(start_window);
        entry["steady_end_window"] = json!(end_window);
        drop(saved_report);
        if !start_window.ready() || !end_window.ready() {
            report.lock().unwrap()["failure_kind"] = "window_activation_lost".into();
            gpui::private::anyhow::bail!(
                "page {name} steady-state window activation lost: start={start_window:?}, end={end_window:?}"
            );
        }
        let scope = status
            .as_ref()
            .map(|s| (s.generation, &s.selected_device_keys));
        ensure!(
            backend_healthy(steady_end_status.as_ref(), minimum_generation)
                && backend_healthy(steady_start_status.as_ref(), minimum_generation)
                && steady_start_status
                    .as_ref()
                    .map(|s| (s.generation, &s.selected_device_keys))
                    == scope
                && steady_end_status
                    .as_ref()
                    .map(|s| (s.generation, &s.selected_device_keys))
                    == scope
                && end_error.is_none()
                && end_pending.is_none()
                && start_error.is_none()
                && start_pending.is_none(),
            "page {name} steady-state owner status changed or was unhealthy"
        );
        ensure!(omitted == 0, "page {name} steady timing overflow");
    }
    report.lock().unwrap()["active_case"] = Value::Null;
    report.lock().unwrap()["active_precondition"] = Value::Null;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn window_readiness_requires_native_foreground_and_both_activation_observations() {
        let ready = WindowState {
            active: true,
            observed_active: true,
            window_hwnd: 42,
            foreground_hwnd: 42,
            foreground_process_id: 7,
        };
        assert!(ready.ready());
        for state in [
            WindowState {
                active: false,
                ..ready
            },
            WindowState {
                observed_active: false,
                ..ready
            },
            WindowState {
                foreground_hwnd: 99,
                ..ready
            },
            WindowState {
                foreground_hwnd: 0,
                ..ready
            },
            WindowState {
                foreground_process_id: 0,
                ..ready
            },
            WindowState {
                window_hwnd: 0,
                foreground_hwnd: 0,
                ..ready
            },
        ] {
            assert!(!state.ready(), "{state:?}");
        }
    }

    #[test]
    fn readiness_waits_for_owner_scope_before_judging_health() {
        use crate::backend::{AppStatus, CapturePhase};
        let old = AppStatus {
            generation: 7,
            phase: CapturePhase::Faulted,
            error: Some("retired fault".into()),
            ..Default::default()
        };
        assert!(!owner_ready(Some(&old), 8));
        let current = AppStatus {
            generation: 8,
            ..old
        };
        assert!(owner_ready(Some(&current), 8));
        assert!(!backend_healthy(Some(&current), 8));
        assert!(!owner_ready(
            Some(&AppStatus {
                command_pending: true,
                ..current.clone()
            }),
            8
        ));
        assert!(!owner_ready(
            Some(&AppStatus {
                phase: CapturePhase::Draining,
                ..current
            }),
            8
        ));
    }

    #[test]
    fn backend_health_rejects_faults_errors_missing_or_retired_status() {
        use crate::backend::{AppStatus, CapturePhase};
        let healthy = AppStatus {
            generation: 8,
            complete: true,
            ..Default::default()
        };
        assert!(backend_healthy(Some(&healthy), 8));
        assert!(!backend_healthy(Some(&healthy), 9));
        assert!(!backend_healthy(None, 8));
        let discovering = AppStatus {
            command_pending: true,
            ..healthy.clone()
        };
        assert!(backend_healthy(Some(&discovering), 8));
        assert!(!owner_ready(Some(&discovering), 8));
        for status in [
            AppStatus {
                phase: CapturePhase::Faulted,
                ..healthy.clone()
            },
            AppStatus {
                complete: false,
                ..healthy.clone()
            },
            AppStatus {
                overflow: 1,
                ..healthy.clone()
            },
            AppStatus {
                ignored_after_limit: 1,
                ..healthy.clone()
            },
            AppStatus {
                phase: CapturePhase::Draining,
                ..healthy.clone()
            },
            AppStatus {
                error: Some("control rejected".into()),
                ..healthy.clone()
            },
        ] {
            assert!(!backend_healthy(Some(&status), 8));
            assert!(!backend_healthy(
                Some(&AppStatus {
                    command_pending: true,
                    ..status
                }),
                8
            ));
        }
    }

    #[test]
    fn empty_frames_have_no_percentiles_and_nearest_rank_is_explicit() {
        let empty = frame_statistics(gpui::FrameTimings {
            samples: Vec::new(),
            omitted: 0,
        });
        assert_eq!(empty["cpu_ms"]["n"], 0);
        assert!(empty["cpu_ms"]["p95"].is_null() && empty["cpu_ms"]["p99"].is_null());
        assert_eq!(empty["raw"], json!([]));
        let values = distribution((1..=100).map(f64::from).collect());
        assert_eq!(values["p95"], 95.0);
        assert_eq!(values["p99"], 99.0);
    }
}
