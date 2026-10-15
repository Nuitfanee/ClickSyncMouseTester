//! Fixed-version distribution summaries with the product's population variance/percentile rules.
use serde::Serialize;

#[derive(Clone, Debug, Serialize)]
pub struct Distribution {
    pub sample_count: usize,
    pub average: f64,
    pub standard_deviation: f64,
    pub p50: f64,
    pub p95: f64,
    pub p99: f64,
    pub p999: Option<f64>,
    pub mad: f64,
    pub iqr: f64,
}
pub fn distribution(values: impl Iterator<Item = f64>) -> Option<Distribution> {
    distribution_with_dispersion(values, true)
}
pub fn distribution_with_dispersion(
    values: impl Iterator<Item = f64>,
    include_dispersion: bool,
) -> Option<Distribution> {
    let mut sorted: Vec<_> = values.filter(|v| v.is_finite()).collect();
    if sorted.is_empty() {
        return None;
    }
    sorted.sort_by(f64::total_cmp);
    Some(sorted_distribution(&sorted, include_dispersion))
}

pub(super) fn sorted_distribution(sorted: &[f64], include_dispersion: bool) -> Distribution {
    let average = sorted.iter().sum::<f64>() / sorted.len() as f64;
    let standard_deviation = (sorted
        .iter()
        .map(|v| (v - average) * (v - average))
        .sum::<f64>()
        / sorted.len() as f64)
        .sqrt();
    let p50 = percentile(sorted, 0.5);
    let mad = if include_dispersion {
        let mut deviations: Vec<_> = sorted.iter().map(|v| (v - p50).abs()).collect();
        deviations.sort_by(f64::total_cmp);
        percentile(&deviations, 0.5)
    } else {
        0.0
    };
    Distribution {
        sample_count: sorted.len(),
        average,
        standard_deviation,
        p50,
        p95: percentile(sorted, 0.95),
        p99: percentile(sorted, 0.99),
        p999: (sorted.len() >= 1000).then(|| percentile(sorted, 0.999)),
        mad,
        iqr: if include_dispersion {
            (percentile(sorted, 0.75) - percentile(sorted, 0.25)).max(0.0)
        } else {
            0.0
        },
    }
}

pub fn summarize(
    query: &crate::ExactQueryResult,
    parameters: &crate::ChartParameters,
) -> Option<Distribution> {
    distribution_with_dispersion(
        query.samples.iter().map(|s| {
            if parameters.metric.velocity() {
                s.point.y.value().abs()
            } else {
                s.point.y.value()
            }
        }),
        parameters.metric != crate::Metric::Frequency,
    )
}

/// Count residuals use the selected raw points and the same nearest-neighbour
/// expansion as the product's moving average, including range boundaries.
pub fn residual_distribution(
    query: &crate::ExactQueryResult,
    parameters: &crate::ChartParameters,
) -> Option<Distribution> {
    distribution(residual_values(query, parameters)?.into_iter())
}
fn residual_values(
    query: &crate::ExactQueryResult,
    parameters: &crate::ChartParameters,
) -> Option<Vec<f64>> {
    let axis = match parameters.metric {
        crate::Metric::Count(axis) | crate::Metric::CountTrend(axis) => axis,
        _ => return None,
    };
    let raw: Vec<_> = query
        .records
        .iter()
        .enumerate()
        .map(|(i, e)| crate::Sample {
            source_index: query.requested_range.start + i,
            segment_id: e.session_segment_id,
            point: crate::Point {
                x: crate::Coordinate::Time {
                    ticks: parameters.time_basis.ticks(*e),
                    frequency: query.data_version.timestamp_frequency,
                },
                y: crate::Coordinate::Integer(super::delta(*e, axis) as i64),
            },
        })
        .collect();
    let mut sums = vec![0.0];
    for sample in &raw {
        sums.push(sums.last().unwrap() + sample.point.y.value());
    }
    let mut trend = parameters.clone();
    trend.metric = crate::Metric::CountTrend(axis);
    let estimates =
        super::window::evaluate(&raw, &sums, 0, None, &trend, &mut super::Work::default());
    Some(
        raw.iter()
            .zip(&estimates)
            .map(|(a, b)| (a.point.y.value() - b.point.y.value()).abs())
            .collect(),
    )
}

pub fn summarize_pair(
    primary: &crate::ExactQueryResult,
    secondary: &crate::ExactQueryResult,
    parameters: &crate::ChartParameters,
) -> Option<Distribution> {
    distribution(
        primary
            .samples
            .iter()
            .zip(&secondary.samples)
            .map(|(a, b)| {
                if parameters.metric.velocity() {
                    a.point.y.value().hypot(b.point.y.value())
                } else {
                    a.point.y.value()
                }
            }),
    )
}
pub fn residual_distribution_pair(
    primary: &crate::ExactQueryResult,
    secondary: &crate::ExactQueryResult,
    parameters: &crate::ChartParameters,
    secondary_parameters: &crate::ChartParameters,
) -> Option<Distribution> {
    distribution(
        residual_values(primary, parameters)?
            .iter()
            .zip(residual_values(secondary, secondary_parameters)?)
            .map(|(a, b)| a.hypot(b)),
    )
}
pub(super) fn percentile(values: &[f64], p: f64) -> f64 {
    let position = p.clamp(0.0, 1.0) * (values.len() - 1) as f64;
    let (lower, upper) = (position.floor() as usize, position.ceil() as usize);
    values[lower] + (values[upper] - values[lower]) * (position - lower as f64)
}
