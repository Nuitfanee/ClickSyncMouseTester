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
    let band = bounds::value_band(&blocks, None, |_| false).unwrap();
    assert_eq!(band.quantiles.map(|v| v + band.origin.value()), [0.125; 3]);
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
    assert!(bounds::value_band(&[], None, |_| false).is_none());
}

fn value_blocks(
    values: &[Coordinate],
    block_size: usize,
) -> Vec<Arc<clicksync_chart::GeometryBlock>> {
    values
        .chunks(block_size)
        .enumerate()
        .map(|(id, values)| {
            let start = id * block_size;
            let origin = Point {
                x: Coordinate::Scalar(start as f64),
                y: values[0],
            };
            Arc::new(clicksync_chart::GeometryBlock {
                id,
                content_version: 1,
                source_range: start..start + values.len(),
                origin,
                connector: None,
                samples: values
                    .iter()
                    .enumerate()
                    .map(|(i, &y)| clicksync_chart::Sample {
                        source_index: start + i,
                        segment_id: 0,
                        point: Point {
                            x: Coordinate::Scalar((start + i) as f64),
                            y,
                        },
                    })
                    .collect(),
                relative_points: Vec::new(),
                bounds: [0.0, (values.len() - 1) as f64, 0.0, 0.0],
            })
        })
        .collect()
}

#[test]
fn value_band_handles_signed_zero_constant_sparse_and_invalid_values() {
    for center in [-5.0, 0.0, 7.0] {
        let mut values = vec![Coordinate::Scalar(center); 400];
        values[0] = Coordinate::Scalar(f64::NAN);
        values[1] = Coordinate::Scalar(f64::INFINITY);
        values[2] = Coordinate::Scalar(-1e9);
        values[3] = Coordinate::Scalar(1e9);
        let blocks = value_blocks(&values, 31);
        let band = bounds::value_band(&blocks, None, |_| false).unwrap();
        assert_eq!(band.quantiles.map(|v| v + band.origin.value()), [center; 3]);
    }
    assert!(
        bounds::value_band(
            &value_blocks(&[Coordinate::Scalar(0.0); 23], 7),
            None,
            |_| false
        )
        .is_none()
    );
    assert!(
        bounds::value_band(
            &value_blocks(&[Coordinate::Scalar(f64::NAN); 100], 7),
            None,
            |_| false
        )
        .is_none()
    );
}

#[test]
fn value_band_preserves_large_integer_differences_and_samples_periodic_motion() {
    let origin = 1_i64 << 60;
    let values: Vec<_> = (0..40_960)
        .map(|i| Coordinate::Integer(origin + i % 20))
        .collect();
    let band = bounds::value_band(&value_blocks(&values, 8192), None, |_| false).unwrap();
    assert_eq!(
        band.origin.relative_to(Coordinate::Integer(origin)) + band.quantiles[0],
        0.0
    );
    assert_eq!(
        band.origin.relative_to(Coordinate::Integer(origin)) + band.quantiles[2],
        19.0
    );
    // A stride of 20 would only see the periodic spike.
    let values: Vec<_> = (0..204_800)
        .map(|i| {
            Coordinate::Scalar(if i % 100 == 0 {
                1e6
            } else {
                (i % 7) as f64 - 3.0
            })
        })
        .collect();
    let band = bounds::value_band(&value_blocks(&values, 8192), None, |_| false).unwrap();
    let [low, _, high] = band.quantiles.map(|v| v + band.origin.value());
    assert_eq!([low, high], [-3.0, 3.0]);
}

#[test]
fn value_band_follow_uses_current_window_even_with_unordered_raw_times() {
    let values: Vec<_> = (0..10_000)
        .map(|i| Coordinate::Integer(if i < 9000 { 5000 } else { -2 }))
        .collect();
    let mut blocks = value_blocks(&values, 511);
    for block in &mut blocks {
        Arc::make_mut(block).samples.reverse();
    }
    let band = bounds::value_band(&blocks, Some((Coordinate::Scalar(9500.0), 499.0)), |_| {
        false
    })
    .unwrap();
    assert_eq!(band.quantiles.map(|v| v + band.origin.value()), [-2.0; 3]);
    assert!(
        bounds::value_band(&blocks, Some((Coordinate::Scalar(20_000.0), 100.0)), |_| {
            false
        })
        .is_none()
    );
}

#[test]
fn narrow_ordered_windows_sample_the_window_not_the_whole_boundary_block() {
    let mut values = vec![Coordinate::Integer(5000); 8192];
    values[8092..].fill(Coordinate::Integer(2));
    values[8150] = Coordinate::Integer(1_000_000);
    let blocks = value_blocks(&values, 8192);
    let band =
        bounds::value_band(&blocks, Some((Coordinate::Scalar(8092.0), 99.0)), |_| true).unwrap();
    assert_eq!(band.quantiles.map(|v| v + band.origin.value()), [2.0; 3]);
    assert!(
        bounds::value_band(&blocks, Some((Coordinate::Scalar(8160.0), 31.0)), |_| true,).is_some()
    );
    assert!(
        bounds::value_band(&blocks, Some((Coordinate::Scalar(8170.0), 21.0)), |_| true,).is_none()
    );
}
