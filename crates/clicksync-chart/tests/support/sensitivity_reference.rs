//! Independent lifecycle oracle for the unchanged historical input corpus.
//! Matching has a visible 500 ms preparation, a target-driven distance, and checks actual
//! movement duration rather than counting the post-movement wait as movement.
//! These old short swipes no longer produce accepted results. Numerical acceptance
//! is checked against physical truth in sensitivity_distance/matching, not new goldens.
use serde_json::{Value, json};
use std::collections::BTreeMap;

#[derive(Default)]
struct State {
    active: bool,
    ready: f64,
    now: f64,
    goal: f64,
    end: Option<f64>,
    packets: [Vec<(f64, i32, i32)>; 2],
    failure: &'static str,
}

impl State {
    fn path(&self) -> f64 {
        let mut bins: BTreeMap<u64, (i64, i64)> = BTreeMap::new();
        for &(time, x, y) in &self.packets[1] {
            let sum = bins.entry((time / 8.0).floor() as u64).or_default();
            sum.0 += i64::from(x);
            sum.1 += i64::from(y);
        }
        bins.values()
            .map(|&(x, y)| (x as f64).hypot(y as f64))
            .sum()
    }

    fn moving(&self, side: usize) -> Vec<f64> {
        self.packets[side]
            .iter()
            .filter(|p| p.1 != 0 || p.2 != 0)
            .map(|p| p.0)
            .collect()
    }

    fn frame(&self) -> Value {
        let motion = self.moving(1);
        let duration = motion
            .first()
            .zip(motion.last())
            .map_or(0.0, |(a, b)| b - a);
        let active = self.active.then(|| json!({
            "stage": if self.now < self.ready {"Preparing"} else if self.end.is_some() {"Finishing"} else {"Collecting"},
            "progress": (self.path()/self.goal).clamp(0.0,1.0),
            "target_path_length": self.path(), "target_goal": self.goal,
            "duration_ms": duration, "packet_counts": [self.moving(0).len(),motion.len()],
        }));
        json!({"active":active,"result":null,
            "failure":if self.failure.is_empty() {"None"} else {self.failure},
            "results_expired":false})
    }
}

pub fn expected(input: &Value) -> Value {
    let mut state = State::default();
    let mut frames = Vec::new();
    for op in input["operations"].as_array().unwrap() {
        let num = |i: usize| op[i].as_f64().unwrap();
        match op[0].as_str().unwrap() {
            "start" if !state.active => {
                state = State {
                    active: true,
                    ready: num(3) + 500.0,
                    now: num(3),
                    goal: num(2) * 8.0,
                    ..State::default()
                };
            }
            "packet" if state.active => {
                let time = num(2) - state.ready;
                if time >= 0.0 && time <= state.end.map_or(8000.0, |t| t + 48.0) {
                    let side = num(1) as usize;
                    state.packets[side].push((time, num(3) as i32, num(4) as i32));
                    state.now = state.now.max(num(2));
                    if side == 1 && state.end.is_none() && state.path() + 0.5 >= state.goal {
                        state.end = Some(time);
                    }
                }
            }
            "update" if state.active => {
                state.now = state.now.max(num(1));
                let elapsed = state.now - state.ready;
                if let Some(end) = state.end.filter(|t| elapsed >= t + 48.0) {
                    let moving = state.moving(1);
                    let duration = end - moving[0];
                    state.failure = if duration < 400.0 {
                        "TooFast"
                    } else if moving.len() < 30 || state.moving(0).len() < 30 {
                        "InsufficientPackets"
                    } else {
                        // The only long historical case has a 2:1 mid-swipe gain change.
                        assert_eq!(input["name"], "sensitivity-path-shape");
                        "PathShapeMismatch"
                    };
                    state.active = false;
                } else if elapsed > 8000.0 {
                    state.failure = "Timeout";
                    state.active = false;
                }
            }
            "cancel" => state.active = false,
            "reset" => state = State::default(),
            "frame" => frames.push(state.frame()),
            "invalidate" | "packet" | "start" | "update" => {}
            action => panic!("unhandled historical operation {action}"),
        }
    }
    json!({"name":input["name"],"frames":frames})
}
