use super::interpolation::{self, Coordinate, Quadratic};
use super::*;

pub(super) const HISTORY_MS: f64 = 3000.0;
pub(super) const MAX_SCROLL_AHEAD_MS: f64 = 100.0;
pub(super) const STROKE_WIDTH: f32 = 1.75;

pub(super) struct Geometry {
    pub points: Arc<[HistoryPoint]>,
    pub size: gpui::Size<gpui::Pixels>,
    pub maximum: f64,
    pub scale_factor: f32,
    pub fill: gpui::Path<gpui::Pixels>,
    pub stroke: Option<gpui::Path<gpui::Pixels>>,
    pub archived: Vec<Paths>,
    pub first_ms: f64,
    #[cfg(test)]
    pub marker_y: gpui::Pixels,
    pub origin_ms: f64,
    pub confirmed_ms: f64,
    curve: Vec<gpui::Point<gpui::Pixels>>,
    quadratics: Vec<Quadratic>,
    context: Vec<HistoryPoint>,
    #[cfg(any(test, feature = "ui-restoration-probe"))]
    pub point_count: usize,
    #[cfg(any(test, feature = "ui-restoration-probe"))]
    pub curve_point_count: usize,
}

impl Geometry {
    pub fn matches(
        &self,
        points: &Arc<[HistoryPoint]>,
        size: gpui::Size<gpui::Pixels>,
        maximum: f64,
        scale_factor: f32,
    ) -> bool {
        Arc::ptr_eq(&self.points, points)
            && self.size == size
            && self.maximum == maximum
            && self.scale_factor == scale_factor
    }

    #[cfg(test)]
    pub fn new(
        points: Arc<[HistoryPoint]>,
        size: gpui::Size<gpui::Pixels>,
        maximum: f64,
        scale_factor: f32,
    ) -> Self {
        Self::following(None, points, size, maximum, scale_factor)
    }

    #[cfg(test)]
    pub fn following(
        previous: Option<&Self>,
        points: Arc<[HistoryPoint]>,
        size: gpui::Size<gpui::Pixels>,
        maximum: f64,
        scale_factor: f32,
    ) -> Self {
        Self::following_with_builder(
            previous,
            points,
            size,
            maximum,
            scale_factor,
            &mut gpui::PolylineStrokeBuilder::default(),
        )
    }

    pub fn following_with_builder(
        previous: Option<&Self>,
        points: Arc<[HistoryPoint]>,
        size: gpui::Size<gpui::Pixels>,
        maximum: f64,
        scale_factor: f32,
        builder: &mut gpui::PolylineStrokeBuilder,
    ) -> Self {
        // Keep two actual predecessor observations for the first visible
        // quadratic. Pruning the raw history must not restart that quadratic
        // at its control point and change the already displayed left edge.
        let context = previous
            .filter(|old| {
                points.first().is_some_and(|first| {
                    old.points
                        .iter()
                        .any(|p| p.logical_ms == first.logical_ms && p.rate == first.rate)
                })
            })
            .map_or_else(Vec::new, |old| {
                let first = points.first().unwrap().logical_ms;
                let mut context: Vec<_> = old
                    .context
                    .iter()
                    .chain(old.points.iter())
                    .rev()
                    .filter(|p| p.logical_ms < first && p.segment == points[0].segment)
                    .take(2)
                    .copied()
                    .collect();
                context.reverse();
                context
            });
        let origin = previous
            .filter(|old| {
                old.size == size && old.maximum == maximum && old.scale_factor == scale_factor
            })
            .filter(|old| {
                points
                    .last()
                    .is_some_and(|p| (p.logical_ms - old.origin_ms).abs() <= HISTORY_MS)
            })
            .map(|old| old.origin_ms);
        Self::with_context(
            points,
            size,
            maximum,
            scale_factor,
            context,
            origin,
            builder,
        )
    }

