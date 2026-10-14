use super::*;
use clicksync_chart::analysis::keys::TimingStatistics;
use gpui::{Div, FontWeight, SharedString, Stateful};
use std::time::Instant;

struct Feedback {
    from: f32,
    velocity: f32,
    target: f32,
    start: Option<Instant>,
    response: f32,
    damping: f32,
}
impl Feedback {
    fn new(value: f32) -> Self {
        Self {
            from: value,
            velocity: 0.0,
            target: value,
            start: None,
            response: 0.28,
            damping: 0.55,
        }
    }
    fn state(&self, now: Instant) -> (f32, f32) {
        let Some(start) = self.start else {
            return (self.target, 0.0);
        };
        let elapsed = now.saturating_duration_since(start).as_secs_f32();
        let omega = std::f32::consts::TAU / self.response;
        let decay_rate = self.damping * omega;
        let frequency = omega * (1.0 - self.damping * self.damping).sqrt();
        let displacement = self.from - self.target;
        let (sin, cos) = (frequency * elapsed).sin_cos();
        let decay = (-decay_rate * elapsed).exp();
        // Exact oscillator: frame partitioning cannot add energy or change the trajectory.
        let position = decay
            * (displacement * cos + (self.velocity + decay_rate * displacement) / frequency * sin);
        let velocity = decay
            * (self.velocity * cos
                - (decay_rate * self.velocity + omega * omega * displacement) / frequency * sin);
        (self.target + position, velocity)
    }
    fn target(&mut self, target: f32, response: f32, now: Instant) {
        if self.target == target {
            return;
        }
        (self.from, self.velocity) = self.state(now);
        self.target = target;
        self.response = response;
        self.damping = if target > 0.0 { 0.70 } else { 0.55 };
        self.start = Some(now);
    }
    fn kick(&mut self, now: Instant) {
        (self.from, self.velocity) = self.state(now);
        self.target = 0.0;
        self.response = 0.28;
        self.damping = 0.55;
        let omega = std::f32::consts::TAU / self.response;
        // Bound total spring energy, not position, so rapid input never snaps or runs away.
        let available_velocity = omega * (1.35_f32.powi(2) - self.from.powi(2)).max(0.0).sqrt();
        self.velocity = (self.velocity + 32.0).max(0.0).min(available_velocity);
        self.start = Some(now);
    }
    fn sample(&mut self, now: Instant) -> (f32, bool) {
        let (position, velocity) = self.state(now);
        let omega = std::f32::consts::TAU / self.response;
        let frequency = omega * (1.0 - self.damping * self.damping).sqrt();
        let displacement = position - self.target;
        let envelope =
            displacement.hypot((velocity + self.damping * omega * displacement) / frequency);
        if envelope < 0.002 {
            self.start = None;
            self.from = self.target;
            self.velocity = 0.0;
            (self.target, false)
        } else {
            (position, true)
        }
    }
}

#[derive(Default, Clone, Copy)]
enum KeyStatus {
    #[default]
    Idle,
    Picking,
    Cancelled,
    Exited,
    Selected,
    Down,
    Up,
}

pub(super) struct KeysPage {
    press: [Feedback; 6],
    actions: [Feedback; 3],
    wheel_motion: [Feedback; 2],
    double_until: [Option<Instant>; 6],
    wheel_start: [Option<Instant>; 2],
    feedback_wake: Option<gpui::Task<()>>,
    wake_deadline: Option<Instant>,
    status: KeyStatus,
    pub(super) name: String,
}
impl Default for KeysPage {
    fn default() -> Self {
        Self {
            press: std::array::from_fn(|_| Feedback::new(0.0)),
            actions: std::array::from_fn(|_| Feedback::new(0.0)),
            wheel_motion: std::array::from_fn(|_| Feedback::new(0.0)),
            double_until: [None; 6],
            wheel_start: [None; 2],
            feedback_wake: None,
            wake_deadline: None,
            status: KeyStatus::Idle,
            name: String::new(),
        }
    }
}
impl KeysPage {
    fn wheel_active(&mut self, index: usize, now: Instant) -> bool {
        if self.wheel_start[index]
            .is_some_and(|start| now.saturating_duration_since(start) < Duration::from_millis(150))
        {
            return true;
        }
        self.wheel_start[index] = None;
        false
    }

    fn wheel_deadline(&self) -> Option<Instant> {
        self.wheel_start
            .iter()
            .flatten()
            .map(|start| *start + Duration::from_millis(150))
            .min()
    }

    fn feedback_deadline(&self) -> Option<Instant> {
        self.wheel_deadline()
            .into_iter()
            .chain(self.double_until.iter().flatten().copied())
            .min()
    }

