pub mod delivery;
pub mod observation;
pub mod platform;
pub mod runtime;
pub mod watermark;
pub mod worker;

pub use clicksync_core::protocol::CapturedRecord;
use std::sync::{
    OnceLock,
    atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
};
use std::thread::{self, Thread};
use std::time::Duration;

/// A renewed permission never revalidates an older queued Start/Resume.
pub struct CapturePermit(AtomicU64);

impl Default for CapturePermit {
    fn default() -> Self {
        Self(AtomicU64::new(1))
    }
}

impl CapturePermit {
    pub fn set(&self, allowed: bool) {
        let _ = self
            .0
            .fetch_update(Ordering::AcqRel, Ordering::Acquire, |old| {
                if old >= u64::MAX - 1 {
                    return (old & 1 != 0).then_some(u64::MAX - 1);
                }
                if (old & 1 != 0) == allowed {
                    None
                } else {
                    Some(old + 1)
                }
            });
    }

    pub fn ticket(&self) -> u64 {
        self.0.load(Ordering::Acquire)
    }

    pub fn allows(&self, ticket: u64) -> bool {
        ticket & 1 != 0 && self.ticket() == ticket
    }
}

/// One consumer; producers only unpark a consumer that has armed its wait.
#[derive(Default)]
pub struct CaptureWake {
    consumer: OnceLock<Thread>,
    waiting: AtomicBool,
}

impl CaptureWake {
    pub fn arm(&self) {
        self.consumer.get_or_init(thread::current);
        // Store-before-queue-recheck must not become a store/load reorder.
        self.waiting.store(true, Ordering::SeqCst);
    }

    pub fn notify(&self) {
        if self.waiting.swap(false, Ordering::SeqCst)
            && let Some(consumer) = self.consumer.get()
        {
            consumer.unpark();
        }
    }

    pub fn wait(&self, timeout: Duration) {
        thread::park_timeout(timeout);
        self.waiting.store(false, Ordering::Release);
    }

    pub fn disarm(&self) {
        self.waiting.store(false, Ordering::Release);
    }
}

pub struct ActivePermit(AtomicU64);

impl Default for ActivePermit {
    fn default() -> Self {
        Self(AtomicU64::new(1))
    }
}

#[derive(Default)]
pub struct CaptureShared {
    pub watermark: watermark::Watermark,
    pub active: AtomicBool,
    pub generation: AtomicU64,
    pub captured: AtomicU64,
    pub queued: AtomicU64,
    pub overflow: AtomicU64,
    pub high_watermark: AtomicUsize,
    pub disconnected: AtomicBool,
    pub input_dropped: AtomicU64,
    pub permit: CapturePermit,
    pub wake: CaptureWake,
    pub active_permit: ActivePermit,
    pub delivery: delivery::RawDelivery,
    pub raw_input_window: AtomicUsize,
    pub motion: observation::MotionMailbox,
}

impl CaptureShared {
    pub(crate) fn motion_current(&self) -> bool {
        self.motion.has_request()
    }
    pub fn is_active(&self) -> bool {
        self.active.load(Ordering::Acquire)
            && self
                .permit
                .allows(self.active_permit.0.load(Ordering::Acquire))
    }

    pub(crate) fn authorize(&self, ticket: u64) -> bool {
        if !self.permit.allows(ticket) {
            return false;
        }
        self.active_permit.0.store(ticket, Ordering::Release);
        self.permit.allows(ticket)
    }
}
