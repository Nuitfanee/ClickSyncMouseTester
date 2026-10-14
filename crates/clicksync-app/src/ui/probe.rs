//! Shared helpers for opt-in functional and performance probes.
use super::*;
use gpui::private::anyhow::{anyhow, ensure};

pub(super) fn ensure_complete_prefix(prefix: &crate::backend::SessionPrefix) -> gpui::Result<()> {
    use crate::backend::CapturePhase;
    use clicksync_core::{SessionStatus, session::CaptureIntegrity};
    let expected_status = match prefix.phase {
        CapturePhase::Empty => SessionStatus::Empty,
        CapturePhase::Collecting => SessionStatus::Collecting,
        CapturePhase::Paused => SessionStatus::Paused,
        CapturePhase::Stopped => SessionStatus::Stopped,
        _ => {
            return Err(anyhow!(
                "unstable or faulted probe prefix: {:?}",
                prefix.phase
            ));
        }
    };
    ensure!(prefix.complete, "probe prefix reports incomplete data");
    ensure!(
        prefix.received_sequence == prefix.received_records
            && prefix.received_records
                == prefix
                    .datasets
                    .iter()
                    .map(|(_, s)| s.event_count() as u64)
                    .sum::<u64>(),
        "probe prefix watermarks do not cover its stored records"
    );
    for (key, snapshot) in &prefix.datasets {
        let evidence = snapshot.capture_evidence();
        ensure!(
            snapshot.integrity() == CaptureIntegrity::Complete && !evidence.has_loss(),
            "probe dataset {key} is incomplete or unknown: {evidence:?}"
        );
        ensure!(
            snapshot.status() == expected_status
                && snapshot.finalized()
                    == (prefix.phase == CapturePhase::Stopped && snapshot.event_count() > 0),
            "probe dataset {key} disagrees with prefix phase"
        );
        ensure!(
            evidence.generation.is_none_or(|v| v == prefix.generation)
                && evidence
                    .received_sequence
                    .is_none_or(|v| v == prefix.received_sequence)
                && evidence.queued.is_none_or(|v| {
                    v >= prefix.received_sequence
                        && (!matches!(prefix.phase, CapturePhase::Paused | CapturePhase::Stopped)
                            || v == prefix.received_sequence)
                }),
            "probe dataset {key} has contradictory capture evidence: {evidence:?}"
        );
    }
    Ok(())
}

pub(super) fn prefix_report(
    prefix: &crate::backend::SessionPrefix,
) -> gpui::Result<serde_json::Value> {
    ensure_complete_prefix(prefix)?;
    Ok(serde_json::json!({
        "generation": prefix.generation, "phase": prefix.phase, "complete": prefix.complete,
        "received_sequence": prefix.received_sequence, "received_records": prefix.received_records,
        "datasets": prefix.datasets.iter().map(|(key, snapshot)| serde_json::json!({
            "device_key": key, "records": snapshot.event_count(), "status": snapshot.status(),
            "finalized": snapshot.finalized(), "evidence": snapshot.capture_evidence(),
        })).collect::<Vec<_>>(),
    }))
}

pub(super) fn scenario(report: &Arc<Mutex<serde_json::Value>>, name: &str) {
    report.lock().unwrap()["scenarios"]
        .as_array_mut()
        .unwrap()
        .push(name.into());
}

pub(super) async fn click(
    handle: gpui::AnyWindowHandle,
    position: gpui::Point<gpui::Pixels>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    use windows::Win32::UI::WindowsAndMessaging::{
        PostMessageW, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MOUSEMOVE,
    };
    handle.update(cx, |_, window, _| unsafe {
        let x = (f32::from(position.x) * window.scale_factor()).round() as u16;
        let y = (f32::from(position.y) * window.scale_factor()).round() as u16;
        let position = windows::Win32::Foundation::LPARAM(((y as u32) << 16 | x as u32) as isize);
        PostMessageW(
            Some(shell::hwnd(window)),
            WM_MOUSEMOVE,
            Default::default(),
            position,
        )?;
        PostMessageW(
            Some(shell::hwnd(window)),
            WM_LBUTTONDOWN,
            windows::Win32::Foundation::WPARAM(1),
            position,
        )?;
        PostMessageW(
            Some(shell::hwnd(window)),
            WM_LBUTTONUP,
            Default::default(),
            position,
        )
    })??;
    Timer::after(Duration::from_millis(60)).await;
    Ok(())
}

