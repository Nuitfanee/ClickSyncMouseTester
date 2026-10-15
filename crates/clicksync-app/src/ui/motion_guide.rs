//! An independently rendered movement lesson, never a capture clock.
use super::*;
use std::time::Instant;

const PERIOD_SECONDS: f64 = 2.4;
const FADE_SECONDS: f64 = 0.24;
const RESET_HIDE_SECONDS: f64 = 0.06;

pub(super) struct MotionGuide {
    pub palette: design::Palette,
    paired: bool,
    demonstrating: bool,
    started: Option<Instant>,
    position: f32,
    opacity: f32,
}

impl MotionGuide {
    pub fn new(paired: bool, palette: design::Palette) -> Self {
        Self {
            palette,
            paired,
            demonstrating: true,
            started: Some(Instant::now()),
            position: 0.0,
            opacity: 1.0,
        }
    }
    pub(super) fn set_demonstrating(&mut self, demonstrating: bool) -> bool {
        if self.demonstrating == demonstrating {
            return false;
        }
        self.demonstrating = demonstrating;
        self.started = None;
        self.position = 0.0;
        self.opacity = 1.0;
        true
    }
    #[cfg(feature = "ui-restoration-probe")]
    pub(super) fn preview(palette: design::Palette) -> Self {
        let mut guide = Self::new(true, palette);
        guide.started = Some(Instant::now() - Duration::from_millis(1000));
        guide
    }
    #[cfg(feature = "ui-restoration-probe")]
    pub(super) fn preview_ready(&self) -> bool {
        self.paired || self.started.is_none()
    }
    fn advance(&mut self, now: Instant, active: bool) -> bool {
        if !active {
            self.started = None;
            return false;
        }
        if self.paired && self.started.is_none() {
            self.started = Some(now);
        }
        if let Some(start) = self.started {
            let time = now.saturating_duration_since(start).as_secs_f64();
            if self.paired {
                let cycle_time = time % PERIOD_SECONDS;
                let phase = (cycle_time / PERIOD_SECONDS) as f32;
                self.position = 72.0 * (phase * phase * (3.0 - 2.0 * phase) - 0.5);
                // Hide both sides of the wrap; display frames need not land on the boundary.
                self.opacity = ((cycle_time - RESET_HIDE_SECONDS) / FADE_SECONDS)
                    .min((PERIOD_SECONDS - RESET_HIDE_SECONDS - cycle_time) / FADE_SECONDS)
                    .clamp(0.0, 1.0) as f32;
            } else {
                let phase = (time / PERIOD_SECONDS).min(1.0) as f32;
                self.position = 46.0
                    * (std::f32::consts::TAU * phase).sin()
                    * (std::f32::consts::PI * phase).sin().powi(2);
                if time >= PERIOD_SECONDS {
                    self.position = 0.0;
                    self.started = None;
                }
            }
        }
        self.started.is_some()
    }
}

impl Render for MotionGuide {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let reduced = motion_preference::reduced();
        if reduced {
            self.position = 0.0;
            self.opacity = 1.0;
            self.started = None;
        }
        if !reduced
            && self.advance(
                Instant::now(),
                window.is_window_active() && self.demonstrating,
            )
        {
            let entity = cx.entity().downgrade();
            window.on_next_frame(move |_, cx| {
                let _ = entity.update(cx, |_, cx| cx.notify());
            });
        }
        let palette = self.palette;
        let mut mice = div()
            .flex()
            .items_center()
            .justify_center()
            .gap(px(4.0))
            .opacity(self.opacity);
        for side in 0..if self.paired { 2 } else { 1 } {
            mice = mice.child(
                gpui::svg()
                    .path("brand/mouse-guide.svg")
                    .with_alpha_mode(gpui::SvgAlphaMode::Coverage)
                    .w(px(60.0))
                    .h(px(88.0))
                    .text_color(rgb(if side == 0 {
                        palette.accent
                    } else {
                        palette.text
                    }))
                    .with_transformation(gpui::Transformation::translate(gpui::point(
                        px(self.position),
                        px(0.0),
                    ))),
            );
        }
        div()
            .flex()
            .flex_col()
            .items_center()
            .gap(px(16.0))
            .py(px(16.0))
            .child(mice)
            .child(
                div()
                    .text_size(px(22.0))
                    .text_color(rgb(palette.muted))
                    .child(if self.paired {
                        "→"
                    } else {
                        "←                   →"
                    }),
            )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn completed_demonstration_returns_to_a_visible_static_pose() {
        let mut guide = MotionGuide::new(true, design::Palette::for_theme(false));
        guide.position = 36.0;
        guide.opacity = 0.0;
        assert!(guide.set_demonstrating(false));
        assert_eq!(guide.position, 0.0);
        assert_eq!(guide.opacity, 1.0);
        assert!(!guide.advance(Instant::now(), guide.demonstrating));
        assert!(!guide.set_demonstrating(false));
        assert!(guide.set_demonstrating(true));
        assert!(guide.advance(Instant::now(), guide.demonstrating));
    }

