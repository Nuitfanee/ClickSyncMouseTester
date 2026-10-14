use super::*;
use clicksync_chart::analysis::polling::HistoryPoint;

const SCALE_HEADROOM: f64 = 1.02;
const INITIAL_MAXIMUM: f64 = 1000.0 * SCALE_HEADROOM;

pub(super) struct Scale {
    identity: Option<(u64, u64)>,
    maximum: f64,
}

impl Default for Scale {
    fn default() -> Self {
        Self {
            identity: None,
            maximum: INITIAL_MAXIMUM,
        }
    }
}

impl Scale {
    pub(super) fn update(
        &mut self,
        identity: (u64, u64),
        points: &[HistoryPoint],
        peak: i32,
    ) -> f64 {
        if self.identity != Some(identity) {
            self.identity = Some(identity);
            self.maximum = INITIAL_MAXIMUM;
        }
        let rate = points
            .iter()
            .map(|point| point.rate)
            .filter(|rate| rate.is_finite())
            .fold(peak.max(0) as f64, f64::max);
        // Reserve real space above the highest tick instead of clipping small overshoots.
        let maximum = [1000.0, 2000.0, 4000.0, 8000.0, 16000.0, 20000.0]
            .into_iter()
            .map(|tier| tier * SCALE_HEADROOM)
            .find(|maximum| rate <= *maximum)
            .unwrap_or(20000.0 * SCALE_HEADROOM);
        self.maximum = self.maximum.max(maximum);
        self.maximum
    }
}

fn axis_ticks(maximum: f64) -> [(f64, f32); 4] {
    [0.0, 0.25, 0.5, 1.0].map(|fraction| {
        let rate = (maximum / SCALE_HEADROOM * fraction).round();
        (rate, (rate / maximum) as f32)
    })
}

fn trace(
    points: &[HistoryPoint],
    plot: Bounds<gpui::Pixels>,
    maximum: f64,
    origin_ms: Option<f64>,
) -> Vec<gpui::Point<gpui::Pixels>> {
    let Some(last) = points.last() else {
        return Vec::new();
    };
    let origin_ms = origin_ms.unwrap_or(last.logical_ms);
    let mut trace = Vec::with_capacity(points.len());
    let mut time = points.first().unwrap().logical_ms;
    for point in points {
        time = time.max(point.logical_ms);
        trace.push(gpui::point(
            px((f64::from(f32::from(plot.right()))
                + f64::from(f32::from(plot.size.width)) * (time - origin_ms) / 3000.0)
                as f32),
            px((f64::from(f32::from(plot.bottom()))
                - f64::from(f32::from(plot.size.height)) * (point.rate / maximum).clamp(0.0, 1.0))
                as f32),
        ));
    }
    trace
}

mod geometry;
mod interpolation;
mod motion;
mod render;
pub(super) use render::HistoryChart;

pub(super) fn placeholder(bounds_report: Arc<Mutex<[f32; 4]>>) -> gpui::Div {
    div().flex_1().min_h_0().child(
        canvas(
            move |bounds, _, _| {
                *bounds_report.lock().unwrap() = [
                    f32::from(bounds.origin.x),
                    f32::from(bounds.origin.y),
                    f32::from(bounds.size.width),
                    f32::from(bounds.size.height),
                ];
            },
            |_, _, _, _| {},
        )
        .size_full(),
    )
}
#[cfg(test)]
mod tests {
    use super::*;

    fn sample(logical_ms: f64, rate: f64) -> HistoryPoint {
        HistoryPoint {
            segment: 1,
            logical_ms,
            observed_ms: logical_ms,
            rate,
        }
    }

    #[test]
    fn scale_keeps_trace_stable_as_noisy_peaks_expire_and_resets_for_a_new_session() {
        let mut scale = Scale::default();
        assert_eq!(scale.update((1, 0), &[], 0), 1020.0);
        for (rate, maximum) in [
            (3980.0, 4080.0),
            (4020.0, 4080.0),
            (3990.0, 4080.0),
            (4060.0, 4080.0),
            (4080.0, 4080.0),
            (0.0, 4080.0),
        ] {
            assert_eq!(scale.update((1, 0), &[sample(3000.0, rate)], 4000), maximum);
            assert!(maximum >= rate);
        }
        assert_eq!(
            scale.update((1, 0), &[sample(3020.0, 4081.0)], 4000),
            8160.0
        );
        assert_eq!(scale.update((1, 0), &[sample(6040.0, 0.0)], 4000), 8160.0);
        assert_eq!(scale.update((1, 0), &[], 0), 8160.0);
        assert_eq!(scale.update((2, 0), &[sample(20.0, 1000.0)], 1000), 1020.0);
        assert_eq!(scale.update((2, 0), &[sample(40.0, 4000.0)], 4000), 4080.0);
        assert_eq!(scale.update((2, 1), &[], 0), 1020.0);
    }

    #[test]
    fn padded_axes_preserve_real_overshoots_and_align_ticks_at_every_scale() {
        let plot = Bounds::new(gpui::point(px(70.0), px(18.0)), size(px(900.0), px(220.0)));
        for tier in [1000, 2000, 4000, 8000, 16000, 20000] {
            let mut scale = Scale::default();
            let peak = f64::from(tier) * if tier == 20000 { 1.0 } else { 1.015 };
            let points = [sample(1000.0, f64::from(tier)), sample(1010.0, peak)];
            let maximum = scale.update((1, 0), &points, tier);
            assert_eq!(maximum, f64::from(tier) * SCALE_HEADROOM);
            let projected = trace(&points, plot, maximum, None);
            assert!(
                projected[1].y > plot.origin.y,
                "overshoot clipped at {tier}"
            );
            if tier < 20000 {
                assert!(
                    projected[1].y < projected[0].y,
                    "overshoot hidden at {tier}"
                );
            }
            for (rate, fraction) in axis_ticks(maximum) {
                let node = trace(&[sample(1010.0, rate)], plot, maximum, None)[0];
                let grid_y = plot.bottom() - plot.size.height * fraction;
                assert!((f32::from(node.y - grid_y)).abs() < 0.0001);
            }
            assert_eq!(axis_ticks(maximum)[3].0, f64::from(tier));
        }
    }

    #[test]
    fn projection_keeps_real_nodes_without_virtual_curve_controls() {
        let plot = Bounds::new(
            gpui::point(px(111.25), px(24.5)),
            size(px(480.5), px(250.0)),
        );
        let now = 1_000_000_000.0;
        let before = trace(
            &[sample(now - 2980.0, 2000.0), sample(now, 2000.0)],
            plot,
            4000.0,
            None,
        );
        let after = trace(
            &[sample(now - 2960.0, 2000.0), sample(now + 20.0, 2000.0)],
            plot,
            4000.0,
            None,
        );
        for trace in [before, after] {
            assert!(trace.first().unwrap().x > plot.origin.x);
            assert_eq!(trace.last().unwrap().x, plot.right());
            assert!(trace.iter().all(|point| point.y == px(149.5)));
        }
        assert!(trace(&[], plot, 4000.0, None).is_empty());
        let single = trace(&[sample(now, 0.0)], plot, 4000.0, None);
        assert_eq!(single.len(), 1);
        assert_eq!(single.last().unwrap(), &plot.bottom_right());
    }
}
