//! Fixed-input window statistics through the production plot/readout caches.
use super::*;
use crate::chart::gaps::GapStatistics;

fn prefix() -> Result<Arc<SessionPrefix>, clicksync_core::SessionError> {
    let mut datasets = Vec::new();
    for (key, times) in [
        (1, [0, 1, 2, 12, 13, 14, 34, 35, 36]),
        (2, [0, 1, 2, 10, 11, 12, 42, 43, 44]),
    ] {
        let config = clicksync_core::SessionConfig {
            timestamp_frequency: 1000,
            ..Default::default()
        };
        let mut session = clicksync_core::Session::new(
            config.clone(),
            clicksync_core::SharedByteBudget::new(config.managed_memory_budget_bytes),
            Default::default(),
        )?;
        session.begin_collecting(1000, true, false)?;
        for (index, time) in times.into_iter().enumerate() {
            let sequence = index as u64 * 2 + u64::from(key);
            session.append(clicksync_core::RawMouseRecord::new(
                1,
                -1,
                1000 + time,
                sequence,
                sequence,
                clicksync_core::RawMouseMovementMode::Relative,
            ))?;
        }
        session.stop();
        session.set_capture_evidence(clicksync_core::session::CaptureEvidence {
            generation: Some(4),
            received_sequence: Some(18),
            queued: Some(18),
            cutoff: Some(18),
            ..session.capture_evidence()
        });
        datasets.push((key, Arc::new(session.snapshot()?)));
    }
    Ok(Arc::new(SessionPrefix {
        generation: 4,
        received_sequence: 18,
        received_records: 18,
        phase: CapturePhase::Stopped,
        complete: true,
        datasets,
    }))
}

pub(super) async fn verify(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<serde_json::Value> {
    let prefix = prefix()?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.show_plot_overview();
            host.workspace.retire(&host.runtime.borrow());
            for view in host
                .chart
                .take()
                .into_iter()
                .chain(host.comparison_chart.take())
            {
                view.borrow_mut().close();
            }
            host.workspace.detail_options.clear();
            host.workspace.detail_viewports.clear();
            host.workspace.overview_viewports.clear();
            host.workspace.hidden_datasets.clear();
            host.workspace.export_group = 1;
            host.import_identity += 1;
            host.logical = false;
            host.selected = vec![1, 2];
            cx.notify();
        });
    })?;
    settle(handle, host, prefix.clone(), cx).await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.open_plot_detail(5, false);
            host.toggle_detail_option(5, 3);
            cx.notify();
        });
    })?;
    settle(handle, host, prefix.clone(), cx).await?;
    Timer::after(Duration::from_millis(250)).await;
    let before = handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        (
            host.runtime.borrow().worker.report().processed_targets,
            host.workspace.interaction.as_ref().unwrap().counts()[0],
        )
    })?;
    let mut checked = Vec::new();
    for (start, width, count, mean_ms) in [
        (0.0, 45.0, 2, Some(15.0)),
        (3.0, 4.0, 1, Some(10.0)),
        (15.0, 10.0, 1, Some(20.0)),
        (34.0, 1.0, 0, None),
    ] {
        handle.update(cx, |_, _, cx| {
            host.update(cx, |host, cx| {
                let view = host.analysis_chart().unwrap();
                let mut viewport = view.borrow().viewport();
                viewport.origin.x = clicksync_chart::Coordinate::Scalar(start);
                viewport.width = width;
                view.borrow_mut().set_viewport(viewport, true);
                host.workspace
                    .interaction
                    .as_ref()
                    .unwrap()
                    .notify_plot(0, cx);
            });
        })?;
        Timer::after(Duration::from_millis(100)).await;
        handle.update(cx, |_, _, cx| {
            let host = host.read(cx);
            let expected = Some((0, GapStatistics { count, mean_ms }));
            ensure!(
                host.detail_gap_values() == expected,
                "wrong visible gap values"
            );
            ensure!(
                host.workspace.interaction.as_ref().unwrap().gap_values(cx) == expected,
                "metric list did not repaint after the viewport changed"
            );
            Ok::<_, gpui::private::anyhow::Error>(())
        })??;
        checked.push(serde_json::json!({ "start_ms": start, "width_ms": width, "count": count, "mean_ms": mean_ms }));
    }
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            for view in host.workspace_plot_views(0) {
                view.borrow_mut().close();
            }
            host.workspace
                .interaction
                .as_ref()
                .unwrap()
                .notify_plot(0, cx);
        });
    })?;
    settle(handle, host, prefix.clone(), cx).await?;
    Timer::after(Duration::from_millis(100)).await;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        ensure!(
            host.workspace.interaction.as_ref().unwrap().gap_values(cx) == host.detail_gap_values(),
            "same-request GPU republishing left a stale gap readout"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            let frame = host.workspace.interaction.as_ref().unwrap();
            ensure!(
                host.runtime.borrow().worker.report().processed_targets == before.0,
                "window gap queries recomputed history"
            );
            ensure!(
                frame.counts()[0] == before.1,
                "window changes rebuilt controls"
            );
            let mut options = host.detail_options(5);
            options.gap_dataset = Some(2);
            host.workspace.detail_options.insert((5, false), options);
            ensure!(
                host.detail_gap_values().is_none(),
                "pending target reused the previous group"
            );
            cx.notify();
            Ok::<_, gpui::private::anyhow::Error>(())
        })
    })??;
    settle(handle, host, prefix.clone(), cx).await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            let view = host.analysis_chart().unwrap();
            view.borrow_mut().fit_trajectory();
            host.workspace
                .interaction
                .as_ref()
                .unwrap()
                .notify_plot(0, cx);
        });
    })?;
    Timer::after(Duration::from_millis(100)).await;
    handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        let expected = Some((
            1,
            GapStatistics {
                count: 2,
                mean_ms: Some(19.0),
            },
        ));
        ensure!(
            host.detail_gap_values() == expected,
            "global view used another target"
        );
        ensure!(
            host.workspace.interaction.as_ref().unwrap().gap_values(cx) == expected,
            "global view did not update the metric list"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.toggle_detail_option(5, 3);
            ensure!(
                host.detail_gap_values().is_none(),
                "disabled gaps kept statistics"
            );
            cx.notify();
            Ok::<_, gpui::private::anyhow::Error>(())
        })
    })??;
    settle(handle, host, prefix.clone(), cx).await?;
    Timer::after(Duration::from_millis(100)).await;
    handle.update(cx, |_, _, cx| {
        ensure!(
            host.read(cx)
                .workspace
                .interaction
                .as_ref()
                .unwrap()
                .gap_values(cx)
                .is_none(),
            "disabled gaps left a mounted metric list"
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |host, cx| {
            host.toggle_detail_option(5, 3);
            cx.notify();
        });
    })?;
    settle(handle, host, prefix, cx).await?;
    Timer::after(Duration::from_millis(100)).await;
    Ok(serde_json::json!({
        "passed": true, "windows": checked, "target_switch": true, "global_view": true,
        "same_request_gpu_republish": true,
        "disabled": true, "recomputed_targets_on_view_change": 0, "control_rebuilds_on_view_change": 0,
    }))
}
