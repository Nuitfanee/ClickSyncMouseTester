use super::Work;
use crate::{
    geometry::{Coordinate, Sample},
    input::{ChartParameters, Metric},
};

pub(super) fn alpha(delta_ms: f64, time_constant_ms: f64) -> f64 {
    if delta_ms <= 0.0 || time_constant_ms <= 0.0 {
        1.0
    } else {
        (1.0 - (-delta_ms / time_constant_ms).exp()).clamp(0.0, 1.0)
    }
}

pub(super) fn expand(
    times: impl Fn(usize) -> f64,
    range_start: usize,
    range_end: usize,
    center: usize,
    minimum: usize,
    mut left: usize,
    mut right: usize,
) -> (usize, usize) {
    while right - left + 1 < minimum && (left > range_start || right < range_end) {
        if left <= range_start {
            right += 1;
        } else if right >= range_end
            || (times(center) - times(left - 1)).abs() <= (times(right + 1) - times(center)).abs()
        {
            left -= 1;
        } else {
            right += 1;
        }
    }
    (left, right)
}

fn settings(parameters: &ChartParameters) -> (f64, usize) {
    let options = parameters.options;
    if parameters.metric.timing() {
        (
            options.timing_window_ms.max(options.trend_window_ms) / 2.0,
            options
                .timing_minimum_samples
                .max(options.minimum_trend_samples),
        )
    } else {
        (options.trend_window_ms / 2.0, options.minimum_trend_samples)
    }
}

pub(super) fn evaluate(
    raw: &[Sample],
    sums: &[f64],
    start: usize,
    previous: Option<Sample>,
    parameters: &ChartParameters,
    work: &mut Work,
) -> Vec<Sample> {
    if raw.is_empty() || (matches!(parameters.metric, Metric::CountTrend(_)) && raw.len() <= 1) {
        return Vec::new();
    }
    let (half, minimum) = settings(parameters);
    let times = |i: usize| raw[i].point.x.value();
    let monotonic = start > 0
        || raw
            .windows(2)
            .all(|w| w[0].point.x.value() <= w[1].point.x.value());
    let mut left_cursor = 0;
    let mut right_cursor = 0;
    let mut smoothed = previous.map(|p| p.point.y.value());
    let mut result = Vec::with_capacity(raw.len() - start);
    for i in start..raw.len() {
        let time = times(i);
        if monotonic {
            left_cursor = raw.partition_point(|p| time - p.point.x.value() > half);
            right_cursor = raw
                .partition_point(|p| p.point.x.value() - time <= half)
                .saturating_sub(1);
        } else {
            while left_cursor < raw.len() && time - times(left_cursor) > half {
                left_cursor += 1;
            }
            while right_cursor + 1 < raw.len() && times(right_cursor + 1) - time <= half {
                right_cursor += 1;
            }
        }
        let (left, right) = expand(
            times,
            0,
            raw.len() - 1,
            i,
            minimum,
            left_cursor.min(raw.len() - 1),
            right_cursor.max(left_cursor).min(raw.len() - 1),
        );
        work.derived_evaluations += 1;
        let count = right - left + 1;
        let value = match parameters.metric {
            Metric::CountTrend(_) => (sums[right + 1] - sums[left]) / count as f64,
            Metric::Frequency => 1000.0 * count as f64 / (sums[right + 1] - sums[left]),
            Metric::IntervalTrend => {
                let mut sorted: Vec<_> = raw[left..=right]
                    .iter()
                    .map(|s| s.point.y.value())
                    .collect();
                sorted.sort_by(f64::total_cmp);
                let trim = ((count as f64 * parameters.options.timing_trim_ratio).round() as usize)
                    .min((count - 1) / 2);
                sorted[trim..count - trim].iter().sum::<f64>() / (count - 2 * trim) as f64
            }
            _ => unreachable!(),
        };
        if parameters.metric.timing() && value <= 0.0 {
            continue;
        }
        let value = if parameters.metric.timing() {
            let current = match smoothed {
                None => value,
                Some(previous_value) => {
                    let delta = (time - times(i.saturating_sub(1))).max(0.0);
                    previous_value
                        + alpha(delta, parameters.options.timing_ema_time_constant_ms)
                            * (value - previous_value)
                }
            };
            smoothed = Some(current);
            current
        } else {
            value
        };
        let mut sample = raw[i];
        sample.point.y = Coordinate::Scalar(value);
        result.push(sample);
    }
    result
}

pub(super) fn stable_end(
    raw: &[Sample],
    start: usize,
    parameters: &ChartParameters,
    non_monotonic: bool,
) -> usize {
    if non_monotonic || raw.len() <= 1 {
        return 0;
    }
    let (half, minimum) = settings(parameters);
    let last_time = raw.last().unwrap().point.x.value();
    let mut stable = start;
    // Strict time closure handles duplicates; the minimum-sample guard prevents
    // future nearest-neighbour expansion from changing a previously short window.
    while stable < raw.len()
        && last_time > raw[stable].point.x.value() + half
        && raw.len() - stable > minimum
    {
        stable += 1;
    }
    stable
}
