use clicksync_app::{
    backend::{Backend, CapturePhase},
    chart::{ChartRequest, ChartWorker, ViewMode, stop_and_freeze},
};
use clicksync_chart::{
    ChartParameters, Coordinate, DatasetId, FixedPrefix, Integrity, Metric, Point,
    gpu::{
        PlotKind, RenderSpec, Renderer, ResourceLimits, Viewport, create_offscreen_device,
        device_identity,
    },
    hlsl,
};
use clicksync_core::protocol::{CaptureSource, ControlCommand};
use serde_json::json;
use std::{
    fs,
    io::BufWriter,
    path::PathBuf,
    sync::Arc,
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

const TIMEOUT: Duration = Duration::from_secs(20);

fn wait(mut condition: impl FnMut() -> bool) {
    let start = Instant::now();
    while !condition() {
        assert!(start.elapsed() < TIMEOUT, "chart/capture deadline exceeded");
        thread::sleep(Duration::from_millis(2));
    }
}

#[test]
fn actual_capture_drains_both_datasets_then_publishes_fixed_gpu_query_and_export_versions() {
    let directory = std::env::var_os("CLICKSYNC_CHART_FINALIZATION_REPORT_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("../../artifacts/ChartFinalization/runs")
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
        directory.join("finalization.json"),
        r#"{"passed":false,"status":"started"}"#,
    )
    .unwrap();
    let backend = Backend::spawn_in_process(true).unwrap();
    backend
        .execute(
            ControlCommand::Start {
                generation: 71,
                device_keys: vec![1, 2],
                source: CaptureSource::Replay {
                    records_per_device: 900,
                    reports_per_second: 8000,
                    real_time: true,
                },
                start_fresh: true,
            },
            TIMEOUT,
        )
        .unwrap();
    wait(|| backend.state().received_records == 1800);
    let prefix = stop_and_freeze(&backend, TIMEOUT).unwrap();
    assert_eq!(prefix.phase, CapturePhase::Stopped);
    assert_eq!(prefix.received_sequence, 1800);
    assert_eq!(prefix.datasets.len(), 2);
    assert!(prefix.complete);
    for (_, snapshot) in &prefix.datasets {
        assert_eq!(snapshot.event_count(), 900);
        assert!(snapshot.finalized());
    }
    let worker = ChartWorker::spawn();
    worker
        .submit(ChartRequest {
            id: 1,
            source_version: 0,
            secondary_metric: None,
            dataset_cpi: Vec::new(),
            prefix: prefix.clone(),
            parameters: ChartParameters::new(Metric::Trajectory),
            kind: PlotKind::Trajectory,
            view: ViewMode::FullSession,
            analysis: None,
        })
        .unwrap();
    let analysis_started = Instant::now();
    let result = loop {
        if let Some(result) = worker.take_result() {
            break result.unwrap();
        }
        assert!(
            analysis_started.elapsed() < TIMEOUT,
            "chart analysis deadline exceeded"
        );
        thread::sleep(Duration::from_millis(2));
    };
    assert_eq!(result.request.prefix.generation, 71);
    let (device, context) = create_offscreen_device().unwrap();
    let identity = device_identity(&device).unwrap();
    let mut renderer = Renderer::new(device, 1, ResourceLimits::default()).unwrap();
    let mut reports = Vec::new();
    for dataset in &result.datasets {
        let version = dataset.scene.state.data_version.unwrap();
        assert_eq!(version.end, 900);
        assert_eq!(version.dataset, DatasetId(dataset.device_key as u64));
        assert_eq!(version.integrity, Integrity::Complete);
        assert!(dataset.scene.state.finished);
        let spec = RenderSpec::new(
            Viewport {
                origin: Point {
                    x: Coordinate::Integer(-20),
                    y: Coordinate::Integer(-20),
                },
                width: 40.0,
                height: 40.0,
            },
            320,
            240,
            1,
        );
        renderer
            .begin(&context, dataset.scene.clone(), spec)
            .unwrap();
        let mut layer = None;
        wait(|| {
            layer = renderer.tick(&context).unwrap();
            layer.is_some()
        });
        let layer = layer.unwrap();
        assert_eq!(layer.version.data, version);
        assert_eq!(layer.version.processed_end, 900);
        assert!(layer.version.finished);
        renderer.begin_readback(&context, layer).unwrap();
        let mut pixels = None;
        wait(|| {
            pixels = renderer.poll_readback(&context).unwrap();
            pixels.is_some()
        });
        let pixels = pixels.unwrap();
        assert!(pixels.bgra.chunks_exact(4).any(|p| p[3] > 0));
        let rgba: Vec<_> = pixels
            .bgra
            .chunks_exact(4)
            .flat_map(|p| [p[2], p[1], p[0], p[3]])
            .collect();
        let path = directory.join(format!("dataset-{}.png", dataset.device_key));
        thread::spawn(move || {
            let mut encoder =
                png::Encoder::new(BufWriter::new(fs::File::create(path).unwrap()), 320, 240);
            encoder.set_color(png::ColorType::Rgba);
            encoder.set_depth(png::BitDepth::Eight);
            encoder
                .write_header()
                .unwrap()
                .write_image_data(&rgba)
                .unwrap();
        })
        .join()
        .unwrap();
        let query = result.query(dataset.device_key, 10..25).unwrap();
        assert_eq!(query.data_version, version);
        let expected = clicksync_chart::query(
            FixedPrefix::new(
                DatasetId(dataset.device_key as u64),
                result.snapshot(dataset.device_key).unwrap(),
            ),
            10..25,
            &result.request.parameters,
        )
        .unwrap();
        assert_eq!(query, expected);
        reports.push(json!({
            "device_key": dataset.device_key, "records": version.end,
            "processed_end": layer_end(&pixels), "image_sha256": hlsl::sha256(&pixels.bgra),
            "query_matches_fixed_prefix": true,
        }));
    }
    let before: Vec<_> = result.export_events(2).unwrap().collect();
    assert_eq!(before.len(), 900);
    backend
        .execute(ControlCommand::Reset { generation: 72 }, TIMEOUT)
        .unwrap();
    wait(|| backend.state().generation == 72 && backend.state().phase == CapturePhase::Empty);
    assert_eq!(result.export_events(2).unwrap().collect::<Vec<_>>(), before);
    assert_eq!(prefix.datasets[0].1.event_count(), 900);
    worker.cancel();
    assert!(
        worker
            .submit(ChartRequest {
                id: 2,
                source_version: 0,
                secondary_metric: None,
                dataset_cpi: Vec::new(),
                prefix: Arc::clone(&prefix),
                parameters: ChartParameters::new(Metric::Trajectory),
                kind: PlotKind::Trajectory,
                view: ViewMode::FullSession,
                analysis: None,
            })
            .is_err()
    );
    assert_eq!(
        backend
            .execute(ControlCommand::Discover, TIMEOUT)
            .unwrap()
            .generation,
        72
    );
    fs::write(directory.join("finalization.json"), serde_json::to_vec_pretty(&json!({
        "passed": true, "source": "in_process_capture_paced_replay",
        "capture_generation": prefix.generation, "received_sequence": prefix.received_sequence,
        "complete": prefix.complete, "datasets": reports, "device": identity,
        "shaders": hlsl::identity(), "resources": renderer.report(),
        "fixed_export_survives_reset": true, "capture_control_independent_of_chart_cancel": true,
    })).unwrap()).unwrap();
}

fn layer_end(pixels: &clicksync_chart::gpu::ReadbackPixels) -> usize {
    pixels.version.published_end
}
