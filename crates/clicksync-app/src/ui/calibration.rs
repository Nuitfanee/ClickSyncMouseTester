use super::*;
use clicksync_chart::analysis::calibration::CalibrationFrame;

type Submission = (u64, u64, u64, crate::backend::CapturePhase, bool);

#[derive(Default)]
pub(super) struct CalibrationPage {
    pub frame: Option<Arc<CalibrationFrame>>,
    pub rearm_pending: bool,
    pub disconnected: bool,
    device: Option<u32>,
    device_changed: bool,
    copied_angle: Option<f64>,
    pub(super) guide: Option<gpui::Entity<motion_guide::MotionGuide>>,
    submitted: Option<Submission>,
    view: Rc<RefCell<TraceView>>,
}

#[derive(Default)]
struct TraceView {
    current: Option<[f64; 3]>,
    drawn: Option<std::time::Instant>,
}

impl TraceView {
    fn update(
        &mut self,
        target: [f64; 3],
        locked: bool,
        now: std::time::Instant,
    ) -> ([f64; 3], bool) {
        let current = self.current.get_or_insert(target);
        let elapsed = self.drawn.replace(now).map_or(0.0, |drawn| {
            now.saturating_duration_since(drawn).as_secs_f64()
        });
        // Preserve the 60 Hz response while advancing by elapsed display time.
        let factor: f64 = if locked { 0.18 } else { 0.24 };
        let factor = -(elapsed * 60.0 * (1.0 - factor).ln()).exp_m1();
        for i in 0..3 {
            current[i] += (target[i] - current[i]) * factor;
        }
        let active = (target[0] - current[0]).abs() >= 0.15
            || (target[1] - current[1]).abs() >= 0.15
            || (target[2] - current[2]).abs() >= 0.002;
        if !active {
            *current = target;
        }
        (*current, active)
    }
}

impl CalibrationPage {
    pub(super) fn accepts_device(&self, device: Option<u32>) -> bool {
        self.device.is_none() || self.device == device
    }

    pub(super) fn observe_device(&mut self, device: u32) {
        self.device_changed = self.device.is_some_and(|previous| previous != device);
        if self.device_changed {
            self.frame = None;
            self.copied_angle = None;
            self.view = Rc::default();
        }
        self.device = Some(device);
    }
    pub(super) fn now(&self) -> f64 {
        crate::capture_lock::now_ms()
    }
    pub fn should_submit(
        &mut self,
        prefix: &crate::backend::SessionPrefix,
        reset: u64,
        locked: bool,
    ) -> bool {
        let key = (
            prefix.generation,
            reset,
            prefix.received_sequence,
            prefix.phase,
            locked,
        );
        if self.submitted == Some(key) {
            return false;
        }
        self.submitted = Some(key);
        true
    }
}

