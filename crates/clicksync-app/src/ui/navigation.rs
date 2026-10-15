//! Original curtain, item choreography, device brands and spring interaction.
use super::*;
use gpui::{Div, Stateful};
use std::{collections::BTreeMap, sync::OnceLock, time::Instant};

type TextKey = (&'static str, &'static str, u32, u32, u32);
type TextCache = Rc<RefCell<BTreeMap<TextKey, gpui::ShapedLine>>>;

pub(super) const CURTAIN_SECONDS: f64 = 0.8;
pub(super) const ITEM_SECONDS: f64 = 0.8;
pub(super) const BRAND_READY_SECONDS: f64 = 1.35;
pub(super) const MENU_READY_SECONDS: f64 = 1.5;
pub(super) const MENU_TITLE_SIZE: f32 = 68.0;
pub(super) const MENU_ITEM_HEIGHT: f32 = 144.0;
pub(super) const MENU_TITLE_SCALE: f32 = MENU_TITLE_SIZE / 80.0;
const HEADER_HEIGHT: f32 = 112.0;
const HEADER_TEXT_SIZE: f32 = 16.0;
const META_TEXT_SIZE: f32 = 14.0;
const META_HEIGHT: f32 = 20.0;
const TITLE_HEIGHT: f32 = 82.0;
pub(super) const MENU_TEXT_INSET: f32 = 36.0;
pub(super) const META_TITLE_GAP: f32 = 8.0;
const BRAND_MAX_HEIGHT: f64 = 600.0;

fn entry_progress(elapsed: f64, index: usize) -> f32 {
    bezier(
        ((elapsed - stagger(index, 5, 0.4, 0.3)) / ITEM_SECONDS).clamp(0.0, 1.0),
        [0.1, 0.8, 0.2, 1.0],
    ) as f32
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
pub(super) enum Phase {
    Hidden,
    Opening,
    Interactive,
    Closing,
}

#[derive(Clone, Debug, Serialize)]
pub(super) struct Spring {
    pub y: f64,
    pub scale: f64,
    velocity_y: f64,
    velocity_scale: f64,
    target_y: f64,
    target_scale: f64,
}
impl Spring {
    fn intro(height: f64) -> Self {
        let y = -(height * 1.2).max(32.0);
        Self {
            y,
            scale: 0.96,
            velocity_y: 0.0,
            velocity_scale: 0.0,
            target_y: y,
            target_scale: 0.96,
        }
    }
    fn step(&mut self, dt: f64) {
        fn advance(value: &mut f64, velocity: &mut f64, target: f64, dt: f64) {
            if dt <= 0.0 {
                return;
            }
            // Exact damped oscillator for mass 1, stiffness 100, damping 10.
            // Frame partitions do not change its trajectory or inject energy.
            let displacement = *value - target;
            let frequency = 75.0_f64.sqrt();
            let (sin, cos) = (frequency * dt).sin_cos();
            let decay = (-5.0 * dt).exp();
            let next =
                decay * (displacement * cos + (*velocity + 5.0 * displacement) / frequency * sin);
            *velocity = decay
                * (*velocity * cos - (5.0 * *velocity + 100.0 * displacement) / frequency * sin);
            *value = target + next;
            let envelope = next.hypot((*velocity + 5.0 * next) / frequency);
            if envelope * 10.0 < 0.002 {
                *value = target;
                *velocity = 0.0;
            }
        }
        advance(&mut self.y, &mut self.velocity_y, self.target_y, dt);
        advance(
            &mut self.scale,
            &mut self.velocity_scale,
            self.target_scale,
            dt,
        );
    }
    fn moving(&self) -> bool {
        (self.y - self.target_y).abs() >= 0.002
            || self.velocity_y.abs() >= 0.002
            || (self.scale - self.target_scale).abs() >= 0.002
            || self.velocity_scale.abs() >= 0.002
    }
}

#[derive(serde::Deserialize)]
pub(super) struct BrandElement {
    pub path: String,
    pub bounds: [f64; 4],
    pub raster_bounds: [f64; 4],
}
pub(super) fn assets() -> &'static BTreeMap<String, Vec<BrandElement>> {
    static ASSETS: OnceLock<BTreeMap<String, Vec<BrandElement>>> = OnceLock::new();
    ASSETS.get_or_init(|| {
        serde_json::from_str(include_str!("../../../../assets/brand/elements.json"))
            .expect("original brand assets")
    })
}
#[derive(serde::Deserialize)]
pub(super) struct Title {
    path: String,
    pub bounds: [f64; 4],
}
pub(super) fn titles() -> &'static Vec<Title> {
    static TITLES: OnceLock<Vec<Title>> = OnceLock::new();
    TITLES.get_or_init(|| {
        serde_json::from_str(include_str!("../../../../assets/brand/menu-titles.json"))
            .expect("original English menu title outlines")
    })
}

#[derive(Clone, Copy, Serialize)]
pub(super) struct Geometry {
    pub width: f32,
    pub height: f32,
    pub guide_x: f32,
    pub guide_y: f32,
    pub header_x: f32,
    pub menu_x: f32,
    pub menu_y: f32,
    pub menu_height: f32,
}
impl Geometry {
    pub fn item_height(self) -> f32 {
        self.menu_height / Page::ALL.len() as f32
    }
    pub fn brand_size(self) -> (f32, f32) {
        (self.guide_x, self.menu_height)
    }
}

pub(super) fn geometry(width: f32, height: f32) -> Geometry {
    let guide_x = (width * 0.28).clamp(320.0, 448.0).min(width);
    let inset = (width * 0.12).clamp(72.0, 192.0);
    let guide_y = HEADER_HEIGHT.min(height);
    let menu_y = (guide_y + 40.0).min(height);
    let menu_height =
        (Page::ALL.len() as f32 * MENU_ITEM_HEIGHT).min((height - menu_y - 68.0).max(1.0));
    let menu_x = (guide_x + inset).min(width);
    Geometry {
        width,
        height,
        guide_x,
        guide_y,
        header_x: menu_x,
        menu_x,
        menu_y,
        menu_height,
    }
}

