#![allow(dead_code)]

use clicksync_chart::{
    AnalysisOptions, Axis, ChartParameters, HistogramLayout, HistogramPolicy, HistogramScale,
    HistogramSpec, Metric, TimeBasis,
};
use clicksync_core::{
    RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SessionMetadata, SessionSnapshot,
    SharedByteBudget,
};

pub const FREQUENCY: u64 = 1_000_000;

pub fn session(chunk_capacity: usize, device: &str) -> Session {
    let config = SessionConfig {
        max_session_events: 100_000,
        warning_event_threshold: 90_000,
        chunk_capacity,
        managed_memory_budget_bytes: 64 * 1024 * 1024,
        timestamp_frequency: FREQUENCY,
    };
    Session::new(
        config.clone(),
        SharedByteBudget::new(config.managed_memory_budget_bytes),
        SessionMetadata {
            device_id: device.into(),
            ..SessionMetadata::default()
        },
    )
    .unwrap()
}

pub fn record(index: usize, ticks: u64) -> RawMouseRecord {
    let xs = [0, 3, -4, 0, 50, -6, 1, 0, 2];
    let ys = [0, 4, 0, 0, -2, 8, -1, 0, 2];
    let mut record = RawMouseRecord::new(
        xs[index % xs.len()],
        ys[index % ys.len()],
        ticks,
        index as u64 + 1,
        index as u64 + 1,
        RawMouseMovementMode::Relative,
    );
    if index.is_multiple_of(13) {
        record.movement_mode = RawMouseMovementMode::Absolute;
    }
    record
}

pub fn replay(
    chunk_capacity: usize,
    count: usize,
    rate: u64,
    segmented: bool,
) -> Vec<SessionSnapshot> {
    let mut session = session(chunk_capacity, "fixture-a");
    session.begin_collecting(1_000, true, true).unwrap();
    let mut snapshots = vec![session.snapshot().unwrap()];
    let step = FREQUENCY / rate;
    let split = count / 2;
    for i in 0..count {
        if segmented && i == split {
            session.pause();
            snapshots.push(session.snapshot().unwrap());
            session
                .begin_collecting(1_000 + i as u64 * step + 90_000, false, true)
                .unwrap();
        }
        let offset = if segmented && i >= split { 90_000 } else { 0 };
        let ticks = 1_000 + offset + i.saturating_sub(usize::from(i % 11 == 3)) as u64 * step;
        session.append(record(i, ticks)).unwrap();
        snapshots.push(session.snapshot().unwrap());
    }
    session.stop();
    snapshots.push(session.snapshot().unwrap());
    snapshots
}

pub fn metrics() -> Vec<Metric> {
    vec![
        Metric::Count(Axis::X),
        Metric::Count(Axis::Y),
        Metric::DeltaMagnitude,
        Metric::CountTrend(Axis::X),
        Metric::CountTrend(Axis::Y),
        Metric::Interval,
        Metric::RawFrequency,
        Metric::IntervalTrend,
        Metric::Frequency,
        Metric::Cumulative(Axis::X),
        Metric::Cumulative(Axis::Y),
        Metric::Trajectory,
        Metric::PathLength,
        Metric::Velocity(Axis::X),
        Metric::Velocity(Axis::Y),
        Metric::VelocityMagnitude,
        Metric::PathSpeed,
    ]
}

pub fn parameters(metric: Metric, basis: TimeBasis) -> ChartParameters {
    let mut parameters = ChartParameters::new(metric);
    parameters.time_basis = basis;
    parameters.cpi = Some(800.0);
    parameters
}

pub fn fixed_histogram() -> HistogramSpec {
    HistogramSpec {
        policy: HistogramPolicy::Fixed(HistogramLayout {
            minimum: -2.0,
            maximum: 6.0,
            width: 1.0,
            bins: 8,
        }),
        scale: HistogramScale::Percent,
    }
}

pub fn reference_cases() -> Vec<(String, SessionSnapshot, ChartParameters)> {
    let mut cases = Vec::new();
    for (label, rate, segmented) in [("8k-segments", 8000, true), ("1k", 1000, false)] {
        let snapshots = replay(4, 31, rate, segmented);
        for count in 1..=31 {
            let snapshot = snapshots.iter().find(|s| s.event_count() == count).unwrap();
            for basis in [TimeBasis::RawCapture, TimeBasis::Logical] {
                for metric in metrics().into_iter().filter(|m| *m != Metric::PathLength) {
                    if !matches!(
                        metric,
                        Metric::Velocity(_) | Metric::VelocityMagnitude | Metric::PathSpeed
                    ) && ![1, 2, 4, 7, 16, 31].contains(&count)
                    {
                        continue;
                    }
                    let p = parameters(metric, basis);
                    cases.push((
                        format!("{label}-{count}-{basis:?}-{metric:?}"),
                        snapshot.clone(),
                        p,
                    ));
                }
            }
        }
        let final_snapshot = snapshots.last().unwrap();
        for metric in [
            Metric::Velocity(Axis::X),
            Metric::PathSpeed,
            Metric::IntervalTrend,
        ] {
            let mut p = parameters(metric, TimeBasis::RawCapture);
            p.options = AnalysisOptions::new(1.0, 3, 1.0, 3, 0.49, 8.0);
            cases.push((
                format!("{label}-short-{metric:?}"),
                final_snapshot.clone(),
                p,
            ));
        }
    }
    cases
}

pub fn metric_name(metric: Metric) -> &'static str {
    match metric {
        Metric::Count(Axis::X) => "count-x",
        Metric::Count(Axis::Y) => "count-y",
        Metric::DeltaMagnitude => "delta-magnitude",
        Metric::CountTrend(Axis::X) => "trend-x",
        Metric::CountTrend(Axis::Y) => "trend-y",
        Metric::Interval => "interval",
        Metric::RawFrequency => "raw-frequency",
        Metric::IntervalTrend => "interval-trend",
        Metric::Frequency => "frequency",
        Metric::Cumulative(Axis::X) => "cumulative-x",
        Metric::Cumulative(Axis::Y) => "cumulative-y",
        Metric::Trajectory => "trajectory",
        Metric::PathLength => "path-length",
        Metric::Velocity(Axis::X) => "velocity-x",
        Metric::Velocity(Axis::Y) => "velocity-y",
        Metric::VelocityMagnitude => "velocity-magnitude",
        Metric::PathSpeed => "path-speed",
    }
}

pub fn write_report(name: &str, document: serde_json::Value) {
    let directory = std::env::var_os("CLICKSYNC_CHART_REPORT_DIR")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| {
            std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../artifacts/ChartCpu")
        });
    std::fs::create_dir_all(&directory).unwrap();
    std::fs::write(
        directory.join(name),
        serde_json::to_vec_pretty(&document).unwrap(),
    )
    .unwrap();
}
