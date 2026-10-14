//! Fixed-prefix CPU analysis and a window-independent D3D11 chart renderer.
pub mod analysis;
pub mod geometry;
pub mod gpu;
pub mod hlsl;
pub mod input;
pub mod query;

pub use analysis::{Change, ChartDelta, ChartEngine, ChartState, RebuildReason, Work};
pub use geometry::{Coordinate, GeometryBlock, GeometryStore, Point, Sample};
pub use input::{
    AnalysisOptions, Axis, ChartError, ChartParameters, DataVersion, DatasetId, FixedPrefix,
    HistogramLayout, HistogramPolicy, HistogramScale, HistogramSpec, Integrity, Metric, TimeBasis,
};
pub use query::{ExactQueryResult, query};
