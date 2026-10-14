use std::env;
use std::error::Error;
use std::ffi::OsString;
use std::io::{self, Write};
use std::mem::size_of;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, SyncSender, TryRecvError, TrySendError};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use clicksync_core::protocol::{
    BATCH_FLUSH_INTERVAL_MS, CAPTURE_QUEUE_RECORDS, CaptureAck, CaptureSource, CaptureStatus,
    ControlCommand, DeviceDescriptor, FRAME_HEADER_BYTES, FrameHeader, FrameKind,
    MAX_BATCH_RECORDS, MAX_DEVICE_COUNT, MAX_FRAME_PAYLOAD_BYTES, WIRE_RECORD_BYTES,
};
use clicksync_core::{RawMouseMovementMode, RawMouseRecord, SessionMetadata};
use clicksync_ipc::Pipe;

use crate::platform::{InputAck, InputCommand, InputService};
use crate::{CaptureShared, CapturedRecord};

const PIPE_PREFIX: &str = r"\\.\pipe\ClickSync-";
const CONNECT_TIMEOUT: Duration = Duration::from_secs(5);
const IO_TIMEOUT: Duration = Duration::from_secs(3);
const CONTROL_IDLE_TIMEOUT: Duration = Duration::from_secs(30);
const INPUT_COMMAND_TIMEOUT: Duration = Duration::from_secs(5);
const STATUS_INTERVAL: Duration = Duration::from_millis(100);
const QUEUE_POLL_INTERVAL: Duration = Duration::from_millis(1);
const STATUS_PAYLOAD_BYTES: usize = 1024;
const PIPE_STAGING_RESERVE_BYTES: usize = MAX_FRAME_PAYLOAD_BYTES * 2;
#[cfg(test)]
const DEVICE_FIELD_BYTES: usize = clicksync_core::protocol::MAX_CAPTURE_DEVICE_FIELD_BYTES;
const REPLAY_RECORD_LIMIT: u64 = 16_000_000;
const REPLAY_FREQUENCY: u64 = 10_000_000;
const REPLAY_START_TICKS: u64 = 1_000_000;

#[derive(Debug, PartialEq)]
struct Arguments {
    data: String,
    control: String,
    parent: u32,
    replay: bool,
}

impl Arguments {
    fn parse(arguments: impl IntoIterator<Item = OsString>) -> io::Result<Self> {
        let mut arguments = arguments.into_iter();
        let mut data = None;
        let mut control = None;
        let mut parent = None;
        let mut replay = false;
        while let Some(argument) = arguments.next() {
            let argument = argument
                .into_string()
                .map_err(|_| invalid("worker arguments must be Unicode"))?;
            match argument.as_str() {
                "--data" | "--control" | "--parent" => {
                    let value = arguments
                        .next()
                        .ok_or_else(|| invalid("missing worker argument value"))?
                        .into_string()
                        .map_err(|_| invalid("worker argument value must be Unicode"))?;
                    let destination = match argument.as_str() {
                        "--data" => &mut data,
                        "--control" => &mut control,
                        _ => &mut parent,
                    };
                    if destination.replace(value).is_some() {
                        return Err(invalid("duplicate worker argument"));
                    }
                }
                "--replay" if !replay => replay = true,
                _ => return Err(invalid("unknown or duplicate worker argument")),
            }
        }
        let data = data.ok_or_else(|| invalid("--data is required"))?;
        let control = control.ok_or_else(|| invalid("--control is required"))?;
        validate_pipe_name(&data)?;
        validate_pipe_name(&control)?;
        if data == control {
            return Err(invalid("data and control require different pipes"));
        }
        let parent = parent
            .ok_or_else(|| invalid("--parent is required"))?
            .parse::<u32>()
            .map_err(|_| invalid("--parent must be a nonzero process ID"))?;
        if parent == 0 {
            return Err(invalid("--parent must be a nonzero process ID"));
        }
        Ok(Self {
            data,
            control,
            parent,
            replay,
        })
    }
}

fn invalid(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, message)
}

fn validate_pipe_name(name: &str) -> io::Result<()> {
    let suffix = name
        .strip_prefix(PIPE_PREFIX)
        .ok_or_else(|| invalid("worker only accepts local ClickSync pipes"))?;
    if suffix.is_empty()
        || name.len() > 240
        || suffix
            .chars()
            .any(|character| character == '\\' || character == '/' || character.is_control())
    {
        return Err(invalid("invalid local ClickSync pipe name"));
    }
    Ok(())
}

pub fn run_from_arguments() -> Result<(), Box<dyn Error>> {
    let arguments = Arguments::parse(env::args_os().skip(1))?;
    if !cfg!(windows) {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "the capture worker requires Windows",
        )
        .into());
    }
    run(arguments).map_err(Into::into)
}

fn run(arguments: Arguments) -> io::Result<()> {
    let cancelled = Arc::new(AtomicBool::new(false));
    let data = Pipe::client(
        &arguments.data,
        Instant::now() + CONNECT_TIMEOUT,
        &cancelled,
    )?;
    let control = Pipe::client(
        &arguments.control,
        Instant::now() + CONNECT_TIMEOUT,
        &cancelled,
    )?;
    if data.peer_process_id()? != arguments.parent || control.peer_process_id()? != arguments.parent
    {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "capture pipe server is not the declared parent process",
        ));
    }

    let (producer, consumer) = rtrb::RingBuffer::new(CAPTURE_QUEUE_RECORDS);
    let (input_producer, input_consumer) =
        rtrb::RingBuffer::new(clicksync_core::protocol::INPUT_QUEUE_EVENTS);
    let shared = Arc::new(CaptureShared::default());
    let devices = Arc::new(Mutex::new(Vec::new()));
    let mut source = InputSource::spawn(
        arguments.replay,
        producer,
        input_producer,
        Arc::clone(&shared),
        Arc::clone(&devices),
    )?;
    let sent = Arc::new(Mutex::new(SentProgress::default()));
    eprintln!(
        "capture transport: queue_records={CAPTURE_QUEUE_RECORDS} batch_records={MAX_BATCH_RECORDS} \
         flush_ms={BATCH_FLUSH_INTERVAL_MS} reserved_bytes={} source={}",
        reserved_transport_bytes(),
        if arguments.replay {
            "Replay"
        } else {
            "RawInput"
        }
    );
    let data_thread = {
        let data = data.clone();
        let control = control.clone();
        let shared = Arc::clone(&shared);
        let sent = Arc::clone(&sent);
        let cancelled = Arc::clone(&cancelled);
        thread::Builder::new()
            .name("ClickSyncData".into())
            .spawn(move || {
                let result = send_data(&data, consumer, input_consumer, &shared, &sent, &cancelled);
                if result.is_err() && !cancelled.load(Ordering::Acquire) {
                    shared.disconnected.store(true, Ordering::Release);
                    shared.active.store(false, Ordering::Release);
                    cancelled.store(true, Ordering::Release);
                    let _ = data.cancel();
                    let _ = control.cancel();
                    return result;
                }
                if cancelled.load(Ordering::Acquire) {
                    return Ok(());
                }
                result
            })?
    };

    let result = control_loop(&control, &mut source, &shared, &devices, &sent, &cancelled);
    cancelled.store(true, Ordering::Release);
    shared.active.store(false, Ordering::Release);
    let _ = data.cancel();
    let _ = control.cancel();
    let shutdown = source.shutdown();
    drop(source);
    let data_result = data_thread
        .join()
        .map_err(|_| io::Error::other("capture data thread panicked"))?;
    data_result.and(result).and(shutdown)
}

/// Fixed queue and transport buffers, excluding platform metadata and OS buffers.
pub fn reserved_transport_bytes() -> usize {
    CAPTURE_QUEUE_RECORDS * size_of::<CapturedRecord>()
        + clicksync_core::protocol::INPUT_QUEUE_EVENTS
            * size_of::<clicksync_core::protocol::ControlInputEvent>()
        + MAX_FRAME_PAYLOAD_BYTES
        + clicksync_core::protocol::MAX_INPUT_BATCH_EVENTS
            * size_of::<clicksync_core::protocol::ControlInputEvent>()
        + size_of::<rtrb::Producer<CapturedRecord>>()
        + size_of::<rtrb::Consumer<CapturedRecord>>()
        + size_of::<CaptureShared>()
        + size_of::<SentProgress>()
        + size_of::<Batch>()
        + size_of::<CapturedRecord>()
        + FRAME_HEADER_BYTES
        + MAX_FRAME_PAYLOAD_BYTES * 2
        + STATUS_PAYLOAD_BYTES
        + PIPE_STAGING_RESERVE_BYTES
}

#[derive(Default)]
struct SentProgress {
    generation: u64,
    sequence: u64,
}

impl SentProgress {
    fn current(&mut self, generation: u64) -> u64 {
        if self.generation != generation {
            self.generation = generation;
            self.sequence = 0;
        }
        self.sequence
    }

    fn complete(&mut self, generation: u64, sequence: u64, shared: &CaptureShared) {
        if shared.generation.load(Ordering::Acquire) == generation {
            self.current(generation);
            self.sequence = self.sequence.max(sequence);
        }
    }
}

struct Batch {
    bytes: [u8; MAX_BATCH_RECORDS * WIRE_RECORD_BYTES],
    count: usize,
    generation: u64,
    cutoff: u64,
    started: Option<Instant>,
}

impl Batch {
    fn new() -> Self {
        Self {
            bytes: [0; MAX_BATCH_RECORDS * WIRE_RECORD_BYTES],
            count: 0,
            generation: 0,
            cutoff: 0,
            started: None,
        }
    }

