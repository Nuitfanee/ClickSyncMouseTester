//! Explicit CPU/allocation measurement of an 8 kHz single swipe; not a hardware accuracy test.
use clicksync_chart::analysis::sensitivity::{Packet, SensitivityEngine};
use serde_json::json;
use std::{path::PathBuf, time::Instant};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let path = std::env::args_os()
        .nth(1)
        .map(PathBuf::from)
        .unwrap_or_else(|| "artifacts/sensitivity-profile/report.json".into());
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let mut collection = Vec::with_capacity(200);
    let mut completion = Vec::with_capacity(200);
    let mut max_start_bytes = 0;
    for _ in 0..200 {
        let mut engine = SensitivityEngine::default();
        let start = allocation_counter::measure(|| {
            assert!(engine.start(800, 10.0, 0.0));
        });
        max_start_bytes = max_start_bytes.max(start.bytes_total);
        let mut times = [0.0; 2];
        let allocations = allocation_counter::measure(|| {
            let began = Instant::now();
            for i in 0..16_000 {
                let packet = Packet {
                    time: 500.0 + i as f64 * 0.125,
                    dx: i32::from(i % 4 == 0),
                    dy: 0,
                };
                engine.push(false, packet);
                engine.push(true, packet);
            }
            times[0] = began.elapsed().as_secs_f64() * 1000.0;
            let began = Instant::now();
            engine.update(3000.0);
            times[1] = began.elapsed().as_secs_f64() * 1000.0;
        });
        assert_eq!(allocations.count_total, 0);
        assert_eq!(engine.frame().result.unwrap().recommended_target_dpi, 800);
        collection.push(times[0]);
        completion.push(times[1]);
    }
    assert!(max_start_bytes <= 512 * 1024);
    let distribution = |mut values: Vec<f64>| {
        values.sort_unstable_by(f64::total_cmp);
        json!({"n":values.len(), "p50_ms":values[99], "p95_ms":values[189],
            "p99_ms":values[197], "max_ms":values[199]})
    };
    let report = json!({
        "passed":true,
        "configuration":if cfg!(debug_assertions) {"debug"} else {"release"},
        "reports_per_trial":32000, "rate_per_device_hz":8000,
        "start_allocation_bytes":max_start_bytes, "input_and_fit_allocations":0,
        "collection":distribution(collection), "completion":distribution(completion),
        "boundary":"Synthetic CPU/allocation measurement only; no hardware precision or UI frame-rate claim."
    });
    std::fs::write(&path, serde_json::to_vec_pretty(&report)?)?;
    println!("{report}");
    Ok(())
}
