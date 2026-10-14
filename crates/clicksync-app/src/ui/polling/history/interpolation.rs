use super::*;

/// Maximum geometric error in physical pixels when flattening the curve.
pub(super) const CURVE_TOLERANCE_PX: f64 = 0.2;
const MAX_SUBDIVISION_DEPTH: u8 = 12;
pub(super) type Coordinate = [f64; 2];
pub(super) type Cubic = [Coordinate; 4];
pub(super) type Quadratic = [Coordinate; 3];

pub(super) fn coordinate(point: gpui::Point<gpui::Pixels>) -> Coordinate {
    [f64::from(f32::from(point.x)), f64::from(f32::from(point.y))]
}

pub(super) fn point(value: Coordinate) -> gpui::Point<gpui::Pixels> {
    gpui::point(px(value[0] as f32), px(value[1] as f32))
}

pub(super) fn midpoint(a: Coordinate, b: Coordinate) -> Coordinate {
    [(a[0] + b[0]) / 2.0, (a[1] + b[1]) / 2.0]
}

/// PollingHistoryChartControl.BuildCurveGeometry: the current real sample is
/// the control point and the adjacent midpoint is the endpoint. The final
/// half interval belongs to the live tail until another real sample arrives.
pub(super) fn quadratics(points: &[gpui::Point<gpui::Pixels>]) -> Vec<Quadratic> {
    let mut start = points.first().map(|p| coordinate(*p));
    points
        .windows(2)
        .map(|pair| {
            let a = coordinate(pair[0]);
            let end = midpoint(a, coordinate(pair[1]));
            let curve = [start.unwrap(), a, end];
            start = Some(end);
            curve
        })
        .collect()
}

pub(super) fn as_cubic([a, control, b]: Quadratic) -> Cubic {
    [
        a,
        std::array::from_fn(|axis| a[axis] + (control[axis] - a[axis]) * 2.0 / 3.0),
        std::array::from_fn(|axis| b[axis] + (control[axis] - b[axis]) * 2.0 / 3.0),
        b,
    ]
}

pub(super) fn evaluate(cubic: Cubic, t: f64) -> Coordinate {
    let u = 1.0 - t;
    std::array::from_fn(|axis| {
        u.powi(3) * cubic[0][axis]
            + 3.0 * u * u * t * cubic[1][axis]
            + 3.0 * u * t * t * cubic[2][axis]
            + t.powi(3) * cubic[3][axis]
    })
}

pub(super) fn tolerance(scale_factor: f32) -> f64 {
    CURVE_TOLERANCE_PX / f64::from(scale_factor.max(1.0))
}

fn flat_enough(cubic: Cubic, tolerance: f64) -> bool {
    let [a, c0, c1, b] = cubic;
    let dx = b[0] - a[0];
    let dy = b[1] - a[1];
    let length_squared = dx * dx + dy * dy;
    if length_squared == 0.0 {
        return [c0, c1]
            .iter()
            .all(|c| (c[0] - a[0]).hypot(c[1] - a[1]) <= tolerance);
    }
    let cross = |p: Coordinate| dx * (p[1] - a[1]) - dy * (p[0] - a[0]);
    let error = cross(c0).abs().max(cross(c1).abs());
    error * error <= tolerance * tolerance * length_squared
}

pub(super) fn flatten(
    cubic: Cubic,
    tolerance: f64,
    depth: u8,
    output: &mut Vec<gpui::Point<gpui::Pixels>>,
) {
    if depth == MAX_SUBDIVISION_DEPTH || flat_enough(cubic, tolerance) {
        output.push(point(cubic[3]));
        return;
    }
    let [a, c0, c1, b] = cubic;
    let a0 = midpoint(a, c0);
    let c = midpoint(c0, c1);
    let b0 = midpoint(c1, b);
    let a1 = midpoint(a0, c);
    let b1 = midpoint(c, b0);
    let center = midpoint(a1, b1);
    flatten([a, a0, a1, center], tolerance, depth + 1, output);
    flatten([center, b1, b0, b], tolerance, depth + 1, output);
}

#[cfg(test)]
mod tests {
    use super::*;

    fn p(x: f32, y: f32) -> gpui::Point<gpui::Pixels> {
        gpui::point(px(x), px(y))
    }

    fn smooth(points: &[gpui::Point<gpui::Pixels>], dpi: f32) -> Vec<gpui::Point<gpui::Pixels>> {
        let Some(first) = points.first() else {
            return Vec::new();
        };
        let mut result = vec![*first];
        for curve in quadratics(points) {
            flatten(as_cubic(curve), tolerance(dpi), 0, &mut result);
        }
        // The reference's terminal LineTo, tested independently of live paint.
        if points.len() > 1 {
            result.push(*points.last().unwrap());
        }
        result
    }

