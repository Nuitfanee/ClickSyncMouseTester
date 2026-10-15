use super::PreparedInstances;
use crate::{ChartEngine, ChartState, Coordinate, DataVersion, Point};
use serde::Serialize;
use std::{fmt, sync::Arc};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PlotKind {
    Points,
    Lines,
    Trajectory,
    Stems,
    Histogram,
}

/// Rust/HLSL layout is independent of the authoritative event layout.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Instance {
    pub a: [f32; 2],
    pub b: [f32; 2],
    /// 0 = disk, 1 = round-capped segment (including stems), 2 = rectangle.
    pub kind: u32,
}

impl Instance {
    pub fn point(x: f32, y: f32) -> Self {
        Self {
            a: [x, y],
            b: [x, y],
            kind: 0,
        }
    }
    pub fn line(a: [f32; 2], b: [f32; 2]) -> Self {
        Self { a, b, kind: 1 }
    }
    pub fn stem(a: [f32; 2], b: [f32; 2]) -> Self {
        Self { a, b, kind: 3 }
    }
    pub fn connector(a: [f32; 2], b: [f32; 2]) -> Self {
        Self { a, b, kind: 4 }
    }
    pub fn bar(a: [f32; 2], b: [f32; 2]) -> Self {
        Self { a, b, kind: 2 }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash)]
pub struct BlockKey {
    pub dataset: u64,
    pub generation: u64,
    pub parameters: u64,
    pub kind: u32,
    pub id: usize,
}

#[derive(Clone, Debug)]
pub struct PreparedBlock {
    pub key: BlockKey,
    pub content_version: u64,
    pub origin: Point,
    pub instances: PreparedInstances,
}

#[derive(Clone, Debug)]
pub struct PreparedScene {
    pub state: ChartState,
    pub blocks: Vec<Arc<PreparedBlock>>,
}

impl PreparedScene {
    pub fn comparison_histograms(
        metric: crate::Metric,
        spec: crate::HistogramSpec,
        datasets: &[(crate::ChartState, Vec<Arc<crate::GeometryBlock>>)],
    ) -> Result<Vec<(Self, Vec<crate::analysis::histogram::HistogramBin>)>, GpuError> {
        use crate::analysis::histogram::{Histogram, resolve_layout};
        let values: Vec<_> = datasets
            .iter()
            .flat_map(|(_, blocks)| blocks)
            .flat_map(|b| &b.samples)
            .map(|s| metric.histogram_value(s.point.y.value()))
            .collect();
        let layout = match spec.policy {
            crate::HistogramPolicy::Fixed(layout) => Some(layout),
            crate::HistogramPolicy::Automatic => {
                resolve_layout(&values, matches!(metric, crate::Metric::Count(_)))
            }
        };
        datasets
            .iter()
            .enumerate()
            .map(|(index, (state, blocks))| {
                let mut scene = Self {
                    state: state.clone(),
                    blocks: Vec::new(),
                };
                let Some(layout) = layout else {
                    return Ok((scene, Vec::new()));
                };
                let bins = Histogram::from_values(
                    blocks
                        .iter()
                        .flat_map(|b| &b.samples)
                        .map(|s| metric.histogram_value(s.point.y.value())),
                    layout,
                    spec.scale,
                )
                .map_err(|_| GpuError::InvalidScene)?
                .bins();
                let group_scale = if datasets.len() > 1 {
                    0.82 / datasets.len() as f64
                } else {
                    1.0
                };
                let offset = if datasets.len() > 1 {
                    (index as f64 + 0.5) * layout.width / datasets.len() as f64
                        - layout.width / 2.0
                        - group_scale * layout.width / 2.0
                } else {
                    0.0
                };
                let instances = bins
                    .iter()
                    .map(|bin| {
                        let center = (bin.minimum + bin.maximum) / 2.0 + offset;
                        let half = (bin.maximum - bin.minimum) * group_scale / 2.0;
                        Instance::bar(
                            [(center - half - layout.minimum) as f32, 0.0],
                            [(center + half - layout.minimum) as f32, bin.value as f32],
                        )
                    })
                    .collect::<Vec<_>>();
                let version = state.data_version.ok_or(GpuError::InvalidScene)?;
                scene.blocks.push(Arc::new(PreparedBlock {
                    key: BlockKey {
                        dataset: version.dataset.0,
                        generation: version.generation,
                        parameters: state.parameter_version,
                        kind: PlotKind::Histogram as u32,
                        id: 0,
                    },
                    content_version: state.content_version,
                    origin: Point {
                        x: Coordinate::Scalar(layout.minimum),
                        y: Coordinate::Scalar(0.0),
                    },
                    instances: instances.into(),
                }));
                scene.validate()?;
                Ok((scene, bins))
            })
            .collect()
    }