impl GpuiHost {
    pub(super) fn pump_calibration_motion(&mut self) -> bool {
        if !self.calibration.rearm_pending
            || !self.lock.locked()
            || self.runtime.borrow().pending_action().is_some()
        {
            return false;
        }
        let runtime = self.runtime.clone();
        if let Some(prefix) = runtime
            .borrow()
            .backend
            .as_ref()
            .and_then(|b| b.latest_prefix())
        {
            if matches!(
                prefix.phase,
                crate::backend::CapturePhase::Collecting | crate::backend::CapturePhase::Draining
            ) {
                return false;
            }
            if prefix
                .datasets
                .iter()
                .any(|(_, snapshot)| snapshot.event_count() > 0)
                && self.domain_frame.as_ref().is_none_or(|frame| {
                    frame.generation != prefix.generation
                        || frame.reset != self.domain_reset
                        || frame.received_sequence != prefix.received_sequence
                        || frame.calibration.locked
                })
            {
                return false;
            }
        }
        self.calibration.rearm_pending = false;
        if let Some(backend) = &runtime.borrow().backend {
            backend.set_capture_permitted(true);
        }
        match runtime
            .borrow_mut()
            .observe_motion(self.receiver, None, true, None)
        {
            Ok(_) => self.control_error = None,
            Err(error) => {
                self.lock.release();
                self.control_error = Some(error);
            }
        }
        self.observe_capture();
        true
    }
    pub(super) fn calibration_status(&mut self, status: &crate::backend::AppStatus) -> bool {
        let removed = self.business_input.first.is_some_and(|first| {
            !status
                .devices
                .iter()
                .any(|device| device.key == first.device_key)
        });
        if removed {
            self.calibration.disconnected = true;
            self.capture_command("pause");
            return true;
        }
        false
    }
    pub(super) fn reset_calibration(&mut self) {
        self.lock.release();
        self.runtime.borrow_mut().cancel_motion();
        self.page_generation = self.runtime.borrow_mut().request_page_reset();
        self.domain_reset += 1;
        self.domain_frame = None;
        self.calibration = CalibrationPage::default();
        self.business_input = business_input::BusinessInput::default();
        self.selected.clear();
        self.selection_initialized = true;
    }
    pub(super) fn copy_calibration(&self, cx: &mut gpui::App) {
        if let Some(angle) = self
            .calibration
            .frame
            .as_ref()
            .and_then(|frame| frame.recommended_angle)
        {
            cx.write_to_clipboard(gpui::ClipboardItem::new_string(format!("{angle:.1}")));
        }
    }
    pub(super) fn calibration_hint(&self) -> &'static str {
        let frame = self.calibration.frame.as_deref();
        if self.calibration.disconnected {
            return self.text(
                "当前鼠标已断开；点击继续，同一鼠标保留测量，更换鼠标将重新计数。",
                "Mouse disconnected. Click to resume; the same mouse retains its measurement, a different mouse starts fresh.",
            );
        }
        if frame.is_some_and(|frame| frame.recommended_angle.is_some()) {
            return if self.lock.locked() {
                self.text("平均补偿角已可用：结合笔间波动和左右差判断是否可重复；右键暂停。",
                    "Mean correction is ready. Check stroke spread and left/right difference for repeatability; right click pauses.")
            } else {
                self.text("平均补偿角已保留：点击继续；更换鼠标会重新计数，应用设置后请重置复测。",
                    "Mean correction retained. Click to resume; a different mouse starts fresh. Reset after applying the setting.")
            };
        }
        if self.lock.locked() {
            if self.calibration.device_changed {
                return self.text(
                    "已更换测量鼠标，正在重新计数；保持日常握姿自然横扫。",
                    "A different mouse was selected. Measurement restarted; swipe naturally with your normal grip.",
                );
            }
            if frame.is_some_and(|frame| frame.has_data) || self.business_input.first.is_some() {
                self.text("测量中：移动当前测量鼠标左右来回滑动至少 30 次。右键暂停。",
                    "Measuring: move the current measurement mouse left and right at least 30 times. Right click pauses.")
            } else {
                self.text("已锁定：等待任意鼠标产生第一条位移来开始测量。右键暂停。",
                    "Locked and waiting for the first movement from any mouse to begin measuring. Right click pauses.")
            }
        } else if frame.is_some_and(|frame| frame.has_data) {
            self.text(
                "已暂停：点击继续；同一鼠标保留测量，更换鼠标将重新计数。",
                "Paused. Click to resume; the same mouse retains its measurement, a different mouse starts fresh.",
            )
        } else if self
            .runtime
            .borrow()
            .status()
            .is_none_or(|status| status.devices.is_empty())
        {
            self.text(
                "未检测到可用鼠标，连接任意鼠标后再开始传感器角度校准。",
                "No mouse is currently available, so angle calibration cannot start.",
            )
        } else {
            self.text("点击测量区域开始并锁定光标；锁定后移动任意鼠标开始测量。",
                "Click the measurement field to lock the cursor, then move any mouse to begin measuring.")
        }
    }
    fn calibration_quality(&self) -> String {
        if let Some(quality) = capture_quality(
            self.runtime.borrow().status().as_ref(),
            self.settings.english,
        ) {
            return quality;
        }
        let Some(frame) = self
            .calibration
            .frame
            .as_ref()
            .filter(|frame| frame.has_data)
        else {
            return String::new();
        };
        let (zh, en) = quality_message(frame.quality_reason);
        let message = self.text(zh, en);
        if self.settings.english {
            format!(
                "Accepted {} · Rejected {} — {message}",
                frame.accepted_strokes, frame.rejected_strokes
            )
        } else {
            format!(
                "有效笔画 {} · 未采用 {} — {message}",
                frame.accepted_strokes, frame.rejected_strokes
            )
        }
    }
    pub(super) fn render_calibration(
        &mut self,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) -> gpui::Stateful<gpui::Div> {
        let palette = design::Palette::for_theme(self.settings.dark);
        let frame = self.calibration.frame.clone();
        let recommended = frame.as_ref().and_then(|frame| frame.recommended_angle);
        let header = div()
            .grid()
            .grid_cols(3)
            .items_center()
            .gap(px(24.0))
            .py(px(22.0))
            .flex_shrink_0()
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(6.0))
                    .child(
                        div()
                            .text_size(px(26.0))
                            .font_weight(gpui::FontWeight::SEMIBOLD)
                            .child(self.text("找到自然的水平线", "Find your natural horizontal")),
                    )
                    .child(
                        div()
                            .text_size(px(13.0))
                            .text_color(rgb(palette.muted))
                            .child(self.text(
                                "保持日常握姿，左右往返移动鼠标。",
                                "Use your normal grip and move the mouse side to side.",
                            )),
                    ),
            )
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(2.0))
                    .items_center()
                    .child(
                        div()
                            .text_size(px(12.0))
                            .text_color(rgb(palette.muted))
                            .child(self.text("平均补偿角", "Mean correction")),
                    )
                    .child(
                        div()
                            .font_family(design::DISPLAY_FONT)
                            .text_size(px(38.0))
                            .line_height(px(44.0))
                            .child(recommended.map_or("—".into(), |angle| format!("{angle:.1}°"))),
                    ),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .justify_end()
                    .gap(px(24.0))
                    .child(
                        design::button(
                            "calibration-copy",
                            if recommended.is_some() && self.calibration.copied_angle == recommended
                            {
                                self.text("已复制", "Copied")
                            } else {
                                self.text("复制角度", "Copy angle")
                            },
                            palette,
                            self.settings.english,
                            recommended.is_some(),
                            recommended.is_some(),
                        )
                        .on_click(cx.listener(|host, _, _, cx| {
                            host.copy_calibration(cx);
                            host.calibration.copied_angle = host
                                .calibration
                                .frame
                                .as_ref()
                                .and_then(|frame| frame.recommended_angle);
                            cx.notify();
                        })),
                    )
                    .child(
                        design::button(
                            "calibration-reset",
                            self.text("重新测量", "Reset"),
                            palette,
                            self.settings.english,
                            false,
                            frame.as_ref().is_some_and(|frame| frame.has_data)
                                || self.lock.locked(),
                        )
                        .on_click(cx.listener(|host, _, _, cx| {
                            if host.lock.locked()
                                || host
                                    .calibration
                                    .frame
                                    .as_ref()
                                    .is_some_and(|frame| frame.has_data)
                            {
                                host.reset_calibration();
                                cx.notify();
                            }
                        })),
                    ),
            );
        let mut stats = div()
            .grid()
            .grid_cols(3)
            .items_center()
            .gap(px(24.0))
            .px(px(48.0))
            .whitespace_nowrap()
            .py(px(16.0))
            .border_b_1()
            .border_color(rgb(palette.hairline));
        for (zh, en, value) in [
            (
                "往返进度",
                "Swipes",
                format!("{} / 30", frame.as_ref().map_or(0, |f| f.swipe_count)),
            ),
            (
                "左右差",
                "L/R difference",
                frame
                    .as_ref()
                    .and_then(|f| f.direction_difference_degrees)
                    .map_or("--".into(), |v| format!("{v:.1}°")),
            ),
            (
                "笔间波动",
                "Stroke SD",
                frame
                    .as_ref()
                    .and_then(|f| f.stroke_spread_degrees)
                    .map_or("--".into(), |v| format!("{v:.1}°")),
            ),
        ] {
            stats = stats.child(
                div()
                    .flex()
                    .justify_center()
                    .items_center()
                    .gap(px(8.0))
                    .child(
                        div()
                            .flex_shrink_0()
                            .text_size(px(13.0))
                            .text_color(rgb(palette.muted))
                            .child(self.text(zh, en)),
                    )
                    .child(
                        div()
                            .w(px(96.0))
                            .flex_shrink_0()
                            .font(design::tabular_display_font())
                            .text_size(px(18.0))
                            .font_weight(gpui::FontWeight::MEDIUM)
                            .child(value),
                    ),
            );
        }
        let traces = frame.clone();
        let view = self.calibration.view.clone();
        let locked = self.lock.locked();
        let prepaint = frame.clone();
        let trace = canvas(
            move |bounds, window, _| {
                let target = trace_target(
                    prepaint.as_deref(),
                    f64::from(f32::from(bounds.size.width)),
                    f64::from(f32::from(bounds.size.height)),
                );
                let (view, active) =
                    view.borrow_mut()
                        .update(target, locked, std::time::Instant::now());
                if active {
                    window.request_animation_frame();
                }
                view
            },
            move |bounds, view, window, _| {
                let Some(frame) = &traces else { return };
                window.with_content_mask(Some(gpui::ContentMask { bounds }), |window| {
                    for stroke in &frame.traces {
                        if stroke.points.len() < 2 {
                            continue;
                        }
                        let mut path = gpui::PathBuilder::stroke(px(1.65));
                        for (i, p) in stroke.points.iter().enumerate() {
                            let point = gpui::point(
                                bounds.center().x + px(((p[0] - view[0]) * view[2]) as f32),
                                bounds.center().y + px(((p[1] - view[1]) * view[2]) as f32),
                            );
                            if i == 0 {
                                path.move_to(point);
                            } else {
                                path.line_to(point);
                            }
                        }
                        if let Ok(path) = path.build() {
                            window.paint_path(
                                path,
                                gpui::rgba(
                                    ((if stroke.current {
                                        palette.text
                                    } else {
                                        palette.muted
                                    }) << 8)
                                        | if stroke.current { 140 } else { 56 },
                                ),
                            );
                        }
                    }
                });
            },
        )
        .w_full()
        .flex_1();
        let empty = frame.as_ref().is_none_or(|frame| !frame.has_data);
        let available = self
            .runtime
            .borrow()
            .status()
            .is_some_and(|status| !status.devices.is_empty());
        let guide = self
            .calibration
            .guide
            .get_or_insert_with(|| cx.new(|_| motion_guide::MotionGuide::new(false, palette)))
            .clone();
        guide.update(cx, |guide, cx| {
            if guide.palette.background != palette.background {
                guide.palette = palette;
                cx.notify();
            }
        });
        let quality = self.calibration_quality();
        let surface = div()
            .id("calibration-field")
            .relative()
            .bg(rgb(palette.panel))
            .rounded(px(12.0))
            .flex()
            .flex_col()
            .flex_1()
            .min_h(px(320.0))
            .px(px(16.0))
            .pt(px(14.0))
            .pb(px(16.0))
            .cursor(gpui::CursorStyle::Crosshair)
            .child(
                div()
                    .text_size(px(13.0))
                    .text_color(rgb(palette.muted))
                    .child(self.calibration_hint()),
            )
            .when(!quality.is_empty(), |surface| {
                surface.child(
                    div()
                        .mt(px(6.0))
                        .text_size(px(12.0))
                        .text_color(rgb(palette.muted))
                        .child(quality),
                )
            })
            .child(div().h(px(10.0)).flex_shrink_0())
            .child(trace)
            .child(
                div()
                    .flex_shrink_0()
                    .text_size(px(12.0))
                    .text_color(rgb(palette.muted))
                    .child(self.text(
                        "初测前将旋转归零、关闭角度吸附并固定 DPI。核对驱动正负方向，应用后重置复测剩余偏角。",
                        "Start with rotation at zero, angle snapping off and fixed DPI. Check your driver's sign convention; reset and remeasure after applying.",
                    )),
            )
            .when(empty, |surface| {
                surface.child(
                    div()
                        .absolute()
                        .inset_0()
                        .flex()
                        .flex_col()
                        .items_center()
                        .justify_center()
                        .gap(px(10.0))
                        .child(guide)
                        .child(
                            div()
                                .text_size(px(22.0))
                                .font_weight(gpui::FontWeight::SEMIBOLD)
                                .child(if !available {
                                    self.text("连接鼠标后开始", "Connect a mouse to begin")
                                } else if locked {
                                    self.text("左右移动鼠标", "Move the mouse side to side")
                                } else {
                                    self.text("点击这里开始校准", "Click here to calibrate")
                                }),
                        )
                        .child(
                            div()
                                .text_size(px(14.0))
                                .text_color(rgb(palette.muted))
                                .child(self.text(
                                    "自然横扫至少 30 次，弧线也可以参与测量。",
                                    "Make at least 30 natural swipes. Gentle arcs are welcome.",
                                )),
                        )
                        .child(
                            div()
                                .text_size(px(12.0))
                                .text_color(rgb(palette.muted))
                                .child(self.text(
                                    "右键或 Esc 暂停 · 点击继续",
                                    "Right click or Esc to pause · Click to resume",
                                )),
                        ),
                )
            })
            .on_click(cx.listener(|host, _, window, cx| {
                host.focus.focus(window);
                host.begin_calibration_motion();
                cx.notify();
            }))
            .on_mouse_down(
                gpui::MouseButton::Right,
                cx.listener(|host, _, _, cx| {
                    if host.lock.locked() {
                        host.capture_command("pause");
                        cx.notify();
                    }
                }),
            );
        div()
            .id("calibration-page")
            .size_full()
            .flex()
            .flex_col()
            .font_family(design::Typography::body_font(self.settings.english))
            .bg(rgb(palette.background))
            .text_color(rgb(palette.text))
            .px(px(32.0))
            .pb(px(20.0))
            .child(header)
            .child(
                div()
                    .flex_1()
                    .flex()
                    .flex_col()
                    .rounded(px(12.0))
                    .overflow_hidden()
                    .border_1()
                    .border_color(rgb(palette.hairline))
                    .child(stats)
                    .child(surface),
            )
    }
}

