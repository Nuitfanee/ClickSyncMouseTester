mod common;
use clicksync_chart::{
    ChartEngine, Coordinate, DatasetId, FixedPrefix, Metric, Point, TimeBasis,
    gpu::{GpuError, Instance, PlotKind, PreparedScene, RenderSpec, Viewport},
};

#[test]
fn instance_layout_and_cpu_geometry_preserve_all_primitives_and_breaks() {
    assert_eq!(std::mem::size_of::<Instance>(), 20);
    assert_eq!(std::mem::offset_of!(Instance, b), 8);
    assert_eq!(std::mem::offset_of!(Instance, kind), 16);
    let snapshots = common::replay(2, 23, 8000, true);
    let snapshot = snapshots.last().unwrap();
    let mut engine = ChartEngine::new(
        common::parameters(Metric::Trajectory, TimeBasis::RawCapture),
        3,
    )
    .unwrap();
    engine
        .finish(FixedPrefix::new(DatasetId(7), snapshot))
        .unwrap();
    let points = PreparedScene::from_engine(&engine, PlotKind::Points).unwrap();
    let lines = PreparedScene::from_engine(&engine, PlotKind::Lines).unwrap();
    let trajectory = PreparedScene::from_engine(&engine, PlotKind::Trajectory).unwrap();
    let stems = PreparedScene::from_engine(&engine, PlotKind::Stems).unwrap();
    assert_eq!(points.instance_count(), engine.geometry().samples().count());
    assert_eq!(stems.instance_count(), points.instance_count());
    assert_eq!(trajectory.instance_count(), points.instance_count());
    assert!(
        trajectory
            .blocks
            .iter()
            .flat_map(|b| b.instances.iter())
            .all(|instance| instance.kind == 0 && instance.a == instance.b)
    );
    assert!(
        stems
            .blocks
            .iter()
            .flat_map(|b| b.instances.iter())
            .all(|instance| instance.kind == 3)
    );
    assert_eq!(
        lines.instance_count(),
        engine
            .geometry()
            .blocks()
            .iter()
            .map(|b| b.segments().count())
            .sum::<usize>()
    );
    assert!(
        lines
            .blocks
            .iter()
            .flat_map(|b| b.instances.iter())
            .any(|i| i.a == i.b)
    );
    for (cpu, gpu) in engine.geometry().blocks().iter().zip(&lines.blocks) {
        assert_eq!(cpu.origin, gpu.origin);
        assert_eq!(gpu.key.dataset, 7);
        assert_eq!(gpu.content_version, cpu.content_version);
    }
}

#[test]
fn histogram_uses_frozen_cpu_bins_and_large_integer_origin_is_subtracted_first() {
    let snapshots = common::replay(4, 21, 1000, false);
    let mut parameters = common::parameters(
        Metric::Count(clicksync_chart::Axis::X),
        TimeBasis::RawCapture,
    );
    parameters.histogram = Some(clicksync_chart::HistogramSpec {
        policy: clicksync_chart::HistogramPolicy::Automatic,
        scale: clicksync_chart::HistogramScale::Count,
    });
    let mut engine = ChartEngine::new(parameters, 4).unwrap();
    engine
        .finish(FixedPrefix::new(DatasetId(1), snapshots.last().unwrap()))
        .unwrap();
    let scene = PreparedScene::from_engine(&engine, PlotKind::Histogram).unwrap();
    assert_eq!(
        scene.instance_count(),
        engine.histogram().unwrap().bins().len()
    );
    assert!(scene.blocks[0].instances.iter().all(|i| i.kind == 2));
    let origin = Coordinate::Integer(1_i64 << 54);
    assert_eq!(
        Coordinate::Integer((1_i64 << 54) + 1).relative_to(origin),
        1.0
    );
}

#[test]
fn invalid_extent_clip_color_and_nonfinite_input_cannot_reach_d3d11() {
    let mut spec = RenderSpec::new(
        Viewport {
            origin: Point {
                x: Coordinate::Integer(0),
                y: Coordinate::Integer(0),
            },
            width: 100.0,
            height: 100.0,
        },
        100,
        100,
        1,
    );
    assert!(spec.validate().is_ok());
    spec.clip[2] = 101;
    assert_eq!(spec.validate(), Err(GpuError::InvalidScene));
    spec.clip[2] = 100;
    spec.viewport.width = f64::INFINITY;
    assert_eq!(spec.validate(), Err(GpuError::InvalidScene));
}

#[test]
fn replaced_instance_payloads_and_metadata_are_validated_independently() {
    use std::sync::Arc;
    let snapshots = common::replay(4, 21, 1000, false);
    let mut engine = ChartEngine::new(
        common::parameters(Metric::Trajectory, TimeBasis::RawCapture),
        4,
    )
    .unwrap();
    engine
        .finish(FixedPrefix::new(DatasetId(1), snapshots.last().unwrap()))
        .unwrap();
    let original = PreparedScene::from_engine(&engine, PlotKind::Points).unwrap();
    for invalid in [
        Instance::point(f32::NAN, 0.0),
        Instance::line([0.0, 0.0], [0.0, f32::INFINITY]),
        Instance {
            kind: 5,
            ..Instance::point(0.0, 0.0)
        },
    ] {
        let mut scene = original.clone();
        Arc::make_mut(&mut scene.blocks[0]).instances = vec![invalid].into();
        assert_eq!(scene.validate(), Err(GpuError::InvalidScene));
        assert!(original.validate().is_ok());
        Arc::make_mut(&mut scene.blocks[0]).instances = original.blocks[0].instances.clone();
        assert!(scene.validate().is_ok());
        Arc::make_mut(&mut scene.blocks[0]).origin.x = Coordinate::Scalar(f64::NAN);
        assert_eq!(scene.validate(), Err(GpuError::InvalidScene));
    }
}

#[test]
fn incremental_upload_descriptions_share_sealed_blocks_and_only_replace_the_tail() {
    let snapshots = common::replay(4, 21, 1000, false);
    let mut engine = ChartEngine::new(
        common::parameters(Metric::Trajectory, TimeBasis::RawCapture),
        4,
    )
    .unwrap();
    engine
        .advance(FixedPrefix::new(DatasetId(1), &snapshots[12]))
        .unwrap();
    let previous = PreparedScene::from_engine(&engine, PlotKind::Trajectory).unwrap();
    engine
        .advance(FixedPrefix::new(DatasetId(1), &snapshots[13]))
        .unwrap();
    let next =
        PreparedScene::from_engine_reusing(&engine, PlotKind::Trajectory, Some(&previous)).unwrap();
    assert!(std::sync::Arc::ptr_eq(&previous.blocks[0], &next.blocks[0]));
    assert!(std::sync::Arc::ptr_eq(&previous.blocks[1], &next.blocks[1]));
    assert_eq!(next.instance_count(), previous.instance_count() + 1);
}