    pub(super) fn reset_transient(&mut self) {
        self.reset_measurement_feedback();
        for action in &mut self.actions {
            *action = Feedback::new(0.0);
        }
    }
    fn reset_measurement_feedback(&mut self) {
        for press in &mut self.press {
            *press = Feedback::new(0.0);
        }
        self.wheel_motion = std::array::from_fn(|_| Feedback::new(0.0));
        self.double_until = [None; 6];
        self.wheel_start = [None; 2];
        self.feedback_wake = None;
        self.wake_deadline = None;
        self.status = if self.name.is_empty() {
            KeyStatus::Idle
        } else {
            KeyStatus::Selected
        };
    }
    fn double_active(&mut self, index: usize, now: Instant) -> bool {
        if self.double_until[index].is_some_and(|end| now < end) {
            return true;
        }
        self.double_until[index] = None;
        false
    }
    fn press_offset(&mut self, index: usize, pressed: bool, now: Instant) -> (f32, bool) {
        let target = if pressed { 1.0 } else { 0.0 };
        self.press[index].target(target, if pressed { 0.18 } else { 0.28 }, now);
        let (value, moving) = self.press[index].sample(now);
        (value * if index == 5 { 6.0 } else { 4.5 }, moving)
    }

    fn input_press(&mut self, index: usize, pressed: bool, released: bool, now: Instant) {
        self.press[index].target(
            if pressed { 1.0 } else { 0.0 },
            if pressed { 0.18 } else { 0.28 },
            now,
        );
        // A tap can finish between display frames; retain a visible, bounded impact.
        if released && !pressed && self.press[index].state(now).0 < 0.5 {
            self.press[index].kick(now);
        }
    }

    fn wheel_impulse(&mut self, index: usize, now: Instant) {
        self.wheel_start[index] = Some(now);
        self.wheel_motion[index].kick(now);
    }

    fn wheel_feedback(&mut self, index: usize, now: Instant) -> (f32, bool) {
        self.wheel_motion[index].sample(now)
    }
}

pub(super) fn format_threshold(value: f64) -> String {
    format!("{value:.2}")
        .trim_end_matches('0')
        .trim_end_matches('.')
        .to_owned()
}
fn milliseconds(value: Option<f64>) -> String {
    value.filter(|value| value.is_finite()).map_or_else(
        || "--".into(),
        |value| {
            if value.abs() < 10.0 {
                format!("{value:.2} ms")
            } else {
                format!("{value:.1} ms")
            }
        },
    )
}
fn display_name(key: KeyIdentity) -> String {
    use windows::Win32::UI::Input::KeyboardAndMouse::{
        GetKeyNameTextW, MAPVK_VK_TO_VSC_EX, MapVirtualKeyW,
    };
    let mapped = if key.scan_code == 0 {
        unsafe { MapVirtualKeyW(key.virtual_key as u32, MAPVK_VK_TO_VSC_EX) }
    } else {
        key.scan_code as u32
    };
    let scan = mapped & 0xff;
    if scan != 0 {
        let parameter = (scan << 16)
            | if key.extended || mapped & 0xff00 != 0 {
                1 << 24
            } else {
                0
            };
        let mut text = [0u16; 64];
        let length = unsafe { GetKeyNameTextW(parameter as i32, &mut text) };
        if length > 0 {
            return String::from_utf16_lossy(&text[..length as usize]);
        }
    }
    format!("VK_{:04X}", key.virtual_key)
}