#[derive(Clone)]
struct Underline {
    value: f32,
    from: f32,
    target: f32,
    started: Instant,
}
impl Underline {
    fn new(selected: bool, now: Instant) -> Self {
        let value = if selected { 1.0 } else { 0.0 };
        Self {
            value,
            from: value,
            target: value,
            started: now,
        }
    }
    fn update(&mut self, selected: bool, now: Instant, duration: f64, symmetric: bool) -> bool {
        let target = if selected { 1.0 } else { 0.0 };
        if target != self.target {
            self.from = self.value;
            self.target = target;
            self.started = now;
        }
        let t = if duration > 0.0 {
            (now.saturating_duration_since(self.started).as_secs_f64() / duration).min(1.0)
        } else {
            1.0
        };
        let ease = if !symmetric && self.target > self.from {
            1.0 - (1.0 - t).powi(3)
        } else if t < 0.5 {
            2.0 * t * t
        } else {
            1.0 - 2.0 * (1.0 - t).powi(2)
        };
        self.value = self.from + (self.target - self.from) * ease as f32;
        t < 1.0 && self.value != self.target
    }
}

#[derive(Clone)]
pub(super) struct Motion {
    pub phase: Phase,
    pub brand: String,
    pub springs: Vec<Spring>,
    started: Instant,
    intro_started: Instant,
    last: Instant,
    from: f64,
    pub coverage: f64,
    startup: bool,
    pub hovered: Option<usize>,
    pub closing_page: Option<Page>,
    pub pressed: Option<Page>,
    pointer: Option<(f32, f32)>,
    pub revision: u64,
    pub intro_elapsed: f64,
    transition_elapsed: f64,
    header_hover: Option<usize>,
    underlines: [Underline; 4],
    hover_surfaces: [Underline; 5],
    text_cache: TextCache,
    handoff_pause: Option<Instant>,
}
impl Motion {
    pub fn new(startup: bool) -> Self {
        let now = Instant::now();
        Self {
            phase: if startup {
                Phase::Opening
            } else {
                Phase::Hidden
            },
            brand: "nuit".into(),
            springs: Vec::new(),
            started: now,
            intro_started: now,
            last: now,
            from: 0.0,
            coverage: 0.0,
            startup,
            hovered: None,
            closing_page: None,
            pressed: None,
            pointer: None,
            revision: 0,
            intro_elapsed: 0.0,
            transition_elapsed: 0.0,
            header_hover: None,
            underlines: std::array::from_fn(|i| Underline::new(i == 0 || i == 3, now)),
            hover_surfaces: std::array::from_fn(|_| Underline::new(false, now)),
            text_cache: Rc::new(RefCell::new(BTreeMap::new())),
            handoff_pause: None,
        }
    }
    pub fn visible(&self) -> bool {
        self.phase != Phase::Hidden
    }
    pub fn interactive(&self) -> bool {
        self.phase == Phase::Interactive
    }
    pub fn accepts_input(&self) -> bool {
        matches!(self.phase, Phase::Opening | Phase::Interactive)
    }
    fn entry_highlighted(&self, index: usize) -> bool {
        self.hovered == Some(index)
            || self.pressed == Some(Page::ALL[index])
            || self.closing_page == Some(Page::ALL[index])
    }
    pub fn show(&mut self, brand: String) {
        self.show_at(brand, Instant::now());
    }
    fn show_at(&mut self, brand: String, now: Instant) {
        self.handoff_pause = None;
        self.advance_curtain(now);
        self.brand = brand;
        self.from = self.coverage;
        self.phase = Phase::Opening;
        self.started = now;
        self.intro_started = now;
        self.last = now;
        self.springs.clear();
        self.pointer = None;
        self.hovered = None;
        self.closing_page = None;
        self.pressed = None;
        self.hover_surfaces = std::array::from_fn(|_| Underline::new(false, now));
        self.header_hover = None;
        self.revision += 1;
    }
    pub fn close(&mut self, page: Option<Page>) -> bool {
        if !self.accepts_input() {
            return false;
        }
        self.begin_close_at(page, Instant::now());
        true
    }
    fn begin_close_at(&mut self, page: Option<Page>, now: Instant) {
        self.from = self.coverage;
        self.started = now;
        self.phase = Phase::Closing;
        self.closing_page = page;
        self.pointer = None;
        for spring in &mut self.springs {
            spring.target_y = 0.0;
            spring.target_scale = 1.0;
        }
        self.revision += 1;
    }
    fn advance_curtain(&mut self, now: Instant) {
        self.transition_elapsed = now.saturating_duration_since(self.started).as_secs_f64();
        if self.phase != Phase::Closing {
            self.intro_elapsed = now
                .saturating_duration_since(self.intro_started)
                .as_secs_f64();
        }
        let t = (self.transition_elapsed / CURTAIN_SECONDS).min(1.0);
        match self.phase {
            Phase::Opening => {
                self.coverage = self.from + (1.0 - self.from) * bezier(t, [0.78, 0.0, 0.13, 1.0]);
                let ready = if self.startup {
                    CURTAIN_SECONDS
                } else {
                    MENU_READY_SECONDS
                };
                if self.transition_elapsed >= ready {
                    self.phase = Phase::Interactive;
                    self.coverage = 1.0;
                }
            }
            Phase::Closing => {
                self.coverage = self.from * (1.0 - bezier(t, [0.78, 0.0, 0.13, 1.0]));
                if self.transition_elapsed >= CURTAIN_SECONDS {
                    self.phase = Phase::Hidden;
                    self.coverage = 0.0;
                    self.hovered = None;
                    self.springs.clear();
                }
            }
            _ => {}
        }
    }
    pub fn update(&mut self, now: Instant, geometry: Geometry, settings: &Settings) -> bool {
        self.update_with_motion(now, geometry, settings, motion_preference::reduced())
    }

