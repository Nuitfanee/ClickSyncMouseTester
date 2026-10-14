use super::*;
use clicksync_chart::analysis::sensitivity::{Failure, MatchFrame, Stage};
use gpui::{Div, FontWeight, SharedString, Stateful};
use std::time::Instant;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub(super) enum Step {
    #[default]
    Setup,
    Measure,
}

pub(super) struct SensitivityPage {
    pub step: Step,
    pub target_dpi: String,
    pub editor: Option<gpui::Entity<editor::TextInput>>,
    pub binding_focus: Vec<gpui::FocusHandle>,
    binding_hover: Option<usize>,
    pub disconnected: [bool; 2],
    pub expired: bool,
    pub requested: bool,
    pub frame: Option<MatchFrame>,
    pub copied: bool,
    pub motion: StageMotion,
    pub(super) guide: Option<gpui::Entity<motion_guide::MotionGuide>>,
    #[cfg(feature = "ui-restoration-probe")]
    pub(super) capture_source: Option<clicksync_core::protocol::CaptureSource>,
    submitted: Option<(u64, u64, u64, u64, u64, u64, bool)>,
}

impl Default for SensitivityPage {
    fn default() -> Self {
        Self {
            step: Step::Setup,
            target_dpi: String::new(),
            editor: None,
            binding_focus: Vec::new(),
            binding_hover: None,
            disconnected: [false; 2],
            expired: false,
            requested: false,
            frame: None,
            copied: false,
            motion: StageMotion::default(),
            guide: None,
            #[cfg(feature = "ui-restoration-probe")]
            capture_source: None,
            submitted: None,
        }
    }
}

impl SensitivityPage {
    pub fn reset(&mut self) {
        *self = Self::default();
    }
    fn result_valid(&self, status: Option<&crate::backend::AppStatus>) -> bool {
        !self.expired
            && !self.disconnected.iter().any(|value| *value)
            && status.is_none_or(|status| !capture_incomplete(status))
    }
    fn expire_capture_result(
        &mut self,
        status: &crate::backend::AppStatus,
        minimum_generation: u64,
        pending: bool,
    ) -> bool {
        if status.generation < minimum_generation
            || pending
            || !capture_incomplete(status)
            || self.expired
            || !(self.requested
                || self
                    .frame
                    .as_ref()
                    .is_some_and(|frame| frame.active.is_some() || frame.result.is_some()))
        {
            return false;
        }
        self.expired = true;
        self.copied = false;
        true
    }
    pub fn now(&self) -> f64 {
        crate::capture_lock::now_ms()
    }
    pub fn should_submit(
        &mut self,
        prefix: &crate::backend::SessionPrefix,
        reset: u64,
        sensitivity_reset: u64,
        round: u64,
        cancelled: u64,
        locked: bool,
    ) -> bool {
        let identity = (
            prefix.generation,
            prefix.received_sequence,
            reset,
            sensitivity_reset,
            round,
            cancelled,
            locked,
        );
        let timed = self.requested
            || self
                .frame
                .as_ref()
                .is_some_and(|frame| frame.active.is_some());
        if self.submitted == Some(identity) && !timed {
            return false;
        }
        self.submitted = Some(identity);
        true
    }
}

pub(super) fn parse_dpi(value: &str) -> Option<i32> {
    value
        .trim()
        .parse::<i32>()
        .ok()
        .filter(|value| (50..=50_000).contains(value))
}

pub(super) fn can_resume_capture(status: &crate::backend::AppStatus, selected: &[u32]) -> bool {
    status.phase == crate::backend::CapturePhase::Paused
        && status.selected_device_keys == selected
        && !capture_incomplete(status)
}

fn disconnected_hint(disconnected: [bool; 2], english: bool) -> Option<&'static str> {
    let (chinese, english_text) = match disconnected {
        [false, false] => return None,
        [true, false] => (
            "被复制鼠标已断开，请重新绑定。",
            "The reference mouse disconnected. Bind it again.",
        ),
        [false, true] => (
            "目标鼠标已断开，请重新绑定并填写当前 DPI。",
            "The target mouse disconnected. Bind it again and enter its current DPI.",
        ),
        [true, true] => (
            "两只鼠标均已断开，请重新绑定并填写目标鼠标当前 DPI。",
            "Both mice disconnected. Bind them again and enter the target mouse's current DPI.",
        ),
    };
    Some(if english { english_text } else { chinese })
}

fn next_setup_focus(current: usize, backward: bool, bindings_enabled: [bool; 2]) -> usize {
    (1..=3)
        .map(|offset| (current + if backward { 3 - offset } else { offset }) % 3)
        .find(|&next| match next {
            0 => bindings_enabled[0],
            1 => bindings_enabled[1],
            _ => true,
        })
        .unwrap()
}

#[derive(Clone, Copy, Debug)]
pub(super) struct Glyph {
    ch: char,
    x: f32,
    y: f32,
    size: f32,
    alpha: f32,
}

#[derive(Default)]
pub(super) struct StageMotion {
    from: Vec<Glyph>,
    started: Option<Instant>,
    seconds: f32,
}
impl StageMotion {
    #[cfg(feature = "ui-restoration-probe")]
    pub(super) fn settled(&self) -> bool {
        self.started.is_none() && self.from.is_empty()
    }

    fn sample(&self, target: &[Glyph], now: Instant) -> Vec<Glyph> {
        let Some(start) = self.started else {
            return target.to_vec();
        };
        let elapsed = now.saturating_duration_since(start).as_secs_f32();
        let (begin, end) = (0.0, self.seconds);
        let t = ((elapsed - begin) / (end - begin)).clamp(0.0, 1.0);
        let p = t * t * (3.0 - 2.0 * t);
        let mut used = vec![false; self.from.len()];
        let mut result = Vec::with_capacity(target.len() + self.from.len());
        for (index, glyph) in target.iter().enumerate() {
            let source = self
                .from
                .iter()
                .enumerate()
                .find(|(i, old)| !used[*i] && old.ch == glyph.ch);
            let mut current = *glyph;
            if let Some((i, old)) = source {
                used[i] = true;
                current.x = old.x + (glyph.x - old.x) * p;
                current.y = old.y + (glyph.y - old.y) * p;
                current.size = old.size + (glyph.size - old.size) * p;
                current.alpha = old.alpha + (glyph.alpha - old.alpha) * p;
            } else {
                let reveal = ((elapsed - index as f32 * 0.003) / self.seconds).clamp(0.0, 1.0);
                current.alpha *= reveal;
                current.y += (1.0 - reveal) * 24.0;
            }
            result.push(current);
        }
        for (index, old) in self.from.iter().enumerate() {
            if !used[index] {
                let mut glyph = *old;
                glyph.alpha *= 1.0 - p;
                glyph.y -= p * 18.0;
                result.push(glyph);
            }
        }
        result
    }
    fn transition(&mut self, current: Vec<Glyph>, seconds: f32, now: Instant) {
        self.from = current;
        self.started = Some(now);
        self.seconds = seconds;
    }
    fn reveal(&self, begin: f32, end: f32, now: Instant) -> f32 {
        self.started.map_or(1.0, |start| {
            let t = ((now.saturating_duration_since(start).as_secs_f32() - begin) / (end - begin))
                .clamp(0.0, 1.0);
            1.0 - (1.0 - t).powi(3)
        })
    }
    fn advance(&mut self, now: Instant) -> bool {
        if self
            .started
            .is_some_and(|start| now.saturating_duration_since(start).as_secs_f32() >= self.seconds)
        {
            self.started = None;
            self.from.clear();
        }
        self.started.is_some()
    }
}

