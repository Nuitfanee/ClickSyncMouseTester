use std::{
    sync::LazyLock,
    time::{Duration, Instant},
};

use anyhow::{Context, Result};
use util::ResultExt;
use windows::Win32::{
    Foundation::HWND,
    Graphics::Dwm::{DWM_TIMING_INFO, DwmFlush, DwmGetCompositionTimingInfo},
    System::Performance::QueryPerformanceFrequency,
};

static QPC_TICKS_PER_SECOND: LazyLock<u64> = LazyLock::new(|| {
    let mut frequency = 0;
    // On systems that run Windows XP or later, the function will always succeed and
    // will thus never return zero.
    unsafe { QueryPerformanceFrequency(&mut frequency).unwrap() };
    frequency as u64
});

const VSYNC_INTERVAL_THRESHOLD: Duration = Duration::from_millis(1);
const DEFAULT_VSYNC_INTERVAL: Duration = Duration::from_micros(16_666); // ~60Hz

pub(crate) struct VSyncProvider {
    interval: Duration,
    f: Box<dyn Fn() -> bool>,
}

impl VSyncProvider {
    pub(crate) fn new() -> Self {
        let interval = get_dwm_interval()
            .context("Failed to get DWM interval")
            .log_err()
            .unwrap_or(DEFAULT_VSYNC_INTERVAL);
        let f = Box::new(|| unsafe { DwmFlush().is_ok() });
        Self { interval, f }
    }

    pub(crate) fn wait_for_vsync(&self) {
        let vsync_start = Instant::now();
        let wait_succeeded = (self.f)();
        let elapsed = vsync_start.elapsed();
        // DwmFlush and DCompositionWaitForCompositorClock returns very early
        // instead of waiting until vblank when the monitor goes to sleep or is
        // unplugged (nothing to present due to desktop occlusion). We use 1ms as
        // a threshold for the duration of the wait functions and fallback to
        // Sleep() if it returns before that. This could happen during normal
        // operation for the first call after the vsync thread becomes non-idle,
        // but it shouldn't happen often.
        if !wait_succeeded || elapsed < VSYNC_INTERVAL_THRESHOLD {
            log::trace!("VSyncProvider::wait_for_vsync() took less time than expected");
            std::thread::sleep(self.interval);
        }
    }
}

fn get_dwm_interval() -> Result<Duration> {
    let mut timing_info = DWM_TIMING_INFO {
        cbSize: std::mem::size_of::<DWM_TIMING_INFO>() as u32,
        ..Default::default()
    };
    unsafe { DwmGetCompositionTimingInfo(HWND::default(), &mut timing_info) }?;
    Ok(refresh_interval(
        timing_info.qpcRefreshPeriod,
        *QPC_TICKS_PER_SECOND,
        timing_info.rateRefresh.uiDenominator as u64,
        timing_info.rateRefresh.uiNumerator as u64,
    ))
}

fn refresh_interval(qpc_period: u64, qpc_frequency: u64, denominator: u64, numerator: u64) -> Duration {
    // Check for interval values that are impossibly low. A 29 microsecond
    // interval was seen (from a qpcRefreshPeriod of 60).
    retrieve_duration(qpc_period, qpc_frequency)
        .filter(|interval| *interval >= VSYNC_INTERVAL_THRESHOLD)
        .or_else(|| {
            retrieve_duration(denominator, numerator)
                .filter(|interval| *interval >= VSYNC_INTERVAL_THRESHOLD)
        })
        .unwrap_or(DEFAULT_VSYNC_INTERVAL)
}

#[inline]
fn retrieve_duration(counts: u64, ticks_per_second: u64) -> Option<Duration> {
    if ticks_per_second == 0 {
        return None;
    }
    Some(Duration::new(
        counts / ticks_per_second,
        ((counts % ticks_per_second) as u128 * 1_000_000_000 / ticks_per_second as u128) as u32,
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn vsync_fallback_preserves_refresh_fractions_and_survives_invalid_timing() {
        for (denominator, numerator, nanos) in [
            (1, 60, 16_666_666),
            (1001, 60_000, 16_683_333),
            (1, 360, 2_777_777),
        ] {
            assert_eq!(
                refresh_interval(60, 10_000_000, denominator, numerator),
                Duration::from_nanos(nanos)
            );
        }
        assert_eq!(refresh_interval(166_667, 10_000_000, 0, 0), Duration::from_nanos(16_666_700));
        for args in [(0, 0, 0, 0), (0, 10_000_000, 1, 0), (1, 10_000_000, 0, 60)] {
            assert_eq!(refresh_interval(args.0, args.1, args.2, args.3), DEFAULT_VSYNC_INTERVAL);
        }
        assert_eq!(retrieve_duration(u64::MAX, 1), Some(Duration::from_secs(u64::MAX)));
    }
}
