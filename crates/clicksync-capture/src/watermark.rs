//! On-demand source-thread observation barriers, outside the per-report hot path.
use std::sync::{
    Mutex,
    atomic::{AtomicBool, Ordering},
};
use std::time::Instant;

#[derive(Clone, Copy, Debug)]
pub struct Checkpoint {
    pub generation: u64,
    pub sequence: u64,
    pub capture_sequence: u64,
    pub ticks: u64,
    pub frequency: u64,
    pub active: bool,
    pub complete: bool,
    pub dropped_packets: u64,
    pub observed: Instant,
}

#[derive(Default)]
pub struct Watermark {
    pending: AtomicBool,
    latest: Mutex<Option<Checkpoint>>,
}

impl Watermark {
    pub fn request(&self) -> bool {
        !self.pending.swap(true, Ordering::AcqRel)
    }

    pub fn requested(&self) -> bool {
        self.pending.load(Ordering::Acquire)
    }

    pub fn cancel_request(&self) {
        self.pending.store(false, Ordering::Release);
    }

    pub fn latest(&self) -> Option<Checkpoint> {
        *self.latest.lock().expect("capture watermark")
    }

    pub(crate) fn publish(&self, checkpoint: Checkpoint) {
        // Never wait for a reader on the source thread; retry at its next request.
        if let Ok(mut latest) = self.latest.try_lock() {
            *latest = Some(checkpoint);
        }
        self.cancel_request();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn requests_coalesce_and_publication_never_waits_for_a_reader() {
        let watermark = Watermark::default();
        assert!(watermark.request());
        assert!(!watermark.request());
        let checkpoint = Checkpoint {
            generation: 2,
            sequence: 7,
            capture_sequence: 20,
            ticks: 100,
            frequency: 1000,
            active: true,
            complete: true,
            dropped_packets: 0,
            observed: Instant::now(),
        };
        let reader = watermark.latest.lock().unwrap();
        watermark.publish(checkpoint);
        assert!(!watermark.requested());
        drop(reader);
        assert!(watermark.latest().is_none());
        assert!(watermark.request());
        watermark.publish(checkpoint);
        assert_eq!(watermark.latest().unwrap().sequence, 7);
    }
}
