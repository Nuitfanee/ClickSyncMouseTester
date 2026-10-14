//! In-process capture host. The producer and control semantics are shared with the worker.
use crate::{
    CaptureShared, CapturedRecord,
    worker::{CommandState, InputSource},
};
use clicksync_core::protocol::{
    CAPTURE_QUEUE_RECORDS, CaptureAck, CaptureStatus, ControlCommand, ControlInputBatch,
    ControlInputEvent, DeviceDescriptor, INPUT_QUEUE_EVENTS, MAX_BATCH_RECORDS,
    MAX_INPUT_BATCH_EVENTS,
};
use std::{
    io,
    sync::{Arc, Mutex, atomic::Ordering},
};

pub struct CaptureRuntime {
    source: InputSource,
    commands: CommandState,
    shared: Arc<CaptureShared>,
    devices: Arc<Mutex<Vec<DeviceDescriptor>>>,
}

pub struct CaptureReader {
    records: rtrb::Consumer<CapturedRecord>,
    inputs: rtrb::Consumer<ControlInputEvent>,
    pending: Option<CapturedRecord>,
    shared: Arc<CaptureShared>,
    frequency: u64,
    generation: u64,
    consumed: u64,
}

impl CaptureRuntime {
    pub fn spawn(replay: bool) -> io::Result<(Self, CaptureReader)> {
        let (producer, records) = rtrb::RingBuffer::new(CAPTURE_QUEUE_RECORDS);
        let (input_producer, inputs) = rtrb::RingBuffer::new(INPUT_QUEUE_EVENTS);
        let shared = Arc::new(CaptureShared::default());
        let devices = Arc::new(Mutex::new(Vec::new()));
        let source = InputSource::spawn(
            replay,
            producer,
            input_producer,
            shared.clone(),
            devices.clone(),
        )?;
        let frequency = source.clock()?.1;
        let commands = CommandState::new(&source)?;
        let reader = CaptureReader {
            records,
            inputs,
            pending: None,
            shared: shared.clone(),
            frequency,
            generation: 0,
            consumed: 0,
        };
        Ok((
            Self {
                source,
                commands,
                shared,
                devices,
            },
            reader,
        ))
    }

    pub fn shared(&self) -> Arc<CaptureShared> {
        self.shared.clone()
    }

    pub fn command(&mut self, command: ControlCommand, permit: u64) -> io::Result<CaptureAck> {
        if matches!(
            command,
            ControlCommand::Discover | ControlCommand::RescanDevices
        ) && self.shared.motion_current()
        {
            self.source.command_with_permit(
                crate::platform::InputCommand::PollMotion,
                None,
                None,
            )?;
        }
        if matches!(
            command,
            ControlCommand::Start { .. } | ControlCommand::Resume
        ) && !self.shared.permit.allows(permit)
        {
            let (ticks, timestamp_frequency) = self.source.clock()?;
            return Ok(CaptureAck {
                command: command.tag(),
                generation: self.shared.generation.load(Ordering::Acquire),
                cutoff: self.shared.captured.load(Ordering::Acquire),
                queued: self.shared.queued.load(Ordering::Acquire),
                ticks,
                timestamp_frequency,
                complete: false,
                overflow: self.shared.overflow.load(Ordering::Acquire),
                queue_high_watermark: self.shared.high_watermark.load(Ordering::Acquire),
                devices: Vec::new(),
                error: Some("capture permission expired before activation".into()),
            });
        }
        let (ack, fatal) = self.commands.execute(
            command,
            &mut self.source,
            &self.shared,
            &self.devices,
            Some(permit),
        );
        self.shared.wake.notify();
        if let Some(error) = fatal {
            return Err(error);
        }
        Ok(ack)
    }

    pub fn observe_motion(
        &mut self,
        request: crate::observation::MotionRequest,
        permit: u64,
    ) -> io::Result<CaptureAck> {
        if !self.shared.permit.allows(permit) || !self.shared.motion.is_current(request.id) {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "motion request or permission expired",
            ));
        }
        if !self
            .shared
            .motion
            .configure(request.id, request.capture_generation)
        {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "motion request expired before arming",
            ));
        }
        let directory = crate::worker::device_directory(&self.devices)?;
        if crate::observation::MotionEligibility::new(&directory, request.excluded).is_empty() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "no eligible motion device",
            ));
        }
        let input = self.source.command_with_permit(
            crate::platform::InputCommand::Observe {
                request,
                ticket: permit,
            },
            None,
            Some(permit),
        )?;
        let capture = request.capture_generation.is_some();
        self.commands.motion_armed(input, capture);
        self.shared.wake.notify();
        Ok(CaptureAck {
            command: if capture {
                clicksync_core::protocol::CommandTag::Start
            } else {
                clicksync_core::protocol::CommandTag::Discover
            },
            generation: input.generation,
            cutoff: input.cutoff,
            queued: self.shared.queued.load(Ordering::Acquire),
            ticks: input.ticks,
            timestamp_frequency: input.frequency,
            complete: true,
            overflow: self.shared.overflow.load(Ordering::Acquire),
            queue_high_watermark: self.shared.high_watermark.load(Ordering::Acquire),
            devices: directory,
            error: None,
        })
    }

    #[cfg(feature = "controlled-input")]
    pub fn replay_motion(
        &mut self,
        reports: Vec<crate::observation::MotionReport>,
    ) -> io::Result<()> {
        self.source
            .command_with_permit(
                crate::platform::InputCommand::ReplayMotion(reports),
                None,
                None,
            )
            .map(|_| ())
    }
}

