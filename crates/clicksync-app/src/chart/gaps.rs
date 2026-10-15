//! Shared marked intervals, visible-window sums, and a pruned drawing index.

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct GapStatistics {
    pub count: usize,
    pub mean_ms: Option<f64>,
}

#[derive(Default)]
pub struct GapIndex {
    starts: Vec<Gap>,
    ends: Vec<(f64, f64)>,
    _reservation: Option<clicksync_core::BudgetReservation>,
}

struct Gap {
    bounds: [f64; 2],
    duration_sum: f64,
    max_end: f64,
}

impl GapIndex {
    pub(super) fn new(
        gaps: Vec<[f64; 2]>,
        reservation: Option<clicksync_core::BudgetReservation>,
    ) -> Self {
        let mut starts: Vec<_> = gaps
            .into_iter()
            .map(|bounds| Gap {
                bounds,
                duration_sum: bounds[1] - bounds[0],
                max_end: bounds[1],
            })
            .collect();
        starts.sort_unstable_by(|a, b| a.bounds[0].total_cmp(&b.bounds[0]));
        let mut ends: Vec<_> = starts
            .iter()
            .map(|gap| (gap.bounds[1], gap.duration_sum))
            .collect();
        ends.sort_unstable_by(|a, b| a.0.total_cmp(&b.0));
        let mut total = 0.0;
        for gap in &mut starts {
            total += gap.duration_sum;
            gap.duration_sum = total;
        }
        total = 0.0;
        for (_, sum) in &mut ends {
            total += *sum;
            *sum = total;
        }
        index_max_end(&mut starts);
        Self {
            starts,
            ends,
            _reservation: reservation,
        }
    }

    /// Count positive-width intersections, using each marked gap's full duration.
    /// Endpoints are indexed independently: raw timestamps can be out of order.
    pub fn query(&self, start_ms: f64, end_ms: f64) -> GapStatistics {
        if !start_ms.is_finite() || !end_ms.is_finite() || end_ms <= start_ms {
            return GapStatistics::default();
        }
        let started = self.starts.partition_point(|gap| gap.bounds[0] < end_ms);
        let ended = self.ends.partition_point(|&(time, _)| time <= start_ms);
        let count = started - ended;
        let started_sum = started
            .checked_sub(1)
            .map_or(0.0, |index| self.starts[index].duration_sum);
        let ended_sum = ended.checked_sub(1).map_or(0.0, |index| self.ends[index].1);
        GapStatistics {
            count,
            mean_ms: (count > 0).then(|| (started_sum - ended_sum) / count as f64),
        }
    }

    /// Visit only overlapping marks in start order; return node visits for workload checks.
    pub fn for_each_visible(
        &self,
        start_ms: f64,
        end_ms: f64,
        mut visit: impl FnMut([f64; 2]),
    ) -> usize {
        if !start_ms.is_finite() || !end_ms.is_finite() || end_ms <= start_ms {
            return 0;
        }
        visit_visible(&self.starts, start_ms, end_ms, &mut visit)
    }
}

// Midpoints form a balanced implicit tree, with no per-node allocation.
fn index_max_end(gaps: &mut [Gap]) -> f64 {
    if gaps.is_empty() {
        return f64::NEG_INFINITY;
    }
    let middle = gaps.len() / 2;
    let (left, rest) = gaps.split_at_mut(middle);
    let (gap, right) = rest.split_first_mut().unwrap();
    gap.max_end = gap.bounds[1]
        .max(index_max_end(left))
        .max(index_max_end(right));
    gap.max_end
}

