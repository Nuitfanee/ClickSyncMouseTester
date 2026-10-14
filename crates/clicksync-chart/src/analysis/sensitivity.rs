//! One target-distance swipe. Input accumulation is bounded; fitting runs only at completion.
mod fit;

use serde::Serialize;

pub const PREPARATION_MS: f64 = 500.0;
const TIMEOUT_MS: f64 = 8000.0;
const DRAIN_MS: f64 = 48.0;
const SAMPLE_MS: f64 = 1.0;
const PATH_MS: f64 = 8.0;
const CAPACITY: usize = (TIMEOUT_MS + DRAIN_MS) as usize + 2;

#[derive(Clone, Copy, Debug)]
pub struct Packet {
    pub time: f64,
    pub dx: i32,
    pub dy: i32,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize)]
pub enum Failure {
    #[default]
    None,
    Timeout,
    TooFast,
    InsufficientPackets,
    Unsynchronized,
    DirectionMismatch,
    PathShapeMismatch,
    UnstableScale,
    InvalidInput,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
pub enum Stage {
    Preparing,
    Collecting,
    Finishing,
}

#[derive(Clone, Debug, Serialize)]
pub struct MeasurementState {
    pub stage: Stage,
    pub progress: f64,
    pub target_path_length: f64,
    pub target_goal: f64,
    pub duration_ms: f64,
    pub packet_counts: [usize; 2],
}

#[derive(Clone, Debug, Serialize)]
pub struct MatchResult {
    pub scale: f64,
    pub recommended_target_dpi: i32,
    pub duration_ms: f64,
    pub packet_counts: [usize; 2],
    pub direction_delta_degrees: f64,
    pub overlap_ratio: f64,
    pub residual: f64,
    /// Resolution-adjusted within-swipe disagreement, not accuracy or a confidence interval.
    pub segment_spread_percent: f64,
    pub lag_ms: f64,
}

#[derive(Clone, Debug, Default, Serialize)]
pub struct MatchFrame {
    pub active: Option<MeasurementState>,
    pub result: Option<MatchResult>,
    pub failure: Failure,
    pub results_expired: bool,
}

#[derive(Clone, Copy, Default)]
struct Point {
    time: f64,
    position: [f64; 2],
}

struct Participant {
    points: Vec<Point>,
    position: [f64; 2],
    path_origin: [f64; 2],
    path_bucket: u64,
    path: f64,
    moving_packets: usize,
    first_motion: Option<f64>,
    last_motion: f64,
    last_time: f64,
}

impl Participant {
    fn new() -> Self {
        Self {
            points: Vec::with_capacity(CAPACITY),
            position: [0.0; 2],
            path_origin: [0.0; 2],
            path_bucket: 0,
            path: 0.0,
            moving_packets: 0,
            first_motion: None,
            last_motion: 0.0,
            last_time: 0.0,
        }
    }

    fn append(&mut self, time: f64, packet: Packet) -> bool {
        if time < self.last_time {
            return false;
        }
        self.last_time = time;
        let bucket = (time / PATH_MS).floor() as u64;
        if bucket != self.path_bucket {
            self.path = self.path_length();
            self.path_origin = self.position;
            self.path_bucket = bucket;
        }
        self.position[0] += f64::from(packet.dx);
        self.position[1] += f64::from(packet.dy);
        if packet.dx != 0 || packet.dy != 0 {
            self.moving_packets += 1;
            self.first_motion.get_or_insert(time);
            self.last_motion = time;
        }
        let point = Point {
            time,
            position: self.position,
        };
        if let Some(last) = self.points.last_mut()
            && (last.time / SAMPLE_MS).floor() == (time / SAMPLE_MS).floor()
        {
            *last = point;
        } else {
            // Time is bounded by the measurement deadline, including the alignment tail.
            if self.points.len() == CAPACITY {
                return false;
            }
            self.points.push(point);
        }
        true
    }

    fn path_length(&self) -> f64 {
        self.path
            + (self.position[0] - self.path_origin[0]).hypot(self.position[1] - self.path_origin[1])
    }
}

struct Measurement {
    ready_at: f64,
    now: f64,
    goal: f64,
    target_dpi: i32,
    participants: [Participant; 2],
    finish: Option<f64>,
}

impl Measurement {
    fn state(&self) -> MeasurementState {
        let target = &self.participants[1];
        MeasurementState {
            stage: if self.now < self.ready_at {
                Stage::Preparing
            } else if self.finish.is_some() {
                Stage::Finishing
            } else {
                Stage::Collecting
            },
            progress: if self.finish.is_some() {
                1.0
            } else {
                (target.path_length() / self.goal).clamp(0.0, 1.0)
            },
            target_path_length: target.path_length(),
            target_goal: self.goal,
            duration_ms: target
                .first_motion
                .map_or(0.0, |first| (target.last_motion - first).max(0.0)),
            packet_counts: self.participants.each_ref().map(|p| p.moving_packets),
        }
    }

