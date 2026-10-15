pub mod diagnostics;
mod local;
#[cfg(test)]
#[path = "backend/local_tests.rs"]
mod local_tests;
mod motion;
use motion::drive_motion;

#[cfg(feature = "ui-restoration-probe")]
use clicksync_core::protocol::CAPTURE_QUEUE_RECORDS;
use clicksync_core::protocol::{
    CAPTURE_METADATA_RESERVATION_BYTES, CaptureAck, CaptureStatus, CapturedRecord, CommandTag,
    ControlCommand, DeviceDescriptor, FRAME_HEADER_BYTES, FrameHeader, FrameKind, MAX_DEVICE_COUNT,
    MAX_FRAME_PAYLOAD_BYTES, WIRE_RECORD_BYTES,
};
use clicksync_core::session::{CaptureEvidence, CaptureIntegrity};
use clicksync_core::{AppendOutcome, Session, SessionConfig, SessionSnapshot, SharedByteBudget};
use clicksync_ipc::Pipe;
use serde::Serialize;
use std::io;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub const TRANSPORT_MANAGED_RESERVATION_BYTES: usize =
    4 * 1024 * 1024 + CAPTURE_METADATA_RESERVATION_BYTES;
const IO_TIMEOUT: Duration = Duration::from_secs(3);
const COMMAND_QUEUE_CAPACITY: usize = 8;
const INGESTION_QUEUE_CAPACITY: usize = 16;
const MAX_RETIRED_SNAPSHOTS: usize = 8;
static PIPE_ID: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize)]
pub enum CapturePhase {
    #[default]
    Empty,
    Collecting,
    Paused,
    Stopped,
    Draining,
    Faulted,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub enum CaptureFault {
    DeviceDisconnected,
    QueueOverflow,
    Incomplete,
    Other,
}

#[derive(Clone, Debug, Default, Serialize)]
pub struct AppStatus {
    pub phase: CapturePhase,
    pub generation: u64,
    pub devices: Vec<DeviceDescriptor>,
    pub selected_device_keys: Vec<u32>,
    pub received_records: u64,
    pub received_sequence: u64,
    pub dataset_records: Vec<usize>,
    pub retention_warning: bool,
    pub retention_limit_reached: bool,
    pub ignored_after_limit: u64,
    pub captured: u64,
    pub sent: u64,
    pub overflow: u64,
    pub queue_high_watermark: usize,
    pub managed_reserved_bytes: usize,
    pub complete: bool,
    pub ignored_old_batches: u64,
    pub command_pending: bool,
    pub capture_fault: Option<CaptureFault>,
    pub error: Option<String>,
}

/// Every dataset is frozen by the session owner at one receive/commit position.
#[derive(Clone)]
pub struct SessionPrefix {
    pub generation: u64,
    pub received_sequence: u64,
    pub received_records: u64,
    pub phase: CapturePhase,
    /// Integrity of accepted data, including during collection; not finalization.
    pub complete: bool,
    pub datasets: Vec<(u32, Arc<SessionSnapshot>)>,
}

/// Read-only access to the owner's latest immutable commit. A consumer never
/// drains capture queues or writes the session through this handle.
#[derive(Clone)]
pub(crate) struct PrefixReader {
    prefix: Arc<Mutex<Option<Arc<SessionPrefix>>>>,
    capture: Option<Arc<clicksync_capture::CaptureShared>>,
}

impl PrefixReader {
    pub(crate) fn same_source(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.prefix, &other.prefix)
    }

    pub(crate) fn latest(&self) -> Option<Arc<SessionPrefix>> {
        self.prefix.lock().expect("prefix publication lock").clone()
    }

    pub(crate) fn checkpoint(&self) -> Option<clicksync_capture::watermark::Checkpoint> {
        let capture = self.capture.as_ref()?;
        let checkpoint = capture.watermark.latest();
        clicksync_capture::platform::request_watermark(capture);
        checkpoint
    }

    #[cfg(test)]
    pub(crate) fn controlled(prefix: Arc<SessionPrefix>) -> Self {
        Self {
            prefix: Arc::new(Mutex::new(Some(prefix))),
            capture: None,
        }
    }
}

struct SnapshotPublication {
    budget: Arc<SharedByteBudget>,
    first: Arc<Mutex<Option<Arc<SessionSnapshot>>>>,
    all: Arc<Mutex<Option<Arc<SessionPrefix>>>>,
}

enum DriverRequest {
    FaultStop {
        generation: u64,
    },
    Motion {
        request: clicksync_capture::observation::MotionRequest,
        permit: u64,
        reply: mpsc::SyncSender<Result<CaptureAck, String>>,
    },
    #[cfg(feature = "ui-restoration-probe")]
    ReplayMotion {
        reports: Vec<clicksync_capture::observation::MotionReport>,
        reply: mpsc::SyncSender<Result<(), String>>,
    },
    Command {
        command: ControlCommand,
        permit: u64,
        reply: Option<mpsc::SyncSender<Result<CaptureAck, String>>>,
    },
}

#[cfg(test)]
type OwnerInspection = Box<dyn FnOnce(&mut SessionOwner, &mut Vec<CapturedRecord>) + Send>;

enum IngestMessage {
    MotionArmed {
        id: u64,
        ack: CaptureAck,
    },
    Prepare(mpsc::SyncSender<()>),
    Ack(CaptureAck),
    Reset(CaptureAck),
    Batch {
        generation: u64,
        records: Vec<CapturedRecord>,
    },
    Status(CaptureStatus),
    Snapshot(mpsc::SyncSender<Result<Arc<SessionSnapshot>, String>>),
    Prefix(mpsc::SyncSender<Result<Arc<SessionPrefix>, String>>),
    Drained {
        generation: u64,
        queued: u64,
        reply: mpsc::SyncSender<bool>,
    },
    #[cfg(test)]
    Inspect(OwnerInspection),
    Rejected(String),
    Error(String),
}

#[derive(Default)]
struct InputPublication {
    frequency: u64,
    source_dropped: u64,
    publication_dropped: u64,
    events: Vec<clicksync_core::protocol::ControlInputEvent>,
}

pub struct Backend {
    byte_budget: Arc<SharedByteBudget>,
    requests: mpsc::SyncSender<DriverRequest>,
    ingestion: mpsc::SyncSender<IngestMessage>,
    state: Arc<Mutex<AppStatus>>,
    latest_snapshot: Arc<Mutex<Option<Arc<SessionSnapshot>>>>,
    latest_prefix: Arc<Mutex<Option<Arc<SessionPrefix>>>>,
    cancelled: Arc<AtomicBool>,
    ingestion_delay_ms: Arc<AtomicU64>,
    data: Option<Pipe>,
    control: Option<Pipe>,
    local_shared: Option<Arc<clicksync_capture::CaptureShared>>,
    child: Option<Child>,
    threads: Vec<JoinHandle<()>>,
    inputs: Arc<Mutex<InputPublication>>,
}

