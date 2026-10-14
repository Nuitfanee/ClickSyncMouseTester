mod common;

use clicksync_chart::{
    ChartEngine, DatasetId, FixedPrefix, HistogramPolicy, HistogramScale, HistogramSpec, Metric,
};
use serde_json::Value;

fn near(actual: f64, expected: f64, label: &str) {
    let tolerance = 1e-11_f64.max(expected.abs() * 1e-12);
    assert!(
        (actual - expected).abs() <= tolerance,
        "{label}: actual={actual:.17} expected={expected:.17} tolerance={tolerance}"
    );
}

#[test]
fn cpu_values_and_histograms_match_frozen_reference() {
    let document: Value =
        serde_json::from_str(include_str!("fixtures/chart-reference.json")).unwrap();
    let expected = document["cases"].as_array().unwrap();
    let fixtures = common::reference_cases();
    assert_eq!(expected.len(), fixtures.len());
    for ((name, snapshot, parameters), reference) in fixtures.into_iter().zip(expected) {
        assert_eq!(reference["name"].as_str().unwrap(), name);
        let prefix = FixedPrefix::new(DatasetId(1), &snapshot);
        let mut engine = ChartEngine::new(parameters.clone(), 7).unwrap();
        engine.finish(prefix).unwrap();
        let points: Vec<_> = engine.geometry().samples().collect();
        let reference_points = reference["points"].as_array().unwrap();
        assert_eq!(points.len(), reference_points.len(), "{name}");
        for (point, reference) in points.iter().zip(reference_points) {
            near(
                point.point.x.value(),
                reference[0].as_f64().unwrap(),
                &format!("{name}:x"),
            );
            near(
                point.point.y.value(),
                reference[1].as_f64().unwrap(),
                &format!("{name}:y"),
            );
        }
        if parameters.metric == Metric::Trajectory {
            continue;
        }
        for (policy, field) in [
            (common::fixed_histogram().policy, "fixed_bins"),
            (HistogramPolicy::Automatic, "automatic_bins"),
        ] {
            let mut p = parameters.clone();
            p.histogram = Some(HistogramSpec {
                policy,
                scale: HistogramScale::Percent,
            });
            let mut histogram_engine = ChartEngine::new(p, 7).unwrap();
            histogram_engine.finish(prefix).unwrap();
            let bins = histogram_engine.histogram().unwrap().bins();
            let reference_bins = reference[field].as_array().unwrap();
            assert_eq!(bins.len(), reference_bins.len(), "{name}:{field}");
            for (bin, reference) in bins.iter().zip(reference_bins) {
                near(bin.minimum, reference[0].as_f64().unwrap(), &name);
                near(bin.maximum, reference[1].as_f64().unwrap(), &name);
                near(bin.value, reference[2].as_f64().unwrap(), &name);
                assert_eq!(bin.count as u64, reference[3].as_u64().unwrap(), "{name}");
            }
        }
    }
    common::write_report(
        "chart-reference.json",
        serde_json::json!({
            "schema": "clicksync-chart-reference-check-v1",
            "cases": expected.len(),
            "matches_reference": true,
            "absolute_tolerance": 1e-11,
            "relative_tolerance": 1e-12,
        }),
    );
}

#[test]
fn every_live_velocity_prefix_matches_reference_for_both_time_bases() {
    let document: Value =
        serde_json::from_str(include_str!("fixtures/chart-reference.json")).unwrap();
    let references: std::collections::HashMap<_, _> = document["cases"]
        .as_array()
        .unwrap()
        .iter()
        .map(|v| (v["name"].as_str().unwrap(), v))
        .collect();
    for (label, rate, segmented) in [("8k-segments", 8000, true), ("1k", 1000, false)] {
        let snapshots = common::replay(4, 31, rate, segmented);
        for basis in [
            clicksync_chart::TimeBasis::RawCapture,
            clicksync_chart::TimeBasis::Logical,
        ] {
            for metric in [
                Metric::Velocity(clicksync_chart::Axis::X),
                Metric::Velocity(clicksync_chart::Axis::Y),
                Metric::VelocityMagnitude,
                Metric::PathSpeed,
            ] {
                let mut engine = ChartEngine::new(common::parameters(metric, basis), 7).unwrap();
                for snapshot in snapshots.iter().filter(|s| s.event_count() > 0) {
                    let name = format!("{label}-{}-{basis:?}-{metric:?}", snapshot.event_count());
                    let reference = references[name.as_str()]["points"].as_array().unwrap();
                    let delta = engine
                        .advance(FixedPrefix::new(DatasetId(1), snapshot))
                        .unwrap();
                    assert_eq!(delta.state.published_end, snapshot.event_count());
                    let points: Vec<_> = engine.geometry().samples().collect();
                    assert_eq!(points.len(), reference.len(), "{name}");
                    for (point, value) in points.iter().zip(reference) {
                        near(point.point.x.value(), value[0].as_f64().unwrap(), &name);
                        near(point.point.y.value(), value[1].as_f64().unwrap(), &name);
                    }
                }
            }
        }
    }
}
