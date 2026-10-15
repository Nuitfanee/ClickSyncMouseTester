use super::*;
use clicksync_capture::runtime::{CaptureReader, CaptureRuntime};
use clicksync_core::protocol::{ControlInputBatch, MAX_BATCH_RECORDS, MAX_INPUT_BATCH_EVENTS};

impl Backend {
    /// Raw Input owns a dedicated thread in this process, alongside the foreground GPUI window.
    pub fn spawn_in_process(replay: bool) -> Result<Self, String> {
        let (mut runtime, reader) = CaptureRuntime::spawn(replay).map_err(|e| e.to_string())?;
        let shared = runtime.shared();
        let discover = runtime
            .command(ControlCommand::Discover, 0)
            .map_err(|e| e.to_string())?;
        if let Some(error) = discover.error {
            return Err(error);
        }
        validate_devices(&discover.devices)?;
        let state = Arc::new(Mutex::new(AppStatus {
            devices: discover.devices,
            complete: true,
            ..AppStatus::default()
        }));
        let latest_snapshot = Arc::new(Mutex::new(None));
        let latest_prefix = Arc::new(Mutex::new(None));
        let cancelled = Arc::new(AtomicBool::new(false));
        let ingestion_delay_ms = Arc::new(AtomicU64::new(0));
        let inputs = Arc::new(Mutex::new(InputPublication::default()));
        let (requests, request_receiver) = mpsc::sync_channel(COMMAND_QUEUE_CAPACITY);
        let (ingestion, owner_receiver) = mpsc::sync_channel(COMMAND_QUEUE_CAPACITY);
        let mut backend = Self {
            byte_budget: SharedByteBudget::new(
                SessionConfig::default().managed_memory_budget_bytes,
            ),
            requests,
            ingestion,
            state: state.clone(),
            latest_snapshot: latest_snapshot.clone(),
            latest_prefix: latest_prefix.clone(),
            cancelled: cancelled.clone(),
            ingestion_delay_ms: ingestion_delay_ms.clone(),
            data: None,
            control: None,
            local_shared: Some(shared),
            child: None,
            threads: Vec::with_capacity(2),
            inputs: inputs.clone(),
        };
        let owner_requests = backend.requests.clone();
        let owner_cancelled = cancelled.clone();
        let owner_state = state.clone();
        let owner_budget = backend.byte_budget.clone();
        backend.threads.push(
            thread::Builder::new()
                .name("clicksync-session-owner".into())
                .spawn(move || {
                    ingest_local(
                        reader,
                        owner_receiver,
                        owner_requests,
                        owner_state,
                        SnapshotPublication {
                            budget: owner_budget,
                            first: latest_snapshot,
                            all: latest_prefix,
                        },
                        owner_cancelled,
                        ingestion_delay_ms,
                        inputs,
                    )
                })
                .map_err(|e| e.to_string())?,
        );
        let driver_ingestion = backend.ingestion.clone();
        backend.threads.push(
            thread::Builder::new()
                .name("clicksync-control-driver".into())
                .spawn(move || {
                    drive_controls(
                        ControlTransport::Local(Box::new(runtime)),
                        request_receiver,
                        driver_ingestion,
                        state,
                        cancelled,
                    )
                })
                .map_err(|e| e.to_string())?,
        );
        Ok(backend)
    }

    pub fn set_capture_permitted(&self, allowed: bool) {
        if let Some(shared) = &self.local_shared {
            shared.permit.set(allowed);
            if !allowed {
                shared.motion.revoke_unselected();
            }
            shared.wake.notify();
        }
    }

    pub(crate) fn capture_ticket(&self) -> u64 {
        self.local_shared.as_ref().map_or(0, |s| s.permit.ticket())
    }

    pub(super) fn wake_owner(&self) {
        if let Some(shared) = &self.local_shared {
            shared.wake.notify();
        }
    }

    pub(super) fn drain_local_shutdown(&self) -> Result<(), String> {
        self.drain_local_shutdown_with_timeout(Duration::from_secs(6))
    }

    pub(super) fn drain_local_shutdown_with_timeout(
        &self,
        timeout: Duration,
    ) -> Result<(), String> {
        self.set_capture_permitted(false);
        // Keep the source/driver alive until the owner has committed and sealed the prefix.
        let ack = self.execute(ControlCommand::Stop, timeout)?;
        let deadline = Instant::now() + timeout;
        loop {
            let remaining = deadline
                .checked_duration_since(Instant::now())
                .ok_or("in-process capture did not drain before shutdown")?;
            let (reply, receiver) = mpsc::sync_channel(1);
            self.ingestion
                .try_send(IngestMessage::Drained {
                    generation: ack.generation,
                    queued: ack.queued,
                    reply,
                })
                .map_err(|error| error.to_string())?;
            self.wake_owner();
            if receiver
                .recv_timeout(remaining)
                .map_err(|error| error.to_string())?
            {
                self.execute(ControlCommand::Shutdown, timeout)?;
                return Ok(());
            }
            thread::sleep(Duration::from_millis(1));
        }
    }
}

