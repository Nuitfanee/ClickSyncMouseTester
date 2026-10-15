use super::{Viewport, shift_view, stable_value_axis};
use std::time::{Duration, Instant};

#[derive(Default)]
pub(super) struct Stability {
    shrink: Option<(Instant, Viewport)>,
}

impl Stability {
    pub(super) fn update(
        &mut self,
        current: Viewport,
        mut required: Viewport,
        now: Instant,
        collecting: bool,
    ) -> Viewport {
        let bottom = required.origin.y.relative_to(current.origin.y);
        let top = bottom + required.height;
        if bottom < 0.0 || top > current.height {
            self.shrink = None;
            return stable_value_axis(current, required);
        }
        if collecting && required.height < current.height * 0.55 {
            let (since, pending) = self.shrink.get_or_insert((now, required));
            let offset = required.origin.y.relative_to(pending.origin.y);
            let low = offset.min(0.0);
            let high = (offset + required.height).max(pending.height);
            pending.origin.y = shift_view(pending.origin.y, low);
            pending.height = high - low;
            if pending.height >= current.height * 0.55 {
                self.shrink = Some((now, required));
            } else if now.saturating_duration_since(*since) >= Duration::from_secs(1) {
                // Shrink to the union observed for a full second, not a single
                // quiet frame. No timer, history scan or redraw is scheduled here.
                required.origin.y = pending.origin.y;
                required.height = pending.height;
                self.shrink = None;
                return required;
            }
        } else {
            self.shrink = None;
        }
        required.origin.y = current.origin.y;
        required.height = current.height;
        required
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clicksync_chart::{Coordinate, Point};

    fn view(x: f64, y: f64, height: f64) -> Viewport {
        Viewport {
            origin: Point {
                x: Coordinate::Scalar(x),
                y: Coordinate::Scalar(y),
            },
            width: 800.0,
            height,
        }
    }

    #[test]
    fn jitter_holds_y_but_follows_x_and_expansion_has_headroom() {
        let mut state = Stability::default();
        let now = Instant::now();
        let current = view(0.0, -10.0, 20.0);
        for i in 0..1000 {
            let next = state.update(
                current,
                view(i as f64, -8.0 + (i % 3) as f64 * 0.1, 16.0),
                now + Duration::from_millis(i),
                true,
            );
            assert_eq!(next.origin.y, current.origin.y);
            assert_eq!(next.height, current.height);
            assert_eq!(next.origin.x.value(), i as f64);
        }
        let expanded = state.update(current, view(1000.0, -10.1, 20.2), now, true);
        assert!(expanded.origin.y.value() < -12.0);
        assert!(expanded.origin.y.value() + expanded.height > 12.0);
        assert_eq!(
            state.update(expanded, view(1000.0, -10.2, 20.4), now, true),
            expanded
        );
    }

    #[test]
    fn shrink_requires_a_full_second_of_small_ranges_and_covers_their_union() {
        let now = Instant::now();
        let current = view(0.0, -100.0, 200.0);
        let mut state = Stability::default();
        let a = view(1.0, -4.0, 8.0);
        let b = view(2.0, -6.0, 10.0);
        assert_eq!(state.update(current, a, now, true).height, 200.0);
        assert_eq!(
            state
                .update(current, b, now + Duration::from_millis(999), true)
                .height,
            200.0
        );
        let shrunk = state.update(current, a, now + Duration::from_secs(1), true);
        assert_eq!(shrunk.origin.y.value(), -6.0);
        assert_eq!(shrunk.height, 10.0);
        assert_eq!(shrunk.origin.x, a.origin.x);
        assert_eq!(
            state.update(shrunk, a, now + Duration::from_secs(10), true),
            shrunk
        );
    }

    #[test]
    fn pause_and_wide_candidates_cancel_pending_shrink() {
        let now = Instant::now();
        let current = view(0.0, 0.0, 200.0);
        let small = view(0.0, 1.0, 8.0);
        for interrupt in [view(0.0, 0.0, 180.0), small] {
            let mut state = Stability::default();
            state.update(current, small, now, true);
            let collecting = interrupt.height > 8.0;
            assert_eq!(
                state.update(current, interrupt, now + Duration::from_secs(2), collecting),
                current
            );
            assert_eq!(
                state.update(current, small, now + Duration::from_secs(3), true),
                current
            );
            assert!(
                state
                    .update(current, small, now + Duration::from_secs(4), true)
                    .height
                    < current.height
            );
        }
        let expanded = stable_value_axis(view(0.0, 1.0, 2.0), view(0.0, 0.0, 4.0));
        assert_eq!(expanded.origin.y.value(), 0.0);
    }
}
