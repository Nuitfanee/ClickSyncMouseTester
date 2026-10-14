//! V2 numerical oracle for the unchanged calibration input corpus.
//! Frozen V1 states, counters and full traces remain the reference for unchanged behavior.
//! Only the deliberately replaced fit/statistics schema is derived here, without calling
//! the production fitter: batch distance lookup and covariance power iteration.
use serde_json::{Value, json};

fn fixture_axis(delta_y: i32, noisy: bool, direction: i32) -> f64 {
    let mut points = vec![[0.0_f64; 2]];
    let mut distances = vec![0.0];
    for i in 0..60 {
        let dx = (direction * 10) as f64;
        let dy = (direction * delta_y + if noisy && i % 7 == 0 { 5 } else { 0 }) as f64;
        let last = points.last().unwrap();
        points.push([last[0] + dx, last[1] + dy]);
        distances.push(distances.last().unwrap() + dx.hypot(dy));
    }
    let spatial: Vec<[f64; 2]> = (0..64)
        .map(|i| {
            let distance = distances.last().unwrap() * i as f64 / 63.0;
            let end = (1..distances.len())
                .find(|&j| distances[j] >= distance)
                .unwrap_or(distances.len() - 1);
            let fraction = (distance - distances[end - 1]) / (distances[end] - distances[end - 1]);
            std::array::from_fn(|axis| {
                points[end - 1][axis] + fraction * (points[end][axis] - points[end - 1][axis])
            })
        })
        .collect();
    let mean: [f64; 2] =
        std::array::from_fn(|axis| spatial.iter().map(|p| p[axis]).sum::<f64>() / 64.0);
    let mut covariance = [[0.0; 2]; 2];
    for point in spatial {
        for row in 0..2 {
            for col in 0..2 {
                covariance[row][col] += (point[row] - mean[row]) * (point[col] - mean[col]);
            }
        }
    }
    let mut axis = [1.0, 0.0];
    for _ in 0..80 {
        let next: [f64; 2] =
            std::array::from_fn(|row| covariance[row][0] * axis[0] + covariance[row][1] * axis[1]);
        let norm = next[0].hypot(next[1]);
        axis = [next[0] / norm, next[1] / norm];
    }
    -axis[1].atan2(axis[0]).to_degrees()
}

pub fn expected(input: &Value, frozen: &Value) -> Value {
    let name = input["name"].as_str().unwrap();
    let (left, right) = match name {
        "calibration-0-false" => (0.0, 0.0),
        "calibration-3-false" => {
            let angle = -(0.3_f64).atan().to_degrees();
            (angle, angle)
        }
        "calibration--3-false" => {
            let angle = (0.3_f64).atan().to_degrees();
            (angle, angle)
        }
        "calibration-3-true" => (fixture_axis(3, true, -1), fixture_axis(3, true, 1)),
        "calibration-trace-cap" => (0.0, 0.0),
        _ => panic!("calibration corpus case needs an independent oracle: {name}"),
    };
    let mut expected = frozen.clone();
    for frame in expected["frames"].as_array_mut().unwrap() {
        let has_data = frame["has_data"].as_bool().unwrap();
        let swipes = frame["swipe_count"].as_u64().unwrap();
        let accepted = if swipes > 0 {
            swipes + 1
        } else {
            frame["traces"]
                .as_array()
                .unwrap()
                .iter()
                .filter(|trace| trace["current"] == false)
                .count() as u64
        };
        // Every completed stroke in these five fixtures is valid. The unfinished
        // long trace is discarded on unlock, rather than evaluated as a rejection.
        let difference = (accepted >= 8).then_some((left - right).abs());
        let reason = if !has_data {
            "None"
        } else if difference.is_some_and(|value| value > 3.0) {
            "DirectionMismatch"
        } else if swipes < 30 {
            "InsufficientProgress"
        } else {
            "Good"
        };
        let object = frame.as_object_mut().unwrap();
        assert!(object.remove("stability_degrees").is_some());
        assert!(object.remove("quality_score").is_some());
        assert!(object.remove("quality_level").is_some());
        object.insert(
            "recommended_angle".into(),
            json!((swipes >= 30).then_some((left + right) * 0.5)),
        );
        object.insert("accepted_strokes".into(), json!(accepted));
        object.insert("rejected_strokes".into(), json!(0));
        object.insert(
            "stroke_spread_degrees".into(),
            json!((accepted >= 4).then_some(0.0)),
        );
        object.insert("direction_difference_degrees".into(), json!(difference));
        object.insert("quality_reason".into(), json!(reason));
    }
    expected
}
