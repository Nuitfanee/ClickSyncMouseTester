use clicksync_app::{
    backend::{CapturePhase, SessionPrefix},
    chart::{ChartRequest, ChartResult, ChartWorker, ViewMode},
    product::{AnalysisRequest, DomainWorker, METRICS, Settings, plot_kind},
    product_files::{FileRequest, FileResult, FileWorker},
};
use clicksync_chart::{
    Axis, ChartParameters, Coordinate, DatasetId, FixedPrefix, Metric, Point,
    analysis::calibration::CalibrationEngine,
};
use clicksync_core::{
    RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SessionMetadata, SharedByteBudget,
};
use serde_json::json;
use std::{
    path::PathBuf,
    sync::Arc,
    thread,
    time::{Duration, Instant},
};

fn wait<T>(mut poll: impl FnMut() -> Option<T>) -> T {
    let started = Instant::now();
    loop {
        if let Some(value) = poll() {
            return value;
        }
        assert!(
            started.elapsed() < Duration::from_secs(30),
            "product worker deadline exceeded"
        );
        thread::sleep(Duration::from_millis(1));
    }
}
fn session() -> Session {
    let config = SessionConfig {
        timestamp_frequency: 1_000_000,
        chunk_capacity: 32,
        max_session_events: 10_000,
        warning_event_threshold: 9_000,
        ..Default::default()
    };
    Session::new(
        config.clone(),
        SharedByteBudget::new(config.managed_memory_budget_bytes),
        SessionMetadata::default(),
    )
    .unwrap()
}
fn fixture() -> (Session, Arc<SessionPrefix>) {
    let mut session = session();
    session.begin_collecting(1_000_000, true, true).unwrap();
    for i in 0..256_u64 {
        if i == 128 {
            session.pause();
            session.begin_collecting(2_000_000, false, true).unwrap();
        }
        let ticks = if i < 128 {
            1_000_000 + i * 1_000
        } else {
            2_000_000 + (i - 128) * 1_000
        };
        session
            .append(RawMouseRecord::new(
                if i % 16 < 8 { 8 } else { -6 },
                (i % 7) as i32 - 3,
                ticks,
                i + 1,
                i + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    session.stop();
    let snapshot = Arc::new(session.snapshot().unwrap());
    let prefix = Arc::new(SessionPrefix {
        generation: 7,
        received_sequence: 512,
        received_records: 512,
        phase: CapturePhase::Stopped,
        complete: true,
        datasets: vec![(1, snapshot.clone()), (2, snapshot)],
    });
    (session, prefix)
}
fn request(
    id: u64,
    source_version: u64,
    prefix: Arc<SessionPrefix>,
    metric: usize,
    histogram: bool,
    basis: bool,
    style: usize,
) -> ChartRequest {
    let settings = Settings::default();
    ChartRequest {
        id,
        source_version,
        prefix,
        parameters: settings.parameters(metric, histogram, basis, id),
        secondary_metric: clicksync_app::product::secondary_metric(metric, histogram),
        dataset_cpi: vec![(1, Some(800.0)), (2, Some(1600.0))],
        kind: plot_kind(metric, style, histogram),
        view: ViewMode::FullSession,
        analysis: None,
    }
}
fn answer(worker: &ChartWorker, id: u64) -> Arc<ChartResult> {
    wait(|| {
        worker.take_result().map(|result| {
            let result = result.unwrap();
            assert_eq!(result.request.id, id);
            result
        })
    })
}

#[test]
fn product_modes_comparison_fixed_exports_queries_and_replacement() {
    let directory = std::env::var_os("CLICKSYNC_PRODUCT_APP_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("../../artifacts/ProductFlow/runs")
                .join(format!(
                    "{}-{}",
                    std::process::id(),
                    std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .unwrap()
                        .as_millis()
                ))
        });
    std::fs::create_dir_all(&directory).unwrap();
    std::fs::write(
        directory.join("report.json"),
        r#"{"passed":false,"status":"started"}"#,
    )
    .unwrap();
    let (mut session, prefix) = fixture();
    let worker = ChartWorker::spawn();
    let mut id = 1;
    for (metric, &(metric_kind, _, _)) in METRICS.iter().enumerate() {
        for basis in [false, true] {
            for histogram in [false, true] {
                for style in 0..if histogram { 1 } else { 3 } {
                    let target = request(id, 0, prefix.clone(), metric, histogram, basis, style);
                    worker.submit(target.clone()).unwrap();
                    let result = answer(&worker, id);
                    assert!(result.eligible_for(&target));
                    if histogram && metric == 17 {
                        assert_eq!(target.parameters.metric, Metric::DeltaMagnitude);
                    }
                    if histogram && metric == 19 {
                        assert_eq!(target.parameters.metric, Metric::VelocityMagnitude);
                    }
                    if histogram && metric == 18 {
                        assert!(target.parameters.histogram.is_none());
                        assert_eq!(target.secondary_metric, Some(Metric::Cumulative(Axis::Y)));
                    }
                    for dataset in &result.datasets {
                        let parameters = target.parameters_for(dataset.device_key);
                        let expected = clicksync_chart::query(
                            FixedPrefix::new(
                                DatasetId(dataset.device_key as u64),
                                result.snapshot(dataset.device_key).unwrap(),
                            ),
                            0..256,
                            &parameters,
                        )
                        .unwrap();
                        assert_eq!(result.query(dataset.device_key, 0..256).unwrap(), expected);
                        assert_eq!(dataset.scene.state.processed_end, 256);
                        assert!(dataset.scene.state.finished);
                    }
                    if histogram && metric_kind == Metric::Trajectory {
                        assert!(target.parameters.histogram.is_none());
                        assert_eq!(target.kind, clicksync_chart::gpu::PlotKind::Trajectory);
                    } else if target.kind == clicksync_chart::gpu::PlotKind::Histogram {
                        assert_eq!(
                            result.datasets[0].histogram.len(),
                            result.datasets[1].histogram.len()
                        );
                        for (a, b) in result.datasets[0]
                            .histogram
                            .iter()
                            .zip(&result.datasets[1].histogram)
                        {
                            assert_eq!((a.minimum, a.maximum), (b.minimum, b.maximum));
                        }
                        for dataset in &result.datasets {
                            let total: f64 = dataset.histogram.iter().map(|bin| bin.value).sum();
                            assert!(
                                (total - 100.0).abs() < 1e-9,
                                "metric {metric}, basis {basis}, histogram total {total}"
                            );
                        }
                    }
                    if metric_kind == Metric::Velocity(Axis::X) {
                        for (a, b) in result
                            .query(1, 0..256)
                            .unwrap()
                            .samples
                            .iter()
                            .zip(&result.query(2, 0..256).unwrap().samples)
                        {
                            assert!((a.point.y.value() - b.point.y.value() * 2.0).abs() < 1e-10);
                        }
                    }
                    id += 1;
                }
            }
        }
    }
    let matrix_requests = id - 1;
    let target = request(id, 0, prefix.clone(), 13, false, false, 0);
    worker.submit(target).unwrap();
    let frozen = answer(&worker, id);
    let viewport = frozen.viewport(1).unwrap();
    let fixed = ViewMode::Fixed(viewport);
    for view in [
        fixed,
        ViewMode::Follow { window_ms: 30.0 },
        ViewMode::FullSession,
    ] {
        id += 1;
        let mut target = frozen.request.clone();
        target.id = id;
        target.view = view;
        worker.submit(target).unwrap();
        let view_result = answer(&worker, id);
        assert_eq!(view_result.datasets[0].work.new_record_reads, 0);
    }
    let files = FileWorker::spawn();
    let before: Vec<_> = frozen.export_events(1).unwrap().collect();
    files
        .submit(FileRequest::Export {
            path: directory.join("fixed.json"),
            snapshot: prefix.datasets[0].1.clone(),
            cpi: Some(800.0),
        })
        .unwrap();
    session.reset().unwrap();
    assert!(matches!(
        wait(|| files.take()).unwrap(),
        FileResult::Saved(_)
    ));
    files
        .submit(FileRequest::ImportAnalysis {
            path: directory.join("fixed.json"),
            operation: 1,
            budget: SharedByteBudget::new(SessionConfig::default().managed_memory_budget_bytes),
            existing: Vec::new(),
        })
        .unwrap();
    let imported = wait(|| files.take()).unwrap();
    if let FileResult::ImportedAnalysis { group, operation } = imported {
        assert_eq!(operation, 1);
        let (snapshot, cpi) = (group.snapshot, group.cpi);
        assert_eq!(cpi, Some(800.0));
        for (actual, expected) in snapshot.events().zip(&before) {
            assert_eq!(
                (
                    actual.raw_delta_x,
                    actual.raw_delta_y,
                    actual.capture_sequence,
                    actual.timing_sequence
                ),
                (
                    expected.raw_delta_x,
                    expected.raw_delta_y,
                    expected.capture_sequence,
                    expected.timing_sequence
                )
            );
            assert_eq!(actual.raw_capture_ticks, expected.raw_capture_ticks * 10);
            assert_eq!(actual.logical_ticks, expected.logical_ticks * 10);
        }
    } else {
        panic!("wrong import result");
    }
    files
        .submit(FileRequest::Query {
            result: frozen.clone(),
            key: 1,
            range: 15..31,
        })
        .unwrap();
    let FileResult::Query(summary) = wait(|| files.take()).unwrap() else {
        panic!("wrong query result");
    };
    assert_eq!(summary.request_id, frozen.request.id);
    assert_eq!(summary.end, 256);
    assert_eq!(summary.record_count, 16);
    // A new source can legitimately reuse lower capture generation/positions.
    id += 1;
    let target = request(id, 1, prefix.clone(), 9, false, false, 0);
    assert!(!frozen.eligible_for(&target));
    worker.submit(target).unwrap();
    let replacement = answer(&worker, id);
    assert_eq!(frozen.export_events(1).unwrap().collect::<Vec<_>>(), before);
    let mut stale = replacement.request.clone();
    stale.id = id + 1;
    stale.source_version = 0;
    assert!(worker.submit(stale).is_err());
    let scheduler = worker.report();
    assert!(scheduler.active_requests <= 1 && scheduler.pending_requests <= 1);
    std::fs::write(
        directory.join("report.json"),
        serde_json::to_vec_pretty(&json!({
            "passed":true, "metric_count":METRICS.len(), "matrix_requests":matrix_requests,
            "basis_count":2, "styles":3, "records_per_dataset":256,
            "independent_cpi":true, "common_histogram_layout":true,
            "fixed_files_queries_survive_reset":true, "source_replacement_rejects_old_result":true,
            "scheduler":scheduler, "query":summary,
        }))
        .unwrap(),
    )
    .unwrap();
}

#[test]
fn single_swipe_worker_finishes_then_reset_and_cancel_keep_attempt_identity() {
    let mut sessions = [session(), session()];
    for session in &mut sessions {
        session.begin_collecting(1_000_000, true, false).unwrap();
    }
    let domains = DomainWorker::spawn();
    let prefix = |sessions: &mut [Session; 2]| {
        Arc::new(SessionPrefix {
            generation: 1,
            received_sequence: sessions.iter().map(|s| s.event_count() as u64).sum(),
            received_records: sessions.iter().map(|s| s.event_count() as u64).sum(),
            phase: CapturePhase::Collecting,
            complete: true,
            datasets: sessions
                .iter_mut()
                .enumerate()
                .map(|(i, s)| (i as u32 + 1, Arc::new(s.snapshot().unwrap())))
                .collect(),
        })
    };
    let mut request = AnalysisRequest {
        prefix: prefix(&mut sessions),
        now: 1000.0,
        locked: false,
        start_round: Some((1, 254, 1000.0)),
        cancelled_round: 0,
        reset: 0,
        sensitivity_reset: 0,
    };
    domains.submit(request.clone());
    assert!(wait(|| domains.take()).sensitivity.active.is_some());
    for i in 0..200 {
        for (side, session) in sessions.iter_mut().enumerate() {
            session
                .append(RawMouseRecord::new(
                    if side == 0 { 10 } else { 5 },
                    0,
                    1_500_000 + i * 10_000,
                    i + 1,
                    i + 1,
                    RawMouseMovementMode::Relative,
                ))
                .unwrap();
        }
        if i % 25 == 24 {
            request.prefix = prefix(&mut sessions);
            request.now = 1500.0 + i as f64 * 10.0;
            domains.submit(request.clone());
            let frame = wait(|| domains.take());
            assert_eq!(frame.sensitivity_round, 1);
            assert_eq!(frame.processed, [i as usize + 1; 2]);
            assert!(frame.sensitivity.result.is_none());
        }
    }
    request.now = 3540.0;
    domains.submit(request.clone());
    let frame = wait(|| domains.take());
    assert!(frame.sensitivity.active.is_none());
    assert_eq!(
        frame
            .sensitivity
            .result
            .as_ref()
            .unwrap()
            .recommended_target_dpi,
        508
    );
    domains.submit(request.clone());
    let repeated = wait(|| domains.take());
    assert_eq!(
        serde_json::to_value(&frame.sensitivity).unwrap(),
        serde_json::to_value(&repeated.sensitivity).unwrap()
    );
    request.sensitivity_reset = 1;
    request.start_round = Some((2, 254, 4000.0));
    request.now = 4000.0;
    domains.submit(request.clone());
    let retry = wait(|| domains.take());
    assert_eq!(retry.sensitivity_round, 2);
    assert!(retry.sensitivity.active.is_some());
    assert!(retry.sensitivity.result.is_none());
    request.cancelled_round = 1;
    request.start_round = None;
    domains.submit(request);
    let cancelled = wait(|| domains.take());
    assert_eq!(cancelled.cancelled_round, 1);
    assert!(cancelled.sensitivity.active.is_none());
    assert!(cancelled.sensitivity.result.is_none());
}

#[test]
fn domain_worker_preserves_incremental_calibration_then_resets() {
    let mut session = session();
    session.begin_collecting(1_000_000, true, false).unwrap();
    let domains = DomainWorker::spawn();
    let mut calibration = CalibrationEngine::default();
    calibration.set_locked(true);
    let mut last = 1000.0;
    for stroke in 0..44 {
        for i in 0..60 {
            let sequence = stroke * 60 + i + 1;
            let direction = if stroke % 2 == 0 { 1 } else { -1 };
            last = 1000.0 + sequence as f64 * 3.0;
            session
                .append(RawMouseRecord::new(
                    10 * direction,
                    direction,
                    (last * 1000.0) as u64,
                    sequence as u64,
                    sequence as u64,
                    RawMouseMovementMode::Relative,
                ))
                .unwrap();
            calibration.push(10 * direction, direction, last);
        }
        let prefix = Arc::new(SessionPrefix {
            generation: 1,
            received_sequence: session.event_count() as u64,
            received_records: session.event_count() as u64,
            phase: CapturePhase::Collecting,
            complete: true,
            datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
        });
        domains.submit(AnalysisRequest {
            prefix,
            now: last,
            locked: true,
            start_round: None,
            cancelled_round: 0,
            reset: 0,
            sensitivity_reset: 0,
        });
        let frame = wait(|| domains.take());
        assert_eq!(frame.processed[0], session.event_count());
        assert_eq!(
            serde_json::to_value(frame.calibration.clone()).unwrap(),
            serde_json::to_value(calibration.frame(last)).unwrap()
        );
    }
    session.pause();
    let paused_prefix = Arc::new(SessionPrefix {
        generation: 1,
        received_sequence: session.event_count() as u64,
        received_records: session.event_count() as u64,
        phase: CapturePhase::Paused,
        complete: true,
        datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
    });
    let pause_request = AnalysisRequest {
        prefix: paused_prefix,
        now: last + 100.0,
        locked: false,
        start_round: None,
        cancelled_round: 0,
        reset: 0,
        sensitivity_reset: 0,
    };
    domains.submit(pause_request.clone());
    let paused = wait(|| domains.take());
    assert_eq!(paused.processed[0], session.event_count());
    let mut later = pause_request;
    later.now += 5000.0;
    domains.submit(later);
    let still_paused = wait(|| domains.take());
    assert_eq!(paused.processed, still_paused.processed);
    assert_eq!(
        paused.calibration.swipe_count,
        still_paused.calibration.swipe_count
    );
    domains.submit(AnalysisRequest {
        prefix: Arc::new(SessionPrefix {
            generation: 1,
            received_sequence: session.event_count() as u64,
            received_records: session.event_count() as u64,
            phase: CapturePhase::Collecting,
            complete: true,
            datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
        }),
        now: last,
        locked: true,
        start_round: None,
        cancelled_round: 0,
        reset: 0,
        sensitivity_reset: 1,
    });
    let sensitivity_reset = wait(|| domains.take());
    assert_eq!(sensitivity_reset.sensitivity_reset, 1);
    assert_eq!(sensitivity_reset.processed[0], session.event_count());
    assert_eq!(
        sensitivity_reset.calibration.swipe_count,
        calibration.frame(last).swipe_count
    );
    assert!(sensitivity_reset.sensitivity.result.is_none());
    session.reset().unwrap();
    domains.submit(AnalysisRequest {
        prefix: Arc::new(SessionPrefix {
            generation: 2,
            received_sequence: 0,
            received_records: 0,
            phase: CapturePhase::Empty,
            complete: true,
            datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
        }),
        now: last,
        locked: false,
        start_round: None,
        cancelled_round: 0,
        reset: 1,
        sensitivity_reset: 1,
    });
    let frame = wait(|| domains.take());
    assert_eq!(frame.reset, 1);
    assert_eq!(frame.processed, [0, 0]);
    assert!(!frame.calibration.has_data);
    assert!(frame.sensitivity.result.is_none());
}

#[test]
fn hit_tree_matches_brute_force_for_precision_duplicates_trajectory_and_anisotropic_viewports() {
    let (_, prefix) = fixture();
    for metric in [Metric::Trajectory, Metric::Count(Axis::X)] {
        let mut engine =
            clicksync_chart::ChartEngine::new(ChartParameters::new(metric), 32).unwrap();
        engine
            .finish(FixedPrefix::new(DatasetId(1), &prefix.datasets[0].1))
            .unwrap();
        let index =
            clicksync_chart::query::hit::HitIndex::prepare(engine.geometry().blocks(), None);
        for scale in [[1.0, 2.0], [100.0, 0.1]] {
            for i in 0..99 {
                let origin = Point {
                    x: Coordinate::Integer(0),
                    y: Coordinate::Integer(0),
                };
                let offset = [i as f64 * 1.37, i as f64 * 0.1 - 5.0];
                let expected = engine
                    .geometry()
                    .samples()
                    .min_by(|a, b| {
                        let distance = |s: clicksync_chart::Sample| {
                            ((s.point.x.relative_to(origin.x) - offset[0]) * scale[0]).powi(2)
                                + ((s.point.y.relative_to(origin.y) - offset[1]) * scale[1]).powi(2)
                        };
                        distance(*a)
                            .total_cmp(&distance(*b))
                            .then(a.source_index.cmp(&b.source_index))
                    })
                    .unwrap();
                assert_eq!(index.nearest(origin, offset, scale), Some(expected));
            }
        }
    }
    // Above 2^53, local integer subtraction still distinguishes neighbouring samples.
    let origin = Point {
        x: Coordinate::Integer((1_i64 << 54) + 7),
        y: Coordinate::Integer(0),
    };
    let points: Vec<_> = (0..96)
        .map(|i| clicksync_chart::Sample {
            source_index: i,
            segment_id: 1,
            point: Point {
                x: Coordinate::Integer((1_i64 << 54) + 7 + i as i64),
                y: Coordinate::Integer(0),
            },
        })
        .collect();
    let block = Arc::new(clicksync_chart::GeometryBlock {
        id: 0,
        content_version: 1,
        source_range: 0..96,
        origin,
        connector: None,
        samples: points,
        relative_points: (0..96).map(|i| [i as f64, 0.0]).collect(),
        bounds: [0.0, 95.0, 0.0, 0.0],
    });
    let tree = clicksync_chart::query::hit::HitIndex::prepare(&[block], None);
    assert_eq!(
        tree.nearest(origin, [17.0, 0.0], [1.0, 1.0])
            .unwrap()
            .source_index,
        17
    );
    assert!(tree.nearest(origin, [f64::NAN, 0.0], [1.0, 1.0]).is_none());
}