pub(super) fn control_bounds(
    bounds: Rc<std::cell::Cell<Bounds<gpui::Pixels>>>,
) -> impl IntoElement {
    canvas(move |actual, _, _| bounds.set(actual), |_, _, _, _| {})
        .absolute()
        .inset_0()
        .size_full()
}

pub(super) async fn press_key(
    handle: gpui::AnyWindowHandle,
    virtual_key: u16,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    use windows::Win32::UI::{
        Input::KeyboardAndMouse::{MAPVK_VK_TO_VSC, MapVirtualKeyW},
        WindowsAndMessaging::{PostMessageW, WM_KEYDOWN, WM_KEYUP},
    };
    handle.update(cx, |_, window, _| unsafe {
        let scan_code = MapVirtualKeyW(virtual_key as u32, MAPVK_VK_TO_VSC);
        let key = windows::Win32::Foundation::WPARAM(virtual_key as usize);
        let down = 1 | ((scan_code as isize) << 16);
        PostMessageW(
            Some(shell::hwnd(window)),
            WM_KEYDOWN,
            key,
            windows::Win32::Foundation::LPARAM(down),
        )?;
        PostMessageW(
            Some(shell::hwnd(window)),
            WM_KEYUP,
            key,
            windows::Win32::Foundation::LPARAM(down | (3 << 30)),
        )
    })??;
    Timer::after(Duration::from_millis(60)).await;
    Ok(())
}

pub(super) fn all_visible(host: &GpuiHost) -> bool {
    let matches = |view: &Rc<RefCell<ChartView>>, slot: usize| {
        let view = view.borrow();
        view.presented_result()
            .zip(host.workspace.targets[slot].as_ref())
            .is_some_and(|(result, target)| {
                let report = view.report();
                result.eligible_for(target)
                    && result.datasets.len() == target.prefix.datasets.len()
                    && report.request_id == report.axes_request_id
                    && report.layer_end > 0
                    && host.workspace.analysis.as_ref().is_none_or(|analysis| {
                        result.request.prefix.received_sequence == analysis.input.received_sequence
                    })
            })
    };
    let main_views = host.visible_views(
        host.chart
            .iter()
            .chain(host.comparison_chart.iter())
            .chain(&host.workspace.extra_views)
            .cloned(),
    );
    !main_views.is_empty()
        && main_views.iter().all(|chart| matches(chart, 0))
        && host
            .workspace
            .monitors
            .iter()
            .take(if host.workspace.detail.is_some() {
                0
            } else {
                host.settings.monitor_metrics.len()
            })
            .enumerate()
            .all(|(index, views)| {
                let views = host.visible_views(views.iter().cloned());
                !views.is_empty() && views.iter().all(|view| matches(view, index + 1))
            })
}

pub(super) async fn wait(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    condition: impl Fn(&GpuiHost) -> bool,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    let deadline = std::time::Instant::now() + Duration::from_secs(15);
    while std::time::Instant::now() < deadline {
        Timer::after(Duration::from_millis(20)).await;
        if handle.update(cx, |_, _, cx| {
            let host = host.read(cx);
            ensure!(
                host.control_error.is_none() && host.backend_error.is_none(),
                "probe control/backend error: {:?} / {:?}",
                host.control_error,
                host.backend_error
            );
            if let Some(status) = host
                .runtime
                .borrow()
                .status()
                .filter(|status| status.generation >= host.page_generation)
            {
                ensure!(
                    capture_status_error(&status).is_none(),
                    "probe capture fault: {status:?}"
                );
            }
            Ok::<_, gpui::private::anyhow::Error>(condition(host))
        })?? {
            return Ok(());
        }
    }
    let state = handle.update(cx, |_, _, cx| {
        let host = host.read(cx);
        format!("phase={:?}, latest={:?}, fixed={:?}, completed={:?}, targets={:?}, visible={}, locked={}, layout={:?}, worker={}",
            host.runtime.borrow().status().map(|status| status.phase),
            host.workspace.latest.as_ref().map(|prefix| (prefix.phase, prefix.received_sequence, prefix.received_records)),
            host.workspace.analysis.as_ref().map(|analysis| (analysis.input.phase, analysis.input.received_sequence)),
            host.workspace.completed,
            host.workspace.targets.iter().map(|target| target.as_ref().map(|request| (request.id, request.prefix.phase, request.prefix.received_sequence))).collect::<Vec<_>>(),
            all_visible(host), host.lock.locked(), host.workspace.interaction.as_ref().map(|frame| frame.layout_report()),
            serde_json::to_string(&host.runtime.borrow().worker.report()).unwrap())
    })?;
    Err(anyhow!("workspace scenario deadline exceeded: {state}"))
}

