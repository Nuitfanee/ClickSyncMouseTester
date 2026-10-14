use clicksync_app::{
    backend::{Backend, CapturePhase, SessionPrefix},
    chart::{ChartRequest, ChartResult, ChartWorker, ViewMode, stop_and_freeze},
};
use clicksync_chart::{
    Axis, ChartParameters, Integrity, Metric,
    gpu::{PlotKind, Viewport},
};
use clicksync_core::protocol::{CaptureSource, ControlCommand};
use serde_json::json;
use std::{
    fs,
    path::PathBuf,
    sync::Arc,
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
const TIMEOUT: Duration = Duration::from_secs(20);

fn wait(mut condition: impl FnMut() -> bool) {
    let start = Instant::now();
    while !condition() {
        assert!(start.elapsed() < TIMEOUT, "live chart deadline exceeded");
        thread::sleep(Duration::from_millis(2));
    }
}

fn request(id: u64, prefix: Arc<SessionPrefix>, metric: Metric, view: ViewMode) -> ChartRequest {
    ChartRequest {
        id,
        source_version: 0,
        secondary_metric: None,
        dataset_cpi: Vec::new(),
        prefix,
        parameters: ChartParameters::new(metric),
        kind: PlotKind::Lines,
        view,
        analysis: None,
    }
}

fn answer(worker: &ChartWorker, id: u64) -> Arc<ChartResult> {
    let mut result = None;
    wait(|| {
        if let Some(answer) = worker.take_result() {
            let answer = answer.unwrap();
            if answer.request.id == id {
                result = Some(answer);
            }
        }
        result.is_some()
    });
    result.unwrap()
}

#[test]
fn actual_capture_plots_live_and_finalizes_through_pause_resume_and_reset() {
    let directory = std::env::var_os("CLICKSYNC_CHART_LIVE_REPORT_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("../../artifacts/ChartLive/runs")
                .join(
                    SystemTime::now()
                        .duration_since(UNIX_EPOCH)
                        .unwrap()
                        .as_millis()
                        .to_string(),
                )
        });
    fs::create_dir_all(&directory).unwrap();
    fs::write(
        directory.join("live.json"),
        r#"{"passed":false,"status":"started"}"#,
    )
    .unwrap();
    let backend = Backend::spawn_in_process(true).unwrap();
    backend
        .execute(
            ControlCommand::Start {
                generation: 10,
                device_keys: vec![1, 2],
                start_fresh: true,
                source: CaptureSource::Replay {
                    records_per_device: 1000,
                    reports_per_second: 1000,
                    real_time: true,
                },
            },
            TIMEOUT,
        )
        .unwrap();
    let worker = ChartWorker::spawn();
    let mut id = 1;
    let mut observed = Vec::new();
    let mut collecting_chart = None;
    wait(|| {
        if let Some(prefix) = backend.latest_prefix()
            && prefix.received_sequence > observed.last().copied().unwrap_or(0)
        {
            assert!(prefix.complete);
            assert!(prefix.datasets.iter().all(|(_, snapshot)| {
                snapshot.integrity() == clicksync_core::session::CaptureIntegrity::Complete
            }));
            worker
                .submit(request(
                    id,
                    prefix.clone(),
                    Metric::CountTrend(Axis::X),
                    ViewMode::Follow { window_ms: 100.0 },
                ))
                .unwrap();
            id += 1;
            observed.push(prefix.received_sequence);
        }
        if let Some(result) = worker.take_result() {
            let result = result.unwrap();
            assert_eq!(result.request.prefix.phase, CapturePhase::Collecting);
            assert!(result.datasets.iter().all(|d| !d.scene.state.finished));
            assert!(
                result
                    .datasets
                    .iter()
                    .any(|d| d.scene.state.processed_end > 0)
            );
            collecting_chart = Some(result);
        }
        backend.state().received_records >= 400 && collecting_chart.is_some()
    });
    let collecting_chart = collecting_chart.unwrap();
    assert_eq!(backend.state().phase, CapturePhase::Collecting);
    backend.execute(ControlCommand::Pause, TIMEOUT).unwrap();
    wait(|| backend.state().phase == CapturePhase::Paused);
    let paused = backend.capture_prefix(TIMEOUT).unwrap();
    assert!(paused.complete);
    assert_eq!(
        paused.received_records,
        paused
            .datasets
            .iter()
            .map(|(_, snapshot)| snapshot.event_count() as u64)
            .sum::<u64>()
    );
    let paused_request = request(
        id,
        paused.clone(),
        Metric::CountTrend(Axis::X),
        ViewMode::FullSession,
    );
    worker.submit(paused_request).unwrap();
    let paused_chart = answer(&worker, id);
    id += 1;
    assert_eq!(paused_chart.request.prefix.phase, CapturePhase::Paused);
    for dataset in &paused_chart.datasets {
        assert!(!dataset.scene.state.finished);
        assert_eq!(
            dataset.scene.state.processed_end,
            paused_chart
                .snapshot(dataset.device_key)
                .unwrap()
                .event_count()
        );
    }
    let stable_export: Vec<_> = paused_chart.export_events(1).unwrap().collect();
    backend.execute(ControlCommand::Resume, TIMEOUT).unwrap();
    wait(|| backend.state().received_records >= paused.received_records + 400);
    let final_prefix = stop_and_freeze(&backend, TIMEOUT).unwrap();
    assert!(final_prefix.complete);
    assert!(final_prefix.datasets.iter().all(|(_, snapshot)| {
        snapshot.integrity() == clicksync_core::session::CaptureIntegrity::Complete
            && snapshot.capture_evidence().queued == Some(final_prefix.received_sequence)
    }));
    let final_request = request(
        id,
        final_prefix.clone(),
        Metric::CountTrend(Axis::X),
        ViewMode::FullSession,
    );
    worker.submit(final_request.clone()).unwrap();
    let live = answer(&worker, id);
    id += 1;
    let reference_worker = ChartWorker::spawn();
    reference_worker.submit(final_request.clone()).unwrap();
    let reference = answer(&reference_worker, final_request.id);
    assert!(live.eligible_for(&final_request));
    assert_eq!(live.datasets.len(), reference.datasets.len());
    for (a, b) in live.datasets.iter().zip(&reference.datasets) {
        assert!(a.scene.state.finished);
        assert_eq!(a.scene.state.data_version, b.scene.state.data_version);
        assert_eq!(
            a.scene
                .blocks
                .iter()
                .flat_map(|b| b.instances.iter())
                .collect::<Vec<_>>(),
            b.scene
                .blocks
                .iter()
                .flat_map(|b| b.instances.iter())
                .collect::<Vec<_>>()
        );
        let range = 0..a.scene.state.processed_end;
        assert_eq!(
            live.query(a.device_key, range.clone()).unwrap(),
            reference.query(b.device_key, range).unwrap()
        );
        assert_eq!(
            live.export_events(a.device_key)
                .unwrap()
                .collect::<Vec<_>>(),
            reference
                .export_events(b.device_key)
                .unwrap()
                .collect::<Vec<_>>()
        );
    }
    assert_eq!(
        paused_chart.export_events(1).unwrap().collect::<Vec<_>>(),
        stable_export
    );
    let fixed = ViewMode::Fixed(Viewport {
        origin: live.viewport(1).unwrap().origin,
        width: 30.0,
        height: 50.0,
    });
    let target = request(id, final_prefix.clone(), Metric::CountTrend(Axis::X), fixed);
    assert!(!live.eligible_for(&target));
    worker.submit(target).unwrap();
    let historical = answer(&worker, id);
    id += 1;
    assert_eq!(historical.viewport(1).unwrap().width, 30.0);
    assert_eq!(historical.datasets[0].work.new_record_reads, 0);
    let report = worker.report();
    assert!(report.active_requests <= 1 && report.pending_requests <= 1);
    backend
        .execute(ControlCommand::Reset { generation: 11 }, TIMEOUT)
        .unwrap();
    wait(|| backend.state().generation == 11 && backend.state().phase == CapturePhase::Empty);
    backend
        .execute(
            ControlCommand::Start {
                generation: 12,
                device_keys: vec![1],
                start_fresh: true,
                source: CaptureSource::Replay {
                    records_per_device: 20,
                    reports_per_second: 8000,
                    real_time: true,
                },
            },
            TIMEOUT,
        )
        .unwrap();
    wait(|| backend.state().received_records == 20);
    let new_prefix = stop_and_freeze(&backend, TIMEOUT).unwrap();
    let new_target = request(
        id,
        new_prefix,
        Metric::CountTrend(Axis::X),
        ViewMode::FullSession,
    );
    assert!(!live.eligible_for(&new_target));
    worker.submit(new_target).unwrap();
    let new_chart = answer(&worker, id);
    assert_eq!(new_chart.request.prefix.generation, 12);
    assert_eq!(new_chart.datasets.len(), 1);
    assert!(
        worker
            .submit(request(
                id + 1,
                final_prefix,
                Metric::CountTrend(Axis::X),
                ViewMode::FullSession
            ))
            .is_err()
    );
    fs::write(directory.join("live.json"), serde_json::to_vec_pretty(&json!({
        "passed": true, "source": "in_process_capture_paced_replay",
        "observed_prefixes": observed, "final_receive_sequence": live.request.prefix.received_sequence,
        "datasets": live.datasets.iter().map(|d| json!({"device_key": d.device_key, "processed_end": d.scene.state.processed_end})).collect::<Vec<_>>(),
        "scheduler": report, "live_before_pause": true, "paused_chart_visible": true,
        "live_receive_sequence": collecting_chart.request.prefix.received_sequence,
        "final_prefix_matches_incremental": true, "pause_resume": true,
        "fixed_history": true, "follow": true, "full_session": true,
        "old_generation_rejected": true, "frozen_export": true,
    })).unwrap()).unwrap();
}

