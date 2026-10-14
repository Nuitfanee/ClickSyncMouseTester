use super::*;
use clicksync_core::{SessionEvent, SessionSnapshot, SessionStatus};

#[derive(Clone)]
pub struct SummaryRequest {
    pub prefix: Arc<SessionPrefix>,
    pub source_version: u64,
    pub dataset_cpi: Vec<(u32, Option<f64>)>,
    pub now_ms: f64,
}

impl SummaryRequest {
    pub fn same_input(&self, other: &Self) -> bool {
        self.source_version == other.source_version
            && self.dataset_cpi == other.dataset_cpi
            && self.prefix.generation == other.prefix.generation
            && self.prefix.phase == other.prefix.phase
            && self.prefix.complete == other.prefix.complete
            && self.prefix.received_sequence == other.prefix.received_sequence
            && self.prefix.datasets.len() == other.prefix.datasets.len()
            && self.prefix.datasets.iter().zip(&other.prefix.datasets).all(
                |((key, snapshot), (other_key, other))| {
                    key == other_key
                        && snapshot.generation() == other.generation()
                        && snapshot.session_revision() == other.session_revision()
                        && snapshot.event_count() == other.event_count()
                        && snapshot.timestamp_frequency() == other.timestamp_frequency()
                },
            )
    }
}

pub struct SummaryFrame {
    pub request: SummaryRequest,
    pub speeds: Vec<Option<f64>>,
    pub next_refresh_ms: Option<f64>,
}

#[derive(Default)]
pub(super) struct SummaryState {
    identity: Option<(u64, u64)>,
    datasets: Vec<VelocityWindow>,
}

impl SummaryState {
    pub fn advance(
        &mut self,
        request: SummaryRequest,
        cancel: &std::sync::atomic::AtomicBool,
    ) -> Option<SummaryFrame> {
        let identity = (request.source_version, request.prefix.generation);
        if self.identity != Some(identity) {
            self.datasets.clear();
            self.identity = Some(identity);
        }
        self.datasets
            .resize_with(request.prefix.datasets.len(), VelocityWindow::default);
        self.datasets.truncate(request.prefix.datasets.len());
        let mut speeds = Vec::with_capacity(self.datasets.len());
        for (state, (key, snapshot)) in self.datasets.iter_mut().zip(&request.prefix.datasets) {
            if !state.advance(*key, snapshot, cancel) {
                return None;
            }
            let cpi = request
                .dataset_cpi
                .iter()
                .find(|(id, _)| id == key)
                .and_then(|(_, cpi)| *cpi);
            speeds.push(state.speed(snapshot, cpi, request.now_ms));
        }
        let moving = request.prefix.phase == CapturePhase::Collecting
            && speeds
                .iter()
                .any(|speed| speed.is_some_and(|speed| speed > 0.0));
        let next_refresh_ms = moving.then_some(request.now_ms + 20.0);
        Some(SummaryFrame {
            request,
            speeds,
            next_refresh_ms,
        })
    }
}

// The original velocity queue is represented by indices into the immutable
// session. Trimming and scanning keep its report order without a second record
// buffer; each discarded interval is visited only once.
#[derive(Default)]
struct VelocityWindow {
    identity: Option<(u32, u64, u64)>,
    processed: usize,
    head: usize,
    distance: f64,
    covered_ticks: u64,
}

impl VelocityWindow {
    fn advance(
        &mut self,
        key: u32,
        snapshot: &SessionSnapshot,
        cancel: &std::sync::atomic::AtomicBool,
    ) -> bool {
        let identity = (key, snapshot.generation(), snapshot.timestamp_frequency());
        if self.identity != Some(identity) || self.processed > snapshot.event_count() {
            *self = Self {
                identity: Some(identity),
                head: 1,
                ..Default::default()
            };
        }
        let window_ticks = window_ticks(snapshot);
        while self.processed < snapshot.event_count() {
            if self.processed.is_multiple_of(4096)
                && cancel.load(std::sync::atomic::Ordering::Acquire)
            {
                return false;
            }
            let index = self.processed;
            self.processed += 1;
            if let Some((previous, current, distance)) = interval(snapshot, index) {
                self.distance += distance;
                self.covered_ticks += current.raw_capture_ticks - previous.raw_capture_ticks;
                self.trim(
                    snapshot,
                    current.raw_capture_ticks.saturating_sub(window_ticks),
                );
            }
        }
        true
    }

