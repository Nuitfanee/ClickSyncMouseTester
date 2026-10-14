use super::*;

pub(super) const HEAD_TAU_MS: f64 = 10.0;
// Keep live deformation close to NOW instead of replacing 100 ms of genuine
// history with a single moving arc. Unconfirmed/sparse history can extend it.
pub(super) const TAIL_MS: f64 = 20.0;

#[derive(Default)]
pub(super) struct Head {
    identity: Option<(u64, u64)>,
    pub rate: f64,
    target: f64,
    drawn: Option<Instant>,
    running: bool,
}

impl Head {
    pub fn update(&mut self, identity: (u64, u64), target: f64, running: bool, now: Instant) {
        let target = if target.is_finite() {
            target.max(0.0)
        } else {
            0.0
        };
        if self.identity != Some(identity) {
            *self = Self {
                identity: Some(identity),
                rate: target,
                target,
                drawn: Some(now),
                running,
            };
            return;
        }
        if self.running != running {
            // Freeze exactly what was drawn. Paused wall time never enters dt.
            self.drawn = Some(now);
        }
        self.target = target;
        self.running = running;
    }

    pub fn sample(&mut self, now: Instant) -> f64 {
        if self.running {
            let delta_ms = self.drawn.map_or(0.0, |last| {
                now.saturating_duration_since(last).as_secs_f64() * 1000.0
            });
            let alpha = -(-delta_ms / HEAD_TAU_MS).exp_m1();
            self.rate += (self.target - self.rate) * alpha;
        }
        if self.drawn.is_none_or(|last| now >= last) {
            self.drawn = Some(now);
        }
        self.rate
    }

    pub fn animating(&self) -> bool {
        self.running && (self.target - self.rate).abs() > 0.01
    }
}

/// The seam advances through confirmed history at display time. Arrival of a
/// result at the same Instant cannot advance it or reshape the old live tail.
#[derive(Default)]
pub(super) struct Seam {
    identity: Option<(u64, u64)>,
    pub logical_ms: f64,
    cursor_ms: f64,
    drawn: Option<Instant>,
    running: bool,
}

impl Seam {
    pub fn sample(&mut self, identity: (u64, u64), limit: f64, now: Instant, running: bool) -> f64 {
        if self.running != running {
            self.drawn = Some(now);
            self.running = running;
            self.cursor_ms = limit.max(self.logical_ms);
        }
        if self.identity != Some(identity) {
            self.identity = Some(identity);
            self.logical_ms = limit;
            self.cursor_ms = limit;
        } else if running {
            let elapsed = self.drawn.map_or(0.0, |last| {
                now.saturating_duration_since(last).as_secs_f64() * 1000.0
            });
            // Clamp the visible seam, not its clock: unavailable history must
            // not turn discarded frame time into a permanently growing tail.
            self.cursor_ms += elapsed;
            if elapsed > 0.0 {
                self.logical_ms = self.logical_ms.max(self.cursor_ms.min(limit));
            }
        }
        if self.drawn.is_none_or(|last| now >= last) {
            self.drawn = Some(now);
        }
        self.logical_ms
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stop_response_does_not_add_a_second_window_of_visual_decay() {
        let start = Instant::now();
        for fps in [60, 120, 240] {
            let mut head = Head::default();
            head.update((1, 0), 4000.0, true, start);
            let mut value = head.sample(start);
            for frame in 1..=(fps * 350 / 1000) {
                let elapsed = f64::from(frame) * 1000.0 / f64::from(fps);
                let target = 4000.0 * (1.0 - elapsed / 300.0).max(0.0);
                let now = start + Duration::from_secs_f64(elapsed / 1000.0);
                head.update((1, 0), target, true, now);
                let next = head.sample(now);
                assert!(next <= value && next >= target);
                value = next;
            }
            assert!(value < 1.0, "head retained {value} Hz at {fps} fps");
        }
    }

    #[test]
    fn ten_ms_response_is_frame_time_based_and_duplicate_draws_do_not_advance_it() {
        let start = Instant::now();
        for step in [1, 5, 10, 20] {
            let mut head = Head::default();
            head.update((1, 0), 1000.0, true, start);
            head.update((1, 0), 4000.0, true, start);
            assert_eq!(head.sample(start), 1000.0);
            for time in (step..=20).step_by(step) {
                let now = start + Duration::from_millis(time as u64);
                let value = head.sample(now);
                assert_eq!(head.sample(now), value);
            }
            assert!((head.rate - (4000.0 - 3000.0 * (-2.0_f64).exp())).abs() < 1e-9);
        }
    }

    #[test]
    fn reversal_alternation_zero_long_frames_pause_resume_and_reset_are_finite() {
        let start = Instant::now();
        let mut head = Head::default();
        head.update((1, 0), 1000.0, true, start);
        for (time, target) in [
            (0, 2000.0),
            (5, 3000.0),
            (10, 1000.0),
            (15, 4000.0),
            (20, 0.0),
        ] {
            let now = start + Duration::from_millis(time);
            head.sample(now);
            head.update((1, 0), target, true, now);
            assert!((0.0..=4000.0).contains(&head.rate));
        }
        head.sample(start + Duration::from_millis(30));
        let frozen = head.rate;
        head.update((1, 0), 0.0, false, start + Duration::from_millis(30));
        assert_eq!(head.sample(start + Duration::from_secs(10)), frozen);
        head.update((1, 0), 2000.0, true, start + Duration::from_secs(10));
        assert_eq!(head.sample(start + Duration::from_secs(10)), frozen);
        assert!(
            (head.sample(start + Duration::from_millis(10010))
                - (2000.0 + (frozen - 2000.0) * (-1.0_f64).exp()))
            .abs()
                < 1e-9
        );
        assert_eq!(head.sample(start + Duration::from_secs(20)), 2000.0);
        head.update((1, 1), 0.0, true, start + Duration::from_secs(20));
        assert_eq!(head.sample(start + Duration::from_secs(20)), 0.0);
        head.update((2, 0), 4000.0, true, start + Duration::from_secs(20));
        assert_eq!(head.rate, 4000.0);
    }

    #[test]
    fn new_or_late_samples_do_not_move_the_seam_at_the_same_display_time() {
        let start = Instant::now();
        let mut seam = Seam::default();
        assert_eq!(seam.sample((1, 0), 20.0, start, true), 20.0);
        assert_eq!(seam.sample((1, 0), 40.0, start, true), 20.0);
        assert_eq!(
            seam.sample((1, 0), 40.0, start + Duration::from_millis(8), true),
            28.0
        );
        assert_eq!(
            seam.sample((1, 0), 25.0, start + Duration::from_millis(10), true),
            28.0
        );
        assert_eq!(
            seam.sample((1, 0), 1000.0, start + Duration::from_secs(5), false),
            28.0
        );
        assert_eq!(
            seam.sample((1, 0), 1000.0, start + Duration::from_secs(10), true),
            28.0
        );
        assert_eq!(
            seam.sample((2, 0), 0.0, start + Duration::from_secs(5), true),
            0.0
        );
    }
}
