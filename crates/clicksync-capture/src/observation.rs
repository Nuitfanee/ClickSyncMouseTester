//! One in-process, one-shot first-motion observation. No wire protocol changes.
use crate::CaptureShared;
use clicksync_core::{RawMouseRecord, devices::DeviceDescriptor};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::{
    Mutex,
    atomic::{AtomicU64, Ordering},
};
use std::time::{Duration, Instant};

#[derive(Clone, Copy, Debug)]
pub struct MotionRequest {
    pub id: u64,
    pub excluded: Option<u32>,
    pub capture_generation: Option<u64>,
    pub timeout: Option<Duration>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct MotionCandidate {
    pub request: u64,
    pub device_key: u32,
    pub ticks: u64,
    pub capture_sequence: u64,
    pub timing_sequence: u64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum MotionResult {
    Selected(MotionCandidate),
    TimedOut,
    DeviceRemoved,
    Revoked,
}

#[derive(Default)]
pub struct MotionMailbox {
    next: AtomicU64,
    current: AtomicU64,
    result: Mutex<Option<(u64, MotionResult)>>,
    capture_generation: AtomicU64,
    owner_ready: AtomicU64,
    removed: AtomicU64,
}
impl MotionMailbox {
    pub fn has_request(&self) -> bool {
        self.current.load(Ordering::Acquire) != 0
    }
    pub fn reserve(&self) -> Result<u64, &'static str> {
        let mut slot = self.result.lock().expect("motion result");
        if slot.is_some() {
            return Err("previous motion result has not been consumed");
        }
        let id = self
            .next
            .fetch_add(1, Ordering::Relaxed)
            .checked_add(1)
            .ok_or("motion request identity exhausted")?;
        self.current
            .compare_exchange(0, id, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| "motion observation already active")?;
        *slot = None;
        self.capture_generation.store(0, Ordering::Release);
        self.owner_ready.store(0, Ordering::Release);
        self.removed.store(0, Ordering::Release);
        Ok(id)
    }
    pub fn configure(&self, id: u64, generation: Option<u64>) -> bool {
        let _slot = self.result.lock().expect("motion result");
        if !self.is_current(id) {
            return false;
        }
        self.capture_generation
            .store(generation.unwrap_or(0), Ordering::Release);
        true
    }
    pub fn is_current(&self, id: u64) -> bool {
        id != 0 && self.current.load(Ordering::Acquire) == id
    }
    pub fn cancel(&self, id: u64) {
        let _ = self
            .current
            .compare_exchange(id, 0, Ordering::AcqRel, Ordering::Acquire);
        let mut slot = self.result.lock().expect("motion result");
        if slot.is_some_and(|(request, result)| {
            request == id
                && (self.capture_generation.load(Ordering::Acquire) == 0
                    || self.owner_ready.load(Ordering::Acquire) == id
                    || !matches!(result, MotionResult::Selected(_)))
        }) {
            *slot = None;
        }
    }
    pub fn cancel_current(&self) {
        let id = self.current.load(Ordering::Acquire);
        self.cancel(id);
    }
    pub fn invalidate_device(&self, key: u32) {
        let mut slot = self.result.lock().expect("motion result");
        if let Some((id, MotionResult::Selected(candidate))) = *slot
            && candidate.device_key == key
            && self.is_current(id)
        {
            self.removed.store(id, Ordering::Release);
            if self.capture_generation.load(Ordering::Acquire) == 0 {
                *slot = Some((id, MotionResult::DeviceRemoved));
            }
        }
    }
    pub fn take(&self, id: u64) -> Option<MotionResult> {
        let mut slot = self.result.lock().expect("motion result");
        if !self.is_current(id) || !slot.is_some_and(|(request, _)| request == id) {
            return None;
        }
        if self.capture_generation.load(Ordering::Acquire) != 0
            && matches!(*slot, Some((_, MotionResult::Selected(_))))
            && self.owner_ready.load(Ordering::Acquire) != id
        {
            return None;
        }
        let result = slot.take().map(|(_, result)| {
            if self.removed.load(Ordering::Acquire) == id {
                MotionResult::DeviceRemoved
            } else {
                result
            }
        });
        let _ = self
            .current
            .compare_exchange(id, 0, Ordering::AcqRel, Ordering::Acquire);
        result
    }
    pub fn peek(&self, id: u64) -> Option<MotionResult> {
        let slot = self.result.lock().expect("motion result");
        slot.filter(|(request, _)| *request == id)
            .map(|(_, result)| result)
    }
    pub fn acknowledge_owner(&self, id: u64) {
        let mut slot = self.result.lock().expect("motion result");
        self.owner_ready.store(id, Ordering::Release);
        if !self.is_current(id) && slot.is_some_and(|(request, _)| request == id) {
            *slot = None;
        }
    }
    pub fn revoke_unselected(&self) {
        let id = self.current.load(Ordering::Acquire);
        if id != 0 {
            self.publish(id, MotionResult::Revoked);
        }
    }
    pub fn publish(&self, id: u64, result: MotionResult) -> bool {
        let mut slot = self.result.lock().expect("motion result");
        if !self.is_current(id) || slot.is_some() {
            return false;
        }
        *slot = Some((id, result));
        true
    }
}

pub(crate) struct ArmedMotion {
    pub request: MotionRequest,
    ticket: u64,
    deadline: Option<Instant>,
    pub eligibility: MotionEligibility,
}

/// Resolve physical exclusions on the cold path; reports use integer membership only.
pub(crate) struct MotionEligibility {
    excluded: Option<u32>,
    excluded_group: String,
    keys: HashMap<u32, bool>,
}
impl MotionEligibility {
    pub fn new(devices: &[DeviceDescriptor], excluded: Option<u32>) -> Self {
        let mut result = Self {
            excluded,
            excluded_group: String::new(),
            keys: HashMap::with_capacity(devices.len()),
        };
        result.update(devices);
        result
    }

