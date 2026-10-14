//! Independent physical trajectories and accumulated-count quantization, not fitter-derived goldens.
use clicksync_chart::analysis::sensitivity::{Failure, MatchFrame, Packet, SensitivityEngine};
use std::f64::consts::PI;

#[derive(Clone, Copy)]
struct Motion {
    cpi: [f64; 2],
    hz: [usize; 2],
    mount: f64,
    curve: f64,
    lag: f64,
    jitter: f64,
    slip: f64,
    spike: f64,
}

impl Default for Motion {
    fn default() -> Self {
        Self {
            cpi: [800.0; 2],
            hz: [1000; 2],
            mount: 0.0,
            curve: 0.0,
            lag: 0.0,
            jitter: 0.0,
            slip: 0.0,
            spike: 0.0,
        }
    }
}

fn events(m: Motion) -> Vec<(bool, Packet)> {
    let mut events = Vec::new();
    for side in 0..2 {
        let mut previous = [0_i32; 2];
        let mut last_time = 0.0_f64;
        for i in 0..=(m.hz[side] * 2) {
            let t = i as f64 * 1000.0 / m.hz[side] as f64;
            let u = (t / 1500.0).clamp(0.0, 1.0);
            let travel = u - (2.0 * PI * u).sin() / (2.0 * PI);
            let mut point = [100.0 * travel, m.curve * (PI * travel).sin()];
            if side == 1 {
                point[0] += m.slip * (travel - 0.45).max(0.0);
                if (0.47..0.48).contains(&u) {
                    point[1] += m.spike;
                }
                let (sin, cos) = m.mount.to_radians().sin_cos();
                point = [
                    cos * point[0] + sin * point[1],
                    -sin * point[0] + cos * point[1],
                ];
            }
            let counts = point.map(|v| (v * m.cpi[side] / 25.4).round() as i32);
            let time = (500.0
                + t
                + if side == 1 { m.lag } else { 0.0 }
                + m.jitter * (i as f64 * 1.77).sin())
            .max(last_time + 0.001);
            last_time = time;
            events.push((
                side == 1,
                Packet {
                    time,
                    dx: counts[0] - previous[0],
                    dy: counts[1] - previous[1],
                },
            ));
            previous = counts;
        }
    }
    events.sort_by(|a, b| a.1.time.total_cmp(&b.1.time).then(a.0.cmp(&b.0)));
    events
}

fn run(m: Motion, cadence: f64) -> MatchFrame {
    let mut engine = SensitivityEngine::default();
    assert!(engine.start(m.cpi[1] as i32, 10.0, 0.0));
    let mut next_update = 500.0;
    for (target, packet) in events(m) {
        // A prefix contains all arrivals through its watermark before update is called.
        if packet.time > next_update {
            engine.update(next_update);
            next_update = packet.time + cadence;
        }
        engine.push(target, packet);
    }
    engine.update(3000.0);
    engine.frame()
}

fn accurate(m: Motion, tolerance: f64) {
    let frame = run(m, 20.0);
    assert_eq!(
        frame.failure,
        Failure::None,
        "cpi={:?} hz={:?} mount={} curve={} lag={}: {frame:?}",
        m.cpi,
        m.hz,
        m.mount,
        m.curve,
        m.lag
    );
    let result = frame
        .result
        .as_ref()
        .unwrap_or_else(|| panic!("incomplete: {frame:?}, cpi={:?}", m.cpi));
    let expected = m.cpi[0] / m.cpi[1];
    assert!(
        (result.scale / expected - 1.0).abs() <= tolerance,
        "{result:?}, truth={expected}, hz={:?}",
        m.hz
    );
    assert!(result.segment_spread_percent <= 2.0);
    assert!(
        (f64::from(result.recommended_target_dpi) - m.cpi[0]).abs() <= m.cpi[0] * tolerance + 0.5,
        "{result:?}, expected target DPI={}",
        m.cpi[0]
    );
}

