use super::*;
use crate::backend::{AppStatus, CapturePhase, SessionPrefix};
use crate::product::polling_stream::Frame;
use clicksync_chart::analysis::polling::{PollingMetrics, PollingMode, RateStatus};
use std::time::Instant;
pub(super) mod history;
#[cfg(feature = "ui-restoration-probe")]
pub(in crate::ui) mod probe;
mod readout;
mod sampling;

#[derive(Clone, Copy, Default, PartialEq, Eq)]
enum Status {
    #[default]
    Ready,
    Measuring,
    Paused,
    Cleared,
    NeedDevice,
    Disconnected,
    Reconnected,
    NoDevices,
    Refreshed,
    Incomplete,
}

pub(super) struct Feedback {
    started: Option<Instant>,
    from: f32,
    target: f32,
    seconds: f32,
}

impl Feedback {
    fn new(value: f32) -> Self {
        Self {
            started: None,
            from: value,
            target: value,
            seconds: 0.2,
        }
    }
    fn value(&self, now: Instant) -> f32 {
        let Some(started) = self.started else {
            return self.target;
        };
        let t = (now.saturating_duration_since(started).as_secs_f32() / self.seconds).min(1.0);
        let eased = t * t * (3.0 - 2.0 * t);
        self.from + (self.target - self.from) * eased
    }
    fn animate(&mut self, target: f32, seconds: f32, now: Instant) {
        self.from = self.value(now);
        self.target = target;
        self.seconds = seconds;
        self.started = Some(now);
    }
    fn sample(&mut self, now: Instant) -> (f32, bool) {
        let value = self.value(now);
        if self
            .started
            .is_some_and(|start| now.saturating_duration_since(start).as_secs_f32() >= self.seconds)
        {
            self.started = None;
        }
        (value, self.started.is_some())
    }
}

#[derive(PartialEq)]
struct ContentKey {
    dark: bool,
    english: bool,
    status: Status,
    pending: bool,
    locked: bool,
    has_session: bool,
    device_menu: bool,
    selected: Option<u32>,
    current: String,
    empty: String,
    peak: Option<i32>,
    error: Option<String>,
    quality: RateStatus,
}

struct Content {
    host: gpui::WeakEntity<GpuiHost>,
    #[cfg(feature = "ui-restoration-probe")]
    renders: u64,
}

impl Render for Content {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        #[cfg(feature = "ui-restoration-probe")]
        {
            self.renders += 1;
        }
        self.host
            .update(cx, |host, cx| host.render_polling_content(window, cx))
            .expect("polling content belongs to its live host")
    }
}

pub(super) struct PollingPage {
    #[cfg(feature = "ui-restoration-probe")]
    pub capture_source: Option<clicksync_core::protocol::CaptureSource>,
    #[cfg(feature = "ui-restoration-probe")]
    replay_clock: Option<(u64, Instant, f64)>,
    pub session_device: Option<u32>,
    status: Status,
    refreshed_until: Option<Instant>,
    status_motion: Feedback,
    history_present: bool,
    chart: Option<gpui::Entity<history::HistoryChart>>,
    readout: Option<gpui::Entity<readout::Readout>>,
    content: Option<gpui::Entity<Content>>,
    content_key: Option<ContentKey>,
    pub current_text: String,
    pub empty_text: String,
    displayed_at: Option<Instant>,
    pub frame: Option<Arc<Frame>>,
    revision: u64,
    quality: RateStatus,
    clearing_since: Option<Instant>,
    pause_ms: Option<f64>,
    pub history_bounds: Arc<Mutex<[f32; 4]>>,
    pub readout_bounds: Arc<Mutex<[f32; 4]>>,
}

impl Default for PollingPage {
    fn default() -> Self {
        Self {
            #[cfg(feature = "ui-restoration-probe")]
            capture_source: None,
            #[cfg(feature = "ui-restoration-probe")]
            replay_clock: None,
            session_device: None,
            status: Status::Ready,
            refreshed_until: None,
            status_motion: Feedback::new(1.0),
            history_present: false,
            chart: None,
            readout: None,
            content: None,
            content_key: None,
            current_text: "--".into(),
            empty_text: "--".into(),
            displayed_at: None,
            frame: None,
            revision: 0,
            quality: RateStatus::WarmingUp,
            clearing_since: None,
            pause_ms: None,
            history_bounds: Arc::new(Mutex::new([0.0; 4])),
            readout_bounds: Arc::new(Mutex::new([0.0; 4])),
        }
    }
}

impl PollingPage {
    #[cfg(feature = "ui-restoration-probe")]
    pub(super) fn disconnected_preview(reconnected: bool) -> Self {
        Self {
            session_device: Some(1),
            status: if reconnected {
                Status::Reconnected
            } else {
                Status::Disconnected
            },
            ..Default::default()
        }
    }

