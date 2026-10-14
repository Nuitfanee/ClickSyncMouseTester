pub mod calibration;
pub mod histogram;
pub mod keys;
pub mod polling;
pub mod sensitivity;
pub mod statistics;
mod velocity;
mod window;

use crate::{
    geometry::{Coordinate, GeometryStore, Point, Sample},
    input::{Axis, ChartError, ChartParameters, DataVersion, FixedPrefix, Metric, TimeBasis},
};
use clicksync_core::SessionEvent;
use histogram::Histogram;
use std::ops::Range;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RebuildReason {
    AutomaticHistogramLayout,
    NonMonotonicTime,
    BidirectionalVelocitySmoothing,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum Change {
    Append(Range<usize>),
    Replace(Range<usize>),
    Rebuild {
        source_range: Range<usize>,
        reason: RebuildReason,
    },
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct Work {
    pub new_record_reads: usize,
    pub derived_evaluations: usize,
    pub smoothing_evaluations: usize,
    pub context_record_reads: usize,
    pub revisited_samples: usize,
    pub geometry_samples_written: usize,
    pub histogram_rebins: usize,
    pub automatic_samples_sorted: usize,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ChartState {
    pub processed_end: usize,
    /// Source-event position, not the derived sample ordinal.
    pub revisable_from: usize,
    pub published_end: usize,
    pub finished: bool,
    pub data_version: Option<DataVersion>,
    pub parameter_version: u64,
    pub content_version: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ChartDelta {
    pub changes: Vec<Change>,
    pub changed_blocks: Range<usize>,
    pub state: ChartState,
    pub work: Work,
}

pub struct ChartEngine {
    parameters: ChartParameters,
    state: ChartState,
    geometry: GeometryStore,
    histogram: Option<Histogram>,
    raw: Vec<Sample>,
    sums: Vec<f64>,
    output: Vec<Sample>,
    path_prefix: Vec<f64>,
    previous_event: Option<SessionEvent>,
    spans: Vec<Range<usize>>,
    published_spans: usize,
    velocity_cache: Option<velocity::VelocityCache>,
    revisable_sample: usize,
    non_monotonic: bool,
}

impl ChartEngine {
    pub fn new(parameters: ChartParameters, block_capacity: usize) -> Result<Self, ChartError> {
        parameters.validate()?;
        if block_capacity == 0 {
            return Err(ChartError::InvalidParameters);
        }
        Ok(Self {
            state: ChartState {
                processed_end: 0,
                revisable_from: 0,
                published_end: 0,
                finished: false,
                data_version: None,
                parameter_version: parameters.parameter_version,
                content_version: 0,
            },
            histogram: parameters.histogram.map(|spec| Histogram::new(spec.scale)),
            parameters,
            geometry: GeometryStore::new(block_capacity),
            raw: Vec::new(),
            sums: vec![0.0],
            output: Vec::new(),
            path_prefix: vec![0.0],
            previous_event: None,
            spans: Vec::new(),
            published_spans: 0,
            velocity_cache: None,
            revisable_sample: 0,
            non_monotonic: false,
        })
    }

    pub fn state(&self) -> &ChartState {
        &self.state
    }

    pub fn parameters(&self) -> &ChartParameters {
        &self.parameters
    }

    pub fn geometry(&self) -> &GeometryStore {
        &self.geometry
    }

    pub fn histogram(&self) -> Option<&Histogram> {
        self.histogram.as_ref()
    }

    /// Allocated scalar/sample cache payloads. Shared geometry is accounted
    /// separately by its immutable block identity.
    pub fn cached_value_bytes(&self) -> usize {
        (self.raw.capacity() + self.output.capacity()) * std::mem::size_of::<Sample>()
            + (self.sums.capacity() + self.path_prefix.capacity()) * std::mem::size_of::<f64>()
            + self.spans.capacity() * std::mem::size_of::<Range<usize>>()
            + self.histogram.as_ref().map_or(0, |histogram| {
                histogram.counts.capacity() * std::mem::size_of::<usize>()
            })
            + self
                .velocity_cache
                .as_ref()
                .map_or(0, velocity::VelocityCache::cached_value_bytes)
    }

    pub fn advance(&mut self, prefix: FixedPrefix<'_>) -> Result<ChartDelta, ChartError> {
        self.update(prefix, false)
    }

    /// Completes this immutable prefix. A later extension requires a new engine.
    pub fn finish(&mut self, prefix: FixedPrefix<'_>) -> Result<ChartDelta, ChartError> {
        self.update(prefix, true)
    }

    fn validate_prefix(&self, prefix: FixedPrefix<'_>) -> Result<(), ChartError> {
        if let Some(old) = self.state.data_version {
            let new = prefix.version();
            if new.dataset != old.dataset {
                return Err(ChartError::WrongDataset);
            }
            if new.generation != old.generation {
                return Err(ChartError::WrongGeneration);
            }
            if new.end < old.end || new.session_revision < old.session_revision {
                return Err(ChartError::RegressedPrefix);
            }
            if new.timestamp_frequency != old.timestamp_frequency
                || (old.end > 0 && prefix.event_at(old.end - 1) != self.previous_event)
            {
                return Err(ChartError::ChangedPrefix);
            }
            if self.state.finished && new != old {
                // Sealed samples stay immutable when later capture evidence arrives.
                let metadata_update = DataVersion {
                    session_revision: new.session_revision,
                    integrity: new.integrity,
                    ..old
                };
                if new != metadata_update {
                    return Err(ChartError::Finished);
                }
            }
        }
        Ok(())
    }

    fn update(&mut self, prefix: FixedPrefix<'_>, finish: bool) -> Result<ChartDelta, ChartError> {
        self.validate_prefix(prefix)?;
        let old_end = self.state.processed_end;
        let mut work = Work {
            context_record_reads: usize::from(
                self.state
                    .data_version
                    .is_some_and(|version| version.end > 0),
            ),
            ..Work::default()
        };
        let mut changes = Vec::new();
        let mut changed_blocks = self.geometry.blocks().len()..self.geometry.blocks().len();
        if self.state.finished {
            self.state.data_version = Some(prefix.version());
            return Ok(ChartDelta {
                changes,
                changed_blocks,
                state: self.state.clone(),
                work,
            });
        }
        self.state.content_version += 1;
        self.read_new(prefix, &mut work);
        if self.parameters.metric.velocity() && prefix.len() > old_end {
            changed_blocks = self.update_velocity(prefix, &mut work, &mut changes);
        } else if prefix.len() > old_end {
            let start = if self.parameters.metric.window() {
                if self.non_monotonic {
                    0
                } else {
                    self.revisable_sample
                }
            } else {
                self.raw.partition_point(|s| s.source_index < old_end)
            };
            let from = self.raw.get(start).map_or(old_end, |s| s.source_index);
            let replacement = if self.parameters.metric.window() {
                window::evaluate(
                    &self.raw,
                    &self.sums,
                    start,
                    self.output
                        .last()
                        .filter(|s| s.source_index < from)
                        .copied()
                        .or_else(|| {
                            self.output
                                .iter()
                                .rev()
                                .find(|s| s.source_index < from)
                                .copied()
                        }),
                    &self.parameters,
                    &mut work,
                )
            } else {
                work.derived_evaluations += self.raw.len() - start;
                self.raw[start..].to_vec()
            };
            changed_blocks = self.replace_output(from, replacement, &mut work, &mut changes);
            changes.push(if self.non_monotonic && self.parameters.metric.window() {
                Change::Rebuild {
                    source_range: from..prefix.len(),
                    reason: RebuildReason::NonMonotonicTime,
                }
            } else if from < old_end {
                Change::Replace(from..prefix.len())
            } else {
                Change::Append(from..prefix.len())
            });
            self.state.published_end = prefix.len();
        }
        self.state.processed_end = prefix.len();
        self.state.data_version = Some(prefix.version());
        self.state.finished = finish;
        if finish {
            self.state.revisable_from = prefix.len();
            self.velocity_cache = None;
        } else if self.parameters.metric.window() {
            self.revisable_sample = window::stable_end(
                &self.raw,
                self.revisable_sample,
                &self.parameters,
                self.non_monotonic,
            );
            self.state.revisable_from = self
                .raw
                .get(self.revisable_sample)
                .map_or(prefix.len(), |s| s.source_index);
        } else if !self.parameters.metric.velocity() {
            self.state.revisable_from = prefix.len();
        }
        Ok(ChartDelta {
            changes,
            changed_blocks,
            state: self.state.clone(),
            work,
        })
    }

    fn read_new(&mut self, prefix: FixedPrefix<'_>, work: &mut Work) {
        let metric = self.parameters.metric;
        let basis = self.parameters.time_basis;
        let frequency = prefix.version().timestamp_frequency;
        for index in self.state.processed_end..prefix.len() {
            let event = prefix.event_at(index).expect("core fixed prefix");
            work.new_record_reads += 1;
            let dx = event.delta_x as f64;
            let dy = event.delta_y as f64;
            let distance = (dx * dx + dy * dy).sqrt();
            // Same left-to-right prefix addition as the legacy analysis index.
            self.path_prefix
                .push(self.path_prefix.last().unwrap() + distance);
            if self
                .previous_event
                .is_none_or(|previous| previous.session_segment_id != event.session_segment_id)
            {
                self.spans.push(index..index + 1);
            } else {
                self.spans.last_mut().unwrap().end = index + 1;
            }
            if !metric.velocity() {
                let y = match metric {
                    Metric::Count(axis) | Metric::CountTrend(axis) => {
                        Some(Coordinate::Integer(delta(event, axis) as i64))
                    }
                    Metric::DeltaMagnitude => Some(Coordinate::Scalar(distance)),
                    Metric::Cumulative(axis) => Some(Coordinate::Integer(cumulative(event, axis))),
                    Metric::Trajectory => Some(Coordinate::Integer(event.session_cumulative_y)),
                    Metric::PathLength => {
                        Some(Coordinate::Scalar(*self.path_prefix.last().unwrap()))
                    }
                    _ => self.previous_event.and_then(|previous| {
                        basis.interval(previous, event, frequency).map(|interval| {
                            Coordinate::Scalar(if metric == Metric::RawFrequency {
                                1000.0 / interval
                            } else {
                                interval
                            })
                        })
                    }),
                };
                if let Some(y) = y {
                    let x = if metric == Metric::Trajectory {
                        Coordinate::Integer(event.session_cumulative_x)
                    } else {
                        Coordinate::Time {
                            ticks: basis.ticks(event),
                            frequency,
                        }
                    };
                    let sample = Sample {
                        source_index: index,
                        segment_id: event.session_segment_id,
                        point: Point { x, y },
                    };
                    if self
                        .raw
                        .last()
                        .is_some_and(|p| p.point.x.value() > x.value())
                    {
                        self.non_monotonic = true;
                    }
                    self.sums.push(self.sums.last().unwrap() + y.value());
                    self.raw.push(sample);
                }
            }
            self.previous_event = Some(event);
        }
    }

    fn update_velocity(
        &mut self,
        prefix: FixedPrefix<'_>,
        work: &mut Work,
        changes: &mut Vec<Change>,
    ) -> Range<usize> {
        let ranges: Vec<_> = if self.parameters.time_basis == TimeBasis::Logical {
            std::iter::once(1.min(prefix.len())..prefix.len()).collect()
        } else {
            self.spans[self.published_spans..]
                .iter()
                .map(|span| span.start.max(1)..span.end)
                .collect()
        };
        let mut first_block = self.geometry.blocks().len();
        let count = ranges.len();
        for (i, range) in ranges.into_iter().enumerate() {
            let mut cache = self.velocity_cache.take().unwrap_or_default();
            if let Some((from, replacement)) = cache.update(
                prefix,
                range.clone(),
                &self.path_prefix,
                &self.parameters,
                work,
            ) {
                let blocks = self.replace_output(from, replacement, work, changes);
                first_block = first_block.min(blocks.start);
                changes.push(if cache.non_monotonic {
                    Change::Rebuild {
                        source_range: from..range.end,
                        reason: RebuildReason::NonMonotonicTime,
                    }
                } else if from == range.start && from < self.state.processed_end {
                    Change::Rebuild {
                        source_range: from..range.end,
                        reason: RebuildReason::BidirectionalVelocitySmoothing,
                    }
                } else if from < self.state.processed_end {
                    Change::Replace(from..range.end)
                } else {
                    Change::Append(from..range.end)
                });
            }
            if i + 1 == count {
                self.velocity_cache = Some(cache);
            }
        }
        self.published_spans = self.spans.len().saturating_sub(1);
        self.state.published_end = prefix.len();
        // Backward EMA has no finite future-dependency horizon. RawCapture closes
        // at a real segment boundary; Logical intentionally spans the session.
        self.state.revisable_from = if self.parameters.time_basis == TimeBasis::Logical {
            1.min(prefix.len())
        } else {
            self.spans.last().map_or(prefix.len(), |r| r.start.max(1))
        };
        first_block..self.geometry.blocks().len()
    }

    fn replace_output(
        &mut self,
        from: usize,
        replacement: Vec<Sample>,
        work: &mut Work,
        changes: &mut Vec<Change>,
    ) -> Range<usize> {
        let index = self.output.partition_point(|s| s.source_index < from);
        work.revisited_samples += self.output.len() - index;
        let metric = self.parameters.metric;
        let histogram_values = self.histogram.as_ref().map(|_| {
            let removed: Vec<_> = self.output[index..]
                .iter()
                .map(|s| metric.histogram_value(s.point.y.value()))
                .collect();
            let added: Vec<_> = replacement
                .iter()
                .map(|s| metric.histogram_value(s.point.y.value()))
                .collect();
            (removed, added)
        });
        self.output.truncate(index);
        self.output.extend(replacement);
        if let (Some(histogram), Some(spec), Some((removed, added))) = (
            &mut self.histogram,
            self.parameters.histogram,
            histogram_values,
        ) && histogram.update(spec, metric, &self.output, &removed, &added, work)
        {
            changes.push(Change::Rebuild {
                source_range: 0..self.output.last().map_or(0, |s| s.source_index + 1),
                reason: RebuildReason::AutomaticHistogramLayout,
            });
        }
        let blocks = self.geometry.replace_from(
            from,
            self.output[index..].iter().copied(),
            self.state.content_version,
        );
        work.geometry_samples_written += self.geometry.blocks()[blocks.clone()]
            .iter()
            .map(|b| b.samples.len())
            .sum::<usize>();
        blocks
    }
}

pub(crate) fn delta(event: SessionEvent, axis: Axis) -> i32 {
    match axis {
        Axis::X => event.delta_x,
        Axis::Y => event.delta_y,
    }
}

pub(crate) fn cumulative(event: SessionEvent, axis: Axis) -> i64 {
    match axis {
        Axis::X => event.session_cumulative_x,
        Axis::Y => event.session_cumulative_y,
    }
}
