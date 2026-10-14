use clicksync_chart::analysis::sensitivity::{
    Failure, PREPARATION_MS, Packet, SensitivityEngine, Stage,
};

fn push(engine: &mut SensitivityEngine, time: f64, a: i32, b: i32) {
    engine.push(false, Packet { time, dx: a, dy: 0 });
    engine.push(true, Packet { time, dx: b, dy: 0 });
}

#[test]
fn one_ten_cm_swipe_uses_target_distance_and_publishes_result() {
    let mut engine = SensitivityEngine::default();
    assert!(engine.start(254, 10.0, 0.0));
    let state = engine.frame().active.unwrap();
    assert_eq!(state.target_goal, 1000.0);
    assert_eq!(state.stage, Stage::Preparing);
    push(&mut engine, PREPARATION_MS - 1.0, 10_000, 10_000);
    assert_eq!(engine.frame().active.unwrap().packet_counts, [0, 0]);
    engine.update(PREPARATION_MS);
    assert_eq!(engine.frame().active.unwrap().stage, Stage::Collecting);
    for i in 0..100 {
        push(&mut engine, PREPARATION_MS + i as f64 * 10.0, 15, 10);
        if i == 66 {
            let state = engine.frame().active.unwrap();
            assert_eq!(state.stage, Stage::Collecting);
            assert_eq!(state.target_path_length, 670.0);
            assert_eq!(state.progress, 0.67);
        }
    }
    let state = engine.frame().active.unwrap();
    assert_eq!(state.stage, Stage::Finishing);
    assert_eq!(state.progress, 1.0);
    assert_eq!(state.packet_counts, [100, 100]);
    engine.update(PREPARATION_MS + 1037.0);
    assert!(engine.frame().result.is_none());
    engine.update(PREPARATION_MS + 1038.0);
    let frame = engine.frame();
    assert_eq!(frame.failure, Failure::None);
    assert!(frame.active.is_none());
    let result = frame.result.unwrap();
    assert!((result.scale - 1.5).abs() < 1e-12);
    assert_eq!(result.recommended_target_dpi, 381);
    assert_eq!(result.duration_ms, 990.0);
    assert!(!engine.start(254, 10.0, 2000.0));
    engine.invalidate();
    assert!(engine.frame().results_expired);
    engine.reset();
    assert!(engine.start(254, 10.0, 2000.0));
    engine.cancel();
    assert!(engine.frame().active.is_none());
    assert!(engine.frame().result.is_none());
}

#[test]
fn preparation_is_explicit_and_the_first_collecting_packet_is_not_discarded() {
    let mut engine = SensitivityEngine::default();
    assert!(engine.start(254, 10.0, 1000.0));
    push(&mut engine, 1499.0, 1000, 1000);
    push(&mut engine, 1500.0, 10, 10);
    let state = engine.frame().active.unwrap();
    assert_eq!(state.stage, Stage::Collecting);
    assert_eq!(state.target_path_length, 10.0);
    assert_eq!(state.packet_counts, [1, 1]);
}

#[test]
fn invalid_start_does_not_change_existing_state() {
    let mut engine = SensitivityEngine::default();
    for distance in [f64::NAN, f64::INFINITY, -1.0, 0.0, f64::MAX] {
        assert!(!engine.start(508, distance, 0.0));
    }
    for time in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
        assert!(!engine.start(508, 10.0, time));
    }
    for dpi in [0, -1] {
        assert!(!engine.start(dpi, 10.0, 0.0));
    }
    assert!(engine.start(50, 10.0, 0.0));
    let before = serde_json::to_value(engine.frame()).unwrap();
    assert!(!engine.start(254, 20.32, 100.0));
    assert_eq!(before, serde_json::to_value(engine.frame()).unwrap());
    assert!((engine.frame().active.unwrap().target_goal - 196.8503937007874).abs() < 1e-10);
}

