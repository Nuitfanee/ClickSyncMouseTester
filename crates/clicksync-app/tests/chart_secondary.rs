use clicksync_app::{
    backend::{CapturePhase, SessionPrefix},
    chart::{ChartRequest, ChartResult, ChartWorker, ViewMode},
};
use clicksync_chart::{Axis, ChartParameters, Metric, gpu::PlotKind};
use clicksync_core::{RawMouseMovementMode, RawMouseRecord, Session, SessionStatus};
use std::{
    sync::Arc,
    thread,
    time::{Duration, Instant},
};

fn answer(worker: &ChartWorker, id: u64) -> Arc<ChartResult> {
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        if let Some(result) = worker.take_result() {
            let result = result.unwrap();
            assert_eq!(result.request.id, id);
            return result;
        }
        assert!(Instant::now() < deadline);
        thread::sleep(Duration::from_millis(1));
    }
}

fn append(session: &mut Session, start: u64, end: u64) {
    for i in start..end {
        session
            .append(RawMouseRecord::new(
                (i % 7) as i32,
                (i % 11) as i32 + 20,
                100 + i * 1250,
                i + 1,
                i + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
}

fn prefix(session: &mut Session) -> Arc<SessionPrefix> {
    Arc::new(SessionPrefix {
        generation: 1,
        received_sequence: session.event_count() as u64,
        received_records: session.event_count() as u64,
        phase: CapturePhase::Collecting,
        complete: true,
        datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
    })
}

#[test]
fn dual_metric_extensions_share_sealed_geometry_and_instances() {
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    session.begin_collecting(100, true, false).unwrap();
    append(&mut session, 0, 9000);
    let worker = ChartWorker::spawn();
    let mut request = ChartRequest {
        id: 1,
        source_version: 0,
        prefix: prefix(&mut session),
        parameters: ChartParameters::new(Metric::Count(Axis::X)),
        dataset_cpi: Vec::new(),
        secondary_metric: Some(Metric::Count(Axis::Y)),
        kind: PlotKind::Lines,
        view: ViewMode::FullSession,
        analysis: None,
    };
    worker.submit(request.clone()).unwrap();
    let before = answer(&worker, 1);
    append(&mut session, 9000, 10_000);
    request.id = 2;
    request.prefix = prefix(&mut session);
    worker.submit(request.clone()).unwrap();
    let after = answer(&worker, 2);
    assert_eq!(after.datasets[0].bounds_work.reused_blocks, 2);
    assert!(after.datasets[0].bounds_work.scanned_instances < 4000);
    for index in [0, 2] {
        assert!(
            Arc::ptr_eq(
                &before.datasets[0].geometry[index],
                &after.datasets[0].geometry[index]
            ),
            "sealed metric geometry was copied"
        );
        assert!(
            before.datasets[0].scene.blocks[index]
                .instances
                .shares_storage_with(&after.datasets[0].scene.blocks[index].instances),
            "sealed metric drawing instances were rebuilt"
        );
    }
    let secondary = after.datasets[0].geometry[2].samples[123];
    assert_eq!(
        after.datasets[0]
            .hit_index
            .nearest(secondary.point, [0.0, 0.0], [100.0, 100.0],),
        Some(secondary)
    );
    assert_eq!(after.export_events(1).unwrap().count(), 10_000);
    append(&mut session, 10_000, 17_000);
    request.id = 3;
    request.prefix = prefix(&mut session);
    worker.submit(request).unwrap();
    let extended = answer(&worker, 3);
    assert!(Arc::ptr_eq(
        &after.datasets[0].geometry[2],
        &extended.datasets[0].geometry[3]
    ));
    assert_eq!(
        extended.datasets[0]
            .hit_index
            .nearest(secondary.point, [0.0, 0.0], [100.0, 100.0],),
        Some(secondary)
    );
}

#[test]
fn incomplete_prefix_can_finish_draining_before_chart_finalization() {
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    session.begin_collecting(100, true, false).unwrap();
    append(&mut session, 0, 20);
    let worker = ChartWorker::spawn();
    let mut input = prefix(&mut session);
    Arc::get_mut(&mut input).unwrap().phase = CapturePhase::Faulted;
    Arc::get_mut(&mut input).unwrap().complete = false;
    let mut request = ChartRequest {
        id: 1,
        source_version: 0,
        prefix: input,
        parameters: ChartParameters::new(Metric::Count(Axis::X)),
        dataset_cpi: Vec::new(),
        secondary_metric: Some(Metric::Count(Axis::Y)),
        kind: PlotKind::Lines,
        view: ViewMode::FullSession,
        analysis: None,
    };
    worker.submit(request.clone()).unwrap();
    assert!(!answer(&worker, 1).datasets[0].scene.state.finished);
    append(&mut session, 20, 40);
    session.stop();
    let mut final_input = prefix(&mut session);
    Arc::get_mut(&mut final_input).unwrap().phase = CapturePhase::Faulted;
    Arc::get_mut(&mut final_input).unwrap().complete = false;
    request.id = 2;
    request.prefix = final_input;
    worker.submit(request).unwrap();
    let result = answer(&worker, 2);
    assert!(result.datasets[0].scene.state.finished);
    assert_eq!(result.datasets[0].scene.state.processed_end, 40);
    assert!(!result.request.prefix.complete);
}

#[test]
fn stopped_empty_datasets_do_not_block_chart_completion() {
    for phase in [CapturePhase::Stopped, CapturePhase::Faulted] {
        for populated in [false, true] {
            let mut empty = Session::default_with_metadata(Default::default()).unwrap();
            empty.begin_collecting(100, true, false).unwrap();
            empty.stop();
            assert_eq!(empty.status(), SessionStatus::Stopped);
            assert!(!empty.finalized());
            let mut datasets = vec![(1, Arc::new(empty.snapshot().unwrap()))];
            if populated {
                let mut session = Session::default_with_metadata(Default::default()).unwrap();
                session.begin_collecting(100, true, false).unwrap();
                append(&mut session, 0, 40);
                session.stop();
                datasets.push((2, Arc::new(session.snapshot().unwrap())));
            }
            let count = if populated { 40 } else { 0 };
            let worker = ChartWorker::spawn();
            worker
                .submit(ChartRequest {
                    id: 1,
                    source_version: 0,
                    prefix: Arc::new(SessionPrefix {
                        generation: 1,
                        received_sequence: count,
                        received_records: count,
                        phase,
                        complete: phase == CapturePhase::Stopped,
                        datasets,
                    }),
                    parameters: ChartParameters::new(Metric::Count(Axis::X)),
                    dataset_cpi: Vec::new(),
                    secondary_metric: Some(Metric::Count(Axis::Y)),
                    kind: PlotKind::Lines,
                    view: ViewMode::FullSession,
                    analysis: None,
                })
                .unwrap();
            let result = answer(&worker, 1);
            assert!(result.datasets.iter().all(|d| d.scene.state.finished));
            assert_eq!(result.datasets[0].scene.state.processed_end, 0);
            assert_eq!(result.export_events(1).unwrap().count(), 0);
            assert!(!result.snapshot(1).unwrap().finalized());
            if populated {
                assert_eq!(result.datasets[1].scene.state.processed_end, 40);
                assert_eq!(result.export_events(2).unwrap().count(), 40);
            }
        }
    }
}
