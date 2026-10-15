//! Display units and ticks shared by chart axes, inspection and statistics.
use clicksync_chart::{
    ChartParameters, Coordinate, HistogramScale, Metric, analysis::histogram::HistogramBin,
    gpu::PlotKind,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Unit {
    Count,
    Milliseconds,
    Hertz,
    MetersPerSecond,
    Millimeters,
    Percent,
}

impl Unit {
    pub fn metric(metric: Metric) -> Self {
        match metric {
            Metric::Count(_)
            | Metric::CountTrend(_)
            | Metric::Cumulative(_)
            | Metric::DeltaMagnitude
            | Metric::Trajectory
            | Metric::PathLength => Self::Count,
            Metric::Interval | Metric::IntervalTrend => Self::Milliseconds,
            Metric::RawFrequency | Metric::Frequency => Self::Hertz,
            Metric::Velocity(_) | Metric::VelocityMagnitude | Metric::PathSpeed => {
                Self::MetersPerSecond
            }
        }
    }

    pub fn axes(parameters: &ChartParameters, kind: PlotKind) -> [Self; 2] {
        let value = Self::metric(parameters.metric);
        match kind {
            PlotKind::Trajectory => [Self::Count; 2],
            PlotKind::Histogram => [
                value,
                match parameters.histogram.map(|spec| spec.scale) {
                    Some(HistogramScale::Percent) => Self::Percent,
                    _ => Self::Count,
                },
            ],
            _ => [Self::Milliseconds, value],
        }
    }

    pub fn symbol(self) -> &'static str {
        match self {
            Self::Count => "count",
            Self::Milliseconds => "ms",
            Self::Hertz => "Hz",
            Self::MetersPerSecond => "m/s",
            Self::Millimeters => "mm",
            Self::Percent => "%",
        }
    }

    pub fn value(self, value: Coordinate) -> String {
        self.format(value, 6)
    }

    pub fn readout(self, value: Coordinate) -> String {
        let label = self.value(value);
        if self == Self::Count {
            return label;
        }
        let label = label.trim_end_matches('0').trim_end_matches('.');
        if label == "-0" {
            "0".into()
        } else {
            label.into()
        }
    }

    pub fn axis_label(self, value: Coordinate, span: f64) -> String {
        let digits = (3.0 - span.max(f64::MIN_POSITIVE).log10().floor()).clamp(0.0, 9.0) as usize;
        let label = self.format(value, digits);
        if self == Self::Count || !label.contains('.') {
            return label;
        }
        let label = label.trim_end_matches('0').trim_end_matches('.');
        if label == "-0" {
            "0".into()
        } else {
            label.into()
        }
    }

    fn format(self, value: Coordinate, digits: usize) -> String {
        if self == Self::Count {
            if let Coordinate::Integer(value) = value {
                return value.to_string();
            }
            let rounded = value.value().round_ties_even();
            return format!("{:.0}", if rounded == 0.0 { 0.0 } else { rounded });
        }
        format!("{:.*}", digits, value.value())
    }

    pub fn histogram_range(self, metric: Metric, bin: HistogramBin, last: bool) -> String {
        if matches!(metric, Metric::Count(_) | Metric::Cumulative(_)) {
            // Integer-centred bins may have half-count edges; show the included counts.
            let first = bin.minimum.ceil();
            let end = if last {
                bin.maximum.floor()
            } else {
                bin.maximum.ceil() - 1.0
            };
            return if first == end {
                self.value(Coordinate::Scalar(first))
            } else {
                format!(
                    "[{}, {}]",
                    self.value(Coordinate::Scalar(first)),
                    self.value(Coordinate::Scalar(end)),
                )
            };
        }
        format!(
            "[{}, {}{}",
            self.value(Coordinate::Scalar(bin.minimum)),
            self.value(Coordinate::Scalar(bin.maximum)),
            if last { "]" } else { ")" },
        )
    }

    pub fn ticks(self, origin: Coordinate, span: f64, pixels: f32) -> Vec<Tick> {
        self.spaced_ticks(origin, span, pixels, 88.0)
    }

    pub fn vertical_ticks(self, origin: Coordinate, span: f64, pixels: f32) -> Vec<Tick> {
        self.spaced_ticks(origin, span, pixels, 32.0)
    }

    fn spaced_ticks(self, origin: Coordinate, span: f64, pixels: f32, spacing: f64) -> Vec<Tick> {
        if !span.is_finite() || span <= 0.0 || !origin.value().is_finite() {
            return Vec::new();
        }
        let desired =
            (span / (f64::from(pixels) / spacing).clamp(2.0, 12.0)).max(if self == Self::Count {
                1.0
            } else {
                f64::MIN_POSITIVE
            });
        let power = 10.0_f64.powf(desired.log10().floor());
        let step = [1.0, 2.0, 2.5, 5.0, 10.0]
            .into_iter()
            .map(|multiple| multiple * power)
            .filter(|step| {
                (self != Self::Count || step.fract() == 0.0)
                    && if spacing < 50.0 {
                        *step / span * f64::from(pixels) >= 20.0
                    } else {
                        *step >= desired
                    }
            })
            .min_by(|a, b| {
                (a / desired)
                    .ln()
                    .abs()
                    .total_cmp(&(b / desired).ln().abs())
            })
            .unwrap_or(10.0 * power);
        let divisions = if self == Self::Count && (step / 5.0).fract() != 0.0 {
            if (step / 2.0).fract() == 0.0 { 2 } else { 1 }
        } else {
            5
        };
        let minor = step / f64::from(divisions);
        if !step.is_finite() || !minor.is_finite() || minor <= 0.0 {
            return Vec::new();
        }
        let first = if self == Self::Count {
            let base = match origin {
                Coordinate::Integer(value) => i128::from(value),
                _ => origin.value().ceil() as i128,
            };
            let stride = minor as i128;
            base.div_euclid(stride) + i128::from(base.rem_euclid(stride) != 0)
        } else {
            (origin.value() / minor).ceil() as i128
        };
        (0..=80)
            .filter_map(|i| {
                let index = first.checked_add(i128::from(i))?;
                let (value, major) = if self == Self::Count {
                    let integer = index.checked_mul(minor as i128)?;
                    (
                        Coordinate::Integer(i64::try_from(integer).ok()?),
                        integer.rem_euclid(step as i128) == 0,
                    )
                } else {
                    (
                        Coordinate::Scalar(index as f64 * minor),
                        index.rem_euclid(i128::from(divisions)) == 0,
                    )
                };
                let fraction = value.relative_to(origin) / span;
                (0.0..=1.0).contains(&fraction).then_some(Tick {
                    fraction,
                    value,
                    major,
                })
            })
            .collect()
    }
}