impl Drop for CaptureRuntime {
    fn drop(&mut self) {
        self.shared.permit.set(false);
        let _ = self.source.shutdown();
        self.shared.wake.notify();
    }
}

impl CaptureReader {
    /// Caller reuses the batch storage. Never combine records from different generations.
    pub fn drain(&mut self, batch: &mut Vec<CapturedRecord>) {
        batch.clear();
        while batch.len() < MAX_BATCH_RECORDS {
            let Some(record) = self.pending.take().or_else(|| self.records.pop().ok()) else {
                break;
            };
            if batch
                .first()
                .is_some_and(|first| first.generation != record.generation)
            {
                self.pending = Some(record);
                break;
            }
            self.generation = record.generation;
            self.consumed = record.session_sequence;
            batch.push(record);
        }
    }

    pub fn drain_inputs(&mut self, batch: &mut ControlInputBatch) {
        batch.events.clear();
        batch.frequency = self.frequency;
        while batch.events.len() < MAX_INPUT_BATCH_EVENTS {
            let Ok(event) = self.inputs.pop() else {
                break;
            };
            batch.events.push(event);
        }
        batch.dropped = self.shared.input_dropped.load(Ordering::Acquire);
    }

    pub fn has_data(&self) -> bool {
        self.pending.is_some() || !self.records.is_empty() || !self.inputs.is_empty()
    }

    pub fn status(&self) -> CaptureStatus {
        let generation = self.shared.generation.load(Ordering::Acquire);
        CaptureStatus {
            generation,
            captured: self.shared.captured.load(Ordering::Acquire),
            queued: self.shared.queued.load(Ordering::Acquire),
            sent: if self.generation == generation {
                self.consumed
            } else {
                0
            },
            overflow: self.shared.overflow.load(Ordering::Acquire),
            queue_high_watermark: self.shared.high_watermark.load(Ordering::Acquire),
            active: self.shared.is_active(),
            disconnected: self.shared.disconnected.load(Ordering::Acquire),
        }
    }

    pub fn shared(&self) -> Arc<CaptureShared> {
        self.shared.clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejected_start_does_not_reopen_a_revoked_running_source() {
        let (mut runtime, _reader) = CaptureRuntime::spawn(true).unwrap();
        let shared = runtime.shared();
        let mut start = ControlCommand::Start {
            generation: 1,
            device_keys: vec![1],
            start_fresh: true,
            source: clicksync_core::protocol::CaptureSource::Replay {
                records_per_device: 100_000,
                reports_per_second: 1000,
                real_time: true,
            },
        };
        assert!(
            runtime
                .command(start.clone(), shared.permit.ticket())
                .unwrap()
                .error
                .is_none()
        );
        shared.permit.set(false);
        shared.permit.set(true);
        if let ControlCommand::Start {
            generation,
            device_keys,
            ..
        } = &mut start
        {
            *generation = 2;
            *device_keys = vec![99];
        }
        assert!(
            runtime
                .command(start, shared.permit.ticket())
                .unwrap()
                .error
                .is_some()
        );
        assert!(
            !shared.is_active(),
            "a rejected command reopened the old source"
        );
    }

    #[test]
    fn revoked_permission_cannot_be_restored_by_a_delayed_start() {
        let (mut runtime, _reader) = CaptureRuntime::spawn(true).unwrap();
        let shared = runtime.shared();
        let old = shared.permit.ticket();
        shared.permit.set(false);
        shared.permit.set(true);
        let start = ControlCommand::Start {
            generation: 1,
            device_keys: vec![1],
            start_fresh: true,
            source: clicksync_core::protocol::CaptureSource::Replay {
                records_per_device: 10,
                reports_per_second: 1000,
                real_time: true,
            },
        };
        assert!(runtime.command(start.clone(), old).unwrap().error.is_some());
        assert_eq!(shared.generation.load(Ordering::Acquire), 0);
        let ticket = shared.permit.ticket();
        assert!(runtime.command(start, ticket).unwrap().error.is_none());
        shared.permit.set(false);
        shared.permit.set(true);
        assert!(!shared.is_active());
        runtime.command(ControlCommand::Pause, ticket).unwrap();
    }

    #[test]
    fn wake_keeps_a_notification_between_empty_check_and_park() {
        let wake = Arc::new(crate::CaptureWake::default());
        wake.arm();
        let producer = wake.clone();
        std::thread::spawn(move || producer.notify())
            .join()
            .unwrap();
        let started = std::time::Instant::now();
        wake.wait(std::time::Duration::from_secs(2));
        assert!(started.elapsed() < std::time::Duration::from_secs(1));
    }
}