    pub fn is_clearing(&self) -> bool {
        self.clearing_since.is_some()
    }
    pub fn has_session(&self) -> bool {
        self.session_device.is_some()
    }
    pub fn domain_now(&mut self, prefix: &SessionPrefix) -> f64 {
        #[cfg(feature = "ui-restoration-probe")]
        if self.capture_source.is_some() {
            let now = Instant::now();
            let time = prefix.datasets.first().map_or(100.0, |(_, snapshot)| {
                snapshot
                    .event_at(snapshot.event_count().saturating_sub(1))
                    .map_or(100.0, |event| {
                        event.raw_capture_ticks as f64 * 1000.0
                            / snapshot.timestamp_frequency() as f64
                    })
            });
            if self
                .replay_clock
                .is_none_or(|(sequence, _, _)| sequence != prefix.received_sequence)
            {
                self.replay_clock = Some((prefix.received_sequence, now, time));
            }
            let (_, started, time) = self.replay_clock.unwrap();
            return time
                + if prefix.phase == CapturePhase::Collecting {
                    now.duration_since(started).as_secs_f64() * 1000.0
                } else {
                    0.0
                };
        }
        let _ = prefix;
        crate::capture_lock::now_ms()
    }
    pub fn accept_metrics(
        &mut self,
        metrics: &PollingMetrics,
        history: bool,
        paused: bool,
    ) -> bool {
        if !self.has_session() || paused || self.clearing_since.is_some() {
            return false;
        }
        let now = Instant::now();
        let mut changed = self.quality != metrics.status;
        self.quality = metrics.status;
        if metrics.status == RateStatus::Valid
            && self
                .displayed_at
                .is_none_or(|last| now.duration_since(last) >= Duration::from_millis(100))
        {
            let current = metrics
                .current_rate
                .expect("valid polling rate")
                .to_string();
            let empty = metrics
                .empty_packet_percent
                .map_or("--".into(), |value| format!("{value:.2}%"));
            changed |= self.current_text != current || self.empty_text != empty;
            self.current_text = current;
            self.empty_text = empty;
            self.displayed_at = Some(now);
        }
        if self.history_present != history {
            self.history_present = history;
            changed = true;
        }
        changed
    }
    fn clear_opacity(&self, now: Instant) -> f32 {
        self.clearing_since.map_or(1.0, |start| {
            1.0 - (now.saturating_duration_since(start).as_secs_f32() / 0.16).min(1.0)
        })
    }

    pub fn finish_clear(&mut self, now: Instant) -> bool {
        if self
            .clearing_since
            .is_none_or(|start| now.saturating_duration_since(start) < Duration::from_millis(160))
        {
            return false;
        }
        // Retain the sibling entities until they observe the empty state; no detached animation.
        let chart = self.chart.take();
        let content = self.content.take();
        let readout = self.readout.take();
        let revision = self.revision;
        *self = Self::default();
        self.chart = chart;
        self.content = content;
        self.readout = readout;
        self.revision = revision;
        self.set_status(Status::Cleared);
        true
    }

    fn set_status(&mut self, status: Status) -> bool {
        if self.status == status {
            return false;
        }
        self.status = status;
        self.status_motion = Feedback::new(0.45);
        self.status_motion.animate(1.0, 0.14, Instant::now());
        true
    }
}

impl GpuiHost {
    pub(super) fn sync_polling_history(&mut self, cx: &mut Context<Self>) {
        if self.page != Page::Polling {
            return;
        }
        if let Some(chart) = &self.polling.chart {
            let frame = self
                .polling
                .frame
                .clone()
                .filter(|_| self.polling.has_session());
            let running = self.last_phase == CapturePhase::Collecting
                && self.lock.locked()
                && self.polling.clearing_since.is_none()
                && frame
                    .as_ref()
                    .is_some_and(|f| f.revision == self.polling.revision);
            let palette = design::Palette::for_theme(self.settings.dark);
            let stream = self.runtime.borrow().backend.as_ref().map(|_| {
                (
                    self.runtime.borrow().polling_stream.reader(),
                    self.receiver.0,
                    self.polling.revision,
                )
            });
            chart.update(cx, |chart, cx| {
                chart.update(frame, running, palette, stream, cx)
            });
        }
    }

    pub(super) fn invalidate_polling_content(&self, cx: &mut Context<Self>) {
        if let Some(content) = &self.polling.content {
            content.update(cx, |_, cx| cx.notify());
        }
    }

    #[cfg(feature = "ui-restoration-probe")]
    pub(super) fn polling_render_stats(&self, cx: &gpui::App) -> serde_json::Value {
        let chart = self.polling.chart.as_ref().unwrap().read(cx);
        let stats = chart.stats();
        let origin = stats.samples.first().map(|sample| sample.0);
        serde_json::json!({
            "paints": stats.paints,
            "geometry_builds": stats.geometry_builds,
            "axes_builds": stats.axes_builds,
            "tail_builds": stats.tail_builds,
            "sampling": chart.sampling_stats(),
            "geometry_events": stats.geometry_events.iter().map(|(changes, size, maximum, logical_ms)| {
                serde_json::json!({ "changed_data": changes[0], "changed_size": changes[1],
                    "changed_axis": changes[2], "changed_dpi": changes[3],
                    "size": size, "maximum": maximum, "logical_ms": logical_ms })
            }).collect::<Vec<_>>(),
            "geometry_points": stats.geometry_points,
            "curve_points": stats.curve_points,
            "content_renders": self.polling.content.as_ref().unwrap().read(cx).renders,
            "samples": stats.samples.iter().map(|(time, logical_ms, cpu_ms)| {
                serde_json::json!({
                    "elapsed_ms": origin.map_or(0.0, |origin| time.duration_since(origin).as_secs_f64() * 1000.0),
                    "logical_ms": logical_ms,
                    "paint_cpu_ms": cpu_ms,
                })
            }).collect::<Vec<_>>(),
            "live_samples": stats.live_samples.iter().map(|(time, logical_ms, rate, seam_ms, cubic, geometry_origin_ms, live, dpi, incoming_slope)| {
                serde_json::json!({
                    "elapsed_ms": origin.map_or(0.0, |origin| time.duration_since(origin).as_secs_f64() * 1000.0),
                    "logical_ms": logical_ms, "head_rate": rate, "seam_ms": seam_ms, "tail": cubic,
                    "geometry_origin_ms": geometry_origin_ms, "live": live, "dpi": dpi,
                    "incoming_slope": incoming_slope,
                })
            }).collect::<Vec<_>>(),
        })
    }