impl Backend {
    pub fn spawn(worker: &Path, replay: bool) -> Result<Self, String> {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|error| error.to_string())?
            .as_nanos();
        let id = PIPE_ID.fetch_add(1, Ordering::Relaxed);
        let prefix = format!(r"\\.\pipe\ClickSync-{}-{nonce}-{id}", std::process::id());
        let data_name = format!("{prefix}-data");
        let control_name = format!("{prefix}-control");
        let data = Pipe::server(&data_name).map_err(|error| error.to_string())?;
        let control = Pipe::server(&control_name).map_err(|error| error.to_string())?;
        let cancelled = Arc::new(AtomicBool::new(false));
        let mut command = Command::new(worker);
        command
            .arg("--data")
            .arg(&data_name)
            .arg("--control")
            .arg(&control_name)
            .arg("--parent")
            .arg(std::process::id().to_string())
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::inherit());
        if replay {
            command.arg("--replay");
        }
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x0800_0000);
        }
        let mut child = command.spawn().map_err(|error| error.to_string())?;
        let connection = (|| -> io::Result<()> {
            let deadline = Instant::now() + Duration::from_secs(8);
            data.connect(deadline, &cancelled)?;
            control.connect(deadline, &cancelled)?;
            if data.peer_process_id()? != child.id() || control.peer_process_id()? != child.id() {
                return Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    "capture pipe peer is not the launched worker",
                ));
            }
            Ok(())
        })();
        if let Err(error) = connection {
            let _ = child.kill();
            let _ = child.wait();
            return Err(error.to_string());
        }
        let discover = exchange_command(&control, &ControlCommand::Discover, &cancelled)
            .inspect_err(|_| {
                let _ = child.kill();
                let _ = child.wait();
            })?;
        if let Some(error) = discover.error {
            let _ = child.kill();
            let _ = child.wait();
            return Err(error);
        }
        if let Err(error) = validate_devices(&discover.devices) {
            let _ = child.kill();
            let _ = child.wait();
            return Err(error);
        }
        let state = Arc::new(Mutex::new(AppStatus {
            devices: discover.devices,
            complete: true,
            ..AppStatus::default()
        }));
        let latest_snapshot = Arc::new(Mutex::new(None));
        let latest_prefix = Arc::new(Mutex::new(None));
        let ingestion_delay_ms = Arc::new(AtomicU64::new(0));
        let (request_sender, request_receiver) = mpsc::sync_channel(COMMAND_QUEUE_CAPACITY);
        let (ingest_sender, ingest_receiver) = mpsc::sync_channel(INGESTION_QUEUE_CAPACITY);
        let (owner_sender, owner_receiver) = mpsc::sync_channel(COMMAND_QUEUE_CAPACITY);
        // Own partial startup before creating threads so every failure follows the shutdown path.
        let mut backend = Self {
            byte_budget: SharedByteBudget::new(
                SessionConfig::default().managed_memory_budget_bytes,
            ),
            requests: request_sender,
            ingestion: owner_sender.clone(),
            state: Arc::clone(&state),
            latest_snapshot: Arc::clone(&latest_snapshot),
            latest_prefix: Arc::clone(&latest_prefix),
            cancelled: Arc::clone(&cancelled),
            ingestion_delay_ms: Arc::clone(&ingestion_delay_ms),
            data: Some(data.clone()),
            control: Some(control.clone()),
            local_shared: None,
            child: Some(child),
            threads: Vec::with_capacity(3),
            inputs: Arc::new(Mutex::new(InputPublication::default())),
        };

        let receiver_pipe = data.clone();
        let receiver_cancelled = Arc::clone(&cancelled);
        let receiver_sender = ingest_sender.clone();
        let receiver_inputs = Arc::clone(&backend.inputs);
        backend.threads.push(
            thread::Builder::new()
                .name("clicksync-pipe-receiver".into())
                .spawn(move || {
                    receive_data(
                        receiver_pipe,
                        receiver_sender,
                        receiver_cancelled,
                        receiver_inputs,
                    )
                })
                .map_err(|error| error.to_string())?,
        );
        let owner_state = Arc::clone(&state);
        let owner_snapshot = Arc::clone(&latest_snapshot);
        let owner_prefix = Arc::clone(&latest_prefix);
        let owner_cancelled = Arc::clone(&cancelled);
        let owner_delay = Arc::clone(&ingestion_delay_ms);
        let owner_requests = backend.requests.clone();
        let owner_budget = backend.byte_budget.clone();
        backend.threads.push(
            thread::Builder::new()
                .name("clicksync-session-owner".into())
                .spawn(move || {
                    ingest_loop(
                        ingest_receiver,
                        owner_receiver,
                        owner_requests,
                        owner_state,
                        SnapshotPublication {
                            budget: owner_budget,
                            first: owner_snapshot,
                            all: owner_prefix,
                        },
                        owner_cancelled,
                        owner_delay,
                    )
                })
                .map_err(|error| error.to_string())?,
        );
        let driver_pipe = control.clone();
        let driver_cancelled = Arc::clone(&cancelled);
        let driver_state = Arc::clone(&state);
        let driver_ingestion = owner_sender;
        backend.threads.push(
            thread::Builder::new()
                .name("clicksync-control-driver".into())
                .spawn(move || {
                    drive_controls(
                        ControlTransport::Pipe(driver_pipe),
                        request_receiver,
                        driver_ingestion,
                        driver_state,
                        driver_cancelled,
                    )
                })
                .map_err(|error| error.to_string())?,
        );
        Ok(backend)
    }

    pub fn default_worker_path() -> Result<PathBuf, String> {
        let executable = std::env::current_exe().map_err(|error| error.to_string())?;
        let directory = executable
            .parent()
            .ok_or("application executable has no parent directory")?;
        let direct = directory.join("clicksync-capture.exe");
        if direct.is_file() {
            return Ok(direct);
        }
        let parent = directory
            .parent()
            .ok_or("capture worker was not found beside the application")?;
        let candidate = parent.join("clicksync-capture.exe");
        if candidate.is_file() {
            Ok(candidate)
        } else {
            Err("capture worker was not found beside the application".into())
        }
    }

    pub fn state(&self) -> AppStatus {
        self.state.lock().expect("application status lock").clone()
    }
    pub fn byte_budget(&self) -> Arc<SharedByteBudget> {
        self.byte_budget.clone()
    }
    pub fn capture_process_id(&self) -> Option<u32> {
        if self.local_shared.is_some() {
            Some(std::process::id())
        } else {
            self.child.as_ref().map(Child::id)
        }
    }
    pub fn take_inputs(&self) -> clicksync_core::protocol::ControlInputBatch {
        let mut input = self.inputs.lock().expect("input publication");
        clicksync_core::protocol::ControlInputBatch {
            frequency: input.frequency,
            dropped: input
                .source_dropped
                .saturating_add(input.publication_dropped),
            events: std::mem::take(&mut input.events),
        }
    }

    pub fn enqueue(&self, command: ControlCommand) -> Result<(), String> {
        self.requests
            .try_send(DriverRequest::Command {
                permit: self.capture_ticket(),
                command,
                reply: None,
            })
            .map_err(|error| format!("control queue unavailable: {error}"))
    }
    pub fn enqueue_tracked(
        &self,
        command: ControlCommand,
    ) -> Result<mpsc::Receiver<Result<CaptureAck, String>>, String> {
        let (sender, receiver) = mpsc::sync_channel(1);
        self.requests
            .try_send(DriverRequest::Command {
                permit: self.capture_ticket(),
                command,
                reply: Some(sender),
            })
            .map_err(|error| format!("control queue unavailable: {error}"))?;
        Ok(receiver)
    }

    pub fn execute(
        &self,
        command: ControlCommand,
        timeout: Duration,
    ) -> Result<CaptureAck, String> {
        let reset = matches!(command, ControlCommand::Reset { .. });
        let (sender, receiver) = mpsc::sync_channel(1);
        self.requests
            .try_send(DriverRequest::Command {
                permit: self.capture_ticket(),
                command,
                reply: Some(sender),
            })
            .map_err(|error| error.to_string())?;
        let ack = receiver
            .recv_timeout(timeout)
            .map_err(|error| error.to_string())??;
        if reset {
            let deadline = Instant::now() + timeout;
            loop {
                let status = self.state();
                if status.generation == ack.generation
                    && status.phase == CapturePhase::Empty
                    && !status.command_pending
                {
                    break;
                }
                if Instant::now() >= deadline {
                    return Err("capture owner did not apply reset before the deadline".into());
                }
                thread::sleep(Duration::from_millis(1));
            }
        }
        Ok(ack)
    }

    pub fn capture_snapshot(&self, timeout: Duration) -> Result<Arc<SessionSnapshot>, String> {
        let (sender, receiver) = mpsc::sync_channel(1);
        self.ingestion
            .try_send(IngestMessage::Snapshot(sender))
            .map_err(|error| error.to_string())?;
        receiver
            .recv_timeout(timeout)
            .map_err(|error| error.to_string())?
    }

    pub fn latest_snapshot(&self) -> Option<Arc<SessionSnapshot>> {
        self.latest_snapshot
            .lock()
            .expect("snapshot publication lock")
            .as_ref()
            .map(Arc::clone)
    }

    pub fn capture_prefix(&self, timeout: Duration) -> Result<Arc<SessionPrefix>, String> {
        let (sender, receiver) = mpsc::sync_channel(1);
        self.ingestion
            .try_send(IngestMessage::Prefix(sender))
            .map_err(|error| error.to_string())?;
        receiver
            .recv_timeout(timeout)
            .map_err(|error| error.to_string())?
    }

    pub fn latest_prefix(&self) -> Option<Arc<SessionPrefix>> {
        self.latest_prefix
            .lock()
            .expect("prefix publication lock")
            .clone()
    }

    pub(crate) fn prefix_reader(&self) -> PrefixReader {
        PrefixReader {
            prefix: self.latest_prefix.clone(),
            capture: self.local_shared.clone(),
        }
    }

    pub fn set_ingestion_delay(&self, delay: Duration) {
        self.ingestion_delay_ms.store(
            u64::try_from(delay.as_millis()).unwrap_or(u64::MAX),
            Ordering::Release,
        );
    }

    pub fn terminate_worker(&mut self) -> Result<(), String> {
        if let Some(child) = &mut self.child {
            child.kill().map_err(|error| error.to_string())?;
        }
        Ok(())
    }

    pub fn shutdown(&mut self) -> Result<(), String> {
        if self.threads.is_empty() && self.child.is_none() {
            return Ok(());
        }
        if self.local_shared.is_some()
            && !self.cancelled.load(Ordering::Acquire)
            && let Err(error) = self.drain_local_shutdown()
        {
            set_fault(&self.state, error.clone());
            return Err(error);
        }
        self.cancel_and_join()
    }

    fn cancel_and_join(&mut self) -> Result<(), String> {
        self.cancelled.store(true, Ordering::Release);
        if let Some(data) = &self.data {
            let _ = data.cancel();
        }
        if let Some(control) = &self.control {
            let _ = control.cancel();
        }
        self.wake_owner();
        if let Some(child) = &mut self.child {
            if child
                .try_wait()
                .map_err(|error| error.to_string())?
                .is_none()
            {
                child.kill().map_err(|error| error.to_string())?;
            }
            let deadline = Instant::now() + Duration::from_secs(3);
            loop {
                if child
                    .try_wait()
                    .map_err(|error| error.to_string())?
                    .is_some()
                {
                    break;
                }
                if Instant::now() >= deadline {
                    return Err(
                        "capture worker did not terminate within the shutdown deadline".into(),
                    );
                }
                thread::sleep(Duration::from_millis(10));
            }
        }
        self.child = None;
        for handle in self.threads.drain(..) {
            handle
                .join()
                .map_err(|_| "application background thread panicked".to_string())?;
        }
        Ok(())
    }
}

impl Drop for Backend {
    fn drop(&mut self) {
        if let Err(error) = self.shutdown() {
            eprintln!("capture shutdown failed: {error}");
            if let Err(error) = self.cancel_and_join() {
                eprintln!("forced capture cleanup failed: {error}");
            }
        }
    }
}

fn exchange_command(
    pipe: &Pipe,
    command: &ControlCommand,
    cancelled: &AtomicBool,
) -> Result<CaptureAck, String> {
    let bytes = serde_json::to_vec(command).map_err(|error| error.to_string())?;
    let header = FrameHeader {
        kind: FrameKind::Command,
        generation: command.generation(),
        payload_bytes: bytes.len(),
        record_count: 0,
    }
    .encode()
    .map_err(|error| error.to_string())?;
    let deadline = Instant::now() + IO_TIMEOUT;
    pipe.write_all(&header, deadline, cancelled)
        .map_err(|error| error.to_string())?;
    pipe.write_all(&bytes, deadline, cancelled)
        .map_err(|error| error.to_string())?;
    let mut header = [0; FRAME_HEADER_BYTES];
    pipe.read_exact(&mut header, deadline, cancelled)
        .map_err(|error| error.to_string())?;
    let header = FrameHeader::decode(&header).map_err(|error| error.to_string())?;
    if header.kind != FrameKind::Ack {
        return Err("capture control response is not an acknowledgment".into());
    }
    let mut payload = vec![0; header.payload_bytes];
    pipe.read_exact(&mut payload, deadline, cancelled)
        .map_err(|error| error.to_string())?;
    let ack: CaptureAck = serde_json::from_slice(&payload).map_err(|error| error.to_string())?;
    if ack.command != command.tag() || ack.generation != header.generation {
        return Err("capture acknowledgment command or generation mismatch".into());
    }
    if ack.timestamp_frequency == 0 {
        return Err("capture acknowledgment has no clock frequency".into());
    }
    validate_devices(&ack.devices)?;
    Ok(ack)
}