fn word(output: &mut Vec<Glyph>, text: &str, x: f32, y: f32, size: f32, alpha: f32) {
    let mut x = x;
    for ch in text.chars() {
        output.push(Glyph {
            ch,
            x,
            y,
            size,
            alpha,
        });
        // Match the bundled display face's advance rather than changing glyph
        // tracking as the stage moves.
        x += size
            * match ch {
                'I' => 0.26,
                'M' => 0.82,
                'T' => 0.59,
                _ => 0.64,
            };
    }
}
fn word_width(text: &str, size: f32) -> f32 {
    let mut glyphs = Vec::new();
    word(&mut glyphs, text, 0.0, 0.0, size, 1.0);
    glyphs.last().map_or(0.0, |g| g.x + size * 0.64)
}
fn setup_word_size(width: f32, height: f32) -> f32 {
    // Reserve 300 DIP for the form, 24/32 DIP clearances and a 12 DIP top inset.
    // Both display words use a 1.12 line height within the remaining space.
    (width / 7.1)
        .min((height - 368.0) / 2.24)
        .clamp(84.0, 176.0)
}
fn glyphs(step: Step, width: f32, height: f32) -> Vec<Glyph> {
    let mut output = Vec::new();
    let size = setup_word_size(width, height);
    match step {
        Step::Setup => {
            word(&mut output, "SOURCE", 0.0, 12.0, size, 1.0);
            word(
                &mut output,
                "TARGET",
                width - word_width("TARGET", size),
                height - size * 1.12,
                size,
                1.0,
            );
        }
        Step::Measure => {
            word(&mut output, "SOURCE", 0.0, 8.0, size * 0.9, 0.04);
            word(
                &mut output,
                "TARGET",
                width - word_width("TARGET", size * 0.9),
                height - size * 0.9 * 1.12,
                size * 0.9,
                0.04,
            );
        }
    }
    output
}