    #[cfg(feature = "ui-restoration-probe")]
    pub(super) fn polling_readout_stats(&self, cx: &gpui::App) -> serde_json::Value {
        let mut stats = self.polling.readout.as_ref().unwrap().read(cx).stats();
        stats["bounds"] = serde_json::json!(*self.polling.readout_bounds.lock().unwrap());
        stats["content_renders"] = self
            .polling
            .content
            .as_ref()
            .unwrap()
            .read(cx)
            .renders
            .into();
        stats["chart_paints"] = self
            .polling
            .chart
            .as_ref()
            .unwrap()
            .read(cx)
            .stats()
            .paints
            .into();
        stats
    }

    pub(super) fn polling_primary(&mut self) {
        if self.lock.locked() || self.runtime.borrow().pending_action().is_some() {
            return;
        }
        let Some(device) = self.selected.first().copied() else {
            self.polling.set_status(Status::NeedDevice);
            return;
        };
        let resume = self.polling.session_device == Some(device)
            && self.runtime.borrow().status().is_some_and(|status| {
                status.phase == CapturePhase::Paused && !capture_incomplete(&status)
            });
        self.capture_command(if resume { "resume" } else { "start" });
    }
    pub(super) fn polling_end(&mut self) {
        if !self.polling.has_session() || self.runtime.borrow().pending_action().is_some() {
            return;
        }
        self.capture_command("reset");
    }
    pub(super) fn polling_select(&mut self, key: u32) {
        if self.lock.locked()
            || self.runtime.borrow().pending_action().is_some()
            || self.runtime.borrow().device_catalog.choice(key).is_none()
        {
            return;
        }
        self.selected = vec![key];
        self.selection_initialized = true;
        self.runtime.borrow_mut().preferred_device = Some(key);
        self.device_menu = false;
    }
    pub(super) fn refresh_polling_devices(&mut self) {
        if self.lock.locked() || self.runtime.borrow().pending_action().is_some() {
            return;
        }
        if let Err(error) = self.runtime.borrow_mut().enqueue_control(
            self.receiver,
            "discover",
            clicksync_core::protocol::ControlCommand::RescanDevices,
        ) {
            self.control_error = Some(error);
        }
    }
    pub(super) fn update_polling_status(&mut self, status: &AppStatus) -> bool {
        let missing = self
            .selected
            .first()
            .is_some_and(|key| !status.devices.iter().any(|device| device.key == *key));
        if missing {
            self.selected.clear();
            self.selection_initialized = true;
            self.lock.release();
            if let Some(backend) = &self.runtime.borrow().backend {
                backend.set_capture_permitted(false);
            }
        }
        let no_devices = self.runtime.borrow().device_catalog.choices().is_empty();
        let disconnected = missing
            || self.polling.status == Status::Disconnected
            || (self.polling.has_session()
                && status.capture_fault == Some(crate::backend::CaptureFault::DeviceDisconnected));
        let state = if disconnected && self.selected.is_empty() {
            if no_devices {
                Status::Disconnected
            } else {
                Status::NeedDevice
            }
        } else if self.polling.has_session() && capture_incomplete(status) {
            if status.capture_fault == Some(crate::backend::CaptureFault::DeviceDisconnected) {
                Status::Reconnected
            } else {
                Status::Incomplete
            }
        } else if no_devices {
            Status::NoDevices
        } else if status.phase == CapturePhase::Collecting {
            Status::Measuring
        } else if self
            .polling
            .refreshed_until
            .is_some_and(|end| Instant::now() < end)
        {
            Status::Refreshed
        } else if self.polling.has_session() {
            Status::Paused
        } else if self.polling.status == Status::Cleared {
            Status::Cleared
        } else if self.polling.status == Status::NeedDevice && self.selected.is_empty() {
            Status::NeedDevice
        } else {
            Status::Ready
        };
        missing | self.polling.set_status(state)
    }

    pub(super) fn polling_discovered(&mut self) {
        self.polling.refreshed_until = Some(Instant::now() + Duration::from_millis(1200));
        self.polling.set_status(Status::Refreshed);
    }

