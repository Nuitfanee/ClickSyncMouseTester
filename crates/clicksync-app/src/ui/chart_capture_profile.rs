//! Production capture/chart lifecycle and unforced live redraw measurements.
use super::probe::{all_visible, capture, click, prefix_report, press_key, scenario, wait};
use super::*;
use crate::backend::CapturePhase;
use clicksync_core::protocol::CaptureSource;
use gpui::private::anyhow::{anyhow, ensure};

pub(super) fn run(path: PathBuf) -> Result<(), Box<dyn std::error::Error>> {
    run_window(path).map_err(Into::into)
}

fn run_window(path: PathBuf) -> gpui::Result<()> {
    let directory = path
        .parent()
        .ok_or_else(|| anyhow!("chart report directory missing"))?
        .to_owned();
    std::fs::create_dir_all(&directory)?;
    let cursor = crate::capture_lock::cursor_observation()?;
    let outcome = Arc::new(Mutex::new(WindowSmokeReport::default()));
    let saved_outcome = outcome.clone();
    let report = Arc::new(Mutex::new(serde_json::json!({
        "passed": false, "scenarios": [], "frames": [], "capture_checkpoints": [],
        "boundary": "Production window, shared runtime, controlled 8 kHz replay and actual redraw timings. Not physical input latency or appearance acceptance.",
    })));
    let saved_report = report.clone();
    Application::new().with_assets(design::Assets).run(move |cx| {
        editor::TextInput::bind_keys(cx);
        if let Err(error) = design::install_fonts(cx) {
            outcome_error(&outcome, error.to_string());
            cx.quit();
            return;
        }
        let closed = outcome.clone();
        cx.on_window_closed(move |cx| {
            if cx.windows().is_empty() {
                closed.lock().unwrap().window_closed = true;
                cx.quit();
            }
        }).detach();
        let failure = outcome.clone();
        let opened = cx.open_window(
            WindowOptions {
                show: true,
                ..shell::main_options(Bounds::centered(
                    None, size(px(DEFAULT_WIDTH as f32), px(DEFAULT_HEIGHT as f32)), cx,
                ))
            },
            move |window, cx| {
                let parent = cx.new(|cx| {
                    cx.observe_window_activation(window, |host: &mut GpuiHost, window, cx| {
                        host.activation_changed(window);
                        cx.notify();
                    }).detach();
                    let mut host = GpuiHost::new(window, failure.clone(), None, true, cx);
                    host.custom_chrome = true;
                    host.persist_settings = false;
                    host.settings = Settings::default();
                    host.settings.dark = false;
                    host.settings.monitor_metrics = vec![14, 13, 17];
                    host
                });
                let runtime = parent.read(cx).runtime.clone();
                runtime::AppRuntime::bind_driver(&runtime, window, &parent, cx);
                GpuiHost::bind_main_close(window, &parent, cx);
                let main = window.window_handle();
                let measured = parent.clone();
                window.spawn(cx, async move |cx| {
                    let result: gpui::Result<()> = async {
                        activate_smoke_capture_window(main, &measured, cx).await?;
                        main.update(cx, |_, _, cx| measured.update(cx, |host, cx| {
                            let mut services = host.runtime.borrow_mut();
                            if let Some(mut backend) = services.backend.take() {
                                backend.shutdown().map_err(|error| anyhow!(error))?;
                            }
                            services.backend = Some(crate::backend::Backend::spawn_in_process(true)
                                .map_err(|error| anyhow!(error))?);
                            services.worker.shutdown();
                            services.worker = crate::chart::ChartWorker::spawn_with_budget(
                                services.backend.as_ref().unwrap().byte_budget(),
                            );
                            drop(services);
                            host.selected = vec![1];
                            host.selection_initialized = true;
                            cx.notify();
                            Ok::<_, gpui::private::anyhow::Error>(())
                        }))??;
                        wait(main, &measured, |host| {
                            host.runtime.borrow().device_catalog.accepts_selection(&host.selected)
                                && host.runtime.borrow().pending_action().is_none()
                        }, cx).await?;
                        let (handle, child) = next(main, &measured, cx).await?;
                        scenario(&report, "configuration-handoff-with-one-runtime");
                        let position = handle.update(cx, |_, _, cx| child.update(cx, |host, _| {
                            host.workspace.capture_source = Some(CaptureSource::Replay {
                                records_per_device: 80_000, reports_per_second: 8_000, real_time: true,
                            });
                            host.runtime.borrow().backend.as_ref().unwrap()
                                .set_ingestion_delay(Duration::from_millis(500));
                            let bounds = host.workspace.trajectory_bounds;
                            bounds.origin + gpui::point(bounds.size.width * 0.25, bounds.size.height * 0.35)
                        }))?;
                        click(handle, position, cx).await?;
                        Timer::after(Duration::from_millis(300)).await;
                        handle.update(cx, |_, _, cx| {
                            child.read(cx).runtime.borrow().backend.as_ref().unwrap()
                                .set_ingestion_delay(Duration::ZERO);
                        })?;
                        wait(handle, &child, |host| all_visible(host)
                            && host.workspace.latest.as_ref().is_some_and(|p| p.received_records > 800)
                            && host.plot_views().iter().all(|v| v.borrow().report().layer_end >= 800), cx).await?;
                        handle.update(cx, |_, _, cx| {
                            let host = child.read(cx);
                            let prefix = host.workspace.latest.as_ref().unwrap();
                            ensure!(prefix.phase == CapturePhase::Collecting,
                                "first chart appeared only after capture ended");
                            let chart = host.chart.as_ref().unwrap().borrow();
                            let viewport = chart.presented_viewport().unwrap();
                            let x = clicksync_chart::Coordinate::Integer(0).relative_to(viewport.origin.x) / viewport.width;
                            let y = clicksync_chart::Coordinate::Integer(0).relative_to(viewport.origin.y) / viewport.height;
                            ensure!((x - 0.25).abs() < 0.01 && (y - 0.35).abs() < 0.01,
                                "first capture lost its click origin: {x}, {y}");
                            checkpoint(host, "first-start", &report)?;
                            report.lock().unwrap()["first_start"] = serde_json::json!({
                                "received_records": prefix.received_records,
                                "presented_while_collecting": host.plot_views().iter()
                                    .map(|v| v.borrow().report().layer_end).collect::<Vec<_>>(),
                                "origin_fraction": [x, y],
                            });
                            Ok::<_, gpui::private::anyhow::Error>(())
                        })??;
                        profile_live(handle, &child, &directory, &report, cx).await?;
                        let retained = handle.update(cx, |_, _, cx| {
                            child.read(cx).workspace.analysis.as_ref().unwrap().input.clone()
                        })?;
                        handle.update(cx, |_, window, cx| {
                            child.update(cx, |host, cx| {
                                host.return_to_performance_setup(window, cx);
                                ensure!(host.workspace.closing && host.workspace.analysis.is_none()
                                    && host.workspace.monitors.iter().all(Vec::is_empty)
                                    && !host.lock.locked(), "return retained capture/chart ownership");
                                Ok::<_, gpui::private::anyhow::Error>(())
                            })?;
                            window.remove_window();
                            Ok::<_, gpui::private::anyhow::Error>(())
                        })??;
                        activate_smoke_capture_window(main, &measured, cx).await?;
                        wait(main, &measured, |host| {
                            let runtime = host.runtime.borrow();
                            host.workspace.child.is_none() && !runtime.is_closed()
                                && runtime.driver_starts == 1 && runtime.pending_action().is_none()
                                && runtime.status().is_some_and(|status| status.phase == CapturePhase::Paused
                                    && status.generation == retained.generation
                                    && status.received_records == retained.received_records)
                        }, cx).await?;
                        main.update(cx, |_, _, cx| {
                            let host = measured.read(cx);
                            ensure!(host.workspace.analysis.as_ref()
                                .is_some_and(|analysis| Arc::ptr_eq(&analysis.input, &retained)),
                                "return replaced the retained measurement");
                            Ok::<_, gpui::private::anyhow::Error>(())
                        })??;
                        scenario(&report, "return-preserves-records-and-retires-window-resources");
                        let (handle, child) = next(main, &measured, cx).await?;
                        wait(handle, &child, all_visible, cx).await?;
                        handle.update(cx, |_, _, cx| {
                            let host = child.read(cx);
                            ensure!(host.workspace.analysis.as_ref()
                                .is_some_and(|analysis| Arc::ptr_eq(&analysis.input, &retained)),
                                "reopening replaced the retained measurement");
                            checkpoint(host, "reopened", &report)
                        })??;
                        handle.update(cx, |_, _, cx| child.update(cx, |host, _| {
                            host.workspace.capture_source = Some(CaptureSource::Replay {
                                records_per_device: 80_000, reports_per_second: 8_000, real_time: true,
                            });
                        }))?;
                        check_capture_key_guards(handle, &child, cx).await?;
                        scenario(&report, "keyboard-modifiers-repeat-and-menu-guards");
                        press_key(handle, 0x53, cx).await?;
                        wait(handle, &child, |host| host.lock.locked() && all_visible(host)
                            && host.runtime.borrow().status().is_some_and(|s| s.phase == CapturePhase::Collecting
                                && s.generation == retained.generation
                                && s.received_records > retained.received_records), cx).await?;
                        scenario(&report, "keyboard-resume-after-return");
                        handle.update(cx, |_, window, cx| {
                            child.update(cx, |host, cx| host.return_to_performance_setup(window, cx));
                            window.remove_window();
                        })?;
                        activate_smoke_capture_window(main, &measured, cx).await?;
                        wait(main, &measured, |host| {
                            let runtime = host.runtime.borrow();
                            host.workspace.child.is_none() && runtime.pending_action().is_none()
                                && runtime.status().is_some_and(|s| s.phase == CapturePhase::Paused
                                    && s.generation == retained.generation
                                    && s.received_records > retained.received_records)
                        }, cx).await?;
                        scenario(&report, "active-return-pauses-and-drains-without-reset");
                        main.update(cx, |_, _, cx| measured.update(cx, |host, _| {
                            host.selected = vec![2];
                        }))?;
                        let (handle, child) = next(main, &measured, cx).await?;
                        handle.update(cx, |_, _, cx| child.update(cx, |host, _| {
                            host.workspace.capture_source = Some(CaptureSource::Replay {
                                records_per_device: 80_000, reports_per_second: 8_000, real_time: true,
                            });
                        }))?;
                        press_key(handle, 0x53, cx).await?;
                        wait(handle, &child, |host| host.lock.locked() && all_visible(host)
                            && host.runtime.borrow().status().is_some_and(|s| s.phase == CapturePhase::Collecting
                                && s.generation > retained.generation && s.selected_device_keys == [2]
                                && s.received_records > 800), cx).await?;
                        scenario(&report, "changed-device-starts-a-new-session-only-on-capture");
                        handle.update(cx, |_, window, cx| {
                            child.update(cx, |host, cx| host.return_to_performance_setup(window, cx));
                            window.remove_window();
                        })?;
                        activate_smoke_capture_window(main, &measured, cx).await?;
                        wait(main, &measured, |host| host.runtime.borrow().pending_action().is_none()
                            && host.runtime.borrow().status().is_some_and(|s| s.phase == CapturePhase::Paused), cx).await?;
                        let imported = retained.datasets[0].1.clone();
                        main.update(cx, |_, _, cx| measured.update(cx, |host, _| {
                            host.imported = Some((imported.clone(), Some(1600.0)));
                            host.import_identity += 1;
                            host.workspace.analysis = None;
                            host.workspace.groups = vec![crate::product::performance_analysis::Group {
                                key: u32::MAX - 1001, snapshot: imported.clone(),
                                cpi: Some(800.0), identity: "retained-comparison".into(),
                            }];
                            host.workspace.next_group_key = 1001;
                        }))?;
                        let (handle, child) = next(main, &measured, cx).await?;
                        wait(handle, &child, all_visible, cx).await?;
                        handle.update(cx, |_, _, cx| {
                            child.update(cx, |host, cx| {
                                ensure!(host.imported.as_ref().is_some_and(|(snapshot, cpi)|
                                    Arc::ptr_eq(snapshot, &imported) && *cpi == Some(1600.0))
                                    && host.workspace.groups.len() == 1
                                    && host.workspace.next_group_key == 1001,
                                    "opening lost imported measurements or comparison identity");
                                // Inject directory-loss presentation; click the production recovery action.
                                host.selected.clear();
                                host.workspace.preview_status = Some(crate::backend::AppStatus {
                                    generation: host.page_generation,
                                    phase: CapturePhase::Faulted,
                                    capture_fault: Some(crate::backend::CaptureFault::DeviceDisconnected),
                                    error: Some("changed backend diagnostic wording".into()),
                                    ..Default::default()
                                });
                                cx.notify();
                                Ok::<_, gpui::private::anyhow::Error>(())
                            })
                        })??;
                        wait(handle, &child, |host| {
                            host.workspace.recovery_control.get().size.width > px(0.0)
                        }, cx).await?;
                        let recovery = handle.update(cx, |_, _, cx| {
                            child.read(cx).workspace.recovery_control.get().center()
                        })?;
                        click(handle, recovery, cx).await?;
                        ensure!(handle.update(cx, |_, _, _| ()).is_err(),
                            "device recovery left a retired chart window open");
                        main.update(cx, |_, _, cx| {
                            let host = measured.read(cx);
                            let runtime = host.runtime.borrow();
                            ensure!(cx.windows().len() == 1 && host.workspace.child.is_none()
                                && runtime.accepts_receiver(host.receiver)
                                && !runtime.accepts_receiver(child.read(cx).receiver)
                                && runtime.driver_starts == 1,
                                "device recovery did not restore the single-window runtime owner");
                            ensure!(host.imported.as_ref().is_some_and(|(snapshot, cpi)|
                                Arc::ptr_eq(snapshot, &imported) && *cpi == Some(1600.0))
                                && host.workspace.groups.len() == 1
                                && Arc::ptr_eq(&host.workspace.groups[0].snapshot, &imported)
                                && host.workspace.next_group_key == 1001,
                                "return lost imported measurements or comparison identity");
                            Ok::<_, gpui::private::anyhow::Error>(())
                        })??;
                        scenario(&report, "imported-measurements-and-comparisons-survive-window-roundtrip");
                        scenario(&report, "device-recovery-closes-chart-and-restores-configuration-owner");
                        check_keyboard_cold_start(main, &measured, &report, cx).await?;
                        report.lock().unwrap()["passed"] = true.into();
                        Ok(())
                    }.await;
                    if let Err(error) = result {
                        eprintln!("chart capture probe failed: {error}");
                        outcome_error(&failure, error.to_string());
                    }
                    let _ = main.update(cx, |_, window, cx| {
                        measured.update(cx, |host, cx| {
                            host.close_performance_child(cx);
                            host.shutdown();
                        });
                        window.remove_window();
                    });
                    let _ = cx.update(|_, cx| cx.quit());
                }).detach();
                parent
            },
        );
        if let Err(error) = opened {
            outcome_error(&outcome, error.to_string());
            cx.quit();
        }
    });
    let after = crate::capture_lock::cursor_observation()?;
    let lifecycle = saved_outcome.lock().unwrap();
    let mut report = saved_report.lock().unwrap();
    report["closed"] = lifecycle.window_closed.into();
    report["error"] = serde_json::json!(lifecycle.error);
    report["bridge"] = serde_json::json!(lifecycle.bridge);
    report["cursor_restored_on_app_exit"] =
        (cursor.showing == after.showing && cursor.clip == after.clip).into();
    std::fs::write(path, serde_json::to_vec_pretty(&*report)?)?;
    ensure!(
        report["passed"] == true
            && lifecycle.window_closed
            && lifecycle.error.is_none()
            && report["cursor_restored_on_app_exit"] == true
            && lifecycle
                .bridge
                .as_ref()
                .is_some_and(|bridge| bridge.live_surfaces == 0),
        "chart capture profile failed: {:?}",
        lifecycle.error
    );
    Ok(())
}