#[test]
fn incomplete_prefix_is_visible_and_merging_remains_bounded() {
    let mut session = clicksync_core::Session::default_with_metadata(Default::default()).unwrap();
    session.begin_collecting(100, true, true).unwrap();
    for i in 0..1000 {
        session
            .append(clicksync_core::RawMouseRecord::new(
                1,
                -1,
                100 + i,
                i + 1,
                i + 1,
                clicksync_core::RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    session.stop();
    let prefix = Arc::new(SessionPrefix {
        generation: 1,
        received_sequence: 1000,
        received_records: 1000,
        phase: CapturePhase::Faulted,
        complete: false,
        datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
    });
    let worker = ChartWorker::spawn();
    for id in 1..=100 {
        worker
            .submit(request(
                id,
                prefix.clone(),
                Metric::Trajectory,
                ViewMode::FullSession,
            ))
            .unwrap();
        let report = worker.report();
        assert!(report.active_requests <= 1 && report.pending_requests <= 1);
    }
    let result = answer(&worker, 100);
    assert_eq!(
        result.datasets[0]
            .scene
            .state
            .data_version
            .unwrap()
            .integrity,
        Integrity::Incomplete
    );
    assert!(result.datasets[0].scene.state.finished);
    assert!(worker.report().merged_targets > 0);
}