    fn trim(&mut self, snapshot: &SessionSnapshot, minimum_end: u64) {
        while self.head < self.processed {
            let Some((previous, current, distance)) = interval(snapshot, self.head) else {
                self.head += 1;
                continue;
            };
            if current.raw_capture_ticks > minimum_end {
                break;
            }
            self.distance -= distance;
            self.covered_ticks -= current.raw_capture_ticks - previous.raw_capture_ticks;
            self.head += 1;
        }
        if self.head >= self.processed {
            self.distance = 0.0;
            self.covered_ticks = 0;
        }
    }

    fn speed(&mut self, snapshot: &SessionSnapshot, cpi: Option<f64>, now_ms: f64) -> Option<f64> {
        let cpi = cpi.filter(|cpi| cpi.is_finite() && *cpi > 0.0)?;
        let last = snapshot.event_at(snapshot.event_count().checked_sub(1)?)?;
        if snapshot.status() != SessionStatus::Collecting {
            let elapsed =
                last.logical_ticks as f64 * 1000.0 / snapshot.timestamp_frequency() as f64;
            return Some(if elapsed > 0.0 {
                snapshot.summary_path_counts() / elapsed / cpi * 25.4
            } else {
                0.0
            });
        }
        let end = (now_ms.max(0.0) * snapshot.timestamp_frequency() as f64 / 1000.0)
            .round_ties_even() as u64;
        let start = end.saturating_sub(window_ticks(snapshot));
        self.trim(snapshot, start);
        let mut distance = self.distance;
        let mut covered = self.covered_ticks;
        if last.raw_capture_ticks > end {
            distance = 0.0;
            covered = 0;
            for index in self.head..self.processed {
                let Some((previous, current, sample_distance)) = interval(snapshot, index) else {
                    continue;
                };
                let overlap_start = start.max(previous.raw_capture_ticks);
                let overlap_end = end.min(current.raw_capture_ticks);
                if overlap_start >= overlap_end {
                    continue;
                }
                let ticks = overlap_end - overlap_start;
                distance += sample_distance * ticks as f64
                    / (current.raw_capture_ticks - previous.raw_capture_ticks) as f64;
                covered += ticks;
            }
        } else if let Some((previous, current, sample_distance)) = interval(snapshot, self.head)
            && previous.raw_capture_ticks < start
        {
            let duration = current.raw_capture_ticks - previous.raw_capture_ticks;
            let excluded = duration.min(start - previous.raw_capture_ticks);
            distance -= sample_distance * excluded as f64 / duration as f64;
            covered -= excluded;
        }
        let elapsed = covered as f64 * 1000.0 / snapshot.timestamp_frequency() as f64;
        Some(if elapsed > 0.0 && distance > 0.0 {
            distance / elapsed / cpi * 25.4
        } else {
            0.0
        })
    }
}

fn window_ticks(snapshot: &SessionSnapshot) -> u64 {
    (30.0 * snapshot.timestamp_frequency() as f64 / 1000.0).round_ties_even() as u64
}

