use super::*;
use clicksync_chart::{Axis, Metric};
use clicksync_core::{RawMouseMovementMode, RawMouseRecord, Session};

fn input(count: usize, phase: CapturePhase) -> (Session, Arc<SessionPrefix>) {
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    session.begin_collecting(100, true, false).unwrap();
    for index in 0..count {
        session
            .append(RawMouseRecord::new(
                (index % 7) as i32 - 3,
                (index % 5) as i32 - 2,
                100 + index as u64 * 1250,
                index as u64 + 1,
                index as u64 + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    if phase == CapturePhase::Paused {
        session.pause();
    }
    let prefix = Arc::new(SessionPrefix {
        generation: 1,
        received_sequence: count as u64,
        received_records: count as u64,
        phase,
        complete: true,
        datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
    });
    (session, prefix)
}

fn request(slot: usize, prefix: Arc<SessionPrefix>, id: u64) -> ChartRequest {
    ChartRequest {
        id,
        source_version: 1,
        prefix,
        parameters: ChartParameters::new(
            [
                Metric::Trajectory,
                Metric::PathSpeed,
                Metric::Interval,
                Metric::Count(Axis::X),
            ][slot],
        ),
        dataset_cpi: vec![(1, Some(800.0))],
        secondary_metric: (slot == 3).then_some(Metric::Count(Axis::Y)),
        kind: if slot == 0 {
            PlotKind::Trajectory
        } else {
            PlotKind::Lines
        },
        view: ViewMode::FullSession,
        analysis: None,
    }
}

#[test]
fn integrity_change_rejects_old_presentation_without_recomputing_history() {
    let input = input(256, CapturePhase::Collecting).1;
    let mut trial = trial::CooperativeTrial::default();
    let first = request(0, input.clone(), 1);
    trial.submit(0, first.clone()).unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    let complete = loop {
        assert!(Instant::now() < deadline);
        if let Some((_, result)) = trial.step().unwrap() {
            break result;
        }
    };
    let next = request(
        0,
        Arc::new(SessionPrefix {
            complete: false,
            ..(*input).clone()
        }),
        2,
    );
    assert!(!complete.eligible_for(&next));
    trial.submit(0, next.clone()).unwrap();
    let incomplete = loop {
        assert!(Instant::now() < deadline);
        if let Some((_, result)) = trial.step().unwrap() {
            break result;
        }
    };
    assert!(incomplete.eligible_for(&next));
    assert!(!incomplete.eligible_for(&first));
    assert_eq!(incomplete.datasets[0].work.new_record_reads, 0);
    assert_eq!(incomplete.datasets[0].work.geometry_samples_written, 0);
    assert!(Arc::ptr_eq(
        &complete.datasets[0].geometry[0],
        &incomplete.datasets[0].geometry[0]
    ));
}

#[test]
fn production_worker_fairly_completes_four_demands_with_a_slow_view_and_newer_prefix() {
    let input = input(70_000, CapturePhase::Collecting).1;
    let mut worker = ChartWorker::spawn();
    worker.set_view_quota(2, 256);
    for slot in 0..4 {
        worker
            .submit_view(7, slot, request(slot, input.clone(), 1))
            .unwrap();
    }
    assert!(
        worker
            .submit_view(7, 4, request(0, input.clone(), 2))
            .is_err()
    );
    let deadline = Instant::now() + Duration::from_secs(10);
    let mut seen = [false; 4];
    let mut updates = 1;
    while !seen.iter().all(|seen| *seen) {
        assert!(Instant::now() < deadline, "one production view starved");
        if updates < 100 {
            updates += 1;
            worker
                .submit_view(7, 0, request(0, input.clone(), updates))
                .unwrap();
        }
        for (slot, seen) in seen.iter_mut().enumerate() {
            if let Some((owner, answer)) = worker.take_view_completion(slot) {
                let result = answer.unwrap();
                assert_eq!(owner, 7);
                assert!(Arc::ptr_eq(&result.request.prefix, &input));
                assert_eq!(result.datasets[0].scene.state.processed_end, 70_000);
                *seen = true;
            }
        }
        std::thread::sleep(Duration::from_millis(1));
    }
    let report = worker.report();
    assert!(report.view_completions.iter().all(|count| *count > 0));
    assert!(report.view_steps[2] > report.view_steps[1]);
    assert!(report.active_requests <= 1 && report.pending_requests <= 4);
    for slot in 0..4 {
        worker.remove_view(slot);
    }
    worker.shutdown();
}

#[test]
fn replacing_and_removing_a_view_rejects_its_late_result_and_preserves_other_receivers() {
    let input = input(25_000, CapturePhase::Collecting).1;
    let mut worker = ChartWorker::spawn();
    worker.set_view_quota(1, 256);
    worker
        .submit_view(1, 1, request(1, input.clone(), 1))
        .unwrap();
    worker
        .submit_view(1, 2, request(2, input.clone(), 1))
        .unwrap();
    worker.remove_view(1);
    let mut next = request(3, input, 2);
    next.source_version = 2;
    worker.submit_view(2, 1, next).unwrap();
    let deadline = Instant::now() + Duration::from_secs(10);
    let mut seen = [false; 2];
    while !seen.iter().all(|seen| *seen) {
        assert!(Instant::now() < deadline);
        for (offset, slot) in [1, 2].into_iter().enumerate() {
            if let Some((owner, answer)) = worker.take_view_completion(slot) {
                let result = answer.unwrap();
                assert_eq!(owner, if slot == 1 { 2 } else { 1 });
                if slot == 1 {
                    assert_eq!(result.request.source_version, 2);
                    assert_eq!(result.request.parameters.metric, Metric::Count(Axis::X));
                }
                seen[offset] = true;
            }
        }
        std::thread::sleep(Duration::from_millis(1));
    }
    worker.shutdown();
}

#[test]
fn fixed_statistics_match_exact_query_and_follow_range_changes() {
    use crate::product::performance_analysis::{PlotAnalysis, PlotOptions, filter_query};
    let (mut session, prefix) = input(1024, CapturePhase::Paused);
    let mut trial = trial::CooperativeTrial::default();
    for (id, range, stems) in [
        (1, None, false),
        (2, Some((12.0, 42.0)), false),
        (3, Some((12.0, 42.0)), true),
    ] {
        let mut target = request(3, prefix.clone(), id);
        target.analysis = Some(PlotAnalysis {
            range,
            options: PlotOptions {
                stems,
                ..Default::default()
            },
        });
        trial.submit(3, target).unwrap();
        let result = (0..100)
            .find_map(|_| trial.step().unwrap().map(|(_, result)| result))
            .unwrap();
        let mut query = result.query(1, 0..1024).unwrap();
        filter_query(
            &mut query,
            range,
            result.request.parameters.time_basis,
            prefix.datasets[0].1.timestamp_frequency(),
        );
        let expected =
            clicksync_chart::analysis::statistics::summarize(&query, &result.request.parameters)
                .unwrap();
        let actual = result.datasets[0].distribution.as_ref().unwrap();
        assert_eq!(
            serde_json::to_value(actual).unwrap(),
            serde_json::to_value(expected).unwrap()
        );
        if id == 3 {
            assert_eq!(result.datasets[0].work.new_record_reads, 0);
        }
    }
    session.begin_collecting(1_300_000, false, false).unwrap();
    let live = request(
        3,
        Arc::new(SessionPrefix {
            phase: CapturePhase::Collecting,
            datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
            ..(*prefix).clone()
        }),
        4,
    );
    trial.submit(3, live).unwrap();
    let result = (0..100)
        .find_map(|_| trial.step().unwrap().map(|(_, result)| result))
        .unwrap();
    assert!(result.datasets[0].distribution.is_none());
}