    fn update_with_motion(
        &mut self,
        now: Instant,
        geometry: Geometry,
        settings: &Settings,
        reduced: bool,
    ) -> bool {
        if self.handoff_pause.is_some() {
            return false;
        }
        if reduced {
            if self.phase == Phase::Closing {
                self.phase = Phase::Hidden;
                self.coverage = 0.0;
                self.hovered = None;
                self.springs.clear();
            } else if self.visible() {
                self.phase = Phase::Interactive;
                self.coverage = 1.0;
                self.intro_elapsed = MENU_READY_SECONDS;
                let count = assets().get(&self.brand).map_or(4, Vec::len);
                self.springs = (0..count)
                    .map(|_| Spring {
                        y: 0.0,
                        scale: 1.0,
                        velocity_y: 0.0,
                        velocity_scale: 0.0,
                        target_y: 0.0,
                        target_scale: 1.0,
                    })
                    .collect();
            }
            self.last = now;
            self.underlines = [
                !settings.english,
                settings.english,
                !settings.dark,
                settings.dark,
            ]
            .map(|selected| Underline::new(selected, now));
            self.hover_surfaces = std::array::from_fn(|i| {
                Underline::new(
                    self.hovered == Some(i) || self.closing_page == Some(Page::ALL[i]),
                    now,
                )
            });
            return false;
        }
        self.advance_curtain(now);
        let count = assets().get(&self.brand).map_or(4, Vec::len);
        let (width, height) = geometry.brand_size();
        if self.visible() && self.phase != Phase::Closing && height > 0.0 {
            let layout = brand_layout(width, height, count);
            if self.springs.len() != count {
                self.springs = (0..count).map(|_| Spring::intro(layout.2)).collect();
            }
            let dt = now.saturating_duration_since(self.last).as_secs_f64();
            let pointer = self.pointer.filter(|(x, y)| {
                *x as f64 >= width as f64 * 0.05 - 10.0
                    && (*x as f64) <= width as f64 * 0.95 + 10.0
                    && *y as f64 >= layout.1 - 10.0
                    && (*y as f64)
                        <= layout.1 + count as f64 * layout.2 + (count - 1) as f64 * layout.3 + 10.0
            });
            for (i, spring) in self.springs.iter_mut().enumerate() {
                let release = stagger(i, count, 0.42, 0.30);
                if self.intro_elapsed >= release {
                    spring.target_y = 0.0;
                    spring.target_scale = 1.0;
                    if self.intro_elapsed >= BRAND_READY_SECONDS
                        && let Some((_, y)) = pointer
                    {
                        let center = layout.1 + i as f64 * (layout.2 + layout.3) + layout.2 / 2.0;
                        spring.target_y = if y as f64 > center {
                            -(layout.2 * 0.28).max(6.0)
                        } else {
                            (layout.2 * 0.28).max(6.0)
                        };
                    }
                }
                spring.step(dt.min((self.intro_elapsed - release).max(0.0)));
            }
        }
        let selected = [
            !settings.english,
            settings.english,
            !settings.dark,
            settings.dark,
        ];
        let mut links_moving = false;
        for (i, underline) in self.underlines.iter_mut().enumerate() {
            links_moving |=
                underline.update(selected[i] || self.header_hover == Some(i), now, 0.3, false);
        }
        for (i, surface) in self.hover_surfaces.iter_mut().enumerate() {
            let keep =
                self.closing_page == Some(Page::ALL[i]) || self.pressed == Some(Page::ALL[i]);
            if keep {
                surface.value = 1.0;
                surface.target = 1.0;
            }
            links_moving |= surface.update(self.hovered == Some(i) || keep, now, 0.2, true);
        }
        self.last = now;
        self.visible()
            && (matches!(self.phase, Phase::Opening | Phase::Closing)
                || self.intro_elapsed < MENU_READY_SECONDS
                || links_moving
                || self.springs.iter().any(Spring::moving))
    }
    pub fn pointer(&mut self, position: Option<(f32, f32)>) {
        self.pointer = position;
    }
    pub fn header_hover(&mut self, index: usize, hovered: bool) {
        if hovered {
            self.header_hover = Some(index);
        } else if self.header_hover == Some(index) {
            self.header_hover = None;
        }
    }
    pub fn entry_hover(&mut self, index: usize, hovered: bool) {
        if hovered {
            self.hovered = Some(index);
        } else if self.hovered == Some(index) {
            self.hovered = None;
        }
    }
    pub fn transfer_to_main(&self) -> Self {
        let mut motion = self.clone();
        motion.startup = false;
        motion
    }

    pub fn hold_for_handoff(&mut self) {
        self.handoff_pause = Some(self.started);
    }

    pub fn resume_handoff(&mut self, now: Instant) {
        if let Some(paused) = self.handoff_pause.take() {
            let elapsed = now.saturating_duration_since(paused);
            self.started += elapsed;
            self.intro_started += elapsed;
            self.last = now;
        }
    }
}