    pub(super) fn polling_control(&mut self, action: &str) {
        self.polling.revision += 1;
        match action {
            "start" => {
                self.polling.pause_ms = None;
                self.polling.session_device = self.selected.first().copied();
                self.polling.frame = None;
                self.polling.current_text = "--".into();
                self.polling.empty_text = "--".into();
                self.polling.quality = RateStatus::WarmingUp;
                self.polling.displayed_at = None;
                self.polling.refreshed_until = None;
                self.polling.clearing_since = None;
            }
            "resume" => {
                self.polling.quality = RateStatus::WarmingUp;
                self.polling.displayed_at = None;
            }
            "pause" | "stop" => {
                let prefix = self
                    .runtime
                    .borrow()
                    .backend
                    .as_ref()
                    .and_then(|b| b.latest_prefix());
                self.polling.pause_ms = prefix.as_ref().map(|p| self.polling.domain_now(p));
            }
            "reset" => {
                self.polling.clearing_since = Some(Instant::now());
                self.domain_reset += 1;
                self.runtime.borrow().polling_stream.clear(self.receiver.0);
            }
            _ => {}
        }
    }

    pub(super) fn accept_polling_sample(&mut self) -> bool {
        if !self.lock.locked() || self.polling.clearing_since.is_some() {
            return false;
        }
        let frame = self.runtime.borrow().polling_stream.reader().latest();
        let generation = self.runtime.borrow().status().map(|s| s.generation);
        let Some(frame) = frame.filter(|frame| {
            frame.revision == self.polling.revision
                && generation.is_some_and(|g| {
                    g >= self.page_generation
                        && frame.belongs_to(self.receiver.0, g, self.domain_reset)
                })
        }) else {
            return false;
        };
        if frame.metrics.status == RateStatus::Valid && !frame.fresh(Instant::now()) {
            return self.polling.accept_metrics(
                &PollingMetrics {
                    status: RateStatus::Delayed,
                    ..frame.metrics.clone()
                },
                !frame.history.is_empty(),
                false,
            );
        }
        if self.polling.frame.as_ref().is_some_and(|old| {
            Arc::ptr_eq(old, &frame)
                || (old.identity() == frame.identity()
                    && frame.received_sequence < old.received_sequence)
        }) {
            return false;
        }
        let peak_changed = self.polling.frame.as_ref().map(|f| f.metrics.peak_rate)
            != Some(frame.metrics.peak_rate);
        let changed = self
            .polling
            .accept_metrics(&frame.metrics, !frame.history.is_empty(), false);
        self.polling.frame = Some(frame);
        changed || peak_changed
    }

