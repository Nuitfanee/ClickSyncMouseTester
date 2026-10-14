use crate::{
    analysis::{ChartEngine, Work, histogram::Histogram},
    geometry::{Coordinate, Point, Sample},
    input::{ChartError, ChartParameters, DataVersion, FixedPrefix, Metric},
};
use clicksync_core::SessionEvent;
use std::ops::Range;
pub mod hit;

#[derive(Clone, Debug, PartialEq)]
pub struct ExactQueryResult {
    pub data_version: DataVersion,
    pub parameter_version: u64,
    pub requested_range: Range<usize>,
    pub context_range: Range<usize>,
    pub records: Vec<SessionEvent>,
    pub samples: Vec<Sample>,
    pub histogram: Option<Histogram>,
}

/// Window values use fixed-prefix context, then are clipped to source positions.
/// This never reads pixels, cached geometry, or a newer session snapshot.
pub fn query(
    prefix: FixedPrefix<'_>,
    range: Range<usize>,
    parameters: &ChartParameters,
) -> Result<ExactQueryResult, ChartError> {
    parameters.validate()?;
    if range.start > range.end || range.end > prefix.len() {
        return Err(ChartError::InvalidRange);
    }
    let records: Vec<_> = prefix.events(range.clone())?.collect();
    let independent = matches!(
        parameters.metric,
        Metric::Count(_)
            | Metric::DeltaMagnitude
            | Metric::Interval
            | Metric::RawFrequency
            | Metric::Cumulative(_)
            | Metric::Trajectory
    );
    let context_range = if independent {
        if parameters.metric.timing() {
            range.start.saturating_sub(1)..range.end
        } else {
            range.clone()
        }
    } else {
        0..prefix.len()
    };
    let mut samples = if independent {
        independent_samples(prefix, range.clone(), parameters)
    } else {
        let mut engine = ChartEngine::new(parameters.clone(), 4096)?;
        engine.finish(prefix)?;
        engine
            .geometry()
            .samples()
            .filter(|s| range.contains(&s.source_index))
            .collect()
    };
    if parameters.metric == Metric::Trajectory {
        let origin = range.start.checked_sub(1).and_then(|i| prefix.event_at(i));
        let ox = origin.map_or(0, |e| e.session_cumulative_x);
        let oy = origin.map_or(0, |e| e.session_cumulative_y);
        for sample in &mut samples {
            if let (Coordinate::Integer(x), Coordinate::Integer(y)) =
                (sample.point.x, sample.point.y)
            {
                sample.point.x = Coordinate::Integer(x - ox);
                sample.point.y = Coordinate::Integer(y - oy);
            }
        }
    }
    let histogram = parameters.histogram.map(|spec| {
        let mut result = Histogram::new(spec.scale);
        result.update(
            spec,
            parameters.metric,
            &samples,
            &[],
            &[],
            &mut Work::default(),
        );
        // A fixed layout is initialized by a rebuild; automatic uses all samples.
        result
    });
    Ok(ExactQueryResult {
        data_version: prefix.version(),
        parameter_version: parameters.parameter_version,
        requested_range: range,
        context_range,
        records,
        samples,
        histogram,
    })
}

fn independent_samples(
    prefix: FixedPrefix<'_>,
    range: Range<usize>,
    parameters: &ChartParameters,
) -> Vec<Sample> {
    let mut result = Vec::with_capacity(range.len());
    let frequency = prefix.version().timestamp_frequency;
    for i in range {
        let event = prefix.event_at(i).expect("core fixed prefix");
        let y = match parameters.metric {
            Metric::Count(axis) => Some(Coordinate::Integer(
                crate::analysis::delta(event, axis) as i64
            )),
            Metric::Cumulative(axis) => Some(Coordinate::Integer(crate::analysis::cumulative(
                event, axis,
            ))),
            Metric::Trajectory => Some(Coordinate::Integer(event.session_cumulative_y)),
            Metric::DeltaMagnitude => {
                let dx = event.delta_x as f64;
                let dy = event.delta_y as f64;
                Some(Coordinate::Scalar((dx * dx + dy * dy).sqrt()))
            }
            _ => i.checked_sub(1).and_then(|previous| {
                parameters
                    .time_basis
                    .interval(prefix.event_at(previous)?, event, frequency)
                    .map(|v| {
                        Coordinate::Scalar(if parameters.metric == Metric::RawFrequency {
                            1000.0 / v
                        } else {
                            v
                        })
                    })
            }),
        };
        if let Some(y) = y {
            let x = if parameters.metric == Metric::Trajectory {
                Coordinate::Integer(event.session_cumulative_x)
            } else {
                Coordinate::Time {
                    ticks: parameters.time_basis.ticks(event),
                    frequency,
                }
            };
            result.push(Sample {
                source_index: i,
                segment_id: event.session_segment_id,
                point: Point { x, y },
            });
        }
    }
    result
}