pub(super) fn stagger(index: usize, count: usize, base: f64, range: f64) -> f64 {
    if count <= 1 {
        return base;
    }
    let reverse = (count - 1 - index.min(count - 1)) as f64 / (count - 1) as f64;
    base + range * (1.0 - (1.0 - reverse).powf(1.5))
}
pub(super) fn bezier(x: f64, c: [f64; 4]) -> f64 {
    fn component(t: f64, a: f64, b: f64) -> f64 {
        3.0 * (1.0 - t).powi(2) * t * a + 3.0 * (1.0 - t) * t * t * b + t.powi(3)
    }
    let (mut low, mut high) = (0.0, 1.0);
    for _ in 0..36 {
        let mid = (low + high) / 2.0;
        if component(mid, c[0], c[2]) < x {
            low = mid;
        } else {
            high = mid;
        }
    }
    component((low + high) / 2.0, c[1], c[3])
}
pub(super) fn brand_layout(width: f32, height: f32, count: usize) -> (f64, f64, f64, f64) {
    let (w, h, n) = (width as f64, height as f64, count.max(1) as f64);
    // Keep the finite fallback for NaN before applying the upper bound.
    let available = (h - 120.0).max(1.0).clamp(1.0, BRAND_MAX_HEIGHT);
    let y = (h - available).max(0.0) / 2.0;
    let gap = if count > 1 {
        (available / (n + (n - 1.0) * 12.0 / 140.0) * 12.0 / 140.0)
            .max(2.0)
            .min(((available - n) / (n - 1.0)).max(0.0))
    } else {
        0.0
    };
    (
        w * 0.9,
        y,
        ((available - gap * (n - 1.0)) / n).max(0.01),
        gap,
    )
}
fn brand_placement(
    width: f32,
    layout: (f64, f64, f64, f64),
    element: &BrandElement,
    index: usize,
    spring: Option<&Spring>,
) -> (Bounds<gpui::Pixels>, gpui::Transformation) {
    // Keep the raster cache key stable; apply the spring on the GPU sprite.
    let fit = (layout.0 / element.bounds[2]).min(layout.2 / element.bounds[3]) as f32;
    let layout_w = element.bounds[2] as f32 * fit;
    let layout_h = element.bounds[3] as f32 * fit;
    let layout_x = (width - layout_w) / 2.0;
    let slot_y = (layout.1 + index as f64 * (layout.2 + layout.3)) as f32;
    let layout_y = slot_y + (layout.2 as f32 - layout_h) / 2.0;
    let bounds = Bounds {
        origin: gpui::point(
            px(layout_x + (element.raster_bounds[0] - element.bounds[0]) as f32 * fit),
            px(layout_y + (element.raster_bounds[1] - element.bounds[1]) as f32 * fit),
        ),
        size: gpui::size(
            px(element.raster_bounds[2] as f32 * fit),
            px(element.raster_bounds[3] as f32 * fit),
        ),
    };
    let scale = spring.map_or(0.96, |spring| spring.scale) as f32;
    let offset = spring.map_or(0.0, |spring| spring.y) as f32;
    let slot_center = gpui::point(px(width / 2.0), px(slot_y + layout.2 as f32 / 2.0));
    (
        bounds,
        gpui::Transformation::scale(gpui::size(scale, scale)).with_translation(
            (slot_center - bounds.center()) * (1.0 - scale) + gpui::point(px(0.0), px(offset)),
        ),
    )
}
pub(super) fn brand_key(name: &str, vendor: Option<u16>) -> String {
    const PROFILES: [(&str, &[&str], &[u16]); 11] = [
        ("maicong", &["maicong", "mai cong", "mchose", "迈从"], &[]),
        ("logi", &["logitech", "logi"], &[0x046d]),
        ("razer", &["razer"], &[0x1532]),
        ("crdrako", &["crdrako"], &[]),
        ("rapoo", &["rapoo", "雷柏"], &[0x24ae]),
        ("lamzu", &["lamzu"], &[0x37b0]),
        ("zowie", &["zowie"], &[0x2345]),
        ("vaxee", &["vaxee"], &[]),
        ("atk", &["atk"], &[0x373b]),
        ("rog", &["asus", "rog"], &[]),
        ("scyrox", &["scyrox"], &[0x395a, 0x3554]),
    ];
    let text = name
        .to_lowercase()
        .chars()
        .map(|character| {
            if character.is_whitespace() || matches!(character, '_' | '-') {
                ' '
            } else {
                character
            }
        })
        .collect::<String>()
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ");
    let matches = |token: &str| {
        if token.contains(' ') || !token.is_ascii() {
            return text.contains(token);
        }
        text.match_indices(token).any(|(start, _)| {
            text[..start]
                .chars()
                .next_back()
                .is_none_or(|c| !c.is_alphanumeric())
                && text[start + token.len()..]
                    .chars()
                    .next()
                    .is_none_or(|c| !c.is_alphanumeric())
        })
    };
    PROFILES
        .iter()
        .find(|(_, tokens, _)| tokens.iter().any(|token| matches(token)))
        .map(|p| p.0)
        .or_else(|| {
            PROFILES
                .iter()
                .find(|(_, _, ids)| vendor.is_some_and(|id| ids.contains(&id)))
                .map(|p| p.0)
        })
        .unwrap_or("nuit")
        .into()
}
pub(super) fn preferred_brand(runtime: &runtime::AppRuntime, selected: &[u32]) -> String {
    runtime
        .status()
        .and_then(|status| {
            let key = selected.first().copied().or(runtime.preferred_device);
            let device = key
                .and_then(|key| status.devices.iter().find(|device| device.key == key))
                .or_else(|| status.devices.first())?;
            Some(brand_key(
                &device.metadata.display_name,
                device
                    .metadata
                    .vendor_id
                    .and_then(|vendor| u16::try_from(vendor).ok()),
            ))
        })
        .unwrap_or_else(|| "nuit".into())
}

#[derive(Clone, Copy)]
pub(super) enum Action {
    Close,
    Select(Page),
    Language,
    Theme,
    Github,
    Hover(usize, bool),
    Press(Option<Page>),
    HeaderHover(usize, bool),
    Pointer(Option<(f32, f32)>),
}