const OWNER_RETRY_DELAY: Duration = Duration::from_millis(10);

pub(super) fn append_pending_batch(
    owner: &mut SessionOwner,
    batch: &mut Vec<CapturedRecord>,
    state: &Arc<Mutex<AppStatus>>,
    snapshot: &Arc<Mutex<Option<Arc<SessionSnapshot>>>>,
    delay: &AtomicU64,
) -> Result<(), String> {
    let Some(generation) = batch.first().map(|record| record.generation) else {
        return Ok(());
    };
    let before = owner.received_sequence;
    let result = owner.append_records(generation, batch, state, snapshot, delay);
    if result.is_ok() {
        batch.clear();
    } else {
        // Only the newly committed prefix may be removed, never the failing record.
        let committed = (owner.received_sequence - before) as usize;
        drop(batch.drain(..committed));
    }
    result
}

pub(super) fn request_fault_stop(
    generation: u64,
    submitted: &mut Option<u64>,
    requests: &mpsc::SyncSender<DriverRequest>,
) {
    if *submitted != Some(generation)
        && requests
            .try_send(DriverRequest::FaultStop { generation })
            .is_ok()
    {
        *submitted = Some(generation);
    }
}

#[allow(clippy::too_many_arguments)]
pub(super) fn ingest_local(
    mut reader: CaptureReader,
    controls: mpsc::Receiver<IngestMessage>,
    requests: mpsc::SyncSender<DriverRequest>,
    state: Arc<Mutex<AppStatus>>,
    publication: SnapshotPublication,
    cancelled: Arc<AtomicBool>,
    delay: Arc<AtomicU64>,
    inputs: Arc<Mutex<InputPublication>>,
) {
    let budget = publication.budget.clone();
    // Retain the established conservative capture/metadata budget.
    let _reservation = match budget.try_reserve(TRANSPORT_MANAGED_RESERVATION_BYTES) {
        Ok(value) => value,
        Err(error) => {
            set_fault(&state, error.to_string());
            return;
        }
    };
    let mut owner = SessionOwner {
        sessions: Vec::with_capacity(2),
        budget,
        generation: 0,
        prepared: false,
        deferred: Vec::new(),
        received_sequence: 0,
        received_records: 0,
        capture_evidence: CaptureEvidence::complete(),
        seal: None,
        retired_snapshots: Vec::with_capacity(MAX_RETIRED_SNAPSHOTS),
        limit_pause_requested: false,
    };
    let shared = reader.shared();
    let mut batch = Vec::with_capacity(MAX_BATCH_RECORDS);
    let mut input_batch = ControlInputBatch {
        events: Vec::with_capacity(MAX_INPUT_BATCH_EVENTS),
        ..Default::default()
    };
    let mut pending = None;
    let mut preparation = None;
    let mut fault_stop_generation = None;
    let mut retry_at: Option<Instant> = None;
    let mut last_prefix = Instant::now();
    let mut armed_motion: Option<PendingMotion> = None;
    let mut motion_seal: Option<CaptureAck> = None;
    let mut selected_motion: Option<u64> = None;
    while !cancelled.load(Ordering::Acquire) {
        // Reader releases must be able to unblock an append that previously failed.
        owner
            .retired_snapshots
            .retain(|snapshot| Arc::strong_count(snapshot) > 1);
        let mut worked = false;
        let result = (|| -> Result<(), String> {
            let control = pending.take().or_else(|| controls.try_recv().ok());
            if let Some(control) = control {
                match control {
                    #[cfg(test)]
                    IngestMessage::Inspect(inspect) => inspect(&mut owner, &mut batch),
                    IngestMessage::MotionArmed { id, ack } => {
                        selected_motion = None;
                        armed_motion = Some(PendingMotion {
                            id,
                            ack,
                            first_error: None,
                        });
                    }
                    IngestMessage::Ack(mut ack)
                        if matches!(
                            ack.command,
                            CommandTag::Pause | CommandTag::Stop | CommandTag::Shutdown
                        ) && armed_motion
                            .as_ref()
                            .is_some_and(|motion| motion.ack.generation == ack.generation) =>
                    {
                        // Keep one actual source watermark, retaining the strongest seal
                        // and incomplete evidence while its Start is still retrying.
                        if let Some(previous) = motion_seal.take() {
                            if previous.command == CommandTag::Shutdown
                                || (previous.command == CommandTag::Stop
                                    && ack.command == CommandTag::Pause)
                            {
                                ack.command = previous.command;
                            }
                            ack.complete &= previous.complete;
                            ack.overflow = ack.overflow.max(previous.overflow);
                        }
                        motion_seal = Some(ack);
                        // A selected first report is retained by the mailbox; an
                        // unselected observation must not hold sealing open forever.
                        shared.motion.revoke_unselected();
                    }
                    IngestMessage::Prepare(reply)
                        if owner.seal.is_some()
                            || !batch.is_empty()
                            || armed_motion.is_some()
                            || motion_seal.is_some() =>
                    {
                        preparation = Some(reply);
                    }
                    IngestMessage::Rejected(error) => {
                        // The serial driver queues cancellation before any next Prepare.
                        preparation = None;
                        owner.handle(
                            IngestMessage::Rejected(error),
                            &state,
                            &publication.first,
                            &delay,
                        )?;
                    }
                    IngestMessage::Reset(ack) => {
                        preparation = None;
                        armed_motion = None;
                        motion_seal = None;
                        selected_motion = None;
                        retry_at = None;
                        owner.handle(
                            IngestMessage::Reset(ack),
                            &state,
                            &publication.first,
                            &delay,
                        )?;
                    }
                    other => owner.handle(other, &state, &publication.first, &delay)?,
                }
                worked = true;
            }
            if retry_at.is_some_and(|deadline| Instant::now() < deadline) {
                return Ok(());
            }
            retry_at = None;
            settle_motion_owner(
                &mut armed_motion,
                &mut selected_motion,
                &mut owner,
                &shared,
                &state,
                &publication.first,
            )?;
            if armed_motion.is_none()
                && let Some(ack) = motion_seal.take()
            {
                if matches!(ack.command, CommandTag::Stop | CommandTag::Shutdown) {
                    // This source already acknowledged Stop; restoring its fault is not a new Stop.
                    fault_stop_generation = Some(ack.generation);
                }
                owner.handle_ack(ack, &state, &publication.first)?;
            }
            if state.lock().expect("application status lock").phase == CapturePhase::Faulted {
                request_fault_stop(owner.generation, &mut fault_stop_generation, &requests);
            }
            // A Start ACK must initialize the session before its first record is consumed.
            if !owner.prepared && armed_motion.is_none() {
                if batch.is_empty() {
                    reader.drain(&mut batch);
                }
                if !batch.is_empty() {
                    append_pending_batch(
                        &mut owner,
                        &mut batch,
                        &state,
                        &publication.first,
                        &delay,
                    )?;
                    worked = true;
                }
                // Sealing can fail after the last record commits; no new record is
                // required to retry its snapshot publication once budget is available.
                owner.try_seal(&state, &publication.first)?;
                if (owner.received_sequence > 0
                    || (owner.seal.is_none()
                        && state.lock().expect("application status lock").phase
                            != CapturePhase::Collecting))
                    && let Some(id) = selected_motion.take()
                {
                    shared.motion.acknowledge_owner(id);
                }
            }
            if owner.seal.is_none()
                && batch.is_empty()
                && armed_motion.is_none()
                && motion_seal.is_none()
                && let Some(reply) = preparation.take()
            {
                owner.handle(
                    IngestMessage::Prepare(reply),
                    &state,
                    &publication.first,
                    &delay,
                )?;
                worked = true;
            }
            let can_pause = {
                let status = state.lock().expect("application status lock");
                status.phase == CapturePhase::Collecting && !status.command_pending
            };
            if can_pause
                && owner.all_at_limit()
                && !owner.limit_pause_requested
                && requests
                    .try_send(DriverRequest::Command {
                        command: ControlCommand::Pause,
                        permit: 0,
                        reply: None,
                    })
                    .is_ok()
            {
                owner.limit_pause_requested = true;
            }
            Ok(())
        })();
        if let Err(error) = result {
            owner.fault(&state, error);
            request_fault_stop(owner.generation, &mut fault_stop_generation, &requests);
            retry_at = Some(Instant::now() + OWNER_RETRY_DELAY);
            worked = false;
        }
        // Keep control input and failure observability independent of append/seal success.
        let capture = reader.status();
        if capture.generation == owner.generation && (capture.overflow > 0 || capture.disconnected)
        {
            request_fault_stop(capture.generation, &mut fault_stop_generation, &requests);
        }
        if let Err(error) = owner.handle(
            IngestMessage::Status(capture),
            &state,
            &publication.first,
            &delay,
        ) {
            owner.fault(&state, error);
        }
        reader.drain_inputs(&mut input_batch);
        worked |= !input_batch.events.is_empty();
        if let Err(error) = publish_input_batch(&inputs, &input_batch) {
            owner.fault(&state, error);
            request_fault_stop(owner.generation, &mut fault_stop_generation, &requests);
            retry_at = Some(Instant::now() + OWNER_RETRY_DELAY);
        }
        owner.publish_summary(&state);
        owner.sync_capture_evidence();
        owner.revoke_stale_publications(&state, &publication);
        let publish_due = retry_at.is_none_or(|deadline| Instant::now() >= deadline);
        if publish_due
            && let Err(error) =
                owner.publish_latest_prefix(&state, &publication.all, &mut last_prefix)
        {
            owner.fault(&state, error);
            owner.revoke_stale_publications(&state, &publication);
            request_fault_stop(owner.generation, &mut fault_stop_generation, &requests);
            retry_at = Some(Instant::now() + OWNER_RETRY_DELAY);
            worked = false;
        }
        if !worked {
            // Arm before rechecking both sources; an intervening publication leaves an unpark token.
            shared.wake.arm();
            pending = controls.try_recv().ok();
            let retry_wait =
                retry_at.and_then(|deadline| deadline.checked_duration_since(Instant::now()));
            if pending.is_none()
                && let Some(wait) = retry_wait
            {
                shared.wake.wait(wait);
            } else if pending.is_none()
                && (owner.prepared || (batch.is_empty() && !reader.has_data()))
            {
                shared.wake.wait(OWNER_RETRY_DELAY);
            } else {
                shared.wake.disarm();
            }
        }
    }
    shared.wake.disarm();
    if !owner.sessions.is_empty() {
        let _ = owner.publish_snapshot(&publication.first);
        let _ = owner.publish_latest_prefix(&state, &publication.all, &mut last_prefix);
    }
}

