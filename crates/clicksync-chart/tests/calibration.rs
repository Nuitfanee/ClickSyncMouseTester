use clicksync_chart::analysis::calibration::{CalibrationEngine, CalibrationFrame};

fn curve(width: f64, sag: f64, degrees: f64, exponent: f64) -> Vec<[i32; 2]> {
    let (sin, cos) = degrees.to_radians().sin_cos();
    (0..=400)
        .map(|i| {
            let u = (i as f64 / 400.0).powf(exponent);
            let x = width * u;
            let y = -4.0 * sag * u * (1.0 - u);
            [
                (cos * x - sin * y).round() as i32,
                (sin * x + cos * y).round() as i32,
            ]
        })
        .collect()
}

fn push_stroke(engine: &mut CalibrationEngine, points: &[[i32; 2]], time: &mut f64) {
    let step = 300.0 / (points.len() - 1) as f64;
    for pair in points.windows(2) {
        engine.push(pair[1][0] - pair[0][0], pair[1][1] - pair[0][1], *time);
        *time += step;
    }
}

fn measure(points: &[i32; 2]) -> CalibrationFrame {
    let line: Vec<_> = (0..=100).map(|i| [points[0] * i, points[1] * i]).collect();
    measure_curve(&line)
}

fn measure_curve(points: &[[i32; 2]]) -> CalibrationFrame {
    let mut engine = CalibrationEngine::default();
    let mut time = 1000.0;
    engine.set_locked(true);
    for i in 0..44 {
        let mut points = points.to_vec();
        if i % 2 != 0 {
            points.reverse();
        }
        push_stroke(&mut engine, &points, &mut time);
    }
    engine.set_locked(false);
    engine.frame(time + 10_000.0)
}

#[test]
fn signed_axis_does_not_take_its_sign_from_the_endpoints() {
    // A gently bent stroke whose endpoint chord slopes down, but main axis slopes up.
    let points: Vec<_> = (0..=400)
        .map(|i| {
            let u = i as f64 / 400.0;
            [
                (10_000.0 * u).round() as i32,
                (-300.0 * u + 350.0 * u.powi(9)).round() as i32,
            ]
        })
        .collect();
    let frame = measure_curve(&points);
    let correction = frame.recommended_angle.unwrap();
    assert!(
        (0.5..1.0).contains(&correction),
        "main-axis correction must be positive, got {correction}"
    );
}

#[test]
fn natural_arcs_are_rotation_equivariant_and_speed_independent() {
    for sag in [0.0, 600.0, 1500.0] {
        for degrees in [-20.0, -0.8, 0.0, 0.8, 20.0] {
            for exponent in [0.5, 1.0, 2.0] {
                let frame = measure_curve(&curve(10_000.0, sag, degrees, exponent));
                let correction = frame.recommended_angle.unwrap_or_else(|| {
                    panic!("rejected natural arc: {sag}, {degrees}, {exponent}")
                });
                assert!(
                    (correction + degrees).abs() < 0.03,
                    "sag={sag}, rotation={degrees}, speed={exponent}: {correction}"
                );
            }
        }
    }
}

#[test]
fn each_stroke_has_equal_weight_regardless_of_length() {
    let mut engine = CalibrationEngine::default();
    let mut time = 1000.0;
    for i in 0..81 {
        // Both directions see alternating short horizontal and long tilted strokes.
        let tilted = (i / 2) % 2 != 0;
        let mut points = curve(
            if tilted { 10_000.0 } else { 500.0 },
            0.0,
            if tilted { 2.0 } else { 0.0 },
            1.0,
        );
        if i % 2 != 0 {
            points.reverse();
        }
        push_stroke(&mut engine, &points, &mut time);
    }
    let frame = engine.frame(time);
    assert!((frame.recommended_angle.unwrap() + 1.0).abs() < 0.03);
}

#[test]
fn straight_line_correction_closes_the_rotation_loop() {
    for delta in [[10, 0], [10, 3], [10, -3]] {
        let frame = measure(&delta);
        let angle = frame.recommended_angle.unwrap();
        let (sin, cos) = angle.to_radians().sin_cos();
        let corrected_y = sin * delta[0] as f64 + cos * delta[1] as f64;
        assert!(corrected_y.abs() < 1e-10, "{delta:?}: {angle}");
    }
}

#[test]
fn exact_polyline_subdivision_does_not_change_the_estimate() {
    let points: Vec<_> = curve(10_000.0, 1000.0, 7.0, 1.0)
        .into_iter()
        .map(|p| [p[0] * 8, p[1] * 8])
        .collect();
    let mut denser = vec![points[0]];
    for (i, pair) in points.windows(2).enumerate() {
        let parts = if i < 100 { 8 } else { 1 };
        for j in 1..=parts {
            denser.push(std::array::from_fn(|axis| {
                pair[0][axis] + (pair[1][axis] - pair[0][axis]) * j / parts
            }));
        }
    }
    let original = measure_curve(&points).recommended_angle.unwrap();
    let subdivided = measure_curve(&denser).recommended_angle.unwrap();
    assert!((original - subdivided).abs() < 1e-9);
}