pub(super) async fn capture(
    handle: gpui::AnyWindowHandle,
    directory: &std::path::Path,
    name: &str,
    report: &Arc<Mutex<serde_json::Value>>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    handle.update(cx, |_, window, _| {
        window.request_windows_frame_capture()?;
        window.refresh();
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    for _ in 0..200 {
        Timer::after(Duration::from_millis(20)).await;
        if let Some(frame) = handle.update(cx, |_, window, _| window.take_windows_frame())?? {
            let entry = serde_json::json!({ "name": name, "screenshot": format!("{name}.png"),
                "width": frame.width, "height": frame.height, "bgra_sha256": clicksync_chart::hlsl::sha256(&frame.bgra) });
            write_probe_frame(
                &directory.join(format!("{name}.png")),
                frame,
                cx.background_executor(),
            )
            .await?;
            report.lock().unwrap()["frames"]
                .as_array_mut()
                .unwrap()
                .push(entry);
            return Ok(());
        }
    }
    Err(anyhow!("workspace composed frame deadline exceeded"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::backend::{CapturePhase, SessionPrefix};
    use clicksync_core::{Session, session::CaptureEvidence};

    fn prefix(evidence: CaptureEvidence) -> SessionPrefix {
        let mut session = Session::default_with_metadata(Default::default()).unwrap();
        session.begin_collecting(100, true, false).unwrap();
        session.set_capture_evidence(evidence);
        SessionPrefix {
            generation: 1,
            received_sequence: 0,
            received_records: 0,
            phase: CapturePhase::Collecting,
            complete: true,
            datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
        }
    }

    #[test]
    fn complete_outer_flag_cannot_hide_unknown_or_lost_dataset_evidence() {
        ensure_complete_prefix(&prefix(CaptureEvidence::complete())).unwrap();
        for evidence in [
            CaptureEvidence::default(),
            CaptureEvidence {
                overflow: 1,
                ..CaptureEvidence::complete()
            },
            CaptureEvidence {
                ignored_after_limit: 1,
                ..CaptureEvidence::complete()
            },
            CaptureEvidence {
                disconnected: true,
                ..CaptureEvidence::complete()
            },
        ] {
            assert!(ensure_complete_prefix(&prefix(evidence)).is_err());
        }
    }

    #[test]
    fn probe_rejects_false_completeness_and_inconsistent_scope_or_phase() {
        let healthy = prefix(CaptureEvidence::complete());
        for broken in [
            SessionPrefix {
                complete: false,
                ..healthy.clone()
            },
            SessionPrefix {
                received_sequence: 1,
                ..healthy.clone()
            },
            SessionPrefix {
                received_sequence: 1,
                received_records: 1,
                ..healthy.clone()
            },
            SessionPrefix {
                phase: CapturePhase::Paused,
                ..healthy.clone()
            },
            SessionPrefix {
                phase: CapturePhase::Faulted,
                ..healthy
            },
            prefix(CaptureEvidence {
                generation: Some(2),
                ..CaptureEvidence::complete()
            }),
            prefix(CaptureEvidence {
                received_sequence: Some(1),
                ..CaptureEvidence::complete()
            }),
        ] {
            assert!(ensure_complete_prefix(&broken).is_err());
        }
    }
}