    pub(super) fn render_polling(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> gpui::Stateful<gpui::Div> {
        let slots = self.polling.readout.as_ref().map_or(4, |readout| {
            readout.read(cx).slots_for(&self.polling.current_text)
        });
        let main_width = f32::from(window.viewport_size().width) - 136.0 - 56.0 - 344.0;
        let hero_size = 168.0f32
            .min((main_width - 70.0) / (slots.max(4) as f32 * 0.6))
            .max(90.0);
        let ink = design::Palette::for_theme(self.settings.dark).text;
        let readout = self
            .polling
            .readout
            .get_or_insert_with(|| cx.new(|_| readout::Readout::new(hero_size, ink, window)))
            .clone();
        let readout_revision = readout.update(cx, |readout, _| {
            readout.update(&self.polling.current_text, hero_size, ink, window)
        });
        let metrics = self
            .polling
            .frame
            .as_ref()
            .filter(|_| self.polling.has_session());
        let key = ContentKey {
            dark: self.settings.dark,
            english: self.settings.english,
            status: self.polling.status,
            pending: self.runtime.borrow().pending_action().is_some(),
            locked: self.lock.locked(),
            has_session: self.polling.has_session(),
            device_menu: self.device_menu,
            selected: self.selected.first().copied(),
            current: self.polling.current_text.clone(),
            empty: self.polling.empty_text.clone(),
            peak: metrics.map(|frame| frame.metrics.peak_rate),
            error: localized_ui_error(
                &self.control_error,
                &self.backend_error,
                self.settings.english,
            ),
            quality: self.polling.quality,
        };
        if self.polling.clearing_since.is_some() || self.polling.content_key.as_ref() != Some(&key)
        {
            self.polling.content_key = Some(key);
            self.invalidate_polling_content(cx);
        }
        let opacity = self.polling.clear_opacity(Instant::now());
        if self.polling.clearing_since.is_some() {
            window.request_animation_frame();
        }
        let host = cx.entity().downgrade();
        let content = self
            .polling
            .content
            .get_or_insert_with(|| {
                cx.new(|_| Content {
                    host,
                    #[cfg(feature = "ui-restoration-probe")]
                    renders: 0,
                })
            })
            .clone();
        let chart = self
            .polling
            .chart
            .get_or_insert_with(|| cx.new(|_| history::HistoryChart::new()))
            .clone();
        self.sync_polling_history(cx);
        let bounds = self.polling.history_bounds.clone();
        let readout_bounds = self.polling.readout_bounds.clone();
        // Independent sibling caches isolate animation notifications from
        // the control tree and from the other animated view.
        div()
            .id("polling-frame")
            .relative()
            .size_full()
            .child(gpui::AnyView::from(content).cached(div().size_full().style().clone()))
            .child(
                canvas(
                    move |_, window, cx| {
                        let [x, y, width, height] = *bounds.lock().unwrap();
                        let mut chart = gpui::AnyView::from(chart)
                            .cached(div().size_full().style().clone())
                            .into_any_element();
                        chart.prepaint_as_root(
                            gpui::point(px(x), px(y)),
                            size(px(width), px(height)).into(),
                            window,
                            cx,
                        );
                        chart
                    },
                    |_, mut chart, window, cx| chart.paint(window, cx),
                )
                .absolute()
                .inset_0()
                .size_full()
                .opacity(opacity),
            )
            .child(
                canvas(
                    move |_, window, cx| {
                        let [x, y, width, height] = *readout_bounds.lock().unwrap();
                        // A revision creates a fresh cache scope immediately
                        // when data/style changes during host rendering.
                        let mut readout = div()
                            .id(("polling-readout", readout_revision))
                            .size_full()
                            .child(
                                gpui::AnyView::from(readout)
                                    .cached(div().size_full().style().clone()),
                            )
                            .into_any_element();
                        readout.prepaint_as_root(
                            gpui::point(px(x), px(y)),
                            size(px(width), px(height)).into(),
                            window,
                            cx,
                        );
                        readout
                    },
                    |_, mut readout, window, cx| readout.paint(window, cx),
                )
                .absolute()
                .inset_0()
                .size_full()
                .opacity(opacity),
            )
    }

    fn render_polling_content(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> gpui::Stateful<gpui::Div> {
        let palette = design::Palette::for_theme(self.settings.dark);
        let typography = design::Typography::for_language(self.settings.english);
        let now = Instant::now();
        let (status_alpha, status_moving) = self.polling.status_motion.sample(now);
        if status_moving {
            window.request_animation_frame();
        }
        let pending = self.runtime.borrow().pending_action().is_some();
        let locked = self.lock.locked();
        let has_session = self.polling.has_session();
        let enabled = !locked && !pending;
        let frame = self.polling.frame.as_ref().filter(|_| has_session);
        let metrics = frame.map(|frame| &frame.metrics);
        let density = ((f32::from(window.viewport_size().height) - 300.0) / 640.0).clamp(0.68, 1.0);
        let readout_size = self.polling.readout.as_ref().unwrap().read(cx).size();
        let unit_left = (readout_size.width
            + self
                .polling
                .readout
                .as_ref()
                .unwrap()
                .read(cx)
                .visible_width())
            / 2.0
            + px(14.0);
        let (pill, notes) = match self.polling.status {
            Status::Incomplete => (
                self.text("数据不完整", "Incomplete data"),
                self.text(
                    "本次测量已中断，可重新测试或结束并清空。",
                    "Measurement interrupted. Start a new test or clear the results.",
                ),
            ),
            Status::Reconnected => (
                self.text("需要重新测试", "New test required"),
                self.text(
                    "鼠标已连接，上次测量已中断。可重新测试或清空结果。",
                    "Mouse connected. The previous measurement was interrupted. Start a new test or clear the results.",
                ),
            ),
            Status::Measuring if self.polling.quality != RateStatus::Valid => {
                match self.polling.quality {
                    RateStatus::WarmingUp => (
                        self.text("采样中", "Sampling"),
                        self.text(
                            "持续移动鼠标，等待完整测量窗口。",
                            "Keep moving while a complete measurement window fills.",
                        ),
                    ),
                    RateStatus::Delayed => (
                        self.text("数据滞后", "Data delayed"),
                        self.text(
                            "保留上次读数，等待采集处理跟上。",
                            "Holding the last reading while capture catches up.",
                        ),
                    ),
                    RateStatus::Incomplete => (
                        self.text("数据不完整", "Incomplete data"),
                        self.text(
                            "本次测量不可继续，请重新开始。",
                            "Start a new measurement to obtain complete data.",
                        ),
                    ),
                    RateStatus::OutOfRange => (
                        self.text("超出范围", "Out of range"),
                        self.text(
                            "报告率超过当前测量范围。",
                            "The report rate exceeds the measurement range.",
                        ),
                    ),
                    RateStatus::Valid => unreachable!(),
                }
            }
            Status::Measuring => (
                self.text("检测中", "Measuring"),
                self.text(
                    "移动鼠标，查看实时回报率。",
                    "Move the mouse to measure its report rate.",
                ),
            ),
            Status::Paused => (
                self.text("已暂停", "Paused"),
                self.text(
                    "读数与曲线已保留，可追加记录。",
                    "Readings and trace are saved. Resume to add more reports.",
                ),
            ),
            Status::Cleared => (
                self.text("已清空", "Cleared"),
                self.text(
                    "本次结果已清空，可以重新开始测试。",
                    "Results cleared. Ready for a new test.",
                ),
            ),
            Status::Disconnected => (
                self.text("设备已断开", "Disconnected"),
                self.text(
                    "连接鼠标并重新选择设备后开始测试。",
                    "Reconnect the mouse and select a device to start again.",
                ),
            ),
            Status::NoDevices => (
                self.text("没有设备", "No devices"),
                self.text(
                    "连接并移动鼠标以确认设备，必要时重新扫描。",
                    "Connect and move a mouse to identify it, then rescan if needed.",
                ),
            ),
            Status::NeedDevice => (
                self.text("请选择设备", "Select a device"),
                if has_session {
                    self.text(
                        "本次测量已中断，请重新选择鼠标后测试。",
                        "Measurement interrupted. Select a mouse to start a new test.",
                    )
                } else {
                    self.text(
                        "选择目标鼠标后开始测试。",
                        "Select a mouse to start testing.",
                    )
                },
            ),
            Status::Refreshed => (
                self.text("设备已刷新", "Devices refreshed"),
                self.text(
                    "设备列表已更新，可以开始测试。",
                    "Device list updated. Ready to start testing.",
                ),
            ),
            Status::Ready => (
                self.text("准备就绪", "Ready"),
                self.text(
                    "选择目标鼠标，开始后持续移动。",
                    "Choose a mouse, start the test, then keep moving it.",
                ),
            ),
        };
        let mut metrics_row = div()
            .relative()
            .flex()
            .flex_shrink_0()
            .border_t_1()
            .border_color(rgb(palette.hairline))
            .pt(px(20.0))
            .pb(px(28.0));
        for (title, value) in [
            (
                self.text("峰值档位", "PEAK TIER"),
                metrics
                    .filter(|m| m.peak_rate > 0)
                    .map_or("--".into(), |metrics| format!("{} Hz", metrics.peak_rate)),
            ),
            (
                self.text("空包率", "EMPTY PACKETS"),
                self.polling.empty_text.clone(),
            ),
        ] {
            metrics_row = metrics_row.child(
                div()
                    .relative()
                    .flex_1()
                    .min_w_0()
                    .flex_shrink_0()
                    .flex()
                    .flex_col()
                    .items_center()
                    .gap(px(8.0))
                    .child(
                        div()
                            .relative()
                            .text_size(px(13.0))
                            .line_height(px(20.0))
                            .text_color(rgb(palette.muted))
                            .child(title),
                    )
                    .child(
                        div()
                            .relative()
                            .font_family(design::DISPLAY_FONT)
                            .text_size(px(30.0))
                            .line_height(px(38.0))
                            .child(value),
                    ),
            );
        }
        let left = div()
            .relative()
            .flex_1()
            .min_w_0()
            .flex()
            .flex_col()
            .child(
                div()
                    .relative()
                    .h(px(100.0))
                    .flex_shrink_0()
                    .flex()
                    .justify_between()
                    .items_start()
                    .gap(px(24.0))
                    .child(
                        div()
                            .font_family(design::DISPLAY_FONT)
                            .font_weight(gpui::FontWeight::MEDIUM)
                            .text_size(px(40.0))
                            .line_height(px(48.0))
                            .text_color(rgb(palette.muted))
                            .child("REPORT RATE"),
                    )
                    .child(
                        div()
                            .h(px(48.0))
                            .flex_shrink_0()
                            .flex()
                            .items_center()
                            .gap(px(8.0))
                            .opacity(status_alpha)
                            .text_size(px(13.0))
                            .text_color(rgb(palette.muted))
                            .child(div().size(px(6.0)).rounded_full().bg(rgb(if locked {
                                palette.accent
                            } else {
                                palette.hairline
                            })))
                            .child(pill),
                    ),
            )
            .child(
                div()
                    .relative()
                    .flex_1()
                    .min_h(px(210.0))
                    .flex()
                    .flex_col()
                    .items_center()
                    .justify_center()
                    .child(
                        div()
                            .relative()
                            .child(readout::placeholder(
                                self.polling.readout_bounds.clone(),
                                readout_size,
                            ))
                            .child(
                                div()
                                    .absolute()
                                    .left(unit_left)
                                    .bottom(px(20.0))
                                    .font_family(design::DISPLAY_FONT)
                                    .text_size(px(30.0))
                                    .text_color(rgb(palette.muted))
                                    .child("Hz"),
                            ),
                    ),
            )
            .child(metrics_row)
            .child(
                div()
                    .relative()
                    .h(px(320.0 * density))
                    .mb(px(6.0))
                    .flex_shrink_0()
                    .flex()
                    .flex_col()
                    .child(
                        div()
                            .relative()
                            .flex_shrink_0()
                            .flex()
                            .items_center()
                            .justify_between()
                            .gap(px(24.0))
                            .mb(px(12.0))
                            .text_color(rgb(palette.muted))
                            .child(
                                div()
                                    .text_size(px(16.0))
                                    .line_height(px(24.0))
                                    .font_weight(gpui::FontWeight::SEMIBOLD)
                                    .text_color(rgb(palette.text))
                                    .child(self.text("回报率历史", "RATE HISTORY")),
                            )
                            .child(
                                div()
                                    .text_size(px(13.0))
                                    .line_height(px(20.0))
                                    .child(self.text("最近 3 秒", "LAST 3 SECONDS")),
                            ),
                    )
                    .child(history::placeholder(self.polling.history_bounds.clone())),
            );
        let choices = self.runtime.borrow().device_catalog.choices().to_vec();
        let name = self.selected.first().and_then(|key| {
            self.runtime
                .borrow()
                .device_catalog
                .label(*key)
                .map(str::to_owned)
        });
        let anchor = device_picker::Anchor::default();
        let mut selector = device_picker::text_trigger(
            "polling-device",
            name.as_deref(),
            palette,
            self.settings.english,
            enabled,
            &anchor,
        )
        .text_size(px(18.0))
        .on_click(cx.listener(|host, _, _, cx| {
            if !host.lock.locked() && host.runtime.borrow().pending_action().is_none() {
                host.device_menu = !host.device_menu;
                cx.notify();
            }
        }));
        if self.device_menu {
            let mut menu = device_picker::popup(
                "polling-device-menu",
                palette,
                anchor,
                cx.listener(|host, _, _, cx| {
                    host.device_menu = false;
                    cx.notify();
                }),
            );
            for choice in &choices {
                let key = choice.key;
                menu = menu.child(
                    device_picker::option(
                        format!("polling-device-{key}"),
                        choice,
                        self.selected.first() == Some(&key),
                        palette,
                        self.settings.english,
                    )
                    .border_0()
                    .on_click(cx.listener(move |host, _, _, cx| {
                        cx.stop_propagation();
                        host.polling_select(key);
                        cx.notify();
                    })),
                );
            }
            if choices.is_empty() {
                menu = menu.child(self.text(
                    "请连接并移动鼠标以确认设备。",
                    "Connect and move a mouse to identify it.",
                ));
            }
            selector = selector.child(gpui::deferred(menu).with_priority(1));
        }
        let primary_text = if locked {
            self.text("正在测试", "TESTING")
        } else if has_session
            && matches!(
                self.polling.status,
                Status::Incomplete
                    | Status::Disconnected
                    | Status::Reconnected
                    | Status::NeedDevice
            )
        {
            self.text("重新测试", "START NEW TEST")
        } else if has_session && self.polling.session_device == self.selected.first().copied() {
            self.text("继续测试", "RESUME TEST")
        } else {
            self.text("开始测试", "START TEST")
        };
        let actions = div()
            .mt(px(32.0))
            .flex_shrink_0()
            .flex()
            .flex_col()
            .gap(px(12.0))
            .child(
                design::button(
                    "polling-primary",
                    primary_text,
                    palette,
                    self.settings.english,
                    true,
                    enabled && !self.selected.is_empty(),
                )
                .border_0()
                .relative()
                .w_full()
                .h(px(48.0))
                .flex()
                .items_center()
                .justify_center()
                .when(locked, |button| button.opacity(0.55))
                .on_click(cx.listener(|host, _, _, cx| {
                    host.polling_primary();
                    cx.notify();
                })),
            )
            .child(
                div()
                    .relative()
                    .min_h(px(44.0))
                    .text_size(px(14.0))
                    .line_height(px(22.0))
                    .text_color(rgb(palette.muted))
                    .opacity(status_alpha)
                    .child(notes),
            )
            .when_some(
                localized_ui_error(
                    &self.control_error,
                    &self.backend_error,
                    self.settings.english,
                ),
                |actions, error| {
                    actions.child(
                        div()
                            .text_size(px(14.0))
                            .line_height(px(22.0))
                            .text_color(rgb(palette.muted))
                            .child(error),
                    )
                },
            )
            .child(
                div()
                    .flex()
                    .gap(px(12.0))
                    .child(
                        div()
                            .id("polling-refresh")
                            .relative()
                            .flex_1()
                            .min_w_0()
                            .h(px(36.0))
                            .flex()
                            .items_center()
                            .gap(px(8.0))
                            .text_size(px(14.0))
                            .when(enabled, |action| {
                                action
                                    .cursor_pointer()
                                    .hover(move |style| style.text_color(rgb(palette.accent)))
                                    .active(|style| style.opacity(0.65))
                            })
                            .when(!enabled, |action| action.opacity(0.4))
                            .child(
                                div()
                                    .font_family(design::DISPLAY_FONT)
                                    .text_size(px(20.0))
                                    .text_color(rgb(palette.accent))
                                    .child("+"),
                            )
                            .child(self.text("重新扫描", "Scan devices"))
                            .on_click(cx.listener(|host, _, _, cx| {
                                host.refresh_polling_devices();
                                cx.notify();
                            })),
                    )
                    .child(
                        div()
                            .id("polling-end")
                            .relative()
                            .flex_1()
                            .min_w_0()
                            .h(px(36.0))
                            .flex()
                            .items_center()
                            .justify_end()
                            .gap(px(8.0))
                            .text_size(px(14.0))
                            .text_color(rgb(if has_session && !pending {
                                palette.text
                            } else {
                                palette.muted
                            }))
                            .when(has_session && !pending, |action| {
                                action
                                    .cursor_pointer()
                                    .hover(move |style| style.text_color(rgb(palette.accent)))
                                    .active(|style| style.opacity(0.65))
                            })
                            .when(!has_session || pending, |action| action.opacity(0.4))
                            .child(
                                div()
                                    .font_family(design::DISPLAY_FONT)
                                    .text_size(px(20.0))
                                    .text_color(rgb(palette.accent))
                                    .child("−"),
                            )
                            .child(self.text("结束并清空", "End & clear"))
                            .on_click(cx.listener(|host, _, _, cx| {
                                host.polling_end();
                                cx.notify();
                            })),
                    ),
            );
        let shortcuts = div()
            .relative()
            .flex_shrink_0()
            .border_t_1()
            .border_color(rgb(palette.hairline))
            .pt(px(16.0))
            .child(
                div()
                    .relative()
                    .min_h(px(32.0))
                    .flex()
                    .items_center()
                    .justify_between()
                    .child(
                        div()
                            .text_size(px(typography.micro))
                            .text_color(rgb(palette.muted))
                            .child(self.text("暂停快捷键", "PAUSE SHORTCUT")),
                    )
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap(px(8.0))
                            .text_size(px(typography.secondary))
                            .child(
                                div()
                                    .px(px(8.0))
                                    .py(px(3.0))
                                    .border_1()
                                    .border_color(rgb(palette.hairline))
                                    .font_family(design::DISPLAY_FONT)
                                    .child("Esc"),
                            )
                            .child(
                                div()
                                    .text_color(rgb(palette.muted))
                                    .child(self.text("或", "or")),
                            )
                            .child(self.text("鼠标右键", "right-click")),
                    ),
            );
        let rail = div()
            .relative()
            .w(px(344.0))
            .flex_shrink_0()
            .border_l_1()
            .border_color(rgb(palette.hairline))
            .pl(px(32.0))
            .flex()
            .flex_col()
            .child(
                div()
                    .text_size(px(22.0))
                    .line_height(px(32.0))
                    .font_weight(gpui::FontWeight::BOLD)
                    .mb(px(28.0))
                    .child(self.text("测试设置", "TEST SETUP")),
            )
            .child(
                div()
                    .text_size(px(13.0))
                    .line_height(px(20.0))
                    .text_color(rgb(palette.muted))
                    .mb(px(10.0))
                    .child(self.text("目标设备", "TARGET DEVICE")),
            )
            .child(div().relative().flex_shrink_0().child(selector))
            .child(
                div()
                    .relative()
                    .mt(px(16.0))
                    .flex_shrink_0()
                    .text_size(px(14.0))
                    .line_height(px(22.0))
                    .text_color(rgb(palette.muted))
                    .child(self.text(
                        "仅统计非零位移的鼠标报告。",
                        "Measures reports with non-zero mouse movement.",
                    )),
            )
            .child(actions)
            .child(div().flex_1().min_h(px(24.0)))
            .child(shortcuts);
        div()
            .id("polling-workspace")
            .size_full()
            .overflow_hidden()
            .flex()
            .gap(px(56.0))
            .px(px(68.0))
            .pt(px(32.0))
            .pb(px(24.0))
            .bg(rgb(palette.background))
            .text_color(rgb(palette.text))
            .font(design::ui_font())
            .text_size(px(typography.body))
            .when(!self.custom_chrome, |body| {
                body.track_focus(&self.focus).on_key_down(
                    cx.listener(|host, event, window, cx| host.keyboard(event, window, cx)),
                )
            })
            .child(left.opacity(self.polling.clear_opacity(Instant::now())))
            .child(rail)
    }
}

#[cfg(test)]
mod presentation_tests {
    use super::*;