async fn next(
    main: gpui::AnyWindowHandle,
    parent: &gpui::Entity<GpuiHost>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<(gpui::AnyWindowHandle, gpui::Entity<GpuiHost>)> {
    let (handle, child) = main.update(cx, |_, window, cx| {
        parent.update(cx, |host, cx| {
            let before = host.runtime.borrow().status().unwrap();
            host.open_performance_window(window, cx);
            let opened = host.workspace.child.ok_or_else(|| {
                anyhow!(
                    "configuration did not open chart window: {:?}",
                    host.control_error,
                )
            })?;
            host.open_performance_window(window, cx);
            let child = opened.entity(cx)?;
            ensure!(
                host.workspace.child == Some(opened)
                    && Rc::ptr_eq(&host.runtime, &child.read(cx).runtime)
                    && host.runtime.borrow().driver_starts == 1
                    && !child.read(cx).owns_runtime
                    && !child.read(cx).lock.locked()
                    && host.runtime.borrow().pending_action().is_none()
                    && host
                        .runtime
                        .borrow()
                        .status()
                        .is_some_and(|s| s.phase == before.phase
                            && s.generation == before.generation
                            && s.received_records == before.received_records),
                "window handoff duplicated runtime or changed capture state"
            );
            Ok::<_, gpui::private::anyhow::Error>((opened.into(), child))
        })
    })??;
    activate_smoke_capture_window(handle, &child, cx).await?;
    wait(
        handle,
        &child,
        |host| host.workspace.trajectory_bounds.size.width > px(0.0),
        cx,
    )
    .await?;
    Ok((handle, child))
}

async fn profile_live(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    directory: &std::path::Path,
    report: &Arc<Mutex<serde_json::Value>>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    // Let capture/readback's queued refresh finish before the unforced interval.
    Timer::after(Duration::from_millis(300)).await;
    let (before, polls, renders) = handle.update(cx, |_, window, cx| {
        window.start_frame_timing();
        let host = host.read(cx);
        (
            host.workspace.interaction.as_ref().unwrap().counts(),
            host.runtime.borrow().polls,
            host.outcome.lock().unwrap().frame_count,
        )
    })?;
    let started = std::time::Instant::now();
    // No readback, forced refresh or injected input in the measured interval.
    Timer::after(Duration::from_secs(3)).await;
    let elapsed = started.elapsed().as_secs_f64();
    let modes = handle.update(cx, |_, window, cx| {
        let timings = window.take_frame_timings().unwrap();
        ensure!(timings.omitted == 0, "live frame timing buffer overflow");
        ensure!(!timings.samples.is_empty(), "live capture produced no redraws");
        let host = host.read(cx);
        report.lock().unwrap()["capture_at_measurement_end"] = serde_json::json!({
            "status": host.runtime.borrow().status(), "window_active": window.is_window_active(),
            "locked": host.lock.locked(), "worker": host.runtime.borrow().worker.report(),
        });
        ensure!(host.control_error.is_none(), "live chart error: {:?}", host.control_error);
        checkpoint(host, "measurement-end", report)?;
        ensure!(host.workspace.latest.as_ref().is_some_and(|p| p.phase == CapturePhase::Collecting),
            "capture stopped during the measurement: phase={:?}, active={}, locked={}",
            host.workspace.latest.as_ref().map(|p| p.phase), window.is_window_active(), host.lock.locked());
        let after = host.workspace.interaction.as_ref().unwrap().counts();
        let delta: [u64; 6] = std::array::from_fn(|i| after[i] - before[i]);
        let views = host.plot_views();
        let modes: Vec<_> = views.iter().map(|view| view.borrow().presented_result().unwrap().request.view).collect();
        let counts = host.workspace.monitors[2][0].borrow().presented_result().unwrap();
        ensure!(counts.datasets[0].scene.blocks.iter().any(|b| b.key.kind & (1 << 17) != 0),
            "live count monitor is missing trend lines");
        report.lock().unwrap()["live_profile"] = serde_json::json!({
            "seconds": elapsed, "reports_per_second": 8000,
            "content_rebuilds": delta[0], "host_notifications": delta[5],
            "runtime_polls": host.runtime.borrow().polls - polls,
            "root_renders": host.outcome.lock().unwrap().frame_count - renders,
            "new_presented_results": &delta[1..5],
            "new_results_per_second": delta[1..5].iter().map(|n| *n as f64 / elapsed).collect::<Vec<_>>(),
            "worker": host.runtime.borrow().worker.report(),
            "charts": views.iter().map(|view| view.borrow().report()).collect::<Vec<_>>(),
            "frames": super::page_profile::frame_statistics(timings),
            "scope": "production GPUI / D3D11 window and controlled replay; not physical input latency",
        });
        ensure!(delta[0] == 0, "streaming data rebuilt controls {} times", delta[0]);
        ensure!(delta[1..5].iter().all(|frames| *frames > 0), "a visible live chart stopped publishing");
        Ok::<_, gpui::private::anyhow::Error>(modes)
    })??;
    capture(handle, directory, "profile-live", report, cx).await?;
    let pause = handle.update(cx, |_, _, cx| {
        host.read(cx).workspace.capture_controls[0].get().center()
    })?;
    click(handle, pause, cx).await?;
    wait(
        handle,
        host,
        |host| {
            host.workspace
                .analysis
                .as_ref()
                .is_some_and(|a| a.input.phase == CapturePhase::Paused)
                && host.runtime.borrow().pending_action().is_none()
                && all_visible(host)
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, window, cx| {
        let host = host.read(cx);
        ensure!(
            host.focus.is_focused(window),
            "pause control orphaned keyboard focus"
        );
        checkpoint(host, "paused", report)
    })??;
    capture(handle, directory, "profile-paused", report, cx).await?;
    let paused_sequence = handle.update(cx, |_, _, cx| {
        host.read(cx)
            .workspace
            .latest
            .as_ref()
            .unwrap()
            .received_sequence
    })?;
    press_key(handle, 0x53, cx).await?;
    Timer::after(Duration::from_millis(300)).await;
    handle.update(cx, |_, window, cx| {
        let host = host.read(cx);
        report.lock().unwrap()["resume_observation"] = serde_json::json!({
            "active": window.is_window_active(),
            "locked": host.lock.locked(),
            "pending": host.runtime.borrow().pending_action(),
            "latest": host.workspace.latest.as_ref().map(|p| (p.phase, p.received_sequence)),
            "fixed": host.workspace.analysis.as_ref().map(|a| (a.input.phase, a.input.received_sequence)),
            "layout": host.workspace.interaction.as_ref().map(|frame| frame.layout_report()),
            "targets": host.workspace.targets.iter().map(|r| r.as_ref().map(|r| (r.id, r.prefix.phase, r.prefix.received_sequence))).collect::<Vec<_>>(),
            "presented": host.plot_views().iter().map(|v| v.borrow().presented_result().map(|r| (r.request.id, r.request.prefix.phase, r.request.prefix.received_sequence))).collect::<Vec<_>>(),
            "charts": host.plot_views().iter().map(|v| v.borrow().report()).collect::<Vec<_>>(),
        });
    })?;
    wait(
        handle,
        host,
        |host| {
            host.workspace.analysis.is_none()
                && host.runtime.borrow().pending_action().is_none()
                && host.workspace.latest.as_ref().is_some_and(|p| {
                    p.phase == CapturePhase::Collecting
                        && p.received_sequence > paused_sequence + 800
                })
                && all_visible(host)
                && host.plot_views().iter().all(|view| {
                    view.borrow().presented_result().is_some_and(|result| {
                        result.request.prefix.received_sequence > paused_sequence + 800
                    })
                })
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| checkpoint(host.read(cx), "resumed", report))??;
    scenario(report, "keyboard-resume-after-pause-control");
    let resumed = std::time::Instant::now();
    capture(handle, directory, "profile-resumed", report, cx).await?;
    report.lock().unwrap()["resumed_screenshot_ms"] =
        (resumed.elapsed().as_secs_f64() * 1000.0).into();
    let stop = handle.update(cx, |_, _, cx| {
        host.read(cx).workspace.capture_controls[1].get().center()
    })?;
    click(handle, stop, cx).await?;
    wait(
        handle,
        host,
        |host| {
            host.workspace
                .analysis
                .as_ref()
                .is_some_and(|a| a.input.phase == CapturePhase::Stopped)
                && host.runtime.borrow().pending_action().is_none()
                && all_visible(host)
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, window, cx| {
        let host = host.read(cx);
        ensure!(
            host.focus.is_focused(window),
            "stop control orphaned keyboard focus"
        );
        let stopped: Vec<_> = host
            .plot_views()
            .iter()
            .map(|view| view.borrow().presented_result().unwrap().request.view)
            .collect();
        ensure!(modes == stopped, "stopping switched chart viewport policy");
        checkpoint(host, "stopped", report)?;
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    capture(handle, directory, "profile-stopped", report, cx).await?;
    let generation = handle.update(cx, |_, _, cx| {
        host.read(cx).workspace.latest.as_ref().unwrap().generation
    })?;
    press_key(handle, 0x53, cx).await?;
    wait(
        handle,
        host,
        |host| {
            host.workspace.latest.as_ref().is_some_and(|p| {
                p.phase == CapturePhase::Collecting
                    && p.generation > generation
                    && p.received_records > 800
            }) && host.runtime.borrow().pending_action().is_none()
                && all_visible(host)
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        checkpoint(host.read(cx), "restarted", report)
    })??;
    capture(handle, directory, "profile-restarted", report, cx).await?;
    scenario(report, "canvas-cold-start-resume-and-fresh-restart");
    scenario(report, "keyboard-new-record-after-stop-control");
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.capture_command("stop");
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| {
            host.workspace
                .analysis
                .as_ref()
                .is_some_and(|a| a.input.phase == CapturePhase::Stopped)
                && host.runtime.borrow().pending_action().is_none()
                && all_visible(host)
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        checkpoint(host.read(cx), "restarted-stopped", report)
    })??;
    check_editor_capture_guard(handle, host, cx).await?;
    scenario(report, "keyboard-editor-retains-focus-and-blocks-capture");
    let reset = handle.update(cx, |_, _, cx| {
        host.read(cx).workspace.capture_controls[2].get().center()
    })?;
    click(handle, reset, cx).await?;
    wait(
        handle,
        host,
        |host| {
            host.runtime.borrow().pending_action().is_none()
                && host
                    .runtime
                    .borrow()
                    .status()
                    .is_some_and(|s| s.phase == CapturePhase::Empty)
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, window, cx| {
        ensure!(
            host.read(cx).focus.is_focused(window),
            "reset control orphaned keyboard focus"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    press_key(handle, 0x53, cx).await?;
    wait(
        handle,
        host,
        |host| {
            host.runtime.borrow().pending_action().is_none()
                && host.workspace.latest.as_ref().is_some_and(|p| {
                    p.phase == CapturePhase::Collecting
                        && p.generation > generation
                        && p.received_records > 800
                })
                && all_visible(host)
        },
        cx,
    )
    .await?;
    press_key(handle, 0x53, cx).await?;
    wait(
        handle,
        host,
        |host| {
            !host.lock.locked()
                && host.runtime.borrow().pending_action().is_none()
                && host
                    .workspace
                    .analysis
                    .as_ref()
                    .is_some_and(|a| a.input.phase == CapturePhase::Paused)
                && all_visible(host)
        },
        cx,
    )
    .await?;
    scenario(report, "keyboard-new-record-after-reset-and-keyboard-pause");
    Ok(())
}

async fn check_keyboard_cold_start(
    main: gpui::AnyWindowHandle,
    parent: &gpui::Entity<GpuiHost>,
    report: &Arc<Mutex<serde_json::Value>>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    activate_smoke_capture_window(main, parent, cx).await?;
    main.update(cx, |_, _, cx| {
        parent.update(cx, |host, cx| {
            host.imported = None;
            host.comparison = None;
            host.workspace.groups.clear();
            host.import_identity += 1;
            host.capture_command("reset");
            cx.notify();
        });
    })?;
    wait(
        main,
        parent,
        |host| {
            host.runtime.borrow().pending_action().is_none()
                && host
                    .runtime
                    .borrow()
                    .status()
                    .is_some_and(|status| status.phase == CapturePhase::Empty)
        },
        cx,
    )
    .await?;
    let (handle, child) = next(main, parent, cx).await?;
    let generation = handle.update(cx, |_, _, cx| {
        child.update(cx, |host, _| {
            ensure!(
                host.plot_views().is_empty()
                    && host.workspace.analysis.is_none()
                    && !host.lock.locked(),
                "cold keyboard start did not open an empty chart window"
            );
            host.workspace.capture_source = Some(CaptureSource::Replay {
                records_per_device: 80_000,
                reports_per_second: 8_000,
                real_time: true,
            });
            Ok::<_, gpui::private::anyhow::Error>(
                host.runtime.borrow().status().unwrap().generation,
            )
        })
    })??;
    press_key(handle, 0x53, cx).await?;
    wait(
        handle,
        &child,
        |host| {
            host.lock.locked()
                && all_visible(host)
                && host.workspace.latest.as_ref().is_some_and(|prefix| {
                    prefix.phase == CapturePhase::Collecting
                        && prefix.generation > generation
                        && prefix.received_records > 800
                })
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        let host = child.read(cx);
        report.lock().unwrap()["keyboard_cold_start"] = serde_json::json!({
            "before_generation": generation,
            "prefix": probe::prefix_report(host.workspace.latest.as_ref().unwrap())?,
        });
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    scenario(report, "keyboard-cold-start");
    Ok(())
}

async fn check_capture_key_guards(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    let before = handle.update(cx, |_, _, cx| {
        host.read(cx).runtime.borrow().status().unwrap()
    })?;
    handle.update(cx, |_, window, cx| {
        for key in ["ctrl-s", "alt-s", "shift-s", "cmd-s"] {
            window.dispatch_keystroke(gpui::Keystroke::parse(key).unwrap(), cx);
            let host = host.read(cx);
            ensure!(
                !host.lock.locked() && host.runtime.borrow().pending_action().is_none(),
                "modified S triggered capture: {key}"
            );
        }
        host.update(cx, |host, cx| {
            host.keyboard(
                &gpui::KeyDownEvent {
                    keystroke: gpui::Keystroke::parse("s").unwrap(),
                    is_held: true,
                },
                window,
                cx,
            );
            ensure!(
                !host.lock.locked() && host.runtime.borrow().pending_action().is_none(),
                "repeated S triggered capture"
            );
            host.workspace.data_menu = true;
            cx.notify();
            Ok::<_, gpui::private::anyhow::Error>(())
        })?;
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    press_key(handle, 0x53, cx).await?;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        ensure!(
            host.workspace.data_menu
                && !host.lock.locked()
                && host.runtime.borrow().pending_action().is_none()
                && host
                    .runtime
                    .borrow()
                    .status()
                    .is_some_and(|s| s.phase == before.phase
                        && s.generation == before.generation
                        && s.received_records == before.received_records),
            "S bypassed the open data menu"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    press_key(handle, 0x1B, cx).await?;
    Ok(())
}

async fn check_editor_capture_guard(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.open_plot_detail(11, false);
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| host.workspace.detail_editors.len() == 2 && all_visible(host),
        cx,
    )
    .await?;
    handle.update(cx, |_, window, cx| {
        host.read(cx).workspace.detail_editors[0]
            .read(cx)
            .focus_handle
            .focus(window);
    })?;
    press_key(handle, 0x53, cx).await?;
    handle.update(cx, |_, window, cx| {
        let host = host.read(cx);
        ensure!(
            host.workspace.detail_editors[0]
                .read(cx)
                .focus_handle
                .is_focused(window)
                && !host.lock.locked()
                && host.runtime.borrow().pending_action().is_none()
                && host
                    .runtime
                    .borrow()
                    .status()
                    .is_some_and(|s| s.phase == CapturePhase::Stopped),
            "workspace stole editor focus or S started capture while editing"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    press_key(handle, 0x1B, cx).await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.show_plot_overview();
            cx.notify();
        })
    })?;
    wait(handle, host, all_visible, cx).await?;
    Ok(())
}

fn checkpoint(
    host: &GpuiHost,
    name: &str,
    report: &Arc<Mutex<serde_json::Value>>,
) -> gpui::Result<()> {
    let status = host
        .runtime
        .borrow()
        .status()
        .ok_or_else(|| anyhow!("capture status missing"))?;
    ensure!(
        status.generation >= host.page_generation && capture_status_error(&status).is_none(),
        "unhealthy capture at {name}: {status:?}"
    );
    let prefix = host
        .workspace
        .latest
        .as_ref()
        .ok_or_else(|| anyhow!("capture prefix missing"))?;
    let pending = host.runtime.borrow().pending_action();
    ensure!(
        pending.is_none(),
        "capture command still pending at {name}: {pending:?}"
    );
    let frozen = matches!(prefix.phase, CapturePhase::Paused | CapturePhase::Stopped);
    ensure!(
        status.generation == prefix.generation
            && status.phase == prefix.phase
            && prefix.received_records <= status.received_records
            && status.sent <= status.captured,
        "inconsistent capture scope at {name}: {status:?}"
    );
    if frozen {
        ensure!(
            status.received_records == prefix.received_records
                && status.captured == status.sent
                && status.sent == status.received_records
                && host.workspace.analysis.as_ref().is_some_and(|analysis| {
                    analysis.input.received_sequence == prefix.received_sequence
                        && analysis.input.phase == prefix.phase
                }),
            "undrained or stale final capture at {name}: {status:?}"
        );
        prefix_report(&host.workspace.analysis.as_ref().unwrap().input)?;
    }
    for view in host.plot_views() {
        let view = view.borrow();
        let result = view
            .presented_result()
            .ok_or_else(|| anyhow!("chart result missing"))?;
        super::probe::ensure_complete_prefix(&result.request.prefix)?;
        ensure!(
            result.datasets.iter().all(|dataset| {
                dataset.scene.state.data_version.is_some_and(|version| {
                    version.integrity == clicksync_chart::Integrity::Complete
                })
            }),
            "chart marked healthy input incomplete at {name}"
        );
        ensure!(
            !frozen
                || (result.request.prefix.received_sequence == prefix.received_sequence
                    && result.datasets.iter().all(|dataset| {
                        prefix.datasets.iter().any(|(key, snapshot)| {
                            *key == dataset.device_key
                                && dataset.scene.state.processed_end == snapshot.event_count()
                        })
                    })),
            "chart did not present the full frozen prefix at {name}"
        );
    }
    report.lock().unwrap()["capture_checkpoints"]
        .as_array_mut()
        .unwrap()
        .push(
            serde_json::json!({ "name": name, "status": status, "pending_action": pending, "prefix": prefix_report(prefix)? }),
        );
    Ok(())
}
