mod common;
use clicksync_chart::{
    Axis, ChartEngine, ChartParameters, DatasetId, FixedPrefix, HistogramPolicy, HistogramScale,
    HistogramSpec, Metric, TimeBasis,
    analysis::detail::{
        DetailStatistics, HistogramStatistics, StatisticKind as K, StatisticValue as V,
    },
};
use clicksync_core::{RawMouseMovementMode, RawMouseRecord, SessionSnapshot};

fn snapshot(records: &[(u64, i32, i32)]) -> SessionSnapshot {
    let mut session = common::session(4, "detail");
    session.begin_collecting(1000, true, false).unwrap();
    for (index, &(ticks, dx, dy)) in records.iter().enumerate() {
        session
            .append(RawMouseRecord::new(
                dx,
                dy,
                1000 + ticks,
                index as u64 + 1,
                index as u64 + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    session.pause();
    session.snapshot().unwrap()
}

fn summary(
    snapshot: &SessionSnapshot,
    metric: Metric,
    secondary: Option<Metric>,
    range: Option<(f64, f64)>,
    histogram: bool,
    cpi: Option<f64>,
) -> DetailStatistics {
    let prefix = FixedPrefix::new(DatasetId(1), snapshot);
    let mut parameters = ChartParameters::new(metric);
    parameters.time_basis = TimeBasis::RawCapture;
    parameters.cpi = cpi;
    parameters.histogram = histogram.then_some(HistogramSpec {
        policy: HistogramPolicy::Automatic,
        scale: HistogramScale::Percent,
    });
    let mut primary = ChartEngine::new(parameters.clone(), 4).unwrap();
    primary.advance(prefix).unwrap();
    let secondary = secondary.map(|metric| {
        parameters.metric = metric;
        let mut engine = ChartEngine::new(parameters, 4).unwrap();
        engine.advance(prefix).unwrap();
        engine
    });
    DetailStatistics::compute(prefix, &primary, secondary.as_ref(), range)
}

fn row(summary: &DetailStatistics, kind: K, axis: Option<Axis>) -> Option<V> {
    summary
        .rows
        .iter()
        .find(|row| row.kind == kind && row.axis == axis)
        .unwrap()
        .value
}

fn number(summary: &DetailStatistics, kind: K, axis: Option<Axis>) -> f64 {
    match row(summary, kind, axis).unwrap() {
        V::Scalar(value) => value,
        V::Integer(value) => value as f64,
    }
}

fn close(actual: f64, expected: f64) {
    assert!((actual - expected).abs() < 1e-10, "{actual} != {expected}");
}

#[test]
fn trajectory_and_path_measure_report_motion_not_coordinate_distribution() {
    // A closed triangle: travel 3+4+5, coordinate coverage 3 by 4.
    let input = snapshot(&[(0, 3, 0), (1000, 0, 4), (2000, -3, -4), (3000, 0, 0)]);
    for metric in [Metric::Trajectory, Metric::PathLength] {
        let stats = summary(&input, metric, None, None, false, Some(254.0));
        assert_eq!(stats.sample_count, 4);
        close(number(&stats, K::PathLength, None), 12.0);
        let expected = if metric == Metric::Trajectory {
            vec![K::Samples, K::PathLength, K::Width, K::Height]
        } else {
            vec![K::Samples, K::PathLength]
        };
        assert_eq!(
            stats.rows.iter().map(|row| row.kind).collect::<Vec<_>>(),
            expected
        );
        if metric == Metric::Trajectory {
            assert_eq!(row(&stats, K::Width, None), Some(V::Integer(3)));
            assert_eq!(row(&stats, K::Height, None), Some(V::Integer(4)));
        }
        let selected = summary(&input, metric, None, Some((1.0, 2.0)), false, Some(254.0));
        close(number(&selected, K::PathLength, None), 9.0);
        assert_eq!(selected.sample_count, 2);
    }
}

#[test]
fn xy_counts_share_sample_count_and_preserve_each_axis_amplitude() {
    let input = snapshot(&[(0, 3, 4), (1000, -3, 0), (2000, 0, -2), (3000, 0, 2)]);
    let stats = summary(
        &input,
        Metric::Count(Axis::X),
        Some(Metric::Count(Axis::Y)),
        None,
        false,
        None,
    );
    assert_eq!(row(&stats, K::Samples, None), Some(V::Integer(4)));
    assert_eq!(
        stats
            .rows
            .iter()
            .map(|row| (row.kind, row.axis))
            .collect::<Vec<_>>(),
        [
            (K::Samples, None),
            (K::MeanAbsolute, Some(Axis::X)),
            (K::PeakAbsolute, Some(Axis::X)),
            (K::MeanAbsolute, Some(Axis::Y)),
            (K::PeakAbsolute, Some(Axis::Y)),
        ]
    );
    close(number(&stats, K::MeanAbsolute, Some(Axis::X)), 1.5);
    close(number(&stats, K::PeakAbsolute, Some(Axis::X)), 3.0);
    close(number(&stats, K::MeanAbsolute, Some(Axis::Y)), 2.0);
    close(number(&stats, K::PeakAbsolute, Some(Axis::Y)), 4.0);
}

#[test]
fn cumulative_uses_coordinate_span_and_selected_axis_travel() {
    let input = snapshot(&[(0, 3, 4), (1000, 2, -1), (2000, -4, 5)]);
    let stats = summary(
        &input,
        Metric::Cumulative(Axis::X),
        Some(Metric::Cumulative(Axis::Y)),
        Some((1.0, 2.0)),
        false,
        None,
    );
    assert_eq!(stats.rows.len(), 5);
    for (axis, span, travel) in [(Axis::X, 4, 6), (Axis::Y, 5, 6)] {
        for (kind, value) in [(K::Span, span), (K::AbsoluteTravel, travel)] {
            assert_eq!(row(&stats, kind, Some(axis)), Some(V::Integer(value)));
        }
    }
    assert!(
        !stats
            .rows
            .iter()
            .any(|row| matches!(row.kind, K::Mean | K::P95))
    );
}

#[test]
fn time_selection_keeps_unordered_holes_out_of_travel_and_never_subtracts_prefixes() {
    let input = snapshot(&[(0, 3, 0), (9000, 1000, 0), (1000, 0, 4), (2000, -3, 0)]);
    let stats = summary(
        &input,
        Metric::PathLength,
        None,
        Some((0.0, 2.0)),
        false,
        None,
    );
    assert_eq!(stats.sample_count, 3);
    close(number(&stats, K::PathLength, None), 10.0);
    assert_eq!(stats.rows.len(), 2);
}

#[test]
fn timing_summaries_keep_population_jitter_and_the_relevant_tail() {
    let input = snapshot(&[(0, 0, 0), (1000, 0, 0), (4000, 0, 0)]);
    let intervals = summary(&input, Metric::Interval, None, None, false, None);
    close(number(&intervals, K::Mean, None), 2.0);
    close(number(&intervals, K::StandardDeviation, None), 1.0);
    close(number(&intervals, K::P99, None), 2.98);
    close(number(&intervals, K::Maximum, None), 3.0);
    assert_eq!(intervals.rows.len(), 5);
    let frequencies = summary(&input, Metric::RawFrequency, None, None, false, None);
    close(number(&frequencies, K::Mean, None), 2000.0 / 3.0);
    close(
        number(&frequencies, K::StandardDeviation, None),
        1000.0 / 3.0,
    );
    close(number(&frequencies, K::P05, None), 1100.0 / 3.0);
    assert_eq!(frequencies.rows.len(), 4);
}

#[test]
fn velocity_amplitudes_preserve_xy_axes_and_match_histogram_magnitudes() {
    let records: Vec<_> = (0..40).map(|i| (i * 1000, -2, 4)).collect();
    let input = snapshot(&records);
    let stats = summary(
        &input,
        Metric::Velocity(Axis::X),
        Some(Metric::Velocity(Axis::Y)),
        None,
        false,
        Some(254.0),
    );
    assert_eq!(stats.rows.len(), 5);
    close(number(&stats, K::MeanAbsolute, Some(Axis::X)), 0.2);
    close(number(&stats, K::PeakAbsolute, Some(Axis::X)), 0.2);
    close(number(&stats, K::MeanAbsolute, Some(Axis::Y)), 0.4);
    close(number(&stats, K::PeakAbsolute, Some(Axis::Y)), 0.4);
    let histogram = summary(
        &input,
        Metric::Velocity(Axis::X),
        None,
        None,
        true,
        Some(254.0),
    );
    close(number(&histogram, K::Mean, None), 0.2);
    close(number(&histogram, K::StandardDeviation, None), 0.0);
    assert_eq!(histogram.rows.len(), 3);
}

#[test]
fn empty_and_stationary_inputs_have_explicit_counts_and_stable_rows() {
    for records in [&[][..], &[(0, 0, 0)][..], &[(0, 0, 0), (1000, 0, 0)][..]] {
        let input = snapshot(records);
        let stats = summary(&input, Metric::Trajectory, None, None, false, None);
        assert_eq!(stats.sample_count, records.len());
        if !records.is_empty() {
            close(number(&stats, K::PathLength, None), 0.0);
            assert_eq!(row(&stats, K::Width, None), Some(V::Integer(0)));
            assert_eq!(row(&stats, K::Height, None), Some(V::Integer(0)));
        } else {
            assert_eq!(stats.rows.len(), 1);
        }
        let empty = summary(
            &input,
            Metric::Interval,
            None,
            Some((100.0, 200.0)),
            false,
            None,
        );
        assert_eq!(empty.sample_count, 0);
        assert_eq!(empty.rows.len(), 1);
        assert_eq!(row(&empty, K::Samples, None), Some(V::Integer(0)));
        let paired = summary(
            &input,
            Metric::Count(Axis::X),
            Some(Metric::Count(Axis::Y)),
            Some((100.0, 200.0)),
            false,
            None,
        );
        assert_eq!(paired.rows, empty.rows);
    }
    for count in [1000, 1001] {
        let records: Vec<_> = (0..count).map(|i| (i * 1000, 0, 0)).collect();
        let input = snapshot(&records);
        let stats = summary(&input, Metric::Interval, None, None, false, None);
        assert_eq!(stats.rows.len(), 5);
        assert_eq!(stats.sample_count, count as usize - 1);
    }
}

#[test]
fn histogram_mode_uses_counts_not_percent_or_height_and_breaks_ties_left() {
    use clicksync_chart::analysis::histogram::HistogramBin;
    let bins = [
        HistogramBin {
            minimum: -0.5,
            maximum: 0.5,
            count: 3,
            value: 30.0,
        },
        HistogramBin {
            minimum: 0.5,
            maximum: 1.5,
            count: 3,
            value: 999.0,
        },
        HistogramBin {
            minimum: 1.5,
            maximum: 2.5,
            count: 0,
            value: 0.0,
        },
    ];
    let stats = HistogramStatistics::compute(&bins).unwrap();
    assert_eq!(stats.modal_bin.minimum, -0.5);
    close(stats.modal_share, 50.0);
    assert!(!stats.last);
    assert!(HistogramStatistics::compute(&bins[2..]).is_none());
}

#[test]
fn summaries_without_quantiles_have_history_independent_allocations() {
    for (metric, histogram) in [
        (Metric::Trajectory, false),
        (Metric::PathLength, false),
        (Metric::Cumulative(Axis::X), false),
        (Metric::Count(Axis::X), false),
        (Metric::CountTrend(Axis::X), false),
        (Metric::IntervalTrend, false),
        (Metric::Velocity(Axis::X), false),
        (Metric::Count(Axis::X), true),
        (Metric::Interval, true),
        (Metric::Velocity(Axis::X), true),
    ] {
        let mut previous = None;
        for count in [32, 8192] {
            let records: Vec<_> = (0..count).map(|i| (i * 1000, 3, -4)).collect();
            let input = snapshot(&records);
            let prefix = FixedPrefix::new(DatasetId(1), &input);
            let mut parameters = ChartParameters::new(metric);
            parameters.cpi = Some(800.0);
            parameters.histogram = histogram.then_some(HistogramSpec {
                policy: HistogramPolicy::Automatic,
                scale: HistogramScale::Percent,
            });
            let mut engine = ChartEngine::new(parameters, 8192).unwrap();
            engine.advance(prefix).unwrap();
            let expected_count = engine.geometry().samples().count();
            let allocations = allocation_counter::measure(|| {
                let stats =
                    DetailStatistics::compute(prefix, &engine, None, Some((0.0, count as f64)));
                assert_eq!(stats.sample_count, expected_count);
                std::hint::black_box(stats);
            });
            let payload = (allocations.count_total, allocations.bytes_total);
            assert!(payload.0 > 0);
            if let Some(previous) = previous {
                assert_eq!(
                    previous, payload,
                    "{metric:?} / {histogram} must not allocate history-sized scratch"
                );
            }
            previous = Some(payload);
        }
    }
}

#[test]
fn pause_boundaries_neither_add_motion_nor_create_raw_timing_intervals() {
    let mut session = common::session(4, "segments");
    session.begin_collecting(1000, true, false).unwrap();
    session
        .append(RawMouseRecord::new(
            3,
            4,
            1000,
            1,
            1,
            RawMouseMovementMode::Relative,
        ))
        .unwrap();
    session
        .append(RawMouseRecord::new(
            0,
            0,
            2000,
            2,
            2,
            RawMouseMovementMode::Relative,
        ))
        .unwrap();
    session.pause();
    session.begin_collecting(1_000_000, false, false).unwrap();
    session
        .append(RawMouseRecord::new(
            -3,
            -4,
            1_000_000,
            3,
            3,
            RawMouseMovementMode::Relative,
        ))
        .unwrap();
    session.pause();
    let input = session.snapshot().unwrap();
    let motion = summary(&input, Metric::Trajectory, None, None, false, None);
    close(number(&motion, K::PathLength, None), 10.0);
    assert_eq!(motion.sample_count, 3);
    let timing = summary(&input, Metric::Interval, None, None, false, None);
    assert_eq!(timing.sample_count, 1);
    close(number(&timing, K::Mean, None), 1.0);
}
