use clicksync_core::{SessionEvent, SessionSnapshot, SessionStatus};
use std::{fmt, ops::Range};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DatasetId(pub u64);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Integrity {
    Complete,
    Incomplete,
    Unknown,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DataVersion {
    pub dataset: DatasetId,
    pub generation: u64,
    pub session_revision: u64,
    pub end: usize,
    pub timestamp_frequency: u64,
    pub status: SessionStatus,
    pub source_finalized: bool,
    pub integrity: Integrity,
}

/// Borrows core's immutable directory and bounded active-tail copy.
#[derive(Clone, Copy)]
pub struct FixedPrefix<'a> {
    pub(crate) snapshot: &'a SessionSnapshot,
    pub(crate) version: DataVersion,
}

impl<'a> FixedPrefix<'a> {
    pub fn new(dataset: DatasetId, snapshot: &'a SessionSnapshot) -> Self {
        Self {
            snapshot,
            version: DataVersion {
                dataset,
                generation: snapshot.generation(),
                session_revision: snapshot.session_revision(),
                end: snapshot.event_count(),
                timestamp_frequency: snapshot.timestamp_frequency(),
                status: snapshot.status(),
                source_finalized: snapshot.finalized(),
                integrity: match snapshot.integrity() {
                    clicksync_core::session::CaptureIntegrity::Complete => Integrity::Complete,
                    clicksync_core::session::CaptureIntegrity::Incomplete => Integrity::Incomplete,
                    clicksync_core::session::CaptureIntegrity::Unknown => Integrity::Unknown,
                },
            },
        }
    }

    pub fn with_end(mut self, end: usize) -> Result<Self, ChartError> {
        if end > self.snapshot.event_count() {
            return Err(ChartError::InvalidRange);
        }
        self.version.end = end;
        Ok(self)
    }

    pub fn with_integrity(mut self, integrity: Integrity) -> Self {
        self.version.integrity = match (self.version.integrity, integrity) {
            (Integrity::Incomplete, _) | (_, Integrity::Incomplete) => Integrity::Incomplete,
            (Integrity::Unknown, _) | (_, Integrity::Unknown) => Integrity::Unknown,
            _ => Integrity::Complete,
        };
        self
    }

    pub fn version(self) -> DataVersion {
        self.version
    }

    pub fn len(self) -> usize {
        self.version.end
    }

    pub fn is_empty(self) -> bool {
        self.len() == 0
    }

    pub fn event_at(self, index: usize) -> Option<SessionEvent> {
        (index < self.len())
            .then(|| self.snapshot.event_at(index))
            .flatten()
    }

