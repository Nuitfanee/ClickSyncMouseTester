use super::*;
use clicksync_chart::{
    Axis, Coordinate, Metric, Point,
    gpu::{BlockKey, Instance, PreparedBlock},
};

fn fixture(count: usize) -> PreparedScene {
    let mut scene = PreparedScene {
        state: ChartEngine::new(ChartParameters::new(Metric::Count(Axis::X)), ANALYSIS_BLOCK)
            .unwrap()
            .state()
            .clone(),
        blocks: Vec::new(),
    };
    for start in (0..count).step_by(ANALYSIS_BLOCK) {
        let instances = (start..(start + ANALYSIS_BLOCK).min(count))
            .map(|i| {
                Instance::line(
                    [(i - start) as f32 * 0.125, (i % 31) as f32 - 16.0],
                    [(i - start + 1) as f32 * 0.125, (i % 19) as f32],
                )
            })
            .collect::<Vec<_>>();
        scene.blocks.push(Arc::new(PreparedBlock {
            key: BlockKey {
                dataset: 1,
                generation: 1,
                parameters: 0,
                kind: 1,
                id: scene.blocks.len(),
            },
            content_version: 1,
            origin: Point {
                x: Coordinate::Time {
                    ticks: (1_u64 << 60) + start as u64 * 1250,
                    frequency: 10_000_000,
                },
                y: Coordinate::Scalar(0.0),
            },
            instances: instances.into(),
        }));
    }
    scene
}

fn check(scene: &PreparedScene, cache: &mut bounds::BoundsCache) -> BoundsWork {
    let expected = scene_bounds(scene);
    let (origin, actual, work) = cache.measure(scene, &|| true).unwrap();
    assert_eq!(origin, expected.0);
    assert_eq!(actual.map(f64::to_bits), expected.1.map(f64::to_bits));
    work
}

#[test]
fn cached_bounds_match_full_scan_after_revisions_reordering_and_rebasing() {
    let mut scene = fixture(20_000);
    let mut cache = bounds::BoundsCache::default();
    assert_eq!(check(&scene, &mut cache).scanned_instances, 20_000);
    assert_eq!(check(&scene, &mut cache).scanned_instances, 0);
    let mut replacement = (*scene.blocks[2]).clone();
    replacement.instances = vec![Instance::point(-9999.0, 12345.0)].into();
    scene.blocks[2] = Arc::new(replacement); // key/version reuse cannot hide replacement
    let work = check(&scene, &mut cache);
    assert_eq!(work.scanned_instances, 1);
    assert_eq!(work.reused_blocks, 2);
    scene.blocks.swap(1, 2); // metric-local block IDs need not match vector positions
    assert_eq!(check(&scene, &mut cache).scanned_instances, 0);
    scene.blocks.remove(0); // changed scene origin invalidates shifted cached bounds
    assert!(check(&scene, &mut cache).scanned_instances > 0);
    scene.blocks.clear();
    assert_eq!(check(&scene, &mut cache).scanned_instances, 0);
    scene = fixture(19);
    assert_eq!(check(&scene, &mut cache).scanned_instances, 19);
    assert!(cache.measure(&scene, &|| false).is_err());
    check(&scene, &mut cache);
}

#[test]
fn bounds_keep_integer_scalar_and_signed_zero_coordinates() {
    let mut scene = fixture(20_000);
    for origin in [
        Coordinate::Integer(i64::MAX - 100_000),
        Coordinate::Scalar(1e15),
        Coordinate::Scalar(-0.0),
    ] {
        for (index, block) in scene.blocks.iter_mut().enumerate() {
            let mut copy = (**block).clone();
            copy.origin.x = origin;
            copy.origin.y = Coordinate::Scalar(index as f64 * -0.1);
            copy.instances = vec![
                Instance::line([-0.0, -0.0], [0.1, 0.3]),
                Instance::point(-100.0, 42.0),
            ]
            .into();
            *block = Arc::new(copy);
        }
        let mut cache = bounds::BoundsCache::default();
        check(&scene, &mut cache);
        assert_eq!(check(&scene, &mut cache).scanned_instances, 0);
    }
}

#[test]
fn interval_display_band_keeps_the_dense_values_without_removing_outliers() {
    let samples: Vec<_> = (0..10_000)
        .map(|index| clicksync_chart::Sample {
            source_index: index,
            segment_id: 0,
            point: Point {
                x: Coordinate::Scalar(index as f64 * 0.125),
                y: Coordinate::Scalar(if index % 1023 == 0 { 8.0 } else { 0.125 }),
            },
        })
        .collect();
    let blocks: Vec<_> = samples
        .chunks(511)
        .enumerate()
        .map(|(id, samples)| {
            Arc::new(clicksync_chart::GeometryBlock {
                id,
                content_version: 1,
                source_range: samples[0].source_index..samples.last().unwrap().source_index + 1,
                origin: samples[0].point,
                connector: None,
                samples: samples.to_vec(),
                relative_points: Vec::new(),
                bounds: [0.0; 4],
            })
        })
        .collect();
    assert_eq!(bounds::interval_band(&blocks), Some([0.125, 0.125, 0.125]));
    assert_eq!(
        blocks
            .iter()
            .map(|block| block.samples.len())
            .sum::<usize>(),
        10_000
    );
    assert!(
        blocks
            .iter()
            .flat_map(|block| &block.samples)
            .any(|sample| sample.point.y.value() == 8.0)
    );
    assert_eq!(bounds::interval_band(&[]), None);
}