pub struct Tick {
    pub fraction: f64,
    pub value: Coordinate,
    pub major: bool,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compact_axes_keep_useful_density_and_submillisecond_precision() {
        let ticks = Unit::Count.vertical_ticks(Coordinate::Scalar(-11.0), 22.0, 220.0);
        assert!(ticks.iter().filter(|tick| tick.major).count() >= 5);
        assert_eq!(
            Unit::Milliseconds.axis_label(Coordinate::Scalar(0.125), 0.5),
            "0.125"
        );
        assert_eq!(
            Unit::MetersPerSecond.axis_label(Coordinate::Scalar(-0.0), 1.0),
            "0"
        );
        let ticks = Unit::Count.vertical_ticks(Coordinate::Scalar(-13.0), 26.0, 210.0);
        assert!(
            ticks
                .iter()
                .filter(|tick| tick.major)
                .any(|tick| tick.value.value() == 5.0)
        );
        let ticks = Unit::MetersPerSecond.vertical_ticks(Coordinate::Scalar(-0.28), 0.56, 170.0);
        assert!(ticks.iter().filter(|tick| tick.major).count() >= 5);
    }

    #[test]
    fn count_ticks_are_integers_without_duplicate_labels_at_any_zoom() {
        for span in [0.01, 0.5, 2.0, 9.0, 40.0, 2000.0] {
            for origin in [
                Coordinate::Scalar(-2.7),
                Coordinate::Integer((1_i64 << 54) + 3),
            ] {
                let ticks = Unit::Count.ticks(origin, span, 800.0);
                let labels: Vec<_> = ticks
                    .iter()
                    .map(|t| Unit::Count.axis_label(t.value, span))
                    .collect();
                assert!(labels.iter().all(|label| !label.contains('.')));
                assert!(labels.windows(2).all(|pair| pair[0] != pair[1]));
                assert!(
                    ticks
                        .iter()
                        .all(|tick| matches!(tick.value, Coordinate::Integer(_)))
                );
            }
        }
        assert_eq!(
            Unit::Count.value(Coordinate::Integer((1_i64 << 54) + 3)),
            "18014398509481987"
        );
        assert_eq!(Unit::Count.value(Coordinate::Scalar(-0.25)), "0");
        assert_eq!(Unit::Count.value(Coordinate::Scalar(3.75)), "4");
        assert_eq!(
            Unit::Milliseconds.value(Coordinate::Scalar(0.125)),
            "0.125000"
        );
        assert_eq!(Unit::metric(Metric::RawFrequency).symbol(), "Hz");
        assert_eq!(Unit::metric(Metric::VelocityMagnitude).symbol(), "m/s");
    }

    #[test]
    fn histogram_counts_and_percent_keep_separate_units_and_integer_membership() {
        let bin = HistogramBin {
            minimum: -0.5,
            maximum: 0.5,
            count: 1,
            value: 25.0,
        };
        assert_eq!(
            Unit::Count.histogram_range(Metric::Count(clicksync_chart::Axis::X), bin, false),
            "0"
        );
        let mut parameters = ChartParameters::new(Metric::Count(clicksync_chart::Axis::X));
        parameters.histogram = Some(clicksync_chart::HistogramSpec {
            policy: clicksync_chart::HistogramPolicy::Automatic,
            scale: HistogramScale::Percent,
        });
        assert_eq!(
            Unit::axes(&parameters, PlotKind::Histogram),
            [Unit::Count, Unit::Percent]
        );
        assert_eq!(Unit::Percent.value(Coordinate::Scalar(12.5)), "12.500000");
    }
}
