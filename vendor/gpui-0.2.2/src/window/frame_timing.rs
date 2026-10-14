//! Opt-in, bounded CPU timings of actual redraws, not platform callbacks or scanout.
use super::{Duration, Instant, Window};

const CAPACITY: usize = 4096;

#[derive(Clone, Copy, Debug)]
/// CPU boundaries of one actual redraw, excluding present-only platform callbacks.
pub struct FrameTiming {
    /// Start of the redraw on the UI thread.
    pub started: Instant,
    /// Layout, prepaint, paint and chart D3D11 command submission.
    pub draw: Duration,
    /// Platform composition submission/Present; not GPU completion or physical scanout.
    pub present: Duration,
    /// Destruction of the frame's transient element arena after presentation.
    pub cleanup: Duration,
}

#[derive(Debug)]
/// A fixed-capacity diagnostic buffer with explicit overflow accounting.
pub struct FrameTimings {
    /// Actual redraws, in UI-thread execution order.
    pub samples: Vec<FrameTiming>,
    /// Redraws observed after the buffer filled.
    pub omitted: usize,
}

impl FrameTimings {
    fn new() -> Self {
        Self {
            samples: Vec::with_capacity(CAPACITY),
            omitted: 0,
        }
    }

    pub(super) fn record(&mut self, sample: FrameTiming) {
        if self.samples.len() < CAPACITY {
            self.samples.push(sample);
        } else {
            self.omitted += 1;
        }
    }
}

impl Window {
    /// Start observing actual redraws without requesting any new frames.
    pub fn start_frame_timing(&mut self) {
        self.frame_timings = Some(FrameTimings::new());
    }

    /// Stop the observer and transfer its bounded buffer out of the render path.
    pub fn take_frame_timings(&mut self) -> Option<FrameTimings> {
        self.frame_timings.take()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn timings_are_bounded_and_record_overflow_without_growing() {
        let mut timings = FrameTimings::new();
        let sample = FrameTiming {
            started: Instant::now(),
            draw: Duration::from_micros(4),
            present: Duration::from_micros(5),
            cleanup: Duration::from_micros(1),
        };
        let capacity = timings.samples.capacity();
        for _ in 0..CAPACITY + 3 {
            timings.record(sample);
        }
        assert_eq!(timings.samples.len(), CAPACITY);
        assert_eq!(timings.omitted, 3);
        assert_eq!(timings.samples.capacity(), capacity);
        assert_eq!(timings.samples[0].draw, sample.draw);
        assert_eq!(timings.samples[CAPACITY - 1].started, sample.started);
    }
}