fn quality_message(reason: &str) -> (&'static str, &'static str) {
    match reason {
        "InsufficientProgress" => (
            "继续补充自然横扫；次数是最低采集量，不代表测量精度。",
            "Add more natural swipes. The count is a minimum sample size, not a precision guarantee.",
        ),
        "Imbalance" => (
            "左右两侧都需要足够的有效笔画。",
            "Both directions need enough accepted strokes.",
        ),
        "DirectionMismatch" => (
            "左右方向差较大，平均角只是折中，无法同时对齐两侧。",
            "Left and right directions differ. The mean is a compromise and cannot align both sides.",
        ),
        "HighDispersion" => (
            "每笔方向变化较大，单一角度的代表性有限；保持日常握姿复测。",
            "Stroke directions vary. One angle may not represent your motion; repeat with your normal grip.",
        ),
        "TooManyOutliers" => (
            "部分笔画方向不同；波动统计保留这些笔画，请检查握姿或动作幅度是否变化。",
            "Some strokes have different directions; they remain in the spread statistic. Check grip or range changes.",
        ),
        "HighRejection" => (
            "未采用的笔画较多；使用足够幅度的自然横扫，检查采集是否正常。",
            "Many strokes were rejected. Use sufficiently wide natural swipes and check capture.",
        ),
        "Good" => (
            "结合左右差与笔间波动判断重复性；笔间波动是方向内标准差，不是误差范围。",
            "Check L/R difference and stroke SD for repeatability. SD is within-direction variation, not an error bound.",
        ),
        _ => ("正在记录自然横扫。", "Recording natural swipes."),
    }
}