    fn append(&mut self, record: CapturedRecord, now: Instant) -> io::Result<bool> {
        if self.count == MAX_BATCH_RECORDS
            || (self.count != 0 && self.generation != record.generation)
        {
            return Ok(false);
        }
        let encoded = record.encode().map_err(io::Error::other)?;
        if self.count == 0 {
            self.generation = record.generation;
            self.started = Some(now);
        }
        let offset = self.count * WIRE_RECORD_BYTES;
        self.bytes[offset..offset + WIRE_RECORD_BYTES].copy_from_slice(&encoded);
        self.count += 1;
        self.cutoff = self.cutoff.max(record.session_sequence);
        Ok(true)
    }

    fn due(&self, now: Instant) -> bool {
        self.count == MAX_BATCH_RECORDS
            || self.started.is_some_and(|started| {
                now.saturating_duration_since(started)
                    >= Duration::from_millis(BATCH_FLUSH_INTERVAL_MS)
            })
    }

    fn clear(&mut self) {
        self.count = 0;
        self.generation = 0;
        self.cutoff = 0;
        self.started = None;
    }
}

fn send_batch(
    pipe: &Pipe,
    batch: &mut Batch,
    shared: &CaptureShared,
    sent: &Mutex<SentProgress>,
    cancelled: &AtomicBool,
) -> io::Result<()> {
    let header = FrameHeader {
        kind: FrameKind::Batch,
        generation: batch.generation,
        payload_bytes: batch.count * WIRE_RECORD_BYTES,
        record_count: batch.count,
    }
    .encode()
    .map_err(io::Error::other)?;
    let deadline = Instant::now() + IO_TIMEOUT;
    pipe.write_all(&header, deadline, cancelled)?;
    pipe.write_all(
        &batch.bytes[..batch.count * WIRE_RECORD_BYTES],
        deadline,
        cancelled,
    )?;
    sent.lock()
        .map_err(|_| io::Error::other("sent watermark lock is poisoned"))?
        .complete(batch.generation, batch.cutoff, shared);
    batch.clear();
    Ok(())
}

fn send_data(
    pipe: &Pipe,
    mut consumer: rtrb::Consumer<CapturedRecord>,
    mut inputs: rtrb::Consumer<clicksync_core::protocol::ControlInputEvent>,
    shared: &CaptureShared,
    sent: &Mutex<SentProgress>,
    cancelled: &AtomicBool,
) -> io::Result<()> {
    let mut batch = Batch::new();
    let mut pending = None;
    let mut status_bytes = Vec::with_capacity(STATUS_PAYLOAD_BYTES);
    let mut next_status = Instant::now();
    let mut input_bytes = Vec::with_capacity(MAX_FRAME_PAYLOAD_BYTES);
    let mut input_batch = clicksync_core::protocol::ControlInputBatch {
        frequency: input_frequency()?,
        events: Vec::with_capacity(clicksync_core::protocol::MAX_INPUT_BATCH_EVENTS),
        ..Default::default()
    };
    let mut next_input = Instant::now();
    let mut input_dropped = 0;
    while !cancelled.load(Ordering::Acquire) {
        let now = Instant::now();
        if now >= next_input {
            input_batch.events.clear();
            while input_batch.events.len() < clicksync_core::protocol::MAX_INPUT_BATCH_EVENTS {
                let Ok(input) = inputs.pop() else {
                    break;
                };
                input_batch.events.push(input);
            }
            input_batch.dropped = shared.input_dropped.load(Ordering::Acquire);
            if !input_batch.events.is_empty() || input_batch.dropped != input_dropped {
                write_json(
                    pipe,
                    FrameKind::Input,
                    0,
                    &mut input_bytes,
                    MAX_FRAME_PAYLOAD_BYTES,
                    cancelled,
                    |writer| serde_json::to_writer(writer, &input_batch),
                )?;
                input_dropped = input_batch.dropped;
            }
            next_input = now + Duration::from_millis(BATCH_FLUSH_INTERVAL_MS);
        }
        if batch.count != 0 && batch.due(now) {
            send_batch(pipe, &mut batch, shared, sent, cancelled)?;
        }
        if now >= next_status {
            if batch.count != 0 {
                send_batch(pipe, &mut batch, shared, sent, cancelled)?;
            }
            let status = capture_status(shared, sent)?;
            write_json(
                pipe,
                FrameKind::Status,
                status.generation,
                &mut status_bytes,
                STATUS_PAYLOAD_BYTES,
                cancelled,
                |writer| serde_json::to_writer(writer, &status),
            )?;
            next_status = Instant::now() + STATUS_INTERVAL;
        }
        match pending.take().or_else(|| consumer.pop().ok()) {
            Some(record) => {
                if !batch.append(record, Instant::now())? {
                    // The single pending record retains its own generation across a flush.
                    pending = Some(record);
                    send_batch(pipe, &mut batch, shared, sent, cancelled)?;
                }
            }
            None => {
                let delay = batch
                    .started
                    .map(|started| {
                        (started + Duration::from_millis(BATCH_FLUSH_INTERVAL_MS))
                            .saturating_duration_since(Instant::now())
                    })
                    .unwrap_or(QUEUE_POLL_INTERVAL)
                    .min(QUEUE_POLL_INTERVAL);
                if !delay.is_zero() {
                    thread::sleep(delay);
                }
            }
        }
    }
    Ok(())
}

fn input_frequency() -> io::Result<u64> {
    #[cfg(windows)]
    {
        let mut frequency = 0;
        if unsafe {
            windows_sys::Win32::System::Performance::QueryPerformanceFrequency(&mut frequency)
        } == 0
            || frequency <= 0
        {
            return Err(io::Error::other("input clock unavailable"));
        }
        Ok(frequency as u64)
    }
    #[cfg(not(windows))]
    {
        Ok(REPLAY_FREQUENCY)
    }
}

fn capture_status(shared: &CaptureShared, sent: &Mutex<SentProgress>) -> io::Result<CaptureStatus> {
    let mut progress = sent
        .lock()
        .map_err(|_| io::Error::other("sent watermark lock is poisoned"))?;
    let generation = shared.generation.load(Ordering::Acquire);
    let sent = progress.current(generation);
    let overflow = shared.overflow.load(Ordering::Acquire);
    let disconnected = shared.disconnected.load(Ordering::Acquire);
    Ok(CaptureStatus {
        generation,
        captured: shared.captured.load(Ordering::Acquire),
        queued: shared.queued.load(Ordering::Acquire),
        sent,
        overflow,
        queue_high_watermark: shared.high_watermark.load(Ordering::Acquire),
        active: shared.active.load(Ordering::Acquire) && overflow == 0 && !disconnected,
        disconnected,
    })
}

struct BoundedWriter<'a> {
    bytes: &'a mut Vec<u8>,
    limit: usize,
}