#[derive(Clone, Copy)]
struct TextFont {
    family: &'static str,
    size: f32,
    weight: gpui::FontWeight,
}
fn moving_text(
    cache: &TextCache,
    value: &'static str,
    font: TextFont,
    ink: u32,
    height: f32,
    offset: f32,
) -> impl gpui::IntoElement + gpui::Styled {
    let TextFont {
        family,
        size,
        weight,
    } = font;
    let cache = cache.clone();
    let key = (value, family, size.to_bits(), weight.0.to_bits(), ink);
    canvas(
        move |_, window, _| {
            cache
                .borrow_mut()
                .entry(key)
                .or_insert_with(|| {
                    let mut font = gpui::Font {
                        family: family.into(),
                        ..design::ui_font()
                    };
                    font.weight = weight;
                    window.text_system().shape_line(
                        value.into(),
                        px(size),
                        &[gpui::TextRun {
                            len: value.len(),
                            font,
                            color: rgb(ink).into(),
                            background_color: None,
                            underline: None,
                            strikethrough: None,
                        }],
                        None,
                    )
                })
                .clone()
        },
        move |bounds, line, window, _| {
            let translation = gpui::point(px(0.0), px(offset));
            let baseline = (px(height) - line.ascent - line.descent) / 2.0 + line.ascent;
            let origin = bounds.origin + gpui::point(px(0.0), baseline);
            let translated_bounds = Bounds {
                origin: bounds.origin + translation,
                size: gpui::size(line.width, px(height)),
            };
            window.paint_layer(translated_bounds, |window| {
                for run in &line.runs {
                    for glyph in &run.glyphs {
                        if let Err(error) = window.paint_glyph_with_gpu_offset(
                            origin + glyph.position,
                            run.font_id,
                            glyph.id,
                            px(size),
                            rgb(ink).into(),
                            translation,
                        ) {
                            eprintln!("menu text paint failed: {error}");
                        }
                    }
                }
            });
        },
    )
    .h(px(height))
}