#[test]
fn disagreement_is_separate_from_within_direction_variation() {
    let mut engine = CalibrationEngine::default();
    let mut time = 1000.0;
    for i in 0..44 {
        let direction = if i % 2 == 0 { 1 } else { -1 };
        let points: Vec<_> = (0..=100).map(|x| [direction * 100 * x, 14 * x]).collect();
        push_stroke(&mut engine, &points, &mut time);
    }
    let frame = engine.frame(time);
    assert!(frame.recommended_angle.unwrap().abs() < 1e-10);
    assert!(frame.stroke_spread_degrees.unwrap() < 1e-10);
    assert!(
        (frame.direction_difference_degrees.unwrap() - 2.0 * 0.14_f64.atan().to_degrees()).abs()
            < 1e-9
    );
    assert_eq!(frame.quality_reason, "DirectionMismatch");
}

#[test]
fn spread_is_the_pooled_sample_sd_of_strokes_and_survives_pause() {
    let mut engine = CalibrationEngine::default();
    let mut time = 1000.0;
    engine.set_locked(true);
    for i in 0..81 {
        let direction = if i % 2 == 0 { 1 } else { -1 };
        let dy = if (i / 2) % 2 == 0 { 0 } else { 2 };
        let points: Vec<_> = (0..=100)
            .map(|x| [direction * 100 * x, direction * dy * x])
            .collect();
        push_stroke(&mut engine, &points, &mut time);
    }
    let expected = 0.02_f64.atan().to_degrees() * 0.5 * (80.0_f64 / 78.0).sqrt();
    let live = engine.frame(time);
    assert!((live.stroke_spread_degrees.unwrap() - expected).abs() < 1e-9);
    assert_eq!(live.accepted_strokes, 80);
    assert_eq!(live.rejected_strokes, 0);
    engine.set_locked(false);
    let paused = engine.frame(time);
    assert_eq!(live.stroke_spread_degrees, paused.stroke_spread_degrees);
    assert_eq!(
        serde_json::to_value(&paused).unwrap(),
        serde_json::to_value(engine.frame(time + 10_000.0)).unwrap()
    );
    engine.reset();
    let cleared = engine.frame(time);
    assert_eq!(cleared.accepted_strokes, 0);
    assert_eq!(cleared.rejected_strokes, 0);
    assert!(cleared.stroke_spread_degrees.is_none());
    assert!(cleared.direction_difference_degrees.is_none());
}

#[test]
fn repeated_outlier_directions_remain_in_the_variation_statistic() {
    let mut engine = CalibrationEngine::default();
    let mut time = 1000.0;
    for i in 0..81 {
        let mut points = curve(
            10_000.0,
            0.0,
            if (i / 2) % 10 == 0 { 15.0 } else { 0.0 },
            1.0,
        );
        if i % 2 != 0 {
            points.reverse();
        }
        push_stroke(&mut engine, &points, &mut time);
    }
    let frame = engine.frame(time);
    assert!(frame.recommended_angle.unwrap().abs() < 1e-9);
    assert!(frame.stroke_spread_degrees.unwrap() > 4.0);
    assert_eq!(frame.quality_reason, "HighDispersion");
}

#[test]
fn short_strokes_are_rejected_individually_and_do_not_merge_into_valid_strokes() {
    let mut engine = CalibrationEngine::default();
    let mut time = 1000.0;
    for i in 0..52 {
        let mut points = curve(if i < 10 { 100.0 } else { 1000.0 }, 0.0, 0.0, 1.0);
        if i % 2 != 0 {
            points.reverse();
        }
        push_stroke(&mut engine, &points, &mut time);
    }
    let frame = engine.frame(time);
    assert_eq!(frame.rejected_strokes, 10);
    assert_eq!(frame.accepted_strokes, 41);
    assert_eq!(frame.swipe_count, 40);
    assert_eq!(frame.recommended_angle, Some(0.0));
}

#[test]
fn rejection_rate_is_not_hidden_by_a_clean_subset() {
    let mut engine = CalibrationEngine::default();
    let mut time = 1000.0;
    for i in 0..264 {
        let mut points = curve(1000.0, if i % 3 == 0 { 0.0 } else { 400.0 }, 0.0, 1.0);
        if i % 2 != 0 {
            points.reverse();
        }
        push_stroke(&mut engine, &points, &mut time);
    }
    let frame = engine.frame(time);
    assert_eq!(frame.accepted_strokes, 88);
    assert_eq!(frame.rejected_strokes, 175);
    assert_eq!(frame.recommended_angle, Some(0.0));
    assert_eq!(frame.quality_reason, "HighRejection");
}
