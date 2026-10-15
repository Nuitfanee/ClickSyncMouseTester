//! Exercise the production mouse routes, including release through the Win32 callback.
use super::*;
use gpui::{Pixels, Point};

async fn release(
    handle: gpui::AnyWindowHandle,
    position: Point<Pixels>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    handle.update(cx, |_, window, _| unsafe {
        use windows::Win32::{
            Foundation::LPARAM,
            UI::WindowsAndMessaging::{PostMessageW, WM_LBUTTONUP},
        };
        let x = (f32::from(position.x) * window.scale_factor()).round() as u16;
        let y = (f32::from(position.y) * window.scale_factor()).round() as u16;
        PostMessageW(
            Some(shell::hwnd(window)),
            WM_LBUTTONUP,
            Default::default(),
            LPARAM(((u32::from(y) << 16) | u32::from(x)) as isize),
        )
    })??;
    Timer::after(Duration::from_millis(100)).await;
    Ok(())
}

pub(super) async fn verify(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<serde_json::Value> {
    let prefix = hover::prefix()?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.open_plot_detail(17, false); // XY counts.
            host.clear_analysis_range();
            cx.notify();
        });
    })?;
    settle(handle, host, prefix.clone(), cx).await?;
    Timer::after(Duration::from_millis(250)).await;
    let processed = handle.update(cx, |_, _, cx| {
        host.read(cx)
            .runtime
            .borrow()
            .worker
            .report()
            .processed_targets
    })?;
    for shift in [true, false] {
        Timer::after(Duration::from_millis(550)).await;
        let (view, start, end, viewport) = handle.update(cx, |_, _, cx| {
            let view = host.read(cx).analysis_chart().unwrap();
            // Pan left: moving right from time zero correctly clamps at the timeline origin.
            let start = hover::position_at(&view.borrow(), 20);
            let end = hover::position_at(&view.borrow(), 8);
            let viewport = view.borrow().viewport();
            (view, start, end, viewport)
        })?;
        let keyboard = handle.update(cx, |_, window, _| unsafe {
            use windows::Win32::{
                Foundation::{LPARAM, WPARAM},
                UI::{
                    Input::KeyboardAndMouse::{
                        GetKeyboardState, SetKeyboardState, VK_LBUTTON, VK_SHIFT,
                    },
                    WindowsAndMessaging::{PostMessageW, WM_LBUTTONDOWN, WM_MOUSEMOVE},
                },
            };
            let mut saved = [0_u8; 256];
            GetKeyboardState(&mut saved)?;
            let mut state = saved;
            state[VK_SHIFT.0 as usize] = if shift { 0x80 } else { 0 };
            state[VK_LBUTTON.0 as usize] = 0x80;
            SetKeyboardState(&state)?;
            for (message, position) in [(WM_LBUTTONDOWN, start), (WM_MOUSEMOVE, end)] {
                let x = (f32::from(position.x) * window.scale_factor()).round() as u16;
                let y = (f32::from(position.y) * window.scale_factor()).round() as u16;
                PostMessageW(
                    Some(shell::hwnd(window)),
                    message,
                    WPARAM(1),
                    LPARAM(((u32::from(y) << 16) | u32::from(x)) as isize),
                )?;
            }
            Ok::<_, windows::core::Error>(saved)
        })??;
        Timer::after(Duration::from_millis(100)).await;
        handle.update(cx, |_, _, _| unsafe {
            windows::Win32::UI::Input::KeyboardAndMouse::SetKeyboardState(&keyboard)
        })??;
        handle.update(cx, |_, _, _| {
            ensure!(view.borrow().is_dragging(), "mouse-down missed pan route");
            Ok::<_, gpui::private::anyhow::Error>(())
        })??;
        // Also exercise on_mouse_up_out for a normal drag.
        release(
            handle,
            if shift {
                end
            } else {
                gpui::point(px(5.0), px(5.0))
            },
            cx,
        )
        .await?;
        handle.update(cx, |_, _, cx| {
            let host = host.read(cx);
            ensure!(
                !view.borrow().is_dragging(),
                "release kept interaction alive"
            );
            ensure!(
                host.workspace.analysis.as_ref().unwrap().range.is_none(),
                "drag changed the analysis range"
            );
            ensure!(view.borrow().viewport() != viewport, "drag did not pan");
            ensure!(
                host.runtime.borrow().worker.report().processed_targets == processed,
                "drag recomputed historical inputs (shift={shift})"
            );
            Ok::<_, gpui::private::anyhow::Error>(())
        })??;
        handle.update(cx, |_, _, cx| {
            host.update(cx, |host, cx| {
                view.borrow_mut().fit_focus();
                host.workspace
                    .interaction
                    .as_ref()
                    .unwrap()
                    .notify_plots(cx);
            });
        })?;
        settle(handle, host, prefix.clone(), cx).await?;
    }
    let views = handle.update(cx, |_, _, cx| host.read(cx).workspace_plot_views(0))?;
    handle.update(cx, |_, _, cx| {
        for view in &views {
            view.borrow_mut()
                .set_resource_limits(clicksync_chart::gpu::ResourceLimits {
                    layer_bytes: 1,
                    ..Default::default()
                });
        }
        host.update(cx, |host, cx| {
            host.workspace
                .interaction
                .as_ref()
                .unwrap()
                .notify_plots(cx);
        });
    })?;
    Timer::after(Duration::from_millis(200)).await;
    handle.update(cx, |_, window, _| {
        ensure!(
            views.iter().all(|view| view.borrow().capacity_blocked()),
            "capacity rejection was not surfaced"
        );
        window.start_frame_timing();
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    Timer::after(Duration::from_millis(300)).await;
    let blocked_frames = handle.update(cx, |_, window, _| {
        let frames = window.take_frame_timings().unwrap();
        ensure!(
            frames.samples.len() <= 2 && frames.omitted == 0,
            "permanent capacity failure kept requesting frames"
        );
        Ok::<_, gpui::private::anyhow::Error>(frames.samples.len())
    })??;
    handle.update(cx, |_, _, cx| {
        for view in &views {
            view.borrow_mut()
                .set_resource_limits(view_limits(0, views.len(), 3));
        }
        host.update(cx, |host, cx| {
            host.workspace
                .interaction
                .as_ref()
                .unwrap()
                .notify_plots(cx);
        });
    })?;
    settle(handle, host, prefix.clone(), cx).await?;
    ensure!(
        views
            .iter()
            .all(|view| !view.borrow().capacity_blocked() && view.borrow().ready()),
        "capacity change did not recover the complete group"
    );
    let catch_up_ms = verify_group_catch_up(handle, host, &views, cx).await?;
    settle(handle, host, prefix, cx).await?;
    Timer::after(Duration::from_millis(250)).await;
    handle.update(cx, |_, window, _| window.start_frame_timing())?;
    Timer::after(Duration::from_millis(300)).await;
    let settled_frames = handle.update(cx, |_, window, _| {
        let frames = window.take_frame_timings().unwrap();
        ensure!(
            frames.samples.len() <= 2 && frames.omitted == 0,
            "converged group kept requesting frames"
        );
        Ok::<_, gpui::private::anyhow::Error>(frames.samples.len())
    })??;
    Ok(serde_json::json!({
        "passed": true, "xy_counts_shift_pan": true, "pan_release_outside": true,
        "capacity_recovery": true, "capacity_blocked_redraws": blocked_frames,
        "group_catch_up_ms": catch_up_ms,
        "settled_group_redraws": settled_frames,
        "boundary": "Posted native down/move/up with thread-local modifier state; no physical mouse input"
    }))
}

async fn verify_group_catch_up(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    views: &[Rc<RefCell<ChartView>>],
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<f64> {
    Timer::after(Duration::from_millis(250)).await;
    let previous = views[0].borrow().result_for_report();
    let next = Arc::new(previous.with_view(previous.request.id + 1, previous.request.view));
    handle.update(cx, |_, _, cx| {
        for view in views {
            let mut view = view.borrow_mut();
            // Reproduce a completed older group awaiting publication as newer data arrives.
            view.defer_presentation_for_trial();
            view.set_result(next.clone()).map_err(|e| anyhow!(e))?;
            ensure!(
                view.ready(),
                "fixture must retain the unchanged viewport revision"
            );
        }
        host.update(cx, |host, cx| {
            host.workspace
                .interaction
                .as_ref()
                .unwrap()
                .notify_plots(cx);
        });
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    let start = Instant::now();
    loop {
        let complete = views.iter().all(|view| {
            let view = view.borrow();
            view.ready() && view.displayed_request_id() == Some(next.request.id)
        });
        if complete {
            break;
        }
        ensure!(
            start.elapsed() < Duration::from_secs(5),
            "publishing the older group lost the wakeup for the latest data"
        );
        // No notifications: only the production presentation path can request another frame.
        Timer::after(Duration::from_millis(4)).await;
    }
    let elapsed = start.elapsed().as_secs_f64() * 1000.0;
    handle.update(cx, |_, _, cx| {
        for view in views {
            view.borrow_mut()
                .set_result(previous.clone())
                .map_err(|e| anyhow!(e))?;
        }
        host.update(cx, |host, cx| {
            host.workspace
                .interaction
                .as_ref()
                .unwrap()
                .notify_plots(cx);
        });
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    Ok(elapsed)
}
