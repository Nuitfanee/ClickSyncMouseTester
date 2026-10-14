//! Natural horizontal axes: spatial stroke fitting and direction-balanced compensation.
use serde::Serialize;
use std::collections::VecDeque;
const EPS: f64 = 1e-9;
const FIT_POINTS: usize = 64;
const MIN_AXIS_DOMINANCE: f64 = 0.9;
const VARIATION_WARNING_DEGREES: f64 = 3.0;

#[derive(Clone, Debug, Serialize)]
pub struct TraceStroke {
    pub points: Vec<[f64; 2]>,
    pub current: bool,
}
#[derive(Clone, Debug, Serialize)]
pub struct CalibrationFrame {
    pub status: &'static str,
    pub locked: bool,
    pub has_data: bool,
    pub recommended_angle: Option<f64>,
    pub swipe_count: usize,
    pub sample_count: usize,
    pub accepted_strokes: usize,
    pub rejected_strokes: usize,
    /// Pooled within-direction sample SD, including statistical outliers; not a confidence interval.
    pub stroke_spread_degrees: Option<f64>,
    pub direction_difference_degrees: Option<f64>,
    pub traces: Vec<TraceStroke>,
    pub quality_reason: &'static str,
}
#[derive(Clone, Copy)]
struct Segment {
    dx: f64,
    dy: f64,
    time: f64,
    end: [f64; 2],
}
struct Stroke {
    points: Vec<[f64; 2]>,
    start: f64,
    last: f64,
    path: f64,
    net: [f64; 2],
    direction: i32,
}
impl Stroke {
    fn new(point: [f64; 2], time: f64) -> Self {
        Self {
            points: vec![point],
            start: time,
            last: time,
            path: 0.0,
            net: [0.0; 2],
            direction: 0,
        }
    }
    fn append(&mut self, s: Segment) {
        let length = (s.dx * s.dx + s.dy * s.dy).sqrt();
        self.path += if length.is_finite() { length } else { 0.0 };
        self.net[0] += s.dx;
        self.net[1] += s.dy;
        self.last = s.time;
        if self.direction == 0 && s.dx != 0.0 {
            self.direction = sign(s.dx);
        }
        if self.points.last() != Some(&s.end) {
            self.points.push(s.end);
        }
    }
}
#[derive(Clone, Copy)]
struct FitSample {
    angle: f64,
}
struct Side {
    accepted: usize,
    inliers: usize,
    squared_deviations: f64,
    angle: Option<f64>,
}

#[derive(Default)]
pub struct CalibrationEngine {
    history: VecDeque<TraceStroke>,
    left: VecDeque<FitSample>,
    right: VecDeque<FitSample>,
    pending: Vec<Segment>,
    active: Option<Stroke>,
    locked: bool,
    position: [f64; 2],
    swipes: usize,
    samples: usize,
    accepted: usize,
    rejected: usize,
    opposite: f64,
    last_accepted_direction: i32,
}