pub(super) fn menu<T: 'static>(
    motion: &Motion,
    settings: &Settings,
    current: Page,
    geometry: Geometry,
    dispatch: fn(&mut T, Action, &mut Window, &mut Context<T>),
    cx: &mut Context<T>,
) -> Stateful<Div> {
    let palette = design::Palette::for_theme(settings.dark);
    let Geometry {
        width,
        height,
        guide_x: guide,
        guide_y: top,
        header_x,
        menu_x: content_x,
        menu_y,
        menu_height,
    } = geometry;
    let ready = motion.accepts_input();
    let item_height = geometry.item_height();
    let item_scale = item_height / MENU_ITEM_HEIGHT;
    let title_scale = MENU_TITLE_SCALE * item_scale;
    let title_height = TITLE_HEIGHT * item_scale;
    let content_height = META_HEIGHT + META_TITLE_GAP * item_scale + title_height;
    let content_top = ((item_height - content_height) / 2.0).max(0.0);
    let title_top = content_top + META_HEIGHT + META_TITLE_GAP * item_scale;
    let text_x = content_x + MENU_TEXT_INSET;
    let text_width = (width - text_x - 48.0).max(1.0);
    let mut body = div()
        .relative()
        .w(px(width))
        .h(px(height))
        .bg(rgb(palette.curtain))
        .font(design::ui_font())
        .text_color(rgb(0x000000))
        .child(
            div()
                .id("navigation-caption")
                .absolute()
                .top_0()
                .left_0()
                .w_full()
                .h(px(top))
                .when(ready, |caption| {
                    caption.window_control_area(gpui::WindowControlArea::Drag)
                }),
        )
        .child(
            div()
                .absolute()
                .left(px(guide))
                .top_0()
                .w(px(1.0))
                .h_full()
                .bg(rgb(0x000000)),
        )
        .child(
            div()
                .absolute()
                .top(px(top))
                .left_0()
                .w_full()
                .h(px(1.0))
                .bg(rgb(0x000000)),
        );
    let mut header = div()
        .absolute()
        .left(px(header_x))
        .top(px((top - 32.0) / 2.0))
        .w(px((width
            - header_x
            - if width <= 1300.0 { 48.0 } else { 64.0 })
        .max(1.0)))
        .h(px(32.0))
        .flex()
        .items_center()
        .justify_between()
        .font_family(design::DISPLAY_FONT)
        .text_size(px(HEADER_TEXT_SIZE))
        .font_weight(gpui::FontWeight::NORMAL)
        .child(
            div()
                .flex()
                .items_center()
                .gap(px(12.0))
                .child(
                    div()
                        .id("navigation-github")
                        .size(px(22.0))
                        .flex_shrink_0()
                        .cursor_pointer()
                        .window_control_area(gpui::WindowControlArea::Client)
                        .on_click(cx.listener(move |owner, _, window, cx| {
                            if ready {
                                dispatch(owner, Action::Github, window, cx);
                            }
                            cx.stop_propagation();
                        }))
                        .child(
                            gpui::svg()
                                .path("brand/github.svg")
                                .size(px(22.0))
                                .text_color(rgb(0x000000)),
                        ),
                )
                .child(concat!("BY NUITFANEE v", env!("CARGO_PKG_VERSION"))),
        );
    let mut utilities = div().flex().items_center();
    for (index, id, label, selected, action) in [
        (0, "menu-zh", "简中", !settings.english, Action::Language),
        (1, "menu-en", "EN", settings.english, Action::Language),
        (2, "menu-light", "LIGHT", !settings.dark, Action::Theme),
        (3, "menu-dark", "DARK", settings.dark, Action::Theme),
    ] {
        if index == 1 || index == 3 {
            utilities = utilities.child(div().mx(px(8.0)).child("/"));
        }
        if index == 2 {
            utilities = utilities.child(div().w(px(if width <= 1300.0 { 20.0 } else { 32.0 })));
        }
        let underline = motion.underlines[index].value;
        utilities = utilities.child(
            div()
                .id(id)
                .relative()
                .h(px(24.0))
                .flex()
                .items_center()
                .cursor_pointer()
                .window_control_area(gpui::WindowControlArea::Client)
                .on_hover(cx.listener(move |owner, hovered: &bool, window, cx| {
                    dispatch(owner, Action::HeaderHover(index, *hovered), window, cx);
                }))
                .on_click(cx.listener(move |owner, _, window, cx| {
                    if ready {
                        dispatch(owner, action, window, cx);
                    }
                    cx.stop_propagation();
                }))
                .child(
                    div()
                        .opacity(if selected {
                            1.0
                        } else {
                            0.55 + 0.45 * underline
                        })
                        .child(label),
                )
                .child(
                    div()
                        .absolute()
                        .bottom_0()
                        .left_0()
                        .h(px(1.0))
                        .w(gpui::relative(underline))
                        .bg(rgb(0x000000)),
                ),
        );
    }
    header = header.child(utilities);
    let symbol = bezier(
        (motion.intro_elapsed / CURTAIN_SECONDS).min(1.0),
        [0.1, 0.8, 0.2, 1.0],
    ) as f32;
    body = body.child(header).child(
        div()
            .id("navigation-close")
            .absolute()
            .left(px(guide / 2.0 - 24.0))
            .top(px((top - 48.0) / 2.0))
            .w(px(48.0))
            .h(px(48.0))
            .cursor_pointer()
            .window_control_area(gpui::WindowControlArea::Client)
            .active(|style| style.opacity(0.7))
            .on_click(cx.listener(move |owner, _, window, cx| {
                if ready {
                    dispatch(owner, Action::Close, window, cx);
                }
                cx.stop_propagation();
            }))
            .child(
                canvas(
                    move |bounds, _, _| bounds,
                    move |bounds, _, window, _| {
                        let center = bounds.center();
                        let half = 16.0;
                        for slope in [-1.0, 1.0] {
                            let start =
                                gpui::point(center.x - px(half), center.y - px(half * slope));
                            let end = gpui::point(
                                start.x + px(2.0 * half * symbol),
                                start.y + px(2.0 * half * slope * symbol),
                            );
                            let mut path = gpui::PathBuilder::stroke(px(1.7));
                            path.move_to(start);
                            path.line_to(end);
                            if let Ok(path) = path.build() {
                                window.paint_path(path, rgb(0x000000));
                            }
                        }
                    },
                )
                .size_full(),
            ),
    );
    let mut items = div()
        .id("navigation-items")
        .absolute()
        .left(px(guide + 1.0))
        .top(px(menu_y))
        .w(px((width - guide - 1.0).max(1.0)))
        .h(px(menu_height))
        .overflow_y_scroll();
    for (index, page) in Page::ALL.into_iter().enumerate() {
        let entered = entry_progress(motion.intro_elapsed, index);
        let highlighted = motion.entry_highlighted(index);
        let ink = if page == current || motion.closing_page == Some(page) {
            0xffffff
        } else if highlighted {
            palette.curtain
        } else {
            0x000000
        };
        let meta_ink = if highlighted {
            palette.curtain
        } else {
            0x000000
        };
        let mut entry = div()
            .id(("navigation-entry", index))
            .relative()
            .w_full()
            .h(px(item_height))
            .flex_shrink_0()
            .overflow_hidden()
            .cursor_pointer()
            .text_color(rgb(ink))
            .on_mouse_down(
                gpui::MouseButton::Left,
                cx.listener(move |owner, _, window, cx| {
                    if ready {
                        dispatch(owner, Action::Press(Some(page)), window, cx);
                    }
                }),
            )
            .on_mouse_up_out(
                gpui::MouseButton::Left,
                cx.listener(move |owner, _, window, cx| {
                    dispatch(owner, Action::Press(None), window, cx);
                }),
            )
            .on_hover(cx.listener(move |owner, hovered: &bool, window, cx| {
                if ready || !*hovered {
                    dispatch(owner, Action::Hover(index, *hovered), window, cx);
                }
            }))
            .on_click(cx.listener(move |owner, _, window, cx| {
                if ready {
                    dispatch(owner, Action::Select(page), window, cx);
                }
                cx.stop_propagation();
            }))
            .child(
                div()
                    .absolute()
                    .inset_0()
                    .bg(rgb(0x000000))
                    .opacity(motion.hover_surfaces[index].value),
            )
            .child(
                div()
                    .absolute()
                    .left(px(content_x - guide - 1.0))
                    .top(px(content_top))
                    .w_full()
                    .h(px(META_HEIGHT))
                    .opacity(entered)
                    .child(
                        moving_text(
                            &motion.text_cache,
                            ["01.", "02.", "03.", "04.", "05."][index],
                            TextFont {
                                family: design::DISPLAY_FONT,
                                size: META_TEXT_SIZE,
                                weight: gpui::FontWeight::SEMIBOLD,
                            },
                            meta_ink,
                            META_HEIGHT,
                            -(1.0 - entered) * 15.0,
                        )
                        .absolute()
                        .left_0()
                        .w(px(24.0)),
                    )
                    .child(
                        moving_text(
                            &motion.text_cache,
                            page.menu_meta(settings.english),
                            TextFont {
                                family: design::DISPLAY_FONT,
                                size: META_TEXT_SIZE,
                                weight: gpui::FontWeight::NORMAL,
                            },
                            meta_ink,
                            META_HEIGHT,
                            -(1.0 - entered) * 15.0,
                        )
                        .absolute()
                        .left(px(MENU_TEXT_INSET))
                        .w(px(text_width)),
                    ),
            );
        let title_y =
            title_top - (1.0 - entered) * item_scale * if settings.english { 105.0 } else { 96.0 };
        if settings.english {
            let title = &titles()[index];
            entry = entry.child(
                gpui::svg()
                    .path(title.path.clone())
                    .absolute()
                    .left(px(text_x - guide - 1.0))
                    .top(px(title_top
                        + (title_height - title.bounds[3] as f32 * title_scale)
                            / 2.0))
                    .w(px(title.bounds[2] as f32 * title_scale))
                    .h(px(title.bounds[3] as f32 * title_scale))
                    .with_transformation(gpui::Transformation::translate(gpui::point(
                        px(0.0),
                        px(title_y - title_top),
                    )))
                    .text_color(rgb(ink))
                    .opacity(entered),
            );
        } else {
            entry = entry.child(
                moving_text(
                    &motion.text_cache,
                    page.label(false),
                    TextFont {
                        family: design::DISPLAY_FONT,
                        size: MENU_TITLE_SIZE * item_scale,
                        weight: gpui::FontWeight::BOLD,
                    },
                    ink,
                    title_height,
                    title_y - title_top,
                )
                .absolute()
                .left(px(text_x - guide - 1.0))
                .top(px(title_top))
                .w(px(text_width))
                .opacity(entered),
            );
        }
        items = items.child(entry);
    }
    body = body.child(items);
    let elements = &assets()[&motion.brand];
    let (brand_width, brand_height) = geometry.brand_size();
    let layout = brand_layout(brand_width, brand_height, elements.len());
    let mut brand = div()
        .id("navigation-brand")
        .relative()
        .absolute()
        .left_0()
        .top(px(menu_y))
        .w(px(brand_width))
        .h(px(brand_height))
        .overflow_hidden()
        .on_mouse_move(
            cx.listener(move |owner, event: &gpui::MouseMoveEvent, window, cx| {
                dispatch(
                    owner,
                    Action::Pointer(Some((
                        f32::from(event.position.x),
                        f32::from(event.position.y) - menu_y,
                    ))),
                    window,
                    cx,
                );
            }),
        )
        .on_hover(cx.listener(move |owner, hovered: &bool, window, cx| {
            if !*hovered {
                dispatch(owner, Action::Pointer(None), window, cx);
            }
        }));
    for (i, element) in elements.iter().enumerate() {
        let (bounds, transformation) =
            brand_placement(brand_width, layout, element, i, motion.springs.get(i));
        brand = brand.child(
            gpui::svg()
                .path(element.path.clone())
                .absolute()
                .left(bounds.origin.x)
                .top(bounds.origin.y)
                .w(bounds.size.width)
                .h(bounds.size.height)
                .with_transformation(transformation)
                .text_color(rgb(palette.brand)),
        );
    }
    div()
        .id("navigation-curtain")
        .absolute()
        .top_0()
        .left_0()
        .w_full()
        .h(px(height * motion.coverage as f32))
        .overflow_hidden()
        .occlude()
        .on_mouse_down(
            gpui::MouseButton::Left,
            cx.listener(|_, _, _, cx| cx.stop_propagation()),
        )
        .on_mouse_down(
            gpui::MouseButton::Right,
            cx.listener(|_, _, _, cx| cx.stop_propagation()),
        )
        .on_scroll_wheel(cx.listener(|_, _, _, cx| cx.stop_propagation()))
        .child(body)
        .child(brand)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn geometry() -> Geometry {
        super::geometry(DEFAULT_WIDTH as f32, DEFAULT_HEIGHT as f32)
    }
    #[test]
    fn handoff_keeps_exit_time_until_first_main_presentation() {
        let mut motion = Motion::new(true);
        let start = motion.started;
        motion.update_with_motion(
            start + Duration::from_secs(2),
            geometry(),
            &Settings::default(),
            false,
        );
        motion.begin_close_at(Some(Page::Keys), start + Duration::from_secs(2));
        motion.hold_for_handoff();
        let mut main = motion.transfer_to_main();
        let ready = start + Duration::from_secs(12);
        assert!(!main.update_with_motion(ready, geometry(), &Settings::default(), false));
        assert_eq!(main.coverage, 1.0);
        main.resume_handoff(ready);
        assert!(main.update_with_motion(ready, geometry(), &Settings::default(), false));
        assert_eq!(main.coverage, 1.0);
        assert!(!main.update_with_motion(
            ready + Duration::from_secs(1),
            geometry(),
            &Settings::default(),
            false
        ));
        assert_eq!(main.phase, Phase::Hidden);
    }

    #[test]
    fn reduced_motion_has_no_displacement_or_animation_loop_and_keeps_input() {
        let mut motion = Motion::new(true);
        let now = Instant::now();
        assert!(!motion.update_with_motion(now, geometry(), &Settings::default(), true));
        assert!(motion.accepts_input() && motion.interactive());
        assert_eq!(motion.coverage, 1.0);
        assert!(
            motion
                .springs
                .iter()
                .all(|s| s.y == 0.0 && s.scale == 1.0 && !s.moving())
        );
        motion.begin_close_at(Some(Page::Keys), now);
        assert!(!motion.update_with_motion(now, geometry(), &Settings::default(), true));
        assert_eq!(motion.phase, Phase::Hidden);
    }
    #[test]
    fn menu_springs_keep_wall_time_across_stalls_and_frame_partitions() {
        let start = Instant::now();
        let run = |step_ms: u64| {
            let mut motion = Motion::new(false);
            motion.show_at("nuit".into(), start);
            motion.update(start, geometry(), &Settings::default());
            for ms in (step_ms..2000).step_by(step_ms as usize).chain([2000]) {
                motion.update(
                    start + Duration::from_millis(ms),
                    geometry(),
                    &Settings::default(),
                );
            }
            motion
        };
        let reference = run(2);
        for step in [3, 8, 16, 33, 100, 200, 500, 2000] {
            let motion = run(step);
            assert_eq!(motion.phase, reference.phase);
            assert_eq!(motion.coverage, reference.coverage);
            for (a, b) in motion.springs.iter().zip(&reference.springs) {
                assert!((a.y - b.y).abs() < 1e-8, "step={step}: {} != {}", a.y, b.y);
                assert!((a.scale - b.scale).abs() < 1e-8);
            }
        }
        let mut motion = run(500);
        assert!(!motion.update(
            start + Duration::from_secs(60),
            geometry(),
            &Settings::default()
        ));
        assert!(motion.springs.iter().all(|spring| !spring.moving()));
    }
    #[test]
    fn menu_text_finishes_at_a_stable_zero_gpu_offset() {
        for elapsed in [MENU_READY_SECONDS, 2.0, 10.0, 86400.0] {
            for index in 0..Page::ALL.len() {
                let progress = entry_progress(elapsed, index);
                assert_eq!(progress, 1.0);
                for item_scale in [0.5, 116.0 / MENU_ITEM_HEIGHT, 1.0] {
                    assert_eq!(-(1.0 - progress) * 15.0, 0.0);
                    for distance in [96.0, 105.0] {
                        assert_eq!(-(1.0 - progress) * item_scale * distance, 0.0);
                    }
                }
            }
        }
    }
    #[test]
    fn menu_input_follows_opening_closing_and_reentry_state() {
        let mut motion = Motion::new(false);
        let start = Instant::now();
        motion.show_at("nuit".into(), start);
        motion.update(
            start + Duration::from_millis(400),
            geometry(),
            &Settings::default(),
        );
        assert!(motion.accepts_input());
        assert!(!motion.interactive());
        motion.begin_close_at(Some(Page::Keys), start + Duration::from_millis(400));
        assert!(!motion.accepts_input());
        assert!(!motion.close(None));
        motion.update(
            start + Duration::from_millis(600),
            geometry(),
            &Settings::default(),
        );
        assert!(!motion.accepts_input());
        motion.show_at("nuit".into(), start + Duration::from_millis(600));
        assert!(motion.accepts_input());
        motion.update(
            start + Duration::from_millis(2100),
            geometry(),
            &Settings::default(),
        );
        assert!(motion.interactive());
        motion.begin_close_at(Some(Page::Keys), start + Duration::from_millis(2100));
        motion.update(
            start + Duration::from_millis(2500),
            geometry(),
            &Settings::default(),
        );
        assert!(!motion.accepts_input());
        motion.show_at("nuit".into(), start + Duration::from_millis(2500));
        assert!(motion.accepts_input());
        let mut startup = Motion::new(true);
        startup.update(
            startup.started + Duration::from_millis(810),
            geometry(),
            &Settings::default(),
        );
        assert!(startup.interactive());
    }

    #[test]
    fn default_menu_animates_opening_highlights_and_closing() {
        let mut motion = Motion::new(false);
        let now = Instant::now();
        motion.show_at("nuit".into(), now);
        assert!(motion.update(now, geometry(), &Settings::default()));
        assert_eq!(motion.phase, Phase::Opening);
        assert_eq!(motion.transition_elapsed, 0.0);
        assert_eq!(motion.intro_elapsed, 0.0);
        assert!(motion.springs.iter().all(|spring| spring.y != 0.0));
        let opening_coverage = motion.coverage;
        let entry_starts: Vec<_> = (0..Page::ALL.len())
            .map(|index| entry_progress(motion.intro_elapsed, index))
            .collect();
        assert!(motion.update(
            now + Duration::from_millis(400),
            geometry(),
            &Settings::default()
        ));
        assert!(motion.coverage > opening_coverage && motion.coverage < 1.0);
        motion.update(
            now + Duration::from_secs(2),
            geometry(),
            &Settings::default(),
        );
        assert!(motion.interactive());
        assert_eq!(motion.coverage, 1.0);
        for (index, initial) in entry_starts.into_iter().enumerate() {
            let complete = entry_progress(motion.intro_elapsed, index);
            assert!(complete > initial);
            assert_eq!(complete, 1.0);
        }
        let hover = now + Duration::from_secs(2);
        motion.entry_hover(2, true);
        motion.header_hover(1, true);
        assert!(motion.update(hover, geometry(), &Settings::default()));
        assert_eq!(motion.hover_surfaces[2].value, 0.0);
        assert_eq!(motion.underlines[1].value, 0.0);
        motion.update(
            hover + Duration::from_millis(100),
            geometry(),
            &Settings::default(),
        );
        assert!(motion.hover_surfaces[2].value > 0.0 && motion.hover_surfaces[2].value < 1.0);
        assert!(motion.underlines[1].value > 0.0 && motion.underlines[1].value < 1.0);
        let closing = hover + Duration::from_millis(400);
        motion.update(closing, geometry(), &Settings::default());
        assert_eq!(motion.hover_surfaces[2].value, 1.0);
        assert_eq!(motion.underlines[1].value, 1.0);
        motion.begin_close_at(Some(Page::Performance), closing);
        assert!(motion.update(closing, geometry(), &Settings::default()));
        assert_eq!(motion.phase, Phase::Closing);
        assert_eq!(motion.coverage, 1.0);
        assert!(motion.update(
            closing + Duration::from_millis(400),
            geometry(),
            &Settings::default()
        ));
        assert!(motion.coverage > 0.0 && motion.coverage < 1.0);
        assert!(!motion.update(
            closing + Duration::from_millis(800),
            geometry(),
            &Settings::default()
        ));
        assert_eq!(motion.phase, Phase::Hidden);
        assert_eq!(motion.coverage, 0.0);
        assert!(!motion.accepts_input());
    }
    #[test]
    fn previous_items_leave_cannot_clear_a_new_hover() {
        let mut motion = Motion::new(false);
        motion.entry_hover(1, true);
        motion.entry_hover(2, true);
        motion.entry_hover(1, false);
        assert_eq!(motion.hovered, Some(2));
        motion.header_hover(0, true);
        motion.header_hover(1, true);
        motion.header_hover(0, false);
        assert_eq!(motion.header_hover, Some(1));
    }
}