fn validate_devices(devices: &[DeviceDescriptor]) -> Result<(), String> {
    if devices.len() > MAX_DEVICE_COUNT {
        return Err("capture device catalog exceeds the configured limit".into());
    }
    for (index, device) in devices.iter().enumerate() {
        if device.key == 0
            || !device.fields_are_bounded()
            || devices[..index]
                .iter()
                .any(|previous| previous.key == device.key)
        {
            return Err("invalid capture device catalog".into());
        }
    }
    Ok(())
}

enum ControlTransport {
    Pipe(Pipe),
    Local(Box<clicksync_capture::runtime::CaptureRuntime>),
}

impl ControlTransport {
    fn execute(
        &mut self,
        command: &ControlCommand,
        permit: u64,
        cancelled: &AtomicBool,
    ) -> Result<CaptureAck, String> {
        match self {
            Self::Pipe(pipe) => exchange_command(pipe, command, cancelled),
            Self::Local(runtime) => runtime
                .command(command.clone(), permit)
                .map_err(|e| e.to_string()),
        }
    }
    fn wake(&self) {
        if let Self::Local(runtime) = self {
            runtime.shared().wake.notify();
        }
    }
}

fn drive_controls(
    mut transport: ControlTransport,
    requests: mpsc::Receiver<DriverRequest>,
    ingestion: mpsc::SyncSender<IngestMessage>,
    state: Arc<Mutex<AppStatus>>,
    cancelled: Arc<AtomicBool>,
) {
    while !cancelled.load(Ordering::Acquire) {
        let request = match requests.recv_timeout(Duration::from_millis(250)) {
            Ok(request) => request,
            Err(mpsc::RecvTimeoutError::Timeout) => DriverRequest::Command {
                permit: 0,
                command: ControlCommand::Discover,
                reply: None,
            },
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        };
        let request = match request {
            DriverRequest::FaultStop { generation } => {
                // Only this serial driver changes the source generation. Motion reserves
                // its generation while arming, before any first report can select it.
                let ControlTransport::Local(runtime) = &transport else {
                    continue;
                };
                if runtime.shared().generation.load(Ordering::Acquire) != generation {
                    continue;
                }
                DriverRequest::Command {
                    command: ControlCommand::Stop,
                    permit: 0,
                    reply: None,
                }
            }
            other => other,
        };
        match request {
            DriverRequest::Motion {
                request,
                permit,
                reply,
            } => {
                let result = drive_motion(&mut transport, request, permit, &ingestion, &state);
                let _ = reply.send(result);
                continue;
            }
            #[cfg(feature = "ui-restoration-probe")]
            DriverRequest::ReplayMotion { reports, reply } => {
                let result = match &mut transport {
                    ControlTransport::Local(runtime) => runtime
                        .replay_motion(reports)
                        .map_err(|error| error.to_string()),
                    ControlTransport::Pipe(_) => {
                        Err("controlled motion requires in-process capture".into())
                    }
                };
                let _ = reply.send(result);
                continue;
            }
            DriverRequest::Command { .. } => {}
            DriverRequest::FaultStop { .. } => unreachable!(),
        }
        let DriverRequest::Command {
            command,
            permit,
            reply,
        } = request
        else {
            unreachable!()
        };
        state
            .lock()
            .expect("application status lock")
            .command_pending = true;
        let needs_prepare = matches!(
            command,
            ControlCommand::Start { .. } | ControlCommand::Resume
        );
        if needs_prepare {
            let (sender, receiver) = mpsc::sync_channel(1);
            let prepared = ingestion.send(IngestMessage::Prepare(sender));
            transport.wake();
            if prepared.is_err() || receiver.recv_timeout(IO_TIMEOUT).is_err() {
                let error = "session owner did not prepare within the command deadline".to_string();
                // Drop the reply endpoint before cancellation. This same serial sender
                // queues Rejected before any later Prepare/ACK, including motion requests.
                drop(receiver);
                let cancelled_prepare = ingestion.send(IngestMessage::Rejected(error.clone()));
                state
                    .lock()
                    .expect("application status lock")
                    .command_pending = false;
                transport.wake();
                if let Some(reply) = reply {
                    let _ = reply.send(Err(error));
                }
                if prepared.is_err() || cancelled_prepare.is_err() {
                    break;
                }
                continue;
            }
            if let ControlCommand::Start { device_keys, .. } = &command {
                state
                    .lock()
                    .expect("application status lock")
                    .selected_device_keys = device_keys.clone();
            }
        }
        let result = transport.execute(&command, permit, &cancelled);
        match &result {
            Ok(ack) if ack.error.is_some() => {
                let _ = ingestion.send(IngestMessage::Rejected(
                    ack.error.clone().expect("capture rejection message"),
                ));
            }
            Ok(ack) if ack.command == CommandTag::Reset => {
                let _ = ingestion.send(IngestMessage::Reset(ack.clone()));
            }
            Ok(ack) => {
                let _ = ingestion.send(IngestMessage::Ack(ack.clone()));
            }
            Err(error) => {
                let _ = ingestion.send(IngestMessage::Error(error.clone()));
            }
        }
        state
            .lock()
            .expect("application status lock")
            .command_pending = false;
        transport.wake();
        if let Some(reply) = reply {
            let result = result.clone().and_then(|ack| {
                if let Some(error) = &ack.error {
                    Err(error.clone())
                } else {
                    Ok(ack)
                }
            });
            let _ = reply.send(result);
        }
        if result.is_err() || command == ControlCommand::Shutdown {
            break;
        }
    }
}

fn receive_data(
    pipe: Pipe,
    ingestion: mpsc::SyncSender<IngestMessage>,
    cancelled: Arc<AtomicBool>,
    inputs: Arc<Mutex<InputPublication>>,
) {
    let mut payload = vec![0; MAX_FRAME_PAYLOAD_BYTES];
    while !cancelled.load(Ordering::Acquire) {
        let result = (|| -> Result<Option<IngestMessage>, String> {
            let deadline = Instant::now() + IO_TIMEOUT;
            let mut bytes = [0; FRAME_HEADER_BYTES];
            pipe.read_exact(&mut bytes, deadline, &cancelled)
                .map_err(|error| error.to_string())?;
            let header = FrameHeader::decode(&bytes).map_err(|error| error.to_string())?;
            pipe.read_exact(&mut payload[..header.payload_bytes], deadline, &cancelled)
                .map_err(|error| error.to_string())?;
            if header.kind == FrameKind::Input {
                let batch: clicksync_core::protocol::ControlInputBatch =
                    serde_json::from_slice(&payload[..header.payload_bytes])
                        .map_err(|e| e.to_string())?;
                publish_inputs(&inputs, batch)?;
                return Ok(None);
            }
            match header.kind {
                FrameKind::Batch => {
                    let records = payload[..header.payload_bytes]
                        .chunks_exact(WIRE_RECORD_BYTES)
                        .map(|bytes| {
                            CapturedRecord::decode(
                                header.generation,
                                bytes.try_into().expect("validated wire record width"),
                            )
                            .map_err(|error| error.to_string())
                        })
                        .collect::<Result<Vec<_>, _>>()?;
                    Ok(Some(IngestMessage::Batch {
                        generation: header.generation,
                        records,
                    }))
                }
                FrameKind::Status => {
                    let status: CaptureStatus =
                        serde_json::from_slice(&payload[..header.payload_bytes])
                            .map_err(|error| error.to_string())?;
                    if status.generation != header.generation {
                        return Err("capture status generation mismatch".into());
                    }
                    Ok(Some(IngestMessage::Status(status)))
                }
                _ => Err("unexpected data pipe frame kind".into()),
            }
        })();
        match result {
            Ok(Some(message)) => {
                if ingestion.send(message).is_err() {
                    break;
                }
            }
            Ok(None) => {}
            Err(error) => {
                if !cancelled.load(Ordering::Acquire) {
                    let _ = ingestion.send(IngestMessage::Error(error));
                }
                break;
            }
        }
    }
}

fn publish_inputs(
    inputs: &Mutex<InputPublication>,
    batch: clicksync_core::protocol::ControlInputBatch,
) -> Result<(), String> {
    publish_input_batch(inputs, &batch)
}

fn publish_input_batch(
    inputs: &Mutex<InputPublication>,
    batch: &clicksync_core::protocol::ControlInputBatch,
) -> Result<(), String> {
    if batch.frequency == 0 || batch.events.len() > clicksync_core::protocol::MAX_INPUT_BATCH_EVENTS
    {
        return Err("invalid control-input batch".into());
    }
    let mut input = inputs.lock().map_err(|_| "input publication poisoned")?;
    input.frequency = batch.frequency;
    input.source_dropped = input.source_dropped.max(batch.dropped);
    let free = clicksync_core::protocol::INPUT_QUEUE_EVENTS.saturating_sub(input.events.len());
    input.publication_dropped = input
        .publication_dropped
        .saturating_add(batch.events.len().saturating_sub(free) as u64);
    input.events.extend(batch.events.iter().copied().take(free));
    Ok(())
}

#[cfg(test)]
mod control_input_tests {
    use super::*;
    use clicksync_core::protocol::{
        ControlInput, ControlInputBatch, ControlInputEvent, INPUT_QUEUE_EVENTS,
        MAX_INPUT_BATCH_EVENTS,
    };

