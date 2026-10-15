//! Semantic summaries of a fixed chart input, never maintained by the capture path.
use super::{cumulative, delta, histogram::HistogramBin, statistics};
use crate::{Axis, ChartEngine, FixedPrefix, Metric};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StatisticKind {
    Samples,
    Mean,
    StandardDeviation,
    P05,
    P95,
    P99,
    Maximum,
    MeanAbsolute,
    PeakAbsolute,
    AbsoluteTravel,
    Span,
    PathLength,
    Width,
    Height,
    ModalRange,
    ModalShare,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StatisticUnit {
    Number,
    Counts,
    Milliseconds,
    Hertz,
    MetersPerSecond,
    Percent,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum StatisticValue {
    Integer(i128),
    Scalar(f64),
}

#[derive(Clone, Debug, PartialEq)]
pub struct Statistic {
    pub kind: StatisticKind,
    pub axis: Option<Axis>,
    pub unit: StatisticUnit,
    pub value: Option<StatisticValue>,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct DetailStatistics {
    pub sample_count: usize,
    pub rows: Vec<Statistic>,
}

impl DetailStatistics {
    /// Engines retain full-prefix window/filter context; only the summary is range-filtered.
    pub fn compute(
        prefix: FixedPrefix<'_>,
        primary: &ChartEngine,
        secondary: Option<&ChartEngine>,
        range: Option<(f64, f64)>,
    ) -> Self {
        let parameters = primary.parameters();
        let selected = |index| {
            range.is_none_or(|(start, end)| {
                prefix.event_at(index).is_some_and(|event| {
                    let time = parameters.time_basis.ticks(event) as f64 * 1000.0
                        / prefix.version().timestamp_frequency as f64;
                    time >= start && time <= end
                })
            })
        };
        let mut result = if parameters.histogram.is_none()
            && matches!(
                parameters.metric,
                Metric::Trajectory | Metric::PathLength | Metric::Cumulative(_)
            ) {
            Self::motion(prefix, primary, secondary.is_some(), selected)
        } else {
            let mut result = Self::default();
            for engine in std::iter::once(primary).chain(secondary) {
                result.scalar(engine, secondary.is_some(), &selected);
            }
            result
        };
        result.rows.insert(
            0,
            Statistic {
                kind: StatisticKind::Samples,
                axis: None,
                unit: StatisticUnit::Number,
                value: Some(StatisticValue::Integer(result.sample_count as i128)),
            },
        );
        result
    }

    fn push(
        &mut self,
        kind: StatisticKind,
        axis: Option<Axis>,
        unit: StatisticUnit,
        value: impl Into<Option<StatisticValue>>,
    ) {
        self.rows.push(Statistic {
            kind,
            axis,
            unit,
            value: value.into(),
        });
    }

    fn number(&mut self, kind: StatisticKind, axis: Option<Axis>, unit: StatisticUnit, value: f64) {
        self.push(
            kind,
            axis,
            unit,
            value.is_finite().then_some(StatisticValue::Scalar(value)),
        );
    }

    fn scalar(&mut self, engine: &ChartEngine, paired: bool, selected: &impl Fn(usize) -> bool) {
        use StatisticKind as K;
        use StatisticUnit as U;
        let parameters = engine.parameters();
        let metric = parameters.metric;
        let histogram = parameters.histogram.is_some();
        let axis = match (paired, metric) {
            (true, Metric::Count(axis) | Metric::Velocity(axis)) => Some(axis),
            _ => None,
        };
        let unit = match metric {
            Metric::Interval | Metric::IntervalTrend => U::Milliseconds,
            Metric::RawFrequency | Metric::Frequency => U::Hertz,
            Metric::Velocity(_) | Metric::VelocityMagnitude | Metric::PathSpeed => {
                U::MetersPerSecond
            }
            _ => U::Counts,
        };
        let amplitude = !histogram && matches!(metric, Metric::Count(_) | Metric::Velocity(_));
        let quantile = if histogram {
            None
        } else {
            match metric {
                Metric::Interval => Some((K::P99, 0.99)),
                Metric::RawFrequency | Metric::Frequency => Some((K::P05, 0.05)),
                Metric::DeltaMagnitude | Metric::VelocityMagnitude | Metric::PathSpeed => {
                    Some((K::P95, 0.95))
                }
                _ => None,
            }
        };
        let values = || {
            engine
                .geometry()
                .samples()
                .filter(|sample| selected(sample.source_index))
                .map(|sample| {
                    let value = sample.point.y.value();
                    if histogram {
                        metric.histogram_value(value)
                    } else {
                        value
                    }
                })
                .filter(|value| value.is_finite())
        };
        let mut count = 0;
        let mut sum = 0.0;
        let mut peak = 0.0_f64;
        // Only charts displaying a quantile need history-sized scratch or sorting.
        let mut sorted = Vec::new();
        for value in values() {
            count += 1;
            if amplitude {
                sum += value.abs();
                peak = peak.max(value.abs());
            } else {
                sum += value;
            }
            if quantile.is_some() {
                sorted.push(value);
            }
        }
        self.sample_count = self.sample_count.max(count);
        if count == 0 {
            return;
        }
        let mean = sum / count as f64;
        if amplitude {
            self.number(K::MeanAbsolute, axis, unit, mean);
            self.number(K::PeakAbsolute, axis, unit, peak);
            return;
        }
        self.number(K::Mean, axis, unit, mean);
        if histogram
            || matches!(
                metric,
                Metric::CountTrend(_)
                    | Metric::Interval
                    | Metric::IntervalTrend
                    | Metric::RawFrequency
                    | Metric::Frequency
            )
        {
            let variance = values().map(|value| (value - mean).powi(2)).sum::<f64>() / count as f64;
            self.number(K::StandardDeviation, axis, unit, variance.sqrt());
        }
        if let Some((kind, p)) = quantile {
            sorted.sort_unstable_by(f64::total_cmp);
            self.number(kind, axis, unit, statistics::percentile(&sorted, p));
            if !matches!(metric, Metric::RawFrequency | Metric::Frequency) {
                self.number(K::Maximum, axis, unit, sorted[sorted.len() - 1]);
            }
        }
    }

    fn motion(
        prefix: FixedPrefix<'_>,
        engine: &ChartEngine,
        paired: bool,
        selected: impl Fn(usize) -> bool,
    ) -> Self {
        use StatisticKind as K;
        use StatisticUnit as U;
        let mut result = Self::default();
        let metric = engine.parameters().metric;
        let mut absolute = [0_i128; 2];
        let mut minimum = [i64::MAX; 2];
        let mut maximum = [i64::MIN; 2];
        let mut path = 0.0;
        for index in 0..prefix.len() {
            if !selected(index) {
                continue;
            }
            let event = prefix.event_at(index).expect("fixed prefix");
            result.sample_count += 1;
            if metric != Metric::PathLength {
                for (i, axis) in [Axis::X, Axis::Y].into_iter().enumerate() {
                    if matches!(metric, Metric::Cumulative(_)) {
                        absolute[i] += i128::from(delta(event, axis)).abs();
                    }
                    minimum[i] = minimum[i].min(cumulative(event, axis));
                    maximum[i] = maximum[i].max(cumulative(event, axis));
                }
            }
            if !matches!(metric, Metric::Cumulative(_)) {
                let dx = f64::from(event.delta_x);
                let dy = f64::from(event.delta_y);
                path += (dx * dx + dy * dy).sqrt();
            }
        }
        if result.sample_count == 0 {
            return result;
        }
        if let Metric::Cumulative(axis) = metric {
            for axis in [Some(axis), paired.then_some(Axis::Y)]
                .into_iter()
                .flatten()
            {
                let i = usize::from(axis == Axis::Y);
                for (kind, value) in [
                    (K::Span, i128::from(maximum[i]) - i128::from(minimum[i])),
                    (K::AbsoluteTravel, absolute[i]),
                ] {
                    result.push(kind, Some(axis), U::Counts, StatisticValue::Integer(value));
                }
            }
        } else {
            result.number(K::PathLength, None, U::Counts, path);
            if metric == Metric::Trajectory {
                for (kind, i) in [(K::Width, 0), (K::Height, 1)] {
                    result.push(
                        kind,
                        None,
                        U::Counts,
                        StatisticValue::Integer(i128::from(maximum[i]) - i128::from(minimum[i])),
                    );
                }
            }
        }
        result
    }
}

/// Computed after range filtering and shared comparison-bin layout, not from plotted bar heights.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct HistogramStatistics {
    pub modal_bin: HistogramBin,
    pub last: bool,
    pub modal_share: f64,
}

impl HistogramStatistics {
    pub fn compute(bins: &[HistogramBin]) -> Option<Self> {
        let count: usize = bins.iter().map(|bin| bin.count).sum();
        let modal = bins
            .iter()
            .reduce(|a, b| if b.count > a.count { b } else { a })?;
        (count > 0).then(|| Self {
            modal_bin: *modal,
            last: modal.maximum == bins.last().expect("nonempty bins").maximum,
            modal_share: modal.count as f64 * 100.0 / count as f64,
        })
    }
}
