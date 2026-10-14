//! Bounded, event-time report counts. Reading metrics never changes eligibility or peak tiers.
use serde::Serialize;
use std::collections::VecDeque;

pub const WINDOW_MS: f64 = 300.0;
const MAX_RATE: f64 = 20000.0;
const PEAK_CONFIRM_MS: f64 = 20.0;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize)]
pub enum PollingMode {
    #[default]
    RawPacketRate,
    MotionReportRate,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize)]
pub enum RateStatus {
    #[default]
    WarmingUp,
    Valid,
    Delayed,
    Incomplete,
    OutOfRange,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize)]
pub struct PollingMetrics {
    pub current_rate: Option<i32>,
    pub peak_rate: i32,
    pub empty_packet_percent: Option<f64>,
    pub dropped_packets: u64,
    pub status: RateStatus,
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize)]
pub struct HistoryPoint {
    pub logical_ms: f64,
    pub observed_ms: f64,
    pub rate: f64,
    /// Distinct runs never share a curve, including resume and unavailable observations.
    pub segment: u64,
}

pub struct PollingEngine {
    mode: PollingMode,
    timestamps: VecDeque<f64>,
    // Cumulative empty count permits logarithmic queries without scanning 1 s of packets.
    empty: VecDeque<(f64, u64)>,
    empty_total: u64,
    empty_before: u64,
    rate_lost_at: f64,
    empty_lost_at: f64,
    history: VecDeque<HistoryPoint>,
    rate_eligible: f64,
    empty_eligible: f64,
    latest: f64,
    peak_eligible: f64,
    peak: i32,
    pending_peak: Option<(f64, i32)>,
    paused_at: f64,
    paused_total: f64,
    history_interval: f64,
    history_observed: f64,
    segment: u64,
}

impl Default for PollingEngine {
    fn default() -> Self {
        Self {
            mode: PollingMode::RawPacketRate,
            timestamps: VecDeque::with_capacity(8192),
            empty: VecDeque::with_capacity(32768),
            empty_total: 0,
            empty_before: 0,
            rate_lost_at: f64::NEG_INFINITY,
            empty_lost_at: f64::NEG_INFINITY,
            history: VecDeque::with_capacity(4096),
            rate_eligible: f64::NAN,
            empty_eligible: f64::NAN,
            latest: f64::NAN,
            peak_eligible: f64::NAN,
            peak: 0,
            pending_peak: None,
            paused_at: f64::NAN,
            paused_total: 0.0,
            history_interval: 1000.0 / 120.0,
            history_observed: f64::NEG_INFINITY,
            segment: 0,
        }
    }
}

