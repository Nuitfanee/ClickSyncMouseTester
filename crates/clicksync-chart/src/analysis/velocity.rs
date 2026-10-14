use super::{Work, cumulative, window};
use crate::{
    geometry::{Coordinate, Point, Sample},
    input::{Axis, ChartParameters, FixedPrefix, Metric},
};
use std::{cell::Cell, ops::Range};

#[derive(Default)]
pub(super) struct VelocityCache {
    processed_end: usize,
    raw_revisable_from: usize,
    primary: Vec<Sample>,
    secondary: Vec<Sample>,
    primary_filter: FilterCache,
    secondary_filter: FilterCache,
    pub non_monotonic: bool,
}

impl VelocityCache {
    pub(super) fn cached_value_bytes(&self) -> usize {
        (self.primary.capacity() + self.secondary.capacity()) * std::mem::size_of::<Sample>()
            + [&self.primary_filter, &self.secondary_filter]
                .into_iter()
                .map(|filter| {
                    (filter.filtered.capacity()
                        + filter.forward.capacity()
                        + filter.backward.capacity())
                        * std::mem::size_of::<f64>()
                })
                .sum::<usize>()
    }

    pub fn update(
        &mut self,
        prefix: FixedPrefix<'_>,
        range: Range<usize>,
        path_prefix: &[f64],
        parameters: &ChartParameters,
        work: &mut Work,
    ) -> Option<(usize, Vec<Sample>)> {
        if self.processed_end == range.end {
            return None;
        }
        let basis = parameters.time_basis;
        let frequency = prefix.version().timestamp_frequency;
        let reads = Cell::new(0);
        let event = |i| {
            reads.set(reads.get() + 1);
            prefix.event_at(i).expect("core fixed prefix")
        };
        let time = |i| basis.ticks(event(i)) as f64 * 1000.0 / frequency as f64;
        for i in self.processed_end.saturating_sub(1).max(range.start)..range.end.saturating_sub(1)
        {
            if time(i) > time(i + 1) {
                self.non_monotonic = true;
            }
        }
        let source_from = if self.non_monotonic {
            range.start
        } else {
            self.raw_revisable_from.max(range.start)
        };
        let changed = self
            .primary
            .partition_point(|s| s.source_index < source_from);
        let old_count = self.primary.len();
        self.primary.truncate(changed);
        self.secondary.truncate(changed);
        let (primary, secondary) = raw_estimates(
            prefix,
            range.clone(),
            source_from,
            path_prefix,
            parameters,
            work,
            self.non_monotonic,
        );
        self.primary.extend(primary);
        self.secondary.extend(secondary);
        let time_constant = parameters.options.trend_window_ms.max(8.0);
        let mut first = self.primary_filter.update(
            &self.primary,
            changed,
            old_count,
            time_constant,
            parameters.metric == Metric::PathSpeed,
            work,
        );
        if parameters.metric == Metric::VelocityMagnitude {
            first = first.min(self.secondary_filter.update(
                &self.secondary,
                changed,
                old_count,
                time_constant,
                false,
                work,
            ));
        }
        let from = if first < changed {
            self.primary[first].source_index
        } else {
            source_from
        };
        let replacement = self.primary[first..]
            .iter()
            .enumerate()
            .map(|(offset, &sample)| {
                let i = first + offset;
                let mut sample = sample;
                let x = self
                    .primary_filter
                    .value(i, parameters.metric == Metric::PathSpeed);
                sample.point.y =
                    Coordinate::Scalar(if parameters.metric == Metric::VelocityMagnitude {
                        let y = self.secondary_filter.value(i, false);
                        (x * x + y * y).sqrt()
                    } else {
                        x
                    });
                sample
            })
            .collect();
        self.processed_end = range.end;
        self.raw_revisable_from = source_from;
        if !self.non_monotonic && !range.is_empty() {
            let last_time = time(range.end - 1);
            let half = parameters.options.trend_window_ms / 2.0;
            while self.raw_revisable_from < range.end
                && last_time > time(self.raw_revisable_from) + half
                && range.end - self.raw_revisable_from > parameters.options.minimum_trend_samples
            {
                self.raw_revisable_from += 1;
            }
        }
        work.context_record_reads += reads.get();
        Some((from, replacement))
    }
}