impl Write for BoundedWriter<'_> {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if bytes.len() > self.limit.saturating_sub(self.bytes.len()) {
            return Err(invalid("JSON frame exceeds its reserved payload capacity"));
        }
        self.bytes.extend_from_slice(bytes);
        Ok(bytes.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

fn write_json(
    pipe: &Pipe,
    kind: FrameKind,
    generation: u64,
    bytes: &mut Vec<u8>,
    limit: usize,
    cancelled: &AtomicBool,
    serialize: impl FnOnce(BoundedWriter<'_>) -> serde_json::Result<()>,
) -> io::Result<()> {
    bytes.clear();
    serialize(BoundedWriter { bytes, limit }).map_err(io::Error::other)?;
    let header = FrameHeader {
        kind,
        generation,
        payload_bytes: bytes.len(),
        record_count: 0,
    }
    .encode()
    .map_err(io::Error::other)?;
    let deadline = Instant::now() + IO_TIMEOUT;
    pipe.write_all(&header, deadline, cancelled)?;
    pipe.write_all(bytes, deadline, cancelled)
}

fn control_loop(
    pipe: &Pipe,
    source: &mut InputSource,
    shared: &CaptureShared,
    devices: &Mutex<Vec<DeviceDescriptor>>,
    sent: &Mutex<SentProgress>,
    cancelled: &AtomicBool,
) -> io::Result<()> {
    let mut input_bytes = vec![0; MAX_FRAME_PAYLOAD_BYTES];
    let mut output_bytes = Vec::with_capacity(MAX_FRAME_PAYLOAD_BYTES);
    let mut command_state = CommandState::new(source)?;
    while !cancelled.load(Ordering::Acquire) {
        let mut header_bytes = [0; FRAME_HEADER_BYTES];
        let deadline = Instant::now() + CONTROL_IDLE_TIMEOUT;
        pipe.read_exact(&mut header_bytes, deadline, cancelled)?;
        let header = FrameHeader::decode(&header_bytes).map_err(io::Error::other)?;
        if header.kind != FrameKind::Command {
            return Err(invalid("control pipe expects a command frame"));
        }
        pipe.read_exact(
            &mut input_bytes[..header.payload_bytes],
            deadline,
            cancelled,
        )?;
        let command: ControlCommand = serde_json::from_slice(&input_bytes[..header.payload_bytes])
            .map_err(io::Error::other)?;
        if command.generation() != header.generation {
            return Err(invalid(
                "control header and command generations do not match",
            ));
        }
        let shutdown = matches!(command, ControlCommand::Shutdown);
        let (acknowledgement, fatal) =
            command_state.execute(command, source, shared, devices, None);
        if acknowledgement.error.is_none() {
            sent.lock()
                .map_err(|_| io::Error::other("sent watermark lock is poisoned"))?
                .current(acknowledgement.generation);
        }
        write_json(
            pipe,
            FrameKind::Ack,
            acknowledgement.generation,
            &mut output_bytes,
            MAX_FRAME_PAYLOAD_BYTES,
            cancelled,
            |writer| serde_json::to_writer(writer, &acknowledgement),
        )?;
        if let Some(error) = fatal {
            return Err(error);
        }
        if shutdown && source.shutdown {
            return Ok(());
        }
    }
    Err(io::Error::new(
        io::ErrorKind::Interrupted,
        "capture control was cancelled",
    ))
}

pub(crate) struct CommandState {
    last: InputAck,
    has_session: bool,
    metadata_bytes: Vec<u8>,
}

impl CommandState {
    pub(crate) fn motion_armed(&mut self, ack: InputAck, capture: bool) {
        if capture {
            self.last = ack;
            self.has_session = true;
        }
    }
    pub(crate) fn new(source: &InputSource) -> io::Result<Self> {
        let (ticks, frequency) = source.clock()?;
        Ok(Self {
            last: InputAck {
                cutoff: 0,
                ticks,
                frequency,
                generation: 0,
            },
            has_session: false,
            metadata_bytes: Vec::with_capacity(MAX_FRAME_PAYLOAD_BYTES),
        })
    }

    pub(crate) fn execute(
        &mut self,
        command: ControlCommand,
        source: &mut InputSource,
        shared: &CaptureShared,
        devices: &Mutex<Vec<DeviceDescriptor>>,
        permit: Option<u64>,
    ) -> (CaptureAck, Option<io::Error>) {
        let tag = command.tag();
        let mut directory = Vec::new();
        let result = (|| {
            match &command {
                ControlCommand::Discover
                | ControlCommand::RescanDevices
                | ControlCommand::Start { .. } => {
                    let input = if matches!(command, ControlCommand::RescanDevices) {
                        InputCommand::RescanDevices
                    } else {
                        InputCommand::Discover
                    };
                    source.command(input, None)?;
                    directory = device_directory(devices)?;
                    // Check the entire metadata reply before opening the input entrance.
                    self.metadata_bytes.clear();
                    let metadata_reply = CaptureAck {
                        command: tag,
                        generation: u64::MAX,
                        cutoff: u64::MAX,
                        queued: u64::MAX,
                        ticks: u64::MAX,
                        timestamp_frequency: u64::MAX,
                        complete: false,
                        overflow: u64::MAX,
                        queue_high_watermark: CAPTURE_QUEUE_RECORDS,
                        devices: std::mem::take(&mut directory),
                        error: None,
                    };
                    let validation = serde_json::to_writer(
                        BoundedWriter {
                            bytes: &mut self.metadata_bytes,
                            limit: MAX_FRAME_PAYLOAD_BYTES,
                        },
                        &metadata_reply,
                    )
                    .map_err(|_| invalid("device directory exceeds the control frame byte limit"));
                    directory = metadata_reply.devices;
                    validation?;
                }
                _ => {}
            }
            let acknowledgement = match command {
                ControlCommand::Discover | ControlCommand::RescanDevices => return Ok(None),
                ControlCommand::Start {
                    generation,
                    device_keys,
                    source: requested_source,
                    start_fresh,
                } => {
                    if !source.accepts(&requested_source) {
                        return Err(invalid("requested source does not match this worker mode"));
                    }
                    validate_selection(&device_keys, &directory)?;
                    let current = shared.generation.load(Ordering::Acquire);
                    if generation == 0
                        || (start_fresh
                            && (generation < current
                                || (generation == current && self.has_session)))
                        || (!start_fresh && (generation != current || !self.has_session))
                    {
                        return Err(invalid(
                            "Start requires the correct unused or resumed generation",
                        ));
                    }
                    let replay = ReplayConfig::from_source(&requested_source)?;
                    let acknowledgement = source.command_with_permit(
                        InputCommand::Start {
                            generation,
                            device_keys,
                            start_fresh,
                        },
                        replay,
                        permit,
                    )?;
                    self.has_session = true;
                    acknowledgement
                }
                ControlCommand::Pause => source.command(InputCommand::Pause, None)?,
                ControlCommand::Resume => {
                    source.command_with_permit(InputCommand::Resume, None, permit)?
                }
                ControlCommand::Stop => source.command(InputCommand::Stop, None)?,
                ControlCommand::Reset { generation } => {
                    if generation == 0 || generation <= shared.generation.load(Ordering::Acquire) {
                        return Err(invalid("Reset requires a newer generation"));
                    }
                    let acknowledgement =
                        source.command(InputCommand::Reset { generation }, None)?;
                    self.has_session = false;
                    acknowledgement
                }
                ControlCommand::Shutdown => source.command(InputCommand::Shutdown, None)?,
            };
            Ok(Some(acknowledgement))
        })();
        let mut fatal = None;
        let error = match result {
            Ok(Some(acknowledgement)) => {
                self.last = acknowledgement;
                None
            }
            Ok(None) => None,
            Err(error) => {
                directory.clear();
                let message = error.to_string();
                if matches!(
                    error.kind(),
                    io::ErrorKind::TimedOut
                        | io::ErrorKind::BrokenPipe
                        | io::ErrorKind::Interrupted
                ) {
                    fatal = Some(error);
                }
                Some(message)
            }
        };
        let overflow = shared.overflow.load(Ordering::Acquire);
        let acknowledgement = CaptureAck {
            command: tag,
            generation: shared.generation.load(Ordering::Acquire),
            queued: shared.queued.load(Ordering::Acquire),
            cutoff: if error.is_none() {
                self.last.cutoff
            } else {
                shared.captured.load(Ordering::Acquire)
            },
            ticks: self.last.ticks,
            timestamp_frequency: self.last.frequency,
            complete: error.is_none()
                && overflow == 0
                && !shared.disconnected.load(Ordering::Acquire),
            overflow,
            queue_high_watermark: shared.high_watermark.load(Ordering::Acquire),
            devices: directory,
            error,
        };
        (acknowledgement, fatal)
    }
}

fn validate_devices(devices: &[DeviceDescriptor]) -> io::Result<()> {
    if devices.len() >= MAX_DEVICE_COUNT {
        return Err(invalid(
            "capture device directory exceeds its bounded capacity",
        ));
    }
    for (index, device) in devices.iter().enumerate() {
        if device.key == 0 || devices[..index].iter().any(|old| old.key == device.key) {
            return Err(invalid("device keys must be unique and nonzero"));
        }
        if !device.fields_are_bounded() {
            return Err(invalid("device metadata field exceeds its byte limit"));
        }
    }
    Ok(())
}

pub(crate) fn device_directory(
    devices: &Mutex<Vec<DeviceDescriptor>>,
) -> io::Result<Vec<DeviceDescriptor>> {
    let devices = devices
        .lock()
        .map_err(|_| io::Error::other("capture device directory lock is poisoned"))?;
    validate_devices(&devices)?;
    Ok(devices.clone())
}

fn validate_selection(keys: &[u32], devices: &[DeviceDescriptor]) -> io::Result<()> {
    let catalog = clicksync_core::devices::DeviceCatalog::new(devices);
    if keys.len() > MAX_DEVICE_COUNT || !catalog.accepts_selection(keys) {
        return Err(invalid(
            "Start requires available, distinct physical mouse devices",
        ));
    }
    Ok(())
}

pub(crate) struct InputSource {
    service: Service,
    shutdown: bool,
}

enum Service {
    RawInput(InputService),
    Replay(ReplayService),
}

impl InputSource {
    pub(crate) fn clock(&self) -> io::Result<(u64, u64)> {
        if matches!(self.service, Service::Replay(_)) {
            return Ok((REPLAY_START_TICKS, REPLAY_FREQUENCY));
        }
        #[cfg(windows)]
        {
            let mut frequency = 0i64;
            if unsafe {
                windows_sys::Win32::System::Performance::QueryPerformanceFrequency(&mut frequency)
            } == 0
            {
                return Err(io::Error::last_os_error());
            }
            if frequency <= 0 {
                return Err(io::Error::other(
                    "Raw Input timestamp frequency is not positive",
                ));
            }
            let mut ticks = 0i64;
            if unsafe {
                windows_sys::Win32::System::Performance::QueryPerformanceCounter(&mut ticks)
            } == 0
            {
                return Err(io::Error::last_os_error());
            }
            if ticks < 0 {
                return Err(io::Error::other("Raw Input timestamp is negative"));
            }
            Ok((ticks as u64, frequency as u64))
        }
        #[cfg(not(windows))]
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Raw Input timestamp frequency requires Windows",
        ))
    }

    pub(crate) fn spawn(
        replay: bool,
        producer: rtrb::Producer<CapturedRecord>,
        inputs: rtrb::Producer<clicksync_core::protocol::ControlInputEvent>,
        shared: Arc<CaptureShared>,
        devices: Arc<Mutex<Vec<DeviceDescriptor>>>,
    ) -> io::Result<Self> {
        let service = if replay {
            *devices
                .lock()
                .map_err(|_| io::Error::other("capture device directory lock is poisoned"))? =
                replay_devices();
            Service::Replay(ReplayService::spawn(producer, shared)?)
        } else {
            Service::RawInput(InputService::spawn_with_inputs(
                producer,
                Some(inputs),
                shared,
                devices,
            )?)
        };
        Ok(Self {
            service,
            shutdown: false,
        })
    }

    fn accepts(&self, source: &CaptureSource) -> bool {
        matches!(
            (&self.service, source),
            (Service::RawInput(_), CaptureSource::RawInput)
                | (Service::Replay(_), CaptureSource::Replay { .. })
        )
    }

    fn command(
        &mut self,
        command: InputCommand,
        replay: Option<ReplayConfig>,
    ) -> io::Result<InputAck> {
        self.command_with_permit(command, replay, None)
    }

    pub(crate) fn command_with_permit(
        &mut self,
        command: InputCommand,
        replay: Option<ReplayConfig>,
        permit: Option<u64>,
    ) -> io::Result<InputAck> {
        let shutdown = matches!(command, InputCommand::Shutdown);
        let result = match (&self.service, permit) {
            (Service::RawInput(service), None) => service.command(command),
            (Service::Replay(service), None) => service.command(command, replay),
            (Service::RawInput(service), permit) => service.command_with_permit(command, permit),
            (Service::Replay(service), permit) => {
                service.command_with_permit(command, replay, permit)
            }
        };
        if result.is_ok() && shutdown {
            self.shutdown = true;
        }
        result
    }

    pub(crate) fn shutdown(&mut self) -> io::Result<()> {
        if self.shutdown {
            Ok(())
        } else {
            self.command(InputCommand::Shutdown, None).map(|_| ())
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct ReplayConfig {
    records_per_device: u64,
    reports_per_second: u32,
    real_time: bool,
}

impl ReplayConfig {
    fn from_source(source: &CaptureSource) -> io::Result<Option<Self>> {
        match *source {
            CaptureSource::RawInput => Ok(None),
            CaptureSource::Replay {
                records_per_device,
                reports_per_second,
                real_time,
            } => {
                if records_per_device == 0
                    || records_per_device > REPLAY_RECORD_LIMIT
                    || !matches!(reports_per_second, 1000 | 8000)
                {
                    return Err(invalid(
                        "replay requires 1 kHz or 8 kHz and 1..=16000000 records",
                    ));
                }
                Ok(Some(Self {
                    records_per_device,
                    reports_per_second,
                    real_time,
                }))
            }
        }
    }
}

fn replay_devices() -> Vec<DeviceDescriptor> {
    (1..=2)
        .map(|key| DeviceDescriptor {
            key,
            metadata: SessionMetadata {
                display_name: format!("Replay mouse {key}"),
                device_id: format!("ClickSync-Replay-{key}"),
                vendor_id: None,
                product_id: None,
                button_count: 5,
                is_virtual: true,
                path_summary: format!("Replay source {key}"),
            },
            endpoint: clicksync_core::devices::EndpointDescriptor {
                source: clicksync_core::devices::DeviceSource::Replay,
                ..Default::default()
            },
        })
        .collect()
}

struct ReplayRequest {
    command: InputCommand,
    permit: Option<u64>,
    config: Option<ReplayConfig>,
    expires: Instant,
    reply: SyncSender<io::Result<InputAck>>,
}

struct ReplayService {
    commands: Option<SyncSender<ReplayRequest>>,
    cancelled: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl ReplayService {
    fn spawn(
        producer: rtrb::Producer<CapturedRecord>,
        shared: Arc<CaptureShared>,
    ) -> io::Result<Self> {
        let (commands, requests) = mpsc::sync_channel(1);
        let cancelled = Arc::new(AtomicBool::new(false));
        let thread_cancelled = Arc::clone(&cancelled);
        let thread = thread::Builder::new()
            .name("ReplayProducer".into())
            .spawn(move || replay_loop(producer, shared, requests, &thread_cancelled))?;
        Ok(Self {
            commands: Some(commands),
            cancelled,
            thread: Some(thread),
        })
    }

    fn command(&self, command: InputCommand, config: Option<ReplayConfig>) -> io::Result<InputAck> {
        self.command_with_permit(command, config, None)
    }

    fn command_with_permit(
        &self,
        command: InputCommand,
        config: Option<ReplayConfig>,
        permit: Option<u64>,
    ) -> io::Result<InputAck> {
        if self.cancelled.load(Ordering::Acquire) {
            return Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "replay producer has stopped",
            ));
        }
        let (reply, acknowledgement) = mpsc::sync_channel(1);
        let expires = Instant::now() + INPUT_COMMAND_TIMEOUT;
        self.commands
            .as_ref()
            .ok_or_else(|| io::Error::new(io::ErrorKind::BrokenPipe, "replay control is closed"))?
            .try_send(ReplayRequest {
                command,
                permit,
                config,
                expires,
                reply,
            })
            .map_err(|error| match error {
                TrySendError::Full(_) => {
                    io::Error::new(io::ErrorKind::WouldBlock, "replay control queue is full")
                }
                TrySendError::Disconnected(_) => {
                    io::Error::new(io::ErrorKind::BrokenPipe, "replay producer exited")
                }
            })?;
        acknowledgement
            .recv_timeout(expires.saturating_duration_since(Instant::now()))
            .map_err(|error| {
                self.cancelled.store(true, Ordering::Release);
                io::Error::new(
                    match error {
                        mpsc::RecvTimeoutError::Timeout => io::ErrorKind::TimedOut,
                        mpsc::RecvTimeoutError::Disconnected => io::ErrorKind::BrokenPipe,
                    },
                    "replay command did not complete",
                )
            })?
    }
}

impl Drop for ReplayService {
    fn drop(&mut self) {
        self.cancelled.store(true, Ordering::Release);
        self.commands.take();
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

struct ReplayState {
    producer: rtrb::Producer<CapturedRecord>,
    shared: Arc<CaptureShared>,
    config: Option<ReplayConfig>,
    device_keys: [u32; 2],
    device_count: usize,
    device_slot: usize,
    index: u64,
    generation: u64,
    sequence: u64,
    capture_sequence: u64,
    timing_sequences: [u64; 2],
    ticks: u64,
    has_session: bool,
    resumable: bool,
    schedule_origin: Instant,
    schedule_index: u64,
    observation: Option<crate::observation::ArmedMotion>,
}

impl ReplayState {
    fn new(producer: rtrb::Producer<CapturedRecord>, shared: Arc<CaptureShared>) -> Self {
        Self {
            producer,
            shared,
            config: None,
            device_keys: [0; 2],
            device_count: 0,
            device_slot: 0,
            index: 0,
            generation: 0,
            sequence: 0,
            capture_sequence: 0,
            timing_sequences: [0; 2],
            ticks: REPLAY_START_TICKS,
            has_session: false,
            resumable: false,
            schedule_origin: Instant::now(),
            schedule_index: 0,
            observation: None,
        }
    }

    fn complete(&self) -> bool {
        self.shared.overflow.load(Ordering::Acquire) == 0
            && !self.shared.disconnected.load(Ordering::Acquire)
    }

    fn seal(&self) {
        self.shared.active.store(false, Ordering::Release);
    }

    fn can_resume(&self) -> bool {
        self.has_session
            && self.resumable
            && !self.shared.active.load(Ordering::Acquire)
            && self.complete()
    }

    fn clear(&mut self, generation: u64) {
        self.seal();
        self.generation = generation;
        self.sequence = 0;
        self.index = 0;
        self.device_slot = 0;
        self.ticks = REPLAY_START_TICKS;
        self.shared.captured.store(0, Ordering::Relaxed);
        self.shared.queued.store(0, Ordering::Relaxed);
        self.shared.overflow.store(0, Ordering::Relaxed);
        self.shared.high_watermark.store(0, Ordering::Relaxed);
        self.shared.disconnected.store(false, Ordering::Release);
        self.shared.generation.store(generation, Ordering::Release);
    }

    fn rebase_schedule(&mut self) {
        self.schedule_origin = Instant::now();
        self.schedule_index = self.index;
    }

    fn apply(
        &mut self,
        command: InputCommand,
        config: Option<ReplayConfig>,
    ) -> io::Result<InputAck> {
        let is_motion = matches!(
            command,
            InputCommand::Discover
                | InputCommand::RescanDevices
                | InputCommand::Observe { .. }
                | InputCommand::PollMotion
        );
        #[cfg(feature = "controlled-input")]
        let is_motion = is_motion || matches!(command, InputCommand::ReplayMotion(_));
        if !is_motion {
            self.shared.motion.cancel_current();
            self.observation = None;
        }
        match command {
            InputCommand::Discover | InputCommand::RescanDevices => {}
            InputCommand::PollMotion => {
                if self
                    .observation
                    .as_ref()
                    .is_some_and(|motion| motion.expire(&self.shared))
                {
                    self.observation = None;
                }
            }
            InputCommand::Observe { request, ticket } => {
                if self.shared.is_active()
                    || !self.shared.motion.is_current(request.id)
                    || !self.shared.permit.allows(ticket)
                    || request.excluded.is_some_and(|key| !matches!(key, 1 | 2))
                {
                    return Err(invalid(
                        "motion observation requires an idle permitted replay source",
                    ));
                }
                if let Some(generation) = request.capture_generation {
                    if generation <= self.generation {
                        return Err(invalid("motion capture requires a new generation"));
                    }
                    self.clear(generation);
                    self.config = None;
                    self.device_count = 0;
                    self.has_session = true;
                    self.resumable = true;
                    self.shared.authorize(ticket);
                }
                self.observation = Some(crate::observation::ArmedMotion::new(
                    request,
                    ticket,
                    &replay_devices(),
                ));
            }
            #[cfg(feature = "controlled-input")]
            InputCommand::ReplayMotion(reports) => {
                if reports.len() > clicksync_core::protocol::CAPTURE_QUEUE_RECORDS {
                    return Err(invalid(
                        "controlled motion batch exceeds the capture queue bound",
                    ));
                }
                self.config = None;
                for report in reports {
                    self.motion_report(report.device_key, report.record);
                }
            }
            InputCommand::Start {
                generation,
                device_keys,
                start_fresh,
            } => {
                let config =
                    config.ok_or_else(|| invalid("replay Start requires a replay source"))?;
                if device_keys.is_empty()
                    || device_keys.len() > 2
                    || device_keys.iter().any(|key| !matches!(key, 1 | 2))
                    || (device_keys.len() == 2 && device_keys[0] == device_keys[1])
                    || generation == 0
                {
                    return Err(invalid(
                        "replay requires one or two distinct replay device keys",
                    ));
                }
                if start_fresh {
                    if generation < self.generation
                        || (generation == self.generation && self.has_session)
                    {
                        return Err(invalid("fresh replay Start requires an unused generation"));
                    }
                } else if !self.can_resume()
                    || generation != self.generation
                    || Some(config) != self.config
                    || device_keys != self.device_keys[..self.device_count]
                {
                    return Err(invalid(
                        "continuing replay Start must preserve its source and session",
                    ));
                }
                if start_fresh {
                    self.clear(generation);
                    self.config = Some(config);
                    self.device_keys[..device_keys.len()].copy_from_slice(&device_keys);
                    self.device_count = device_keys.len();
                }
                self.has_session = true;
                self.resumable = true;
                self.rebase_schedule();
                self.shared.active.store(true, Ordering::Release);
            }
            InputCommand::Pause => self.seal(),
            InputCommand::Resume => {
                if !self.can_resume() {
                    return Err(invalid("there is no complete replay session to resume"));
                }
                self.rebase_schedule();
                self.shared.active.store(true, Ordering::Release);
            }
            InputCommand::Stop | InputCommand::Shutdown => {
                self.seal();
                self.resumable = false;
            }
            InputCommand::Reset { generation } => {
                if generation == 0 || generation <= self.generation {
                    return Err(invalid("replay Reset requires a newer generation"));
                }
                self.clear(generation);
                self.has_session = false;
                self.resumable = false;
                self.config = None;
                self.device_count = 0;
            }
        }
        Ok(InputAck {
            cutoff: self.sequence,
            ticks: self.ticks,
            frequency: REPLAY_FREQUENCY,
            generation: self.generation,
        })
    }

    fn due_in(&self) -> Duration {
        match self.config {
            Some(config) if config.real_time => {
                let due = self.schedule_origin
                    + Duration::from_nanos(
                        (self.index - self.schedule_index) * 1_000_000_000
                            / u64::from(config.reports_per_second),
                    );
                due.saturating_duration_since(Instant::now())
            }
            _ => Duration::ZERO,
        }
    }

    fn produce(&mut self) {
        let Some(config) = self.config else {
            // An armed business capture is driven by motion reports, not the
            // standard generated replay workload.
            return;
        };
        if self.index >= config.records_per_device {
            self.seal();
            return;
        }
        self.sequence += 1;
        self.capture_sequence += 1;
        let device_key = self.device_keys[self.device_slot];
        let timing_sequence = &mut self.timing_sequences[device_key as usize - 1];
        *timing_sequence += 1;
        self.ticks = REPLAY_START_TICKS
            + self.index * REPLAY_FREQUENCY / u64::from(config.reports_per_second);
        let captured = CapturedRecord {
            generation: self.generation,
            device_key,
            session_sequence: self.sequence,
            record: RawMouseRecord::new(
                (self.index % 7) as i32 - 3,
                (self.index % 5) as i32 - 2,
                self.ticks,
                self.capture_sequence,
                *timing_sequence,
                RawMouseMovementMode::Relative,
            ),
        };
        self.shared.captured.store(self.sequence, Ordering::Release);
        if self.producer.push(captured).is_err() {
            self.shared.overflow.fetch_add(1, Ordering::Release);
            self.resumable = false;
            self.seal();
            self.shared.wake.notify();
            return;
        }
        self.shared.queued.fetch_add(1, Ordering::Relaxed);
        self.shared.wake.notify();
        let depth = self.producer.buffer().capacity() - self.producer.slots();
        self.shared
            .high_watermark
            .fetch_max(depth, Ordering::Relaxed);
        self.device_slot += 1;
        if self.device_slot == self.device_count {
            self.device_slot = 0;
            self.index += 1;
        }
        if self.index == config.records_per_device {
            self.seal();
        }
    }

    #[cfg(feature = "controlled-input")]
    fn motion_report(&mut self, key: u32, mut record: RawMouseRecord) {
        self.capture_sequence += 1;
        if !matches!(key, 1 | 2) {
            return;
        }
        self.timing_sequences[key as usize - 1] += 1;
        record.capture_sequence = self.capture_sequence;
        record.timing_sequence = self.timing_sequences[key as usize - 1];
        self.ticks = record.raw_capture_ticks;
        let mut selected = None;
        if let Some(observation) = &self.observation {
            if observation.expire(&self.shared) {
                self.observation = None;
            } else if let Some(candidate) = observation.offer(&self.shared, key, &record) {
                if observation.request.capture_generation.is_some() {
                    self.device_keys[0] = key;
                    self.device_count = 1;
                    self.shared.active.store(true, Ordering::Release);
                }
                self.observation = None;
                if !self.shared.motion.publish(
                    candidate.request,
                    crate::observation::MotionResult::Selected(candidate),
                ) {
                    self.seal();
                    return;
                }
                selected = Some(candidate);
            }
        }
        if self.shared.is_active() && self.device_keys[..self.device_count].contains(&key) {
            self.sequence += 1;
            let captured = CapturedRecord {
                generation: self.generation,
                device_key: key,
                session_sequence: self.sequence,
                record,
            };
            self.shared.captured.store(self.sequence, Ordering::Release);
            if self.producer.push(captured).is_err() {
                self.shared.overflow.fetch_add(1, Ordering::Release);
                self.resumable = false;
                self.seal();
            } else {
                self.shared.queued.fetch_add(1, Ordering::Relaxed);
                let depth = self.producer.buffer().capacity() - self.producer.slots();
                self.shared
                    .high_watermark
                    .fetch_max(depth, Ordering::Relaxed);
            }
            self.shared.wake.notify();
        }
        if let Some(candidate) = selected {
            let _ = candidate;
            self.shared.wake.notify();
        }
    }
}

fn replay_loop(
    producer: rtrb::Producer<CapturedRecord>,
    shared: Arc<CaptureShared>,
    requests: Receiver<ReplayRequest>,
    cancelled: &AtomicBool,
) {
    let mut state = ReplayState::new(producer, shared);
    while !cancelled.load(Ordering::Acquire) {
        if state.shared.watermark.requested() {
            state
                .shared
                .watermark
                .publish(crate::watermark::Checkpoint {
                    generation: state.generation,
                    sequence: state.sequence,
                    capture_sequence: state.capture_sequence,
                    ticks: state.ticks,
                    frequency: REPLAY_FREQUENCY,
                    active: state.shared.is_active(),
                    complete: state.complete(),
                    dropped_packets: state.shared.overflow.load(Ordering::Acquire),
                    observed: Instant::now(),
                });
        }
        let request = match requests.try_recv() {
            Ok(request) => Some(request),
            Err(TryRecvError::Disconnected) => break,
            Err(TryRecvError::Empty) => {
                let active = state.shared.is_active() && state.config.is_some();
                let delay = if active {
                    state.due_in()
                } else {
                    STATUS_INTERVAL
                };
                if active && delay.is_zero() {
                    state.produce();
                    continue;
                }
                match requests.recv_timeout(delay.min(STATUS_INTERVAL)) {
                    Ok(request) => Some(request),
                    Err(mpsc::RecvTimeoutError::Timeout) => None,
                    Err(mpsc::RecvTimeoutError::Disconnected) => break,
                }
            }
        };
        if let Some(request) = request {
            let shutdown = matches!(request.command, InputCommand::Shutdown);
            if cancelled.load(Ordering::Acquire) || Instant::now() >= request.expires {
                state.seal();
                let _ = request.reply.try_send(Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "replay command expired before dispatch",
                )));
                break;
            }
            let activating = matches!(
                request.command,
                InputCommand::Start { .. } | InputCommand::Resume
            );
            let acknowledgement = if activating
                && request
                    .permit
                    .is_some_and(|ticket| !state.shared.permit.allows(ticket))
            {
                Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    "capture permission expired before replay-thread activation",
                ))
            } else {
                let result = state.apply(request.command, request.config);
                if result.is_ok()
                    && activating
                    && let Some(ticket) = request.permit
                {
                    state.shared.authorize(ticket);
                }
                result
            };
            let _ = request.reply.try_send(acknowledgement);
            if shutdown {
                break;
            }
        }
    }
    state.seal();
}

