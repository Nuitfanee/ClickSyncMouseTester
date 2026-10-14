use super::*;
use clicksync_capture::observation::{MotionCandidate, MotionResult};

#[derive(Default)]
pub(super) struct BusinessInput {
    pub bindings: [Option<u32>; 2],
    pub binding_slot: Option<usize>,
    pub first: Option<MotionCandidate>,
    pub issue: Option<MotionResult>,
}

impl GpuiHost {
    pub(super) fn begin_device_binding(&mut self, slot: usize) {
        if self.page != Page::Sensitivity
            || !self.was_active
            || slot > 1
            || self.runtime.borrow().pending_action().is_some()
            || self.lock.locked()
        {
            return;
        }
        self.runtime.borrow_mut().cancel_motion();
        self.round_cancelled += 1;
        self.sensitivity_reset += 1;
        self.domain_frame = None;
        self.business_input.bindings[slot] = None;
        self.business_input.first = None;
        self.business_input.issue = None;
        self.sensitivity.disconnected[slot] = false;
        self.selected = self
            .business_input
            .bindings
            .iter()
            .flatten()
            .copied()
            .collect();
        self.selection_initialized = true;
        let excluded = self.business_input.bindings[1 - slot];
        let runtime = self.runtime.clone();
        if let Some(backend) = &runtime.borrow().backend {
            backend.set_capture_permitted(true);
        }
        match runtime.borrow_mut().observe_motion(
            self.receiver,
            excluded,
            false,
            Some(Duration::from_secs(10)),
        ) {
            Ok(_) => self.business_input.binding_slot = Some(slot),
            Err(error) => self.control_error = Some(error),
        }
    }
    pub(super) fn begin_calibration_motion(&mut self) {
        if self.page != Page::Calibration
            || !self.was_active
            || self.runtime.borrow().pending_action().is_some()
            || self.lock.locked()
        {
            return;
        }
        if self
            .runtime
            .borrow()
            .status()
            .is_none_or(|status| status.devices.is_empty())
        {
            return;
        }
        if let Err(error) = self.lock.acquire() {
            self.control_error = Some(error);
            return;
        }
        self.business_input.first = None;
        self.business_input.issue = None;
        self.selected.clear();
        self.selection_initialized = true;
        self.calibration.disconnected = false;
        self.calibration.rearm_pending = true;
        self.pump_calibration_motion();
        self.observe_capture();
    }
    pub(super) fn business_events(&mut self, events: &mut runtime::RuntimeEvents) -> bool {
        let mut changed = false;
        if let Some((capture, ack)) = events.motion_armed.take() {
            if capture {
                self.page_generation = ack.generation;
            }
            changed = true;
        }
        if let Some(result) = events.motion.take() {
            match result {
                Ok(MotionResult::Selected(candidate)) => {
                    self.business_input.first = Some(candidate);
                    if self.page == Page::Calibration {
                        self.calibration.observe_device(candidate.device_key);
                        self.selected = vec![candidate.device_key];
                    } else if self.page == Page::Sensitivity
                        && let Some(slot) = self.business_input.binding_slot.take()
                    {
                        self.business_input.bindings[slot] = Some(candidate.device_key);
                        self.sensitivity.disconnected[slot] = false;
                        self.selected = self
                            .business_input
                            .bindings
                            .iter()
                            .copied()
                            .flatten()
                            .collect();
                    }
                    self.selection_initialized = true;
                }
                Ok(issue) => {
                    self.business_input.issue = Some(issue);
                    self.business_input.binding_slot = None;
                    if self.page == Page::Calibration {
                        self.capture_command("pause");
                    }
                }
                Err(error) => {
                    self.control_error = Some(error);
                    self.business_input.binding_slot = None;
                    self.lock.release();
                }
            }
            changed = true;
        }
        changed
    }
}