impl CalibrationEngine {
    pub fn reset(&mut self) {
        *self = Self::default();
    }
    pub fn set_locked(&mut self, locked: bool) {
        if !locked {
            self.active = None;
            self.pending.clear();
            self.opposite = 0.0;
        }
        self.locked = locked;
    }
    pub fn push(&mut self, dx: i32, dy: i32, time_ms: f64) {
        if dx == 0 && dy == 0 {
            return;
        }
        let dx = dx as f64;
        let dy = dy as f64;
        let length = (dx * dx + dy * dy).sqrt();
        if length.is_finite() && length >= 1.5 && dx.abs() >= EPS && dx.abs() / length >= 0.75 {
            self.samples += 1;
        }
        let previous = self.position;
        self.position[0] += dx;
        self.position[1] += dy;
        if self.active.is_none() {
            self.active = Some(Stroke::new(previous, time_ms));
        }
        let segment = Segment {
            dx,
            dy,
            time: time_ms,
            end: self.position,
        };
        let direction = sign(dx);
        let active_direction = self.active.as_ref().unwrap().direction;
        if !self.pending.is_empty() {
            if direction == 0 || (active_direction != 0 && direction != active_direction) {
                self.buffer(segment);
                if self.opposite >= 12.0 {
                    self.complete_and_restart();
                }
                return;
            }
            for segment in self.pending.drain(..) {
                self.active.as_mut().unwrap().append(segment);
            }
            self.opposite = 0.0;
        }
        if active_direction != 0 && direction != 0 && direction != active_direction {
            self.buffer(segment);
            if self.opposite >= 12.0 {
                self.complete_and_restart();
            }
        } else {
            self.active.as_mut().unwrap().append(segment);
        }
    }
    fn buffer(&mut self, segment: Segment) {
        let direction = sign(segment.dx);
        if self
            .active
            .as_ref()
            .is_some_and(|s| s.direction != 0 && direction != 0 && direction != s.direction)
        {
            self.opposite += segment.dx.abs();
        }
        self.pending.push(segment);
    }
    fn complete_and_restart(&mut self) {
        let Some(active) = self.active.take() else {
            return;
        };
        if active.points.len() >= 2 {
            self.history.push_back(trace(&active.points, false));
            while self.history.len() > 40 {
                self.history.pop_front();
            }
        }
        if let Some(sample) = evaluate(&active) {
            self.accepted += 1;
            let direction = sign(active.net[0]);
            let side = if direction < 0 {
                &mut self.left
            } else {
                &mut self.right
            };
            side.push_back(sample);
            while side.len() > 40 {
                side.pop_front();
            }
            if self.last_accepted_direction != 0 && direction != self.last_accepted_direction {
                self.swipes += 1;
            }
            self.last_accepted_direction = direction;
        } else {
            self.rejected += 1;
        }
        let mut next = Stroke::new(
            *active.points.last().unwrap(),
            self.pending.first().map_or(active.last, |s| s.time),
        );
        for segment in self.pending.drain(..) {
            next.append(segment);
        }
        self.opposite = 0.0;
        self.active = Some(next);
    }
    pub fn frame(&self, _now: f64) -> CalibrationFrame {
        let left = side_fit(&self.left);
        let right = side_fit(&self.right);
        let has_data = !self.history.is_empty()
            || self.active.as_ref().is_some_and(|s| s.points.len() > 1)
            || !self.pending.is_empty()
            || self.swipes > 0
            || self.samples > 0;
        let recommended = if self.swipes >= 30 {
            candidate(&left, &right)
        } else {
            None
        };
        let spread = if left.accepted >= 2 && right.accepted >= 2 {
            Some(
                ((left.squared_deviations + right.squared_deviations)
                    / (left.accepted + right.accepted - 2) as f64)
                    .sqrt(),
            )
        } else {
            None
        };
        let difference = left
            .angle
            .zip(right.angle)
            .map(|(left, right)| wrap(right - left).abs());
        let retained = (left.accepted + right.accepted).max(1) as f64;
        let reason = if !has_data {
            "None"
        } else if difference.is_some_and(|value| value > VARIATION_WARNING_DEGREES) {
            "DirectionMismatch"
        } else if spread.is_some_and(|value| value > VARIATION_WARNING_DEGREES) {
            "HighDispersion"
        } else if self.accepted > 0 && (left.inliers + right.inliers) as f64 / retained < 0.75 {
            "TooManyOutliers"
        } else if self.rejected as f64 / (self.accepted + self.rejected).max(1) as f64 > 0.4 {
            "HighRejection"
        } else if self.swipes < 30 {
            "InsufficientProgress"
        } else if left.angle.is_none() || right.angle.is_none() {
            "Imbalance"
        } else {
            "Good"
        };
        let mut traces: Vec<_> = self.history.iter().cloned().collect();
        let points = self
            .active
            .as_ref()
            .map_or(&[][..], |s| s.points.as_slice());
        let mut pending = Vec::new();
        let mut previous = points.last().copied();
        for segment in &self.pending {
            if previous != Some(segment.end) {
                pending.push(segment.end);
                previous = Some(segment.end);
            }
        }
        let count = points.len() + pending.len();
        if count > 0 {
            traces.push(sample_trace(
                count,
                |i| {
                    if i < points.len() {
                        points[i]
                    } else {
                        pending[i - points.len()]
                    }
                },
                true,
            ));
        }
        CalibrationFrame {
            status: if !has_data {
                "Empty"
            } else if recommended.is_some() {
                "ResultReady"
            } else if self.locked {
                "Collecting"
            } else {
                "Paused"
            },
            locked: self.locked,
            has_data,
            recommended_angle: recommended,
            swipe_count: self.swipes,
            sample_count: self.samples,
            accepted_strokes: self.accepted,
            rejected_strokes: self.rejected,
            stroke_spread_degrees: spread,
            direction_difference_degrees: difference,
            traces,
            quality_reason: reason,
        }
    }
}
fn sign(x: f64) -> i32 {
    if x < 0.0 {
        -1
    } else if x > 0.0 {
        1
    } else {
        0
    }
}
fn wrap(mut x: f64) -> f64 {
    while x >= 90.0 {
        x -= 180.0;
    }
    while x < -90.0 {
        x += 180.0;
    }
    x
}
fn trace(points: &[[f64; 2]], current: bool) -> TraceStroke {
    sample_trace(points.len(), |i| points[i], current)
}
fn sample_trace(count: usize, point: impl Fn(usize) -> [f64; 2], current: bool) -> TraceStroke {
    let points = if count <= 320 {
        (0..count).map(point).collect()
    } else {
        let step = (count - 1) as f64 / 319.0;
        (0..320)
            .map(|i| point(((i as f64 * step).round_ties_even() as usize).min(count - 1)))
            .collect()
    };
    TraceStroke { points, current }
}

