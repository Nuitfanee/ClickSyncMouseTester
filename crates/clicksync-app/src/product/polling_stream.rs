//! One report-rate owner publishes metrics and history from certified source observations.
use crate::backend::{CapturePhase, PrefixReader, SessionPrefix};
use clicksync_capture::watermark::Checkpoint;
use clicksync_chart::analysis::polling::{
    HistoryPoint, PollingEngine, PollingMetrics, PollingMode, RateStatus,
};
mod timer;
use std::{
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

pub(crate) const SAMPLE_HZ: u32 = 120;
const MAX_OBSERVATION_AGE: Duration = Duration::from_millis(50);

fn sample_interval() -> Duration {
    Duration::from_secs_f64(1.0 / f64::from(SAMPLE_HZ))
}

#[derive(Clone, Copy, PartialEq, Eq)]
struct Owner {
    receiver: u64,
    generation: u64,
    reset: u64,
    mode: PollingMode,
}

#[derive(Clone)]
pub(crate) struct Request {
    pub receiver: u64,
    pub generation: u64,
    pub reset: u64,
    pub mode: PollingMode,
    pub source: PrefixReader,
    pub now_ms: f64,
    pub running: bool,
    pub revision: u64,
    pub pause_ms: Option<f64>,
}

impl Request {
    fn owner(&self) -> Owner {
        Owner {
            receiver: self.receiver,
            generation: self.generation,
            reset: self.reset,
            mode: self.mode,
        }
    }
}

#[derive(Clone)]
pub(crate) struct Frame {
    owner: Owner,
    pub history: Arc<[HistoryPoint]>,
    pub metrics: PollingMetrics,
    pub logical_ms: f64,
    pub revision: u64,
    pub received_sequence: u64,
    pub observed_at: Option<Instant>,
    pub running: bool,
    pub observation: u64,
    #[cfg(any(test, feature = "ui-restoration-probe"))]
    pub processed: usize,
    #[cfg(any(test, feature = "ui-restoration-probe"))]
    pub sample_hz: u32,
    #[cfg(any(test, feature = "ui-restoration-probe"))]
    pub high_resolution_timer: bool,
    #[cfg(any(test, feature = "ui-restoration-probe"))]
    pub execution_intervals_ms: Arc<[f64]>,
}

impl Frame {
    pub fn fresh(&self, now: Instant) -> bool {
        self.observed_at
            .is_some_and(|at| now.saturating_duration_since(at) <= MAX_OBSERVATION_AGE)
    }
    pub fn identity(&self) -> (u64, u64) {
        (self.owner.generation, self.owner.reset)
    }

    pub fn belongs_to(&self, receiver: u64, generation: u64, reset: u64) -> bool {
        self.owner.receiver == receiver
            && self.owner.generation == generation
            && self.owner.reset == reset
    }
}

#[derive(Clone)]
pub(crate) struct Reader(Arc<Mutex<Mailbox>>);

impl Reader {
    pub fn latest(&self) -> Option<Arc<Frame>> {
        self.0.lock().unwrap().latest.clone()
    }

    pub fn same_source(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}

#[derive(Default)]
struct Mailbox {
    request: Option<Request>,
    latest: Option<Arc<Frame>>,
    epoch: u64,
    state_epoch: u64,
    closing: bool,
}

#[derive(Default)]
struct State {
    owner: Option<Owner>,
    processed: usize,
    segment: u32,
    engine: PollingEngine,
    phase: Option<CapturePhase>,
    observation: u64,
    sampled_at: Option<f64>,
    history: Arc<[HistoryPoint]>,
    metrics: PollingMetrics,
    logical_ms: f64,
    pending: Option<Checkpoint>,
    received_sequence: u64,
    observed_at: Option<Instant>,
    pause_ms: Option<f64>,
}

impl State {
    fn sample(
        &mut self,
        request: &Request,
        prefix: &SessionPrefix,
        checkpoint: Option<Checkpoint>,
        now: Instant,
        cancel: &AtomicBool,
    ) -> Option<Frame> {
        if prefix.generation != request.generation {
            return None;
        }
        if self.owner != Some(request.owner()) {
            *self = Self::default();
            self.owner = Some(request.owner());
            self.engine.set_mode(request.mode);
            self.engine.set_history_interval(1.0);
        }
        if request.pause_ms != self.pause_ms {
            if let Some(time) = request.pause_ms {
                self.engine.end_segment(time);
            }
            self.pause_ms = request.pause_ms;
        }
        let running = request.running && prefix.phase == CapturePhase::Collecting;
        if !running {
            if self.phase == Some(CapturePhase::Collecting) {
                self.engine.end_segment(request.now_ms);
            }
            self.phase = Some(CapturePhase::Paused);
            self.pending = None;
        } else {
            self.phase = Some(CapturePhase::Collecting);
            if let Some(mark) = checkpoint.filter(|c| c.generation == request.generation) {
                self.metrics.dropped_packets =
                    self.metrics.dropped_packets.max(mark.dropped_packets);
            }
            if self.pending.is_none() {
                self.pending = checkpoint.filter(|c| c.generation == request.generation);
            }
            let previous_status = self.metrics.status;
            // Publication cadence may temporarily trail the next source barrier.
            // Keep the last certified frame until it ages out; never turn normal
            // one-batch latency into gaps, zeroes or a new observation.
            let mut status = if self
                .observed_at
                .is_some_and(|at| now.saturating_duration_since(at) <= MAX_OBSERVATION_AGE)
            {
                previous_status
            } else {
                RateStatus::Delayed
            };
            if !prefix.complete
                || checkpoint.is_some_and(|c| c.generation == request.generation && !c.complete)
            {
                self.pending = None;
                status = RateStatus::Incomplete;
            } else if let Some(mark) = self.pending {
                let time = mark.ticks as f64 * 1000.0 / mark.frequency as f64;
                let latest_segment = prefix
                    .datasets
                    .first()
                    .and_then(|(_, d)| d.segments().last());
                if !mark.active
                    || latest_segment.is_some_and(|s| s.started_at_raw_capture_ticks > mark.ticks)
                {
                    self.pending = None;
                    status = RateStatus::WarmingUp;
                } else if prefix.received_sequence >= mark.sequence {
                    if let Some((_, dataset)) = prefix.datasets.first() {
                        while let Some(event) = dataset.event_at(self.processed) {
                            if event.capture_sequence > mark.capture_sequence {
                                break;
                            }
                            if self.processed.is_multiple_of(4096) && cancel.load(Ordering::Acquire)
                            {
                                return None;
                            }
                            let time = event.raw_capture_ticks as f64 * 1000.0
                                / dataset.timestamp_frequency() as f64;
                            if self.segment != event.session_segment_id {
                                let started =
                                    dataset.segment(event.session_segment_id).map_or(time, |s| {
                                        s.started_at_raw_capture_ticks as f64 * 1000.0
                                            / dataset.timestamp_frequency() as f64
                                    });
                                self.engine.begin_segment(started);
                                self.segment = event.session_segment_id;
                            }
                            self.engine.push(time, event.raw_delta_x, event.raw_delta_y);
                            self.processed += 1;
                        }
                        // Resume can have no reports yet. Its old window must not become live again.
                        if let Some(segment) = latest_segment
                            && self.segment != segment.segment_id
                        {
                            self.engine.begin_segment(
                                segment.started_at_raw_capture_ticks as f64 * 1000.0
                                    / dataset.timestamp_frequency() as f64,
                            );
                            self.segment = segment.segment_id;
                        }
                    }
                    self.pending = None;
                    self.received_sequence = mark.sequence;
                    self.observed_at = Some(mark.observed);
                    if now
                        .max(Instant::now())
                        .saturating_duration_since(mark.observed)
                        <= MAX_OBSERVATION_AGE
                    {
                        self.metrics = self.engine.metrics(time, self.metrics.dropped_packets);
                        status = self.metrics.status;
                        self.logical_ms = self.engine.logical_now(time);
                        if status == RateStatus::Valid
                            && self.sampled_at.is_none_or(|previous| time > previous)
                        {
                            let history = self.engine.history_frame(time).0;
                            if history.back().is_some_and(|p| p.observed_ms == time) {
                                self.history = history.iter().copied().collect();
                            }
                        }
                        self.sampled_at = Some(time);
                    } else {
                        status = RateStatus::Delayed;
                    }
                }
            }
            if status != RateStatus::Valid {
                if previous_status == RateStatus::Valid {
                    self.engine.break_history();
                }
                self.metrics.current_rate = None;
                self.metrics.empty_packet_percent = None;
            }
            self.metrics.status = status;
        }
        self.observation += 1;
        Some(Frame {
            owner: request.owner(),
            history: self.history.clone(),
            metrics: self.metrics.clone(),
            logical_ms: self.logical_ms,
            revision: request.revision,
            received_sequence: self.received_sequence,
            observed_at: self.observed_at,
            running: running
                && matches!(
                    self.metrics.status,
                    RateStatus::Valid | RateStatus::WarmingUp
                ),
            observation: self.observation,
            #[cfg(any(test, feature = "ui-restoration-probe"))]
            processed: self.processed,
            #[cfg(any(test, feature = "ui-restoration-probe"))]
            sample_hz: SAMPLE_HZ,
            #[cfg(any(test, feature = "ui-restoration-probe"))]
            high_resolution_timer: false,
            #[cfg(any(test, feature = "ui-restoration-probe"))]
            execution_intervals_ms: Arc::from([]),
        })
    }
}

/// One active source, one replaceable request, one latest publication. Timer
/// overruns skip deadlines; they never enqueue work or replay a fixed prefix.
pub(crate) struct Worker {
    mailbox: Arc<Mutex<Mailbox>>,
    wake: Arc<timer::Waiter>,
    cancel: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl Worker {
    pub fn spawn() -> Self {
        let mailbox = Arc::new(Mutex::new(Mailbox::default()));
        let wake = Arc::new(timer::Waiter::new());
        let cancel = Arc::new(AtomicBool::new(false));
        let shared = mailbox.clone();
        let signal = wake.clone();
        let cancelled = cancel.clone();
        let thread = thread::Builder::new()
            .name("clicksync-polling-history".into())
            .spawn(move || {
                let mut state = State::default();
                let mut deadline = Instant::now();
                let mut epoch = 0;
                let mut state_epoch = 0;
                #[cfg(any(test, feature = "ui-restoration-probe"))]
                let mut executions = std::collections::VecDeque::with_capacity(4096);
                loop {
                    let (request, ticket) = loop {
                        let slot = shared.lock().unwrap();
                        if slot.closing {
                            return;
                        }
                        if slot.epoch != epoch {
                            epoch = slot.epoch;
                            deadline = Instant::now();
                            #[cfg(any(test, feature = "ui-restoration-probe"))]
                            executions.clear();
                        }
                        if state_epoch != slot.state_epoch {
                            state = State::default();
                            state_epoch = slot.state_epoch;
                        }
                        let Some(request) = slot.request.as_ref() else {
                            drop(slot);
                            signal.wait(None);
                            continue;
                        };
                        if !request.running
                            && slot.latest.as_ref().is_some_and(|frame| !frame.running)
                        {
                            drop(slot);
                            signal.wait(None);
                            continue;
                        }
                        let now = Instant::now();
                        if now >= deadline {
                            break (request.clone(), slot.epoch);
                        }
                        drop(slot);
                        signal.wait(Some(deadline));
                    };
                    let now = Instant::now();
                    let checkpoint = request.source.checkpoint();
                    if let Some(prefix) = request.source.latest()
                        && let Some(frame) =
                            state.sample(&request, &prefix, checkpoint, now, &cancelled)
                    {
                        let mut slot = shared.lock().unwrap();
                        if slot.epoch == ticket && !slot.closing {
                            let changed = slot.latest.as_ref().is_none_or(|old| {
                                old.observation != frame.observation || old.running != frame.running
                            });
                            if changed {
                                #[cfg(any(test, feature = "ui-restoration-probe"))]
                                let frame = {
                                    if slot
                                        .latest
                                        .as_ref()
                                        .is_none_or(|old| old.observation != frame.observation)
                                    {
                                        if executions.len() == 4096 {
                                            executions.pop_front();
                                        }
                                        executions.push_back(now);
                                    }
                                    Frame {
                                        high_resolution_timer: signal.high_resolution,
                                        execution_intervals_ms: executions
                                            .iter()
                                            .zip(executions.iter().skip(1))
                                            .map(|(a, b)| {
                                                b.duration_since(*a).as_secs_f64() * 1000.0
                                            })
                                            .collect(),
                                        ..frame
                                    }
                                };
                                slot.latest = Some(Arc::new(frame));
                            }
                        }
                    }
                    deadline = next_deadline(deadline, sample_interval(), Instant::now());
                }
            })
            .expect("polling history worker");
        Self {
            mailbox,
            wake,
            cancel,
            thread: Some(thread),
        }
    }

    pub fn reader(&self) -> Reader {
        Reader(self.mailbox.clone())
    }

    pub fn configure(&self, request: Request) {
        let mut slot = self.mailbox.lock().unwrap();
        let replaced = slot.request.as_ref().is_none_or(|old| {
            old.owner() != request.owner() || !old.source.same_source(&request.source)
        });
        let wake = slot.request.as_ref().is_none_or(|old| {
            replaced || old.revision != request.revision || old.running != request.running
        });
        if replaced {
            slot.state_epoch += 1;
        }
        if wake {
            slot.epoch += 1;
            slot.latest = None;
        }
        slot.request = Some(request);
        if wake {
            self.wake.notify();
        }
    }

    pub fn clear(&self, receiver: u64) {
        let mut slot = self.mailbox.lock().unwrap();
        if slot
            .request
            .as_ref()
            .is_some_and(|r| r.receiver == receiver)
        {
            slot.request = None;
            slot.latest = None;
            slot.epoch += 1;
            slot.state_epoch += 1;
            self.wake.notify();
        }
    }

    pub fn shutdown(&mut self) {
        self.cancel.store(true, Ordering::Release);
        {
            let mut slot = self.mailbox.lock().unwrap();
            slot.closing = true;
            slot.request = None;
            slot.latest = None;
        }
        self.wake.notify();
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

impl Drop for Worker {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn next_deadline(previous: Instant, interval: Duration, now: Instant) -> Instant {
    let late = now.saturating_duration_since(previous).as_secs_f64();
    previous + interval.mul_f64((late / interval.as_secs_f64()).floor() + 1.0)
}

#[cfg(test)]
impl Frame {
    pub fn for_test(generation: u64, reset: u64, time: f64, rate: f64) -> Self {
        Self {
            owner: Owner {
                receiver: 1,
                generation,
                reset,
                mode: PollingMode::MotionReportRate,
            },
            history: Arc::from([HistoryPoint {
                logical_ms: time,
                observed_ms: time,
                rate,
                segment: 1,
            }]),
            metrics: PollingMetrics {
                current_rate: Some(rate as i32),
                status: RateStatus::Valid,
                ..Default::default()
            },
            logical_ms: time,
            revision: 0,
            received_sequence: 1,
            observed_at: Some(Instant::now()),
            running: true,
            observation: 1,
            processed: 1,
            sample_hz: SAMPLE_HZ,
            high_resolution_timer: false,
            execution_intervals_ms: Arc::from([]),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clicksync_core::{
        RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SessionMetadata,
        SharedByteBudget,
    };

    fn session() -> Session {
        let mut s = Session::new(
            SessionConfig {
                timestamp_frequency: 1_000_000,
                ..Default::default()
            },
            SharedByteBudget::new(4 * 1024 * 1024 * 1024),
            SessionMetadata::default(),
        )
        .unwrap();
        s.begin_collecting(1_000_000, true, false).unwrap();
        s
    }
    fn append(session: &mut Session, time: u64) {
        let sequence = session.event_count() as u64 + 1;
        session
            .append(RawMouseRecord::new(
                1,
                0,
                time * 1000,
                sequence,
                sequence,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    fn prefix(s: &mut Session, phase: CapturePhase) -> Arc<SessionPrefix> {
        Arc::new(SessionPrefix {
            generation: 1,
            received_sequence: s.event_count() as u64,
            received_records: s.event_count() as u64,
            phase,
            complete: true,
            datasets: vec![(1, Arc::new(s.snapshot().unwrap()))],
        })
    }
    fn request(p: Arc<SessionPrefix>) -> Request {
        Request {
            receiver: 1,
            generation: 1,
            reset: 0,
            mode: PollingMode::MotionReportRate,
            source: PrefixReader::controlled(p),
            now_ms: 1000.0,
            running: true,
            revision: 0,
            pause_ms: None,
        }
    }
    fn mark(p: &SessionPrefix, time: f64, now: Instant) -> Checkpoint {
        Checkpoint {
            generation: p.generation,
            sequence: p.received_sequence,
            capture_sequence: p.received_sequence,
            ticks: (time * 1000.0) as u64,
            frequency: 1_000_000,
            active: true,
            complete: p.complete,
            dropped_packets: 0,
            observed: now,
        }
    }
    fn sample(state: &mut State, request: &Request, prefix: &SessionPrefix, time: f64) -> Frame {
        let now = Instant::now();
        state
            .sample(
                request,
                prefix,
                Some(mark(prefix, time, now)),
                now,
                &AtomicBool::new(false),
            )
            .unwrap()
    }
    #[test]
    fn one_twenty_hz_late_deadlines_do_not_backfill() {
        let start = Instant::now();
        let period = sample_interval();
        assert_eq!(period, Duration::from_nanos(8_333_333));
        for missed in [0, 1, 7, 120] {
            assert_eq!(
                next_deadline(
                    start,
                    period,
                    start + period * missed + Duration::from_millis(1)
                ),
                start + period * (missed + 1)
            );
        }
    }
    #[test]
    fn one_owner_measures_each_report_once_and_never_invents_warmup_points() {
        for hz in [60, 120] {
            let mut s = session();
            let r = request(prefix(&mut s, CapturePhase::Collecting));
            let mut state = State::default();
            let mut next = 1000;
            for tick in 0..=hz {
                let time = 1000.0 + tick as f64 * 1000.0 / hz as f64;
                while next as f64 <= time {
                    append(&mut s, next);
                    next += 1;
                }
                let p = prefix(&mut s, CapturePhase::Collecting);
                let f = sample(&mut state, &r, &p, time);
                assert_eq!(f.processed, s.event_count());
                let duplicate = sample(&mut state, &r, &p, time);
                assert!(Arc::ptr_eq(&f.history, &duplicate.history));
                assert!(f.history.iter().all(|p| p.observed_ms >= 1310.0));
                if tick == hz {
                    assert_eq!(f.metrics.current_rate, Some(1000));
                    assert_eq!(f.metrics.peak_rate, 1000);
                }
            }
        }
    }
    #[test]
    fn backlog_staleness_and_integrity_are_not_zero_rates() {
        let mut s = session();
        for t in 1000..=1400 {
            append(&mut s, t);
        }
        let p = prefix(&mut s, CapturePhase::Collecting);
        let r = request(p.clone());
        let mut state = State::default();
        let good = sample(&mut state, &r, &p, 1400.0);
        assert_eq!(good.metrics.current_rate, Some(1000));
        let now = Instant::now();
        let mut pending = mark(&p, 1420.0, now);
        pending.sequence += 1;
        let holding = state
            .sample(&r, &p, Some(pending), now, &AtomicBool::new(false))
            .unwrap();
        assert_eq!(holding.metrics, good.metrics);
        assert!(Arc::ptr_eq(&good.history, &holding.history));
        let delayed = state
            .sample(
                &r,
                &p,
                None,
                now + Duration::from_millis(51),
                &AtomicBool::new(false),
            )
            .unwrap();
        assert_eq!(delayed.metrics.status, RateStatus::Delayed);
        assert_eq!(delayed.metrics.current_rate, None);
        assert!(Arc::ptr_eq(&good.history, &delayed.history));
        append(&mut s, 1420);
        let caught = prefix(&mut s, CapturePhase::Collecting);
        let stale = state
            .sample(
                &r,
                &caught,
                None,
                now + Duration::from_millis(60),
                &AtomicBool::new(false),
            )
            .unwrap();
        assert_eq!(stale.metrics.status, RateStatus::Delayed);
        assert_eq!(stale.metrics.current_rate, None);
        let resumed = sample(&mut state, &r, &caught, 1430.0);
        assert_eq!(resumed.metrics.current_rate, Some(903));
        assert_ne!(
            resumed.history.last().unwrap().segment,
            good.history.last().unwrap().segment
        );
        let mut incomplete = (*caught).clone();
        incomplete.complete = false;
        let failed = sample(&mut state, &r, &incomplete, 1440.0);
        assert_eq!(failed.metrics.status, RateStatus::Incomplete);
        assert_eq!(failed.metrics.current_rate, None);
    }
    #[test]
    fn certified_silence_reaches_zero_after_one_window_without_new_reports() {
        let mut s = session();
        for index in 0..=3200 {
            let sequence = index + 1;
            s.append(RawMouseRecord::new(
                1,
                0,
                1_000_000 + index * 250,
                sequence,
                sequence,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
        }
        let p = prefix(&mut s, CapturePhase::Collecting);
        let r = request(p.clone());
        let mut state = State::default();
        // A fixed prefix can still certify silence with fresh source checkpoints.
        for elapsed in (0..=400).step_by(10) {
            let time = 1800.0 + f64::from(elapsed);
            let expected = 4000.0 * f64::from((300 - elapsed).max(0)) / 300.0;
            let frame = sample(&mut state, &r, &p, time);
            assert_eq!(frame.metrics.status, RateStatus::Valid);
            assert_eq!(
                frame.metrics.current_rate,
                Some(expected.round_ties_even() as i32)
            );
            assert_eq!(frame.metrics.peak_rate, 4000);
            assert_eq!(frame.processed, 3201);
            let point = frame.history.last().unwrap();
            assert_eq!(point.observed_ms, time);
            assert!((point.rate - expected).abs() < 1e-9);
        }
    }
    #[test]
    fn pause_freezes_the_frame_resume_creates_a_gap_and_reset_rejects_old_owners() {
        let mut s = session();
        for t in 1000..=1800 {
            append(&mut s, t);
        }
        let p = prefix(&mut s, CapturePhase::Collecting);
        let mut r = request(p.clone());
        let mut state = State::default();
        let live = sample(&mut state, &r, &p, 1800.0);
        r.running = false;
        r.now_ms = 1800.0;
        r.revision += 1;
        append(&mut s, 1801);
        let drain = prefix(&mut s, CapturePhase::Collecting);
        let paused = sample(&mut state, &r, &drain, 2000.0);
        assert_eq!(paused.metrics, live.metrics);
        assert_eq!(paused.processed, live.processed);
        assert!(Arc::ptr_eq(&paused.history, &live.history));
        let later = sample(&mut state, &r, &drain, 9000.0);
        assert!(Arc::ptr_eq(&later.history, &paused.history));
        s.pause();
        s.begin_collecting(9_000_000, false, false).unwrap();
        r.running = true;
        r.revision += 1;
        let warming = sample(
            &mut state,
            &r,
            &prefix(&mut s, CapturePhase::Collecting),
            9000.0,
        );
        assert_eq!(warming.metrics.status, RateStatus::WarmingUp);
        assert!(Arc::ptr_eq(&warming.history, &paused.history));
        for t in 9000..=9400 {
            append(&mut s, t);
        }
        let next = sample(
            &mut state,
            &r,
            &prefix(&mut s, CapturePhase::Collecting),
            9400.0,
        );
        assert_eq!(next.metrics.current_rate, Some(1000));
        assert_eq!(next.metrics.peak_rate, 1000);
        assert_eq!(next.history.len(), live.history.len() + 1);
        assert_eq!(next.history.last().unwrap().logical_ms, 2200.0);
        assert_ne!(
            next.history.last().unwrap().segment,
            live.history.last().unwrap().segment
        );
        r.generation = 2;
        assert!(
            state
                .sample(&r, &p, None, Instant::now(), &AtomicBool::new(false))
                .is_none()
        );
        r.generation = 1;
        r.reset = 1;
        let reset = sample(
            &mut state,
            &r,
            &prefix(&mut s, CapturePhase::Collecting),
            9400.0,
        );
        assert!(reset.belongs_to(1, 1, 1));
        assert!(!reset.belongs_to(2, 1, 1));
        assert_eq!(reset.history.len(), 1);
    }
    #[test]
    fn watermark_excludes_later_same_timestamp_reports() {
        let mut s = session();
        for t in 1000..=1400 {
            append(&mut s, t);
        }
        let p = prefix(&mut s, CapturePhase::Collecting);
        let r = request(p.clone());
        let now = Instant::now();
        let m = mark(&p, 1400.0, now);
        append(&mut s, 1400);
        let p = prefix(&mut s, CapturePhase::Collecting);
        let f = State::default()
            .sample(&r, &p, Some(m), now, &AtomicBool::new(false))
            .unwrap();
        assert_eq!(f.metrics.current_rate, Some(1000));
        assert_eq!(f.processed, 401);
        assert_eq!(f.received_sequence, 401);
    }
    #[test]
    fn coalesced_pause_resume_preserves_the_explicit_clock_boundary() {
        let mut s = session();
        for t in 1000..=1800 {
            append(&mut s, t);
        }
        let p = prefix(&mut s, CapturePhase::Collecting);
        let mut r = request(p.clone());
        let mut state = State::default();
        let live = sample(&mut state, &r, &p, 1800.0);
        append(&mut s, 1801);
        s.pause();
        s.begin_collecting(9_000_000, false, false).unwrap();
        // The latest-only mailbox can skip the intermediate paused request.
        r.revision = 2;
        r.pause_ms = Some(1800.0);
        r.now_ms = 9000.0;
        let warming = sample(
            &mut state,
            &r,
            &prefix(&mut s, CapturePhase::Collecting),
            9000.0,
        );
        assert_eq!(warming.metrics.status, RateStatus::WarmingUp);
        assert_eq!(warming.metrics.peak_rate, live.metrics.peak_rate);
        assert!(Arc::ptr_eq(&warming.history, &live.history));
        for t in 9000..=9400 {
            append(&mut s, t);
        }
        let resumed = sample(
            &mut state,
            &r,
            &prefix(&mut s, CapturePhase::Collecting),
            9400.0,
        );
        assert_eq!(resumed.metrics.current_rate, Some(1000));
        assert_eq!(resumed.history.last().unwrap().logical_ms, 2200.0);
        assert_ne!(
            resumed.history.last().unwrap().segment,
            live.history.last().unwrap().segment
        );
    }
    #[test]
    fn clear_and_source_replacement_reset_state_but_control_revisions_do_not() {
        let mut s = session();
        let p = prefix(&mut s, CapturePhase::Collecting);
        let mut r = request(p.clone());
        r.running = false;
        let mut worker = Worker::spawn();
        let reader = worker.reader();
        let configure = |request: &Request| {
            worker.configure(request.clone());
            let deadline = Instant::now() + Duration::from_secs(3);
            loop {
                if let Some(frame) = reader.latest()
                    && frame.revision == request.revision
                {
                    break frame;
                }
                assert!(Instant::now() < deadline);
                thread::sleep(Duration::from_millis(1));
            }
        };
        assert_eq!(configure(&r).observation, 1);
        r.revision += 1;
        assert_eq!(configure(&r).observation, 2);
        worker.clear(2);
        assert!(reader.latest().is_some());
        worker.clear(1);
        assert!(reader.latest().is_none());
        r.revision += 1;
        assert_eq!(configure(&r).observation, 1);
        r.revision += 1;
        assert_eq!(configure(&r).observation, 2);
        r.source = PrefixReader::controlled(p);
        r.revision += 1;
        assert_eq!(configure(&r).observation, 1);
        worker.shutdown();
    }
    #[test]
    fn bounded_worker_publication_rejects_revision_and_receiver_changes() {
        let mut s = session();
        let mut r = request(prefix(&mut s, CapturePhase::Collecting));
        let mut worker = Worker::spawn();
        let reader = worker.reader();
        worker.configure(r.clone());
        let wait = |revision, receiver| {
            let deadline = Instant::now() + Duration::from_secs(3);
            loop {
                if let Some(f) = reader.latest()
                    && f.revision == revision
                    && f.belongs_to(receiver, 1, 0)
                {
                    break f;
                }
                assert!(Instant::now() < deadline);
                thread::sleep(Duration::from_millis(1));
            }
        };
        let f = wait(0, 1);
        assert_eq!(f.metrics.status, RateStatus::Delayed);
        assert!(f.history.is_empty());
        assert_eq!(f.sample_hz, SAMPLE_HZ);
        assert_eq!(f.high_resolution_timer, worker.wake.high_resolution);
        assert!(
            f.execution_intervals_ms
                .iter()
                .all(|dt| *dt > 0.0 && dt.is_finite())
        );
        r.running = false;
        r.revision = 1;
        worker.configure(r.clone());
        let f = wait(1, 1);
        assert!(!f.running);
        let observation = f.observation;
        thread::sleep(Duration::from_millis(30));
        assert_eq!(reader.latest().unwrap().observation, observation);
        worker.clear(1);
        assert!(reader.latest().is_none());
        r.receiver = 2;
        worker.configure(r);
        assert!(!wait(1, 2).belongs_to(1, 1, 0));
        worker.shutdown();
        assert!(reader.latest().is_none());
    }
}