    pub fn events(
        self,
        range: Range<usize>,
    ) -> Result<impl Iterator<Item = SessionEvent>, ChartError> {
        if range.start > range.end || range.end > self.len() {
            return Err(ChartError::InvalidRange);
        }
        Ok(range.map(move |i| self.event_at(i).expect("core fixed prefix")))
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Axis {
    X,
    Y,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TimeBasis {
    RawCapture,
    Logical,
}

impl TimeBasis {
    pub fn ticks(self, event: SessionEvent) -> u64 {
        match self {
            Self::RawCapture => event.raw_relative_ticks,
            Self::Logical => event.logical_ticks,
        }
    }

    pub(crate) fn interval(
        self,
        previous: SessionEvent,
        current: SessionEvent,
        frequency: u64,
    ) -> Option<f64> {
        if self == Self::RawCapture && previous.session_segment_id != current.session_segment_id {
            return None;
        }
        let ticks = self.ticks(current).checked_sub(self.ticks(previous))?;
        (ticks > 0).then(|| ticks as f64 * 1000.0 / frequency as f64)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Metric {
    Count(Axis),
    DeltaMagnitude,
    CountTrend(Axis),
    Interval,
    RawFrequency,
    IntervalTrend,
    Frequency,
    Cumulative(Axis),
    Trajectory,
    PathLength,
    Velocity(Axis),
    VelocityMagnitude,
    PathSpeed,
}

impl Metric {
    pub(crate) fn velocity(self) -> bool {
        matches!(
            self,
            Self::Velocity(_) | Self::VelocityMagnitude | Self::PathSpeed
        )
    }

    pub(crate) fn window(self) -> bool {
        matches!(
            self,
            Self::CountTrend(_) | Self::IntervalTrend | Self::Frequency
        )
    }

    pub(crate) fn timing(self) -> bool {
        matches!(
            self,
            Self::Interval | Self::RawFrequency | Self::IntervalTrend | Self::Frequency
        )
    }

    pub fn histogram_value(self, value: f64) -> f64 {
        if matches!(self, Self::Velocity(_)) {
            value.abs()
        } else {
            value
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct AnalysisOptions {
    pub trend_window_ms: f64,
    pub minimum_trend_samples: usize,
    pub timing_window_ms: f64,
    pub timing_minimum_samples: usize,
    pub timing_trim_ratio: f64,
    pub timing_ema_time_constant_ms: f64,
}

impl Default for AnalysisOptions {
    fn default() -> Self {
        Self::new(12.0, 3, 75.0, 7, 0.1, 60.0)
    }
}

impl AnalysisOptions {
    pub fn new(
        trend_window_ms: f64,
        minimum_trend_samples: usize,
        timing_window_ms: f64,
        timing_minimum_samples: usize,
        timing_trim_ratio: f64,
        timing_ema_time_constant_ms: f64,
    ) -> Self {
        fn positive(value: f64, fallback: f64) -> f64 {
            if value.is_finite() && value > 0.0 {
                value
            } else {
                fallback
            }
        }
        Self {
            trend_window_ms: if trend_window_ms.is_finite() {
                trend_window_ms.max(1.0)
            } else {
                12.0
            },
            minimum_trend_samples: minimum_trend_samples.max(3),
            timing_window_ms: positive(timing_window_ms, 75.0),
            timing_minimum_samples: timing_minimum_samples.max(3),
            timing_trim_ratio: if timing_trim_ratio.is_finite() {
                timing_trim_ratio.clamp(0.0, 0.49)
            } else {
                0.1
            },
            timing_ema_time_constant_ms: positive(timing_ema_time_constant_ms, 60.0),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct HistogramLayout {
    pub minimum: f64,
    pub maximum: f64,
    pub width: f64,
    pub bins: usize,
}

impl HistogramLayout {
    pub fn is_valid(self) -> bool {
        self.minimum.is_finite()
            && self.maximum.is_finite()
            && self.width.is_finite()
            && self.bins > 0
            && self.width > 0.0
            && self.maximum > self.minimum
    }

    pub(crate) fn index(self, value: f64) -> usize {
        let index = ((value - self.minimum) / self.width).floor();
        if index <= 0.0 {
            0
        } else if index >= self.bins as f64 {
            self.bins - 1
        } else {
            index as usize
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum HistogramPolicy {
    Fixed(HistogramLayout),
    Automatic,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HistogramScale {
    Count,
    Percent,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct HistogramSpec {
    pub policy: HistogramPolicy,
    pub scale: HistogramScale,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ChartParameters {
    pub metric: Metric,
    pub time_basis: TimeBasis,
    pub options: AnalysisOptions,
    pub cpi: Option<f64>,
    pub parameter_version: u64,
    pub histogram: Option<HistogramSpec>,
}

impl ChartParameters {
    pub fn new(metric: Metric) -> Self {
        Self {
            metric,
            time_basis: TimeBasis::RawCapture,
            options: AnalysisOptions::default(),
            cpi: None,
            parameter_version: 0,
            histogram: None,
        }
    }

    pub(crate) fn validate(&self) -> Result<(), ChartError> {
        let o = self.options;
        if o != AnalysisOptions::new(
            o.trend_window_ms,
            o.minimum_trend_samples,
            o.timing_window_ms,
            o.timing_minimum_samples,
            o.timing_trim_ratio,
            o.timing_ema_time_constant_ms,
        ) {
            return Err(ChartError::InvalidParameters);
        }
        if let Some(spec) = self.histogram {
            if self.metric == Metric::Trajectory {
                return Err(ChartError::InvalidParameters);
            }
            if let HistogramPolicy::Fixed(layout) = spec.policy
                && !layout.is_valid()
            {
                return Err(ChartError::InvalidParameters);
            }
        }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ChartError {
    InvalidRange,
    InvalidParameters,
    WrongDataset,
    WrongGeneration,
    RegressedPrefix,
    ChangedPrefix,
    Finished,
}

impl fmt::Display for ChartError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}

impl std::error::Error for ChartError {}