fn interval(snapshot: &SessionSnapshot, index: usize) -> Option<(SessionEvent, SessionEvent, f64)> {
    let previous = snapshot.event_at(index.checked_sub(1)?)?;
    let current = snapshot.event_at(index)?;
    if previous.session_segment_id != current.session_segment_id
        || previous.raw_capture_ticks >= current.raw_capture_ticks
        || previous.timing_sequence >= current.timing_sequence
    {
        return None;
    }
    let (x, y) = (current.delta_x as f64, current.delta_y as f64);
    Some((previous, current, (x * x + y * y).sqrt()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use clicksync_core::{
        RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SharedByteBudget,
    };
    use std::sync::atomic::AtomicBool;

    fn session() -> Session {
        let config = SessionConfig {
            timestamp_frequency: 1000,
            ..Default::default()
        };
        let mut session = Session::new(
            config.clone(),
            SharedByteBudget::new(config.managed_memory_budget_bytes),
            Default::default(),
        )
        .unwrap();
        session.begin_collecting(1000, true, false).unwrap();
        session
    }
    fn append(session: &mut Session, ticks: u64, sequence: u64, x: i32) {
        session
            .append(RawMouseRecord::new(
                x,
                0,
                ticks,
                sequence,
                sequence,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    fn request(session: &mut Session, now_ms: f64) -> SummaryRequest {
        SummaryRequest {
            prefix: Arc::new(SessionPrefix {
                generation: 1,
                received_sequence: session.event_count() as u64,
                received_records: session.event_count() as u64,
                phase: if session.status() == SessionStatus::Collecting {
                    CapturePhase::Collecting
                } else {
                    CapturePhase::Paused
                },
                complete: true,
                datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
            }),
            source_version: 1,
            dataset_cpi: vec![(1, Some(1000.0))],
            now_ms,
        }
    }
    fn near(actual: Option<f64>, expected: f64) {
        assert!(
            (actual.unwrap() - expected).abs() < 1e-12,
            "{actual:?} != {expected}"
        );
    }

    #[test]
    fn current_speed_clips_the_thirty_ms_window_decays_and_uses_session_average_when_paused() {
        let mut session = session();
        let mut state = SummaryState::default();
        let cancel = AtomicBool::new(false);
        for (ticks, sequence, x) in [(1000, 1, 1), (1010, 2, 10), (1020, 3, 20), (1040, 4, 10)] {
            append(&mut session, ticks, sequence, x);
            state
                .advance(request(&mut session, ticks as f64), &cancel)
                .unwrap();
        }
        for (now, expected) in [
            (1040.0, 0.0254),
            (1050.0, 0.0127),
            (1060.0, 0.0127),
            (1070.0, 0.0),
        ] {
            let frame = state.advance(request(&mut session, now), &cancel).unwrap();
            near(frame.speeds[0], expected);
            assert_eq!(frame.next_refresh_ms.is_some(), expected > 0.0);
        }
        session.pause();
        near(
            state
                .advance(request(&mut session, 1100.0), &cancel)
                .unwrap()
                .speeds[0],
            0.026035,
        );
        session.begin_collecting(2000, false, false).unwrap();
        append(&mut session, 2000, 5, 10);
        near(
            state
                .advance(request(&mut session, 2000.0), &cancel)
                .unwrap()
                .speeds[0],
            0.0,
        );
        append(&mut session, 2010, 6, 20);
        near(
            state
                .advance(request(&mut session, 2010.0), &cancel)
                .unwrap()
                .speeds[0],
            0.0508,
        );
    }

    #[test]
    fn unordered_ticks_duplicate_ticks_and_sequence_regressions_keep_original_interval_order() {
        let mut session = session();
        for (ticks, sequence, x) in [
            (1000, 1, 1),
            (1010, 2, 10),
            (1010, 3, 100),
            (1005, 4, 100),
            (1020, 3, 30),
            (1030, 5, 20),
        ] {
            append(&mut session, ticks, sequence, x);
        }
        let cancel = AtomicBool::new(false);
        let mut state = SummaryState::default();
        near(
            state
                .advance(request(&mut session, 1030.0), &cancel)
                .unwrap()
                .speeds[0],
            0.0381,
        );
        append(&mut session, 1050, 6, 20);
        near(
            state
                .advance(request(&mut session, 1040.0), &cancel)
                .unwrap()
                .speeds[0],
            0.0381,
        );
        near(
            state
                .advance(request(&mut session, 1060.0), &cancel)
                .unwrap()
                .speeds[0],
            0.0254,
        );
        near(
            state
                .advance(request(&mut session, 1080.0), &cancel)
                .unwrap()
                .speeds[0],
            0.0,
        );
        let mut cpi = request(&mut session, 1060.0);
        cpi.dataset_cpi[0].1 = Some(2000.0);
        // Advancing time cannot restore intervals that have already expired.
        near(state.advance(cpi, &cancel).unwrap().speeds[0], 0.0);
    }

    #[test]
    fn independent_cpi_and_source_replacement_do_not_reuse_old_speed() {
        let mut session = session();
        append(&mut session, 1000, 1, 1);
        append(&mut session, 1010, 2, 10);
        let mut request = request(&mut session, 1010.0);
        let snapshot = request.prefix.datasets[0].1.clone();
        request.prefix = Arc::new(SessionPrefix {
            datasets: vec![(1, snapshot.clone()), (2, snapshot)],
            generation: request.prefix.generation,
            received_sequence: request.prefix.received_sequence * 2,
            received_records: request.prefix.received_records * 2,
            phase: request.prefix.phase,
            complete: request.prefix.complete,
        });
        request.dataset_cpi.push((2, Some(2000.0)));
        let mut state = SummaryState::default();
        let cancel = AtomicBool::new(false);
        let frame = state.advance(request.clone(), &cancel).unwrap();
        near(frame.speeds[0], 0.0254);
        near(frame.speeds[1], 0.0127);
        request.source_version += 1;
        request.now_ms = 1050.0;
        let frame = state.advance(request, &cancel).unwrap();
        assert_eq!(frame.speeds, vec![Some(0.0), Some(0.0)]);
        assert!(frame.next_refresh_ms.is_none());
    }
}
