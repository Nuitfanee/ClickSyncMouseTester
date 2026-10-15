use super::*;
use clicksync_chart::gpu::PlotKind;

pub(super) fn prefix() -> Result<Arc<SessionPrefix>, clicksync_core::SessionError> {
    let mut datasets = Vec::new();
    for (key, dx, dy) in [(1, 2, 1), (2, -3, -2), (3, 1, -3)] {
        let mut session = clicksync_core::Session::default_with_metadata(Default::default())?;
        session.begin_collecting(100, true, false)?;
        for index in 0..32_u64 {
            session.append(clicksync_core::RawMouseRecord::new(
                dx,
                dy,
                100 + (index + 1) * 1250 * u64::from(key),
                index * 3 + u64::from(key),
                index * 3 + u64::from(key),
                clicksync_core::RawMouseMovementMode::Relative,
            ))?;
        }
        session.stop();
        session.set_capture_evidence(clicksync_core::session::CaptureEvidence {
            generation: Some(2),
            received_sequence: Some(96),
            queued: Some(96),
            cutoff: Some(96),
            ..session.capture_evidence()
        });
        datasets.push((key, Arc::new(session.snapshot()?)));
    }
    Ok(Arc::new(SessionPrefix {
        generation: 2,
        received_sequence: 96,
        received_records: 96,
        phase: CapturePhase::Stopped,
        complete: true,
        datasets,
    }))
}

fn position(view: &ChartView) -> gpui::Point<gpui::Pixels> {
    position_at(view, 12)
}

pub(super) fn position_at(view: &ChartView, index: usize) -> gpui::Point<gpui::Pixels> {
    let result = view.presented_result().unwrap();
    let dataset = result
        .datasets
        .iter()
        .find(|dataset| dataset.device_key == view.dataset_key())
        .unwrap();
    let viewport = view.presented_viewport().unwrap();
    let value = if result.request.kind == PlotKind::Histogram {
        let bin = dataset.histogram.iter().find(|bin| bin.count > 0).unwrap();
        let side = result
            .datasets
            .iter()
            .position(|dataset| dataset.device_key == view.dataset_key())
            .unwrap();
        let count = result.datasets.len() as f64;
        let width = bin.maximum - bin.minimum;
        clicksync_chart::Point {
            x: clicksync_chart::Coordinate::Scalar(
                (bin.minimum + bin.maximum) * 0.5 + (side as f64 + 0.5) * width / count
                    - width / 2.0
                    - 0.82 / count * width / 2.0,
            ),
            y: clicksync_chart::Coordinate::Scalar(bin.value * 0.5),
        }
    } else {
        dataset
            .geometry
            .iter()
            .flat_map(|block| &block.samples)
            .find(|sample| sample.source_index == index)
            .unwrap()
            .point
    };
    let bounds = view.bounds_for_trial();
    let x = value.x.relative_to(viewport.origin.x) / viewport.width;
    let y = value.y.relative_to(viewport.origin.y) / viewport.height;
    bounds.origin
        + gpui::point(
            bounds.size.width * x as f32,
            bounds.size.height
                * if result.request.kind == PlotKind::Trajectory {
                    y as f32
                } else {
                    (1.0 - y) as f32
                },
        )
}

async fn move_pointer(
    handle: gpui::AnyWindowHandle,
    position: gpui::Point<gpui::Pixels>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    handle.update(cx, |_, window, _| unsafe {
        use windows::Win32::{
            Foundation::LPARAM,
            UI::WindowsAndMessaging::{PostMessageW, WM_MOUSEMOVE},
        };
        let x = (f32::from(position.x) * window.scale_factor()).round() as u16;
        let y = (f32::from(position.y) * window.scale_factor()).round() as u16;
        PostMessageW(
            Some(shell::hwnd(window)),
            WM_MOUSEMOVE,
            Default::default(),
            LPARAM(((u32::from(y) << 16) | u32::from(x)) as isize),
        )
    })??;
    Timer::after(Duration::from_millis(100)).await;
    Ok(())
}