impl GpuiHost {
    pub(super) fn keys_input(
        &mut self,
        input: &clicksync_core::protocol::ControlInput,
        time: f64,
        active: bool,
        text_entry: bool,
    ) -> bool {
        use clicksync_core::protocol::ControlInput;
        if self.page != Page::Keys || self.menu.visible() {
            return false;
        }
        match *input {
            ControlInput::Mouse { flags, data } if active => self.keys_mouse(flags, data, time),
            ControlInput::Keyboard {
                virtual_key,
                scan_code,
                extended,
                down,
            } => {
                self.keys_keyboard(
                    KeyIdentity {
                        virtual_key,
                        scan_code,
                        extended,
                    },
                    down,
                    time,
                    active,
                    text_entry,
                );
            }
            _ => return false,
        }
        true
    }
    pub(super) fn keys_mouse(&mut self, flags: u16, data: u16, timestamp: f64) {
        let before = self.keys.buttons.each_ref().map(|stats| {
            (
                stats.down_count,
                stats.up_count,
                stats.double_click_count,
                stats.is_pressed,
            )
        });
        let wheel = [self.keys.wheel_up, self.keys.wheel_down];
        self.keys.mouse(flags, data, timestamp);
        let now = Instant::now();
        for (index, stats) in self.keys.buttons.iter().enumerate() {
            let (down, up, double, pressed) = before[index];
            self.keys_ui.input_press(
                index,
                stats.is_pressed,
                stats.up_count != up && (pressed || stats.down_count != down),
                now,
            );
            if stats.double_click_count != double {
                self.keys_ui.double_until[index] = Some(now + Duration::from_millis(350));
            }
        }
        for (index, count) in [self.keys.wheel_up, self.keys.wheel_down]
            .into_iter()
            .enumerate()
        {
            if count != wheel[index] {
                self.keys_ui.wheel_impulse(index, now);
            }
        }
    }
    pub(super) fn keys_keyboard(
        &mut self,
        key: KeyIdentity,
        down: bool,
        time: f64,
        active: bool,
        text_entry: bool,
    ) {
        let picking = self.keys.picking;
        let before = (
            self.keys.custom.down_count,
            self.keys.custom.up_count,
            self.keys.custom.double_click_count,
            self.keys.custom.is_pressed,
        );
        self.keys.keyboard(key, down, time, active, text_entry);
        if picking && down {
            if key.virtual_key == 27 {
                self.keys_ui.status = KeyStatus::Cancelled;
            } else {
                self.keys_ui.name = display_name(key);
                self.keys_ui.status = KeyStatus::Selected;
                self.keys_ui.double_until[5] = None;
                self.keys_ui.press[5] = Feedback::new(0.0);
            }
        } else if before.0 != self.keys.custom.down_count {
            self.keys_ui.status = KeyStatus::Down;
        } else if before.1 != self.keys.custom.up_count {
            self.keys_ui.status = KeyStatus::Up;
        }
        if before.2 != self.keys.custom.double_click_count {
            self.keys_ui.double_until[5] = Some(Instant::now() + Duration::from_millis(350));
        }
        if !picking {
            self.keys_ui.input_press(
                5,
                self.keys.custom.is_pressed,
                before.1 != self.keys.custom.up_count && before.3,
                Instant::now(),
            );
        }
    }
    pub(super) fn pick_custom_key(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.commit_edit(cx);
        self.focus.focus(window);
        self.keys.picking = !self.keys.picking;
        self.keys_ui.status = if self.keys.picking {
            KeyStatus::Picking
        } else {
            KeyStatus::Exited
        };
    }
    pub(super) fn reset_key_statistics(&mut self) {
        self.keys.reset_statistics();
        self.keys_ui.reset_measurement_feedback();
        if self.keys.picking {
            self.keys_ui.status = KeyStatus::Picking;
        }
    }
    pub(super) fn reset_custom_key(&mut self) {
        self.keys.reset_custom();
        self.keys_ui.name.clear();
        self.keys_ui.status = KeyStatus::Idle;
        self.keys_ui.double_until[5] = None;
        self.keys_ui.press[5] = Feedback::new(0.0);
    }
    fn key_status(&self) -> &'static str {
        match self.keys_ui.status {
            KeyStatus::Idle => self.text("选择一个键，开始检测", "Choose a key to begin"),
            KeyStatus::Picking => self.text(
                "按下键盘上的任意键 · Esc 取消",
                "Press any key · Esc to cancel",
            ),
            KeyStatus::Cancelled | KeyStatus::Exited => {
                self.text("已取消选键", "Selection cancelled")
            }
            KeyStatus::Selected => self.text("已就绪，按键即可测试", "Ready to test"),
            KeyStatus::Down => self.text("正在按住", "Held down"),
            KeyStatus::Up => self.text("已抬起", "Released"),
        }
    }

    pub(super) fn render_keys(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        let palette = design::Palette::for_theme(self.settings.dark);
        let now = Instant::now();
        let compact = f32::from(window.viewport_size().height) < 850.0;
        let inset = if compact { 24.0 } else { 40.0 };
        let mut animated = false;
        let mut cards = div()
            .grid()
            .grid_cols(5)
            .gap(px(12.0))
            .h(px(if compact { 320.0 } else { 352.0 }))
            .flex_shrink_0();
        for (index, chinese, english_name) in [
            (0, "左键", "Left"),
            (1, "中键", "Middle"),
            (2, "右键", "Right"),
            (3, "前进", "Forward"),
            (4, "后退", "Back"),
        ] {
            let stats = &self.keys.buttons[index];
            let pressed = stats.is_pressed;
            let (offset, moving) = self.keys_ui.press_offset(index, pressed, now);
            let double = self.keys_ui.double_active(index, now);
            let pressure = if pressed {
                1.0
            } else {
                (offset / 4.5).clamp(0.0, 1.0)
            };
            animated |= moving;
            cards = cards.child(
                panel(palette, 14.0)
                    .top(px(offset))
                    .min_w_0()
                    .h_full()
                    .border_color(tint(
                        palette.hairline,
                        palette.accent,
                        if double { 1.0 } else { pressure },
                    ))
                    .bg(tint(palette.panel, palette.selected, pressure))
                    .px(px(18.0))
                    .py(px(16.0))
                    .flex()
                    .flex_col()
                    .justify_between()
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .justify_between()
                            .child(
                                label(self.text(chinese, english_name), palette.text, 15.0)
                                    .font_weight(FontWeight::SEMIBOLD),
                            )
                            .child(label(
                                if pressed {
                                    self.text("按下", "Down")
                                } else {
                                    self.text("抬起", "Up")
                                },
                                if pressed {
                                    palette.accent
                                } else {
                                    palette.muted
                                },
                                12.0,
                            )),
                    )
                    .child(
                        div()
                            .flex()
                            .flex_col()
                            .items_center()
                            .gap(px(2.0))
                            .py(px(6.0))
                            .child(number(
                                stats.double_click_count.to_string(),
                                if double { palette.accent } else { palette.text },
                                if compact { 36.0 } else { 42.0 },
                            ))
                            .child(label(
                                self.text("双击次数", "Double clicks"),
                                palette.muted,
                                12.0,
                            )),
                    )
                    .child(
                        div()
                            .flex()
                            .flex_col()
                            .gap(px(6.0))
                            .pb(px(12.0))
                            .border_b_1()
                            .border_color(rgb(palette.hairline))
                            .child(metric_row(
                                self.text("按下计数", "Down count"),
                                stats.down_count.to_string(),
                                palette,
                                15.0,
                            ))
                            .child(metric_row(
                                self.text("抬起计数", "Up count"),
                                stats.up_count.to_string(),
                                palette,
                                15.0,
                            )),
                    )
                    .child(self.key_timing(stats, true, palette))
                    .child(self.key_timing(stats, false, palette)),
            );
        }
        let mouse_editor = self.key_threshold(4, cx);
        let reset = self
            .key_action(0, palette, now, &mut animated, cx)
            .on_click(cx.listener(|host, _, window, cx| {
                host.commit_edit(cx);
                host.focus.focus(window);
                host.reset_key_statistics();
                cx.notify();
            }));
        let threshold = panel(palette, 14.0)
            .px(px(20.0))
            .py(px(14.0))
            .flex_shrink_0()
            .flex()
            .items_center()
            .gap(px(16.0))
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .flex()
                    .flex_col()
                    .gap(px(4.0))
                    .child(
                        label(
                            self.text("鼠标双击阈值", "Mouse double-click threshold"),
                            palette.text,
                            14.0,
                        )
                        .font_weight(FontWeight::SEMIBOLD),
                    )
                    .child(label(
                        self.text(
                            "建议 60–120 ms，数值越小，判定越严格。",
                            "Recommended: 60–120 ms. Lower is stricter.",
                        ),
                        palette.muted,
                        12.0,
                    )),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap(px(8.0))
                    .child(mouse_editor)
                    .child(label("ms", palette.muted, 12.0)),
            )
            .child(
                div()
                    .w(px(1.0))
                    .h(px(24.0))
                    .mx(px(4.0))
                    .bg(rgb(palette.hairline)),
            )
            .child(reset);

        let mut wheel = panel(palette, 14.0)
            .w(px(156.0))
            .p(px(16.0))
            .flex_shrink_0()
            .flex()
            .flex_col()
            .gap(px(8.0))
            .child(
                label(self.text("滚轮", "Scroll wheel"), palette.text, 14.0)
                    .font_weight(FontWeight::SEMIBOLD),
            );
        for (index, arrow, count, chinese, english_name) in [
            (0, "↑", self.keys.wheel_up, "上滚", "Scroll up"),
            (1, "↓", self.keys.wheel_down, "下滚", "Scroll down"),
        ] {
            let pulse = self.keys_ui.wheel_active(index, now);
            let (impulse, moving) = self.keys_ui.wheel_feedback(index, now);
            animated |= moving;
            let direction = if index == 0 { -1.0 } else { 1.0 };
            let glow = impulse.abs().min(1.0);
            wheel = wheel.child(
                div()
                    .flex_1()
                    .min_h_0()
                    .w_full()
                    .rounded(px(8.0))
                    .bg(tint(palette.panel, palette.selected, glow))
                    .flex()
                    .flex_col()
                    .items_center()
                    .justify_center()
                    .gap(px(4.0))
                    .child(
                        div()
                            .relative()
                            .left(px(8.0))
                            .top(px(direction * impulse * 2.0))
                            .w_full()
                            .flex()
                            .items_center()
                            .gap(px(8.0))
                            .child(
                                div()
                                    .relative()
                                    .w(px(24.0))
                                    .h(px(32.0))
                                    .flex_shrink_0()
                                    .child(
                                        label(arrow, palette.accent, 22.0)
                                            .absolute()
                                            .w_full()
                                            .text_center()
                                            .top(px(direction * (impulse * 8.0 - 6.0)))
                                            .opacity(glow * 0.22),
                                    )
                                    .child(
                                        label(
                                            arrow,
                                            if pulse || moving {
                                                palette.accent
                                            } else {
                                                palette.muted
                                            },
                                            22.0,
                                        )
                                        .relative()
                                        .w_full()
                                        .text_center()
                                        .top(px(direction * impulse * 8.0)),
                                    ),
                            )
                            .child(
                                number(
                                    count.to_string(),
                                    if pulse { palette.accent } else { palette.text },
                                    30.0,
                                )
                                .flex_1()
                                .min_w_0()
                                .text_center(),
                            ),
                    )
                    .child(
                        label(self.text(chinese, english_name), palette.muted, 12.0)
                            .relative()
                            .left(px(8.0)),
                    ),
            );
            if index == 0 {
                wheel = wheel.child(div().h(px(1.0)).flex_shrink_0().bg(rgb(palette.hairline)));
            }
        }
        let pick = self
            .key_action(1, palette, now, &mut animated, cx)
            .on_click(cx.listener(|host, _, window, cx| {
                host.pick_custom_key(window, cx);
                cx.notify();
            }));
        let reset_custom = self
            .key_action(2, palette, now, &mut animated, cx)
            .on_click(cx.listener(|host, _, window, cx| {
                host.commit_edit(cx);
                host.focus.focus(window);
                host.reset_custom_key();
                cx.notify();
            }));
        let pressed = self.keys.custom.is_pressed;
        let (offset, moving) = self.keys_ui.press_offset(5, pressed, now);
        animated |= moving;
        let double = self.keys_ui.double_active(5, now);
        let mut counts = div().grid().grid_cols(3).gap(px(20.0));
        for (name, count, highlight) in [
            (
                self.text("按下次数", "Presses"),
                self.keys.custom.down_count,
                false,
            ),
            (
                self.text("抬起次数", "Releases"),
                self.keys.custom.up_count,
                false,
            ),
            (
                self.text("双击次数", "Double clicks"),
                self.keys.custom.double_click_count,
                double,
            ),
        ] {
            counts = counts.child(
                div()
                    .min_w_0()
                    .flex()
                    .flex_col()
                    .items_center()
                    .text_center()
                    .gap(px(3.0))
                    .child(number(
                        count.to_string(),
                        if highlight {
                            palette.accent
                        } else {
                            palette.text
                        },
                        30.0,
                    ))
                    .child(label(name, palette.muted, 12.0)),
            );
        }
        let key_editor = self.key_threshold(5, cx);
        let key_name = if self.keys.picking {
            self.text("请按键", "Press a key").to_owned()
        } else if self.keys.selected.is_some() {
            self.keys_ui.name.clone()
        } else {
            self.text("未选择", "No key selected").to_owned()
        };
        let key_size = if key_name.chars().count() > 14 {
            17.0
        } else {
            28.0
        };
        let custom = panel(palette, 14.0)
            .flex_1()
            .min_w_0()
            .p(px(24.0))
            .flex()
            .items_center()
            .gap(px(28.0))
            .child(
                div()
                    .w(px(248.0))
                    .h_full()
                    .max_h(px(260.0))
                    .flex_shrink_0()
                    .pr(px(28.0))
                    .border_r_1()
                    .border_color(rgb(palette.hairline))
                    .flex()
                    .flex_col()
                    .justify_between()
                    .gap(px(14.0))
                    .child(
                        label(
                            self.text("自定义键位检测", "Custom key detection"),
                            palette.text,
                            16.0,
                        )
                        .text_center()
                        .font_weight(FontWeight::SEMIBOLD),
                    )
                    .child(
                        div()
                            .relative()
                            .top(px(offset))
                            .w_full()
                            .min_h(px(94.0))
                            .flex()
                            .flex_col()
                            .items_center()
                            .text_center()
                            .justify_center()
                            .gap(px(10.0))
                            .child(
                                label(
                                    key_name,
                                    if pressed || self.keys.picking {
                                        palette.accent
                                    } else {
                                        palette.text
                                    },
                                    key_size,
                                )
                                .font_weight(FontWeight::SEMIBOLD),
                            )
                            .child(label(self.key_status(), palette.muted, 12.0)),
                    )
                    .child(
                        div()
                            .flex()
                            .justify_center()
                            .gap(px(8.0))
                            .child(pick)
                            .child(reset_custom),
                    ),
            )
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .h_full()
                    .max_h(px(260.0))
                    .flex()
                    .flex_col()
                    .justify_between()
                    .gap(px(16.0))
                    .child(counts)
                    .child(
                        div()
                            .grid()
                            .grid_cols(2)
                            .gap(px(24.0))
                            .pt(px(16.0))
                            .border_t_1()
                            .border_color(rgb(palette.hairline))
                            .child(self.custom_key_timing(true, palette))
                            .child(self.custom_key_timing(false, palette)),
                    )
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap(px(10.0))
                            .child(label(
                                self.text("双击阈值", "Double-click threshold"),
                                palette.muted,
                                12.0,
                            ))
                            .child(key_editor)
                            .child(label("ms", palette.muted, 12.0)),
                    ),
            );
        let deadline = self.keys_ui.feedback_deadline();
        if self.keys_ui.wake_deadline != deadline {
            self.keys_ui.feedback_wake = None;
            self.keys_ui.wake_deadline = deadline;
        }
        if self.keys_ui.feedback_wake.is_none()
            && let Some(deadline) = deadline
        {
            self.keys_ui.feedback_wake = Some(cx.spawn_in(window, async move |host, cx| {
                Timer::after(deadline.saturating_duration_since(Instant::now())).await;
                let _ = host.update(cx, |host, cx| {
                    host.keys_ui.feedback_wake = None;
                    host.keys_ui.wake_deadline = None;
                    if host.page == Page::Keys {
                        cx.notify();
                    }
                });
            }));
        }
        if animated {
            window.request_animation_frame();
        }
        div()
            .id("keys-page")
            .size_full()
            .overflow_y_scroll()
            .bg(rgb(palette.background))
            .text_color(rgb(palette.text))
            .font_family(design::Typography::body_font(self.settings.english))
            .track_focus(&self.focus)
            .on_key_down(cx.listener(Self::keyboard))
            .on_mouse_down(
                gpui::MouseButton::Left,
                cx.listener(|host, _, window, cx| {
                    host.commit_edit(cx);
                    host.focus.focus(window);
                    cx.notify();
                }),
            )
            .px(px(inset))
            .py(px(if compact { 20.0 } else { 30.0 }))
            .flex()
            .flex_col()
            .gap(px(14.0))
            .child(cards)
            .child(threshold)
            .child(
                div()
                    .flex()
                    .gap(px(14.0))
                    .flex_1()
                    .min_h(px(280.0))
                    .flex_shrink_0()
                    .child(wheel)
                    .child(custom),
            )
            .when(self.input_dropped > 0, |element| {
                element.child(label(
                    format!(
                        "{}: {}",
                        self.text("输入溢出", "Input overflow"),
                        self.input_dropped
                    ),
                    palette.accent,
                    12.0,
                ))
            })
    }

    pub(super) fn key_action_feedback(
        &mut self,
        index: usize,
        pressed: bool,
        cx: &mut Context<Self>,
    ) {
        let target = if pressed { 1.0 } else { 0.0 };
        self.keys_ui.actions[index].target(
            target,
            if pressed { 0.18 } else { 0.28 },
            Instant::now(),
        );
        cx.notify();
    }

    fn key_action(
        &mut self,
        index: usize,
        palette: design::Palette,
        now: Instant,
        animated: &mut bool,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        let (value, moving) = self.keys_ui.actions[index].sample(now);
        *animated |= moving;
        let offset = value * 2.5;
        let primary = index == 1 && !self.keys.picking;
        let pressed = self.keys_ui.actions[index].target != 0.0;
        let (id, title) = match index {
            0 => ("reset-keys", self.text("重置统计", "Reset statistics")),
            1 => (
                "pick-key",
                if self.keys.picking {
                    self.text("取消选键", "Cancel")
                } else if self.keys.selected.is_some() {
                    self.text("更换按键", "Change key")
                } else {
                    self.text("选择按键", "Choose key")
                },
            ),
            _ => ("reset-custom", self.text("清除按键", "Clear key")),
        };
        // The outer hit target stays still while its surface compresses.
        div()
            .id(id)
            .group(id)
            .h(px(36.0))
            .min_w(px(if index == 1 { 112.0 } else { 72.0 }))
            .flex_shrink_0()
            .cursor_pointer()
            .whitespace_nowrap()
            .on_mouse_down(
                gpui::MouseButton::Left,
                cx.listener(move |host, _, _, cx| {
                    host.key_action_feedback(index, true, cx);
                }),
            )
            .on_mouse_up(
                gpui::MouseButton::Left,
                cx.listener(move |host, _, _, cx| {
                    host.key_action_feedback(index, false, cx);
                }),
            )
            .on_mouse_up_out(
                gpui::MouseButton::Left,
                cx.listener(move |host, _, _, cx| {
                    host.key_action_feedback(index, false, cx);
                }),
            )
            .on_hover(cx.listener(move |host, hovered, _, cx| {
                if !hovered && host.keys_ui.actions[index].target != 0.0 {
                    host.key_action_feedback(index, false, cx);
                }
            }))
            .child(
                div()
                    .id(("key-action-surface", index))
                    .relative()
                    .top(px(offset))
                    .h_full()
                    .rounded(px(8.0))
                    .px(px(16.0))
                    .flex()
                    .items_center()
                    .justify_center()
                    .border_1()
                    .border_color(rgb(if primary {
                        palette.accent
                    } else {
                        palette.hairline
                    }))
                    .bg(rgb(if primary {
                        palette.accent
                    } else {
                        palette.panel
                    }))
                    .group_hover(id, move |style| {
                        style.bg(rgb(if primary {
                            palette.accent_hover
                        } else {
                            palette.hover
                        }))
                    })
                    .group_active(id, move |style| {
                        style
                            .bg(rgb(if primary {
                                palette.accent
                            } else {
                                palette.selected
                            }))
                            .opacity(0.88)
                    })
                    .when(pressed, |surface| {
                        surface
                            .bg(rgb(if primary {
                                palette.accent
                            } else {
                                palette.selected
                            }))
                            .opacity(0.88)
                    })
                    .child(
                        label(
                            title,
                            if primary {
                                palette.accent_text
                            } else {
                                palette.text
                            },
                            13.0,
                        )
                        .font_weight(FontWeight::MEDIUM),
                    ),
            )
    }

    fn key_threshold(&mut self, index: usize, cx: &mut Context<Self>) -> Stateful<Div> {
        let field = self.editors[index].clone();
        let value = self.edit_value(index);
        field.update(cx, |field, cx| {
            field.palette = design::Palette::for_theme(self.settings.dark);
            field.raised = true;
            field.compact = true;
            if self.edit != Some(index) && field.content.as_ref() != value {
                field.set_value(&value, cx);
            }
        });
        div()
            .id(SharedString::from(format!("key-threshold-{index}")))
            .w(px(88.0))
            .flex_shrink_0()
            .child(field)
            .on_mouse_down(
                gpui::MouseButton::Left,
                cx.listener(move |host, _, window, cx| {
                    if host.edit != Some(index) {
                        host.begin_edit(index, window, cx);
                    }
                    cx.stop_propagation();
                }),
            )
    }
    fn custom_key_timing(&self, down_down: bool, palette: design::Palette) -> Div {
        let stats = &self.keys.custom;
        let (title, current, minimum, average) = if down_down {
            (
                self.text("连按间隔", "Press interval"),
                stats.current_down_down_ms,
                stats.minimum_down_down_ms,
                stats.average_down_down_ms(),
            )
        } else {
            (
                self.text("按住时长", "Hold duration"),
                stats.current_down_up_ms,
                stats.minimum_down_up_ms,
                stats.average_down_up_ms(),
            )
        };
        div()
            .min_w_0()
            .flex()
            .flex_col()
            .items_center()
            .text_center()
            .gap(px(5.0))
            .child(label(title, palette.muted, 12.0))
            .child(number(milliseconds(current), palette.text, 22.0))
            .child(
                div()
                    .w_full()
                    .flex()
                    .flex_wrap()
                    .justify_center()
                    .gap_x(px(12.0))
                    .gap_y(px(3.0))
                    .children(
                        [
                            (self.text("最小", "Min"), minimum),
                            (self.text("平均", "Avg"), average),
                        ]
                        .into_iter()
                        .map(|(name, value)| {
                            div()
                                .flex()
                                .gap(px(4.0))
                                .child(label(name, palette.muted, 12.0).whitespace_nowrap())
                                .child(number(milliseconds(value), palette.muted, 12.0))
                        }),
                    ),
            )
    }

    fn key_timing(
        &self,
        stats: &TimingStatistics,
        down_down: bool,
        palette: design::Palette,
    ) -> Div {
        let (current, minimum, average) = if down_down {
            (
                stats.current_down_down_ms,
                stats.minimum_down_down_ms,
                stats.average_down_down_ms(),
            )
        } else {
            (
                stats.current_down_up_ms,
                stats.minimum_down_up_ms,
                stats.average_down_up_ms(),
            )
        };
        let title = if down_down {
            self.text("连按间隔", "Press interval")
        } else {
            self.text("按住时长", "Hold duration")
        };
        let mut footer = div().flex().flex_col().gap(px(2.0));
        for (name, value) in [
            (self.text("最小", "Min"), minimum),
            (self.text("平均", "Avg"), average),
        ] {
            footer = footer.child(
                div()
                    .flex()
                    .gap(px(3.0))
                    .w_full()
                    .justify_between()
                    .child(label(name, palette.muted, 12.0))
                    .child(number(milliseconds(value), palette.muted, 12.0)),
            );
        }
        div()
            .w_full()
            .min_w_0()
            .flex()
            .flex_col()
            .gap(px(6.0))
            .child(metric_row(title, milliseconds(current), palette, 14.0))
            .child(footer)
    }
}
fn label(text: impl Into<SharedString>, color: u32, size: f32) -> Div {
    div()
        .text_size(px(size))
        .line_height(px(size * 1.35))
        .text_color(rgb(color))
        .child(text.into())
}
fn number(text: impl Into<SharedString>, color: u32, size: f32) -> Div {
    label(text, color, size)
        .font(design::tabular_display_font())
        .whitespace_nowrap()
        .font_weight(FontWeight::MEDIUM)
}
fn metric_row(title: &str, value: String, palette: design::Palette, size: f32) -> Div {
    div()
        .w_full()
        .flex()
        .justify_between()
        .items_center()
        .gap(px(4.0))
        .child(
            label(title.to_owned(), palette.muted, 12.0)
                .whitespace_nowrap()
                .flex_shrink_0(),
        )
        .child(number(value, palette.text, size).flex_shrink_0())
}
fn panel(palette: design::Palette, radius: f32) -> Div {
    div()
        .relative()
        .rounded(px(radius))
        .border_1()
        .border_color(rgb(palette.hairline))
        .bg(rgb(palette.panel))
}