#[cfg(test)]
mod tests {
    use super::*;
    use clicksync_core::protocol::replay_record;

    fn configuration(records_per_device: u64, reports_per_second: u32) -> ReplayConfig {
        ReplayConfig {
            records_per_device,
            reports_per_second,
            real_time: false,
        }
    }

    fn replay(capacity: usize) -> (ReplayState, rtrb::Consumer<CapturedRecord>) {
        let (producer, consumer) = rtrb::RingBuffer::new(capacity);
        (
            ReplayState::new(producer, Arc::new(CaptureShared::default())),
            consumer,
        )
    }

    fn start(generation: u64, keys: &[u32]) -> InputCommand {
        InputCommand::Start {
            generation,
            device_keys: keys.to_vec(),
            start_fresh: true,
        }
    }

    #[test]
    fn cli_requires_distinct_local_pipes_and_a_parent() {
        let arguments = [
            "--data",
            r"\\.\pipe\ClickSync-test-data",
            "--control",
            r"\\.\pipe\ClickSync-test-control",
            "--parent",
            "123",
            "--replay",
        ];
        let parsed = Arguments::parse(arguments.map(OsString::from)).unwrap();
        assert_eq!(parsed.parent, 123);
        assert!(parsed.replay);
        for name in [
            r"\\remote\pipe\ClickSync-test",
            r"\\.\pipe\Other-test",
            r"\\.\pipe\ClickSync-",
            r"\\.\pipe\ClickSync-test\nested",
        ] {
            assert!(validate_pipe_name(name).is_err());
        }
        for arguments in [
            vec![],
            vec!["--replay", "--replay"],
            vec!["--data", r"\\.\pipe\ClickSync-data", "--data", "duplicate"],
            vec![
                "--data",
                r"\\.\pipe\ClickSync-data",
                "--control",
                r"\\.\pipe\ClickSync-data",
                "--parent",
                "123",
            ],
            vec![
                "--data",
                r"\\.\pipe\ClickSync-data",
                "--control",
                r"\\.\pipe\ClickSync-control",
                "--parent",
                "0",
            ],
        ] {
            assert!(Arguments::parse(arguments.into_iter().map(OsString::from)).is_err());
        }
    }

