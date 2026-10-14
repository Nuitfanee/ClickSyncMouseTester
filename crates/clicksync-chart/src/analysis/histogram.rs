use super::Work;
use crate::{
    geometry::Sample,
    input::{HistogramLayout, HistogramPolicy, HistogramScale, HistogramSpec, Metric},
};

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct HistogramBin {
    pub minimum: f64,
    pub maximum: f64,
    pub value: f64,
    pub count: usize,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Histogram {
    pub layout: Option<HistogramLayout>,
    pub layout_version: u64,
    pub counts: Vec<usize>,
    pub valid_samples: usize,
    pub scale: HistogramScale,
}

impl Histogram {
    pub fn from_values(
        values: impl Iterator<Item = f64>,
        layout: HistogramLayout,
        scale: HistogramScale,
    ) -> Result<Self, crate::ChartError> {
        if !layout.is_valid() {
            return Err(crate::ChartError::InvalidParameters);
        }
        let mut histogram = Self {
            layout: Some(layout),
            layout_version: 1,
            counts: vec![0; layout.bins],
            valid_samples: 0,
            scale,
        };
        for value in values.filter(|v| v.is_finite()) {
            let index = layout.index(value);
            histogram.counts[index] += 1;
            histogram.valid_samples += 1;
        }
        Ok(histogram)
    }
    pub fn new(scale: HistogramScale) -> Self {
        Self {
            layout: None,
            layout_version: 0,
            counts: Vec::new(),
            valid_samples: 0,
            scale,
        }
    }

    pub fn bins(&self) -> Vec<HistogramBin> {
        let Some(layout) = self.layout.filter(|_| self.valid_samples > 0) else {
            return Vec::new();
        };
        self.counts
            .iter()
            .enumerate()
            .map(|(i, &count)| {
                let minimum = layout.minimum + i as f64 * layout.width;
                HistogramBin {
                    minimum,
                    maximum: if i + 1 == layout.bins {
                        layout.maximum
                    } else {
                        minimum + layout.width
                    },
                    value: if self.scale == HistogramScale::Percent {
                        count as f64 * 100.0 / self.valid_samples as f64
                    } else {
                        count as f64
                    },
                    count,
                }
            })
            .collect()
    }

    pub(crate) fn update(
        &mut self,
        spec: HistogramSpec,
        metric: Metric,
        output: &[Sample],
        removed: &[f64],
        added: &[f64],
        work: &mut Work,
    ) -> bool {
        let layout = match spec.policy {
            HistogramPolicy::Fixed(layout) => Some(layout),
            HistogramPolicy::Automatic => {
                let values: Vec<_> = output
                    .iter()
                    .map(|s| metric.histogram_value(s.point.y.value()))
                    .filter(|v| v.is_finite())
                    .collect();
                work.automatic_samples_sorted += values.len();
                resolve_layout(&values, matches!(metric, Metric::Count(_)))
            }
        };
        let changed = layout != self.layout;
        if changed {
            self.layout_version += 1;
            self.layout = layout;
            self.counts = layout.map_or_else(Vec::new, |l| vec![0; l.bins]);
            self.valid_samples = 0;
            for value in output
                .iter()
                .map(|s| metric.histogram_value(s.point.y.value()))
            {
                self.add(value, work);
            }
        } else if let Some(layout) = self.layout {
            for &value in removed.iter().filter(|v| v.is_finite()) {
                self.counts[layout.index(value)] -= 1;
                self.valid_samples -= 1;
                work.histogram_rebins += 1;
            }
            for &value in added {
                self.add(value, work);
            }
        }
        changed && matches!(spec.policy, HistogramPolicy::Automatic)
    }

    fn add(&mut self, value: f64, work: &mut Work) {
        if let Some(layout) = self.layout
            && value.is_finite()
        {
            self.counts[layout.index(value)] += 1;
            self.valid_samples += 1;
            work.histogram_rebins += 1;
        }
    }
}

/// Layout and interpolation are ported from the legacy histogram policy.
pub fn resolve_layout(samples: &[f64], integer_counts: bool) -> Option<HistogramLayout> {
    let mut sorted: Vec<_> = samples.iter().copied().filter(|v| v.is_finite()).collect();
    if sorted.is_empty() {
        return None;
    }
    sorted.sort_by(f64::total_cmp);
    if integer_counts {
        let minimum = sorted[0].round() as i32;
        let maximum = sorted[sorted.len() - 1].round() as i32;
        if minimum == i32::MAX || maximum == i32::MIN {
            return None;
        }
        let span = maximum as i64 - minimum as i64 + 1;
        if span <= 96 {
            return Some(HistogramLayout {
                minimum: minimum as f64 - 0.5,
                maximum: maximum as f64 + 0.5,
                width: 1.0,
                bins: span as usize,
            });
        }
        let width = (span as f64 / 96.0).ceil().max(1.0);
        let bins = (span as f64 / width).ceil() as usize;
        return Some(HistogramLayout {
            minimum: minimum as f64,
            maximum: minimum as f64 + bins as f64 * width,
            width,
            bins,
        });
    }
    let mut minimum = sorted[0];
    let mut maximum = sorted[sorted.len() - 1];
    if minimum == maximum {
        let padding = 1.0_f64.max(minimum.abs() * 0.1);
        minimum -= padding;
        maximum += padding;
    }
    let iqr = (percentile(&sorted, 0.75) - percentile(&sorted, 0.25)).max(0.0);
    let range = maximum - minimum;
    let width = if iqr > 0.0 && sorted.len() > 1 {
        2.0 * iqr / (sorted.len() as f64).powf(1.0 / 3.0)
    } else {
        0.0
    };
    let bins = (if width > 0.0 {
        (range / width).ceil()
    } else {
        (sorted.len() as f64).sqrt().ceil()
    })
    .clamp(8.0, 96.0) as usize;
    Some(HistogramLayout {
        minimum,
        maximum,
        width: range / bins as f64,
        bins,
    })
    .filter(|l| l.is_valid())
}

fn percentile(sorted: &[f64], fraction: f64) -> f64 {
    let position = fraction.clamp(0.0, 1.0) * (sorted.len() - 1) as f64;
    let lower = position.floor() as usize;
    let upper = position.ceil() as usize;
    if lower == upper {
        sorted[lower]
    } else {
        sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower as f64)
    }
}
