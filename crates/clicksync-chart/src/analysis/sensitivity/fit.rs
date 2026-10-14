//! Shared-time displacement fitting. No packet-rate weighting or independent-axis projection.
use super::{Failure, Point};

const EPS: f64 = 1e-12;
const MAX_LAG: i32 = 24;
const MAX_BINS: usize = 128;
const MIN_BINS: usize = 8;
const MAX_RESIDUAL: f64 = 0.12;
const MAX_SPREAD: f64 = 0.02;

#[derive(Clone, Copy, Default)]
struct Pair {
    a: [f64; 2],
    b: [f64; 2],
}

impl Pair {
    fn energy(self) -> [f64; 2] {
        [dot(self.a, self.a), dot(self.b, self.b)]
    }
}

#[derive(Clone, Copy)]
struct Fit {
    scale: f64,
    rotation: f64,
    residual: f64,
}

pub(super) struct Analysis {
    pub scale: f64,
    pub rotation: f64,
    pub overlap: f64,
    pub residual: f64,
    pub spread: f64,
    pub lag: f64,
}

fn dot(a: [f64; 2], b: [f64; 2]) -> f64 {
    a[0] * b[0] + a[1] * b[1]
}

fn position(points: &[Point], time: f64) -> [f64; 2] {
    let index = points.partition_point(|p| p.time < time);
    if index == 0 {
        return points[0].position;
    }
    if index == points.len() {
        return points[index - 1].position;
    }
    let (a, b) = (points[index - 1], points[index]);
    let t = (time - a.time) / (b.time - a.time);
    std::array::from_fn(|axis| a.position[axis] + t * (b.position[axis] - a.position[axis]))
}

fn displacements(
    points: &[Point],
    window: [f64; 2],
    lag: f64,
    count: usize,
) -> [[f64; 2]; MAX_BINS] {
    let mut result = [[0.0; 2]; MAX_BINS];
    let mut previous = position(points, window[0] + lag);
    for (i, displacement) in result.iter_mut().enumerate().take(count) {
        let t = window[0] + (window[1] - window[0]) * (i + 1) as f64 / count as f64;
        let next = position(points, t + lag);
        *displacement = std::array::from_fn(|axis| next[axis] - previous[axis]);
        previous = next;
    }
    result
}

fn pairs(a: &[Point], b: &[[f64; 2]], window: [f64; 2], lag: f64) -> [Pair; MAX_BINS] {
    let source = displacements(a, window, lag, b.len());
    let mut result = [Pair::default(); MAX_BINS];
    for (pair, (a, b)) in result.iter_mut().zip(source.into_iter().zip(b)) {
        *pair = Pair { a, b: *b };
    }
    result
}

fn fit(pairs: &[Pair], weights: &[f64]) -> Option<Fit> {
    let (mut aa, mut bb, mut ab, mut cross) = (0.0, 0.0, 0.0, 0.0);
    for (p, w) in pairs.iter().zip(weights) {
        let energy = p.energy();
        aa += w * energy[0];
        bb += w * energy[1];
        ab += w * dot(p.a, p.b);
        cross += w * (p.b[0] * p.a[1] - p.b[1] * p.a[0]);
    }
    if aa <= EPS || bb <= EPS {
        return None;
    }
    // Symmetric geometric-mean fit: exchanging the devices gives the reciprocal scale.
    let scale = (aa / bb).sqrt();
    let rotation = cross.atan2(ab);
    let residual = (2.0 - 2.0 * (ab.hypot(cross) / (aa * bb).sqrt()).min(1.0)).sqrt();
    Some(Fit {
        scale,
        rotation,
        residual,
    })
}

fn errors(pairs: &[Pair], fit: Fit) -> [f64; MAX_BINS] {
    let mut errors = [0.0; MAX_BINS];
    let (sin, cos) = fit.rotation.sin_cos();
    for (i, p) in pairs.iter().enumerate() {
        let predicted = [
            fit.scale * (cos * p.b[0] - sin * p.b[1]),
            fit.scale * (sin * p.b[0] + cos * p.b[1]),
        ];
        let difference = (p.a[0] - predicted[0]).hypot(p.a[1] - predicted[1]);
        let denominator = (p.a[0].hypot(p.a[1]) + fit.scale * p.b[0].hypot(p.b[1])) * 0.5;
        errors[i] = difference / denominator.max(1.0);
    }
    errors
}