fn raw_estimates(
    prefix: FixedPrefix<'_>,
    range: Range<usize>,
    from: usize,
    path_prefix: &[f64],
    parameters: &ChartParameters,
    work: &mut Work,
    non_monotonic: bool,
) -> (Vec<Sample>, Vec<Sample>) {
    let Some(cpi) = parameters.cpi.filter(|cpi| *cpi > 0.0) else {
        return (Vec::new(), Vec::new());
    };
    let basis = parameters.time_basis;
    let frequency = prefix.version().timestamp_frequency;
    let reads = Cell::new(0);
    let event = |i| {
        reads.set(reads.get() + 1);
        prefix.event_at(i).expect("core fixed prefix")
    };
    let time = |i| basis.ticks(event(i)) as f64 * 1000.0 / frequency as f64;
    let half = parameters.options.trend_window_ms / 2.0;
    let mut left_cursor = range.start;
    let mut right_cursor = range.start;
    let mut primary = Vec::new();
    let mut secondary = Vec::new();
    for i in from..range.end {
        work.derived_evaluations += 1;
        if non_monotonic {
            while left_cursor < range.end - 1 && time(i) - time(left_cursor) > half {
                left_cursor += 1;
            }
            right_cursor = right_cursor.max(left_cursor.saturating_sub(1));
            while right_cursor + 1 < range.end && time(right_cursor + 1) - time(i) <= half {
                right_cursor += 1;
            }
        } else {
            left_cursor = lower_bound(range.clone(), |j| time(i) - time(j) > half);
            right_cursor = lower_bound(range.clone(), |j| time(j) - time(i) <= half)
                .saturating_sub(1)
                .max(range.start);
        }
        let (left, right) = window::expand(
            time,
            range.start,
            range.end - 1,
            i,
            parameters.options.minimum_trend_samples,
            left_cursor.min(range.end - 1),
            right_cursor.max(left_cursor).min(range.end - 1),
        );
        let Some(interval) = basis.interval(event(left), event(right), frequency) else {
            continue;
        };
        let resolve_axis = |axis| {
            (cumulative(event(right), axis) - cumulative(event(left), axis)) as f64 / interval
                * (25.4 / cpi)
        };
        let x = match parameters.metric {
            Metric::Velocity(axis) => resolve_axis(axis),
            Metric::VelocityMagnitude => resolve_axis(Axis::X),
            Metric::PathSpeed => {
                (path_prefix[right + 1] - path_prefix[left + 1]).max(0.0) / interval * (25.4 / cpi)
            }
            _ => unreachable!(),
        };
        let y = if parameters.metric == Metric::VelocityMagnitude {
            resolve_axis(Axis::Y)
        } else {
            0.0
        };
        if x.is_finite() && y.is_finite() {
            let current = event(i);
            let sample = Sample {
                source_index: i,
                segment_id: current.session_segment_id,
                point: Point {
                    x: Coordinate::Time {
                        ticks: basis.ticks(current),
                        frequency,
                    },
                    y: Coordinate::Scalar(x),
                },
            };
            primary.push(sample);
            if parameters.metric == Metric::VelocityMagnitude {
                let mut second = sample;
                second.point.y = Coordinate::Scalar(y);
                secondary.push(second);
            }
        }
    }
    work.context_record_reads += reads.get();
    (primary, secondary)
}

fn lower_bound(range: Range<usize>, predicate: impl Fn(usize) -> bool) -> usize {
    let mut low = range.start;
    let mut high = range.end;
    while low < high {
        let middle = low + (high - low) / 2;
        if predicate(middle) {
            low = middle + 1;
        } else {
            high = middle;
        }
    }
    low
}

#[derive(Default)]
struct FilterCache {
    filtered: Vec<f64>,
    forward: Vec<f64>,
    backward: Vec<f64>,
}

