use clicksync_chart::analysis::{
    calibration::CalibrationEngine,
    keys::{KeyEngine, KeyIdentity, TimingStatistics},
    polling::{PollingEngine, PollingMode},
    sensitivity::{Packet, SensitivityEngine},
};
use serde_json::{Value, json};
use std::path::PathBuf;

#[path = "support/calibration_reference.rs"]
mod calibration_reference;
#[path = "support/polling_reference.rs"]
mod polling_reference;
#[path = "support/sensitivity_reference.rs"]
mod sensitivity_reference;

fn fixture(name: &str, domain: &str, operations: Vec<Value>) -> Value {
    json!({"name": name, "domain": domain, "operations":operations})
}
fn fixtures() -> Vec<Value> {
    let mut cases = Vec::new();
    for (rate, motion) in [(1000, false), (8000, false), (1000, true)] {
        let mut ops = vec![json!(["mode", i32::from(motion)]), json!(["begin", 1000.0])];
        let step = 1000.0 / rate as f64;
        for i in 0..rate * 2 {
            let t = 1000.0 + i as f64 * step;
            ops.push(json!([
                "packet",
                t,
                if motion && i % 3 == 0 { 0 } else { 1 },
                0
            ]));
            if i % (rate / 25) == 0 {
                ops.push(json!(["frame", t]));
            }
        }
        ops.extend([
            json!(["frame", 3000.0]),
            json!(["end", 3000.0]),
            json!(["frame", 3100.0]),
            json!(["frame", 3900.0]),
            json!(["begin", 4000.0]),
            json!(["packet", 4000.0, 3, 0]),
            json!(["frame", 4000.0]),
            json!(["frame", 5000.0]),
            json!(["reset"]),
            json!(["frame", 5000.0]),
        ]);
        cases.push(fixture(&format!("polling-{rate}-{motion}"), "polling", ops));
    }
    let mut ops = vec![json!(["interval", 0.1]), json!(["begin", 0.0])];
    for i in 0..700 {
        let t = i as f64 * 0.999;
        ops.push(json!(["packet", t, 1, 0]));
        if i % 10 == 0 {
            ops.push(json!(["frame", t]));
        }
    }
    ops.extend([
        json!(["packet", 650.0, 0, 0]),
        json!(["frame", 700.0]),
        json!(["mode", 1]),
        json!(["frame", 701.0]),
        json!(["interval", 1000.0]),
        json!(["end", 710.0]),
        json!(["frame", 900.0]),
        json!(["frame", 1000.0]),
        json!(["frame", 1000.0]),
    ]);
    cases.push(fixture("polling-boundaries", "polling", ops));
    for (angle, noisy) in [(0, false), (3, false), (-3, false), (3, true)] {
        let mut ops = vec![json!(["frame", 0.0]), json!(["lock", 1])];
        let mut time = 1000.0;
        for stroke in 0..44 {
            let direction = if stroke % 2 == 0 { 1 } else { -1 };
            for i in 0..60 {
                let dx = direction * 10;
                let dy = direction * angle + if noisy && i % 7 == 0 { 5 } else { 0 };
                ops.push(json!(["packet", time, dx, dy]));
                time += 3.0;
            }
            ops.push(json!(["frame", time]));
        }
        ops.extend([
            json!(["lock", 0]),
            json!(["frame", time]),
            json!(["frame", time + 2000.0]),
            json!(["reset"]),
            json!(["frame", time + 2000.0]),
        ]);
        cases.push(fixture(
            &format!("calibration-{angle}-{noisy}"),
            "calibration",
            ops,
        ));
    }
    let mut ops = vec![json!(["lock", 1])];
    for i in 0..1500 {
        ops.push(json!(["packet", i as f64, 2, 1]));
    }
    ops.extend([
        json!(["frame", 1500.0]),
        json!(["lock", 0]),
        json!(["frame", 1500.0]),
    ]);
    cases.push(fixture("calibration-trace-cap", "calibration", ops));
    for variant in [
        "scale",
        "lag",
        "rotation",
        "shape",
        "curve",
        "fast",
        "few",
        "timeout",
        "out-of-range-lag",
    ] {
        let mut ops = Vec::new();
        for round in 0..if variant == "scale" { 3 } else { 1 } {
            let start = round as f64 * 3000.0;
            ops.push(json!(["start", 100, 100, start]));
            ops.push(json!(["packet", 0, start + 100.0, 1000, 0])); // excluded warmup
            ops.push(json!(["frame"]));
            let count = if variant == "few" { 20 } else { 160 };
            let dt = if variant == "fast" { 0.4 } else { 5.0 };
            for i in 0..count {
                let t = start + 200.0 + i as f64 * dt;
                let dx = 10 + (i % 13);
                let (ax, ay, bx, by) = match variant {
                    "scale" => (dx * 2, 0, dx, 0),
                    "rotation" => (dx, 0, 0, dx),
                    "curve" => (dx, if i % 2 == 0 { dx * 2 } else { -dx * 2 }, dx, 0),
                    "shape" => (dx, 0, dx, if i % 10 < 5 { dx } else { -dx }),
                    "few" => (100, 0, 100, 0),
                    "timeout" => (1, 0, 1, 0),
                    _ => (dx, 0, dx, 0),
                };
                let lag = if variant == "lag" {
                    12.0
                } else if variant == "out-of-range-lag" {
                    80.0
                } else {
                    0.0
                };
                ops.push(json!(["packet", 0, t, ax, ay]));
                ops.push(json!(["packet", 1, t + lag, bx, by]));
                if i % 10 == 0 {
                    ops.push(json!(["frame"]));
                }
            }
            ops.extend([
                json!([
                    "update",
                    start + if variant == "fast" { 515.0 } else { 2000.0 }
                ]),
                json!(["frame"]),
            ]);
        }
        if variant == "timeout" {
            ops.extend([json!(["update", 10000.0]), json!(["frame"])]);
        }
        ops.extend([
            json!(["invalidate"]),
            json!(["frame"]),
            json!(["cancel"]),
            json!(["reset"]),
            json!(["frame"]),
        ]);
        cases.push(fixture(
            &format!("sensitivity-{variant}"),
            "sensitivity",
            ops,
        ));
    }
    let mut shape_ops = vec![json!(["start", 100, 100, 1000.0])];
    for i in 0..48 {
        let time = 1250.0 + i as f64 * 20.0;
        shape_ops.push(json!(["packet", 0, time, 30, 0]));
        shape_ops.push(json!(["packet", 1, time, if i < 24 { 20 } else { 40 }, 0]));
    }
    shape_ops.extend([json!(["update", 2510.0]), json!(["frame"])]);
    cases.push(fixture("sensitivity-path-shape", "sensitivity", shape_ops));
    cases.push(fixture(
        "key-boundaries",
        "keys",
        vec![
            json!(["down", 0.0, 80.0]),
            json!(["down", 1.0, 80.0]),
            json!(["up", 0.2]),
            json!(["frame"]),
            json!(["down", 0.2, 80.0]),
            json!(["up", 0.5]),
            json!(["frame"]),
            json!(["down", 80.2, 80.0]),
            json!(["up", 100.0]),
            json!(["frame"]),
            json!(["down", 100.2, 80.0]),
            json!(["up", 101.0]),
            json!(["frame"]),
            json!(["up", 102.0]),
            json!(["down", 3100.2, 80.0]),
            json!(["up", 8100.2]),
            json!(["frame"]),
            json!(["down", 8200.0, 80.0]),
            json!(["transient"]),
            json!(["up", 8201.0]),
            json!(["frame"]),
            json!(["reset"]),
            json!(["frame"]),
        ],
    ));
    for count in [0, 1, 2, 31, 999, 1000, 1001] {
        for dispersion in [true, false] {
            cases.push(fixture(
                &format!("statistics-{count}-{dispersion}"),
                "statistics",
                vec![json!([
                    "values",
                    dispersion,
                    (0..count)
                        .map(|i| ((i * 37) % 97) as f64 - 40.5)
                        .collect::<Vec<_>>()
                ])],
            ));
        }
    }
    cases
}
fn run_native(test: &Value) -> Value {
    let mut polling = PollingEngine::default();
    let mut calibration = CalibrationEngine::default();
    let mut sensitivity = SensitivityEngine::default();
    let mut keys = TimingStatistics::default();
    let mut frames = Vec::new();
    let domain = test["domain"].as_str().unwrap();
    for op in test["operations"].as_array().unwrap() {
        let action = op[0].as_str().unwrap();
        let t = |index: usize| op[index].as_f64().unwrap();
        let int = |index: usize| op[index].as_i64().unwrap() as i32;
        match domain {
            "statistics" => frames.push(
                serde_json::to_value(
                    clicksync_chart::analysis::statistics::distribution_with_dispersion(
                        op[2]
                            .as_array()
                            .unwrap()
                            .iter()
                            .map(|v| v.as_f64().unwrap()),
                        op[1].as_bool().unwrap(),
                    ),
                )
                .unwrap(),
            ),
            "polling" => match action {
                "reset" => polling.reset(),
                "begin" => polling.begin_segment(t(1)),
                "end" => polling.end_segment(t(1)),
                "mode" => polling.set_mode(if int(1) == 0 {
                    PollingMode::RawPacketRate
                } else {
                    PollingMode::MotionReportRate
                }),
                "interval" => polling.set_history_interval(t(1)),
                "packet" => polling.push(t(1), int(2), int(3)),
                "frame" => {
                    let metrics = polling.metrics(t(1), 7);
                    let (history, rate) = polling.history_frame(t(1));
                    frames.push(json!({"metrics":metrics,"history":history,"rate":rate}));
                }
                _ => panic!("{action}"),
            },
            "calibration" => match action {
                "reset" => calibration.reset(),
                "lock" => calibration.set_locked(int(1) != 0),
                "packet" => calibration.push(int(2), int(3), t(1)),
                "frame" => frames.push(serde_json::to_value(calibration.frame(t(1))).unwrap()),
                _ => panic!("{action}"),
            },
            "sensitivity" => match action {
                "reset" => sensitivity.reset(),
                "start" => {
                    // The independent frozen corpus specifies an eight-inch measurement.
                    sensitivity.start(int(2), 20.32, t(3));
                }
                "cancel" => sensitivity.cancel(),
                "invalidate" => sensitivity.invalidate(),
                "packet" => sensitivity.push(
                    int(1) != 0,
                    Packet {
                        time: t(2),
                        dx: int(3),
                        dy: int(4),
                    },
                ),
                "update" => sensitivity.update(t(1)),
                "frame" => frames.push(serde_json::to_value(sensitivity.frame()).unwrap()),
                _ => panic!("{action}"),
            },
            "keys" => match action {
                "reset" => keys.reset(),
                "transient" => keys.reset_pressed(),
                "down" => {
                    keys.down(t(1), t(2));
                }
                "up" => keys.up(t(1)),
                "frame" => {
                    let mut frame = serde_json::to_value(&keys).unwrap();
                    frame["average_down_down_ms"] = json!(keys.average_down_down_ms());
                    frame["average_down_up_ms"] = json!(keys.average_down_up_ms());
                    frames.push(frame);
                }
                _ => panic!("{action}"),
            },
            _ => panic!("{domain}"),
        }
    }
    json!({"name":test["name"],"frames":frames})
}
fn compare(expected: &Value, actual: &Value, path: &str) {
    match (expected, actual) {
        (Value::Number(a), Value::Number(b)) => {
            if a.is_i64() && b.is_i64() {
                assert_eq!(a, b, "{path}");
            } else {
                let (a, b) = (a.as_f64().unwrap(), b.as_f64().unwrap());
                let tolerance = 1e-9_f64.max(a.abs() * 1e-9);
                assert!(
                    (a - b).abs() <= tolerance,
                    "{path}: expected {a}, actual {b}, tolerance {tolerance}"
                );
            }
        }
        (Value::Array(a), Value::Array(b)) => {
            assert_eq!(a.len(), b.len(), "{path} length");
            for (i, (a, b)) in a.iter().zip(b).enumerate() {
                compare(a, b, &format!("{path}[{i}]"));
            }
        }
        (Value::Object(a), Value::Object(b)) => {
            assert_eq!(a.len(), b.len(), "{path} keys");
            for (k, a) in a {
                compare(
                    a,
                    b.get(k).unwrap_or_else(|| panic!("{path}/{k} missing")),
                    &format!("{path}/{k}"),
                );
            }
        }
        _ => assert_eq!(expected, actual, "{path}"),
    }
}
#[test]
fn product_algorithms_match_frozen_reference() {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let directory = std::env::var_os("CLICKSYNC_PRODUCT_REFERENCE_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            root.join("../..")
                .join("artifacts/ProductDomains/runs")
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
    let input = directory.join("input.json");
    let native_path = directory.join("native.json");
    let fixtures = fixtures();
    let serialized_input = serde_json::to_vec(&fixtures).unwrap();
    std::fs::write(&input, &serialized_input).unwrap();
    // Workspace feature unification can change JSON object ordering.
    let current_input: Value = serde_json::from_slice(&serialized_input).unwrap();
    let frozen_input: Value =
        serde_json::from_str(include_str!("fixtures/product-domains-input.json")).unwrap();
    assert!(current_input == frozen_input, "reference input changed");
    let native: Vec<_> = fixtures.iter().map(run_native).collect();
    std::fs::write(&native_path, serde_json::to_vec(&native).unwrap()).unwrap();
    let expected: Value =
        serde_json::from_str(include_str!("fixtures/product-domains-expected.json")).unwrap();
    assert_eq!(expected.as_array().unwrap().len(), fixtures.len());
    for (index, ((input, actual), frozen)) in fixtures
        .iter()
        .zip(&native)
        .zip(expected.as_array().unwrap())
        .enumerate()
    {
        let reference = if input["domain"] == "calibration" {
            calibration_reference::expected(input, frozen)
        } else if input["domain"] == "polling" {
            polling_reference::expected(input)
        } else if input["domain"] == "sensitivity" {
            sensitivity_reference::expected(input)
        } else {
            frozen.clone()
        };
        compare(&reference, actual, &format!("domains[{index}]"));
    }
    // The single-swipe lifecycle oracle covers every historical sensitivity frame.
    // Accepted ratios and each quality rejection have independent physical-input tests.
    std::fs::write(
        directory.join("report.json"),
        serde_json::to_vec(&json!({
            "passed":true,"cases":fixtures.len(),"floating_relative_tolerance":1e-9,
            "comparison":"frozen full states/traces and strict integers; calibration v2 independent spatial-axis/statistics oracle; polling v2 independent batch-count oracle; sensitivity v2 independent bounded-swipe lifecycle oracle",
        }))
        .unwrap(),
    )
    .unwrap();
    println!("Product domain evidence: {}", directory.display());
}
#[test]
fn key_selection_release_repeats_focus_wheel_and_thresholds() {
    let mut engine = KeyEngine::default();
    let a = KeyIdentity {
        virtual_key: 65,
        scan_code: 30,
        extended: false,
    };
    engine.picking = true;
    engine.keyboard(a, true, 1.0, true, false);
    engine.keyboard(a, true, 2.0, true, false);
    engine.keyboard(a, false, 3.0, true, false);
    assert_eq!(engine.custom.down_count, 0);
    engine.keyboard(a, true, 4.0, true, false);
    engine.keyboard(a, true, 5.0, true, false);
    engine.keyboard(
        KeyIdentity {
            extended: true,
            ..a
        },
        false,
        6.0,
        true,
        false,
    );
    assert!(engine.custom.is_pressed);
    engine.keyboard(a, false, 7.0, true, false);
    engine.keyboard(a, true, 8.0, true, false);
    assert_eq!(engine.custom.double_click_count, 1);
    engine.reset_transient();
    engine.keyboard(a, true, 9.0, false, false);
    engine.keyboard(a, true, 10.0, true, true);
    assert_eq!(engine.custom.down_count, 2);
    engine.mouse(0x1 | 0x10 | 0x4 | 0x100 | 0x40, 0, 100.0);
    engine.mouse(0x2 | 0x20 | 0x8 | 0x200 | 0x80, 0, 120.0);
    assert!(
        engine
            .buttons
            .iter()
            .all(|s| s.down_count == 1 && s.up_count == 1 && s.current_down_up_ms == Some(20.0))
    );
    engine.mouse(0x400, 120, 130.0);
    engine.mouse(0x400, (-240_i16) as u16, 140.0);
    assert_eq!((engine.wheel_up, engine.wheel_down), (1, 1));
    assert_eq!(
        (engine.wheel_up_until, engine.wheel_down_until),
        (280.0, 290.0)
    );
    for value in [0.0, 1.0, 1000.0, f64::NAN] {
        assert_eq!(KeyEngine::normalize_threshold(value), 80.0);
    }
}