    #[test]
    fn batches_are_bounded_and_flush_low_rate_input_after_four_ms() {
        let mut batch = Batch::new();
        let now = Instant::now();
        assert!(!batch.due(now));
        assert!(batch.append(replay_record(0, 1, 1), now).unwrap());
        assert!(!batch.due(now + Duration::from_millis(3)));
        assert!(batch.due(now + Duration::from_millis(4)));
        for index in 1..MAX_BATCH_RECORDS {
            assert!(
                batch
                    .append(replay_record(index as u64, 1, 1), now)
                    .unwrap()
            );
        }
        assert!(batch.due(now));
        assert!(!batch.append(replay_record(256, 1, 1), now).unwrap());
        assert_eq!(batch.count, MAX_BATCH_RECORDS);
        for index in 0..MAX_BATCH_RECORDS {
            let offset = index * WIRE_RECORD_BYTES;
            assert_eq!(
                CapturedRecord::decode(
                    batch.generation,
                    batch.bytes[offset..offset + WIRE_RECORD_BYTES]
                        .try_into()
                        .unwrap(),
                )
                .unwrap(),
                replay_record(index as u64, 1, 1)
            );
        }
    }

    #[test]
    fn generation_boundary_leaves_the_pending_record_unchanged() {
        let mut batch = Batch::new();
        let now = Instant::now();
        let old = replay_record(0, 17, 1);
        let pending = replay_record(0, 18, 2);
        assert!(batch.append(old, now).unwrap());
        assert!(!batch.append(pending, now).unwrap());
        assert_eq!((batch.generation, batch.count), (17, 1));
        batch.clear();
        assert!(batch.append(pending, now).unwrap());
        assert_eq!((batch.generation, batch.count, batch.cutoff), (18, 1, 1));
        assert_eq!(
            CapturedRecord::decode(18, batch.bytes[..WIRE_RECORD_BYTES].try_into().unwrap())
                .unwrap(),
            pending
        );
    }