impl GpuiHost {
    fn sensitivity_ready(&self) -> bool {
        let [Some(source), Some(target)] = self.business_input.bindings else {
            return false;
        };
        let runtime = self.runtime.borrow();
        runtime.device_catalog.accepts_selection(&[source, target])
            && !self.sensitivity.disconnected.iter().any(|value| *value)
            && self.business_input.binding_slot.is_none()
            && parse_dpi(&self.sensitivity.target_dpi).is_some()
    }
    pub(super) fn sensitivity_can_start(&self) -> bool {
        self.page == Page::Sensitivity
            && self.sensitivity.step == Step::Measure
            && self.was_active
            && self.sensitivity_ready()
            && !self.sensitivity.expired
            && !self.sensitivity.requested
            && self.runtime.borrow().pending_action().is_none()
            && self
                .sensitivity
                .frame
                .as_ref()
                .is_none_or(|f| f.active.is_none() && f.result.is_none())
    }
    pub(super) fn sensitivity_stage(&mut self, step: Step, window: &mut Window) {
        if self.page != Page::Sensitivity
            || step == self.sensitivity.step
            || (step == Step::Measure && !self.sensitivity_ready())
        {
            return;
        }
        let (width, height) = self.sensitivity_size(window);
        let now = Instant::now();
        let current = self
            .sensitivity
            .motion
            .sample(&glyphs(self.sensitivity.step, width, height), now);
        if step != Step::Measure {
            self.cancel_sensitivity_round();
            self.reset_measurements();
        }
        self.sensitivity.step = step;
        self.sensitivity.guide = None;
        self.focus.focus(window);
        self.sensitivity.motion.transition(current, 0.3, now);
    }
    pub(super) fn sensitivity_bind(&mut self, slot: usize) {
        if self.sensitivity.step != Step::Setup
            || slot > 1
            || self.runtime.borrow().pending_action().is_some()
        {
            return;
        }
        if self.business_input.bindings[slot].is_some()
            && self.business_input.binding_slot != Some(slot)
        {
            self.runtime.borrow_mut().cancel_motion();
            self.business_input.bindings[slot] = None;
            self.business_input.binding_slot = None;
            self.business_input.issue = None;
            self.selected = self
                .business_input
                .bindings
                .iter()
                .flatten()
                .copied()
                .collect();
            self.reset_measurements();
        } else {
            self.reset_measurements();
            self.begin_device_binding(slot);
        }
        self.sensitivity.disconnected[slot] = false;
        self.sensitivity.expired = false;
    }
    pub(super) fn cancel_sensitivity_round(&mut self) {
        self.sensitivity.requested = false;
        self.round_cancelled += 1;
        if let Some(frame) = &mut self.sensitivity.frame {
            frame.active = None;
        }
        if self
            .runtime
            .borrow()
            .status()
            .is_some_and(|s| s.phase == crate::backend::CapturePhase::Collecting)
            || self
                .runtime
                .borrow()
                .pending_action()
                .is_some_and(|a| a == "start" || a == "resume")
        {
            self.capture_command("pause");
        }
    }
    pub(super) fn sensitivity_status(
        &mut self,
        status: &crate::backend::AppStatus,
        cx: &mut Context<Self>,
    ) -> bool {
        if status.generation < self.page_generation {
            return false;
        }
        let mut disconnected = false;
        for side in 0..2 {
            if let Some(key) = self.business_input.bindings[side]
                && !status.devices.iter().any(|device| device.key == key)
            {
                self.business_input.bindings[side] = None;
                self.sensitivity.disconnected[side] = true;
                if side == 1 {
                    self.sensitivity.target_dpi.clear();
                    if let Some(editor) = &self.sensitivity.editor {
                        editor.update(cx, |field, cx| field.set_value("", cx));
                    }
                }
                disconnected = true;
            }
        }
        if disconnected {
            self.runtime.borrow_mut().cancel_motion();
            if let Some(backend) = &self.runtime.borrow().backend {
                backend.set_capture_permitted(false);
            }
            self.business_input.binding_slot = None;
            self.business_input.first = None;
            self.business_input.issue = None;
            self.selected = self
                .business_input
                .bindings
                .iter()
                .flatten()
                .copied()
                .collect();
            self.selection_initialized = true;
            self.cancel_sensitivity_round();
            self.reset_measurements();
            self.sensitivity.expired = true;
            return true;
        }
        if self.sensitivity.expire_capture_result(
            status,
            self.page_generation,
            self.runtime.borrow().pending_action().is_some(),
        ) {
            self.cancel_sensitivity_round();
            return true;
        }
        false
    }
    pub(super) fn sensitivity_frame(&mut self, frame: &MatchFrame) {
        let was_active = self.sensitivity.requested;
        self.sensitivity.frame = Some(frame.clone());
        if frame.active.is_some() {
            self.sensitivity.requested = true;
        } else if was_active && (frame.result.is_some() || frame.failure != Failure::None) {
            self.sensitivity.requested = false;
            self.capture_command("pause");
        }
    }
    fn initialize_sensitivity_editor(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.sensitivity.editor.is_some() {
            return;
        }
        let field = cx.new(|cx| {
            let mut field = editor::TextInput::new(cx, &self.sensitivity.target_dpi);
            field.kind = editor::InputKind::Integer;
            field.set_placeholder("DPI");
            field
        });
        cx.observe(&field, |host: &mut Self, field, cx| {
            if host.page != Page::Sensitivity {
                return;
            }
            let text = field.read(cx).content.to_string();
            if host.sensitivity.target_dpi != text {
                host.sensitivity.target_dpi = text;
                host.reset_measurements();
                cx.notify();
            }
        })
        .detach();
        cx.subscribe_in(&field, window, |host: &mut Self, _, event, window, cx| {
            match event {
                editor::EditEvent::Traverse(backward) => {
                    host.focus_sensitivity_setup(2, *backward, window, cx);
                }
                editor::EditEvent::Submit | editor::EditEvent::Cancel => host.focus.focus(window),
            }
            cx.stop_propagation();
            cx.notify();
        })
        .detach();
        self.sensitivity.editor = Some(field);
        self.sensitivity.binding_focus = (0..2).map(|_| cx.focus_handle()).collect();
    }
    fn focus_sensitivity_setup(
        &self,
        current: usize,
        backward: bool,
        window: &mut Window,
        cx: &Context<Self>,
    ) {
        let available = self.runtime.borrow().pending_action().is_none();
        let next = next_setup_focus(
            current,
            backward,
            std::array::from_fn(|side| available && self.business_input.binding_slot != Some(side)),
        );
        if next == 2 {
            self.sensitivity
                .editor
                .as_ref()
                .unwrap()
                .read(cx)
                .focus_handle
                .focus(window);
        } else {
            self.sensitivity.binding_focus[next].focus(window);
        }
    }
    fn sensitivity_binding_action(
        &self,
        side: usize,
        pending: bool,
        bound: bool,
        palette: design::Palette,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        let enabled = !pending && self.runtime.borrow().pending_action().is_none();
        let focused = enabled && self.sensitivity.binding_focus[side].is_focused(window);
        let color = if enabled && (self.sensitivity.binding_hover == Some(side) || focused) {
            palette.accent
        } else if pending || bound {
            palette.muted
        } else {
            palette.text
        };
        let group: SharedString = format!("sensitivity-bind-{side}").into();
        let text = if pending {
            self.text("等待运动…", "Waiting…")
        } else if bound {
            self.text("解除绑定", "Unbind")
        } else if self.sensitivity.disconnected[side] {
            self.text("重新绑定", "Rebind")
        } else {
            self.text("绑定", "Bind")
        };
        div()
            .id(group.clone())
            .group(group.clone())
            .relative()
            .h(px(48.0))
            .flex_shrink_0()
            .flex()
            .items_center()
            .gap(px(8.0))
            .text_size(px(16.0))
            .line_height(px(24.0))
            .font_weight(FontWeight::MEDIUM)
            .text_color(rgb(color))
            .when(enabled, |button| {
                button
                    .track_focus(&self.sensitivity.binding_focus[side])
                    .cursor_pointer()
                    .active(|style| style.opacity(0.72))
            })
            .child(
                div()
                    .w(px(16.0))
                    .flex_shrink_0()
                    .text_center()
                    .text_color(rgb(palette.accent))
                    .text_size(px(24.0))
                    .font_family(design::DISPLAY_FONT)
                    .child(if pending {
                        "·"
                    } else if bound {
                        "−"
                    } else {
                        "+"
                    }),
            )
            .child(
                div()
                    .text_color(rgb(color))
                    .border_b_1()
                    .border_color(if focused {
                        rgb(palette.accent).into()
                    } else {
                        gpui::transparent_black()
                    })
                    .when(enabled, |label| {
                        label.group_hover(group, move |style| {
                            style
                                .border_color(rgb(palette.accent))
                                .text_color(rgb(palette.accent))
                        })
                    })
                    .child(text),
            )
            .on_hover(cx.listener(move |host, hovered: &bool, _, cx| {
                if *hovered {
                    host.sensitivity.binding_hover = Some(side);
                } else if host.sensitivity.binding_hover == Some(side) {
                    host.sensitivity.binding_hover = None;
                }
                cx.notify();
            }))
            .on_click(cx.listener(move |host, _, window, cx| {
                if enabled {
                    host.focus.focus(window);
                    host.sensitivity_bind(side);
                    cx.notify();
                }
            }))
            .on_key_down(
                cx.listener(move |host, event: &gpui::KeyDownEvent, window, cx| {
                    if !enabled {
                        return;
                    }
                    match event.keystroke.key.as_str() {
                        "enter" | "space" => {
                            if !event.is_held {
                                host.focus.focus(window);
                                host.sensitivity_bind(side);
                            }
                        }
                        "tab" => {
                            host.focus_sensitivity_setup(
                                side,
                                event.keystroke.modifiers.shift,
                                window,
                                cx,
                            );
                        }
                        "escape" => host.focus.focus(window),
                        _ => return,
                    }
                    cx.stop_propagation();
                    cx.notify();
                }),
            )
    }
    pub(super) fn copy_sensitivity_result(&mut self, cx: &mut Context<Self>) {
        if !self
            .sensitivity
            .result_valid(self.runtime.borrow().status().as_ref())
            || !self.sensitivity_ready()
        {
            return;
        }
        if let Some(value) = self
            .sensitivity
            .frame
            .as_ref()
            .and_then(|frame| frame.result.as_ref().map(|r| r.recommended_target_dpi))
        {
            cx.write_to_clipboard(gpui::ClipboardItem::new_string(value.to_string()));
            self.sensitivity.copied = true;
        }
    }
    fn sensitivity_size(&self, window: &Window) -> (f32, f32) {
        (
            f32::from(window.viewport_size().width) - 136.0,
            f32::from(window.viewport_size().height) - shell::CAPTION_HEIGHT - 124.0,
        )
    }
    pub(super) fn render_sensitivity(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        self.initialize_sensitivity_editor(window, cx);
        let palette = design::Palette::for_theme(self.settings.dark);
        let step = self.sensitivity.step;
        self.sensitivity
            .editor
            .as_ref()
            .unwrap()
            .update(cx, |field, _| {
                field.palette = palette;
                field.enabled = step == Step::Setup;
            });
        let now = Instant::now();
        let (width, height) = self.sensitivity_size(window);
        let moving = self.sensitivity.motion.advance(now);
        if moving {
            window.request_animation_frame();
        }
        let stage = self.sensitivity.step;
        let sampled = self
            .sensitivity
            .motion
            .sample(&glyphs(stage, width, height), now);
        let mut hero = div().relative().flex_1().min_h_0().overflow_hidden();
        for (index, glyph) in sampled
            .into_iter()
            .enumerate()
            .filter(|_| stage == Step::Setup)
        {
            hero = hero.child(
                div()
                    .absolute()
                    .left(px(glyph.x))
                    .top(px(glyph.y))
                    .font_family(design::DISPLAY_FONT)
                    .font_weight(FontWeight::BOLD)
                    .text_size(px(glyph.size))
                    .line_height(px(glyph.size * 1.12))
                    .opacity(glyph.alpha)
                    .child(glyph.ch.to_string())
                    .when(index == 0 || index == 6, |word| word),
            );
        }
        let mut footer = div()
            .h(px(64.0))
            .flex_shrink_0()
            .border_t_1()
            .border_color(rgb(palette.hairline))
            .pt(px(16.0))
            .flex()
            .items_center()
            .justify_between();
        match stage {
            Step::Setup => {
                let reveal = self.sensitivity.motion.reveal(0.0, 0.25, now);
                let mut form = div().w(px(480.0)).flex().flex_col().gap(px(20.0));
                for side in 0..2 {
                    let key = self.business_input.bindings[side];
                    let title = key
                        .and_then(|key| {
                            self.runtime
                                .borrow()
                                .device_catalog
                                .label(key)
                                .map(str::to_owned)
                        })
                        .unwrap_or_else(|| self.text("未绑定", "Not bound").to_owned());
                    let pending = self.business_input.binding_slot == Some(side);
                    form = form.child(
                        div()
                            .w_full()
                            .flex()
                            .flex_col()
                            .gap(px(4.0))
                            .child(
                                div()
                                    .flex()
                                    .items_center()
                                    .gap(px(12.0))
                                    .child(label(
                                        if side == 0 {
                                            self.text("被复制鼠标", "Reference mouse")
                                        } else {
                                            self.text("目标鼠标", "Target mouse")
                                        },
                                        palette.muted,
                                        14.0,
                                    ))
                                    .when(self.sensitivity.disconnected[side], |row| {
                                        row.child(label(
                                            self.text("设备已断开", "Disconnected"),
                                            palette.accent,
                                            13.0,
                                        ))
                                    }),
                            )
                            .child(
                                div()
                                    .w_full()
                                    .flex()
                                    .items_center()
                                    .gap(px(24.0))
                                    .child(
                                        label(title, palette.text, 22.0)
                                            .flex_1()
                                            .min_w_0()
                                            .font_weight(FontWeight::SEMIBOLD)
                                            .line_height(px(30.0)),
                                    )
                                    .child(self.sensitivity_binding_action(
                                        side,
                                        pending,
                                        key.is_some(),
                                        palette,
                                        window,
                                        cx,
                                    )),
                            )
                            .child(
                                label(
                                    if pending {
                                        if side == 0 {
                                            self.text(
                                                "轻移被复制鼠标以完成绑定",
                                                "Move the reference mouse to bind",
                                            )
                                        } else {
                                            self.text(
                                                "轻移目标鼠标以完成绑定",
                                                "Move the target mouse to bind",
                                            )
                                        }
                                    } else {
                                        ""
                                    },
                                    palette.muted,
                                    13.0,
                                )
                                .h(px(20.0))
                                .line_height(px(20.0)),
                            ),
                    );
                }
                form = form.child(
                    div()
                        .w_full()
                        .flex()
                        .flex_col()
                        .gap(px(8.0))
                        .child(label(
                            self.text("目标鼠标当前 DPI", "Target mouse current DPI"),
                            palette.muted,
                            14.0,
                        ))
                        .child(self.sensitivity.editor.as_ref().unwrap().clone())
                        .child(label(
                            self.text("50–50000 · 整数", "50–50000 · Whole number"),
                            palette.muted,
                            13.0,
                        )),
                );
                hero = hero.child(
                    div()
                        .absolute()
                        .top_0()
                        .bottom_0()
                        .left_0()
                        .right_0()
                        .flex()
                        .items_center()
                        .justify_center()
                        .opacity(reveal)
                        .child(form),
                );
                let message = if let Some(hint) =
                    disconnected_hint(self.sensitivity.disconnected, self.settings.english)
                {
                    hint
                } else if self.business_input.issue
                    == Some(clicksync_capture::observation::MotionResult::TimedOut)
                {
                    self.text("绑定超时，请重试。", "Binding timed out. Try again.")
                } else if parse_dpi(&self.sensitivity.target_dpi).is_none() {
                    self.text(
                        "绑定两只鼠标，并填写目标鼠标当前 DPI。",
                        "Bind both mice and enter the target mouse's current DPI.",
                    )
                } else {
                    self.text(
                        "改 DPI 或重绑会清空已测结果。",
                        "Changing DPI or bindings clears measured results.",
                    )
                };
                let ready = self.sensitivity_ready();
                footer = footer.child(label(message, palette.muted, 14.0)).child(
                    design::button(
                        "sensitivity-next",
                        self.text("下一步", "NEXT"),
                        palette,
                        self.settings.english,
                        true,
                        ready,
                    )
                    .when(!ready, |button| {
                        button
                            .opacity(1.0)
                            .bg(rgb(palette.hover))
                            .border_color(rgb(palette.hairline))
                            .text_color(rgb(palette.muted))
                    })
                    .h(px(44.0))
                    .w(px(168.0))
                    .text_center()
                    .on_click(cx.listener(|host, _, window, cx| {
                        host.sensitivity_stage(Step::Measure, window);
                        cx.notify();
                    })),
                );
            }
            Step::Measure => {
                // Keep secondary text above 4.5:1 on the light measurement canvas.
                let palette = design::Palette {
                    muted: if self.settings.dark {
                        palette.muted
                    } else {
                        0x68707c
                    },
                    ..palette
                };
                let frame = self.sensitivity.frame.as_ref();
                let status = self
                    .runtime
                    .borrow()
                    .status()
                    .filter(|status| status.generation >= self.page_generation);
                let valid = self.sensitivity.result_valid(status.as_ref());
                let disconnected = self.sensitivity.disconnected.iter().any(|value| *value);
                let recommended = frame
                    .filter(|_| valid)
                    .and_then(|f| f.result.as_ref().map(|r| r.recommended_target_dpi));
                let active = frame.filter(|_| valid).and_then(|f| f.active.as_ref());
                let needs_retry =
                    !valid || frame.is_some_and(|frame| frame.failure != Failure::None);
                let number = recommended.map(|dpi| dpi.to_string()).unwrap_or_else(|| {
                    if needs_retry {
                        return "—".into();
                    }
                    active.map_or_else(
                        || "10".into(),
                        |f| match f.stage {
                            Stage::Preparing => self.text("准备", "Ready").into(),
                            Stage::Finishing => self.text("分析", "Checking").into(),
                            Stage::Collecting => format!("{:.0}%", f.progress * 100.0),
                        },
                    )
                });
                let hint = if let Some(hint) =
                    disconnected_hint(self.sensitivity.disconnected, self.settings.english)
                {
                    hint
                } else if let Some(hint) =
                    capture_failure_hint(status.as_ref(), self.settings.english)
                {
                    hint
                } else if !valid {
                    self.text(
                        "结果已过期，重新绑定后再使用。",
                        "Result expired. Bind the devices again.",
                    )
                } else if let Some(frame) = frame.filter(|f| f.failure != Failure::None) {
                    failure_text(frame.failure, self.settings.english)
                } else if recommended.is_some() {
                    self.text(
                        "将目标鼠标设为此 DPI，匹配相同平移下的灵敏度。",
                        "Set the target to this DPI to match translation sensitivity.",
                    )
                } else if active.is_some_and(|f| f.stage == Stage::Preparing) {
                    self.text(
                        "先贴稳两只鼠标，提示变化后开始移动。",
                        "Hold both mice together. Wait for the move prompt.",
                    )
                } else if active.is_some_and(|f| f.stage == Stage::Finishing) {
                    self.text("移动已完成。", "Movement complete.")
                } else {
                    self.text(
                        "并排贴稳，保持朝向，一起向右移动。",
                        "Hold both mice together. Keep their heading and move right.",
                    )
                };
                let content_top = 12.0;
                let content_bottom = height - 12.0;
                let readout_size = measurement_readout_size(
                    &number,
                    needs_retry || active.is_some_and(|f| f.stage != Stage::Collecting),
                );
                let guide = self
                    .sensitivity
                    .guide
                    .get_or_insert_with(|| {
                        cx.new(|_| motion_guide::MotionGuide::new(true, palette))
                    })
                    .clone();
                guide.update(cx, |guide, cx| {
                    let changed = guide.set_demonstrating(
                        recommended.is_none()
                            && valid
                            && frame.is_none_or(|frame| frame.failure == Failure::None)
                            && active.is_none_or(|active| active.stage == Stage::Collecting),
                    );
                    if guide.palette.background != palette.background
                        || guide.palette.muted != palette.muted
                        || changed
                    {
                        guide.palette = palette;
                        cx.notify();
                    }
                });
                let summary = div()
                    .flex_1()
                    .min_w_0()
                    .flex()
                    .flex_col()
                    .items_center()
                    .text_center()
                    .child(
                        label(
                            if recommended.is_some() {
                                let target_name = self.business_input.bindings[1]
                                    .and_then(|key| {
                                        self.runtime
                                            .borrow()
                                            .device_catalog
                                            .label(key)
                                            .map(str::to_owned)
                                    })
                                    .unwrap_or_else(|| {
                                        self.text("目标鼠标", "target mouse").into()
                                    });
                                format!("{} {target_name} DPI", self.text("推荐", "Recommended"))
                            } else if needs_retry {
                                self.text("测量状态", "Measurement status").to_owned()
                            } else if active.is_some() {
                                self.text("测量进度", "Measurement progress").to_owned()
                            } else {
                                self.text("移动距离", "Swipe distance").to_owned()
                            },
                            palette.muted,
                            14.0,
                        )
                        .line_height(px(20.0)),
                    )
                    .child(
                        div()
                            .relative()
                            .mt(px(6.0))
                            .flex()
                            .items_end()
                            .gap(px(8.0))
                            .child(
                                label(
                                    number,
                                    if needs_retry {
                                        palette.muted
                                    } else {
                                        palette.text
                                    },
                                    readout_size,
                                )
                                .font_family(design::DISPLAY_FONT)
                                .font_weight(FontWeight::BOLD)
                                .line_height(px(readout_size * 1.12)),
                            )
                            .when(
                                recommended.is_none() && active.is_none() && !needs_retry,
                                |d| {
                                    d.child(
                                        label("cm", palette.muted, 24.0)
                                            .font_family(design::DISPLAY_FONT)
                                            .pb(px(12.0)),
                                    )
                                },
                            ),
                    )
                    .child(
                        label(
                            frame
                                .and_then(|f| f.result.as_ref())
                                .filter(|_| recommended.is_some())
                                .map_or_else(String::new, |frame| {
                                    format!(
                                        "{} {:.4} ×  ·  {:+.1}%",
                                        self.text("调整倍率", "Scale"),
                                        frame.scale,
                                        (frame.scale - 1.0) * 100.0
                                    )
                                }),
                            palette.muted,
                            14.0,
                        )
                        .relative()
                        .mt(px(8.0))
                        .h(px(20.0))
                        .line_height(px(20.0)),
                    );
                let mut content = div()
                    .absolute()
                    .top(px(content_top))
                    .h(px((content_bottom - content_top).max(1.0)))
                    .left_0()
                    .right_0()
                    .flex()
                    .flex_col()
                    .justify_center()
                    .items_center()
                    .child(
                        label(
                            if disconnected {
                                self.text("设备已断开", "Device disconnected")
                            } else if recommended.is_some() {
                                self.text("测量完成", "Measurement complete")
                            } else if needs_retry {
                                self.text("需要重新测量", "Measure again")
                            } else {
                                self.text(
                                    "按下\"S\"或点击开始，两只鼠标一起移动。",
                                    "Press \"S\" or click Start, then move both mice together.",
                                )
                            },
                            palette.text,
                            26.0,
                        )
                        .font_weight(FontWeight::SEMIBOLD)
                        .text_center()
                        .line_height(px(36.0)),
                    )
                    .child(
                        label(hint, palette.muted, 14.0)
                            .mt(px(10.0))
                            .max_w(px(820.0))
                            .text_center()
                            .line_height(px(22.0)),
                    )
                    .child(
                        div()
                            .w_full()
                            .max_w(px(640.0))
                            .mt(px(28.0))
                            .flex()
                            .items_center()
                            .gap(px(32.0))
                            .child(
                                div()
                                    .flex_1()
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .child(guide),
                            )
                            .child(summary),
                    );
                let mut progress = div()
                    .relative()
                    .mt(px(32.0))
                    .w_full()
                    .max_w(px(820.0))
                    .flex()
                    .gap(px(48.0));
                for side in 0..2 {
                    let connected = !self.sensitivity.disconnected[side]
                        && self.business_input.bindings[side].is_some();
                    let state = if self.sensitivity.disconnected[side] {
                        self.text("已断开", "Disconnected")
                    } else if !connected {
                        self.text("等待绑定", "Not connected")
                    } else if let Some(active) = active {
                        if active.packet_counts[side] > 0 {
                            self.text("已收到运动", "Motion received")
                        } else {
                            self.text("等待运动", "Waiting for motion")
                        }
                    } else {
                        self.text("就绪", "Ready")
                    };
                    progress = progress.child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .flex()
                            .flex_col()
                            .gap(px(8.0))
                            .child(
                                div()
                                    .flex()
                                    .items_center()
                                    .justify_between()
                                    .gap(px(12.0))
                                    .child(label(
                                        if side == 0 {
                                            self.text("被复制鼠标", "Reference mouse")
                                        } else {
                                            self.text("目标鼠标", "Target mouse")
                                        },
                                        palette.muted,
                                        14.0,
                                    ))
                                    .when(recommended.is_none(), |d| {
                                        d.child(
                                            label(
                                                state,
                                                if self.sensitivity.disconnected[side] {
                                                    palette.accent
                                                } else {
                                                    palette.muted
                                                },
                                                13.0,
                                            )
                                            .flex_shrink_0(),
                                        )
                                    }),
                            )
                            .child(
                                label(
                                    self.business_input.bindings[side]
                                        .and_then(|key| {
                                            self.runtime
                                                .borrow()
                                                .device_catalog
                                                .label(key)
                                                .map(str::to_owned)
                                        })
                                        .unwrap_or_else(|| self.text("未绑定", "Not bound").into()),
                                    palette.text,
                                    17.0,
                                )
                                .font_weight(FontWeight::MEDIUM)
                                .line_height(px(24.0)),
                            ),
                    );
                }
                content = content.child(progress);
                let fraction = active.map_or(if recommended.is_some() { 1.0 } else { 0.0 }, |f| {
                    f.progress
                });
                content = content
                    .child(
                        div()
                            .mt(px(18.0))
                            .h(px(4.0))
                            .rounded_full()
                            .overflow_hidden()
                            .w_full()
                            .max_w(px(820.0))
                            .bg(rgb(palette.hairline))
                            .child(
                                div()
                                    .h_full()
                                    .w(gpui::relative(fraction as f32))
                                    .bg(rgb(palette.accent)),
                            ),
                    )
                    .child(
                        label(
                            if recommended.is_some()
                                || active.is_some_and(|active| active.stage == Stage::Finishing)
                            {
                                self.text("距离按目标 DPI 估算", "Distance estimated from target DPI")
                            } else {
                                self.text(
                                    "距离按目标 DPI 估算 · 避免相互滑动或转动",
                                    "Distance estimated from target DPI · Avoid slipping or twisting",
                                )
                            },
                            palette.muted,
                            13.0,
                        )
                        .mt(px(12.0))
                        .text_center()
                        .line_height(px(20.0)),
                    );
                let can_start = self.sensitivity_can_start();
                let can_copy = recommended.is_some();
                let can_retry = self.was_active
                    && self.sensitivity_ready()
                    && !self.sensitivity.requested
                    && self.runtime.borrow().pending_action().is_none()
                    && (recommended.is_some() || needs_retry);
                footer = footer
                    .w_full()
                    .max_w(px(820.0))
                    .h_auto()
                    .border_t_0()
                    .pt_0()
                    .mt(px(32.0))
                    .child(
                        design::button(
                            "sensitivity-back-setup",
                            self.text("← 设备与 DPI", "← Devices & DPI"),
                            palette,
                            self.settings.english,
                            false,
                            true,
                        )
                        .min_h(px(44.0))
                        .border_0()
                        .text_color(rgb(palette.muted))
                        .on_click(cx.listener(|host, _, window, cx| {
                            host.sensitivity_stage(Step::Setup, window);
                            cx.notify();
                        })),
                    )
                    .child(
                        div()
                            .flex()
                            .gap(px(12.0))
                            .child(
                                design::button(
                                    "sensitivity-rebind",
                                    self.text("重新绑定", "Rebind"),
                                    palette,
                                    self.settings.english,
                                    disconnected,
                                    true,
                                )
                                .min_h(px(44.0))
                                .when(!disconnected, |button| {
                                    button.border_0().text_color(rgb(palette.muted))
                                })
                                .on_click(cx.listener(
                                    |host, _, window, cx| {
                                        host.sensitivity_stage(Step::Setup, window);
                                        if !host.sensitivity.disconnected.iter().any(|value| *value)
                                        {
                                            host.business_input =
                                                business_input::BusinessInput::default();
                                            host.selected.clear();
                                        }
                                        cx.notify();
                                    },
                                )),
                            )
                            .when(can_retry, |d| {
                                d.child(
                                    design::button(
                                        "sensitivity-remeasure",
                                        self.text("重新测量", "Measure again"),
                                        palette,
                                        self.settings.english,
                                        recommended.is_none(),
                                        can_retry,
                                    )
                                    .min_h(px(44.0))
                                    .on_click(cx.listener(
                                        |host, _, _, cx| {
                                            if host.was_active
                                                && host.sensitivity_ready()
                                                && !host.sensitivity.requested
                                                && host.runtime.borrow().pending_action().is_none()
                                            {
                                                host.reset_measurements();
                                                host.start_round();
                                            }
                                            cx.notify();
                                        },
                                    )),
                                )
                            })
                            .when(can_copy, |d| {
                                d.child(
                                    design::button(
                                        "sensitivity-copy",
                                        if self.sensitivity.copied {
                                            self.text("已复制", "Copied")
                                        } else {
                                            self.text("复制 DPI", "Copy DPI")
                                        },
                                        palette,
                                        self.settings.english,
                                        can_copy,
                                        can_copy,
                                    )
                                    .min_h(px(44.0))
                                    .min_w(px(152.0))
                                    .on_click(cx.listener(
                                        |host, _, _, cx| {
                                            host.copy_sensitivity_result(cx);
                                            cx.notify();
                                        },
                                    )),
                                )
                            })
                            .when(!can_copy && (!can_retry || active.is_some()), |d| {
                                d.child(
                                    design::button(
                                        "sensitivity-start-measurement",
                                        if active.is_some() {
                                            self.text("取消测量", "Cancel")
                                        } else if recommended.is_some() {
                                            self.text("测量完成", "Complete")
                                        } else {
                                            self.text("开始测量 · S", "Start · S")
                                        },
                                        palette,
                                        self.settings.english,
                                        recommended.is_none(),
                                        can_start || active.is_some(),
                                    )
                                    .min_h(px(44.0))
                                    .min_w(px(152.0))
                                    .on_click(cx.listener(
                                        |host, _, _, cx| {
                                            if host
                                                .sensitivity
                                                .frame
                                                .as_ref()
                                                .is_some_and(|frame| frame.active.is_some())
                                            {
                                                host.cancel_sensitivity_round();
                                            } else {
                                                host.start_round();
                                            }
                                            cx.notify();
                                        },
                                    )),
                                )
                            }),
                    );
                hero = hero.child(content.child(footer));
                footer = div();
            }
        }
        div()
            .id("sensitivity-workspace")
            .size_full()
            .overflow_hidden()
            .flex()
            .flex_col()
            .px(px(68.0))
            .pt(px(12.0))
            .pb(px(18.0))
            .bg(rgb(palette.background))
            .text_color(rgb(palette.text))
            .font_family(design::Typography::body_font(self.settings.english))
            .when(!self.custom_chrome, |d| {
                d.track_focus(&self.focus)
                    .on_key_down(cx.listener(|host, e, window, cx| host.keyboard(e, window, cx)))
            })
            .child(hero)
            .when_some(
                displayed_error(&self.control_error, &self.backend_error),
                |d, error| {
                    d.child(label(
                        binding_error_text(&error, self.settings.english).to_owned(),
                        palette.accent,
                        12.0,
                    ))
                },
            )
            .child(footer)
    }
}