struct PendingMotion {
    id: u64,
    ack: CaptureAck,
    first_error: Option<String>,
}

fn settle_motion_owner(
    armed: &mut Option<PendingMotion>,
    selected: &mut Option<u64>,
    owner: &mut SessionOwner,
    shared: &clicksync_capture::CaptureShared,
    state: &Arc<Mutex<AppStatus>>,
    snapshot: &Arc<Mutex<Option<Arc<SessionSnapshot>>>>,
) -> Result<(), String> {
    use clicksync_capture::observation::MotionResult;
    let Some(motion) = armed.as_mut() else {
        return Ok(());
    };
    let mut settled = false;
    let result: Result<(), String> = (|| {
        if owner.generation != motion.ack.generation {
            // Keep the ACK in `armed` until initialization succeeds; controls still run
            // between attempts when the retired-reader limit or byte budget blocks it.
            owner.retire_latest(snapshot)?;
            owner.sessions.clear();
            owner.generation = motion.ack.generation;
            owner.received_sequence = 0;
            owner.received_records = 0;
            owner.capture_evidence = CaptureEvidence::complete();
            owner.seal = None;
            owner.limit_pause_requested = false;
            let mut status = state.lock().expect("application status lock");
            status.generation = motion.ack.generation;
            status.selected_device_keys.clear();
            status.phase = CapturePhase::Empty;
            status.complete = true;
            status.error = None;
            status.capture_fault = None;
        }
        match shared.motion.peek(motion.id) {
            Some(MotionResult::Selected(candidate)) => {
                let mut ack = motion.ack.clone();
                ack.ticks = candidate.ticks;
                state
                    .lock()
                    .expect("application status lock")
                    .selected_device_keys = vec![candidate.device_key];
                owner.handle_ack(ack, state, snapshot)?;
                settled = true;
                *selected = Some(motion.id);
            }
            Some(_) => {
                owner.prepared = false;
                settled = true;
            }
            None if !shared.motion.is_current(motion.id) => {
                owner.prepared = false;
                settled = true;
            }
            None => {}
        }
        Ok(())
    })();
    if let Err(error) = &result
        && motion.first_error.is_none()
    {
        motion.first_error = Some(error.clone());
    }
    // Restore only this motion's failure after either initialization reset, before
    // any append/seal/publication; never copy a prior generation's error or evidence.
    if owner.generation == motion.ack.generation
        && let Some(error) = &motion.first_error
        && (owner.capture_evidence.integrity != CaptureIntegrity::Incomplete
            || state.lock().expect("application status lock").phase != CapturePhase::Faulted)
    {
        owner.fault(state, error.clone());
    }
    if settled {
        *armed = None;
    }
    result
}