fn evaluate(s: &Stroke) -> Option<FitSample> {
    if s.points.len() < 6 || sign(s.net[0]) == 0 {
        return None;
    }
    let duration = s.last - s.start;
    if !duration.is_finite() || !(90.0..=1500.0).contains(&duration) {
        return None;
    }
    let net = (s.net[0] * s.net[0] + s.net[1] * s.net[1]).sqrt();
    if !net.is_finite() || net < 220.0 || s.net[0].abs() < EPS || s.net[0].abs() / net < 0.75 {
        return None;
    }
    let path = s.path.max(net);
    if path < EPS || net / path < 0.82 {
        return None;
    }
    let points = spatial_points::<FIT_POINTS>(&s.points, s.path);
    let n = points.len() as f64;
    let centroid = [
        points.iter().map(|p| p[0]).sum::<f64>() / n,
        points.iter().map(|p| p[1]).sum::<f64>() / n,
    ];
    let mut vx = 0.0;
    let mut vy = 0.0;
    let mut covariance = 0.0;
    for point in &points {
        let x = point[0] - centroid[0];
        let y = point[1] - centroid[1];
        vx += x * x;
        vy += y * y;
        covariance += x * y;
    }
    let discriminant = ((vx - vy) * (vx - vy) + 4.0 * covariance * covariance).sqrt();
    // Curvature is allowed if there is still a distinct overall horizontal axis.
    if vx + vy < EPS || discriminant / (vx + vy) < MIN_AXIS_DOMINANCE {
        return None;
    }
    let angle = wrap(-0.5 * (2.0 * covariance).atan2(vx - vy).to_degrees());
    angle.is_finite().then_some(FitSample {
        angle: if angle.abs() < EPS { 0.0 } else { angle },
    })
}
fn spatial_points<const N: usize>(points: &[[f64; 2]], length: f64) -> [[f64; 2]; N] {
    let mut result = [[0.0; 2]; N];
    result[0] = points[0];
    result[N - 1] = *points.last().unwrap();
    let mut end = 1;
    let mut walked = 0.0;
    for (i, point) in result.iter_mut().enumerate().take(N - 1).skip(1) {
        let target = length * i as f64 / (N - 1) as f64;
        loop {
            let from = points[end - 1];
            let to = points[end];
            let distance = (to[0] - from[0]).hypot(to[1] - from[1]);
            if walked + distance >= target || end == points.len() - 1 {
                let t = if distance > EPS {
                    ((target - walked) / distance).clamp(0.0, 1.0)
                } else {
                    0.0
                };
                *point = [
                    from[0] + t * (to[0] - from[0]),
                    from[1] + t * (to[1] - from[1]),
                ];
                break;
            }
            walked += distance;
            end += 1;
        }
    }
    result
}
fn median(values: &[f64]) -> Option<f64> {
    if values.is_empty() {
        return None;
    }
    let mut values = values.to_vec();
    values.sort_by(f64::total_cmp);
    Some((values[(values.len() - 1) / 2] + values[values.len() / 2]) * 0.5)
}
fn side_fit(samples: &VecDeque<FitSample>) -> Side {
    let mut result = Side {
        accepted: samples.len(),
        inliers: 0,
        squared_deviations: 0.0,
        angle: None,
    };
    let Some(center) = median(&samples.iter().map(|s| s.angle).collect::<Vec<_>>()) else {
        return result;
    };
    let mad = median(
        &samples
            .iter()
            .map(|s| wrap(s.angle - center).abs())
            .collect::<Vec<_>>(),
    );
    let mean_offset =
        samples.iter().map(|s| wrap(s.angle - center)).sum::<f64>() / samples.len() as f64;
    result.squared_deviations = samples
        .iter()
        .map(|s| (wrap(s.angle - center) - mean_offset).powi(2))
        .sum();
    let threshold = 1.2_f64.max(2.5 * mad.unwrap_or(0.0));
    let inliers: Vec<_> = samples
        .iter()
        .filter(|s| wrap(s.angle - center).abs() <= threshold)
        .collect();
    result.inliers = inliers.len();
    if inliers.len() >= 4 {
        let offset: f64 = inliers.iter().map(|s| wrap(s.angle - center)).sum();
        result.angle = Some(wrap(center + offset / inliers.len() as f64));
    }
    result
}
fn candidate(left: &Side, right: &Side) -> Option<f64> {
    Some(wrap(left.angle? + wrap(right.angle? - left.angle?) * 0.5))
}

