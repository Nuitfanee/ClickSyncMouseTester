//! Repeatable CPU measurements; not a hardware capture or long-stability gate.
use clicksync_chart::{
    ChartState, Coordinate, DataVersion, DatasetId, Integrity, Point,
    analysis::calibration::CalibrationEngine,
    gpu::{BlockKey, Instance, PreparedBlock, PreparedScene},
};
use std::{hint::black_box, sync::Arc, time::Instant};

fn measure(mut operation: impl FnMut(), repeats: usize) -> u128 {
    operation();
    let mut times = Vec::with_capacity(repeats);
    for _ in 0..repeats {
        let started = Instant::now();
        operation();
        times.push(started.elapsed().as_nanos());
    }
    times.sort_unstable();
    times[times.len() / 2]
}

fn main() {
    let mut results = Vec::new();
    for count in [100_000, 1_000_000] {
        let scene = PreparedScene {
            state: ChartState {
                processed_end: count,
                revisable_from: count,
                published_end: count,
                finished: true,
                data_version: Some(DataVersion {
                    dataset: DatasetId(1),
                    generation: 1,
                    end: count,
                    session_revision: 1,
                    timestamp_frequency: 10_000_000,
                    status: clicksync_core::SessionStatus::Stopped,
                    source_finalized: true,
                    integrity: Integrity::Complete,
                }),
                parameter_version: 1,
                content_version: 1,
            },
            blocks: (0..count.div_ceil(8192))
                .map(|id| {
                    Arc::new(PreparedBlock {
                        key: BlockKey {
                            dataset: 1,
                            generation: 1,
                            parameters: 1,
                            kind: 0,
                            id,
                        },
                        content_version: 1,
                        origin: Point {
                            x: Coordinate::Integer(0),
                            y: Coordinate::Integer(0),
                        },
                        instances: vec![Instance::point(1.0, 2.0); (count - id * 8192).min(8192)]
                            .into(),
                    })
                })
                .collect(),
        };
        let mut calibration = CalibrationEngine::default();
        calibration.set_locked(true);
        for index in 0..count {
            calibration.push(1, 0, index as f64);
        }
        let validation_ns = measure(|| black_box(&scene).validate().unwrap(), 31);
        let trace_ns = measure(
            || {
                black_box(calibration.frame(count as f64));
            },
            31,
        );
        results.push(serde_json::json!({
            "records":count,
            "blocks":scene.blocks.len(),
            "validation_median_ns":validation_ns,
            "calibration_frame_median_ns":trace_ns
        }));
    }
    let mut lookups = Vec::new();
    for count in [1000_u32, 10_000] {
        let mut session =
            clicksync_core::Session::default_with_metadata(Default::default()).unwrap();
        for id in 1..=count {
            session.begin_collecting(id as u64, false, false).unwrap();
            session.pause();
        }
        let snapshot = session.snapshot().unwrap();
        let linear_ns = measure(
            || {
                for id in 1..=count {
                    black_box(
                        black_box(&snapshot)
                            .segments()
                            .iter()
                            .find(|s| s.segment_id == id),
                    );
                }
            },
            7,
        );
        let indexed_ns = measure(
            || {
                for id in 1..=count {
                    black_box(black_box(&snapshot).segment(id));
                }
            },
            7,
        );
        lookups.push(serde_json::json!({
            "segments":count,
            "linear_median_ns":linear_ns,
            "indexed_median_ns":indexed_ns
        }));
    }
    println!(
        "{}",
        serde_json::json!({"repeats":31,"workloads":results,"segment_lookup_repeats":7,"segment_lookups":lookups})
    );
}
