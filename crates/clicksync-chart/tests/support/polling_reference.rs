//! Independent batch-count reference for the unchanged polling operation corpus.
//! The historical interval-estimator reference remains frozen; v2 counts (now-300, now].
use serde_json::{Value, json};

pub fn expected(input: &Value) -> Value {
    let mut packets: Vec<(f64, bool)> = Vec::new();
    let mut history: Vec<Value> = Vec::new();
    let mut frames = Vec::new();
    let mut motion = false;
    let mut eligible: Option<f64> = None;
    let mut first: Option<f64> = None;
    let mut latest = f64::NEG_INFINITY;
    let mut peak = 0;
    let mut peak_after = f64::INFINITY;
    let mut candidate: Option<(f64, i32)> = None;
    let mut paused: Option<f64> = None;
    let mut pause_total = 0.0;
    let mut interval = 1000.0 / 120.0;
    let mut observed = f64::NEG_INFINITY;
    let mut segment = 0u64;
    for op in input["operations"].as_array().unwrap() {
        let time = || op[1].as_f64().unwrap();
        match op[0].as_str().unwrap() {
            "mode" | "reset" | "begin" => {
                let action = op[0].as_str().unwrap();
                if action == "mode" {
                    let next = op[1].as_i64().unwrap() == 1;
                    if next == motion {
                        continue;
                    }
                    motion = next;
                }
                packets.clear();
                eligible = None;
                first = None;
                latest = f64::NEG_INFINITY;
                candidate = None;
                if action == "begin" {
                    if let Some(p) = paused {
                        pause_total += (time() - p).max(0.0);
                    }
                    paused = None;
                    peak_after = time() + 600.0;
                    segment += 1;
                } else {
                    history.clear();
                    observed = f64::NEG_INFINITY;
                    peak = 0;
                    segment = 0;
                    paused = None;
                    pause_total = 0.0;
                    peak_after = f64::INFINITY;
                }
            }
            "interval" => {
                interval = if time().is_finite() && time() > 0.0 {
                    time().clamp(1.0, 100.0)
                } else {
                    1000.0 / 120.0
                };
            }
            "end" => {
                if paused.is_none() {
                    paused = Some(time());
                }
                candidate = None;
                peak_after = f64::INFINITY;
            }
            "packet" => {
                let t = time().max(latest);
                latest = t;
                let empty = op[2].as_i64().unwrap() == 0 && op[3].as_i64().unwrap() == 0;
                if (!motion || !empty) && first.is_none() {
                    first = Some(t);
                    eligible = Some(t + 310.0);
                }
                packets.push((t, empty));
                if (!motion || !empty) && eligible.is_some_and(|e| t >= e) && t >= peak_after {
                    let rate = count(&packets, motion, t);
                    let tier = [125, 250, 500, 1000, 2000, 4000, 8000]
                        .into_iter()
                        .find(|v| ((rate - *v as f64) / *v as f64).abs() <= 0.015)
                        .unwrap_or(rate.round_ties_even() as i32);
                    if tier <= peak {
                        candidate = None;
                    } else if let Some((start, minimum)) = candidate {
                        let minimum = minimum.min(tier);
                        if t - start >= 20.0 {
                            peak = minimum;
                            candidate = None;
                        } else {
                            candidate = Some((start, minimum));
                        }
                    } else {
                        candidate = Some((t, tier));
                    }
                }
            }
            "frame" => {
                let t = time();
                let rate = eligible
                    .filter(|e| t >= *e)
                    .filter(|_| t >= latest)
                    .map(|_| count(&packets, motion, t));
                let status = if eligible.is_none_or(|e| t < e) {
                    "WarmingUp"
                } else if t < latest {
                    "Delayed"
                } else {
                    "Valid"
                };
                let empty = if rate.is_some() && motion && first.is_some_and(|f| t >= f + 1000.0) {
                    let items: Vec<_> = packets
                        .iter()
                        .filter(|(p, _)| *p > t - 1000.0 && *p <= t)
                        .collect();
                    if items.is_empty() {
                        None
                    } else {
                        Some(
                            items.iter().filter(|(_, empty)| *empty).count() as f64 * 100.0
                                / items.len() as f64,
                        )
                    }
                } else {
                    None
                };
                if let Some(rate) = rate
                    && paused.is_none()
                    && t - observed >= interval
                {
                    let logical = t - pause_total;
                    if history
                        .last()
                        .is_none_or(|p| p["logical_ms"].as_f64().unwrap() < logical)
                    {
                        history.push(json!({"logical_ms":logical,"observed_ms":t,"rate":rate,"segment":segment}));
                        history.retain(|p| p["logical_ms"].as_f64().unwrap() >= logical - 3000.0);
                        observed = t;
                    }
                }
                frames.push(json!({"metrics":{"current_rate":rate.map(|r| r.round_ties_even() as i32),"peak_rate":peak,
                    "empty_packet_percent":empty,"dropped_packets":7,"status":status},"history":history,"rate":rate}));
            }
            action => panic!("unexpected polling operation {action}"),
        }
    }
    json!({"name":input["name"], "frames":frames})
}
fn count(packets: &[(f64, bool)], motion: bool, now: f64) -> f64 {
    let begin = packets.partition_point(|(t, _)| *t <= now - 300.0);
    packets[begin..]
        .iter()
        .filter(|(t, empty)| *t <= now && (!motion || !empty))
        .count() as f64
        * (1000.0 / 300.0)
}