fn tint(base: u32, overlay: u32, amount: f32) -> gpui::Rgba {
    rgb(base).blend(gpui::Rgba {
        a: amount.clamp(0.0, 1.0),
        ..rgb(overlay)
    })
}
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn spring_retargeting_preserves_position_and_velocity_without_restarting_a_hold() {
        let now = Instant::now();
        let mut feedback = Feedback::new(0.0);
        feedback.target(1.0, 0.18, now);
        let halfway = now + Duration::from_millis(40);
        let state = feedback.state(halfway);
        assert!(state.0 > 0.0 && state.0 < 1.0 && state.1 > 0.0);
        feedback.target(1.0, 0.18, halfway);
        assert_eq!(feedback.start, Some(now));
        feedback.target(0.0, 0.28, halfway);
        assert_eq!(feedback.state(halfway), state);
        let later = halfway + Duration::from_millis(70);
        let reversed = feedback.state(later);
        feedback.target(1.0, 0.18, later);
        let actual = feedback.state(later);
        assert!((actual.0 - reversed.0).abs() < 1e-6);
        assert!((actual.1 - reversed.1).abs() < 1e-6);
        assert_eq!(
            feedback.sample(later + Duration::from_secs(1)),
            (1.0, false)
        );
    }

    #[test]
    fn press_settles_at_depth_and_release_recoils_before_returning_to_rest() {
        let now = Instant::now();
        let mut page = KeysPage::default();
        for (index, depth) in [(0, 4.5), (5, 6.0)] {
            assert_eq!(page.press_offset(index, true, now), (0.0, true));
            let released = now + Duration::from_secs(1);
            assert_eq!(page.press_offset(index, true, released), (depth, false));
            assert_eq!(page.press_offset(index, false, released), (depth, true));
            let mut recoiled = false;
            for millis in 1..1000 {
                let (position, _) =
                    page.press_offset(index, false, released + Duration::from_millis(millis));
                assert!(position.abs() <= depth);
                recoiled |= position < -0.1;
            }
            assert!(recoiled);
            assert_eq!(
                page.press_offset(index, false, released + Duration::from_secs(1)),
                (0.0, false)
            );
        }
    }

    #[test]
    fn default_page_animates_coalesced_taps_and_both_wheel_directions() {
        let now = Instant::now();
        let mut page = KeysPage::default();
        for index in 0..6 {
            page.input_press(index, true, false, now);
            page.input_press(index, false, true, now + Duration::from_millis(1));
            let (position, moving) =
                page.press_offset(index, false, now + Duration::from_millis(50));
            assert!(position > 1.0 && moving);
        }
        for index in 0..2 {
            page.wheel_impulse(index, now);
            assert!(page.wheel_active(index, now));
            let (position, moving) = page.wheel_feedback(index, now + Duration::from_millis(40));
            assert!(position > 0.0 && moving);
            assert_eq!(
                page.wheel_feedback(index, now + Duration::from_secs(1)),
                (0.0, false)
            );
        }
    }

    #[test]
    fn wheel_impulses_keep_direction_independence_continuity_and_bounded_energy() {
        let now = Instant::now();
        let mut page = KeysPage::default();
        for tick in 0..1000 {
            let time = now + Duration::from_millis(tick);
            let before = page.wheel_motion[0].state(time).0;
            page.wheel_impulse(0, time);
            assert_eq!(page.wheel_motion[0].state(time).0, before);
            let (position, _) = page.wheel_feedback(0, time);
            assert!(position.abs() <= 1.3501);
            assert_eq!(page.wheel_feedback(1, time), (0.0, false));
        }
        assert_eq!(
            page.wheel_feedback(0, now + Duration::from_secs(2)),
            (0.0, false)
        );
        page.wheel_impulse(1, now + Duration::from_secs(2));
        assert!(page.wheel_feedback(1, now + Duration::from_millis(2040)).0 > 0.0);
        assert_eq!(
            page.wheel_feedback(0, now + Duration::from_millis(2040)),
            (0.0, false)
        );
    }

    #[test]
    fn spring_sampling_frequency_does_not_change_the_trajectory() {
        let now = Instant::now();
        let mut frequent = Feedback::new(0.0);
        let mut sparse = Feedback::new(0.0);
        frequent.kick(now);
        sparse.kick(now);
        for millis in 0..120 {
            frequent.sample(now + Duration::from_millis(millis));
        }
        assert_eq!(
            frequent.sample(now + Duration::from_millis(120)),
            sparse.sample(now + Duration::from_millis(120))
        );
    }

    #[test]
    fn feedback_wakeup_tracks_earliest_highlight_and_reset_clears_all_motion() {
        let now = Instant::now();
        let mut page = KeysPage::default();
        page.double_until[0] = Some(now + Duration::from_millis(350));
        assert_eq!(page.feedback_deadline(), page.double_until[0]);
        page.wheel_start[1] = Some(now);
        assert_eq!(
            page.feedback_deadline(),
            Some(now + Duration::from_millis(150))
        );
        assert!(!page.wheel_active(1, now + Duration::from_millis(150)));
        assert_eq!(page.feedback_deadline(), page.double_until[0]);
        assert!(!page.double_active(0, now + Duration::from_millis(350)));
        assert_eq!(page.feedback_deadline(), None);
        page.press[5].target(1.0, 0.18, now);
        page.actions[1].target(1.0, 0.18, now);
        page.wheel_motion[0].kick(now);
        page.wake_deadline = Some(now);
        page.reset_transient();
        assert_eq!(page.press[5].sample(now), (0.0, false));
        assert_eq!(page.actions[1].sample(now), (0.0, false));
        assert_eq!(page.wheel_motion[0].sample(now), (0.0, false));
        assert!(page.wake_deadline.is_none());
        assert!(page.feedback_wake.is_none());
    }

    #[test]
    fn resetting_measurements_preserves_the_buttons_in_flight_release() {
        let now = Instant::now();
        let mut page = KeysPage::default();
        page.actions[0] = Feedback::new(1.0);
        page.actions[0].target(0.0, 0.28, now);
        let halfway = now + Duration::from_millis(80);
        let state = page.actions[0].state(halfway);
        page.reset_measurement_feedback();
        assert_eq!(page.actions[0].state(halfway), state);
        assert_eq!(
            page.actions[0].sample(now + Duration::from_secs(1)),
            (0.0, false)
        );
    }

    #[test]
    fn wheel_feedback_expires_independently_and_repeated_input_extends_only_its_direction() {
        let start = Instant::now();
        let mut page = KeysPage {
            wheel_start: [Some(start), Some(start + Duration::from_millis(50))],
            ..Default::default()
        };
        assert_eq!(
            page.wheel_deadline(),
            Some(start + Duration::from_millis(150))
        );
        assert!(page.wheel_active(0, start + Duration::from_millis(149)));
        assert!(!page.wheel_active(0, start + Duration::from_millis(150)));
        assert!(page.wheel_active(1, start + Duration::from_millis(150)));
        page.wheel_start[1] = Some(start + Duration::from_millis(180));
        assert_eq!(
            page.wheel_deadline(),
            Some(start + Duration::from_millis(330))
        );
        assert!(page.wheel_active(1, start + Duration::from_millis(200)));
        assert!(!page.wheel_active(1, start + Duration::from_millis(330)));
        assert_eq!(page.wheel_deadline(), None);
        page.wheel_start[0] = Some(start);
        page.reset_transient();
        assert_eq!(page.wheel_deadline(), None);
        assert!(page.feedback_wake.is_none());
    }
}