    pub fn update(&mut self, devices: &[DeviceDescriptor]) {
        // Preserve the excluded physical identity if its original endpoint disconnects.
        if let Some(device) = devices
            .iter()
            .find(|device| Some(device.key) == self.excluded)
        {
            self.excluded_group = device.physical_device_key().into_owned();
        }
        self.keys.clear();
        self.keys.extend(
            devices
                .iter()
                .filter(|device| {
                    device.eligible_for_motion()
                        && Some(device.key) != self.excluded
                        && (self.excluded_group.is_empty()
                            || device.physical_device_key().as_ref() != self.excluded_group)
                })
                .map(|device| (device.key, device.endpoint.requires_motion_confirmation())),
        );
    }

    pub fn is_empty(&self) -> bool {
        self.keys.is_empty()
    }
}

impl ArmedMotion {
    pub fn new(request: MotionRequest, ticket: u64, devices: &[DeviceDescriptor]) -> Self {
        Self {
            request,
            ticket,
            deadline: request.timeout.map(|timeout| Instant::now() + timeout),
            eligibility: MotionEligibility::new(devices, request.excluded),
        }
    }
    pub fn live(&self, shared: &CaptureShared) -> bool {
        shared.motion.is_current(self.request.id) && shared.permit.allows(self.ticket)
    }
    pub fn offer(
        &self,
        shared: &CaptureShared,
        key: u32,
        record: &RawMouseRecord,
    ) -> Option<MotionCandidate> {
        if !self.live(shared) {
            return None;
        }
        if self
            .deadline
            .is_some_and(|deadline| Instant::now() >= deadline)
        {
            if shared
                .motion
                .publish(self.request.id, MotionResult::TimedOut)
            {
                shared.wake.notify();
            }
            return None;
        }
        if record.raw_delta_x == 0 && record.raw_delta_y == 0 {
            return None;
        }
        let relative_only = *self.eligibility.keys.get(&key)?;
        if relative_only && record.relative_delta() == (0, 0) {
            return None;
        }
        let candidate = MotionCandidate {
            request: self.request.id,
            device_key: key,
            ticks: record.raw_capture_ticks,
            capture_sequence: record.capture_sequence,
            timing_sequence: record.timing_sequence,
        };
        Some(candidate)
    }
    pub fn expire(&self, shared: &CaptureShared) -> bool {
        let result = if !shared.permit.allows(self.ticket) {
            Some(MotionResult::Revoked)
        } else if self
            .deadline
            .is_some_and(|deadline| Instant::now() >= deadline)
        {
            Some(MotionResult::TimedOut)
        } else {
            None
        };
        if let Some(result) = result {
            if shared.motion.publish(self.request.id, result) {
                shared.wake.notify();
            }
            return true;
        }
        !shared.motion.is_current(self.request.id)
    }
}

/// Metadata exclusions shared with the device catalog.
pub use clicksync_core::devices::eligible_metadata;

#[cfg(feature = "controlled-input")]
#[derive(Clone, Debug)]
pub struct MotionReport {
    pub device_key: u32,
    pub record: RawMouseRecord,
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cancelled_selected_capture_waits_for_owner_ack_before_releasing_its_single_slot() {
        let mailbox = MotionMailbox::default();
        let id = mailbox.reserve().unwrap();
        assert!(mailbox.configure(id, Some(1)));
        let candidate = MotionCandidate {
            request: id,
            device_key: 2,
            ticks: 100,
            capture_sequence: 1,
            timing_sequence: 1,
        };
        assert!(mailbox.publish(id, MotionResult::Selected(candidate)));
        mailbox.cancel(id);
        assert_eq!(mailbox.peek(id), Some(MotionResult::Selected(candidate)));
        assert!(mailbox.reserve().is_err());
        assert!(mailbox.take(id).is_none());
        mailbox.acknowledge_owner(id);
        let next = mailbox.reserve().unwrap();
        assert!(!mailbox.publish(id, MotionResult::Selected(candidate)));
        mailbox.cancel(next);
    }
    #[test]
    fn removed_selected_capture_still_provides_owner_input_identity_before_ui_reports_removal() {
        let mailbox = MotionMailbox::default();
        let id = mailbox.reserve().unwrap();
        assert!(mailbox.configure(id, Some(1)));
        let candidate = MotionCandidate {
            request: id,
            device_key: 2,
            ticks: 100,
            capture_sequence: 1,
            timing_sequence: 1,
        };
        assert!(mailbox.publish(id, MotionResult::Selected(candidate)));
        mailbox.invalidate_device(2);
        assert_eq!(mailbox.peek(id), Some(MotionResult::Selected(candidate)));
        mailbox.acknowledge_owner(id);
        assert_eq!(mailbox.take(id), Some(MotionResult::DeviceRemoved));
    }
}