    #[test]
    fn publication_bound_and_source_overflow_accumulate_independently_across_drains() {
        let input = Mutex::new(InputPublication::default());
        let event = ControlInputEvent {
            ticks: 123,
            input: ControlInput::Mouse {
                flags: 0x400,
                data: 120,
            },
        };
        for _ in 0..INPUT_QUEUE_EVENTS / MAX_INPUT_BATCH_EVENTS {
            publish_inputs(
                &input,
                ControlInputBatch {
                    frequency: 10_000_000,
                    dropped: 5,
                    events: vec![event; MAX_INPUT_BATCH_EVENTS],
                },
            )
            .unwrap();
        }
        publish_inputs(
            &input,
            ControlInputBatch {
                frequency: 10_000_000,
                dropped: 7,
                events: vec![event; MAX_INPUT_BATCH_EVENTS],
            },
        )
        .unwrap();
        let mut value = input.lock().unwrap();
        assert_eq!(value.events.len(), INPUT_QUEUE_EVENTS);
        assert_eq!(
            value.source_dropped + value.publication_dropped,
            7 + MAX_INPUT_BATCH_EVENTS as u64
        );
        value.events.clear();
        drop(value);
        publish_inputs(
            &input,
            ControlInputBatch {
                frequency: 10_000_000,
                dropped: 200,
                events: vec![event],
            },
        )
        .unwrap();
        let value = input.lock().unwrap();
        assert_eq!(
            value.source_dropped + value.publication_dropped,
            200 + MAX_INPUT_BATCH_EVENTS as u64
        );
        assert_eq!(value.events, [event]);
    }

    #[cfg(windows)]
    #[test]
    fn input_frames_cross_the_production_pipe_receiver_without_a_mouse_session() {
        let name = format!(
            r"\\.\pipe\ClickSync-product-input-{}-{}",
            std::process::id(),
            PIPE_ID.fetch_add(1, Ordering::Relaxed)
        );
        let server = Pipe::server(&name).unwrap();
        let cancelled = Arc::new(AtomicBool::new(false));
        let publication = Arc::new(Mutex::new(InputPublication::default()));
        let published = publication.clone();
        let receiver_cancelled = cancelled.clone();
        let receiver_pipe = server.clone();
        let (ingestion, messages) = mpsc::sync_channel(1);
        let receiver = thread::spawn(move || {
            receiver_pipe
                .connect(Instant::now() + IO_TIMEOUT, &receiver_cancelled)
                .unwrap();
            receive_data(receiver_pipe, ingestion, receiver_cancelled, published);
        });
        let client = Pipe::client(&name, Instant::now() + IO_TIMEOUT, &cancelled).unwrap();
        let events = vec![
            ControlInputEvent {
                ticks: 1,
                input: ControlInput::Keyboard {
                    virtual_key: 65,
                    scan_code: 30,
                    extended: false,
                    down: true,
                },
            },
            ControlInputEvent {
                ticks: 2,
                input: ControlInput::Mouse {
                    flags: 0x400,
                    data: (-120_i16) as u16,
                },
            },
        ];
        let payload = serde_json::to_vec(&ControlInputBatch {
            frequency: 10_000_000,
            dropped: 3,
            events: events.clone(),
        })
        .unwrap();
        let header = FrameHeader {
            kind: FrameKind::Input,
            generation: 0,
            payload_bytes: payload.len(),
            record_count: 0,
        }
        .encode()
        .unwrap();
        client
            .write_all(&header, Instant::now() + IO_TIMEOUT, &cancelled)
            .unwrap();
        client
            .write_all(&payload, Instant::now() + IO_TIMEOUT, &cancelled)
            .unwrap();
        let start = Instant::now();
        while publication.lock().unwrap().events.len() != 2 {
            assert!(start.elapsed() < IO_TIMEOUT);
            thread::sleep(Duration::from_millis(1));
        }
        assert!(messages.try_recv().is_err());
        let value = publication.lock().unwrap();
        assert_eq!(value.events, events);
        assert_eq!(value.source_dropped, 3);
        drop(value);
        cancelled.store(true, Ordering::Release);
        server.cancel().unwrap();
        receiver.join().unwrap();
    }
}

struct SessionOwner {
    sessions: Vec<(u32, Session)>,
    budget: Arc<SharedByteBudget>,
    generation: u64,
    prepared: bool,
    deferred: Vec<IngestMessage>,
    received_sequence: u64,
    received_records: u64,
    capture_evidence: CaptureEvidence,
    seal: Option<CaptureAck>,
    retired_snapshots: Vec<Arc<SessionSnapshot>>,
    limit_pause_requested: bool,
}