#[test]
fn no_motion_and_one_missing_mouse_timeout_without_unbounded_wait() {
    for source_only in [false, true] {
        let mut engine = SensitivityEngine::default();
        engine.start(254, 10.0, 0.0);
        if source_only {
            engine.push(
                false,
                Packet {
                    time: 600.0,
                    dx: 10_000,
                    dy: 0,
                },
            );
        }
        engine.update(8501.0);
        assert_eq!(engine.frame().failure, Failure::Timeout);
        assert!(engine.frame().active.is_none());
        assert!(engine.start(254, 10.0, 9000.0));
    }
}

#[test]
fn actual_motion_duration_not_idle_wait_decides_too_fast() {
    let mut engine = SensitivityEngine::default();
    engine.start(254, 10.0, 0.0);
    for i in 0..100 {
        push(&mut engine, 500.0 + i as f64, 10, 10);
    }
    engine.update(3000.0);
    assert_eq!(engine.frame().failure, Failure::TooFast);
    assert!(engine.frame().result.is_none());
}

#[test]
fn too_few_packets_and_invalid_timestamps_are_not_accepted() {
    let mut engine = SensitivityEngine::default();
    engine.start(254, 10.0, 0.0);
    for i in 0..20 {
        push(&mut engine, 500.0 + i as f64 * 30.0, 50, 50);
    }
    engine.update(2000.0);
    assert_eq!(engine.frame().failure, Failure::InsufficientPackets);
    for time in [f64::NAN, 599.0] {
        engine.reset();
        engine.start(254, 10.0, 0.0);
        push(&mut engine, 600.0, 10, 10);
        engine.push(
            false,
            Packet {
                time,
                dx: 10,
                dy: 0,
            },
        );
        assert_eq!(engine.frame().failure, Failure::InvalidInput);
    }
}

#[test]
fn packet_path_and_completion_do_not_allocate_after_start() {
    let mut engine = SensitivityEngine::default();
    engine.start(800, 10.0, 0.0);
    let allocations = allocation_counter::measure(|| {
        // 8 kHz, including stationary reports, takes the same bounded sample storage.
        for i in 0..16_000 {
            push(
                &mut engine,
                500.0 + i as f64 * 0.125,
                i32::from(i % 4 == 0),
                i32::from(i % 4 == 0),
            );
            if i % 160 == 0 {
                engine.update(500.0 + i as f64 * 0.125);
            }
        }
        engine.update(3000.0);
    });
    assert_eq!(allocations.count_total, 0);
    assert_eq!(engine.frame().failure, Failure::None);
    assert_eq!(engine.frame().result.unwrap().recommended_target_dpi, 800);
}

#[test]
fn target_motion_duration_excludes_the_source_head_start() {
    let mut engine = SensitivityEngine::default();
    assert!(engine.start(254, 10.0, 0.0));
    for i in 0..100 {
        push(
            &mut engine,
            500.0 + i as f64 * 10.0,
            10,
            if i < 70 { 0 } else { 34 },
        );
    }
    let state = engine.frame().active.unwrap();
    assert_eq!(state.stage, Stage::Finishing);
    assert_eq!(state.duration_ms, 290.0);
    engine.update(2000.0);
    assert_eq!(engine.frame().failure, Failure::TooFast);
    assert!(engine.frame().result.is_none());
}

#[test]
fn target_distance_completion_still_requires_source_motion() {
    let mut engine = SensitivityEngine::default();
    assert!(engine.start(254, 10.0, 0.0));
    for i in 0..100 {
        push(&mut engine, 500.0 + i as f64 * 10.0, 0, 10);
    }
    assert_eq!(engine.frame().active.unwrap().stage, Stage::Finishing);
    engine.update(2000.0);
    assert_eq!(engine.frame().failure, Failure::InsufficientPackets);
    assert!(engine.frame().result.is_none());
}

#[test]
fn target_distance_keeps_the_half_count_endpoint_tolerance() {
    let mut engine = SensitivityEngine::default();
    assert!(engine.start(50, 10.0, 0.0));
    push(&mut engine, 500.0, 1000, 196);
    assert_eq!(engine.frame().active.unwrap().stage, Stage::Collecting);
    push(&mut engine, 1000.0, 1, 1);
    assert_eq!(engine.frame().active.unwrap().stage, Stage::Finishing);
}