#[cfg(test)]
mod trace_tests {
    use super::*;

    #[test]
    fn current_trace_matches_full_materialization_at_sampling_boundaries() {
        for count in [1, 319, 320, 321, 100_000] {
            for tail in [0, 1, 323] {
                let mut engine = CalibrationEngine::default();
                let mut stroke = Stroke::new([0.0, 0.0], 0.0);
                stroke.points = (0..count).map(|i| [i as f64, (i % 7) as f64]).collect();
                let mut expected = stroke.points.clone();
                // Include duplicates at the seam and inside the pending reversal.
                for i in 0..tail {
                    let end = if i < 2 {
                        *stroke.points.last().unwrap()
                    } else {
                        [-(i / 2) as f64, 0.0]
                    };
                    engine.pending.push(Segment {
                        dx: 0.0,
                        dy: 0.0,
                        time: 1.0,
                        end,
                    });
                    if expected.last() != Some(&end) {
                        expected.push(end);
                    }
                }
                engine.active = Some(stroke);
                let expected = if expected.len() <= 320 {
                    expected
                } else {
                    let step = (expected.len() - 1) as f64 / 319.0;
                    (0..320)
                        .map(|i| {
                            expected[((i as f64 * step).round_ties_even() as usize)
                                .min(expected.len() - 1)]
                        })
                        .collect()
                };
                let frame = engine.frame(1.0);
                let current = frame.traces.last().unwrap();
                assert!(current.current);
                assert_eq!(current.points, expected);
            }
        }
    }
}