    fn with_context(
        points: Arc<[HistoryPoint]>,
        size: gpui::Size<gpui::Pixels>,
        maximum: f64,
        scale_factor: f32,
        context: Vec<HistoryPoint>,
        origin: Option<f64>,
        builder: &mut gpui::PolylineStrokeBuilder,
    ) -> Self {
        let origin_ms = origin.unwrap_or_else(|| points.last().map_or(0.0, |p| p.logical_ms));
        let extended: Vec<_> = context.iter().chain(points.iter()).copied().collect();
        // Keep a bounded projection origin across publications. Reprojecting
        // every old control relative to each new last sample perturbs narrow,
        // steep intervals through f32 rounding at the live seam.
        let last_segment = extended.last().map_or(0, |p| p.segment);
        let split = extended
            .iter()
            .rposition(|p| p.segment != last_segment)
            .map_or(0, |i| i + 1);
        let first_ms = extended.get(split).map_or(origin_ms, |p| p.logical_ms);
        let project = |points: &[HistoryPoint]| {
            trace(
                points,
                Bounds::new(gpui::Point::default(), size),
                maximum,
                Some(origin_ms),
            )
        };
        let mut archived = Vec::new();
        for run in extended[..split].chunk_by(|a, b| a.segment == b.segment) {
            let projected = project(run);
            let mut curve = projected.first().copied().into_iter().collect::<Vec<_>>();
            for q in interpolation::quadratics(&projected) {
                interpolation::flatten(
                    interpolation::as_cubic(q),
                    interpolation::tolerance(scale_factor),
                    0,
                    &mut curve,
                );
            }
            if let Some(last) = projected.last() {
                curve.push(*last);
            }
            compact(&mut curve);
            let mut paths = Paths::default();
            paths.update(&curve, size.height, scale_factor, builder);
            archived.push(paths);
        }
        let projected = project(&extended[split..]);
        #[cfg(test)]
        let marker_y = projected.last().map_or(size.height, |point| point.y);
        let quadratics = interpolation::quadratics(&projected);
        let mut curve = Vec::with_capacity(projected.len() * 2 + 2);
        if let Some(first) = projected.first() {
            curve.push(*first);
        }
        for quadratic in &quadratics {
            interpolation::flatten(
                interpolation::as_cubic(*quadratic),
                interpolation::tolerance(scale_factor),
                0,
                &mut curve,
            );
        }
        compact(&mut curve);
        let mut paths = Paths::default();
        paths.update(&curve, size.height, scale_factor, builder);
        let Paths { fill, stroke } = paths;
        let latest_ms = points.last().map_or(0.0, |p| p.logical_ms);
        let confirmed_ms = if extended.len() - split >= 2 {
            (extended[extended.len() - 2].logical_ms + latest_ms) / 2.0
        } else {
            latest_ms
        };
        Self {
            origin_ms,
            confirmed_ms,
            first_ms,
            archived,
            quadratics,
            context,
            points,
            size,
            maximum,
            scale_factor,
            fill,
            stroke,
            #[cfg(test)]
            marker_y,
            #[cfg(any(test, feature = "ui-restoration-probe"))]
            point_count: projected.len(),
            #[cfg(any(test, feature = "ui-restoration-probe"))]
            curve_point_count: curve.len(),
            curve,
        }
    }

    pub fn x(&self, logical_ms: f64) -> f64 {
        f64::from(f32::from(self.size.width)) * (1.0 + (logical_ms - self.origin_ms) / HISTORY_MS)
    }

    fn boundary(&self, logical_ms: f64) -> (Coordinate, f64) {
        let x = self.x(logical_ms);
        let mut end = self
            .curve
            .partition_point(|p| f64::from(f32::from(p.x)) < x);
        // Projection stores f32 pixels. At the last confirmed midpoint, its
        // quantized x may be a few ULPs left of the f64 display x. Keep that
        // point's actual incoming tangent when a new quadratic is appended.
        let terminal = interpolation::coordinate(*self.curve.last().unwrap());
        let at_terminal = (x - terminal[0]).abs()
            <= f64::from(f32::from(self.size.width)) * f64::from(f32::EPSILON);
        if end == self.curve.len() && at_terminal && self.curve.len() >= 2 {
            end -= 1;
        }
        if end == 0 || end == self.curve.len() {
            let p = if end == 0 {
                self.curve[0]
            } else {
                *self.curve.last().unwrap()
            };
            return ([x, f64::from(f32::from(p.y))], 0.0);
        }
        let a = interpolation::coordinate(self.curve[end - 1]);
        let b = interpolation::coordinate(self.curve[end]);
        let fraction = if b[0] > a[0] {
            (x - a[0]) / (b[0] - a[0])
        } else {
            1.0
        };
        let epsilon = f64::from(f32::from(self.size.width)) * f64::from(f32::EPSILON);
        let snapped = [a, b].into_iter().find(|p| (p[0] - x).abs() <= epsilon);
        let y = snapped.map_or_else(|| a[1] + (b[1] - a[1]) * fraction, |p| p[1]);
        let tangent_x = snapped.map_or(x, |p| p[0]);
        let index = if at_terminal {
            self.quadratics.len().saturating_sub(1)
        } else {
            self.quadratics.partition_point(|q| q[2][0] < tangent_x)
        };
        let slope = self.quadratics.get(index).map_or(0.0, |q| {
            let tangent_x = if (q[2][0] - tangent_x).abs() <= epsilon {
                q[2][0]
            } else if (q[0][0] - tangent_x).abs() <= epsilon {
                q[0][0]
            } else {
                tangent_x
            };
            if tangent_x < q[0][0] {
                return 0.0;
            }
            if q[0] == q[1] && tangent_x <= q[0][0] + epsilon {
                // The first true node joins the independent horizontal
                // leading edge. Its zero derivative must not turn into the
                // outgoing limiting slope when the second sample arrives.
                return 0.0;
            }
            let cubic = interpolation::as_cubic(*q);
            let mut low = 0.0;
            let mut high = 1.0;
            for _ in 0..32 {
                let t = (low + high) / 2.0;
                if interpolation::evaluate(cubic, t)[0] < tangent_x {
                    low = t;
                } else {
                    high = t;
                }
            }
            let t = (low + high) / 2.0;
            let dx = (1.0 - t) * (q[1][0] - q[0][0]) + t * (q[2][0] - q[1][0]);
            let dy = (1.0 - t) * (q[1][1] - q[0][1]) + t * (q[2][1] - q[1][1]);
            if dx > 1e-12 { dy / dx } else { 0.0 }
        });
        ([x, y], slope)
    }