async fn inspect(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    slot: usize,
    key: u32,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    let position = handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        let views = host.workspace_plot_views(slot);
        let view = views
            .iter()
            .find(|view| view.borrow().dataset_key() == key)
            .unwrap();
        let position = position(&view.borrow());
        ensure!(
            views[0].borrow().bounds_for_trial().contains(&position),
            "hover fixture is outside plot {slot} for dataset {key}"
        );
        Ok::<_, gpui::private::anyhow::Error>(position)
    })??;
    move_pointer(handle, position, cx).await?;
    handle.update(cx, |_, window, cx| {
        let host = host.read(cx);
        let link = host
            .workspace
            .link
            .as_ref()
            .ok_or_else(|| anyhow!("no hover link: slot={slot}, key={key}, position={position:?}, actual_pointer={:?}, active={}, presentations={}, counts={:?}",
                window.mouse_position(), window.is_window_active(), window.windows_presentation_count(),
                host.workspace.interaction.as_ref().unwrap().counts()))?;
        ensure!(
            link.key == key,
            "plot {slot}: hovering dataset {key} selected dataset {}, position={position:?}, actual_pointer={:?}",
            link.key, window.mouse_position()
        );
        let hovered = host
            .plot_views()
            .into_iter()
            .filter(|view| !view.borrow().hover_readout().is_empty())
            .collect::<Vec<_>>();
        ensure!(
            hovered.len() == 1,
            "hover left stale readouts on other datasets"
        );
        ensure!(
            hovered[0].borrow().dataset_key() == key,
            "readout dataset differs from link"
        );
        let readout = hovered[0].borrow().hover_readout();
        ensure!(
            readout[0].1 == ["A", "B", "C"][(key - 1) as usize],
            "readout label differs from selected dataset"
        );
        ensure!(
            host.workspace.selected_point == Some((link.request_id, key, link.index))
                || hovered[0].borrow().histogram_hit().is_some(),
            "hover report identity differs from selected point"
        );
        ensure!(
            link.markers
                .iter()
                .any(|(request, candidate, points)| *request == link.request_id
                    && *candidate == key
                    && !points.is_empty()),
            "selected dataset has no real linked marker"
        );
        ensure!(!host.file_busy, "hover occupied the file worker");
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    Ok(())
}

pub(super) async fn verify(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<serde_json::Value> {
    let prefix = prefix()?;
    let input_integrity = crate::ui::probe::prefix_report(&prefix)?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.workspace.retire(&host.runtime.borrow());
            for view in host
                .chart
                .take()
                .into_iter()
                .chain(host.comparison_chart.take())
            {
                view.borrow_mut().close();
            }
            host.workspace.overview_viewports.clear();
            host.workspace.detail_viewports.clear();
            host.import_identity += 1;
            host.selected = vec![1, 2];
            host.workspace.dual = true;
            cx.notify();
        });
    })?;
    settle(handle, host, prefix.clone(), cx).await?;
    Timer::after(Duration::from_millis(250)).await;
    let before = handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        (
            host.runtime.borrow().worker.report().processed_targets,
            host.workspace.interaction.as_ref().unwrap().counts(),
            host.plot_views()
                .iter()
                .map(|view| view.borrow().report().resources.unwrap().uploaded_bytes)
                .collect::<Vec<_>>(),
        )
    })?;
    let mut checked = Vec::new();
    for slot in 0..4 {
        for key in [2, 1, 3, 2] {
            inspect(handle, host, slot, key, cx).await?;
            let counts = handle.update(cx, |_, _, cx| {
                host.read(cx)
                    .workspace
                    .interaction
                    .as_ref()
                    .unwrap()
                    .counts()
            })?;
            checked.push(serde_json::json!({ "slot": slot, "key": key, "counts": counts }));
        }
    }
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        ensure!(
            before.0 == host.runtime.borrow().worker.report().processed_targets,
            "multi-dataset hover recomputed historical inputs"
        );
        ensure!(
            before.1[5] == host.workspace.interaction.as_ref().unwrap().counts()[5],
            "multi-dataset hover notified host controls"
        );
        ensure!(
            before.2
                == host
                    .plot_views()
                    .iter()
                    .map(|view| view.borrow().report().resources.unwrap().uploaded_bytes)
                    .collect::<Vec<_>>(),
            "multi-dataset hover uploaded unchanged geometry"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    let native_content_rebuilds = handle.update(cx, |_, _, cx| {
        host.read(cx)
            .workspace
            .interaction
            .as_ref()
            .unwrap()
            .counts()[0]
            - before.1[0]
    })?;
    // Native mouse events can revalidate GPUI hitboxes; isolate frame-local hover work here.
    let content = handle.update(cx, |_, _, cx| {
        host.read(cx)
            .workspace
            .interaction
            .as_ref()
            .unwrap()
            .counts()[0]
    })?;
    for key in [2, 1, 3] {
        handle.update(cx, |_, _, cx| {
            host.update(cx, |host, cx| {
                let views = host.workspace_plot_views(0);
                let target = views
                    .iter()
                    .find(|view| view.borrow().dataset_key() == key)
                    .unwrap();
                host.queue_analysis_pointer(views[0].clone(), position(&target.borrow()), cx);
            });
        })?;
        Timer::after(Duration::from_millis(100)).await;
    }
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        ensure!(
            content == host.workspace.interaction.as_ref().unwrap().counts()[0],
            "frame-local hover rebuilt cached controls"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    for slot in [0, 1] {
        let (view, viewport) = handle.update(cx, |_, _, cx| {
            host.update(cx, |host, cx| {
                let views = host.workspace_plot_views(slot);
                let primary = views[0].clone();
                let viewport = primary.borrow().viewport();
                let anchor = position(&views[1].borrow());
                primary.borrow_mut().zoom_at(0.7, anchor, false, false);
                primary.borrow_mut().pan(4.0, -3.0);
                host.workspace
                    .interaction
                    .as_ref()
                    .unwrap()
                    .notify_plot(slot, cx);
                (primary, viewport)
            })
        })?;
        settle(handle, host, prefix.clone(), cx).await?;
        inspect(handle, host, slot, 2, cx).await?;
        handle.update(cx, |_, _, cx| {
            host.update(cx, |host, cx| {
                view.borrow_mut().set_viewport(viewport, true);
                host.workspace
                    .interaction
                    .as_ref()
                    .unwrap()
                    .notify_plot(slot, cx);
            });
        })?;
        settle(handle, host, prefix.clone(), cx).await?;
    }
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.toggle_analysis_dataset(1);
            cx.notify();
        });
    })?;
    settle(handle, host, prefix.clone(), cx).await?;
    inspect(handle, host, 0, 3, cx).await?;
    inspect(handle, host, 0, 2, cx).await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.toggle_analysis_dataset(1);
            let view = host.workspace.monitors[1][0].clone();
            let mut wide = view.borrow().viewport();
            wide.origin.y = clicksync_chart::Coordinate::Scalar(-10_000.0);
            wide.height = 20_000.0;
            view.borrow_mut().set_viewport(wide, true);
            host.open_plot_detail(host.settings.monitor_metrics[1], false);
            cx.notify();
        });
    })?;
    settle(handle, host, prefix.clone(), cx).await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            let view = host.analysis_chart().unwrap();
            let mut view = view.borrow_mut();
            let result = view.presented_result().unwrap();
            let focus = result
                .workspace_viewport(view.dataset_key())
                .map_err(|error| anyhow!(error))?;
            ensure!(
                view.viewport() == focus,
                "first detail must use focus, not the overview's manual/global Y axis"
            );
            view.fit_trajectory();
            ensure!(
                view.viewport()
                    == result
                        .full_workspace_viewport(view.dataset_key())
                        .map_err(|error| anyhow!(error))?,
                "global view must retain the full extent"
            );
            let mut panned = view.viewport();
            panned.origin.x = crate::chart_view::shift_view(panned.origin.x, 0.1);
            panned.width *= 0.95;
            view.set_viewport(panned, true);
            view.fit_focus();
            ensure!(
                view.viewport() == focus,
                "focus must restore both the time range and the value axis"
            );
            host.workspace
                .interaction
                .as_ref()
                .unwrap()
                .notify_plots(cx);
            Ok::<_, gpui::private::anyhow::Error>(())
        })
    })??;
    settle(handle, host, prefix.clone(), cx).await?;
    for key in [2, 1, 3] {
        inspect(handle, host, 0, key, cx).await?;
    }
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.open_plot_detail(host.settings.monitor_metrics[1], true);
            cx.notify();
        });
    })?;
    settle(handle, host, prefix, cx).await?;
    for key in [2, 1, 3] {
        inspect(handle, host, 0, key, cx).await?;
    }
    let outside = handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        let view = host.workspace_plot_views(0)[0].borrow().report();
        gpui::point(
            px(view.logical_bounds[0] + view.logical_bounds[2] * 0.5),
            px((view.outer_bounds[1] + view.logical_bounds[1]) * 0.5),
        )
    })?;
    move_pointer(handle, outside, cx).await?;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        ensure!(
            host.workspace.link.is_none(),
            "leaving plots kept the hover link"
        );
        ensure!(
            host.plot_views()
                .iter()
                .all(|view| view.borrow().hover_readout().is_empty()),
            "leaving plots kept the hover readout"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    Ok(serde_json::json!({
        "passed": true, "overview": checked,
        "hidden_primary": true, "pan_zoom": true, "detail": true, "histogram": true,
        "leave_clears_hover": true, "recomputed_targets": 0, "uploaded_bytes": 0,
        "frame_local_content_rebuilds": 0, "native_content_rebuilds": native_content_rebuilds,
        "input_integrity": input_integrity,
        "boundary": "Three fixed datasets, production GPUI/D3D11 and posted WM_MOUSEMOVE; no physical input or human acceptance",
    }))
}