#[test]
fn same_swipe_matches_across_report_rates_and_cpi_ratios() {
    for hz in [
        [125, 1000],
        [1000, 125],
        [500, 8000],
        [8000, 500],
        [1000, 1000],
    ] {
        for cpi in [
            [800.0, 800.0],
            [800.0, 840.0],
            [800.0, 400.0],
            [400.0, 1600.0],
        ] {
            accurate(
                Motion {
                    hz,
                    cpi,
                    ..Motion::default()
                },
                0.01,
            );
        }
    }
}

#[test]
fn fixed_mount_and_shared_curves_do_not_need_a_straight_line() {
    for curve in [0.0, 10.0, 30.0] {
        for mount in [-25.0, 0.0, 25.0] {
            accurate(
                Motion {
                    curve,
                    mount,
                    cpi: [800.0, 840.0],
                    ..Motion::default()
                },
                0.01,
            );
        }
    }
}

#[test]
fn arrival_lag_and_small_jitter_are_handled_on_shared_intervals() {
    for lag in [-18.0, 0.0, 18.0] {
        let motion = Motion {
            lag,
            jitter: 0.15,
            hz: [125, 1000],
            ..Motion::default()
        };
        accurate(motion, 0.01);
        let result = run(motion, 20.0).result.unwrap();
        assert!((result.lag_ms + lag).abs() <= 2.0, "{result:?}");
    }
}

#[test]
fn isolated_disturbance_is_downweighted_but_sustained_slip_is_rejected() {
    accurate(
        Motion {
            spike: 1.0,
            ..Motion::default()
        },
        0.01,
    );
    let frame = run(
        Motion {
            slip: 25.0,
            ..Motion::default()
        },
        20.0,
    );
    // The target reaches 10 cm earlier under slip; the boundary-lag gate rejects first.
    assert_eq!(frame.failure, Failure::Unsynchronized, "{frame:?}");
    assert!(frame.result.is_none());
}

#[test]
fn incompatible_direction_is_rejected() {
    assert_eq!(
        run(
            Motion {
                mount: 90.0,
                ..Motion::default()
            },
            20.0
        )
        .failure,
        Failure::DirectionMismatch
    );
}

#[test]
fn result_is_independent_of_worker_poll_cadence() {
    let motion = Motion {
        mount: 20.0,
        lag: 12.0,
        hz: [500, 8000],
        ..Motion::default()
    };
    let a = run(motion, 20.0).result.unwrap();
    let b = run(motion, 200.0).result.unwrap();
    assert!((a.scale - b.scale).abs() < 1e-12);
    assert_eq!(a.recommended_target_dpi, b.recommended_target_dpi);
}

#[test]
fn low_and_high_dpi_preserve_count_quantization_accuracy() {
    for cpi in [
        [50.0, 50.0],
        [100.0, 105.0],
        [3200.0, 3360.0],
        [50_000.0, 48_000.0],
    ] {
        accurate(
            Motion {
                cpi,
                hz: [125, 8000],
                ..Motion::default()
            },
            0.01,
        );
    }
}

#[test]
fn varying_gain_and_out_of_range_lag_do_not_publish_a_result() {
    let unstable = run(
        Motion {
            slip: 8.0,
            ..Motion::default()
        },
        20.0,
    );
    assert_eq!(unstable.failure, Failure::UnstableScale, "{unstable:?}");
    assert!(unstable.result.is_none());
    let late = run(
        Motion {
            lag: 80.0,
            ..Motion::default()
        },
        20.0,
    );
    assert_eq!(late.failure, Failure::Unsynchronized);
    assert!(late.result.is_none());
}

#[test]
fn swapping_equal_nominal_devices_gives_reciprocal_results() {
    let a = run(
        Motion {
            cpi: [800.0, 840.0],
            mount: 20.0,
            curve: 10.0,
            ..Motion::default()
        },
        20.0,
    )
    .result
    .unwrap();
    let b = run(
        Motion {
            cpi: [840.0, 800.0],
            mount: -20.0,
            curve: 10.0,
            ..Motion::default()
        },
        20.0,
    )
    .result
    .unwrap();
    assert!((a.scale * b.scale - 1.0).abs() < 0.002);
}