    /// Runs on the CPU worker. Window/context access is unnecessary.
    pub fn from_engine(engine: &ChartEngine, kind: PlotKind) -> Result<Self, GpuError> {
        Self::from_engine_reusing(engine, kind, None)
    }

    pub fn from_engine_reusing(
        engine: &ChartEngine,
        kind: PlotKind,
        previous: Option<&Self>,
    ) -> Result<Self, GpuError> {
        let state = engine.state().clone();
        let version = state.data_version.ok_or(GpuError::InvalidScene)?;
        let key = |id| BlockKey {
            dataset: version.dataset.0,
            generation: version.generation,
            parameters: state.parameter_version,
            kind: kind as u32,
            id,
        };
        let mut blocks = Vec::new();
        if kind == PlotKind::Histogram {
            if let Some(histogram) = engine.histogram() {
                let bins = histogram.bins();
                if let Some(first) = bins.first() {
                    let origin = Point {
                        x: Coordinate::Scalar(first.minimum),
                        y: Coordinate::Scalar(0.0),
                    };
                    let instances = bins
                        .iter()
                        .map(|bin| {
                            Instance::bar(
                                [(bin.minimum - first.minimum) as f32, 0.0],
                                [(bin.maximum - first.minimum) as f32, bin.value as f32],
                            )
                        })
                        .collect::<Vec<_>>();
                    blocks.push(Arc::new(PreparedBlock {
                        key: key(0),
                        content_version: state.content_version,
                        origin,
                        instances: instances.into(),
                    }));
                }
            }
        } else {
            for block in engine.geometry().blocks() {
                if let Some(old) = previous.and_then(|scene| scene.blocks.get(block.id))
                    && old.key == key(block.id)
                    && old.content_version == block.content_version
                    && old.origin == block.origin
                {
                    blocks.push(old.clone());
                    continue;
                }
                let local = |point: Point| {
                    [
                        point.x.relative_to(block.origin.x) as f32,
                        point.y.relative_to(block.origin.y) as f32,
                    ]
                };
                let instances = match kind {
                    PlotKind::Points | PlotKind::Trajectory => block
                        .samples
                        .iter()
                        .map(|s| {
                            let p = local(s.point);
                            Instance::point(p[0], p[1])
                        })
                        .collect::<Vec<_>>(),
                    PlotKind::Lines => block
                        .segments()
                        .map(|(a, b)| Instance::line(local(a.point), local(b.point)))
                        .collect(),
                    PlotKind::Stems => block
                        .samples
                        .iter()
                        .map(|s| {
                            let p = local(s.point);
                            Instance::stem([p[0], -block.origin.y.value() as f32], p)
                        })
                        .collect(),
                    PlotKind::Histogram => unreachable!(),
                };
                blocks.push(Arc::new(PreparedBlock {
                    key: key(block.id),
                    content_version: block.content_version,
                    origin: block.origin,
                    instances: instances.into(),
                }));
            }
        }
        let scene = Self { state, blocks };
        scene.validate()?;
        Ok(scene)
    }

    pub fn validate(&self) -> Result<(), GpuError> {
        let version = self.state.data_version.ok_or(GpuError::InvalidScene)?;
        if self.state.published_end > self.state.processed_end
            || self.state.processed_end > version.end
            || self.blocks.iter().any(|b| {
                b.key.dataset != version.dataset.0
                    || b.key.generation != version.generation
                    || b.key.parameters != self.state.parameter_version
                    || !b.origin.x.value().is_finite()
                    || !b.origin.y.value().is_finite()
                    || !b.instances.is_valid()
            })
        {
            return Err(GpuError::InvalidScene);
        }
        Ok(())
    }