    fn valid(rate: i32) -> PollingMetrics {
        PollingMetrics {
            current_rate: Some(rate),
            peak_rate: rate,
            status: RateStatus::Valid,
            ..Default::default()
        }
    }

    #[test]
    fn warmup_and_resume_hold_the_last_reading_without_creating_zero() {
        let mut page = PollingPage {
            session_device: Some(1),
            ..Default::default()
        };
        assert_eq!(page.current_text, "--");
        page.accept_metrics(&PollingMetrics::default(), false, false);
        assert_eq!(page.current_text, "--");
        page.accept_metrics(&valid(8000), true, false);
        assert_eq!(page.current_text, "8000");
        page.accept_metrics(&PollingMetrics::default(), true, false);
        assert_eq!(page.current_text, "8000");
        assert_eq!(page.quality, RateStatus::WarmingUp);
        page.displayed_at = None;
        page.accept_metrics(&valid(4000), true, false);
        assert_eq!(page.current_text, "4000");
    }

    #[test]
    fn paused_and_retiring_readouts_cannot_be_overwritten_by_late_results() {
        let mut page = PollingPage {
            session_device: Some(1),
            ..Default::default()
        };
        page.accept_metrics(&valid(8000), true, false);
        assert!(!page.accept_metrics(&valid(0), false, true));
        assert_eq!(page.current_text, "8000");
        assert!(page.history_present);
        let start = Instant::now();
        page.clearing_since = Some(start);
        assert!(!page.accept_metrics(&valid(0), false, false));
        assert_eq!(page.clear_opacity(start), 1.0);
        assert_eq!(page.clear_opacity(start + Duration::from_millis(80)), 0.5);
        assert!(!page.finish_clear(start + Duration::from_millis(159)));
        assert_eq!(page.current_text, "8000");
        assert!(page.finish_clear(start + Duration::from_millis(160)));
        assert_eq!(page.current_text, "--");
        assert!(!page.has_session());
        assert!(page.frame.is_none());
    }

    #[test]
    fn genuine_zero_is_displayed_but_delayed_and_invalid_measurements_are_not() {
        let mut page = PollingPage {
            session_device: Some(1),
            ..Default::default()
        };
        page.accept_metrics(&valid(1000), true, false);
        for status in [
            RateStatus::Delayed,
            RateStatus::Incomplete,
            RateStatus::OutOfRange,
        ] {
            page.accept_metrics(
                &PollingMetrics {
                    status,
                    ..Default::default()
                },
                true,
                false,
            );
            assert_eq!(page.current_text, "1000");
            assert_eq!(page.quality, status);
        }
        page.displayed_at = None;
        page.accept_metrics(&valid(0), true, false);
        assert_eq!(page.current_text, "0");
        assert_eq!(page.quality, RateStatus::Valid);
    }
}
