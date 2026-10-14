use super::*;

const GLYPHS: [&str; 11] = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "-"];

struct Digits {
    text: String,
    slots: usize,
}

impl Default for Digits {
    fn default() -> Self {
        Self {
            text: "--".into(),
            slots: 4,
        }
    }
}

impl Digits {
    fn numeric(text: &str) -> bool {
        !text.is_empty() && text.bytes().all(|byte| byte.is_ascii_digit())
    }

    fn slots_for(&self, text: &str) -> usize {
        if Self::numeric(text) {
            self.slots.max(text.len())
        } else {
            4
        }
    }

    fn update(&mut self, text: &str) -> bool {
        if self.text == text {
            return false;
        }
        self.slots = self.slots_for(text);
        self.text.clear();
        self.text.push_str(text);
        true
    }
}

struct GlyphCache {
    font_size: f32,
    slot_width: f32,
    lines: [gpui::ShapedLine; 11],
    ink: [[f32; 2]; 11],
}

impl GlyphCache {
    fn new(font_size: f32, window: &Window) -> Self {
        let lines = GLYPHS.map(|text| {
            window.text_system().shape_line(
                text.into(),
                px(font_size),
                &[gpui::TextRun {
                    len: text.len(),
                    font: gpui::font(design::DISPLAY_FONT),
                    color: rgb(0xffffff).into(),
                    background_color: None,
                    underline: None,
                    strikethrough: None,
                }],
                None,
            )
        });
        let slot_width = lines[..10]
            .iter()
            .map(|line| f32::from(line.width))
            .fold(0.0f32, f32::max);
        let ink = std::array::from_fn(|index| {
            let line = &lines[index];
            let inset = (slot_width - f32::from(line.width)) / 2.0;
            let bounds = window
                .text_system()
                .typographic_bounds(
                    line.runs[0].font_id,
                    px(font_size),
                    GLYPHS[index].chars().next().unwrap(),
                )
                .expect("bundled polling glyph has typographic bounds");
            let left = inset + f32::from(bounds.origin.x);
            [left, left + f32::from(bounds.size.width)]
        });
        Self {
            font_size,
            slot_width,
            lines,
            ink,
        }
    }

    fn ink_span(&self, text: &str) -> (f32, f32) {
        text.bytes().enumerate().fold(
            (f32::INFINITY, f32::NEG_INFINITY),
            |(left, right), (index, byte)| {
                let ink = self.ink[if byte.is_ascii_digit() {
                    (byte - b'0') as usize
                } else {
                    10
                }];
                let offset = index as f32 * self.slot_width;
                (left.min(offset + ink[0]), right.max(offset + ink[1]))
            },
        )
    }
}

pub(in crate::ui) struct Readout {
    state: Rc<RefCell<State>>,
}

struct State {
    digits: Digits,
    glyphs: GlyphCache,
    ink: u32,
    revision: u64,
    #[cfg(feature = "ui-restoration-probe")]
    paints: u64,
    #[cfg(feature = "ui-restoration-probe")]
    glyph_builds: u64,
    #[cfg(feature = "ui-restoration-probe")]
    samples: Vec<(Instant, f32, f64)>,
}

impl Readout {
    pub fn new(font_size: f32, ink: u32, window: &Window) -> Self {
        Self {
            state: Rc::new(RefCell::new(State {
                digits: Digits::default(),
                glyphs: GlyphCache::new(font_size, window),
                ink,
                revision: 0,
                #[cfg(feature = "ui-restoration-probe")]
                paints: 0,
                #[cfg(feature = "ui-restoration-probe")]
                glyph_builds: 1,
                #[cfg(feature = "ui-restoration-probe")]
                samples: Vec::new(),
            })),
        }
    }

    pub fn slots_for(&self, text: &str) -> usize {
        self.state.borrow().digits.slots_for(text)
    }

    pub fn size(&self) -> gpui::Size<gpui::Pixels> {
        let state = self.state.borrow();
        size(
            px(state.glyphs.slot_width * state.digits.slots as f32),
            px(state.glyphs.font_size),
        )
    }

    pub fn visible_width(&self) -> gpui::Pixels {
        let state = self.state.borrow();
        let (left, right) = state.glyphs.ink_span(&state.digits.text);
        px(right - left)
    }