fn robust(pairs: &[Pair]) -> Result<(Fit, [f64; MAX_BINS]), Failure> {
    let mut weights = [1.0; MAX_BINS];
    let mut estimate = fit(pairs, &weights).ok_or(Failure::Unsynchronized)?;
    for _ in 0..4 {
        let errors = errors(pairs, estimate);
        let mut ordered = errors;
        ordered[..pairs.len()].sort_unstable_by(f64::total_cmp);
        let cutoff = (ordered[pairs.len() / 2] * 2.5).clamp(0.015, 0.08);
        for (weight, error) in weights.iter_mut().zip(errors).take(pairs.len()) {
            *weight = (cutoff / error.max(EPS)).min(1.0).powi(2);
        }
        estimate = fit(pairs, &weights).ok_or(Failure::Unsynchronized)?;
    }
    // Downweight isolated disturbances, but do not turn widespread slipping into a good fit.
    let rejected = weights[..pairs.len()].iter().filter(|w| **w < 0.25).count();
    if rejected * 5 > pairs.len() || estimate.residual > MAX_RESIDUAL {
        return Err(Failure::PathShapeMismatch);
    }
    Ok((estimate, weights))
}

pub(super) fn analyze(a: &[Point], b: &[Point], requested: [f64; 2]) -> Result<Analysis, Failure> {
    let first = [
        a.first().ok_or(Failure::InsufficientPackets)?.time,
        b.first().ok_or(Failure::InsufficientPackets)?.time,
    ];
    let last = [a.last().unwrap().time, b.last().unwrap().time];
    // The same window for every lag prevents short-overlap candidates winning by cropping.
    let window = [
        requested[0]
            .max(first[1])
            .max(first[0] + f64::from(MAX_LAG)),
        requested[1].min(last[1]).min(last[0] - f64::from(MAX_LAG)),
    ];
    let overlap = (window[1] - window[0]) / (requested[1] - requested[0]);
    if overlap < 0.75 {
        return Err(Failure::Unsynchronized);
    }
    let trim = (window[1] - window[0]) * 0.05;
    let window = [window[0] + trim, window[1] - trim];
    let count = (((window[1] - window[0]) / 24.0) as usize).clamp(MIN_BINS, MAX_BINS);
    let target = displacements(b, window, 0.0, count);
    let preliminary = pairs(a, &target[..count], window, 0.0);
    let travel = preliminary[..count].iter().fold([0.0; 2], |sums, p| {
        [
            sums[0] + p.a[0].hypot(p.a[1]),
            sums[1] + p.b[0].hypot(p.b[1]),
        ]
    });
    // Limit temporal subdivision by count resolution; the quality gates still apply.
    let count = count
        .min((travel[0].min(travel[1]) * MAX_RESIDUAL) as usize)
        .max(MIN_BINS);
    // Only source time shifts with lag; target windows are shared by all candidates.
    let target = displacements(b, window, 0.0, count);
    let mut candidates = [f64::INFINITY; (MAX_LAG * 2 + 1) as usize];
    let mut best = (0.0_f64, f64::INFINITY);
    for lag in -MAX_LAG..=MAX_LAG {
        let ps = pairs(a, &target[..count], window, f64::from(lag));
        let Some(estimate) = fit(&ps[..count], &[1.0; MAX_BINS]) else {
            continue;
        };
        candidates[(lag + MAX_LAG) as usize] = estimate.residual;
        if estimate.residual < best.1 - 1e-6
            || ((estimate.residual - best.1).abs() <= 1e-6 && f64::from(lag).abs() < best.0.abs())
        {
            best = (f64::from(lag), estimate.residual);
        }
    }
    if !best.1.is_finite() || best.1 > 0.35 {
        return Err(Failure::Unsynchronized);
    }
    if best.0.abs() == f64::from(MAX_LAG) {
        let neighbor = (best.0 - best.0.signum() + f64::from(MAX_LAG)) as usize;
        if candidates[neighbor] > best.1 + 0.001 {
            return Err(Failure::Unsynchronized);
        }
    }
    let ps = pairs(a, &target[..count], window, best.0);
    let ps = &ps[..count];
    let moving = ps
        .iter()
        .filter(|p| p.energy().iter().all(|energy| *energy >= 16.0))
        .count();
    if moving < MIN_BINS {
        return Err(Failure::InsufficientPackets);
    }
    let (estimate, weights) = robust(ps)?;
    if estimate.rotation.abs().to_degrees() > 35.0 {
        return Err(Failure::DirectionMismatch);
    }
    let mut spread = 0.0_f64;
    for segment in 0..6 {
        let from = segment * count / 6;
        let to = (segment + 1) * count / 6;
        let local = fit(&ps[from..to], &weights[from..to]).ok_or(Failure::Unsynchronized)?;
        let totals = ps[from..to].iter().fold([[0.0; 2]; 2], |mut sums, p| {
            sums[0] = std::array::from_fn(|axis| sums[0][axis] + p.a[axis]);
            sums[1] = std::array::from_fn(|axis| sums[1][axis] + p.b[axis]);
            sums
        });
        let lengths = totals.map(|p| p[0].hypot(p[1]));
        // An interval displacement has two quantized endpoints, unlike a packet average.
        // Subtract their count-resolution bound, not an empirical low-DPI tolerance.
        let resolution =
            std::f64::consts::SQRT_2 * (1.0 / lengths[0].max(1.0) + 1.0 / lengths[1].max(1.0));
        if lengths.iter().any(|length| *length <= 1.0) {
            return Err(Failure::InsufficientPackets);
        }
        let local_scale = lengths[0] / lengths[1];
        spread = spread.max(((local_scale / estimate.scale).ln().abs() - resolution).max(0.0));
        if (local.rotation - estimate.rotation).abs().to_degrees() > 8.0 {
            return Err(Failure::PathShapeMismatch);
        }
    }
    // A second time scale detects fits that depend on report phase or window boundaries.
    let coarse_count = (count / 2).max(MIN_BINS);
    let coarse_target = displacements(b, window, 0.0, coarse_count);
    let coarse = pairs(a, &coarse_target[..coarse_count], window, best.0);
    let (coarse, _) = robust(&coarse[..coarse_count])?;
    spread = spread.max((coarse.scale / estimate.scale).ln().abs());
    if spread > MAX_SPREAD {
        return Err(Failure::UnstableScale);
    }
    Ok(Analysis {
        scale: estimate.scale,
        rotation: estimate.rotation,
        overlap,
        residual: estimate.residual,
        spread,
        lag: best.0,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reused_target_displacements_preserve_pairs_for_every_lag_and_bin_count() {
        let a: Vec<_> = (0..100)
            .map(|i| Point {
                time: f64::from(i) * 20.25,
                position: [f64::from(i * i), f64::from(i % 7)],
            })
            .collect();
        let b: Vec<_> = (0..200)
            .map(|i| Point {
                time: f64::from(i) * 10.1 + 0.5,
                position: [f64::from(i * i) * 0.3, f64::from(i % 11)],
            })
            .collect();
        let window = [50.5, 1900.25];
        for count in MIN_BINS..=MAX_BINS {
            let target = displacements(&b, window, 0.0, count);
            for lag in -MAX_LAG..=MAX_LAG {
                let lag = f64::from(lag);
                let actual = pairs(&a, &target[..count], window, lag);
                let mut previous = [position(&a, window[0] + lag), position(&b, window[0])];
                for (i, pair) in actual.iter().enumerate().take(count) {
                    let t = window[0] + (window[1] - window[0]) * (i + 1) as f64 / count as f64;
                    let next = [position(&a, t + lag), position(&b, t)];
                    let expected_a: [f64; 2] =
                        std::array::from_fn(|axis| next[0][axis] - previous[0][axis]);
                    let expected_b: [f64; 2] =
                        std::array::from_fn(|axis| next[1][axis] - previous[1][axis]);
                    assert_eq!(pair.a.map(f64::to_bits), expected_a.map(f64::to_bits));
                    assert_eq!(pair.b.map(f64::to_bits), expected_b.map(f64::to_bits));
                    previous = next;
                }
            }
        }
    }
}
