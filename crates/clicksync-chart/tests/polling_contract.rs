use clicksync_chart::analysis::polling::{PollingEngine, PollingMode, RateStatus};

fn feed(p: &mut PollingEngine, hz: usize, end: usize) {
    for i in 0..=hz * end / 1000 {
        p.push(i as f64 * 1000.0 / hz as f64, 1, 0);
    }
}
#[test]
fn fixed_window_counts_include_silence_and_use_half_open_edges() {
    let mut p = PollingEngine::default();
    p.begin_segment(0.0);
    feed(&mut p, 1000, 1000);
    for (now, expected) in [(1000.0, 1000), (1080.0, 733), (1101.0, 663), (1300.0, 0)] {
        assert_eq!(p.metrics(now, 0).current_rate, Some(expected));
        assert_eq!(p.metrics(now, 0).status, RateStatus::Valid);
    }
}
#[test]
fn polling_frequency_cannot_change_eligibility_or_peak_tiers() {
    let mut dense = PollingEngine::default();
    let mut sparse = PollingEngine::default();
    for p in [&mut dense, &mut sparse] {
        p.begin_segment(0.0);
        feed(p, 1000, 1000);
    }
    for now in 1000..1500 {
        dense.metrics(now as f64, 0);
        dense.history_frame(now as f64);
    }
    for t in 1500..=1720 {
        dense.push(t as f64, 1, 0);
        sparse.push(t as f64, 1, 0);
    }
    assert_eq!(dense.metrics(1720.0, 0), sparse.metrics(1720.0, 0));
    assert_eq!(dense.metrics(1720.0, 0).current_rate, Some(737));
    let metrics = dense.metrics(1720.0, 0);
    for _ in 0..100 {
        assert_eq!(dense.metrics(1720.0, 0), metrics);
    }
}
#[test]
fn batch_delivery_counts_agree_with_independent_full_window_oracle() {
    for batch in [1, 10, 50, 100] {
        let mut p = PollingEngine::default();
        p.begin_segment(0.0);
        let mut all = Vec::new();
        for b in 0..=2000 / batch {
            for j in 0..8 * batch {
                let t = (b * batch) as f64 + j as f64 * 0.001;
                p.push(t, 1, 0);
                all.push(t);
            }
        }
        for phase in [0.01, 0.5, 0.99] {
            let now = 2000.0 + batch as f64 * phase;
            if now < *all.last().unwrap() {
                continue;
            }
            let expected =
                all.iter().filter(|&&t| t > now - 300.0 && t <= now).count() as f64 / 0.3;
            assert_eq!(
                p.metrics(now, 0).current_rate,
                Some(expected.round_ties_even() as i32)
            );
        }
    }
}
#[test]
fn tiers_preserve_classification_and_need_new_input_time() {
    let mut p = PollingEngine::default();
    p.begin_segment(0.0);
    feed(&mut p, 990, 2000);
    assert_eq!(p.metrics(2000.0, 0).current_rate, Some(990));
    assert_eq!(p.metrics(2000.0, 0).peak_rate, 1000);
    let mut p = PollingEngine::default();
    p.begin_segment(0.0);
    feed(&mut p, 1000, 600);
    for _ in 0..100 {
        assert_eq!(p.metrics(600.0, 0).peak_rate, 0);
    }
    for t in 601..=620 {
        p.push(t as f64, 1, 0);
    }
    assert_eq!(p.metrics(620.0, 0).peak_rate, 1000);
}
#[test]
fn invalid_samples_are_not_zero_and_pause_does_not_create_history() {
    let mut p = PollingEngine::default();
    p.begin_segment(0.0);
    feed(&mut p, 1000, 200);
    assert_eq!(p.metrics(200.0, 0).current_rate, None);
    assert!(p.history_frame(200.0).0.is_empty());
    for t in 201..=800 {
        p.push(t as f64, 1, 0);
    }
    let history = p.history_frame(800.0).0.clone();
    p.end_segment(800.0);
    assert_eq!(*p.history_frame(5000.0).0, history);
    p.begin_segment(5000.0);
    p.push(5000.0, 1, 0);
    assert_eq!(p.metrics(5000.0, 0).current_rate, None);
    for t in 5001..=5400 {
        p.push(t as f64, 1, 0);
    }
    let h = p.history_frame(5400.0).0;
    assert_eq!(h.back().unwrap().logical_ms, 1200.0);
    assert_ne!(h[0].segment, h[1].segment);
}
#[test]
fn motion_empty_ratio_and_range_limits_are_explicit() {
    let mut p = PollingEngine::default();
    p.set_mode(PollingMode::MotionReportRate);
    p.begin_segment(0.0);
    for i in 0..=16000 {
        p.push(i as f64 / 8.0, if i % 2 == 0 { 0 } else { 1 }, 0);
    }
    let m = p.metrics(2000.0, 0);
    assert_eq!(m.current_rate, Some(4000));
    assert_eq!(m.empty_packet_percent, Some(50.0));
    let mut p = PollingEngine::default();
    p.begin_segment(0.0);
    feed(&mut p, 40000, 1000);
    assert_eq!(p.metrics(1000.0, 0).status, RateStatus::OutOfRange);
    assert_eq!(p.metrics(1000.0, 0).current_rate, None);
}
#[test]
fn bounded_hot_path_and_reset_reuse_allocations() {
    let mut p = PollingEngine::default();
    let allocations = allocation_counter::measure(|| {
        for _ in 0..2 {
            p.reset();
            p.begin_segment(0.0);
            for i in 0..16000 {
                let t = i as f64 / 8.0;
                p.push(t, 1, 0);
                if i % 64 == 0 {
                    p.metrics(t, 0);
                    p.history_frame(t);
                }
            }
        }
    });
    assert_eq!(allocations.count_total, 0);
    assert_eq!(allocations.bytes_total, 0);
}