    #[test]
    fn paired_guide_moves_forward_fades_at_wrap_and_stops_when_inactive() {
        let start = Instant::now();
        let mut guide = MotionGuide::new(true, design::Palette::for_theme(false));
        guide.started = Some(start);
        let mut previous = -36.0;
        for ms in (0..2400).step_by(10) {
            assert!(guide.advance(start + Duration::from_millis(ms), true));
            assert!(guide.position >= previous);
            assert!((0.0..=1.0).contains(&guide.opacity));
            previous = guide.position;
        }
        for (ms, position) in [(0, -36.0), (600, -24.75), (1200, 0.0), (1800, 24.75)] {
            assert!(guide.advance(start + Duration::from_millis(ms), true));
            assert!((guide.position - position).abs() < 0.0001);
        }
        guide.advance(start + Duration::from_millis(2400), true);
        assert_eq!((guide.position, guide.opacity), (-36.0, 0.0));
        guide.advance(start + Duration::from_millis(2700), true);
        let frozen = (guide.position, guide.opacity);
        assert!(!guide.advance(start + Duration::from_secs(3), false));
        assert!(!guide.advance(start + Duration::from_secs(4), false));
        assert_eq!((guide.position, guide.opacity), frozen);
        assert!(guide.advance(start + Duration::from_secs(5), true));
        assert_eq!((guide.position, guide.opacity), (-36.0, 0.0));
    }

    #[test]
    fn paired_wrap_is_hidden_between_unsynchronized_display_frames() {
        let start = Instant::now();
        for hz in [30, 60, 120, 144, 240] {
            for offset_ms in [1, 5, 11, 19] {
                let mut guide = MotionGuide::new(true, design::Palette::for_theme(false));
                guide.started = Some(start);
                let mut previous = (-36.0, 0.0);
                let mut wraps = 0;
                for frame in 0..hz * 8 {
                    let seconds = offset_ms as f64 / 1000.0 + frame as f64 / hz as f64;
                    guide.advance(start + Duration::from_secs_f64(seconds), true);
                    if guide.position < previous.0 {
                        assert_eq!(previous.1, 0.0, "{hz} Hz: last frame before wrap");
                        assert_eq!(guide.opacity, 0.0, "{hz} Hz: first frame after wrap");
                        wraps += 1;
                    }
                    previous = (guide.position, guide.opacity);
                }
                assert_eq!(wraps, 3);
            }
        }
    }

    #[test]
    fn paired_fade_has_a_full_opacity_plateau_and_hidden_reset_window() {
        let start = Instant::now();
        let mut guide = MotionGuide::new(true, design::Palette::for_theme(false));
        guide.started = Some(start);
        for ms in 0..=4800 {
            guide.advance(start + Duration::from_millis(ms), true);
            let cycle_ms = ms % 2400;
            if !(60..=2340).contains(&cycle_ms) {
                assert_eq!(guide.opacity, 0.0);
            } else if (301..2100).contains(&cycle_ms) {
                assert_eq!(guide.opacity, 1.0);
            }
            assert!((-36.0..=36.0).contains(&guide.position));
            assert!((0.0..=1.0).contains(&guide.opacity));
        }
        for ms in [60, 61, 2340, 2341] {
            guide.advance(start + Duration::from_millis(ms), true);
            let before = guide.opacity;
            guide.advance(start + Duration::from_millis(ms + 1), true);
            assert!((guide.opacity - before).abs() <= 1.0 / 240.0 + f32::EPSILON);
        }
    }

    #[test]
    fn single_guide_keeps_its_calibration_trajectory_and_finishes_at_rest() {
        let start = Instant::now();
        let mut guide = MotionGuide::new(false, design::Palette::for_theme(false));
        guide.started = Some(start);
        for (ms, position) in [(0, 0.0), (600, 23.0), (1200, 0.0), (1800, -23.0)] {
            assert!(guide.advance(start + Duration::from_millis(ms), true));
            assert!((guide.position - position).abs() < 0.0001);
            assert_eq!(guide.opacity, 1.0);
        }
        for ms in [2400, 3000, 4800] {
            assert!(!guide.advance(start + Duration::from_millis(ms), true));
            assert_eq!((guide.position, guide.opacity), (0.0, 1.0));
        }
    }

    #[test]
    fn new_guides_animate_immediately_and_single_guide_finishes_once() {
        for paired in [false, true] {
            let mut guide = MotionGuide::new(paired, design::Palette::for_theme(false));
            let start = guide.started.expect("new guide starts its animation");
            assert!(guide.advance(start + Duration::from_millis(600), true));
            assert!(guide.position.abs() > 20.0);
            assert_eq!(guide.opacity, 1.0);
        }
        let mut guide = MotionGuide::new(false, design::Palette::for_theme(false));
        let now = guide.started.unwrap();
        assert!(guide.advance(now + Duration::from_secs(1), true));
        assert!(!guide.advance(now + Duration::from_secs(3), true));
        assert!(!guide.advance(now + Duration::from_secs(4), true));
        assert_eq!(guide.position, 0.0);
    }
}