    #[test]
    fn old_send_completion_does_not_update_the_current_generation_watermark() {
        let shared = CaptureShared::default();
        let mut sent = SentProgress::default();
        shared.generation.store(1, Ordering::Release);
        sent.complete(1, 100, &shared);
        assert_eq!(sent.current(1), 100);
        shared.generation.store(2, Ordering::Release);
        sent.complete(1, 200, &shared);
        assert_eq!(sent.current(2), 0);
        sent.complete(2, 3, &shared);
        sent.complete(1, 500, &shared);
        sent.complete(2, 2, &shared);
        assert_eq!(sent.current(2), 3);
    }

    #[test]
    fn status_uses_the_current_watermark_and_never_marks_overflow_or_disconnect_active() {
        let shared = CaptureShared::default();
        let sent = Mutex::new(SentProgress::default());
        shared.generation.store(2, Ordering::Release);
        shared.active.store(true, Ordering::Release);
        sent.lock().unwrap().complete(2, 7, &shared);
        let status = capture_status(&shared, &sent).unwrap();
        assert_eq!((status.generation, status.sent), (2, 7));
        assert!(status.active);
        shared.overflow.store(1, Ordering::Release);
        let status = capture_status(&shared, &sent).unwrap();
        assert!(!status.active);
        shared.overflow.store(0, Ordering::Release);
        shared.disconnected.store(true, Ordering::Release);
        assert!(!capture_status(&shared, &sent).unwrap().active);
    }

    #[test]
    fn metadata_and_selection_validation_reject_unbounded_or_ambiguous_inputs() {
        let mut devices = replay_devices();
        validate_devices(&devices).unwrap();
        for keys in [vec![], vec![0], vec![1, 1], vec![3]] {
            assert!(validate_selection(&keys, &devices).is_err());
        }
        validate_selection(&[1, 2], &devices).unwrap();
        devices[0].endpoint.physical_device_key = "container:one".into();
        devices[1].endpoint.physical_device_key = "container:one".into();
        assert!(validate_selection(&[1, 2], &devices).is_err());
        devices[1].endpoint.physical_device_key = "container:two".into();
        validate_selection(&[1, 2], &devices).unwrap();
        devices[0].endpoint.source = clicksync_core::devices::DeviceSource::RawInput;
        assert!(validate_selection(&[1], &devices).is_err());
        devices[0].endpoint.source = clicksync_core::devices::DeviceSource::Replay;
        devices[0].endpoint.physical_device_key = "x".repeat(DEVICE_FIELD_BYTES + 1);
        assert!(validate_devices(&devices).is_err());
        devices[0].endpoint.physical_device_key.clear();
        devices[0].metadata.device_id = "x".repeat(DEVICE_FIELD_BYTES + 1);
        assert!(validate_devices(&devices).is_err());
        let devices = (1..=MAX_DEVICE_COUNT)
            .map(|key| DeviceDescriptor {
                key: key as u32,
                metadata: SessionMetadata::default(),
                endpoint: Default::default(),
            })
            .collect::<Vec<_>>();
        assert!(validate_devices(&devices).is_err());
        let mut bytes = Vec::with_capacity(8);
        assert!(
            serde_json::to_writer(
                BoundedWriter {
                    bytes: &mut bytes,
                    limit: 8,
                },
                &"123456789",
            )
            .is_err()
        );
        assert!(bytes.len() <= 8);
        assert_eq!(bytes.capacity(), 8);
    }

    #[test]
    fn replay_source_limits_are_checked_on_the_cold_path() {
        for (records_per_device, reports_per_second) in [
            (0, 1000),
            (REPLAY_RECORD_LIMIT + 1, 1000),
            (1, 0),
            (1, 2000),
        ] {
            assert!(
                ReplayConfig::from_source(&CaptureSource::Replay {
                    records_per_device,
                    reports_per_second,
                    real_time: true,
                })
                .is_err()
            );
        }
        for reports_per_second in [1000, 8000] {
            assert!(
                ReplayConfig::from_source(&CaptureSource::Replay {
                    records_per_device: REPLAY_RECORD_LIMIT,
                    reports_per_second,
                    real_time: true,
                })
                .unwrap()
                .is_some()
            );
        }
    }

    #[test]
    fn replay_single_and_dual_device_1k_and_8k_records_have_exact_integer_semantics() {
        for rate in [1000, 8000] {
            for keys in [vec![1], vec![1, 2], vec![2, 1]] {
                let (mut state, mut consumer) = replay(256);
                let acknowledgement = state
                    .apply(start(7, &keys), Some(configuration(10, rate)))
                    .unwrap();
                assert_eq!(acknowledgement.frequency, REPLAY_FREQUENCY);
                assert_eq!(acknowledgement.ticks, REPLAY_START_TICKS);
                assert_eq!(acknowledgement.cutoff, 0);
                while state.shared.active.load(Ordering::Acquire) {
                    state.produce();
                }
                for index in 0..10u64 {
                    for (slot, key) in keys.iter().enumerate() {
                        let captured = consumer.pop().unwrap();
                        assert_eq!(captured.generation, 7);
                        assert_eq!(captured.device_key, *key);
                        assert_eq!(captured.record.timing_sequence, index + 1);
                        assert_eq!(
                            captured.record.capture_sequence,
                            index * keys.len() as u64 + slot as u64 + 1
                        );
                        assert_eq!(
                            captured.record.raw_capture_ticks,
                            REPLAY_START_TICKS + index * REPLAY_FREQUENCY / u64::from(rate)
                        );
                        assert_eq!(captured.record.raw_delta_x, (index % 7) as i32 - 3);
                        assert_eq!(captured.record.raw_delta_y, (index % 5) as i32 - 2);
                    }
                }
                assert!(consumer.pop().is_err());
                assert_eq!(state.shared.overflow.load(Ordering::Acquire), 0);
                assert_eq!(
                    state.shared.queued.load(Ordering::Acquire),
                    10 * keys.len() as u64
                );
            }
        }
    }