impl PollingEngine {
    pub fn reset(&mut self) {
        self.reset_segment();
        self.history.clear();
        self.peak = 0;
        self.peak_eligible = f64::NAN;
        self.paused_at = f64::NAN;
        self.paused_total = 0.0;
        self.history_observed = f64::NEG_INFINITY;
        self.segment = 0;
    }
    fn reset_segment(&mut self) {
        self.timestamps.clear();
        self.empty.clear();
        self.empty_total = 0;
        self.empty_before = 0;
        self.rate_lost_at = f64::NEG_INFINITY;
        self.empty_lost_at = f64::NEG_INFINITY;
        self.rate_eligible = f64::NAN;
        self.empty_eligible = f64::NAN;
        self.latest = f64::NAN;
        self.pending_peak = None;
    }
    pub fn set_mode(&mut self, mode: PollingMode) {
        if self.mode != mode {
            self.mode = mode;
            self.reset();
        }
    }
    pub fn set_history_interval(&mut self, interval_ms: f64) {
        self.history_interval = if interval_ms.is_finite() && interval_ms > 0.0 {
            interval_ms.clamp(1.0, 100.0)
        } else {
            1000.0 / 120.0
        };
    }
    pub fn begin_segment(&mut self, now_ms: f64) {
        self.reset_segment();
        if self.paused_at.is_finite() && now_ms > self.paused_at {
            self.paused_total += now_ms - self.paused_at;
        }
        self.paused_at = f64::NAN;
        self.peak_eligible = now_ms + 2.0 * WINDOW_MS;
        self.break_history();
    }
    pub fn end_segment(&mut self, now_ms: f64) {
        if now_ms.is_finite() && !self.paused_at.is_finite() {
            self.paused_at = now_ms;
        }
        self.pending_peak = None;
        self.peak_eligible = f64::NAN;
    }
    pub fn break_history(&mut self) {
        self.segment = self.segment.wrapping_add(1);
    }
    pub fn push(&mut self, timestamp_ms: f64, dx: i32, dy: i32) {
        if !timestamp_ms.is_finite() {
            return;
        }
        let timestamp = timestamp_ms.max(self.latest);
        self.latest = timestamp;
        let empty = dx == 0 && dy == 0;
        let counted = self.mode == PollingMode::RawPacketRate || !empty;
        if counted && !self.rate_eligible.is_finite() {
            self.rate_eligible = timestamp + 310.0;
            self.empty_eligible = timestamp + 1000.0;
        }
        while self
            .empty
            .front()
            .is_some_and(|p| p.0 <= timestamp - 1000.0)
        {
            self.empty_before = self.empty.pop_front().unwrap().1;
        }
        if self.empty_eligible.is_finite() {
            if self.empty.len() == 32768 {
                let (time, total) = self.empty.pop_front().unwrap();
                self.empty_lost_at = time;
                self.empty_before = total;
            }
            self.empty_total += u64::from(empty);
            self.empty.push_back((timestamp, self.empty_total));
        }
        while self
            .timestamps
            .front()
            .is_some_and(|t| *t <= timestamp - WINDOW_MS)
        {
            self.timestamps.pop_front();
        }
        if counted {
            if self.timestamps.len() == 8192 {
                self.rate_lost_at = self.timestamps.pop_front().unwrap();
            }
            self.timestamps.push_back(timestamp);
            self.advance_peak(timestamp);
        }
    }
    fn rate(&self, now: f64) -> Result<f64, RateStatus> {
        if !now.is_finite() || !self.rate_eligible.is_finite() || now < self.rate_eligible {
            return Err(RateStatus::WarmingUp);
        }
        // Queries cannot recover windows preceding the newest accepted event.
        if now < self.latest {
            return Err(RateStatus::Delayed);
        }
        let start = self.timestamps.partition_point(|t| *t <= now - WINDOW_MS);
        let rate = (self.timestamps.len() - start) as f64 * (1000.0 / WINDOW_MS);
        if self.rate_lost_at > now - WINDOW_MS || rate > MAX_RATE {
            Err(RateStatus::OutOfRange)
        } else {
            Ok(rate)
        }
    }
    fn advance_peak(&mut self, now: f64) {
        let Ok(rate) = self.rate(now) else {
            self.pending_peak = None;
            return;
        };
        if !self.peak_eligible.is_finite() || now < self.peak_eligible {
            return;
        }
        let nearest = [125, 250, 500, 1000, 2000, 4000, 8000]
            .into_iter()
            .min_by(|a, b| {
                ((rate - *a as f64).abs() / *a as f64)
                    .total_cmp(&((rate - *b as f64).abs() / *b as f64))
            })
            .unwrap();
        let candidate = if (rate - nearest as f64).abs() / nearest as f64 <= 0.015 {
            nearest
        } else {
            rate.round_ties_even() as i32
        };
        if candidate <= self.peak {
            self.pending_peak = None;
            return;
        }
        match self.pending_peak {
            Some((started, minimum)) => {
                let minimum = minimum.min(candidate);
                if now - started >= PEAK_CONFIRM_MS {
                    self.peak = minimum;
                    self.pending_peak = None;
                } else {
                    self.pending_peak = Some((started, minimum));
                }
            }
            None => self.pending_peak = Some((now, candidate)),
        }
    }
    pub fn metrics(&self, now: f64, dropped_packets: u64) -> PollingMetrics {
        let rate = self.rate(now);
        let empty_packet_percent = if rate.is_ok()
            && self.mode == PollingMode::MotionReportRate
            && now >= self.empty_eligible
            && self.empty_lost_at <= now - 1000.0
        {
            let start = self.empty.partition_point(|p| p.0 <= now - 1000.0);
            let count = self.empty.len() - start;
            (count > 0).then(|| {
                let before = if start == 0 {
                    self.empty_before
                } else {
                    self.empty[start - 1].1
                };
                (self.empty_total - before) as f64 * 100.0 / count as f64
            })
        } else {
            None
        };
        PollingMetrics {
            current_rate: rate.ok().map(|v| v.round_ties_even() as i32),
            peak_rate: self.peak,
            empty_packet_percent,
            dropped_packets,
            status: rate.err().unwrap_or(RateStatus::Valid),
        }
    }
    pub fn logical_now(&self, now: f64) -> f64 {
        now - self.paused_total
            - if self.paused_at.is_finite() && now > self.paused_at {
                now - self.paused_at
            } else {
                0.0
            }
    }
    pub fn history_frame(&mut self, now: f64) -> (&VecDeque<HistoryPoint>, Option<f64>) {
        let rate = self.rate(now).ok();
        if let Some(rate) = rate
            && now - self.history_observed >= self.history_interval
            && !self.paused_at.is_finite()
        {
            let logical = self.logical_now(now);
            if self.history.back().is_none_or(|p| logical > p.logical_ms) {
                self.history_observed = now;
                if self.history.len() == 4096 {
                    self.history.pop_front();
                }
                self.history.push_back(HistoryPoint {
                    logical_ms: logical,
                    observed_ms: now,
                    rate,
                    segment: self.segment,
                });
                while self
                    .history
                    .front()
                    .is_some_and(|p| p.logical_ms < logical - 3000.0)
                {
                    self.history.pop_front();
                }
            }
        }
        (&self.history, rate)
    }
}