    #[cfg(test)]
    pub fn tail(&self, seam_ms: f64, display_ms: f64, head_rate: f64) -> Tail {
        let mut tail = Tail::default();
        self.update_tail(
            &mut tail,
            seam_ms,
            display_ms,
            head_rate,
            &mut gpui::PolylineStrokeBuilder::default(),
        );
        tail
    }

    pub fn update_tail(
        &self,
        tail: &mut Tail,
        seam_ms: f64,
        display_ms: f64,
        head_rate: f64,
        builder: &mut gpui::PolylineStrokeBuilder,
    ) {
        let (start, slope) = self.boundary(seam_ms);
        let height = f64::from(f32::from(self.size.height));
        let end = [
            self.x(display_ms),
            height * (1.0 - (head_rate / self.maximum).clamp(0.0, 1.0)),
        ];
        let width = (end[0] - start[0]).max(0.0);
        let mut handle = width / 3.0;
        // Keep the historical direction at the join, including on reversal.
        // Limit the handle's LENGTH rather than replacing the slope. Its
        // excursion beyond the endpoint range is below a physical pixel.
        let allowance = interpolation::tolerance(self.scale_factor);
        let low = (start[1].min(end[1]) - allowance).max(0.0);
        let high = (start[1].max(end[1]) + allowance).min(height);
        if slope > 0.0 {
            handle = handle.min(((high - start[1]) / slope).max(0.0));
        } else if slope < 0.0 {
            handle = handle.min(((low - start[1]) / slope).max(0.0));
        }
        let cubic = [
            start,
            [start[0] + handle, start[1] + slope * handle],
            // Like the reference's final LineTo, approach NOW in the
            // connector's actual direction, without imposing a flat shelf.
            [end[0] - width / 3.0, end[1] - (end[1] - start[1]) / 3.0],
            end,
        ];
        tail.curve.clear();
        tail.curve.push(interpolation::point(start));
        interpolation::flatten(
            cubic,
            interpolation::tolerance(self.scale_factor),
            0,
            &mut tail.curve,
        );
        tail.paths
            .update(&tail.curve, self.size.height, self.scale_factor, builder);
        tail.start_x = px(start[0] as f32);
        tail.marker_y = px(end[1] as f32);
        #[cfg(any(test, feature = "ui-restoration-probe"))]
        {
            tail.cubic = cubic;
        }
        #[cfg(feature = "ui-restoration-probe")]
        {
            tail.incoming_slope = slope;
        }
    }
}

#[derive(Default)]
pub(super) struct Tail {
    pub paths: Paths,
    pub start_x: gpui::Pixels,
    pub marker_y: gpui::Pixels,
    #[cfg(any(test, feature = "ui-restoration-probe"))]
    pub cubic: interpolation::Cubic,
    #[cfg(feature = "ui-restoration-probe")]
    pub incoming_slope: f64,
    curve: Vec<gpui::Point<gpui::Pixels>>,
}

pub(super) struct Paths {
    pub fill: gpui::Path<gpui::Pixels>,
    pub stroke: Option<gpui::Path<gpui::Pixels>>,
}

impl Default for Paths {
    fn default() -> Self {
        Self {
            fill: gpui::Path::new(gpui::Point::default()),
            stroke: None,
        }
    }
}

impl Paths {
    fn update(
        &mut self,
        curve: &[gpui::Point<gpui::Pixels>],
        baseline: gpui::Pixels,
        scale_factor: f32,
        builder: &mut gpui::PolylineStrokeBuilder,
    ) {
        self.fill.clear(gpui::point(px(0.0), baseline));
        self.fill.reserve(curve.len().saturating_sub(1) * 6);
        let solid = (
            gpui::point(0.0, 1.0),
            gpui::point(0.0, 1.0),
            gpui::point(0.0, 1.0),
        );
        // Time is monotonic. Each interval is a non-overlapping trapezoid,
        // so filling the area needs two triangles and no general tessellator.
        for pair in curve.windows(2) {
            let [a, b] = [pair[0], pair[1]];
            let bottom_a = gpui::point(a.x, baseline);
            let bottom_b = gpui::point(b.x, baseline);
            self.fill.push_triangle((bottom_a, a, b), solid);
            self.fill.push_triangle((bottom_a, b, bottom_b), solid);
        }
        if curve.is_empty() {
            self.stroke = None;
        } else {
            let options = gpui::StrokeOptions::default()
                .with_line_width(STROKE_WIDTH)
                .with_line_join(gpui::LineJoin::Round)
                .with_line_cap(gpui::LineCap::Round)
                .with_tolerance(
                    (super::interpolation::CURVE_TOLERANCE_PX / f64::from(scale_factor.max(1.0)))
                        as f32,
                );
            let stroke = self
                .stroke
                .get_or_insert_with(|| gpui::Path::new(gpui::Point::default()));
            if builder.stroke(curve, &options, stroke).is_err() {
                self.stroke = None;
            }
        }
    }
}