    #[test]
    fn replay_pause_stop_and_reset_keep_prefix_and_generation_boundaries() {
        let (mut state, mut consumer) = replay(16);
        state
            .apply(start(1, &[1, 2]), Some(configuration(10, 8000)))
            .unwrap();
        state.produce();
        state.produce();
        let paused = state.apply(InputCommand::Pause, None).unwrap();
        assert_eq!((paused.generation, paused.cutoff), (1, 2));
        assert!(!state.shared.active.load(Ordering::Acquire));
        state.apply(InputCommand::Resume, None).unwrap();
        state.produce();
        let stopped = state.apply(InputCommand::Stop, None).unwrap();
        assert_eq!((stopped.generation, stopped.cutoff), (1, 3));
        assert!(state.apply(InputCommand::Resume, None).is_err());
        state
            .apply(InputCommand::Reset { generation: 2 }, None)
            .unwrap();
        state
            .apply(start(2, &[2]), Some(configuration(1, 1000)))
            .unwrap();
        state.produce();
        let records = (0..4).map(|_| consumer.pop().unwrap()).collect::<Vec<_>>();
        assert_eq!(
            records
                .iter()
                .map(|record| record.generation)
                .collect::<Vec<_>>(),
            [1, 1, 1, 2]
        );
        assert_eq!(
            records
                .iter()
                .map(|record| record.record.capture_sequence)
                .collect::<Vec<_>>(),
            [1, 2, 3, 4]
        );
        assert_eq!(
            records
                .iter()
                .map(|record| record.record.timing_sequence)
                .collect::<Vec<_>>(),
            [1, 1, 2, 2]
        );
        assert_eq!(
            records
                .iter()
                .map(|record| record.session_sequence)
                .collect::<Vec<_>>(),
            [1, 2, 3, 1]
        );
        assert!(consumer.pop().is_err());
        assert!(
            state
                .apply(InputCommand::Reset { generation: 2 }, None)
                .is_err()
        );
        assert!(
            state
                .apply(start(2, &[2]), Some(configuration(1, 1000)))
                .is_err()
        );
    }

    #[test]
    fn replay_overflow_seals_input_and_preserves_only_the_accepted_prefix() {
        let (mut state, mut consumer) = replay(3);
        state
            .apply(start(1, &[1, 2]), Some(configuration(10, 8000)))
            .unwrap();
        while state.shared.active.load(Ordering::Acquire) {
            state.produce();
        }
        assert_eq!(state.shared.captured.load(Ordering::Acquire), 4);
        assert_eq!(state.shared.queued.load(Ordering::Acquire), 3);
        assert_eq!(state.shared.high_watermark.load(Ordering::Acquire), 3);
        assert_eq!(state.shared.overflow.load(Ordering::Acquire), 1);
        assert!(!state.complete());
        assert_eq!(state.apply(InputCommand::Pause, None).unwrap().cutoff, 4);
        assert!(state.apply(InputCommand::Resume, None).is_err());
        for sequence in 1..=3 {
            assert_eq!(consumer.pop().unwrap().record.capture_sequence, sequence);
        }
        assert!(consumer.pop().is_err());
    }

    #[test]
    fn replay_steady_production_has_no_heap_allocations() {
        let (mut state, mut consumer) = replay(CAPTURE_QUEUE_RECORDS);
        state
            .apply(start(1, &[1, 2]), Some(configuration(100_000, 8000)))
            .unwrap();
        let allocations = allocation_counter::measure(|| {
            for sequence in 1..=200_000 {
                state.produce();
                assert_eq!(consumer.pop().unwrap().record.capture_sequence, sequence);
            }
        });
        assert_eq!(allocations.count_total, 0);
        assert_eq!(allocations.bytes_total, 0);
        assert_eq!(state.shared.overflow.load(Ordering::Acquire), 0);
    }

    #[test]
    fn replay_thread_accepts_control_while_waiting_for_real_time_schedule() {
        let (producer, mut consumer) = rtrb::RingBuffer::new(CAPTURE_QUEUE_RECORDS);
        let shared = Arc::new(CaptureShared::default());
        let service = ReplayService::spawn(producer, Arc::clone(&shared)).unwrap();
        let mut config = configuration(1000, 1000);
        config.real_time = true;
        let started = service.command(start(1, &[1, 2]), Some(config)).unwrap();
        assert_eq!(
            (started.ticks, started.frequency),
            (REPLAY_START_TICKS, REPLAY_FREQUENCY)
        );
        let deadline = Instant::now() + Duration::from_secs(2);
        while shared.queued.load(Ordering::Acquire) == 0 {
            assert!(Instant::now() < deadline, "replay did not start");
            thread::sleep(Duration::from_millis(1));
        }
        let before = Instant::now();
        let paused = service.command(InputCommand::Pause, None).unwrap();
        assert!(before.elapsed() < Duration::from_secs(2));
        assert!(!shared.active.load(Ordering::Acquire));
        thread::sleep(Duration::from_millis(5));
        assert_eq!(shared.captured.load(Ordering::Acquire), paused.cutoff);
        for sequence in 1..=paused.cutoff {
            assert_eq!(consumer.pop().unwrap().record.capture_sequence, sequence);
        }
        assert!(consumer.pop().is_err());
        let resumed = service.command(InputCommand::Resume, None).unwrap();
        assert_eq!(resumed.generation, paused.generation);
        service.command(InputCommand::Stop, None).unwrap();
        assert!(service.command(InputCommand::Resume, None).is_err());
        service.command(InputCommand::Shutdown, None).unwrap();
        drop(service);
        assert!(!shared.active.load(Ordering::Acquire));
    }

    #[test]
    fn runtime_transport_reservation_is_bounded_and_covers_the_ring_and_staging() {
        let bytes = reserved_transport_bytes();
        assert!(bytes >= CAPTURE_QUEUE_RECORDS * size_of::<CapturedRecord>());
        assert!(bytes < 2 * 1024 * 1024);
        eprintln!("worker fixed transport reservation: {bytes} bytes");
    }

    #[cfg(windows)]
    mod pipes {
        use super::*;
        use std::sync::atomic::AtomicU64;

        static PIPE_SEQUENCE: AtomicU64 = AtomicU64::new(0);

        #[test]
        fn control_input_batches_keep_clock_order_and_independent_mouse_sequence() {
            use clicksync_core::protocol::{ControlInput, ControlInputBatch, ControlInputEvent};
            let id = PIPE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
            let name = format!(
                "{PIPE_PREFIX}control-input-send-{}-{id}",
                std::process::id()
            );
            let server = Pipe::server(&name).unwrap();
            let (mut producer, consumer) = rtrb::RingBuffer::new(4);
            let (mut input_producer, input_consumer) = rtrb::RingBuffer::new(256);
            let shared = Arc::new(CaptureShared::default());
            producer
                .push(clicksync_core::protocol::replay_record(0, 1, 1))
                .unwrap();
            let events: Vec<_> = (0..140)
                .map(|i| ControlInputEvent {
                    ticks: i,
                    input: ControlInput::Keyboard {
                        virtual_key: 65,
                        scan_code: 30,
                        extended: false,
                        down: i % 2 == 0,
                    },
                })
                .collect();
            for &event in &events {
                input_producer.push(event).unwrap();
            }
            shared.input_dropped.store(7, Ordering::Release);
            let cancelled = Arc::new(AtomicBool::new(false));
            let sender_cancelled = cancelled.clone();
            let sender_shared = shared.clone();
            let sender = thread::spawn(move || {
                let pipe =
                    Pipe::client(&name, Instant::now() + IO_TIMEOUT, &sender_cancelled).unwrap();
                let result = send_data(
                    &pipe,
                    consumer,
                    input_consumer,
                    &sender_shared,
                    &Mutex::new(SentProgress::default()),
                    &sender_cancelled,
                );
                if !sender_cancelled.load(Ordering::Acquire) {
                    result.unwrap();
                }
            });
            server
                .connect(Instant::now() + IO_TIMEOUT, &cancelled)
                .unwrap();
            let mut received = Vec::new();
            let mut mouse_sequence = None;
            while received.len() < events.len() || mouse_sequence.is_none() {
                let mut bytes = [0; FRAME_HEADER_BYTES];
                server
                    .read_exact(&mut bytes, Instant::now() + IO_TIMEOUT, &cancelled)
                    .unwrap();
                let header = FrameHeader::decode(&bytes).unwrap();
                let mut payload = vec![0; header.payload_bytes];
                server
                    .read_exact(&mut payload, Instant::now() + IO_TIMEOUT, &cancelled)
                    .unwrap();
                match header.kind {
                    FrameKind::Input => {
                        let batch: ControlInputBatch = serde_json::from_slice(&payload).unwrap();
                        assert!(
                            batch.frequency > 0
                                && batch.events.len()
                                    <= clicksync_core::protocol::MAX_INPUT_BATCH_EVENTS
                        );
                        assert_eq!(batch.dropped, 7);
                        received.extend(batch.events);
                    }
                    FrameKind::Batch => {
                        let record = CapturedRecord::decode(
                            header.generation,
                            payload.as_slice().try_into().unwrap(),
                        )
                        .unwrap();
                        mouse_sequence = Some(record.session_sequence);
                    }
                    FrameKind::Status => {}
                    _ => panic!("unexpected data frame"),
                }
            }
            assert_eq!(received, events);
            assert_eq!(mouse_sequence, Some(1));
            assert_eq!(shared.overflow.load(Ordering::Acquire), 0);
            cancelled.store(true, Ordering::Release);
            server.cancel().unwrap();
            sender.join().unwrap();
        }

