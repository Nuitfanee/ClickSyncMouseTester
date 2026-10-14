use super::probe::{scenario, wait};
use super::*;
use crate::backend::CapturePhase;
use clicksync_capture::observation::MotionReport;
use clicksync_core::{RawMouseMovementMode, RawMouseRecord};
use gpui::private::anyhow::{anyhow, ensure};

pub(super) fn run(path: PathBuf) -> Result<(), Box<dyn std::error::Error>> {
    run_window(path).map_err(Into::into)
}
fn run_window(path: PathBuf) -> gpui::Result<()> {
    let directory = path
        .parent()
        .ok_or_else(|| anyhow!("business report directory missing"))?
        .to_owned();
    std::fs::create_dir_all(&directory)?;
    let cursor = crate::capture_lock::cursor_observation()?;
    let report = Arc::new(Mutex::new(serde_json::json!({
        "passed":false,"error":null,"scenarios":[],
        "boundary":"Production page receiver and owner/source handshake with controlled event-by-event motion and hardware-composed GPUI. Physical device binding, high-rate input and final page visuals remain unverified."
    })));
    let saved_report = report.clone();
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
                        host.page = Page::Sensitivity;
                        host
                    });
                    let runtime = host.read(cx).runtime.clone();
                    runtime::AppRuntime::bind_driver(&runtime, window, &host, cx);
                    GpuiHost::bind_main_close(window, &host, cx);
                    let handle = window.window_handle();
                    let measured = host.clone();
                    window
                        .spawn(cx, async move |cx| {
                            match exercise(handle, &measured, &report, cx).await {
                                Ok(()) => report.lock().unwrap()["passed"] = true.into(),
                                Err(error) => {
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
    let mut report = saved_report.lock().unwrap();
    report["cursor_restored"] =
        (cursor.clip == after.clip && cursor.showing == after.showing).into();
    report["bridge"] = serde_json::to_value(&saved_outcome.lock().unwrap().bridge)?;
    if let Some(error) = &saved_outcome.lock().unwrap().error {
        report["error"] = error.clone().into();
    }
    std::fs::write(path, serde_json::to_vec_pretty(&*report)?)?;
    ensure!(
        report["passed"] == true && report["cursor_restored"] == true && report["error"].is_null(),
        "business smoke failed: {}",
        report["error"]
    );
    Ok(())
}
fn motion(key: u32, ticks: u64, dx: i32) -> MotionReport {
    MotionReport {
        device_key: key,
        record: RawMouseRecord::new(dx, 1, ticks, 0, 0, RawMouseMovementMode::Relative),
    }
}
async fn exercise(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    report: &Arc<Mutex<serde_json::Value>>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    activate_smoke_capture_window(handle, host, cx).await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            if let Some(mut backend) = host.runtime.borrow_mut().backend.take() {
                backend.shutdown().map_err(|error| anyhow!(error))?;
            }
            host.runtime.borrow_mut().backend = Some(
                crate::backend::Backend::spawn_in_process(true).map_err(|error| anyhow!(error))?,
            );
            host.begin_device_binding(0);
            cx.notify();
            Ok::<_, gpui::private::anyhow::Error>(())
        })
    })??;
    wait(
        handle,
        host,
        |host| host.runtime.borrow().pending_action().is_none(),
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        host.runtime
            .borrow()
            .backend
            .as_ref()
            .unwrap()
            .replay_motion(vec![motion(1, 100, 3), motion(2, 100, 8)])
            .map_err(|error| anyhow!(error))
    })??;
    wait(
        handle,
        host,
        |host| host.business_input.bindings[0] == Some(1),
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.begin_device_binding(1);
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| host.runtime.borrow().pending_action().is_none(),
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        host.runtime
            .borrow()
            .backend
            .as_ref()
            .unwrap()
            .replay_motion(vec![motion(1, 200, 4), motion(2, 200, 9)])
            .map_err(|error| anyhow!(error))
    })??;
    wait(
        handle,
        host,
        |host| host.selected == vec![1, 2] && host.business_input.bindings == [Some(1), Some(2)],
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        let status = host.runtime.borrow().status().unwrap();
        ensure!(
            !host.lock.locked() && status.received_sequence == 0 && status.captured == 0,
            "binding consumed capture or cursor ownership"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    scenario(
        report,
        "production-binding-first-event-exclusion-and-independent-business-permission",
    );
    sensitivity_swipe(handle, host, report, cx).await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.navigate(Page::Calibration);
            cx.notify();
        })
    })?;
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
            host.capture_command("start");
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| host.runtime.borrow().pending_action().is_none() && host.lock.locked(),
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        host.runtime
            .borrow()
            .backend
            .as_ref()
            .unwrap()
            .replay_motion(vec![
                motion(2, 300, 13),
                motion(1, 300, 99),
                motion(2, 300, 17),
                motion(2, 301, 21),
            ])
            .map_err(|error| anyhow!(error))
    })??;
    wait(
        handle,
        host,
        |host| {
            host.business_input
                .first
                .is_some_and(|first| first.device_key == 2)
                && host.selected == vec![2]
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.capture_command("pause");
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| {
            host.last_phase == CapturePhase::Paused
                && !host.lock.locked()
                && host.runtime.borrow().pending_action().is_none()
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        let prefix = host
            .runtime
            .borrow()
            .backend
            .as_ref()
            .unwrap()
            .capture_prefix(Duration::from_secs(3))
            .map_err(|error| anyhow!(error))?;
        super::probe::ensure_complete_prefix(&prefix)?;
        ensure!(
            prefix.received_sequence == 3
                && prefix.datasets[0].0 == 2
                && prefix.datasets[0].1.event_at(0).unwrap().raw_delta_x == 13,
            "UI route missed first calibration input"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    scenario(
        report,
        "production-calibration-source-arm-first-report-and-pause-drain",
    );
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.navigate(Page::Sensitivity);
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| host.runtime.borrow().pending_action().is_none(),
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.begin_device_binding(0);
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| host.runtime.borrow().pending_action().is_none(),
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.navigate(Page::Keys);
            cx.notify();
        })
    })?;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        host.runtime
            .borrow()
            .backend
            .as_ref()
            .unwrap()
            .replay_motion(vec![motion(1, 400, 5)])
            .map_err(|error| anyhow!(error))
    })??;
    Timer::after(Duration::from_millis(120)).await;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        ensure!(
            host.business_input.first.is_none()
                && host.business_input.bindings == [None, None]
                && host.runtime.borrow().pending_motion.is_none()
                && host.runtime.borrow().driver_starts == 1,
            "old receiver observation reached another page or added a driver"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    scenario(
        report,
        "production-page-exit-cancels-observation-and-keeps-one-runtime-driver",
    );
    wait(
        handle,
        host,
        |host| {
            host.runtime.borrow().pending_action().is_none()
                && host.runtime.borrow().status().is_some_and(|status| {
                    status.phase == CapturePhase::Empty && status.generation >= host.page_generation
                })
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, window, cx| {
        host.update(cx, |host, cx| {
            use clicksync_core::protocol::ControlInput;
            use gpui::EntityInputHandler;
            let key = ControlInput::Keyboard {
                virtual_key: 65,
                scan_code: 30,
                extended: false,
                down: true,
            };
            let release = ControlInput::Keyboard {
                virtual_key: 65,
                scan_code: 30,
                extended: false,
                down: false,
            };
            host.pick_custom_key(window, cx);
            host.keys_input(&key, 100.0, true, false);
            host.keys_input(&release, 101.0, true, false);
            host.keys_input(&key, 200.0, true, false);
            host.keys_input(&key, 201.0, true, false);
            host.keys_input(&release, 220.0, true, false);
            host.keys_input(&key, 300.0, true, true);
            host.keys_input(&key, 310.0, false, false);
            ensure!(
                host.keys
                    .selected
                    .is_some_and(|identity| identity.virtual_key == 65)
                    && host.keys.custom.down_count == 1
                    && host.keys.custom.up_count == 1
                    && !host.keys.custom.is_pressed,
                "key routing counted picking, held or filtered input"
            );
            let mouse = ControlInput::Mouse { flags: 1, data: 0 };
            host.keys_input(&mouse, 400.0, false, false);
            host.keys_input(&mouse, 410.0, true, false);
            host.keys_input(&mouse, 411.0, true, false);
            host.keys_input(
                &ControlInput::Mouse { flags: 2, data: 0 },
                430.0,
                true,
                false,
            );
            host.keys_input(
                &ControlInput::Mouse {
                    flags: 0x0400,
                    data: (-120i16) as u16,
                },
                440.0,
                true,
                false,
            );
            ensure!(
                host.keys.buttons[0].down_count == 1
                    && host.keys.buttons[0].up_count == 1
                    && host.keys.wheel_down == 1,
                "mouse/wheel routing bypassed key-page guards"
            );
            scenario(
                report,
                "keys-held-input-editing-activation-and-wheel-routing",
            );

            host.begin_edit(5, window, cx);
            host.editors[5].update(cx, |field, cx| {
                field.select_entire_value(cx);
                field.replace_text_in_range(None, "42.5", window, cx);
            });
            host.commit_edit(cx);
            ensure!(
                host.settings.key_threshold_ms == 42.5
                    && host.edit.is_none()
                    && host.keys.key_threshold_ms == 42.5,
                "threshold edit did not commit"
            );
            host.begin_edit(5, window, cx);
            host.editors[5].update(cx, |field, cx| {
                field.replace_text_in_range(None, "99", window, cx);
            });
            host.cancel_edit(cx);
            ensure!(
                host.settings.key_threshold_ms == 42.5
                    && host.editors[5].read(cx).content.as_ref() == "42.5",
                "cancel committed a draft"
            );
            host.begin_edit(0, window, cx);
            host.editors[0].update(cx, |field, cx| {
                field.replace_text_in_range(None, "0", window, cx);
            });
            let saved = host.settings.cpi;
            host.commit_edit(cx);
            ensure!(
                host.settings.cpi == saved
                    && host.control_error.is_some()
                    && host.editors[0].read(cx).content.as_ref() == saved.to_string(),
                "invalid DPI overwrote the valid value or left an invalid editor"
            );
            host.control_error = None;
            scenario(report, "editor-commit-cancel-and-invalid-dpi-restore");
            host.navigate(Page::Polling);
            ensure!(
                host.keys.custom.down_count == 0
                    && host.keys.buttons[0].down_count == 0
                    && host.keys.wheel_down == 0
                    && host.edit.is_none(),
                "page exit retained key counts or an editor"
            );
            scenario(report, "page-exit-clears-key-state-and-editor");
            Ok::<_, gpui::private::anyhow::Error>(())
        })
    })??;
    polling::probe::exercise(handle, host, report, cx).await?;
    Ok(())
}

async fn sensitivity_swipe(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    report: &Arc<Mutex<serde_json::Value>>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    use gpui::EntityInputHandler;

    handle.update(cx, |_, window, cx| {
        host.update(cx, |host, cx| {
            host.sensitivity_stage(sensitivity::Step::Measure, window);
            ensure!(
                host.sensitivity.step == sensitivity::Step::Setup,
                "empty target DPI allowed measurement"
            );
            host.sensitivity
                .editor
                .as_ref()
                .ok_or_else(|| anyhow!("missing target DPI editor"))?
                .clone()
                .update(cx, |field, cx| {
                    field.replace_text_in_range(None, "254", window, cx);
                });
            Ok::<_, gpui::private::anyhow::Error>(())
        })
    })??;
    wait(
        handle,
        host,
        |host| host.sensitivity.target_dpi == "254",
        cx,
    )
    .await?;
    handle.update(cx, |_, window, cx| {
        host.update(cx, |host, cx| {
            host.sensitivity.capture_source =
                Some(clicksync_core::protocol::CaptureSource::Replay {
                    records_per_device: 80_000,
                    reports_per_second: 1000,
                    real_time: true,
                });
            host.sensitivity_stage(sensitivity::Step::Measure, window);
            host.start_round();
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| {
            host.runtime.borrow().pending_action().is_none()
                && host
                    .sensitivity
                    .frame
                    .as_ref()
                    .is_some_and(|f| f.active.is_some())
        },
        cx,
    )
    .await?;
    let (ready, frequency) = handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        host.runtime
            .borrow()
            .backend
            .as_ref()
            .unwrap()
            .replay_motion(Vec::new())
            .map_err(|error| anyhow!(error))?;
        let prefix = host
            .runtime
            .borrow()
            .backend
            .as_ref()
            .unwrap()
            .latest_prefix()
            .unwrap();
        Ok::<_, gpui::private::anyhow::Error>((
            host.round_started + clicksync_chart::analysis::sensitivity::PREPARATION_MS,
            prefix.datasets[0].1.timestamp_frequency(),
        ))
    })??;
    // Generate a completed historical swipe, never future-dated Raw Input.
    let remaining = (ready + 2100.0 - crate::capture_lock::now_ms()).max(0.0);
    Timer::after(Duration::from_secs_f64(remaining / 1000.0)).await;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        let reports = (0..210)
            .flat_map(|i| {
                let ticks = ((ready + i as f64 * 10.0) * frequency as f64 / 1000.0).round() as u64;
                [(1, 10), (2, 5)].map(|(device_key, dx)| MotionReport {
                    device_key,
                    record: RawMouseRecord::new(dx, 0, ticks, 0, 0, RawMouseMovementMode::Relative),
                })
            })
            .collect();
        host.runtime
            .borrow()
            .backend
            .as_ref()
            .unwrap()
            .replay_motion(reports)
            .map_err(|error| anyhow!(error))
    })??;
    wait(
        handle,
        host,
        |host| {
            host.last_phase == CapturePhase::Paused
                && host.runtime.borrow().pending_action().is_none()
                && !host.sensitivity.requested
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        let frame = host
            .sensitivity
            .frame
            .as_ref()
            .ok_or_else(|| anyhow!("missing matching frame"))?;
        ensure!(
            frame.failure == clicksync_chart::analysis::sensitivity::Failure::None,
            "{frame:?}; worker consumed {:?} reports",
            host.domain_frame.as_ref().map(|frame| frame.processed)
        );
        ensure!(
            frame
                .result
                .as_ref()
                .is_some_and(|r| r.recommended_target_dpi == 508),
            "one swipe did not publish its final DPI: {frame:?}"
        );
        ensure!(!host.lock.locked(), "sensitivity acquired cursor ownership");
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            let healthy = host.runtime.borrow().status().unwrap();
            let result = host.domain_frame.clone().unwrap();
            for (step, missing, requested) in [
                (sensitivity::Step::Setup, [true, false], false),
                (sensitivity::Step::Measure, [false, true], false),
                (sensitivity::Step::Measure, [true; 2], true),
            ] {
                host.business_input.bindings = [Some(1), Some(2)];
                host.selected = vec![1, 2];
                host.sensitivity.step = step;
                host.sensitivity.disconnected = [false; 2];
                host.sensitivity.expired = false;
                host.sensitivity.target_dpi = "254".into();
                host.sensitivity.frame = Some(result.sensitivity.clone());
                host.sensitivity.requested = requested;
                host.sensitivity.copied = true;
                let reset = host.sensitivity_reset;
                let cancelled = host.round_cancelled;
                let mut disconnected = healthy.clone();
                disconnected
                    .devices
                    .retain(|device| device.key != 1 || !missing[0]);
                disconnected
                    .devices
                    .retain(|device| device.key != 2 || !missing[1]);
                let mut old = disconnected.clone();
                old.generation = host.page_generation.saturating_sub(1);
                ensure!(
                    !host.sensitivity_status(&old, cx)
                        && host.business_input.bindings == [Some(1), Some(2)],
                    "old-generation status cleared current bindings"
                );
                ensure!(
                    host.sensitivity_status(&disconnected, cx),
                    "device removal did not update sensitivity"
                );
                let expected = [(!missing[0]).then_some(1), (!missing[1]).then_some(2)];
                ensure!(
                    host.business_input.bindings == expected
                        && host.selected == expected.into_iter().flatten().collect::<Vec<_>>()
                        && host.sensitivity.disconnected == missing
                        && host.sensitivity.frame.is_none()
                        && host.sensitivity.expired
                        && !host.sensitivity.requested
                        && !host.sensitivity.copied
                        && host.sensitivity_reset == reset + 1
                        && host.round_cancelled == cancelled + 1,
                    "disconnect retained a binding, result, or round identity"
                );
                ensure!(
                    host.sensitivity.target_dpi == if missing[1] { "" } else { "254" },
                    "disconnect cleared the wrong DPI input"
                );
                ensure!(
                    !host.accept_business_frame(result.clone())
                        && !host.sensitivity_status(&disconnected, cx)
                        && !host.sensitivity_status(&healthy, cx)
                        && host.business_input.bindings == expected
                        && !host.sensitivity_can_start(),
                    "late result or reconnection restored an invalid binding"
                );
            }
            host.business_input.bindings = [Some(1), Some(2)];
            host.selected = vec![1, 2];
            host.sensitivity.disconnected = [false; 2];
            host.sensitivity.target_dpi = "254".into();
            host.sensitivity
                .editor
                .as_ref()
                .unwrap()
                .update(cx, |field, cx| field.set_value("254", cx));
            cx.notify();
            Ok::<_, gpui::private::anyhow::Error>(())
        })
    })??;
    scenario(
        report,
        "sensitivity-disconnection-clears-binding-and-rejects-stale-result",
    );
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.reset_measurements();
            host.start_round();
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| {
            host.runtime.borrow().pending_action().is_none()
                && host
                    .sensitivity
                    .frame
                    .as_ref()
                    .is_some_and(|f| f.active.is_some())
        },
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.cancel_sensitivity_round();
            cx.notify();
        })
    })?;
    wait(
        handle,
        host,
        |host| host.runtime.borrow().pending_action().is_none(),
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        ensure!(
            !host.sensitivity.requested
                && host
                    .sensitivity
                    .frame
                    .as_ref()
                    .is_none_or(|f| f.active.is_none() && f.result.is_none()),
            "cancelled attempt accepted a late matching frame"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    scenario(
        report,
        "sensitivity-single-swipe-result-auto-pause-and-cancelled-retry",
    );
    Ok(())
}
