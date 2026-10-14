use super::{ChartResult, DatasetChart};
use clicksync_chart::{Point, TimeBasis, analysis::histogram::HistogramBin};
use clicksync_core::SessionSnapshot;
use std::{ops::Range, sync::Arc};

const TIME_TOLERANCE_MS: f64 = 0.000001;

pub(crate) fn histogram_bin(bins: &[HistogramBin], value: f64) -> Option<usize> {
    let index = bins
        .partition_point(|bin| bin.maximum <= value)
        .min(bins.len().saturating_sub(1));
    let bin = bins.get(index)?;
    (value >= bin.minimum
        && (value < bin.maximum || (index + 1 == bins.len() && value <= bin.maximum)))
        .then_some(index)
}

fn sample_bin(bins: &[HistogramBin], metric: clicksync_chart::Metric, value: f64) -> Option<usize> {
    let value = metric.histogram_value(value);
    if !value.is_finite() {
        return None;
    }
    // Fixed layouts clamp overflow into the end bins, unlike pointer hit testing.
    histogram_bin(
        bins,
        value.clamp(bins.first()?.minimum, bins.last()?.maximum),
    )
}

/// Prepared on ChartWorker, after range filtering and shared histogram layout.
#[derive(Default)]
pub(super) struct LinkIndex {
    times: Vec<(f64, usize)>,
    runs: Vec<Range<usize>>,
    bins: Vec<(Option<usize>, usize)>,
}

impl LinkIndex {
    pub(super) fn prepare(
        dataset: &DatasetChart,
        snapshot: &SessionSnapshot,
        parameters: &clicksync_chart::ChartParameters,
        comparison: bool,
    ) -> Self {
        let mut times = if comparison {
            snapshot
                .events()
                .enumerate()
                .map(|(index, event)| {
                    (
                        parameters.time_basis.ticks(event) as f64 * 1000.0
                            / snapshot.timestamp_frequency() as f64,
                        index,
                    )
                })
                .collect::<Vec<_>>()
        } else {
            Vec::new()
        };
        times.sort_unstable_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)));
        let mut runs: Vec<Range<usize>> = Vec::new();
        let mut bins = vec![(None, 0); dataset.histogram.len()];
        for (index, block) in dataset.geometry.iter().enumerate() {
            // X and Y are separate monotone source-index runs, even in a raw-time range.
            if index == 0 || block.source_range.start < dataset.geometry[index - 1].source_range.end
            {
                runs.push(index..index + 1);
            } else {
                runs.last_mut().unwrap().end = index + 1;
            }
            if bins.is_empty() {
                continue;
            }
            for sample in &block.samples {
                if let Some(index) = sample_bin(
                    &dataset.histogram,
                    parameters.metric,
                    sample.point.y.value(),
                ) {
                    bins[index].0.get_or_insert(sample.source_index);
                    bins[index].1 += 1;
                }
            }
        }
        Self { times, runs, bins }
    }

    fn time_match(&self, time: f64) -> (Option<usize>, bool) {
        if !time.is_finite() || time < 0.0 {
            return (None, false);
        }
        let first = self
            .times
            .partition_point(|(candidate, _)| candidate - time < -TIME_TOLERANCE_MS);
        let Some(&(candidate, index)) = self.times.get(first) else {
            return (None, false);
        };
        if (candidate - time).abs() > TIME_TOLERANCE_MS {
            return (None, false);
        }
        if self
            .times
            .get(first + 1)
            .is_some_and(|(candidate, _)| (candidate - time).abs() <= TIME_TOLERANCE_MS)
        {
            (None, true)
        } else {
            (Some(index), false)
        }
    }

    fn points(
        &self,
        dataset: &DatasetChart,
        index: usize,
        histogram: Option<clicksync_chart::Metric>,
    ) -> Vec<Point> {
        let mut points = Vec::with_capacity(2);
        for run in &self.runs {
            let blocks = &dataset.geometry[run.clone()];
            let block = blocks.partition_point(|block| block.source_range.end <= index);
            if let Some(block) = blocks
                .get(block)
                .filter(|b| b.source_range.contains(&index))
            {
                let sample = block
                    .samples
                    .partition_point(|sample| sample.source_index < index);
                if let Some(sample) = block
                    .samples
                    .get(sample)
                    .filter(|s| s.source_index == index)
                {
                    let point = if let Some(metric) = histogram {
                        let Some(bin) =
                            sample_bin(&dataset.histogram, metric, sample.point.y.value())
                                .map(|slot| dataset.histogram[slot])
                        else {
                            continue;
                        };
                        Point {
                            x: clicksync_chart::Coordinate::Scalar(
                                (bin.minimum + bin.maximum) * 0.5,
                            ),
                            y: clicksync_chart::Coordinate::Scalar(bin.value),
                        }
                    } else {
                        sample.point
                    };
                    points.push(point);
                    if points.len() == 2 {
                        break;
                    }
                }
            }
        }
        points
    }
}