    pub fn instance_count(&self) -> usize {
        self.blocks.iter().map(|b| b.instances.len()).sum()
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Viewport {
    /// Subtraction happens in the original integer/tick domain.
    pub origin: Point,
    pub width: f64,
    pub height: f64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct RenderSpec {
    pub viewport: Viewport,
    /// Larger Y values move down the plot, as in mouse movement coordinates.
    pub y_down: bool,
    pub width: u32,
    pub height: u32,
    pub clip: [u32; 4],
    pub color: [f32; 4],
    pub radius: f32,
    pub line_width: f32,
    pub style_version: u64,
    pub request_id: u64,
    pub device_epoch: u64,
}

impl RenderSpec {
    pub fn new(viewport: Viewport, width: u32, height: u32, device_epoch: u64) -> Self {
        Self {
            viewport,
            y_down: false,
            width,
            height,
            clip: [0, 0, width, height],
            color: [0.05, 0.4, 0.9, 1.0],
            radius: 3.0,
            line_width: 2.0,
            style_version: 0,
            request_id: 0,
            device_epoch,
        }
    }

    /// Convert a normalized Y fraction between data and screen coordinates.
    pub fn y_fraction(&self, fraction: f64) -> f64 {
        if self.y_down {
            fraction
        } else {
            1.0 - fraction
        }
    }

    pub fn validate(&self) -> Result<(), GpuError> {
        let v = self.viewport;
        if self.width == 0
            || self.height == 0
            || !v.width.is_finite()
            || v.width <= 0.0
            || !v.height.is_finite()
            || v.height <= 0.0
            || !v.origin.x.value().is_finite()
            || !v.origin.y.value().is_finite()
            || self.clip[0] >= self.clip[2]
            || self.clip[1] >= self.clip[3]
            || self.clip[2] > self.width
            || self.clip[3] > self.height
            || self
                .color
                .iter()
                .any(|x| !x.is_finite() || !(0.0..=1.0).contains(x))
            || !self.radius.is_finite()
            || self.radius <= 0.0
            || !self.line_width.is_finite()
            || self.line_width <= 0.0
        {
            return Err(GpuError::InvalidScene);
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct ResourceLimits {
    pub resident_bytes: usize,
    pub resident_blocks: usize,
    pub upload_slots: usize,
    pub instances_per_upload: usize,
    pub draws_per_tick: usize,
    pub layer_slots: usize,
    /// Total across all allocated layer textures, including leased old sizes.
    pub layer_bytes: usize,
    pub readback_bytes: usize,
}

impl Default for ResourceLimits {
    fn default() -> Self {
        Self {
            resident_bytes: 64 * 1024 * 1024,
            resident_blocks: 1024,
            upload_slots: 2,
            instances_per_upload: 8192,
            draws_per_tick: 8,
            layer_slots: 3,
            layer_bytes: 128 * 1024 * 1024,
            readback_bytes: 32 * 1024 * 1024,
        }
    }
}

#[derive(Clone, Debug, Serialize, Default)]
pub struct ResourceReport {
    pub resident_bytes: usize,
    pub resident_blocks: usize,
    pub upload_bytes: usize,
    pub upload_slots: usize,
    pub busy_upload_slots: usize,
    pub layer_bytes: usize,
    pub layers: usize,
    pub readback_bytes: usize,
    pub peak_resident_bytes: usize,
    pub peak_layer_bytes: usize,
    pub peak_readback_bytes: usize,
    pub uploaded_bytes: u64,
    pub reused_blocks: u64,
    pub partial_uploads: u64,
    pub draw_calls: u64,
    pub drawn_instances: u64,
    pub culled_instances: u64,
    pub scene_validation_blocks: u64,
    pub pressure_returns: u64,
    pub completed_layers: u64,
}

#[derive(Clone, Debug, PartialEq)]
pub enum GpuError {
    InvalidScene,
    ResourceBusy,
    /// Cannot make forward progress at this size while retaining the displayed layer.
    LayerCapacity,
    WrongEpoch,
    WrongThread,
    WrongDevice,
    Device(String),
}

impl fmt::Display for GpuError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}
impl std::error::Error for GpuError {}

impl From<crate::ChartError> for GpuError {
    fn from(_: crate::ChartError) -> Self {
        Self::InvalidScene
    }
}

#[derive(Clone, Debug)]
pub struct LayerVersion {
    pub data: DataVersion,
    pub processed_end: usize,
    pub published_end: usize,
    pub content_version: u64,
    pub parameter_version: u64,
    pub finished: bool,
    pub spec: RenderSpec,
}