fn capture_quality(status: Option<&crate::backend::AppStatus>, english: bool) -> Option<String> {
    let status = status?;
    let dropped = status.overflow;
    if dropped > 0 {
        return Some(if english {
            format!(
                "The capture queue overflowed and dropped {dropped} packets. This result may be distorted by tool-side pressure, so reset and measure again."
            )
        } else {
            format!(
                "采集队列曾溢出并丢弃 {dropped} 个包；当前结果可能被工具侧压力影响，建议重置后重测。"
            )
        });
    }
    if !capture_incomplete(status) {
        return None;
    }
    let error = capture_status_error(status)?;
    Some(if english {
        format!(
            "Capture failed or data is incomplete. This result is not reliable; reset and measure again. {error}"
        )
    } else {
        format!("采集失败或数据不完整，当前结果不可靠；请重置后重测。{error}")
    })
}

fn trace_target(frame: Option<&CalibrationFrame>, width: f64, height: f64) -> [f64; 3] {
    let mut range = [
        f64::INFINITY,
        f64::NEG_INFINITY,
        f64::INFINITY,
        f64::NEG_INFINITY,
    ];
    if let Some(frame) = frame {
        for point in frame.traces.iter().flat_map(|stroke| &stroke.points) {
            range[0] = range[0].min(point[0]);
            range[1] = range[1].max(point[0]);
            range[2] = range[2].min(point[1]);
            range[3] = range[3].max(point[1]);
        }
    }
    if !range.iter().all(|value| value.is_finite()) {
        range = [-200.0, 200.0, -80.0, 80.0];
    }
    let scale = ((width - 52.0).max(32.0) / (range[1] - range[0]).max(1.0))
        .min((height - 52.0).max(32.0) / (range[3] - range[2]).max(1.0))
        .clamp(0.08, 1.8);
    [
        (range[0] + range[1]) * 0.5,
        (range[2] + range[3]) * 0.5,
        scale,
    ]
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn calibration_submission_has_no_wall_clock_expiry_and_tracks_input_identity() {
        let mut page = CalibrationPage::default();
        let mut prefix = crate::backend::SessionPrefix {
            generation: 1,
            received_sequence: 1,
            received_records: 1,
            phase: crate::backend::CapturePhase::Paused,
            complete: true,
            datasets: Vec::new(),
        };
        assert!(page.should_submit(&prefix, 1, false));
        assert!(!page.should_submit(&prefix, 1, false));
        prefix.received_sequence += 1;
        assert!(page.should_submit(&prefix, 1, false));
        assert!(page.should_submit(&prefix, 2, false));
        assert!(page.should_submit(&prefix, 2, true));
        prefix.generation += 1;
        assert!(page.should_submit(&prefix, 2, true));
        prefix.phase = crate::backend::CapturePhase::Collecting;
        assert!(page.should_submit(&prefix, 2, true));
        assert!(!page.should_submit(&prefix, 2, true));
    }

    #[test]
    fn selecting_another_device_clears_the_displayed_measurement_and_copy_state() {
        let mut page = CalibrationPage::default();
        assert!(page.accepts_device(None));
        page.observe_device(1);
        assert!(page.accepts_device(Some(1)));
        let mut engine = clicksync_chart::analysis::calibration::CalibrationEngine::default();
        engine.push(10, 1, 1.0);
        page.frame = Some(Arc::new(engine.frame(1.0)));
        page.copied_angle = Some(1.0);
        page.observe_device(1);
        assert!(page.frame.is_some());
        assert_eq!(page.copied_angle, Some(1.0));
        assert!(!page.device_changed);
        page.observe_device(2);
        assert!(page.frame.is_none());
        assert!(page.copied_angle.is_none());
        assert!(page.device_changed);
        assert!(page.accepts_device(Some(2)));
        assert!(!page.accepts_device(Some(1)));
        assert!(!page.accepts_device(None));
    }

    #[test]
    fn calibration_warnings_describe_directions_and_spread() {
        assert!(
            quality_message("DirectionMismatch")
                .1
                .contains("compromise")
        );
        assert!(quality_message("HighDispersion").1.contains("normal grip"));
        assert!(quality_message("HighRejection").1.contains("rejected"));
        assert!(quality_message("Good").1.contains("not an error bound"));
    }

    #[test]
    fn capture_quality_reports_non_overflow_faults_even_without_a_business_frame() {
        use crate::backend::{AppStatus, CapturePhase};
        let mut status = AppStatus {
            phase: CapturePhase::Faulted,
            error: Some("retention limit reached".into()),
            ..AppStatus::default()
        };
        assert!(
            capture_quality(Some(&status), true)
                .unwrap()
                .contains("retention limit reached")
        );
        assert!(
            capture_quality(Some(&status), false)
                .unwrap()
                .contains("不可靠")
        );
        status.error = None;
        assert!(capture_quality(Some(&status), true).is_some());
        status.phase = CapturePhase::Paused;
        assert!(capture_quality(Some(&status), true).is_some());
        status.complete = true;
        assert!(capture_quality(Some(&status), true).is_none());
        status.error = Some("device selection was rejected".into());
        assert!(capture_quality(Some(&status), true).is_none());
        status.overflow = 7;
        assert!(
            capture_quality(Some(&status), true)
                .unwrap()
                .contains("dropped 7 packets")
        );
        assert!(capture_quality(None, true).is_none());
    }
    #[test]
    fn trace_fits_without_stretch_and_converges_from_current_view() {
        let mut view = TraceView::default();
        let start = std::time::Instant::now();
        let frame = Duration::from_secs_f64(1.0 / 60.0);
        assert_eq!(
            view.update([0.0, 0.0, 1.8], false, start).0,
            [0.0, 0.0, 1.8]
        );
        let (intermediate, active) = view.update([500.0, 40.0, 0.8], true, start + frame);
        assert!(active);
        assert!((intermediate[0] - 90.0).abs() < 0.00001);
        let (reversed, _) = view.update([0.0, 0.0, 1.8], false, start + frame * 2);
        assert!(reversed[0] > 0.0 && reversed[0] < intermediate[0]);
        for i in 3..=103 {
            view.update([0.0, 0.0, 1.8], false, start + frame * i);
        }
        assert_eq!(
            view.update([0.0, 0.0, 1.8], false, start + frame * 104),
            ([0.0, 0.0, 1.8], false)
        );
        assert_eq!(trace_target(None, 1000.0, 500.0), [0.0, 0.0, 1.8]);
    }

    #[test]
    fn trace_camera_has_one_response_at_every_refresh_rate_and_duplicate_instant() {
        let start = std::time::Instant::now();
        for locked in [true, false] {
            let factor: f64 = if locked { 0.18 } else { 0.24 };
            let expected = 500.0 * (1.0 - (1.0 - factor).powi(12));
            for hz in [30, 60, 120, 240] {
                let mut view = TraceView::default();
                view.update([0.0, 0.0, 1.8], locked, start);
                for frame in 0..=hz / 5 {
                    let now = start + Duration::from_secs_f64(f64::from(frame) / f64::from(hz));
                    let drawn = view.update([500.0, 40.0, 0.8], locked, now);
                    assert_eq!(view.update([500.0, 40.0, 0.8], locked, now), drawn);
                }
                assert!((view.current.unwrap()[0] - expected).abs() < 0.00001);
            }
        }
        let mut view = TraceView::default();
        view.update([0.0, 0.0, 1.8], true, start);
        assert_eq!(
            view.update([500.0, 40.0, 0.8], true, start),
            ([0.0, 0.0, 1.8], true)
        );
    }
}
