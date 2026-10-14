//! Opt-in production-window polling lifecycle checks; replay is not a hardware-rate test.
use super::*;
use crate::ui::probe::{scenario, wait};
use gpui::private::anyhow::ensure;

pub(in crate::ui) async fn exercise(
    handle: gpui::AnyWindowHandle,
    host: &gpui::Entity<GpuiHost>,
    report: &Arc<Mutex<serde_json::Value>>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    wait(
        handle,
        host,
        |h| h.runtime.borrow().pending_action().is_none(),
        cx,
    )
    .await?;
    activate_smoke_capture_window(handle, host, cx).await?;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |h, cx| {
            h.polling.capture_source = Some(clicksync_core::protocol::CaptureSource::Replay {
                records_per_device: 240_000,
                reports_per_second: 8000,
                real_time: true,
            });
            h.polling_select(1);
            h.polling_primary();
            ensure!(
                h.polling.current_text == "--",
                "start exposed a numeric warmup value"
            );
            cx.notify();
            Ok::<_, gpui::private::anyhow::Error>(())
        })
    })??;
    wait(
        handle,
        host,
        |h| {
            h.polling.frame.as_ref().is_some_and(|f| {
                f.metrics.status == RateStatus::Valid && f.metrics.empty_packet_percent.is_some()
            }) && h.polling.current_text != "--"
        },
        cx,
    )
    .await?;
    let (frozen, text, empty) = handle.update(cx, |_, _, cx| {
        host.update(cx, |h, cx| {
            let frame = h.polling.frame.clone().unwrap();
            let now = frame.history.last().unwrap().observed_ms;
            let prefix = h
                .runtime
                .borrow()
                .backend
                .as_ref()
                .unwrap()
                .latest_prefix()
                .unwrap();
            let dataset = &prefix.datasets[0].1;
            let count = dataset
                .events()
                .take(frame.received_sequence as usize)
                .filter(|event| {
                    let t = event.raw_capture_ticks as f64 * 1000.0
                        / dataset.timestamp_frequency() as f64;
                    t > now - 300.0
                        && t <= now
                        && (event.raw_delta_x != 0 || event.raw_delta_y != 0)
                })
                .count();
            ensure!(
                frame.metrics.current_rate == Some((count as f64 / 0.3).round_ties_even() as i32),
                "window count differs from committed raw reports"
            );
            ensure!(
                frame.history.iter().all(|p| p.rate > 0.0),
                "warmup inserted zero points"
            );
            let text = h.polling.current_text.clone();
            let empty = h.polling.empty_text.clone();
            h.capture_command("pause");
            cx.notify();
            Ok::<_, gpui::private::anyhow::Error>((frame, text, empty))
        })
    })??;
    wait(
        handle,
        host,
        |h| h.last_phase == CapturePhase::Paused && h.runtime.borrow().pending_action().is_none(),
        cx,
    )
    .await?;
    Timer::after(Duration::from_millis(300)).await;
    let paints = handle.update(cx, |_, _, cx| {
        let h = host.read(cx);
        ensure!(
            h.polling.current_text == text && h.polling.empty_text == empty,
            "pause changed displayed metrics"
        );
        ensure!(
            h.polling
                .frame
                .as_ref()
                .is_some_and(|f| Arc::ptr_eq(f, &frozen)),
            "pause accepted a late measurement frame"
        );
        Ok::<_, gpui::private::anyhow::Error>(
            h.polling_render_stats(cx)["paints"].as_u64().unwrap(),
        )
    })??;
    Timer::after(Duration::from_millis(200)).await;
    handle.update(cx, |_, _, cx| {
        host.update(cx, |h, cx| {
            ensure!(
                h.polling_render_stats(cx)["paints"] == paints,
                "paused chart requests frames"
            );
            h.polling_primary();
            ensure!(
                h.polling.current_text == text && h.polling.quality == RateStatus::WarmingUp,
                "resume changed the last reading before a valid window"
            );
            cx.notify();
            Ok::<_, gpui::private::anyhow::Error>(())
        })
    })??;
    wait(
        handle,
        host,
        |h| {
            h.polling.frame.as_ref().is_some_and(|f| {
                f.metrics.status == RateStatus::Valid
                    && f.revision == h.polling.revision
                    && f.history
                        .last()
                        .zip(frozen.history.last())
                        .is_some_and(|(new, old)| new.segment != old.segment)
            })
        },
        cx,
    )
    .await?;
    let end_text = handle.update(cx, |_, _, cx| {
        host.update(cx, |h, cx| {
            let text = h.polling.current_text.clone();
            h.polling_end();
            ensure!(
                h.polling.current_text == text && h.polling.is_clearing(),
                "end discarded the presentation before retiring"
            );
            cx.notify();
            Ok::<_, gpui::private::anyhow::Error>(text)
        })
    })??;
    ensure!(!end_text.is_empty(), "missing end reading");
    wait(
        handle,
        host,
        |h| !h.polling.has_session() && h.runtime.borrow().pending_action().is_none(),
        cx,
    )
    .await?;
    handle.update(cx, |_, _, cx| {
        let h = host.read(cx);
        ensure!(
            h.polling.current_text == "--" && h.polling.frame.is_none(),
            "clear retained a result"
        );
        ensure!(
            h.runtime
                .borrow()
                .polling_stream
                .reader()
                .latest()
                .is_none(),
            "clear retained a worker publication"
        );
        scenario(
            report,
            "polling-certified-count-pause-freeze-resume-gap-and-retire",
        );
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    Ok(())
}