        struct Worker {
            data: Option<Pipe>,
            control: Option<Pipe>,
            thread: Option<JoinHandle<io::Result<()>>>,
            cancelled: AtomicBool,
        }

        impl Worker {
            fn spawn() -> Self {
                let id = PIPE_SEQUENCE.fetch_add(1, Ordering::Relaxed);
                let data_name =
                    format!("{PIPE_PREFIX}worker-test-{}-{id}-data", std::process::id());
                let control_name = format!(
                    "{PIPE_PREFIX}worker-test-{}-{id}-control",
                    std::process::id()
                );
                let data = Pipe::server(&data_name).unwrap();
                let control = Pipe::server(&control_name).unwrap();
                let thread = thread::spawn(move || {
                    run(Arguments {
                        data: data_name,
                        control: control_name,
                        parent: std::process::id(),
                        replay: true,
                    })
                });
                let cancelled = AtomicBool::new(false);
                let deadline = Instant::now() + IO_TIMEOUT;
                data.connect(deadline, &cancelled).unwrap();
                control.connect(deadline, &cancelled).unwrap();
                Self {
                    data: Some(data),
                    control: Some(control),
                    thread: Some(thread),
                    cancelled,
                }
            }

            fn command(&self, command: ControlCommand) -> CaptureAck {
                let control = self.control.as_ref().unwrap();
                let bytes = serde_json::to_vec(&command).unwrap();
                let header = FrameHeader {
                    kind: FrameKind::Command,
                    generation: command.generation(),
                    payload_bytes: bytes.len(),
                    record_count: 0,
                }
                .encode()
                .unwrap();
                let deadline = Instant::now() + IO_TIMEOUT;
                control
                    .write_all(&header, deadline, &self.cancelled)
                    .unwrap();
                control
                    .write_all(&bytes, deadline, &self.cancelled)
                    .unwrap();
                let (header, bytes) = self.frame(control);
                assert_eq!(header.kind, FrameKind::Ack);
                let acknowledgement: CaptureAck = serde_json::from_slice(&bytes).unwrap();
                assert_eq!(header.generation, acknowledgement.generation);
                assert_eq!(acknowledgement.command, command.tag());
                assert_ne!(acknowledgement.timestamp_frequency, 0);
                acknowledgement
            }

            fn frame(&self, pipe: &Pipe) -> (FrameHeader, Vec<u8>) {
                let deadline = Instant::now() + IO_TIMEOUT;
                let mut header = [0; FRAME_HEADER_BYTES];
                pipe.read_exact(&mut header, deadline, &self.cancelled)
                    .unwrap();
                let header = FrameHeader::decode(&header).unwrap();
                let mut payload = vec![0; header.payload_bytes];
                pipe.read_exact(&mut payload, deadline, &self.cancelled)
                    .unwrap();
                (header, payload)
            }

            fn data(&self) -> (FrameHeader, Vec<u8>) {
                let (header, payload) = self.frame(self.data.as_ref().unwrap());
                assert!(matches!(header.kind, FrameKind::Batch | FrameKind::Status));
                if header.kind == FrameKind::Status {
                    let status: CaptureStatus = serde_json::from_slice(&payload).unwrap();
                    assert_eq!(header.generation, status.generation);
                }
                (header, payload)
            }

            fn finish(mut self) {
                let acknowledgement = self.command(ControlCommand::Shutdown);
                assert!(acknowledgement.error.is_none());
                self.control.take();
                self.data.take();
                self.thread.take().unwrap().join().unwrap().unwrap();
            }
        }

        impl Drop for Worker {
            fn drop(&mut self) {
                self.control.take();
                self.data.take();
                if let Some(thread) = self.thread.take() {
                    let _ = thread.join();
                }
            }
        }

        fn replay_start(records_per_device: u64) -> ControlCommand {
            ControlCommand::Start {
                generation: 1,
                device_keys: vec![1, 2],
                source: CaptureSource::Replay {
                    records_per_device,
                    reports_per_second: 8000,
                    real_time: false,
                },
                start_fresh: true,
            }
        }

        #[test]
        fn idle_worker_sends_generation_consistent_status_heartbeats() {
            let worker = Worker::spawn();
            let directory = worker.command(ControlCommand::Discover);
            assert_eq!(directory.generation, 0);
            assert_eq!(directory.timestamp_frequency, REPLAY_FREQUENCY);
            assert_eq!(directory.devices.len(), 2);
            for _ in 0..3 {
                let before = Instant::now();
                let (header, bytes) = worker.data();
                assert_eq!(header.kind, FrameKind::Status);
                assert!(before.elapsed() < Duration::from_secs(1));
                let status: CaptureStatus = serde_json::from_slice(&bytes).unwrap();
                assert_eq!(status.generation, 0);
                assert_eq!(status.sent, 0);
                assert!(!status.active);
            }
            worker.finish();
        }

        #[test]
        fn replay_worker_rejects_raw_source_and_keeps_its_actual_transport_alive() {
            let worker = Worker::spawn();
            let rejected = worker.command(ControlCommand::Start {
                generation: 1,
                device_keys: vec![1],
                source: CaptureSource::RawInput,
                start_fresh: true,
            });
            assert!(rejected.error.is_some());
            assert!(!rejected.complete);
            assert_eq!(rejected.generation, 0);
            let started = worker.command(replay_start(16));
            assert!(started.error.is_none());
            assert!(started.complete);
            assert_eq!(started.devices.len(), 2);
            assert_eq!(started.ticks, REPLAY_START_TICKS);
            assert_eq!(started.timestamp_frequency, REPLAY_FREQUENCY);
            let mut expected = 1;
            let deadline = Instant::now() + IO_TIMEOUT;
            while expected <= 32 {
                assert!(Instant::now() < deadline, "replay batches were not flushed");
                let (header, bytes) = worker.data();
                if header.kind == FrameKind::Batch {
                    assert_eq!(header.generation, 1);
                    for bytes in bytes.chunks_exact(WIRE_RECORD_BYTES) {
                        let captured =
                            CapturedRecord::decode(1, bytes.try_into().unwrap()).unwrap();
                        let index = (expected - 1) / 2;
                        assert_eq!(captured.record.capture_sequence, expected);
                        assert_eq!(captured.record.timing_sequence, index + 1);
                        assert_eq!(captured.device_key, ((expected - 1) % 2) as u32 + 1);
                        assert_eq!(
                            captured.record.raw_capture_ticks,
                            REPLAY_START_TICKS + index * 1250
                        );
                        expected += 1;
                    }
                }
            }
            let stopped = worker.command(ControlCommand::Stop);
            assert_eq!(stopped.cutoff, 32);
            assert!(stopped.complete);
            loop {
                assert!(Instant::now() < deadline, "sent watermark did not advance");
                let (header, bytes) = worker.data();
                if header.kind == FrameKind::Status {
                    let status: CaptureStatus = serde_json::from_slice(&bytes).unwrap();
                    if status.generation == 1 && status.sent == 32 {
                        break;
                    }
                }
            }
            worker.finish();
        }

        #[test]
        fn blocked_data_write_does_not_delay_stop_ack_and_overflow_prefix_drains_in_order() {
            let worker = Worker::spawn();
            worker.command(replay_start(REPLAY_RECORD_LIMIT));
            thread::sleep(Duration::from_millis(100));
            let before = Instant::now();
            let stopped = worker.command(ControlCommand::Stop);
            assert!(before.elapsed() < Duration::from_secs(1));
            assert!(stopped.error.is_none());
            assert!(!stopped.complete);
            assert_eq!(stopped.overflow, 1);
            let accepted = stopped.cutoff - stopped.overflow;
            assert!(accepted >= CAPTURE_QUEUE_RECORDS as u64);
            let mut expected = 1;
            let deadline = Instant::now() + IO_TIMEOUT;
            loop {
                assert!(Instant::now() < deadline, "overflow prefix did not drain");
                let (header, bytes) = worker.data();
                match header.kind {
                    FrameKind::Batch => {
                        assert_eq!(header.generation, 1);
                        for bytes in bytes.chunks_exact(WIRE_RECORD_BYTES) {
                            let captured =
                                CapturedRecord::decode(1, bytes.try_into().unwrap()).unwrap();
                            assert_eq!(captured.record.capture_sequence, expected);
                            expected += 1;
                        }
                    }
                    FrameKind::Status => {
                        let status: CaptureStatus = serde_json::from_slice(&bytes).unwrap();
                        if status.generation == 1 && status.sent == accepted {
                            assert_eq!(status.overflow, 1);
                            assert!(!status.active);
                            break;
                        }
                    }
                    _ => unreachable!(),
                }
            }
            assert_eq!(expected - 1, accepted);
            worker.finish();
        }

        #[test]
        fn control_eof_cancels_blocked_data_io_and_joins_worker_threads() {
            let mut worker = Worker::spawn();
            worker.command(replay_start(REPLAY_RECORD_LIMIT));
            thread::sleep(Duration::from_millis(100));
            worker.control.take();
            let deadline = Instant::now() + Duration::from_secs(2);
            while !worker.thread.as_ref().unwrap().is_finished() {
                assert!(
                    Instant::now() < deadline,
                    "worker survived its control connection"
                );
                thread::sleep(Duration::from_millis(1));
            }
            assert!(worker.thread.take().unwrap().join().unwrap().is_err());
        }
    }
}