    fn complete(&self) -> Result<MatchResult, Failure> {
        let target = &self.participants[1];
        let end = self.finish.ok_or(Failure::Timeout)?;
        let begin = target.first_motion.ok_or(Failure::InsufficientPackets)?;
        let duration = end - begin;
        if duration < 400.0 {
            return Err(Failure::TooFast);
        }
        if self.participants.iter().any(|p| p.moving_packets < 30) {
            return Err(Failure::InsufficientPackets);
        }
        let analysis = fit::analyze(&self.participants[0].points, &target.points, [begin, end])?;
        let dpi = (f64::from(self.target_dpi) * analysis.scale).round_ties_even();
        if !dpi.is_finite() || dpi < 1.0 || dpi > f64::from(i32::MAX) {
            return Err(Failure::InvalidInput);
        }
        Ok(MatchResult {
            scale: analysis.scale,
            recommended_target_dpi: dpi as i32,
            duration_ms: duration,
            packet_counts: self.participants.each_ref().map(|p| p.moving_packets),
            direction_delta_degrees: analysis.rotation.to_degrees(),
            overlap_ratio: analysis.overlap,
            residual: analysis.residual,
            segment_spread_percent: analysis.spread * 100.0,
            lag_ms: analysis.lag,
        })
    }
}

#[derive(Default)]
pub struct SensitivityEngine {
    active: Option<Measurement>,
    result: Option<MatchResult>,
    failure: Failure,
    expired: bool,
}

impl SensitivityEngine {
    pub fn reset(&mut self) {
        *self = Self::default();
    }

    pub fn cancel(&mut self) {
        self.active = None;
    }

    pub fn invalidate(&mut self) {
        self.expired |= self.result.is_some();
    }

    pub fn start(&mut self, target_dpi: i32, distance_cm: f64, started: f64) -> bool {
        let goal = f64::from(target_dpi) * (distance_cm / 2.54);
        let ready_at = started + PREPARATION_MS;
        if self.active.is_some()
            || self.result.is_some()
            || target_dpi <= 0
            || !goal.is_finite()
            || goal <= 0.0
            || !started.is_finite()
            || !(ready_at + TIMEOUT_MS + DRAIN_MS).is_finite()
            || ready_at <= started
            || ready_at + TIMEOUT_MS <= ready_at
        {
            return false;
        }
        self.failure = Failure::None;
        self.expired = false;
        self.active = Some(Measurement {
            ready_at,
            now: started,
            goal,
            target_dpi,
            participants: std::array::from_fn(|_| Participant::new()),
            finish: None,
        });
        true
    }

    pub fn push(&mut self, target: bool, packet: Packet) {
        let Some(measurement) = &mut self.active else {
            return;
        };
        if !packet.time.is_finite() {
            self.fail(Failure::InvalidInput);
            return;
        }
        if packet.time < measurement.ready_at {
            return;
        }
        let time = packet.time - measurement.ready_at;
        let cutoff = measurement.finish.map_or(TIMEOUT_MS, |t| t + DRAIN_MS);
        if time > cutoff {
            return;
        }
        measurement.now = measurement.now.max(packet.time);
        if !measurement.participants[usize::from(target)].append(time, packet) {
            self.fail(Failure::InvalidInput);
            return;
        }
        if target
            && measurement.finish.is_none()
            // Counts are integers; a sub-count remainder must not require another swipe.
            && measurement.participants[1].path_length() + 0.5 >= measurement.goal
        {
            measurement.finish = Some(time);
        }
    }

    /// Called after the fixed prefix has been consumed, never from the per-packet path.
    pub fn update(&mut self, now: f64) {
        let Some(measurement) = &mut self.active else {
            return;
        };
        if !now.is_finite() {
            self.fail(Failure::InvalidInput);
            return;
        }
        measurement.now = measurement.now.max(now);
        let elapsed = measurement.now - measurement.ready_at;
        if measurement
            .finish
            .is_some_and(|end| elapsed >= end + DRAIN_MS)
        {
            match measurement.complete() {
                Ok(result) => {
                    self.result = Some(result);
                    self.active = None;
                }
                Err(reason) => self.fail(reason),
            }
        } else if measurement.finish.is_none() && elapsed > TIMEOUT_MS {
            self.fail(Failure::Timeout);
        }
    }

    fn fail(&mut self, reason: Failure) {
        self.failure = reason;
        self.active = None;
    }

    pub fn frame(&self) -> MatchFrame {
        MatchFrame {
            active: self.active.as_ref().map(Measurement::state),
            result: self.result.clone(),
            failure: self.failure,
            results_expired: self.expired,
        }
    }
}