impl FilterCache {
    fn update(
        &mut self,
        raw: &[Sample],
        changed: usize,
        old_count: usize,
        time_constant: f64,
        non_negative: bool,
        work: &mut Work,
    ) -> usize {
        let count = raw.len();
        self.filtered.resize(count, 0.0);
        self.forward.resize(count, 0.0);
        self.backward.resize(count, 0.0);
        if count == 0 {
            return 0;
        }
        let from = if (old_count < 5) != (count < 5) {
            0
        } else if count >= 5 {
            changed.saturating_sub(2)
        } else {
            changed
        };
        for (i, sample) in raw.iter().enumerate().skip(from) {
            self.filtered[i] = coerce(sample.point.y.value(), non_negative);
        }
        if count >= 5 {
            // Restore the revisable suffix to raw values before applying the
            // original in-place, left-to-right five-point MAD filter.
            for i in from..count {
                work.smoothing_evaluations += 1;
                let start = i.saturating_sub(2);
                let end = (i + 3).min(count);
                let mut values = [0.0; 5];
                let values = &mut values[..end - start];
                values.copy_from_slice(&self.filtered[start..end]);
                values.sort_by(f64::total_cmp);
                let median_value = median(values);
                let mut deviations = [0.0; 5];
                let deviations = &mut deviations[..end - start];
                for (to, value) in deviations.iter_mut().zip(&self.filtered[start..end]) {
                    *to = (value - median_value).abs();
                }
                deviations.sort_by(f64::total_cmp);
                let scaled_mad = median(deviations) * 1.4826;
                let residual = self.filtered[i] - median_value;
                let limit = (scaled_mad * 6.0).max(median_value.abs() * 4.0).max(4.0);
                if residual.abs() > limit {
                    self.filtered[i] =
                        coerce(median_value + residual.signum() * limit, non_negative);
                }
            }
        }
        if from == 0 {
            self.forward[0] = self.filtered[0];
        }
        for i in from.max(1)..count {
            work.smoothing_evaluations += 1;
            let delta = (raw[i].point.x.value() - raw[i - 1].point.x.value()).max(0.0);
            self.forward[i] = self.forward[i - 1]
                + window::alpha(delta, time_constant) * (self.filtered[i] - self.forward[i - 1]);
        }
        self.backward[count - 1] = self.filtered[count - 1];
        let mut first = from;
        for i in (0..count - 1).rev() {
            work.smoothing_evaluations += 1;
            let delta = (raw[i + 1].point.x.value() - raw[i].point.x.value()).max(0.0);
            let value = self.backward[i + 1]
                + window::alpha(delta, time_constant) * (self.filtered[i] - self.backward[i + 1]);
            let identical = value.to_bits() == self.backward[i].to_bits();
            self.backward[i] = value;
            // No epsilon or fixed horizon: an identical recurrence state with
            // unchanged preceding inputs proves all earlier values identical.
            if i < from && identical {
                break;
            }
            if !identical {
                first = first.min(i);
            }
        }
        first
    }

    fn value(&self, index: usize, non_negative: bool) -> f64 {
        coerce(
            (self.forward[index] + self.backward[index]) / 2.0,
            non_negative,
        )
    }
}

fn coerce(value: f64, non_negative: bool) -> f64 {
    if !value.is_finite() {
        0.0
    } else if non_negative {
        value.max(0.0)
    } else {
        value
    }
}

fn median(sorted: &[f64]) -> f64 {
    let middle = sorted.len() / 2;
    if sorted.len().is_multiple_of(2) {
        (sorted[middle - 1] + sorted[middle]) / 2.0
    } else {
        sorted[middle]
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn warmed_mad_and_bidirectional_filter_allocate_nothing() {
        let raw: Vec<_> = (0..256)
            .map(|index| Sample {
                source_index: index,
                segment_id: 1,
                point: Point {
                    x: Coordinate::Scalar(index as f64 * 0.125),
                    y: Coordinate::Scalar(if index % 13 == 0 {
                        800.0
                    } else {
                        index as f64 % 7.0
                    }),
                },
            })
            .collect();
        let mut filter = FilterCache::default();
        let mut work = Work::default();
        filter.update(&raw, 0, 0, 8.0, false, &mut work);
        let allocations = allocation_counter::measure(|| {
            filter.update(&raw, 248, raw.len(), 8.0, false, &mut work);
        });
        assert_eq!(allocations.count_total, 0);
        assert_eq!(allocations.bytes_total, 0);
    }
}