#[derive(Clone)]
pub struct LinkedResult {
    pub request_id: u64,
    pub source_version: u64,
    pub key: u32,
    pub index: usize,
    pub time_ms: f64,
    pub logical: bool,
    pub links: Vec<(u32, Option<usize>)>,
    pub markers: Vec<(u64, u32, Vec<Point>)>,
    pub matches: usize,
    pub ambiguous: usize,
}

impl ChartResult {
    /// A read-only lookup on presented results; never takes the file-operation slot.
    pub fn linked(
        &self,
        key: u32,
        mut index: usize,
        charts: &[Arc<ChartResult>],
        bin: Option<HistogramBin>,
    ) -> Result<LinkedResult, String> {
        let mut matches = 1;
        if let Some(bin) = bin {
            let dataset = self
                .datasets
                .iter()
                .find(|dataset| dataset.device_key == key)
                .ok_or("missing histogram dataset")?;
            let slot = dataset
                .histogram
                .iter()
                .position(|candidate| *candidate == bin)
                .ok_or("histogram bin is not from the presented result")?;
            let entry = dataset
                .link_index
                .as_ref()
                .ok_or("missing analysis link index")?
                .bins[slot];
            index = entry.0.ok_or("no actual reports in this histogram bin")?;
            matches = entry.1;
        }
        let logical = self.request.parameters.time_basis == TimeBasis::Logical;
        let time =
            crate::product::performance_analysis::time_ms(self.snapshot(key)?, index, logical)
                .ok_or("selected report unavailable")?;
        let mut ambiguous = 0;
        let mut links = Vec::with_capacity(self.datasets.len());
        for dataset in &self.datasets {
            let mapped = if dataset.device_key == key {
                Some(index)
            } else {
                let (mapped, duplicate) = dataset
                    .link_index
                    .as_ref()
                    .ok_or("missing analysis link index")?
                    .time_match(time);
                ambiguous += usize::from(duplicate);
                mapped
            };
            links.push((dataset.device_key, mapped));
        }
        let mut markers = Vec::new();
        for chart in charts {
            if chart.request.source_version != self.request.source_version
                || chart.request.parameters.time_basis != self.request.parameters.time_basis
                || chart.request.prefix.received_sequence != self.request.prefix.received_sequence
                || chart.request.prefix.generation != self.request.prefix.generation
            {
                continue;
            }
            for dataset in &chart.datasets {
                let index = links
                    .iter()
                    .find(|(key, _)| *key == dataset.device_key)
                    .and_then(|(_, index)| *index);
                let points = match index {
                    Some(index) => dataset
                        .link_index
                        .as_ref()
                        .ok_or("missing analysis link index")?
                        .points(
                            dataset,
                            index,
                            (chart.request.kind == clicksync_chart::gpu::PlotKind::Histogram)
                                .then_some(chart.request.parameters.metric),
                        ),
                    None => Vec::new(),
                };
                markers.push((chart.request.id, dataset.device_key, points));
            }
        }
        Ok(LinkedResult {
            request_id: self.request.id,
            source_version: self.request.source_version,
            key,
            index,
            time_ms: time,
            logical,
            links,
            markers,
            matches,
            ambiguous,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn indexed_times_keep_duplicates_tolerance_and_missing_reports() {
        let index = LinkIndex {
            times: vec![(0.0, 0), (1.0, 3), (1.0, 1), (2.0, 2), (3.0, 4)],
            ..Default::default()
        };
        assert_eq!(index.time_match(1.0), (None, true));
        assert_eq!(index.time_match(2.0), (Some(2), false));
        assert_eq!(
            index.time_match(2.0 + TIME_TOLERANCE_MS / 2.0),
            (Some(2), false)
        );
        assert_eq!(
            index.time_match(2.0 + TIME_TOLERANCE_MS * 2.0),
            (None, false)
        );
        assert_eq!(index.time_match(50.0), (None, false));
        assert_eq!(index.time_match(f64::NAN), (None, false));
    }

    #[test]
    fn histogram_membership_uses_open_internal_edges_and_a_closed_final_edge() {
        let bins = [
            HistogramBin {
                minimum: -0.5,
                maximum: 0.5,
                value: 1.0,
                count: 1,
            },
            HistogramBin {
                minimum: 0.5,
                maximum: 1.5,
                value: 1.0,
                count: 1,
            },
        ];
        assert_eq!(histogram_bin(&bins, -0.5), Some(0));
        assert_eq!(histogram_bin(&bins, 0.5), Some(1));
        assert_eq!(histogram_bin(&bins, 1.5), Some(1));
        assert_eq!(histogram_bin(&bins, 2.0), None);
        assert_eq!(histogram_bin(&[], 0.0), None);
        let count = clicksync_chart::Metric::Count(clicksync_chart::Axis::X);
        let velocity = clicksync_chart::Metric::Velocity(clicksync_chart::Axis::X);
        assert_eq!(sample_bin(&bins, count, -10.0), Some(0));
        assert_eq!(sample_bin(&bins, count, 10.0), Some(1));
        assert_eq!(sample_bin(&bins, velocity, -1.0), Some(1));
        assert_eq!(sample_bin(&bins, velocity, f64::NAN), None);
    }
}