    #[test]
    fn reference_midpoints_controls_and_independent_known_values() {
        let nodes = [
            p(0.0, 0.0),
            p(100.0, 200.0),
            p(300.0, 300.0),
            p(400.0, 300.0),
        ];
        let curves = quadratics(&nodes);
        assert_eq!(curves[0], [[0.0, 0.0], [0.0, 0.0], [50.0, 100.0]]);
        assert_eq!(curves[1], [[50.0, 100.0], [100.0, 200.0], [200.0, 250.0]]);
        assert_eq!(evaluate(as_cubic(curves[1]), 0.5), [112.5, 187.5]);
        assert_eq!(evaluate(as_cubic(curves[2]), 0.5), [287.5, 287.5]);
        let result = smooth(&nodes, 2.0);
        assert!(result.contains(&p(50.0, 100.0)));
        assert!(result.contains(&p(200.0, 250.0)));
        assert_eq!(result.last(), nodes.last());
    }

    #[test]
    fn constant_ramps_reversals_alternation_zero_and_irregular_duplicates_stay_in_real_hulls() {
        for nodes in [
            vec![p(0.0, 100.0), p(20.0, 100.0), p(40.0, 100.0)],
            vec![p(0.0, 0.0), p(20.0, 100.0), p(40.0, 200.0), p(60.0, 300.0)],
            vec![p(0.0, 300.0), p(20.0, 200.0), p(40.0, 100.0), p(60.0, 0.0)],
            vec![p(0.0, 0.0), p(10.0, 200.0), p(11.0, 0.0), p(100.0, 0.0)],
            vec![p(0.0, 0.0), p(20.0, 200.0), p(40.0, 0.0), p(60.0, 200.0)],
            vec![p(0.0, 200.0), p(0.0, 0.0), p(0.001, 200.0), p(100.0, 0.0)],
        ] {
            for curve in quadratics(&nodes) {
                let low = curve.iter().map(|p| p[1]).reduce(f64::min).unwrap();
                let high = curve.iter().map(|p| p[1]).reduce(f64::max).unwrap();
                let mut previous_x = curve[0][0];
                for index in 0..=1000 {
                    let value = evaluate(as_cubic(curve), index as f64 / 1000.0);
                    assert!(value[0] >= previous_x - 1e-9);
                    assert!(value[1] >= low - 1e-9 && value[1] <= high + 1e-9);
                    previous_x = value[0];
                }
            }
            let result = smooth(&nodes, 4.0);
            assert!(result.windows(2).all(|pair| pair[0].x <= pair[1].x));
            assert!(result.iter().all(|p| f32::from(p.y).is_finite()));
        }
        assert!(smooth(&[], 1.0).is_empty());
        assert_eq!(smooth(&[p(1.0, 2.0)], 1.0), [p(1.0, 2.0)]);
    }

    #[test]
    fn appending_real_samples_never_changes_confirmed_quadratics() {
        let nodes = [
            p(0.0, 1000.0),
            p(20.0, 2000.0),
            p(40.0, 3000.0),
            p(60.0, 4000.0),
        ];
        for count in 2..nodes.len() {
            assert_eq!(
                quadratics(&nodes[..count]),
                quadratics(&nodes[..count + 1])[..count - 1]
            );
        }
    }

    #[test]
    fn flattening_bounds_physical_pixel_error_at_multiple_dpis_including_s_curves() {
        let curves = [
            [[0.0, 200.0], [100.0, 200.0], [200.0, 0.0], [300.0, 0.0]],
            as_cubic([[0.0, 0.0], [40.0, 200.0], [300.0, 0.0]]),
        ];
        for cubic in curves {
            for dpi in [1.0, 1.25, 1.5, 2.0, 4.0] {
                let mut points = vec![point(cubic[0])];
                flatten(cubic, CURVE_TOLERANCE_PX / dpi, 0, &mut points);
                assert!(points.len() > 2);
                for index in 0..=1000 {
                    let sample = evaluate(cubic, index as f64 / 1000.0);
                    let distance = points
                        .windows(2)
                        .map(|pair| {
                            let a = coordinate(pair[0]);
                            let b = coordinate(pair[1]);
                            let dx = b[0] - a[0];
                            let dy = b[1] - a[1];
                            let t = (((sample[0] - a[0]) * dx + (sample[1] - a[1]) * dy)
                                / (dx * dx + dy * dy))
                                .clamp(0.0, 1.0);
                            (sample[0] - a[0] - t * dx).hypot(sample[1] - a[1] - t * dy)
                        })
                        .reduce(f64::min)
                        .unwrap();
                    assert!(distance * dpi <= CURVE_TOLERANCE_PX + 1e-4);
                }
            }
        }
    }
}