fn compact(points: &mut Vec<gpui::Point<gpui::Pixels>>) {
    let mut len = 0;
    for index in 0..points.len() {
        let point = points[index];
        if len >= 2 && points[len - 2].y == point.y && points[len - 1].y == point.y {
            points[len - 1] = point;
        } else {
            points[len] = point;
            len += 1;
        }
    }
    points.truncate(len);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reusable_polyline_strokes_match_svg_path_vertices_and_reset_between_shapes() {
        let mut reusable = gpui::PolylineStrokeBuilder::default();
        let mut output = gpui::Path::new(gpui::Point::default());
        for dpi in [1.0, 1.5, 2.0, 4.0] {
            let options = gpui::StrokeOptions::default()
                .with_line_width(STROKE_WIDTH)
                .with_line_join(gpui::LineJoin::Round)
                .with_line_cap(gpui::LineCap::Round)
                .with_tolerance(interpolation::tolerance(dpi) as f32);
            let dense: Vec<_> = (0..257)
                .map(|i| gpui::point(px(i as f32 * 0.7), px(if i % 2 == 0 { 0.0 } else { 300.0 })))
                .collect();
            let shapes = [
                dense,
                vec![gpui::point(px(8.0), px(30.0))],
                vec![gpui::point(px(8.0), px(30.0)); 4],
                vec![
                    gpui::point(px(-20.0), px(0.0)),
                    gpui::point(px(0.0), px(300.0)),
                    gpui::point(px(0.0), px(300.0)),
                    gpui::point(px(10.0), px(0.0)),
                    gpui::point(px(1500.0), px(0.0)),
                ],
                Vec::new(),
            ];
            for points in &shapes {
                let mut reference = gpui::PathBuilder::stroke(px(STROKE_WIDTH))
                    .with_style(gpui::PathStyle::Stroke(options));
                if let Some(first) = points.first() {
                    reference.move_to(*first);
                    for point in &points[1..] {
                        reference.line_to(*point);
                    }
                }
                let reference = reference.build().unwrap();
                reusable.stroke(points, &options, &mut output).unwrap();
                // Path's Debug includes bounds, every triangle position, texture
                // coordinates and paint state, not just the curve control points.
                assert_eq!(format!("{output:?}"), format!("{reference:?}"));
            }
        }
    }

    #[test]
    fn boundary_compaction_keeps_spikes_in_time_order_and_plateaus_use_two_points() {
        let points = [
            gpui::point(px(0.0), px(50.0)),
            gpui::point(px(0.1), px(10.0)),
            gpui::point(px(0.2), px(90.0)),
            gpui::point(px(0.3), px(50.0)),
            gpui::point(px(1.0), px(50.0)),
        ];
        let mut compacted = points.to_vec();
        compact(&mut compacted);
        assert_eq!(compacted, points);
        let mut flat: Vec<_> = (0..4096)
            .map(|index| gpui::point(px(index as f32 / 10.0), px(25.0)))
            .collect();
        let ends = [flat[0], *flat.last().unwrap()];
        compact(&mut flat);
        assert_eq!(flat, ends);
    }

    #[test]
    fn geometry_cache_identity_covers_data_size_scale_and_dpi() {
        let points: Arc<[_]> = (0..4096)
            .map(|index| HistoryPoint {
                segment: 1,
                logical_ms: index as f64 * 3000.0 / 4095.0,
                observed_ms: index as f64,
                rate: if index % 2 == 0 { 1000.0 } else { 4000.0 },
            })
            .collect();
        let plot_size = size(px(200.0), px(250.0));
        let geometry = Geometry::new(points.clone(), plot_size, 4000.0, 1.5);
        assert!(geometry.matches(&points, plot_size, 4000.0, 1.5));
        assert!(!geometry.matches(&Arc::from(points.as_ref()), plot_size, 4000.0, 1.5));
        assert!(!geometry.matches(&points, plot_size, 8000.0, 1.5));
        assert!(!geometry.matches(&points, plot_size, 4000.0, 2.0));
        assert!(!geometry.matches(&points, size(px(201.0), px(250.0)), 4000.0, 1.5));
        assert_eq!(geometry.point_count, points.len());
        assert_eq!(geometry.marker_y, px(0.0));
        assert!(geometry.stroke.is_some());
        assert!(geometry.curve_point_count >= geometry.point_count);
    }

    #[test]
    fn round_strokes_remain_renderable_for_full_history_and_all_supported_dpis() {
        let points: Arc<[_]> = (0..4096)
            .map(|index| HistoryPoint {
                segment: 1,
                logical_ms: 1_000_000_000.0 + index as f64 * 3000.0 / 4095.0,
                observed_ms: 1_000_000_000.0 + index as f64,
                rate: if index % 2 == 0 { 0.0 } else { 4000.0 },
            })
            .collect();
        for dpi in [1.0, 1.5, 2.0, 4.0] {
            for width in [200.0, 1500.0, 4000.0] {
                let geometry =
                    Geometry::new(points.clone(), size(px(width), px(300.0)), 4000.0, dpi);
                assert!(
                    geometry.stroke.is_some(),
                    "full-history stroke failed at width={width}, dpi={dpi}"
                );
                assert_eq!(geometry.marker_y, px(0.0));
            }
        }
    }

    fn history(samples: &[(f64, f64)]) -> Arc<[HistoryPoint]> {
        samples
            .iter()
            .map(|&(logical_ms, rate)| HistoryPoint {
                segment: 1,
                logical_ms,
                observed_ms: logical_ms,
                rate,
            })
            .collect()
    }

    fn world(geometry: &Geometry, cubic: interpolation::Cubic) -> interpolation::Cubic {
        cubic.map(|[x, y]| {
            [
                geometry.origin_ms
                    + (x / f64::from(f32::from(geometry.size.width)) - 1.0) * HISTORY_MS,
                y,
            ]
        })
    }

    #[test]
    fn zero_twenty_forty_sixty_ms_arrivals_do_not_reshape_the_tail_at_the_same_instant() {
        let start = Instant::now();
        let samples = [
            (0.0, 1000.0),
            (20.0, 2000.0),
            (40.0, 3000.0),
            (60.0, 4000.0),
        ];
        let mut head = super::super::motion::Head::default();
        let mut seam = super::super::motion::Seam::default();
        head.update((1, 0), 1000.0, true, start);
        let mut previous: Option<interpolation::Cubic> = None;
        for count in 1..=4 {
            let geometry = Geometry::new(
                history(&samples[..count]),
                size(px(1500.0), px(300.0)),
                4000.0,
                2.0,
            );
            head.update((1, 0), samples[count - 1].1, true, start);
            let seam_ms = seam.sample((1, 0), -80.0, start, true);
            let tail = geometry.tail(seam_ms, 20.0, head.sample(start));
            let current = world(&geometry, tail.cubic);
            if let Some(previous) = previous {
                for (a, b) in previous.into_iter().zip(current) {
                    assert!((a[0] - b[0]).abs() < 1e-4);
                    assert!((a[1] - b[1]).abs() < 1e-4);
                }
            }
            previous = Some(current);
            assert_eq!(tail.marker_y, px(225.0));
        }
        let expected = 4000.0 - 3000.0 * (-1.0_f64).exp();
        assert!((head.sample(start + Duration::from_millis(10)) - expected).abs() < 1e-9);
    }

    #[test]
    fn fixed_logical_history_is_stable_and_the_moving_seam_enters_it_continuously() {
        let samples: Vec<_> = (0..=12)
            .map(|index| {
                let rate = if index <= 6 {
                    index as f64 * 600.0
                } else {
                    (12 - index) as f64 * 600.0
                };
                (index as f64 * 20.0, rate)
            })
            .collect();
        let a = Geometry::new(
            history(&samples[..10]),
            size(px(1500.0), px(300.0)),
            4000.0,
            2.0,
        );
        let b = Geometry::new(history(&samples), a.size, a.maximum, a.scale_factor);
        for logical_ms in (0..=120).map(f64::from) {
            let (old, _) = a.boundary(logical_ms);
            let (new, _) = b.boundary(logical_ms);
            assert!(
                (old[1] - new[1]).abs() < 1e-4,
                "confirmed history moved at {logical_ms}"
            );
        }
        let value = |tail: &Tail, x: f64| {
            let mut low = 0.0;
            let mut high = 1.0;
            for _ in 0..48 {
                let t = (low + high) / 2.0;
                if interpolation::evaluate(tail.cubic, t)[0] < x {
                    low = t;
                } else {
                    high = t;
                }
            }
            interpolation::evaluate(tail.cubic, (low + high) / 2.0)[1]
        };
        let logical_ms = 90.0;
        let after = b.boundary(logical_ms).0;
        let mut previous_error = f64::INFINITY;
        for epsilon in [0.1, 0.01, 0.001, 0.0001] {
            let before = b.tail(logical_ms - epsilon, 190.0 - epsilon, 1000.0);
            let error = (value(&before, b.x(logical_ms)) - after[1]).abs();
            // Bound the entering interval by the known historical displacement
            // and the connector's endpoint displacement, then check convergence
            // instead of assuming the incoming and outgoing directions agree.
            let history_delta = (before.cubic[0][1] - after[1]).abs();
            let live_bound =
                3.0 * (before.cubic[3][1] - before.cubic[0][1]).abs() * epsilon / 100.0;
            assert!(error <= history_delta + live_bound + 1e-9);
            assert!(error < previous_error);
            previous_error = error;
        }
        assert!(previous_error < 0.001);
        let before = b.tail(logical_ms - 0.0001, 190.0 - 0.0001, 1000.0);
        // The same seam point is literally shared with the cached flattened
        // boundary; both paths and the marker use the one tail vertex sequence.
        assert_eq!(
            before.curve.first().copied(),
            Some(interpolation::point(before.cubic[0]))
        );
        assert_eq!(
            before.curve.last().copied(),
            Some(interpolation::point(before.cubic[3]))
        );
        assert_eq!(before.marker_y, before.curve.last().unwrap().y);
        assert!(before.paths.stroke.is_some());
    }

    #[test]
    fn live_tail_bounds_limited_tangents_and_fill_boundary_hold_for_all_dpis_and_input_shapes() {
        for rates in [
            [1000.0, 1000.0, 1000.0, 1000.0],
            [0.0, 1000.0, 2000.0, 3000.0],
            [3000.0, 2000.0, 1000.0, 0.0],
            [0.0, 4000.0, 0.0, 4000.0],
            [4000.0, 1000.0, 3000.0, 0.0],
        ] {
            let samples: Vec<_> = [0.0, 20.0, 20.0, 61.0].into_iter().zip(rates).collect();
            for dpi in [1.0, 1.5, 2.0, 4.0] {
                let geometry =
                    Geometry::new(history(&samples), size(px(900.0), px(300.0)), 4000.0, dpi);
                for rate in [0.0, 125.0, 2000.0, 8000.0] {
                    let tail = geometry.tail(25.0, 90.0, rate);
                    assert!(tail.cubic.windows(2).all(|pair| pair[0][0] <= pair[1][0]));
                    assert!(tail.cubic.iter().all(|p| (0.0..=300.0).contains(&p[1])));
                    assert!(tail.curve.windows(2).all(|pair| pair[0].x <= pair[1].x));
                    assert_eq!(tail.marker_y, tail.curve.last().unwrap().y);
                    assert!(tail.paths.stroke.is_some());
                    let start = tail.cubic[0];
                    let handle = tail.cubic[1];
                    if handle[0] > start[0] {
                        let expected = geometry.boundary(25.0).1;
                        assert!(
                            ((handle[1] - start[1]) / (handle[0] - start[0]) - expected).abs()
                                < 1e-7
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn equal_live_endpoints_preserve_the_join_without_a_visible_elastic_bulge() {
        let geometry = Geometry::new(
            history(&[(0.0, 1000.0), (8.0, 1200.0), (16.0, 1000.0)]),
            size(px(1500.0), px(300.0)),
            4000.0,
            2.0,
        );
        let (start, slope) = geometry.boundary(4.0);
        assert!(
            slope.abs() > 1.0,
            "fixture must have a steep incoming tangent"
        );
        let rate = geometry.maximum * (1.0 - start[1] / 300.0);
        let tail = geometry.tail(4.0, 104.0, rate);
        let maximum_departure = (0..=256)
            .map(|i| {
                (interpolation::evaluate(tail.cubic, f64::from(i) / 256.0)[1] - start[1]).abs()
            })
            .fold(0.0_f64, f64::max);
        assert!(
            maximum_departure * f64::from(geometry.scale_factor)
                <= interpolation::CURVE_TOLERANCE_PX,
            "equal endpoints invented a {maximum_departure} DIP elastic bulge"
        );
        let [a, control, _, _] = tail.cubic;
        assert!(control[0] > a[0], "nonzero incoming tangent was flattened");
        assert!(((control[1] - a[1]) / (control[0] - a[0]) - slope).abs() < 1e-7);
    }

    #[test]
    fn noisy_and_reversing_history_limits_tail_excursions_to_a_subpixel() {
        for rates in [
            [1000.0; 8],
            [
                1000.0, 1200.0, 1000.0, 1200.0, 1000.0, 1200.0, 1000.0, 1000.0,
            ],
            [0.0, 500.0, 1000.0, 1500.0, 2000.0, 2500.0, 3000.0, 3500.0],
            [3500.0, 3000.0, 2500.0, 2000.0, 1500.0, 1000.0, 500.0, 0.0],
            [0.0, 1000.0, 2000.0, 3000.0, 2000.0, 1000.0, 0.0, 0.0],
            [0.0, 4000.0, 0.0, 4000.0, 0.0, 4000.0, 0.0, 4000.0],
        ] {
            // Include duplicate times, sparse intervals and an asynchronous gap.
            for times in [
                [0.0, 8.0, 16.0, 24.0, 32.0, 40.0, 48.0, 56.0],
                [0.0, 8.0, 8.0, 23.0, 25.0, 60.0, 125.0, 180.0],
            ] {
                let samples: Vec<_> = times.into_iter().zip(rates).collect();
                for dpi in [1.0, 1.5, 2.0, 4.0] {
                    let geometry =
                        Geometry::new(history(&samples), size(px(1500.0), px(300.0)), 4000.0, dpi);
                    for seam in 0..=geometry.confirmed_ms.floor() as u32 {
                        for head in [0.0, 1000.0, 2000.0, 4000.0] {
                            let tail =
                                geometry.tail(f64::from(seam), f64::from(seam) + 100.0, head);
                            let [start, _, _, end] = tail.cubic;
                            let allowance = interpolation::tolerance(dpi);
                            let low = (start[1].min(end[1]) - allowance).max(0.0);
                            let high = (start[1].max(end[1]) + allowance).min(300.0);
                            assert!(
                                tail.cubic
                                    .iter()
                                    .all(|p| p[1] >= low - 1e-9 && p[1] <= high + 1e-9),
                                "invented control excursion: seam={seam}, head={head}, cubic={:?}",
                                tail.cubic,
                            );
                            let mut previous = start;
                            for i in 1..=64 {
                                let p = interpolation::evaluate(tail.cubic, f64::from(i) / 64.0);
                                assert!(p[0] >= previous[0]);
                                assert!(p[1] >= low - 1e-9 && p[1] <= high + 1e-9);
                                previous = p;
                            }
                            assert_eq!(tail.marker_y, tail.curve.last().unwrap().y);
                            assert!(tail.paths.stroke.is_some());
                            let [a, c, _, _] = tail.cubic;
                            if c[0] > a[0] {
                                let slope = geometry.boundary(f64::from(seam)).1;
                                let cross = (c[1] - a[1]) - slope * (c[0] - a[0]);
                                assert!(
                                    cross.abs() / slope.hypot(1.0) < 1e-9,
                                    "join changed direction: seam={seam}, slope={slope}, {a:?} -> {c:?}"
                                );
                            }
                        }
                    }
                }
            }
        }
    }

    #[test]
    fn limiting_remains_continuous_across_reversal_arrival_and_history_handoff() {
        let start = Instant::now();
        let samples = [
            (0.0, 1000.0),
            (20.0, 2000.0),
            (40.0, 3000.0),
            (60.0, 4000.0),
            (80.0, 3000.0),
            (100.0, 1000.0),
            (120.0, 1000.0),
        ];
        let before = Geometry::new(
            history(&samples[..6]),
            size(px(1500.0), px(300.0)),
            4000.0,
            2.0,
        );
        let after = Geometry::following(
            Some(&before),
            history(&samples),
            before.size,
            before.maximum,
            before.scale_factor,
        );
        let mut head = super::super::motion::Head::default();
        head.update((1, 0), 2000.0, true, start);
        // A newly admitted result updates the target, never the geometry at the
        // same display Instant. This time the seam is inside real history.
        let old = world(&before, before.tail(45.0, 145.0, head.sample(start)).cubic);
        head.update((1, 0), 1000.0, true, start);
        let new = world(&after, after.tail(45.0, 145.0, head.sample(start)).cubic);
        for (old, new) in old.into_iter().zip(new) {
            assert!((old[0] - new[0]).abs() < 1e-4);
            assert!((old[1] - new[1]).abs() < 1e-4);
        }
        for seam in 1..90 {
            let seam = f64::from(seam);
            let a = after.tail(seam, seam + 100.0, 2000.0);
            let b = after.tail(seam + 1e-6, seam + 100.0 + 1e-6, 2000.0);
            for i in 0..=64 {
                let t = f64::from(i) / 64.0;
                let a = interpolation::evaluate(a.cubic, t);
                let b = interpolation::evaluate(b.cubic, t);
                assert!((a[1] - b[1]).abs() < 1e-3, "limiter jumped at seam={seam}");
            }
            assert_eq!(a.cubic[0], after.boundary(seam).0);
            // The portion being handed to cached history converges to that
            // same boundary, even when the historical tangent was limited.
            let entering = interpolation::evaluate(a.cubic, 1e-6);
            assert!((entering[1] - after.boundary(seam + 1e-4).0[1]).abs() < 0.01);
        }
        let rate = head.sample(start + Duration::from_millis(10));
        assert!((rate - (1000.0 + 1000.0 * (-1.0_f64).exp())).abs() < 1e-9);
    }

    #[test]
    fn short_tail_admits_samples_on_time_late_and_at_duplicate_instants_without_reshaping() {
        use super::super::motion::{Head, Seam, TAIL_MS};
        let start = Instant::now();
        let samples = [
            (0.0, 1000.0),
            (20.0, 2000.0),
            (40.0, 3000.0),
            (60.0, 4000.0),
            (80.0, 1000.0),
            (100.0, 0.0),
            (120.0, 1000.0),
        ];
        for width in [200.0, 929.2, 1500.0] {
            for dpi in [1.0, 1.5, 2.0, 4.0] {
                let mut head = Head::default();
                let mut seam = Seam::default();
                let mut geometry = Geometry::new(
                    history(&samples[..1]),
                    size(px(width), px(300.0)),
                    4000.0,
                    dpi,
                );
                head.update((1, 0), 1000.0, true, start);
                seam.sample((1, 0), -TAIL_MS, start, true);
                for (count, display) in [20_u64, 40, 60, 145, 145, 160].into_iter().enumerate() {
                    let now = start + Duration::from_millis(display);
                    let display = display as f64;
                    let old_seam = seam.sample(
                        (1, 0),
                        (display - TAIL_MS).min(geometry.confirmed_ms),
                        now,
                        true,
                    );
                    let old_rate = head.sample(now);
                    let before = world(&geometry, geometry.tail(old_seam, display, old_rate).cubic);
                    let next = Geometry::following(
                        Some(&geometry),
                        history(&samples[..count + 2]),
                        geometry.size,
                        geometry.maximum,
                        geometry.scale_factor,
                    );
                    head.update((1, 0), samples[count + 1].1, true, now);
                    let new_seam = seam.sample(
                        (1, 0),
                        (display - TAIL_MS).min(next.confirmed_ms),
                        now,
                        true,
                    );
                    assert_eq!(new_seam, old_seam);
                    assert_eq!(head.sample(now), old_rate);
                    let after = world(&next, next.tail(new_seam, display, old_rate).cubic);
                    for (old, new) in before.into_iter().zip(after) {
                        assert!(
                            (old[0] - new[0]).abs() < 1e-4,
                            "short tail handle moved: width={width}, count={count}, seam={old_seam}, {old:?} -> {new:?}"
                        );
                        assert!(
                            (old[1] - new[1]).abs() < 1e-4,
                            "short tail reshaped on arrival: width={width}, count={count}, seam={old_seam}, {old:?} -> {new:?}"
                        );
                    }
                    geometry = next;
                }
            }
        }
    }

    #[test]
    fn projection_origin_is_bounded_and_rebasing_preserves_visible_history() {
        let samples: Vec<_> = (0..=602)
            .map(|i| {
                (
                    f64::from(i) * 10.0,
                    2000.0 + (f64::from(i) * 0.09).sin() * 900.0,
                )
            })
            .collect();
        for dpi in [1.0, 1.5, 2.0, 4.0] {
            for width in [200.0, 929.2, 1500.0] {
                let initial = Geometry::new(
                    history(&samples[..=300]),
                    size(px(width), px(300.0)),
                    4000.0,
                    dpi,
                );
                let before = Geometry::following(
                    Some(&initial),
                    history(&samples[300..=600]),
                    initial.size,
                    initial.maximum,
                    dpi,
                );
                assert_eq!(before.origin_ms, 3000.0);
                let after = Geometry::following(
                    Some(&before),
                    history(&samples[301..=601]),
                    initial.size,
                    initial.maximum,
                    dpi,
                );
                assert_eq!(after.origin_ms, 6010.0);
                for time in (3100..5900).step_by(7) {
                    let old = before.boundary(f64::from(time)).0[1];
                    let new = after.boundary(f64::from(time)).0[1];
                    assert!(
                        (old - new).abs() * f64::from(dpi) < 0.2,
                        "rebase moved history: dpi={dpi}, width={width}, time={time}, {old}->{new}"
                    );
                }
            }
        }
    }

    #[test]
    fn pruning_keeps_true_predecessors_and_does_not_restart_visible_midpoint_segments() {
        let samples: Vec<_> = (0..=8)
            .map(|index| {
                (
                    index as f64 * 20.0,
                    if index % 2 == 0 { 0.0 } else { 4000.0 },
                )
            })
            .collect();
        let a = Geometry::new(
            history(&samples[..7]),
            size(px(1500.0), px(300.0)),
            4000.0,
            2.0,
        );
        let b = Geometry::following(
            Some(&a),
            history(&samples[2..8]),
            a.size,
            a.maximum,
            a.scale_factor,
        );
        let c = Geometry::following(
            Some(&b),
            history(&samples[4..]),
            b.size,
            b.maximum,
            b.scale_factor,
        );
        assert_eq!(b.context.len(), 2);
        assert_eq!(c.context.len(), 2);
        for logical_ms in 80..=110 {
            let y = a.boundary(logical_ms as f64).0[1];
            assert!((b.boundary(logical_ms as f64).0[1] - y).abs() < 1e-4);
            assert!((c.boundary(logical_ms as f64).0[1] - y).abs() < 1e-4);
        }
        assert_eq!(a.points.len(), 7);
        assert_eq!(b.points.len(), 6);
        let sparse = Geometry::new(
            history(&[(0.0, 1000.0), (4000.0, 2000.0)]),
            a.size,
            a.maximum,
            a.scale_factor,
        );
        assert!(sparse.curve.windows(2).all(|pair| pair[0].x <= pair[1].x));
    }
}

#[cfg(test)]
mod segment_tests {
    use super::*;

    #[test]
    fn one_retained_observation_uses_its_real_predecessor_midpoint() {
        let points: Arc<[HistoryPoint]> = (0..3)
            .map(|i| HistoryPoint {
                logical_ms: f64::from(i) * 20.0,
                observed_ms: f64::from(i) * 20.0,
                rate: 1000.0 + f64::from(i) * 500.0,
                segment: 1,
            })
            .collect();
        let before = Geometry::new(points.clone(), size(px(900.0), px(200.0)), 4080.0, 2.0);
        let after = Geometry::following(
            Some(&before),
            Arc::from(&points[2..]),
            before.size,
            before.maximum,
            before.scale_factor,
        );
        assert_eq!(after.context.len(), 2);
        assert_eq!(after.confirmed_ms, 30.0);
        for time in 0..=30 {
            assert_eq!(
                before.boundary(f64::from(time)),
                after.boundary(f64::from(time))
            );
        }
    }

    #[test]
    fn segments_have_independent_geometry_and_do_not_extend_into_warmup() {
        let points: Arc<[HistoryPoint]> = Arc::from([
            HistoryPoint {
                logical_ms: 0.0,
                observed_ms: 0.0,
                rate: 1000.0,
                segment: 1,
            },
            HistoryPoint {
                logical_ms: 20.0,
                observed_ms: 20.0,
                rate: 1000.0,
                segment: 1,
            },
            HistoryPoint {
                logical_ms: 340.0,
                observed_ms: 5340.0,
                rate: 4000.0,
                segment: 2,
            },
            HistoryPoint {
                logical_ms: 360.0,
                observed_ms: 5360.0,
                rate: 4000.0,
                segment: 2,
            },
        ]);
        let geometry = Geometry::new(points, size(px(900.0), px(200.0)), 8000.0, 1.0);
        assert_eq!(geometry.archived.len(), 1);
        assert_eq!(geometry.first_ms, 340.0);
        assert_eq!(geometry.confirmed_ms, 350.0);
        assert!(
            geometry
                .curve
                .iter()
                .all(|p| f64::from(f32::from(p.x)) >= geometry.x(340.0) - 1e-4)
        );
        assert!(geometry.curve.iter().all(|p| p.y == px(100.0)));
    }
}