fn visit_visible(
    gaps: &[Gap],
    start_ms: f64,
    end_ms: f64,
    visit: &mut impl FnMut([f64; 2]),
) -> usize {
    if gaps.is_empty() {
        return 0;
    }
    let middle = gaps.len() / 2;
    let gap = &gaps[middle];
    if gaps[0].bounds[0] >= end_ms || gap.max_end <= start_ms {
        return 1;
    }
    let left = visit_visible(&gaps[..middle], start_ms, end_ms, visit);
    if gap.bounds[0] < end_ms && gap.bounds[1] > start_ms {
        visit(gap.bounds);
    }
    1 + left + visit_visible(&gaps[middle + 1..], start_ms, end_ms, visit)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn visible_gaps_include_crossings_but_not_zero_width_boundary_contacts() {
        let index = GapIndex::new(vec![[1.0, 8.0], [10.0, 14.0], [20.0, 30.0]], None);
        assert_eq!(
            index.query(7.0, 12.0),
            GapStatistics {
                count: 2,
                mean_ms: Some(5.5)
            }
        );
        assert_eq!(index.query(8.0, 10.0), GapStatistics::default());
        assert_eq!(index.query(2.0, 3.0).mean_ms, Some(7.0));
        assert_eq!(index.query(40.0, 50.0), GapStatistics::default());
        assert_eq!(index.query(10.0, 10.0), GapStatistics::default());
        assert_eq!(index.query(f64::NAN, 10.0), GapStatistics::default());
        assert_eq!(
            GapIndex::default().query(0.0, 1.0),
            GapStatistics::default()
        );
        for (start, end) in [(8.0, 10.0), (10.0, 10.0), (f64::NAN, 10.0)] {
            index.for_each_visible(start, end, |_| panic!("no visible gap"));
        }
    }

    #[test]
    fn independent_endpoint_sums_match_brute_force_with_nested_and_duplicate_intervals() {
        let gaps = [
            [1.0, 100.0],
            [2.0, 5.0],
            [1.0, 8.0],
            [20.0, 22.0],
            [2.0, 5.0],
        ];
        let index = GapIndex::new(gaps.to_vec(), None);
        for start in -1..103 {
            for width in [1, 4, 12, 100] {
                let (start, end) = (start as f64, (start + width) as f64);
                let visible: Vec<_> = gaps
                    .iter()
                    .filter(|gap| gap[0] < end && gap[1] > start)
                    .collect();
                let expected = GapStatistics {
                    count: visible.len(),
                    mean_ms: (!visible.is_empty()).then(|| {
                        visible.iter().map(|gap| gap[1] - gap[0]).sum::<f64>()
                            / visible.len() as f64
                    }),
                };
                assert_eq!(index.query(start, end), expected, "{start}..{end}");
                let mut marked = Vec::new();
                index.for_each_visible(start, end, |gap| marked.push(gap));
                marked.sort_unstable_by(|a, b| a[0].total_cmp(&b[0]).then(a[1].total_cmp(&b[1])));
                let mut visible: Vec<_> = visible.into_iter().copied().collect();
                visible.sort_unstable_by(|a, b| a[0].total_cmp(&b[0]).then(a[1].total_cmp(&b[1])));
                assert_eq!(marked, visible, "{start}..{end}");
            }
        }
    }

    #[test]
    fn narrow_window_drawing_prunes_history_even_with_a_long_crossing_gap() {
        let count = 1 << 18;
        let mut gaps: Vec<_> = (0..count)
            .map(|i| [i as f64 * 2.0, i as f64 * 2.0 + 1.0])
            .collect();
        gaps.push([0.0, count as f64 * 2.0]);
        let index = GapIndex::new(gaps, None);
        let start = count as f64;
        let mut visible = Vec::new();
        let visits = index.for_each_visible(start, start + 0.5, |gap| visible.push(gap));
        assert_eq!(visible, [[0.0, count as f64 * 2.0], [start, start + 1.0]]);
        assert_eq!(index.query(start, start + 0.5).count, visible.len());
        assert!(visits < 128, "narrow window visited {visits}/{count} nodes");
        assert_eq!(
            index.for_each_visible(-2.0, -1.0, |_| panic!("outside history")),
            1
        );
    }
}