fn label(text: impl Into<SharedString>, color: u32, size: f32) -> Div {
    div()
        .text_size(px(size))
        .line_height(px(size * 1.25))
        .text_color(rgb(color))
        .child(text.into())
}

fn measurement_readout_size(number: &str, compact: bool) -> f32 {
    if compact {
        return 36.0;
    }
    // Reserve 280 DIP of the 304 DIP column, including the widest supported i32 result.
    (280.0 / (number.len() as f32 * 0.65)).clamp(36.0, 72.0)
}

fn capture_failure_hint(
    status: Option<&crate::backend::AppStatus>,
    english: bool,
) -> Option<&'static str> {
    status.filter(|status| capture_incomplete(status)).map(|_| {
        if english {
            "Capture failed or data is incomplete. Reset and measure again."
        } else {
            "采集失败或数据不完整，请重置后重新测量。"
        }
    })
}
pub(super) fn binding_error_text(error: &str, english: bool) -> &str {
    if !english && error == "no eligible motion device" {
        "没有可绑定的鼠标设备。"
    } else {
        error
    }
}

fn failure_text(failure: Failure, english: bool) -> &'static str {
    let pair = match failure {
        Failure::Timeout => ("测量超时，请重试。", "Measurement timed out. Try again."),
        Failure::TooFast => (
            "移动过快，请放慢一点。",
            "Movement was too fast. Slow down.",
        ),
        Failure::InsufficientPackets => (
            "有效数据太少。贴稳后以适中速度重试。",
            "Too little usable data. Hold both mice together and retry at a moderate speed.",
        ),
        Failure::DirectionMismatch => (
            "两只鼠标移动角度差距较大。",
            "The mice moved at different angles.",
        ),
        Failure::PathShapeMismatch => (
            "两只鼠标的轨迹形态不一致。",
            "The mice followed different path shapes.",
        ),
        Failure::UnstableScale => (
            "前后段比例不一致。贴稳两只鼠标后重试。",
            "The scale changed during the swipe. Hold both mice together and retry.",
        ),
        Failure::InvalidInput => (
            "输入时间或计数异常，请重新测量。",
            "Invalid input. Please measure again.",
        ),
        Failure::None | Failure::Unsynchronized => {
            ("两只鼠标不同步。", "The mice were not synchronized.")
        }
    };
    if english { pair.1 } else { pair.0 }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resume_requires_the_same_devices_and_a_complete_paused_capture() {
        use crate::backend::{AppStatus, CapturePhase};
        let healthy = AppStatus {
            phase: CapturePhase::Paused,
            complete: true,
            selected_device_keys: vec![1, 2],
            ..Default::default()
        };
        assert!(can_resume_capture(&healthy, &[1, 2]));
        for selected in [&[3, 2][..], &[1, 3][..], &[2, 1][..]] {
            assert!(!can_resume_capture(&healthy, selected));
        }
        for status in [
            AppStatus {
                phase: CapturePhase::Faulted,
                ..healthy.clone()
            },
            AppStatus {
                phase: CapturePhase::Collecting,
                ..healthy.clone()
            },
            AppStatus {
                complete: false,
                ..healthy.clone()
            },
            AppStatus {
                overflow: 1,
                ..healthy.clone()
            },
        ] {
            assert!(!can_resume_capture(&status, &[1, 2]));
        }
    }

    #[test]
    fn disconnected_hint_identifies_the_role_and_target_dpi_requirement() {
        assert!(disconnected_hint([false; 2], false).is_none());
        assert!(disconnected_hint([false; 2], true).is_none());
        for (english, source, target) in [
            (false, "被复制鼠标", "目标鼠标"),
            (true, "reference mouse", "target mouse"),
        ] {
            assert!(
                disconnected_hint([true, false], english)
                    .unwrap()
                    .contains(source)
            );
            let target_hint = disconnected_hint([false, true], english).unwrap();
            assert!(target_hint.contains(target) && target_hint.contains("DPI"));
            assert!(
                disconnected_hint([true; 2], english)
                    .unwrap()
                    .contains("DPI")
            );
        }
    }

    #[test]
    fn setup_focus_cycles_between_two_bindings_and_one_target_dpi() {
        for (current, forward, backward) in [(0, 1, 2), (1, 2, 0), (2, 0, 1)] {
            assert_eq!(next_setup_focus(current, false, [true; 2]), forward);
            assert_eq!(next_setup_focus(current, true, [true; 2]), backward);
        }
        assert_eq!(next_setup_focus(2, true, [false, true]), 1);
        assert_eq!(next_setup_focus(2, false, [true, false]), 0);
        assert_eq!(next_setup_focus(2, false, [false; 2]), 2);
        assert_eq!(next_setup_focus(2, true, [false; 2]), 2);
        let page = SensitivityPage::default();
        assert!(page.target_dpi.is_empty());
        assert!(page.editor.is_none());
    }

    #[test]
    fn stage_transition_animates_from_current_glyphs_and_settles_after_reversal() {
        let now = Instant::now();
        let origin = Glyph {
            ch: 'S',
            x: 0.0,
            y: 0.0,
            size: 48.0,
            alpha: 1.0,
        };
        let target = Glyph { x: 100.0, ..origin };
        let mut motion = StageMotion::default();
        motion.transition(vec![origin], 0.3, now);
        assert!(motion.advance(now));
        assert_eq!(motion.sample(&[target], now)[0].x, 0.0);
        let halfway = now + Duration::from_millis(150);
        let current = motion.sample(&[target], halfway);
        assert!((current[0].x - 50.0).abs() < 0.001);
        let position = current[0].x;
        motion.transition(current, 0.3, halfway);
        assert_eq!(motion.sample(&[origin], halfway)[0].x, position);
        let settled = now + Duration::from_millis(500);
        assert!(!motion.advance(settled));
        assert!(motion.from.is_empty());
        assert_eq!(motion.sample(&[origin], settled)[0].x, 0.0);
    }

    #[test]
    fn measurement_readout_keeps_normal_numbers_large_and_fits_integer_results() {
        for number in ["10", "47%", "100%", "50000"] {
            assert_eq!(measurement_readout_size(number, false), 72.0);
        }
        let largest = i32::MAX.to_string();
        let size = measurement_readout_size(&largest, false);
        assert!((36.0..48.0).contains(&size));
        assert!(size * largest.len() as f32 * 0.65 <= 280.0);
        assert_eq!(measurement_readout_size("Checking", true), 36.0);
        assert_eq!(measurement_readout_size("—", true), 36.0);
    }

    #[test]
    fn faulted_or_incomplete_capture_invalidates_recommendation_without_changing_data() {
        use crate::backend::{AppStatus, CapturePhase};
        let mut frame =
            clicksync_chart::analysis::sensitivity::SensitivityEngine::default().frame();
        frame.result = Some(clicksync_chart::analysis::sensitivity::MatchResult {
            scale: 1.0,
            recommended_target_dpi: 800,
            duration_ms: 1000.0,
            packet_counts: [1000; 2],
            direction_delta_degrees: 0.0,
            overlap_ratio: 1.0,
            residual: 0.0,
            segment_spread_percent: 0.0,
            lag_ms: 0.0,
        });
        let mut page = SensitivityPage {
            frame: Some(frame),
            copied: true,
            ..SensitivityPage::default()
        };
        let healthy = AppStatus {
            complete: true,
            ..AppStatus::default()
        };
        assert!(page.result_valid(Some(&healthy)));
        assert!(page.result_valid(Some(&AppStatus {
            error: Some("device selection was rejected".into()),
            ..healthy.clone()
        })));
        for status in [
            AppStatus {
                phase: CapturePhase::Faulted,
                ..healthy.clone()
            },
            AppStatus {
                error: Some("retention loss".into()),
                ignored_after_limit: 1,
                ..healthy.clone()
            },
            AppStatus {
                complete: false,
                ..healthy.clone()
            },
            AppStatus {
                overflow: 1,
                ..healthy.clone()
            },
        ] {
            assert!(!page.result_valid(Some(&status)));
        }
        let fault = AppStatus {
            phase: CapturePhase::Faulted,
            ..healthy.clone()
        };
        assert!(page.expire_capture_result(&fault, 0, false));
        assert!(!page.copied);
        assert!(!page.result_valid(Some(&healthy)));
        assert!(!page.expire_capture_result(&fault, 0, false));
        assert_eq!(
            page.frame
                .as_ref()
                .unwrap()
                .result
                .as_ref()
                .map(|r| r.recommended_target_dpi),
            Some(800)
        );
        page.frame = None;
        page.expired = false;
        assert!(
            !page.expire_capture_result(&fault, 0, false),
            "reset must allow a new capture attempt"
        );
    }
    #[test]
    fn old_fault_after_start_ack_does_not_expire_the_new_round() {
        use crate::backend::{AppStatus, CapturePhase};
        let old = AppStatus {
            generation: 10,
            phase: CapturePhase::Faulted,
            ..Default::default()
        };
        let mut page = SensitivityPage {
            requested: true,
            ..Default::default()
        };
        assert!(!page.expire_capture_result(&old, 10, true));
        assert!(!page.expire_capture_result(&old, 11, false));
        let healthy = AppStatus {
            generation: 11,
            phase: CapturePhase::Collecting,
            complete: true,
            ..Default::default()
        };
        assert!(!page.expire_capture_result(&healthy, 11, false));
        assert!(page.requested && !page.expired);
        let fault = AppStatus {
            complete: false,
            ..healthy
        };
        assert!(page.expire_capture_result(&fault, 11, false));
        assert!(!page.expire_capture_result(&fault, 11, false));
    }
    #[test]
    fn healthy_control_rejection_keeps_result_valid_and_has_no_incomplete_hint() {
        let status = crate::backend::AppStatus {
            complete: true,
            error: Some("device selection was rejected".into()),
            ..Default::default()
        };
        let mut page = SensitivityPage {
            requested: true,
            ..Default::default()
        };
        assert!(page.result_valid(Some(&status)));
        assert!(!page.expire_capture_result(&status, 0, false));
        assert!(capture_failure_hint(Some(&status), false).is_none());
        assert!(capture_failure_hint(Some(&status), true).is_none());
        let fault = crate::backend::AppStatus {
            ignored_after_limit: 1,
            ..status
        };
        assert!(capture_failure_hint(Some(&fault), false).is_some());
        assert!(capture_failure_hint(Some(&fault), true).is_some());
    }
    #[test]
    fn dpi_matches_reference_bounds_and_invalid_input_stays_invalid() {
        for (input, expected) in [
            ("", None),
            ("49", None),
            ("50", Some(50)),
            (" 800 ", Some(800)),
            ("50000", Some(50000)),
            ("50001", None),
            ("800.0", None),
            ("2147483648", None),
        ] {
            assert_eq!(parse_dpi(input), expected);
        }
    }
}