fn ingest_loop(
    messages: mpsc::Receiver<IngestMessage>,
    controls: mpsc::Receiver<IngestMessage>,
    requests: mpsc::SyncSender<DriverRequest>,
    state: Arc<Mutex<AppStatus>>,
    publication: SnapshotPublication,
    cancelled: Arc<AtomicBool>,
    delay: Arc<AtomicU64>,
) {
    let latest_snapshot = publication.first;
    let latest_prefix = publication.all;
    let budget = publication.budget;
    let _transport_reservation = match budget.try_reserve(TRANSPORT_MANAGED_RESERVATION_BYTES) {
        Ok(reservation) => reservation,
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
        deferred: Vec::with_capacity(INGESTION_QUEUE_CAPACITY),
        received_sequence: 0,
        received_records: 0,
        capture_evidence: CaptureEvidence::complete(),
        seal: None,
        retired_snapshots: Vec::with_capacity(MAX_RETIRED_SNAPSHOTS),
        limit_pause_requested: false,
    };
    let mut last_published = Instant::now();
    let mut last_prefix = Instant::now();
    while !cancelled.load(Ordering::Acquire) {
        let can_pause_limit = {
            let status = state.lock().expect("application status lock");
            status.phase == CapturePhase::Collecting && !status.command_pending
        };
        if can_pause_limit
            && owner.all_at_limit()
            && !owner.limit_pause_requested
            && requests
                .try_send(DriverRequest::Command {
                    permit: 0,
                    command: ControlCommand::Pause,
                    reply: None,
                })
                .is_ok()
        {
            owner.limit_pause_requested = true;
        }
        owner
            .retired_snapshots
            .retain(|snapshot| Arc::strong_count(snapshot) > 1);
        // Control/snapshot requests do not queue behind a backlog of data batches.
        let next = match controls.try_recv() {
            Ok(message) => Ok(message),
            Err(mpsc::TryRecvError::Empty) => messages.recv_timeout(Duration::from_millis(10)),
            Err(mpsc::TryRecvError::Disconnected) => break,
        };
        let message = match next {
            Ok(message) => message,
            Err(mpsc::RecvTimeoutError::Timeout) => {
                if last_published.elapsed() >= Duration::from_millis(50) {
                    owner.publish_summary(&state);
                    last_published = Instant::now();
                }
                if let Err(error) =
                    owner.publish_latest_prefix(&state, &latest_prefix, &mut last_prefix)
                {
                    owner.fault(&state, error);
                }
                continue;
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        };
        if owner.prepared && matches!(message, IngestMessage::Batch { .. }) {
            if owner.deferred.len() == INGESTION_QUEUE_CAPACITY {
                owner.fault(&state, "start transition batch budget exhausted".into());
                let _ = requests.try_send(DriverRequest::Command {
                    permit: 0,
                    command: ControlCommand::Stop,
                    reply: None,
                });
                break;
            }
            owner.deferred.push(message);
            continue;
        }
        let result = owner.handle(message, &state, &latest_snapshot, &delay);
        if let Err(error) = result {
            owner.fault(&state, error);
            let _ = requests.try_send(DriverRequest::Command {
                permit: 0,
                command: ControlCommand::Stop,
                reply: None,
            });
        }
        if !owner.prepared && !owner.deferred.is_empty() {
            let mut deferred = std::mem::replace(
                &mut owner.deferred,
                Vec::with_capacity(INGESTION_QUEUE_CAPACITY),
            );
            for message in deferred.drain(..) {
                if let Err(error) = owner.handle(message, &state, &latest_snapshot, &delay) {
                    owner.fault(&state, error);
                    let _ = requests.try_send(DriverRequest::Command {
                        permit: 0,
                        command: ControlCommand::Stop,
                        reply: None,
                    });
                    break;
                }
            }
            owner.deferred = deferred;
        }
        owner.publish_summary(&state);
        if let Err(error) = owner.publish_latest_prefix(&state, &latest_prefix, &mut last_prefix) {
            owner.fault(&state, error);
        }
        last_published = Instant::now();
    }
    let final_snapshot = latest_snapshot
        .lock()
        .expect("snapshot publication lock")
        .take();
    drop(final_snapshot);
    latest_prefix
        .lock()
        .expect("prefix publication lock")
        .take();
}

impl SessionOwner {
    fn snapshot_metadata_changed(snapshot: &SessionSnapshot, session: &Session) -> bool {
        let mut previous = snapshot.capture_evidence();
        let current = session.capture_evidence();
        // Receive progress is published at preview cadence, not as a metadata change.
        previous.received_sequence = current.received_sequence;
        snapshot.generation() != session.generation()
            || snapshot.status() != session.status()
            || snapshot.finalized() != session.finalized()
            || previous != current
    }

    fn prefix_metadata_changed(&self, prefix: &SessionPrefix, status: &AppStatus) -> bool {
        (prefix.generation, prefix.phase, prefix.complete)
            != (self.generation, status.phase, status.complete)
            || prefix.datasets.len() != self.sessions.len()
            || prefix.datasets.iter().zip(&self.sessions).any(
                |((key, snapshot), (current, session))| {
                    key != current || Self::snapshot_metadata_changed(snapshot, session)
                },
            )
    }

    fn revoke_stale_publications(
        &self,
        state: &Arc<Mutex<AppStatus>>,
        publication: &SnapshotPublication,
    ) {
        let mut first = publication.first.lock().expect("snapshot publication lock");
        if first.as_ref().is_some_and(|snapshot| {
            self.sessions
                .first()
                .is_none_or(|(_, session)| Self::snapshot_metadata_changed(snapshot, session))
        }) {
            first.take();
        }
        drop(first);
        let status = state.lock().expect("application status lock");
        let mut latest = publication.all.lock().expect("prefix publication lock");
        if latest
            .as_ref()
            .is_some_and(|prefix| self.prefix_metadata_changed(prefix, &status))
        {
            latest.take();
        }
    }

    fn sync_capture_evidence(&mut self) {
        self.capture_evidence.generation = Some(self.generation);
        self.capture_evidence.received_sequence = Some(self.received_sequence);
        for (_, session) in &mut self.sessions {
            session.set_capture_evidence(self.capture_evidence);
        }
    }

    fn fault(&mut self, state: &Arc<Mutex<AppStatus>>, error: String) {
        self.capture_evidence.integrity = CaptureIntegrity::Incomplete;
        self.sync_capture_evidence();
        set_fault(state, error);
    }

    fn publish_latest_prefix(
        &mut self,
        state: &Arc<Mutex<AppStatus>>,
        latest: &Arc<Mutex<Option<Arc<SessionPrefix>>>>,
        last: &mut Instant,
    ) -> Result<(), String> {
        if self.sessions.is_empty() || self.prepared {
            latest.lock().expect("prefix publication lock").take();
            return Ok(());
        }
        self.sync_capture_evidence();
        let status = state.lock().expect("application status lock");
        let previous = latest.lock().expect("prefix publication lock").clone();
        let changed = previous
            .as_ref()
            .is_none_or(|p| self.prefix_metadata_changed(p, &status));
        drop(status);
        let newer = previous
            .as_ref()
            .is_none_or(|p| p.received_sequence != self.received_sequence);
        // Publish genuine fixed prefixes at preview cadence; consumers still coalesce latest-only.
        if changed || (newer && last.elapsed() >= Duration::from_micros(8_333)) {
            let prefix = self.fixed_prefix(state)?;
            *latest.lock().expect("prefix publication lock") = Some(prefix);
            *last = Instant::now();
        }
        Ok(())
    }

    fn handle(
        &mut self,
        message: IngestMessage,
        state: &Arc<Mutex<AppStatus>>,
        latest_snapshot: &Arc<Mutex<Option<Arc<SessionSnapshot>>>>,
        delay: &AtomicU64,
    ) -> Result<(), String> {
        match message {
            IngestMessage::MotionArmed { .. } => {
                return Err("business motion cannot enter the external pipe owner".into());
            }
            IngestMessage::Prepare(reply) => {
                self.prepared = reply.send(()).is_ok();
            }
            IngestMessage::Ack(ack) => self.handle_ack(ack, state, latest_snapshot)?,
            IngestMessage::Reset(ack) => {
                self.prepared = false;
                self.generation = ack.generation;
                self.received_sequence = 0;
                self.received_records = 0;
                self.capture_evidence = CaptureEvidence::complete();
                self.seal = None;
                self.limit_pause_requested = false;
                for (_, session) in &mut self.sessions {
                    session.reset().map_err(|error| error.to_string())?;
                }
                self.retire_latest(latest_snapshot)?;
                let mut status = state.lock().expect("application status lock");
                status.phase = CapturePhase::Empty;
                status.generation = self.generation;
                status.error = None;
                status.capture_fault = None;
                status.complete = true;
                status.overflow = 0;
            }
            IngestMessage::Batch {
                generation,
                records,
            } => {
                self.append_records(generation, &records, state, latest_snapshot, delay)?;
            }
            IngestMessage::Status(capture) => {
                if capture.generation == self.generation {
                    self.capture_evidence.overflow =
                        self.capture_evidence.overflow.max(capture.overflow);
                    self.capture_evidence.disconnected |= capture.disconnected;
                    if capture.overflow > 0 || capture.disconnected {
                        self.capture_evidence.integrity = CaptureIntegrity::Incomplete;
                        self.sync_capture_evidence();
                    }
                    let mut status = state.lock().expect("application status lock");
                    status.captured = capture.captured;
                    status.sent = capture.sent;
                    status.overflow = capture.overflow;
                    status.queue_high_watermark = capture.queue_high_watermark;
                    if capture.overflow > 0 || capture.disconnected {
                        status_complete_error(
                            &mut status,
                            if capture.disconnected {
                                CaptureFault::DeviceDisconnected
                            } else {
                                CaptureFault::QueueOverflow
                            },
                            if capture.disconnected {
                                "selected input device disconnected".into()
                            } else {
                                "capture queue overflow; received prefix is incomplete".into()
                            },
                        );
                    }
                }
            }
            IngestMessage::Snapshot(reply) => {
                if state.lock().expect("application status lock").phase == CapturePhase::Faulted {
                    self.capture_evidence.integrity = CaptureIntegrity::Incomplete;
                }
                let result = self.publish_snapshot(latest_snapshot);
                let _ = reply.send(result);
            }
            IngestMessage::Prefix(reply) => {
                let result = self.fixed_prefix(state);
                let _ = reply.send(result);
            }
            IngestMessage::Drained {
                generation,
                queued,
                reply,
            } => {
                let drained = self.generation == generation
                    && self.received_sequence == queued
                    && self.seal.is_none()
                    && self.sessions.iter().all(|(_, session)| {
                        session.status() == clicksync_core::SessionStatus::Stopped
                    });
                let _ = reply.send(drained);
            }
            #[cfg(test)]
            IngestMessage::Inspect(_) => unreachable!("local owner inspection on pipe owner"),
            IngestMessage::Rejected(error) => {
                self.prepared = false;
                let mut status = state.lock().expect("application status lock");
                status.selected_device_keys = self.sessions.iter().map(|(key, _)| *key).collect();
                if status.phase != CapturePhase::Faulted {
                    status.error = Some(error);
                }
            }
            IngestMessage::Error(error) => {
                self.prepared = false;
                self.fault(state, error);
                for (_, session) in &mut self.sessions {
                    session.pause();
                }
                if !self.sessions.is_empty() {
                    let _ = self.publish_snapshot(latest_snapshot);
                }
            }
        }
        Ok(())
    }

    fn append_records(
        &mut self,
        generation: u64,
        records: &[CapturedRecord],
        state: &Arc<Mutex<AppStatus>>,
        latest_snapshot: &Arc<Mutex<Option<Arc<SessionSnapshot>>>>,
        delay: &AtomicU64,
    ) -> Result<(), String> {
        if generation != self.generation {
            state
                .lock()
                .expect("application status lock")
                .ignored_old_batches += 1;
            return Ok(());
        }
        let delay_ms = delay.load(Ordering::Acquire).min(2000);
        if delay_ms > 0 {
            thread::sleep(Duration::from_millis(delay_ms));
        }
        for record in records {
            if record.session_sequence != self.received_sequence + 1 {
                return Err(format!(
                    "session transport sequence discontinuity: expected {}, received {}",
                    self.received_sequence + 1,
                    record.session_sequence
                ));
            }
            let session = self
                .sessions
                .iter_mut()
                .find(|(key, _)| *key == record.device_key)
                .ok_or("batch contains an unselected device key")?;
            match session
                .1
                .append(record.record)
                .map_err(|error| error.to_string())?
            {
                AppendOutcome::Appended => {}
                AppendOutcome::PausedAtLimit => {}
                AppendOutcome::IgnoredWhileStopped
                    if session.1.event_count() >= session.1.config().max_session_events => {}
                AppendOutcome::IgnoredWhileStopped => {
                    return Err(
                        "current-generation record arrived outside its capture segment".into(),
                    );
                }
            }
            self.received_sequence = record.session_sequence;
            self.received_records += 1;
        }
        if self
            .sessions
            .iter()
            .any(|(_, session)| session.integrity() != CaptureIntegrity::Complete)
        {
            state.lock().expect("application status lock").complete = false;
        }
        self.try_seal(state, latest_snapshot)?;
        Ok(())
    }

    fn handle_ack(
        &mut self,
        ack: CaptureAck,
        state: &Arc<Mutex<AppStatus>>,
        latest_snapshot: &Arc<Mutex<Option<Arc<SessionSnapshot>>>>,
    ) -> Result<(), String> {
        match ack.command {
            CommandTag::Discover => {
                state.lock().expect("application status lock").devices = ack.devices;
            }
            CommandTag::Start => {
                let selected = state
                    .lock()
                    .expect("application status lock")
                    .selected_device_keys
                    .clone();
                if selected.is_empty() || selected.len() > 2 {
                    return Err("capture needs one or two selected devices".into());
                }
                if ack.generation == self.generation
                    && !self.sessions.is_empty()
                    && self
                        .sessions
                        .iter()
                        .map(|(key, _)| *key)
                        .collect::<Vec<_>>()
                        == selected
                {
                    let dual = self.sessions.len() == 2;
                    for (_, session) in &mut self.sessions {
                        session
                            .begin_collecting(ack.ticks, false, dual)
                            .map_err(|error| error.to_string())?;
                    }
                    self.prepared = false;
                    self.capture_evidence.queued = None;
                    self.capture_evidence.cutoff = None;
                    self.seal = None;
                    self.limit_pause_requested = false;
                    state.lock().expect("application status lock").phase = CapturePhase::Collecting;
                    return Ok(());
                }
                let mut sessions = Vec::with_capacity(2);
                for key in &selected {
                    let metadata = ack
                        .devices
                        .iter()
                        .find(|device| device.key == *key)
                        .map(|device| device.metadata.clone())
                        .ok_or("selected device is not in the capture acknowledgment")?;
                    let config = SessionConfig {
                        timestamp_frequency: ack.timestamp_frequency,
                        ..SessionConfig::default()
                    };
                    let mut session = Session::new(config, Arc::clone(&self.budget), metadata)
                        .map_err(|error| error.to_string())?;
                    session
                        .begin_collecting(ack.ticks, true, selected.len() == 2)
                        .map_err(|error| error.to_string())?;
                    sessions.push((*key, session));
                }
                self.sessions = sessions;
                self.generation = ack.generation;
                self.received_sequence = 0;
                self.received_records = 0;
                self.capture_evidence = CaptureEvidence::complete();
                self.prepared = false;
                self.seal = None;
                self.limit_pause_requested = false;
                self.retire_latest(latest_snapshot)?;
                let mut status = state.lock().expect("application status lock");
                status.phase = CapturePhase::Collecting;
                status.generation = self.generation;
                status.complete = true;
                status.error = None;
                status.capture_fault = None;
            }
            CommandTag::Resume => {
                if ack.generation != self.generation {
                    return Err("resume acknowledgment belongs to another generation".into());
                }
                let dual = self.sessions.len() == 2;
                for (_, session) in &mut self.sessions {
                    session
                        .begin_collecting(ack.ticks, false, dual)
                        .map_err(|error| error.to_string())?;
                }
                self.prepared = false;
                self.capture_evidence.queued = None;
                self.capture_evidence.cutoff = None;
                self.seal = None;
                self.limit_pause_requested = false;
                state.lock().expect("application status lock").phase = CapturePhase::Collecting;
            }
            CommandTag::Pause | CommandTag::Stop | CommandTag::Shutdown => {
                if ack.generation != self.generation {
                    return Err("stop acknowledgment belongs to another generation".into());
                }
                let mut status = state.lock().expect("application status lock");
                if status.phase != CapturePhase::Faulted {
                    status.phase = CapturePhase::Draining;
                }
                drop(status);
                self.capture_evidence.queued = Some(ack.queued);
                self.capture_evidence.cutoff = Some(ack.cutoff);
                self.capture_evidence.overflow = self.capture_evidence.overflow.max(ack.overflow);
                if !ack.complete {
                    self.capture_evidence.integrity = CaptureIntegrity::Incomplete;
                }
                self.sync_capture_evidence();
                self.seal = Some(ack);
                self.try_seal(state, latest_snapshot)?;
            }
            CommandTag::Reset => {
                return Err("reset acknowledgment took the wrong owner path".into());
            }
        }
        Ok(())
    }

    fn try_seal(
        &mut self,
        state: &Arc<Mutex<AppStatus>>,
        latest_snapshot: &Arc<Mutex<Option<Arc<SessionSnapshot>>>>,
    ) -> Result<(), String> {
        let Some(ack) = &self.seal else {
            return Ok(());
        };
        if !ack.complete {
            let mut status = state.lock().expect("application status lock");
            status.overflow = ack.overflow;
            status_complete_error(
                &mut status,
                CaptureFault::Incomplete,
                "capture stop reports an incomplete received prefix".into(),
            );
        }
        if self.received_sequence > ack.cutoff {
            return Err("records exceeded the acknowledged capture cutoff".into());
        }
        if ack.queued > ack.cutoff || (ack.complete && ack.queued != ack.cutoff) {
            return Err("capture acknowledgment has an inconsistent accepted prefix".into());
        }
        let accepted_cutoff = ack.queued;
        if self.received_sequence < accepted_cutoff {
            return Ok(());
        }
        let phase = if ack.command == CommandTag::Pause
            && !self.sessions.iter().any(|(_, session)| session.finalized())
        {
            for (_, session) in &mut self.sessions {
                if session.status() != clicksync_core::SessionStatus::Paused {
                    session.pause();
                }
            }
            CapturePhase::Paused
        } else {
            for (_, session) in &mut self.sessions {
                if session.status() != clicksync_core::SessionStatus::Stopped {
                    session.stop();
                }
            }
            CapturePhase::Stopped
        };
        let already_published = latest_snapshot
            .lock()
            .expect("snapshot publication lock")
            .as_ref()
            .is_some_and(|snapshot| {
                self.sessions.first().is_some_and(|(_, session)| {
                    snapshot.generation() == session.generation()
                        && snapshot.session_revision() == session.session_revision()
                        && snapshot.capture_evidence() == session.capture_evidence()
                })
            });
        if !self.sessions.is_empty() && !already_published {
            self.publish_snapshot(latest_snapshot)?;
        }
        self.seal = None;
        let mut status = state.lock().expect("application status lock");
        if status.phase != CapturePhase::Faulted {
            status.phase = phase;
            status.complete = self
                .sessions
                .iter()
                .all(|(_, session)| session.integrity() == CaptureIntegrity::Complete);
        }
        Ok(())
    }

    fn publish_summary(&self, state: &Arc<Mutex<AppStatus>>) {
        let mut status = state.lock().expect("application status lock");
        status.generation = self.generation;
        status.received_records = self.received_records;
        status.received_sequence = self.received_sequence;
        status.dataset_records.clear();
        status.dataset_records.extend(
            self.sessions
                .iter()
                .map(|(_, session)| session.event_count()),
        );
        status.managed_reserved_bytes = self.budget.reserved_bytes();
        status.retention_warning = self
            .sessions
            .iter()
            .any(|(_, session)| session.warning_reached());
        status.retention_limit_reached = self.all_at_limit();
        status.ignored_after_limit = self.received_records.saturating_sub(
            self.sessions
                .iter()
                .map(|(_, session)| session.event_count() as u64)
                .sum(),
        );
        status.complete &= self
            .sessions
            .iter()
            .all(|(_, session)| session.integrity() == CaptureIntegrity::Complete);
    }

    fn all_at_limit(&self) -> bool {
        !self.sessions.is_empty()
            && self
                .sessions
                .iter()
                .all(|(_, session)| session.event_count() >= session.config().max_session_events)
    }

    fn retire_latest(
        &mut self,
        latest_snapshot: &Arc<Mutex<Option<Arc<SessionSnapshot>>>>,
    ) -> Result<(), String> {
        if self.retired_snapshots.len() == MAX_RETIRED_SNAPSHOTS {
            return Err("retired snapshot reader limit reached".into());
        }
        if let Some(snapshot) = latest_snapshot
            .lock()
            .expect("snapshot publication lock")
            .take()
            && Arc::strong_count(&snapshot) > 1
        {
            self.retired_snapshots.push(snapshot);
        }
        Ok(())
    }

    fn fixed_prefix(
        &mut self,
        state: &Arc<Mutex<AppStatus>>,
    ) -> Result<Arc<SessionPrefix>, String> {
        if self.sessions.is_empty() {
            return Err("there is no selected session".into());
        }
        if state.lock().expect("application status lock").phase == CapturePhase::Faulted {
            self.capture_evidence.integrity = CaptureIntegrity::Incomplete;
        }
        self.sync_capture_evidence();
        let mut datasets = Vec::with_capacity(self.sessions.len());
        for (key, session) in &mut self.sessions {
            datasets.push((
                *key,
                Arc::new(session.snapshot().map_err(|error| error.to_string())?),
            ));
        }
        let status = state.lock().expect("application status lock");
        Ok(Arc::new(SessionPrefix {
            generation: self.generation,
            received_sequence: self.received_sequence,
            received_records: self.received_records,
            phase: status.phase,
            complete: status.complete
                && self
                    .sessions
                    .iter()
                    .all(|(_, session)| session.integrity() == CaptureIntegrity::Complete),
            datasets,
        }))
    }

    fn publish_snapshot(
        &mut self,
        latest_snapshot: &Arc<Mutex<Option<Arc<SessionSnapshot>>>>,
    ) -> Result<Arc<SessionSnapshot>, String> {
        self.sync_capture_evidence();
        let session = self
            .sessions
            .first_mut()
            .ok_or("there is no selected session")?;
        let snapshot = Arc::new(session.1.snapshot().map_err(|error| error.to_string())?);
        self.retire_latest(latest_snapshot)?;
        *latest_snapshot.lock().expect("snapshot publication lock") = Some(Arc::clone(&snapshot));
        Ok(snapshot)
    }
}

fn set_fault(state: &Arc<Mutex<AppStatus>>, error: String) {
    let mut state = state.lock().expect("application status lock");
    status_complete_error(&mut state, CaptureFault::Other, error);
}

fn status_complete_error(status: &mut AppStatus, fault: CaptureFault, error: String) {
    if status.phase != CapturePhase::Faulted {
        status.error = Some(error);
        status.capture_fault = Some(fault);
    }
    status.phase = CapturePhase::Faulted;
    status.complete = false;
}

#[cfg(test)]
mod tests {
    use super::*;

    fn owner() -> SessionOwner {
        SessionOwner {
            sessions: Vec::new(),
            budget: SharedByteBudget::new(SessionConfig::default().managed_memory_budget_bytes),
            generation: 0,
            prepared: false,
            deferred: Vec::new(),
            received_sequence: 0,
            received_records: 0,
            capture_evidence: CaptureEvidence::complete(),
            seal: None,
            retired_snapshots: Vec::new(),
            limit_pause_requested: false,
        }
    }

    #[test]
    fn source_sequence_gaps_are_preserved_while_transport_watermarks_are_contiguous() {
        let mut owner = owner();
        let state = Arc::new(Mutex::new(AppStatus {
            selected_device_keys: vec![1],
            ..AppStatus::default()
        }));
        let snapshot = Arc::new(Mutex::new(None));
        owner
            .handle_ack(
                CaptureAck {
                    command: CommandTag::Start,
                    generation: 1,
                    cutoff: 0,
                    queued: 0,
                    ticks: 1_000_000,
                    timestamp_frequency: 10_000_000,
                    complete: true,
                    overflow: 0,
                    queue_high_watermark: 0,
                    devices: vec![DeviceDescriptor {
                        key: 1,
                        metadata: Default::default(),
                        endpoint: Default::default(),
                    }],
                    error: None,
                },
                &state,
                &snapshot,
            )
            .unwrap();
        let records = [91, 102]
            .into_iter()
            .enumerate()
            .map(|(index, source)| {
                let mut captured = clicksync_core::protocol::replay_record(index as u64, 1, 1);
                captured.record.capture_sequence = source;
                captured.record.timing_sequence = source - 50;
                captured
            })
            .collect();
        owner
            .handle(
                IngestMessage::Batch {
                    generation: 1,
                    records,
                },
                &state,
                &snapshot,
                &AtomicU64::new(0),
            )
            .unwrap();
        assert_eq!((owner.received_records, owner.received_sequence), (2, 2));
        let published = owner.publish_snapshot(&snapshot).unwrap();
        assert_eq!(
            published
                .events()
                .map(|event| event.capture_sequence)
                .collect::<Vec<_>>(),
            [91, 102]
        );
        assert_eq!(
            published
                .events()
                .map(|event| event.timing_sequence)
                .collect::<Vec<_>>(),
            [41, 52]
        );
        let discontinuous = vec![clicksync_core::protocol::replay_record(3, 1, 1)];
        assert!(
            owner
                .handle(
                    IngestMessage::Batch {
                        generation: 1,
                        records: discontinuous,
                    },
                    &state,
                    &snapshot,
                    &AtomicU64::new(0)
                )
                .unwrap_err()
                .contains("transport sequence discontinuity")
        );
        assert_eq!(owner.received_records, 2);
        owner
            .handle(
                IngestMessage::Batch {
                    generation: 9,
                    records: vec![clicksync_core::protocol::replay_record(0, 9, 1)],
                },
                &state,
                &snapshot,
                &AtomicU64::new(0),
            )
            .unwrap();
        assert_eq!(owner.received_records, 2);
        assert_eq!(state.lock().unwrap().ignored_old_batches, 1);
    }

    #[test]
    fn subsequent_faults_preserve_the_original_diagnostic() {
        let state = Arc::new(Mutex::new(AppStatus::default()));
        set_fault(&state, "capture sequence discontinuity".into());
        set_fault(&state, "pipe closed".into());
        let status = state.lock().unwrap();
        assert_eq!(
            status.error.as_deref(),
            Some("capture sequence discontinuity")
        );
        assert_eq!(status.phase, CapturePhase::Faulted);
        assert_eq!(status.capture_fault, Some(CaptureFault::Other));
        assert!(!status.complete);
    }

    #[test]
    fn capture_fault_kind_survives_discovery_and_secondary_faults_until_a_new_generation() {
        for (disconnected, expected) in [
            (true, CaptureFault::DeviceDisconnected),
            (false, CaptureFault::QueueOverflow),
        ] {
            let mut owner = owner();
            let state = Arc::new(Mutex::new(AppStatus {
                selected_device_keys: vec![1],
                ..Default::default()
            }));
            let snapshot = Arc::new(Mutex::new(None));
            let mut ack = CaptureAck {
                command: CommandTag::Start,
                generation: 1,
                cutoff: 0,
                queued: 0,
                ticks: 1_000_000,
                timestamp_frequency: 10_000_000,
                complete: true,
                overflow: 0,
                queue_high_watermark: 0,
                devices: vec![DeviceDescriptor {
                    key: 1,
                    ..Default::default()
                }],
                error: None,
            };
            owner.handle_ack(ack.clone(), &state, &snapshot).unwrap();
            owner
                .handle(
                    IngestMessage::Status(CaptureStatus {
                        generation: 1,
                        captured: 0,
                        queued: 0,
                        sent: 0,
                        overflow: u64::from(!disconnected),
                        queue_high_watermark: 0,
                        active: false,
                        disconnected,
                    }),
                    &state,
                    &snapshot,
                    &AtomicU64::new(0),
                )
                .unwrap();
            let diagnostic = state.lock().unwrap().error.clone();
            ack.command = CommandTag::Discover;
            owner.handle_ack(ack.clone(), &state, &snapshot).unwrap();
            set_fault(&state, "secondary transport failure".into());
            {
                let status = state.lock().unwrap();
                assert_eq!(status.capture_fault, Some(expected));
                assert_eq!(status.error, diagnostic);
                assert!(!status.complete);
            }
            ack.command = CommandTag::Start;
            ack.generation = 2;
            owner.handle_ack(ack.clone(), &state, &snapshot).unwrap();
            assert!(state.lock().unwrap().capture_fault.is_none());
            assert!(state.lock().unwrap().error.is_none());
            set_fault(&state, "another failure".into());
            ack.command = CommandTag::Reset;
            ack.generation = 3;
            owner
                .handle(
                    IngestMessage::Reset(ack),
                    &state,
                    &snapshot,
                    &AtomicU64::new(0),
                )
                .unwrap();
            let status = state.lock().unwrap();
            assert!(status.capture_fault.is_none() && status.error.is_none() && status.complete);
        }
    }

    #[test]
    fn local_pending_batch_recovers_budget_failure_without_losing_or_repeating_records() {
        for (preload, allow_seal) in [(0, false), (3, false), (3, true)] {
            let config = SessionConfig {
                max_session_events: 16,
                warning_event_threshold: 8,
                chunk_capacity: 4,
                managed_memory_budget_bytes: 64 * 1024,
                ..SessionConfig::default()
            };
            let mut owner = owner();
            owner.generation = 7;
            owner.budget = SharedByteBudget::new(config.managed_memory_budget_bytes);
            let mut session =
                Session::new(config.clone(), owner.budget.clone(), Default::default()).unwrap();
            session.begin_collecting(1000, true, false).unwrap();
            owner.sessions.push((1, session));
            let state = Arc::new(Mutex::new(AppStatus {
                generation: 7,
                phase: CapturePhase::Collecting,
                complete: true,
                ..Default::default()
            }));
            let snapshot = Arc::new(Mutex::new(None));
            let delay = AtomicU64::new(0);
            let records: Vec<_> = (0..preload + 6)
                .map(|index| {
                    let mut record = clicksync_core::protocol::replay_record(index as u64, 7, 1);
                    record.record.raw_capture_ticks = 1000 + (index / 2) as u64 * 10;
                    record.record.capture_sequence = 100 + index as u64 * 3;
                    record.record.timing_sequence = 1000 + index as u64 * 2;
                    record.record.raw_delta_x = i32::MIN + index as i32;
                    record.record.raw_delta_y = i32::MAX - index as i32;
                    record.record.button_flags = 0x411;
                    record.record.button_data = 0xff88;
                    record.record.extra_information = 0xdead_beef;
                    record.record.movement_mode = if index & 1 == 0 {
                        clicksync_core::RawMouseMovementMode::Absolute
                    } else {
                        clicksync_core::RawMouseMovementMode::Relative
                    };
                    record.record.raw_mouse_flags = if index & 1 == 0 { 0xb } else { 0xa };
                    record
                })
                .collect();
            let mut reference = Session::new(
                config.clone(),
                SharedByteBudget::new(config.managed_memory_budget_bytes),
                Default::default(),
            )
            .unwrap();
            reference.begin_collecting(1000, true, false).unwrap();
            for record in &records {
                reference.append(record.record).unwrap();
            }
            reference.stop();
            owner
                .append_records(7, &records[..preload], &state, &snapshot, &delay)
                .unwrap();
            let layout = owner.sessions[0].1.memory_layout();
            let headroom = if allow_seal {
                layout.directory_root_bytes + layout.directory_page_bytes
            } else {
                0
            };
            let blocked = owner
                .budget
                .try_reserve(owner.budget.available_bytes() - headroom)
                .unwrap();
            let mut batch = records[preload..].to_vec();
            let capacity = batch.capacity();
            let error =
                local::append_pending_batch(&mut owner, &mut batch, &state, &snapshot, &delay)
                    .unwrap_err();
            assert!(error.contains("managed byte budget exceeded"), "{error}");
            owner.fault(&state, error);
            let committed = if preload == 0 { 0 } else { 4 };
            assert_eq!(owner.received_sequence, committed as u64);
            assert_eq!(owner.received_records, committed as u64);
            assert_eq!(owner.sessions[0].1.event_count(), committed);
            assert_eq!(batch, records[committed..]);
            assert!(
                local::append_pending_batch(&mut owner, &mut batch, &state, &snapshot, &delay)
                    .unwrap_err()
                    .contains("managed byte budget exceeded")
            );
            assert_eq!(batch, records[committed..]);
            assert_eq!(owner.received_sequence, committed as u64);
            // The Stop ACK is processed while the uncommitted suffix still awaits budget.
            owner
                .handle_ack(
                    CaptureAck {
                        command: CommandTag::Stop,
                        generation: 7,
                        cutoff: records.len() as u64,
                        queued: records.len() as u64,
                        ticks: 2000,
                        timestamp_frequency: config.timestamp_frequency,
                        complete: true,
                        overflow: 0,
                        queue_high_watermark: records.len(),
                        devices: Vec::new(),
                        error: None,
                    },
                    &state,
                    &snapshot,
                )
                .unwrap();
            assert!(owner.seal.is_some());
            assert_eq!(
                owner.sessions[0].1.status(),
                clicksync_core::SessionStatus::Collecting
            );
            drop(blocked);
            local::append_pending_batch(&mut owner, &mut batch, &state, &snapshot, &delay).unwrap();
            assert!(batch.is_empty());
            assert_eq!(batch.capacity(), capacity);
            assert!(owner.seal.is_none());
            assert_eq!(owner.received_sequence, records.len() as u64);
            assert_eq!(owner.received_records, records.len() as u64);
            assert_eq!(
                owner.budget.limit_bytes(),
                config.managed_memory_budget_bytes
            );
            let frozen = snapshot.lock().unwrap().clone().unwrap();
            let reference = reference.snapshot().unwrap();
            assert_eq!(frozen.status(), clicksync_core::SessionStatus::Stopped);
            assert_eq!(frozen.event_count(), records.len());
            assert!(frozen.events().eq(reference.events()));
            assert_eq!(
                frozen.capture_evidence().received_sequence,
                Some(records.len() as u64)
            );
            assert_eq!(frozen.capture_evidence().queued, Some(records.len() as u64));
            assert_eq!(frozen.capture_evidence().ignored_after_limit, 0);
            assert_eq!(frozen.integrity(), CaptureIntegrity::Incomplete);
            local::append_pending_batch(&mut owner, &mut batch, &state, &snapshot, &delay).unwrap();
            assert_eq!(owner.received_records, records.len() as u64);
        }
    }

    #[test]
    fn local_pending_batch_does_not_discard_a_failed_duplicate_sequence() {
        let mut owner = owner();
        owner.generation = 7;
        let mut session = Session::default_with_metadata(Default::default()).unwrap();
        session.begin_collecting(1_000_000, true, false).unwrap();
        owner.sessions.push((1, session));
        let state = Arc::new(Mutex::new(AppStatus::default()));
        let snapshot = Arc::new(Mutex::new(None));
        let delay = AtomicU64::new(0);
        let record = clicksync_core::protocol::replay_record(0, 7, 1);
        owner
            .append_records(7, &[record], &state, &snapshot, &delay)
            .unwrap();
        let mut batch = vec![record];
        assert!(
            local::append_pending_batch(&mut owner, &mut batch, &state, &snapshot, &delay)
                .unwrap_err()
                .contains("transport sequence discontinuity")
        );
        assert_eq!(batch, [record]);
        assert_eq!(owner.received_sequence, 1);
        assert_eq!(owner.received_records, 1);
    }

    #[test]
    fn local_fault_stop_retries_a_full_queue_but_submits_only_once_per_generation() {
        let (requests, receiver) = mpsc::sync_channel(1);
        assert!(
            requests
                .try_send(DriverRequest::Command {
                    command: ControlCommand::Discover,
                    permit: 0,
                    reply: None,
                })
                .is_ok()
        );
        let mut submitted = None;
        local::request_fault_stop(7, &mut submitted, &requests);
        assert_eq!(submitted, None);
        let _ = receiver.try_recv().unwrap();
        for generation in [7, 8] {
            local::request_fault_stop(generation, &mut submitted, &requests);
            assert_eq!(submitted, Some(generation));
            assert!(matches!(
                receiver.try_recv().unwrap(),
                DriverRequest::FaultStop { generation: actual } if actual == generation
            ));
            for _ in 0..100 {
                local::request_fault_stop(generation, &mut submitted, &requests);
            }
            assert!(matches!(
                receiver.try_recv(),
                Err(mpsc::TryRecvError::Empty)
            ));
        }
    }

    #[test]
    fn independent_dataset_limits_preserve_their_prefixes_without_a_transport_fault() {
        let mut owner = owner();
        owner.generation = 1;
        for (key, limit) in [(1, 2), (2, 4)] {
            let mut session = Session::new(
                SessionConfig {
                    max_session_events: limit,
                    warning_event_threshold: 1,
                    chunk_capacity: 4,
                    ..SessionConfig::default()
                },
                Arc::clone(&owner.budget),
                Default::default(),
            )
            .unwrap();
            session.begin_collecting(1_000_000, true, true).unwrap();
            owner.sessions.push((key, session));
        }
        let state = Arc::new(Mutex::new(AppStatus {
            phase: CapturePhase::Collecting,
            complete: true,
            ..AppStatus::default()
        }));
        let snapshot = Arc::new(Mutex::new(None));
        let records: Vec<_> = (0..11)
            .map(|index| clicksync_core::protocol::replay_record(index, 1, (index % 2 + 1) as u32))
            .collect();
        owner
            .append_records(1, &records[..6], &state, &snapshot, &AtomicU64::new(0))
            .unwrap();
        let partial = owner.fixed_prefix(&state).unwrap();
        assert!(!partial.complete);
        assert_eq!(partial.phase, CapturePhase::Collecting);
        assert_eq!(
            partial.datasets[0].1.integrity(),
            CaptureIntegrity::Incomplete
        );
        assert_eq!(
            partial.datasets[1].1.integrity(),
            CaptureIntegrity::Complete
        );
        assert_eq!(
            partial.datasets[0].1.capture_evidence().ignored_after_limit,
            1
        );
        owner
            .handle(
                IngestMessage::Batch {
                    generation: 1,
                    records: records[6..].to_vec(),
                },
                &state,
                &snapshot,
                &AtomicU64::new(0),
            )
            .unwrap();
        owner.publish_summary(&state);
        let status = state.lock().unwrap();
        assert_eq!(status.received_records, 11);
        assert_eq!(status.received_sequence, 11);
        assert_eq!(status.dataset_records, [2, 4]);
        assert_eq!(status.ignored_after_limit, 5);
        assert!(status.retention_warning && status.retention_limit_reached);
        assert!(!status.complete);
        assert!(status.error.is_none());
        assert_eq!(status.phase, CapturePhase::Collecting);
        assert!(!status.command_pending && owner.all_at_limit());
        drop(status);
        let prefix = owner.fixed_prefix(&state).unwrap();
        assert!(!prefix.complete);
        assert_eq!(
            prefix
                .datasets
                .iter()
                .map(|(_, snapshot)| snapshot.capture_evidence().ignored_after_limit)
                .collect::<Vec<_>>(),
            [4, 1]
        );
        assert!(
            prefix
                .datasets
                .iter()
                .all(|(_, snapshot)| { snapshot.integrity() == CaptureIntegrity::Incomplete })
        );
        let snapshot = owner.publish_snapshot(&snapshot).unwrap();
        assert_eq!(snapshot.event_count(), 2);
        assert_eq!(snapshot.event_at(1).unwrap().capture_sequence, 3);
    }

    #[test]
    fn retention_loss_survives_complete_stop_ack_and_reset_keeps_fixed_evidence() {
        let mut owner = owner();
        owner.generation = 1;
        let mut session = Session::new(
            SessionConfig {
                max_session_events: 1,
                warning_event_threshold: 1,
                chunk_capacity: 4,
                ..SessionConfig::default()
            },
            owner.budget.clone(),
            Default::default(),
        )
        .unwrap();
        session.begin_collecting(1_000_000, true, false).unwrap();
        owner.sessions.push((1, session));
        let state = Arc::new(Mutex::new(AppStatus {
            phase: CapturePhase::Collecting,
            complete: true,
            ..Default::default()
        }));
        let latest = Arc::new(Mutex::new(None));
        owner
            .append_records(
                1,
                &(0..2)
                    .map(|index| clicksync_core::protocol::replay_record(index, 1, 1))
                    .collect::<Vec<_>>(),
                &state,
                &latest,
                &AtomicU64::new(0),
            )
            .unwrap();
        let ack = CaptureAck {
            command: CommandTag::Stop,
            generation: 1,
            cutoff: 2,
            queued: 2,
            ticks: 2_000_000,
            timestamp_frequency: 10_000_000,
            complete: true,
            overflow: 0,
            queue_high_watermark: 2,
            devices: Vec::new(),
            error: None,
        };
        owner.handle_ack(ack.clone(), &state, &latest).unwrap();
        let frozen = owner.fixed_prefix(&state).unwrap();
        assert!(!frozen.complete);
        assert_eq!(state.lock().unwrap().phase, CapturePhase::Stopped);
        assert!(!state.lock().unwrap().complete);
        assert_eq!(frozen.received_sequence, 2);
        assert_eq!(frozen.datasets[0].1.event_count(), 1);
        let evidence = frozen.datasets[0].1.capture_evidence();
        assert_eq!(evidence.integrity, CaptureIntegrity::Incomplete);
        assert_eq!(evidence.ignored_after_limit, 1);
        assert_eq!(evidence.received_sequence, Some(2));
        assert_eq!(evidence.queued, Some(2));
        assert_eq!(evidence.cutoff, Some(2));
        owner
            .handle(
                IngestMessage::Reset(CaptureAck {
                    command: CommandTag::Reset,
                    generation: 2,
                    cutoff: 0,
                    queued: 0,
                    ..ack
                }),
                &state,
                &latest,
                &AtomicU64::new(0),
            )
            .unwrap();
        assert_eq!(frozen.datasets[0].1.capture_evidence(), evidence);
        assert_eq!(owner.sessions[0].1.integrity(), CaptureIntegrity::Complete);
    }

    #[test]
    fn incomplete_seal_drains_the_exact_accepted_prefix_even_without_an_assigned_failed_sequence() {
        for (cutoff, queued) in [(2, 2), (3, 2)] {
            let mut owner = owner();
            owner.generation = 1;
            let mut session = Session::default_with_metadata(Default::default()).unwrap();
            session.begin_collecting(1_000_000, true, false).unwrap();
            owner.sessions.push((1, session));
            let state = Arc::new(Mutex::new(AppStatus {
                phase: CapturePhase::Collecting,
                complete: true,
                ..AppStatus::default()
            }));
            let snapshot = Arc::new(Mutex::new(None));
            owner
                .handle_ack(
                    CaptureAck {
                        command: CommandTag::Stop,
                        generation: 1,
                        cutoff,
                        queued,
                        ticks: 2_000_000,
                        timestamp_frequency: 10_000_000,
                        complete: false,
                        overflow: 1,
                        queue_high_watermark: 2,
                        devices: Vec::new(),
                        error: None,
                    },
                    &state,
                    &snapshot,
                )
                .unwrap();
            assert!(snapshot.lock().unwrap().is_none());
            owner
                .handle(
                    IngestMessage::Batch {
                        generation: 1,
                        records: (0..2)
                            .map(|index| clicksync_core::protocol::replay_record(index, 1, 1))
                            .collect(),
                    },
                    &state,
                    &snapshot,
                    &AtomicU64::new(0),
                )
                .unwrap();
            assert!(owner.seal.is_none());
            assert_eq!(snapshot.lock().unwrap().as_ref().unwrap().event_count(), 2);
            let evidence = snapshot
                .lock()
                .unwrap()
                .as_ref()
                .unwrap()
                .capture_evidence();
            assert_eq!(evidence.integrity, CaptureIntegrity::Incomplete);
            assert_eq!(evidence.overflow, 1);
            assert_eq!(evidence.received_sequence, Some(queued));
            assert_eq!(evidence.queued, Some(queued));
            assert_eq!(evidence.cutoff, Some(cutoff));
            assert_eq!(state.lock().unwrap().phase, CapturePhase::Faulted);
            assert!(!state.lock().unwrap().complete);
        }
    }
}