    pub fn update(&mut self, text: &str, font_size: f32, ink: u32, window: &Window) -> u64 {
        let mut state = self.state.borrow_mut();
        let resized = state.glyphs.font_size != font_size;
        let changed = state.digits.update(text);
        if resized {
            state.glyphs = GlyphCache::new(font_size, window);
            #[cfg(feature = "ui-restoration-probe")]
            {
                state.glyph_builds += 1;
            }
        }
        let repaint = changed || resized || state.ink != ink;
        state.ink = ink;
        if repaint {
            state.revision = state.revision.wrapping_add(1);
        }
        state.revision
    }

    #[cfg(feature = "ui-restoration-probe")]
    pub fn stats(&self) -> serde_json::Value {
        let state = self.state.borrow();
        let origin = state.samples.first().map(|sample| sample.0);
        serde_json::json!({
            "text": state.digits.text,
            "slots": state.digits.slots,
            "paints": state.paints,
            "glyph_builds": state.glyph_builds,
            "revision": state.revision,
            "samples": state.samples.iter().map(|(time, alpha, cpu_ms)| {
                serde_json::json!({
                    "elapsed_ms": origin.map_or(0.0, |origin| time.duration_since(origin).as_secs_f64() * 1000.0),
                    "glyph_alpha": alpha,
                    "paint_cpu_ms": cpu_ms,
                })
            }).collect::<Vec<_>>(),
        })
    }
}

impl Render for Readout {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        let state = self.state.clone();
        canvas(
            |_, _, _| {},
            move |bounds, _, window, _| {
                #[cfg(feature = "ui-restoration-probe")]
                let started = Instant::now();
                let mut state = state.borrow_mut();
                let glyphs = &state.glyphs;
                // Center the visible ink, including the side bearings of 1 and -.
                // Reserved slots keep the canvas stable as the digit count changes.
                let (ink_left, ink_right) = glyphs.ink_span(&state.digits.text);
                let left = (f32::from(bounds.size.width) - ink_right - ink_left) / 2.0;
                let color: gpui::Hsla = rgb(state.ink).into();
                window.paint_layer(bounds, |window| {
                    for (index, byte) in state.digits.text.bytes().enumerate() {
                        let line = &glyphs.lines[if byte.is_ascii_digit() {
                            (byte - b'0') as usize
                        } else {
                            10
                        }];
                        let origin = bounds.origin
                            + gpui::point(
                                px(left + index as f32 * glyphs.slot_width)
                                    + (px(glyphs.slot_width) - line.width) / 2.0,
                                (bounds.size.height - line.ascent - line.descent) / 2.0
                                    + line.ascent,
                            );
                        for run in &line.runs {
                            for glyph in &run.glyphs {
                                if let Err(error) = window.paint_glyph(
                                    origin + glyph.position,
                                    run.font_id,
                                    glyph.id,
                                    px(glyphs.font_size),
                                    color,
                                ) {
                                    eprintln!("polling digit paint failed: {error}");
                                }
                            }
                        }
                    }
                });
                #[cfg(feature = "ui-restoration-probe")]
                {
                    state.paints += 1;
                    if state.samples.len() < 2048 {
                        state.samples.push((
                            started,
                            color.a,
                            started.elapsed().as_secs_f64() * 1000.0,
                        ));
                    }
                }
                #[cfg(not(feature = "ui-restoration-probe"))]
                let _ = &mut state;
            },
        )
        .size_full()
    }
}

pub(super) fn placeholder(
    bounds_report: Arc<Mutex<[f32; 4]>>,
    dimensions: gpui::Size<gpui::Pixels>,
) -> gpui::Div {
    div()
        .w(dimensions.width)
        .h(dimensions.height)
        .flex_shrink_0()
        .child(
            canvas(
                move |bounds, _, _| {
                    *bounds_report.lock().unwrap() = [
                        f32::from(bounds.origin.x),
                        f32::from(bounds.origin.y),
                        f32::from(bounds.size.width),
                        f32::from(bounds.size.height),
                    ];
                },
                |_, _, _, _| {},
            )
            .size_full(),
        )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn digits_publish_latest_value_reuse_width_and_reset_placeholder() {
        let mut digits = Digits::default();
        for value in ["2067", "8000", "0", "999", "1000", "2168", "2167"] {
            assert!(digits.update(value));
            assert_eq!(digits.text, value);
            assert_eq!(digits.slots, 4);
            assert!(!digits.update(value));
        }
        assert!(digits.update("20000"));
        assert!(digits.update("8000"));
        assert_eq!(digits.slots, 5);
        assert_eq!(digits.slots_for("--"), 4);
        assert!(digits.update("--"));
        assert_eq!(digits.text, "--");
        assert_eq!(digits.slots, 4);
    }
}
