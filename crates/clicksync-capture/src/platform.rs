use std::collections::{HashMap, HashSet};
use std::ffi::c_void;
use std::io;
use std::mem::size_of;
use std::ptr::{null, null_mut};
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::mpsc::{self, Receiver, SyncSender, TryRecvError, TrySendError};
use std::sync::{Arc, Mutex, TryLockError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use clicksync_core::devices::{EndpointActivity, EndpointDescriptor};
use clicksync_core::protocol::{
    ControlInput, ControlInputEvent, DeviceDescriptor, MAX_CAPTURE_DEVICE_FIELD_BYTES,
    MAX_CAPTURE_DEVICE_IDENTITIES, MAX_DEVICE_COUNT,
};
use clicksync_core::{RawMouseMovementMode, RawMouseRecord, SessionMetadata};
use windows_sys::Win32::Foundation::{HANDLE, HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency};
use windows_sys::Win32::System::Threading::GetCurrentThreadId;
use windows_sys::Win32::UI::Input::{
    GetRawInputData, GetRawInputDeviceInfoW, GetRawInputDeviceList, RAWINPUT, RAWINPUTDEVICE,
    RAWINPUTDEVICELIST, RAWINPUTHEADER, RAWKEYBOARD, RAWMOUSE, RID_DEVICE_INFO, RID_INPUT,
    RIDEV_DEVNOTIFY, RIDEV_INPUTSINK, RIDEV_REMOVE, RIDI_DEVICEINFO, RIDI_DEVICENAME,
    RIM_TYPEKEYBOARD, RIM_TYPEMOUSE, RegisterRawInputDevices,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CREATESTRUCTW, CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GWLP_USERDATA,
    GetMessageW, GetWindowLongPtrW, HWND_MESSAGE, MSG, PM_NOREMOVE, PeekMessageW, PostMessageW,
    PostQuitMessage, PostThreadMessageW, RegisterClassW, SetWindowLongPtrW, UnregisterClassW,
    WM_APP, WM_CLOSE, WM_INPUT, WM_INPUT_DEVICE_CHANGE, WM_NCCREATE, WM_NCDESTROY, WM_QUIT,
    WNDCLASSW,
};

use crate::{CaptureShared, CapturedRecord};
mod devices;

#[cfg(test)]
#[path = "capture_measurements.rs"]
mod capture_measurements;

const CONTROL_MESSAGE: u32 = WM_APP + 1;
const WATERMARK_MESSAGE: u32 = WM_APP + 2;
const CONTROL_CAPACITY: usize = 16;
const CONTROL_TIMEOUT: Duration = Duration::from_secs(5);
const MAX_ENUMERATED_DEVICES: usize = 65_536;
const MAX_DEVICE_PATH_UNITS: usize = MAX_CAPTURE_DEVICE_FIELD_BYTES;
const MOUSE_ABSOLUTE: u16 = 0x0001;
const DEVICE_REMOVAL: WPARAM = 2;

static SERVICE_RUNNING: AtomicBool = AtomicBool::new(false);

pub fn request_watermark(shared: &CaptureShared) {
    if !shared.watermark.request() {
        return;
    }
    let hwnd = shared.raw_input_window.load(Ordering::Acquire);
    // Replay observes the same coalesced request on its own source thread.
    if hwnd != 0 && unsafe { PostMessageW(hwnd as HWND, WATERMARK_MESSAGE, 0, 0) } == 0 {
        shared.watermark.cancel_request();
    }
}

#[derive(Debug)]
pub enum InputCommand {
    Discover,
    RescanDevices,
    Observe {
        request: crate::observation::MotionRequest,
        ticket: u64,
    },
    PollMotion,
    #[cfg(feature = "controlled-input")]
    ReplayMotion(Vec<crate::observation::MotionReport>),
    Start {
        generation: u64,
        device_keys: Vec<u32>,
        start_fresh: bool,
    },
    Pause,
    Resume,
    Stop,
    Reset {
        generation: u64,
    },
    Shutdown,
}

#[derive(Clone, Copy, Debug)]
pub struct InputAck {
    /// Last attempted selected-device record, including an overflowed attempt.
    pub cutoff: u64,
    pub ticks: u64,
    pub frequency: u64,
    pub generation: u64,
}

pub struct InputService {
    commands: SyncSender<ControlRequest>,
    shutdown: Arc<AtomicBool>,
    hwnd: usize,
    thread_id: u32,
    finished: Receiver<()>,
    thread: Option<JoinHandle<()>>,
}

impl InputService {
    pub fn spawn(
        producer: rtrb::Producer<CapturedRecord>,
        shared: Arc<CaptureShared>,
        devices: Arc<Mutex<Vec<DeviceDescriptor>>>,
    ) -> io::Result<Self> {
        Self::spawn_with_inputs(producer, None, shared, devices)
    }
    pub fn spawn_with_inputs(
        producer: rtrb::Producer<CapturedRecord>,
        inputs: Option<rtrb::Producer<ControlInputEvent>>,
        shared: Arc<CaptureShared>,
        devices: Arc<Mutex<Vec<DeviceDescriptor>>>,
    ) -> io::Result<Self> {
        if producer.buffer().capacity() == 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Raw Input requires a nonempty capture queue",
            ));
        }
        SERVICE_RUNNING
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| {
                io::Error::new(
                    io::ErrorKind::AlreadyExists,
                    "a Raw Input service already owns this process's mouse registration",
                )
            })?;
        let reservation = ServiceReservation;
        let (commands, receiver) = mpsc::sync_channel(CONTROL_CAPACITY);
        let (ready_tx, ready_rx) = mpsc::sync_channel(1);
        let (finished_tx, finished) = mpsc::sync_channel(1);
        let shutdown = Arc::new(AtomicBool::new(false));
        let thread_shutdown = Arc::clone(&shutdown);
        let startup_thread_id = Arc::new(AtomicU32::new(0));
        let published_thread_id = Arc::clone(&startup_thread_id);
        let thread = thread::Builder::new()
            .name("ClickSyncRawInput".into())
            .spawn(move || {
                let _reservation = reservation;
                publish_thread_queue(&published_thread_id);
                run_input_thread(
                    producer,
                    inputs,
                    shared,
                    devices,
                    receiver,
                    thread_shutdown,
                    ready_tx,
                );
                let _ = finished_tx.send(());
            })?;

        match ready_rx.recv_timeout(CONTROL_TIMEOUT) {
            Ok(Ok((hwnd, thread_id))) => Ok(Self {
                commands,
                shutdown,
                hwnd,
                thread_id,
                finished,
                thread: Some(thread),
            }),
            result => {
                cancel_startup_thread(&shutdown, &startup_thread_id);
                finish_thread(thread, &finished);
                Err(match result {
                    Ok(Err(error)) => error,
                    Err(mpsc::RecvTimeoutError::Timeout) => io::Error::new(
                        io::ErrorKind::TimedOut,
                        "Raw Input initialization timed out",
                    ),
                    Err(mpsc::RecvTimeoutError::Disconnected) => io::Error::new(
                        io::ErrorKind::BrokenPipe,
                        "Raw Input initialization thread exited",
                    ),
                    Ok(Ok(_)) => unreachable!(),
                })
            }
        }
    }

    pub fn command(&self, command: InputCommand) -> io::Result<InputAck> {
        self.command_with_permit(command, None)
    }

    pub(crate) fn command_with_permit(
        &self,
        command: InputCommand,
        permit: Option<u64>,
    ) -> io::Result<InputAck> {
        if self.shutdown.load(Ordering::Acquire) {
            return Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "Raw Input service is shutting down",
            ));
        }
        let (reply, acknowledgement) = mpsc::sync_channel(1);
        let expires = Instant::now() + CONTROL_TIMEOUT;
        self.commands
            .try_send(ControlRequest {
                command,
                permit,
                reply,
                expires,
            })
            .map_err(|error| match error {
                TrySendError::Full(_) => {
                    io::Error::new(io::ErrorKind::WouldBlock, "Raw Input control queue is full")
                }
                TrySendError::Disconnected(_) => {
                    io::Error::new(io::ErrorKind::BrokenPipe, "Raw Input thread exited")
                }
            })?;
        // Messages contain no owning pointers: a timeout cannot leave a dangling request.
        if unsafe { PostMessageW(self.hwnd as HWND, CONTROL_MESSAGE, 0, 0) } == 0 {
            let error = io::Error::last_os_error();
            self.request_shutdown();
            return Err(error);
        }
        match acknowledgement.recv_timeout(expires.saturating_duration_since(Instant::now())) {
            Ok(result) => result,
            Err(error) => {
                // Fail closed instead of allowing a timed-out Start to activate later.
                self.request_shutdown();
                Err(io::Error::new(
                    match error {
                        mpsc::RecvTimeoutError::Timeout => io::ErrorKind::TimedOut,
                        mpsc::RecvTimeoutError::Disconnected => io::ErrorKind::BrokenPipe,
                    },
                    "Raw Input command did not complete",
                ))
            }
        }
    }

    fn request_shutdown(&self) {
        self.shutdown.store(true, Ordering::Release);
        if self.thread.as_ref().is_some_and(JoinHandle::is_finished) {
            return;
        }
        if unsafe { PostMessageW(self.hwnd as HWND, CONTROL_MESSAGE, 0, 0) } == 0 {
            unsafe {
                PostThreadMessageW(self.thread_id, WM_QUIT, 0, 0);
            }
        }
    }
}

fn publish_thread_queue(thread_id: &AtomicU32) {
    let mut message = MSG::default();
    // Publish only after User32 has created the queue, so timeout cleanup can always wake it.
    unsafe { PeekMessageW(&mut message, null_mut(), 0, 0, PM_NOREMOVE) };
    thread_id.store(unsafe { GetCurrentThreadId() }, Ordering::Release);
}

fn cancel_startup_thread(shutdown: &AtomicBool, thread_id: &AtomicU32) {
    shutdown.store(true, Ordering::Release);
    let id = thread_id.load(Ordering::Acquire);
    if id != 0 {
        unsafe { PostThreadMessageW(id, WM_QUIT, 0, 0) };
    }
}

impl Drop for InputService {
    fn drop(&mut self) {
        self.request_shutdown();
        if let Some(thread) = self.thread.take() {
            finish_thread(thread, &self.finished);
        }
    }
}

fn finish_thread(thread: JoinHandle<()>, finished: &Receiver<()>) {
    // Join only after completion; a stalled system call must not hang the caller.
    if finished.recv_timeout(CONTROL_TIMEOUT).is_ok() || thread.is_finished() {
        let _ = thread.join();
    }
}

struct ServiceReservation;

impl Drop for ServiceReservation {
    fn drop(&mut self) {
        SERVICE_RUNNING.store(false, Ordering::Release);
    }
}

struct ControlRequest {
    command: InputCommand,
    permit: Option<u64>,
    reply: SyncSender<io::Result<InputAck>>,
    expires: Instant,
}

#[derive(Default)]
struct DeviceState {
    key: u32,
    timing_sequence: u64,
    present: bool,
    selected: bool,
    observable: bool,
    motion_pending: bool,
    activity: EndpointActivity,
}

impl DeviceState {
    fn can_capture(&self) -> bool {
        self.present && self.observable && (!self.motion_pending || self.activity.motion)
    }
}

struct DiscoveredDevice {
    handle: usize,
    metadata: SessionMetadata,
    endpoint: EndpointDescriptor,
}

struct CaptureState {
    producer: rtrb::Producer<CapturedRecord>,
    shared: Arc<CaptureShared>,
    handles: HashMap<usize, usize>,
    identities: HashMap<String, usize>,
    device_states: Vec<DeviceState>,
    descriptors: Vec<DeviceDescriptor>,
    generation: u64,
    capture_sequence: u64,
    session_sequence: u64,
    has_session: bool,
    resumable: bool,
    running: bool,
    incomplete: bool,
    observation: Option<crate::observation::ArmedMotion>,
}

impl CaptureState {
    fn new(producer: rtrb::Producer<CapturedRecord>, shared: Arc<CaptureShared>) -> Self {
        shared.active.store(false, Ordering::Release);
        Self {
            producer,
            shared,
            handles: HashMap::new(),
            identities: HashMap::new(),
            device_states: Vec::new(),
            descriptors: Vec::new(),
            generation: 0,
            capture_sequence: 0,
            session_sequence: 0,
            has_session: false,
            resumable: false,
            running: false,
            incomplete: false,
            observation: None,
        }
    }

    fn replace_directory(
        &mut self,
        discovered: Vec<DiscoveredDevice>,
    ) -> io::Result<Vec<DeviceDescriptor>> {
        if discovered.len() > MAX_DEVICE_COUNT {
            return Err(io::Error::new(
                io::ErrorKind::OutOfMemory,
                "Raw Input mouse directory exceeds the protocol device limit",
            ));
        }
        if discovered.iter().any(|device| {
            [
                &device.metadata.device_id,
                &device.metadata.display_name,
                &device.metadata.path_summary,
                &device.endpoint.physical_device_key,
                &device.endpoint.endpoint_token,
            ]
            .iter()
            .any(|field| field.len() > MAX_CAPTURE_DEVICE_FIELD_BYTES)
        }) {
            return Err(io::Error::new(
                io::ErrorKind::OutOfMemory,
                "Raw Input device metadata exceeds its reserved byte limit",
            ));
        }
        let discovered: Vec<_> = discovered
            .into_iter()
            .map(|device| (device.metadata.device_id.to_uppercase(), device))
            .collect();
        let new_identities = discovered
            .iter()
            .map(|(identity, _)| identity)
            .filter(|identity| !self.identities.contains_key(*identity))
            .collect::<HashSet<_>>()
            .len();
        if new_identities > MAX_CAPTURE_DEVICE_IDENTITIES - self.device_states.len() {
            return Err(io::Error::new(
                io::ErrorKind::OutOfMemory,
                "Raw Input device identity history is full",
            ));
        }
        // All fallible validation precedes publication of presence, handles and identities.
        let mut handles = HashMap::with_capacity(discovered.len());
        let mut descriptors = Vec::with_capacity(discovered.len());
        for device in &mut self.device_states {
            device.present = false;
        }
        for (identity, device) in discovered {
            let index = match self.identities.get(&identity) {
                Some(&index) => index,
                None => {
                    let index = self.device_states.len();
                    self.device_states.push(DeviceState {
                        key: (index + 1) as u32,
                        ..DeviceState::default()
                    });
                    // Keys belong to paths for this service's lifetime, never to reused handles.
                    self.identities.insert(identity, index);
                    index
                }
            };
            let state = &mut self.device_states[index];
            if !state.present {
                let mut endpoint = device.endpoint;
                endpoint.activity = state.activity;
                let descriptor = DeviceDescriptor {
                    key: state.key,
                    metadata: device.metadata,
                    endpoint,
                };
                state.observable = descriptor.eligible_for_motion();
                state.motion_pending = descriptor.endpoint.requires_motion_confirmation();
                descriptors.push(descriptor);
            }
            state.present = true;
            handles.insert(device.handle, index);
        }
        self.handles = handles;
        for device in &self.device_states {
            if !device.can_capture() {
                self.shared.motion.invalidate_device(device.key);
            }
        }
        if let Some(observation) = &mut self.observation {
            observation.eligibility.update(&descriptors);
            if observation.eligibility.is_empty() {
                self.shared.motion.publish(
                    observation.request.id,
                    crate::observation::MotionResult::DeviceRemoved,
                );
                self.shared.wake.notify();
                self.observation = None;
            }
        }
        if self
            .device_states
            .iter()
            .any(|device| device.selected && !device.can_capture())
        {
            self.disconnected();
        }
        descriptors.sort_unstable_by_key(|device| device.key);
        self.descriptors = descriptors.clone();
        Ok(descriptors)
    }

    fn directory(&self) -> Vec<DeviceDescriptor> {
        self.descriptors
            .iter()
            .filter(|descriptor| self.device_states[descriptor.key as usize - 1].present)
            .cloned()
            .map(|mut descriptor| {
                descriptor.endpoint.activity =
                    self.device_states[descriptor.key as usize - 1].activity;
                descriptor
            })
            .collect()
    }

    fn start(&mut self, generation: u64, keys: &[u32], start_fresh: bool) -> io::Result<()> {
        if generation == 0
            || keys.is_empty()
            || keys.len() > MAX_DEVICE_COUNT
            || keys.iter().any(|&key| {
                key == 0
                    || !self
                        .device_states
                        .get(key as usize - 1)
                        .is_some_and(|device| device.can_capture() && device.key == key)
            })
            || keys
                .iter()
                .enumerate()
                .any(|(index, key)| keys[..index].contains(key))
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Start requires distinct, currently connected device keys",
            ));
        }
        if generation < self.generation
            || (start_fresh && generation == self.generation && self.has_session)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "a fresh capture requires an unused session generation",
            ));
        }
        if !start_fresh
            && (!self.can_resume()
                || generation != self.generation
                || self
                    .device_states
                    .iter()
                    .filter(|device| device.selected)
                    .count()
                    != keys.len()
                || keys
                    .iter()
                    .any(|&key| !self.device_states[key as usize - 1].selected))
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "continuing Start must preserve its complete session and device selection",
            ));
        }
        if generation > self.generation {
            self.reset(generation)?;
        } else if !self.complete() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "this capture is stopped or incomplete; reset with a new generation",
            ));
        }
        if start_fresh {
            self.session_sequence = 0;
            self.clear_counters();
        }
        for device in &mut self.device_states {
            device.selected = keys.contains(&device.key);
        }
        self.has_session = true;
        self.resumable = true;
        self.running = true;
        self.shared.active.store(true, Ordering::Release);
        Ok(())
    }

    fn pause(&mut self) {
        self.running = false;
        self.shared.active.store(false, Ordering::Release);
    }

    fn stop(&mut self) {
        self.pause();
        self.resumable = false;
    }

    fn resume(&mut self) -> io::Result<()> {
        if !self.can_resume() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "there is no complete, connected capture to resume",
            ));
        }
        self.running = true;
        self.shared.active.store(true, Ordering::Release);
        Ok(())
    }

    fn can_resume(&self) -> bool {
        self.has_session
            && self.resumable
            && !self.running
            && self.complete()
            && self
                .device_states
                .iter()
                .all(|device| !device.selected || device.can_capture())
    }

    fn reset(&mut self, generation: u64) -> io::Result<()> {
        if generation == 0 || generation <= self.generation {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Reset requires a newer session generation",
            ));
        }
        self.stop();
        self.generation = generation;
        self.session_sequence = 0;
        self.has_session = false;
        self.incomplete = false;
        for device in &mut self.device_states {
            device.selected = false;
        }
        self.clear_counters();
        self.shared.generation.store(generation, Ordering::Release);
        Ok(())
    }

    fn clear_counters(&self) {
        self.shared.captured.store(0, Ordering::Relaxed);
        self.shared.queued.store(0, Ordering::Relaxed);
        self.shared.overflow.store(0, Ordering::Relaxed);
        self.shared.high_watermark.store(0, Ordering::Relaxed);
        self.shared.disconnected.store(false, Ordering::Release);
    }

    fn is_active(&self) -> bool {
        self.running && self.shared.is_active()
    }

    fn complete(&self) -> bool {
        !self.incomplete
            && self.shared.overflow.load(Ordering::Acquire) == 0
            && !self.shared.disconnected.load(Ordering::Acquire)
    }

    fn capture_mouse(&mut self, handle: usize, mouse: RAWMOUSE, ticks: u64) {
        // Source counters include idle, paused and unselected reports, as in RawInputBroker.
        let Some(capture_sequence) = self
            .capture_sequence
            .checked_add(1)
            .filter(|&sequence| sequence <= i64::MAX as u64)
        else {
            self.fail();
            return;
        };
        self.capture_sequence = capture_sequence;
        let Some(&index) = self.handles.get(&handle) else {
            return;
        };
        let device = &mut self.device_states[index];
        let Some(timing_sequence) = device
            .timing_sequence
            .checked_add(1)
            .filter(|&sequence| sequence <= i64::MAX as u64)
        else {
            self.fail();
            return;
        };
        device.timing_sequence = timing_sequence;
        let key = device.key;
        let record = decode_mouse(mouse, ticks, capture_sequence, timing_sequence);
        let (delta_x, delta_y) = record.relative_delta();
        device.activity.observe(delta_x, delta_y);
        let mut selected_motion = None;
        if let Some(observation) = &self.observation {
            if observation.expire(&self.shared) {
                self.observation = None;
            } else if let Some(candidate) = observation.offer(&self.shared, key, &record) {
                let generation = observation.request.capture_generation;
                self.observation = None;
                if let Some(generation) = generation
                    && self.start(generation, &[key], true).is_err()
                {
                    self.shared
                        .motion
                        .publish(candidate.request, crate::observation::MotionResult::Revoked);
                    return;
                }
                if !self.shared.motion.publish(
                    candidate.request,
                    crate::observation::MotionResult::Selected(candidate),
                ) {
                    if generation.is_some() {
                        self.pause();
                    }
                    return;
                }
                selected_motion = Some(candidate);
            }
        }
        let active = self.is_active();
        let device = &self.device_states[index];
        if !active || !device.selected || !device.present {
            if let Some(candidate) = selected_motion {
                let _ = candidate;
                self.shared.wake.notify();
            }
            return;
        }
        self.session_sequence += 1;
        let captured = CapturedRecord {
            generation: self.generation,
            device_key: device.key,
            session_sequence: self.session_sequence,
            record,
        };
        match self.producer.push(captured) {
            Ok(()) => {
                self.shared.queued.fetch_add(1, Ordering::Relaxed);
                let depth = self.producer.buffer().capacity() - self.producer.slots();
                if depth > self.shared.high_watermark.load(Ordering::Relaxed) {
                    self.shared.high_watermark.store(depth, Ordering::Relaxed);
                }
                self.shared
                    .captured
                    .store(self.session_sequence, Ordering::Release);
                self.shared.wake.notify();
            }
            Err(_) => {
                self.shared
                    .captured
                    .store(self.session_sequence, Ordering::Relaxed);
                self.fail();
            }
        }
        if let Some(candidate) = selected_motion {
            let _ = candidate;
            self.shared.wake.notify();
        }
    }

    fn failed_attempt(&mut self, handle: usize) {
        if !self.is_active() {
            return;
        }
        let Some(&index) = self.handles.get(&handle) else {
            return;
        };
        let device = &self.device_states[index];
        if !device.selected || !device.present {
            return;
        }
        self.session_sequence += 1;
        self.shared
            .captured
            .store(self.session_sequence, Ordering::Relaxed);
        self.fail();
    }

    fn fail(&mut self) {
        if self.running && self.shared.active.load(Ordering::Acquire) {
            self.incomplete = true;
            self.shared.overflow.fetch_add(1, Ordering::Relaxed);
            self.pause();
            self.shared.wake.notify();
        }
    }

    fn disconnected(&mut self) {
        // Presence changes affect a live or paused capture, never its already sealed result.
        if self.resumable {
            self.incomplete = true;
            self.shared.disconnected.store(true, Ordering::Relaxed);
            self.pause();
            self.shared.wake.notify();
        }
    }

    fn remove_device(&mut self, handle: usize) {
        if let Some(index) = self.handles.remove(&handle) {
            let device = &mut self.device_states[index];
            device.present = false;
            self.shared.motion.invalidate_device(device.key);
            if device.selected {
                self.disconnected();
            }
        }
    }
}

impl Drop for CaptureState {
    fn drop(&mut self) {
        if self.running {
            self.disconnected();
        } else {
            self.stop();
        }
    }
}

fn decode_mouse(
    mouse: RAWMOUSE,
    ticks: u64,
    capture_sequence: u64,
    timing_sequence: u64,
) -> RawMouseRecord {
    let buttons = unsafe { mouse.Anonymous.Anonymous };
    RawMouseRecord {
        raw_delta_x: mouse.lLastX,
        raw_delta_y: mouse.lLastY,
        button_flags: buttons.usButtonFlags,
        raw_mouse_flags: mouse.usFlags,
        button_data: buttons.usButtonData,
        extra_information: mouse.ulExtraInformation,
        raw_capture_ticks: ticks,
        capture_sequence,
        timing_sequence,
        movement_mode: if mouse.usFlags & MOUSE_ABSOLUTE != 0 {
            RawMouseMovementMode::Absolute
        } else {
            RawMouseMovementMode::Relative
        },
    }
}

struct WindowState {
    capture: CaptureState,
    inputs: Option<rtrb::Producer<ControlInputEvent>>,
    devices: Arc<Mutex<Vec<DeviceDescriptor>>>,
    commands: Receiver<ControlRequest>,
    shutdown: Arc<AtomicBool>,
    frequency: u64,
    discovery: devices::Discovery,
    paths_by_handle: HashMap<usize, String>,
}

impl WindowState {
    fn control_input(&mut self, ticks: u64, input: ControlInput) {
        if let Some(producer) = &mut self.inputs
            && producer.push(ControlInputEvent { ticks, input }).is_err()
        {
            self.capture
                .shared
                .input_dropped
                .fetch_add(1, Ordering::Relaxed);
        }
        self.capture.shared.wake.notify();
    }
    fn refresh_devices(&mut self) -> io::Result<()> {
        let discovered = self.discovery.enumerate()?;
        let paths = discovered
            .iter()
            .map(|device| (device.handle, device.metadata.device_id.clone()))
            .collect();
        self.capture.replace_directory(discovered)?;
        self.paths_by_handle = paths;
        self.publish_devices()
    }

    fn publish_devices(&mut self) -> io::Result<()> {
        let descriptors = self.capture.directory();
        let expires = Instant::now() + CONTROL_TIMEOUT;
        loop {
            match self.devices.try_lock() {
                Ok(mut devices) => {
                    *devices = descriptors;
                    return Ok(());
                }
                Err(TryLockError::Poisoned(_)) => {
                    return Err(io::Error::other(
                        "Raw Input device directory lock is poisoned",
                    ));
                }
                Err(TryLockError::WouldBlock) => {
                    if Instant::now() >= expires || self.shutdown.load(Ordering::Acquire) {
                        return Err(io::Error::new(
                            io::ErrorKind::TimedOut,
                            "Raw Input device directory publication timed out",
                        ));
                    }
                    thread::sleep(Duration::from_millis(1));
                }
            }
        }
    }

    fn handle_commands(&mut self) {
        if self.shutdown.load(Ordering::Acquire) {
            self.capture.stop();
            unsafe { PostQuitMessage(0) };
            return;
        }
        for _ in 0..CONTROL_CAPACITY {
            let request = match self.commands.try_recv() {
                Ok(request) => request,
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => {
                    self.capture.stop();
                    unsafe { PostQuitMessage(0) };
                    break;
                }
            };
            if Instant::now() >= request.expires || self.shutdown.load(Ordering::Acquire) {
                self.capture.stop();
                let _ = request.reply.try_send(Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "Raw Input command expired before dispatch",
                )));
                self.shutdown.store(true, Ordering::Release);
                unsafe { PostQuitMessage(0) };
                break;
            }
            let shutdown = matches!(&request.command, InputCommand::Shutdown);
            let result = self.apply_permitted_command(request.command, request.permit);
            if self.shutdown.load(Ordering::Acquire) {
                self.capture.stop();
            }
            let _ = request.reply.try_send(result);
            if shutdown {
                self.shutdown.store(true, Ordering::Release);
                unsafe { PostQuitMessage(0) };
                break;
            }
        }
    }

    fn apply_command(&mut self, command: InputCommand) -> io::Result<InputAck> {
        if !matches!(
            command,
            InputCommand::Discover
                | InputCommand::RescanDevices
                | InputCommand::Observe { .. }
                | InputCommand::PollMotion
        ) {
            self.capture.shared.motion.cancel_current();
            self.capture.observation = None;
        }
        match command {
            InputCommand::Discover => self.publish_devices()?,
            InputCommand::RescanDevices => self.refresh_devices()?,
            InputCommand::PollMotion => {
                if self
                    .capture
                    .observation
                    .as_ref()
                    .is_some_and(|motion| motion.expire(&self.capture.shared))
                {
                    self.capture.observation = None;
                }
            }
            InputCommand::Observe { request, ticket } => {
                let observation = crate::observation::ArmedMotion::new(
                    request,
                    ticket,
                    &self.capture.directory(),
                );
                if self.capture.is_active()
                    || !self.capture.shared.motion.is_current(request.id)
                    || !self.capture.shared.permit.allows(ticket)
                    || observation.eligibility.is_empty()
                {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidInput,
                        "motion observation requires an idle, permitted source and an eligible device",
                    ));
                }
                if let Some(generation) = request.capture_generation {
                    self.capture.reset(generation)?;
                    self.capture.shared.authorize(ticket);
                }
                self.capture.observation = Some(observation);
            }
            #[cfg(feature = "controlled-input")]
            InputCommand::ReplayMotion(_) => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "controlled motion is available only in replay mode",
                ));
            }
            InputCommand::Start {
                generation,
                device_keys,
                start_fresh,
            } => {
                let catalog =
                    clicksync_core::devices::DeviceCatalog::new(&self.capture.directory());
                if !catalog.accepts_selection(&device_keys) {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidInput,
                        "Start requires available, distinct physical mouse devices",
                    ));
                }
                self.capture.start(generation, &device_keys, start_fresh)?;
            }
            InputCommand::Pause => self.capture.pause(),
            InputCommand::Resume => self.capture.resume()?,
            InputCommand::Stop | InputCommand::Shutdown => self.capture.stop(),
            InputCommand::Reset { generation } => self.capture.reset(generation)?,
        }
        // Pause/Stop seal the producer on its sole owner before taking the ACK timestamp.
        let ticks = match performance_ticks() {
            Ok(ticks) => ticks,
            Err(error) => {
                self.capture.disconnected();
                self.capture.stop();
                return Err(error);
            }
        };
        Ok(InputAck {
            cutoff: self.capture.session_sequence,
            ticks,
            frequency: self.frequency,
            generation: self.capture.generation,
        })
    }

    fn apply_permitted_command(
        &mut self,
        command: InputCommand,
        permit: Option<u64>,
    ) -> io::Result<InputAck> {
        let activating = matches!(command, InputCommand::Start { .. } | InputCommand::Resume);
        if activating && permit.is_some_and(|ticket| !self.capture.shared.permit.allows(ticket)) {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "capture permission expired before input-thread activation",
            ));
        }
        let ack = self.apply_command(command)?;
        if activating && let Some(ticket) = permit {
            // Commit only after validation, on the producer thread. A concurrent revoke
            // leaves this old ticket disabled, even if focus is subsequently restored.
            self.capture.shared.authorize(ticket);
        }
        Ok(ack)
    }

    fn handle_input(&mut self, raw_handle: LPARAM, ticks: u64, delivery_code: usize) {
        if self.shutdown.load(Ordering::Acquire) {
            self.capture.stop();
            return;
        }
        // RAWINPUT is aligned for the header and mouse union, and no size-query allocation is used.
        let mut raw = RAWINPUT::default();
        let mut size = size_of::<RAWINPUT>() as u32;
        let read = unsafe {
            GetRawInputData(
                raw_handle as *mut c_void,
                RID_INPUT,
                (&mut raw as *mut RAWINPUT).cast(),
                &mut size,
                size_of::<RAWINPUTHEADER>() as u32,
            )
        };
        let header_size = size_of::<RAWINPUTHEADER>() as u32;
        if read == u32::MAX
            || read < header_size
            || read > size_of::<RAWINPUT>() as u32
            || raw.header.dwSize < header_size
            || raw.header.dwSize > read
        {
            // Identity is unavailable: mark incomplete without assigning an invented sequence.
            self.capture.fail();
            return;
        }
        if raw.header.dwType == RIM_TYPEKEYBOARD {
            if read < header_size + size_of::<RAWKEYBOARD>() as u32 {
                self.capture
                    .shared
                    .input_dropped
                    .fetch_add(1, Ordering::Relaxed);
                return;
            }
            let keyboard = unsafe { raw.data.keyboard };
            if keyboard.MakeCode != 255 {
                let mapped = if keyboard.MakeCode == 0 {
                    unsafe {
                        windows_sys::Win32::UI::Input::KeyboardAndMouse::MapVirtualKeyW(
                            keyboard.VKey as u32,
                            4,
                        )
                    }
                } else {
                    keyboard.MakeCode as u32
                };
                self.control_input(
                    ticks,
                    ControlInput::Keyboard {
                        virtual_key: keyboard.VKey,
                        scan_code: (mapped & 0xff) as u16,
                        extended: keyboard.Flags & 0x6 != 0 || mapped & 0xff00 != 0,
                        down: keyboard.Flags & 0x1 == 0,
                    },
                );
            }
            return;
        }
        if raw.header.dwType != RIM_TYPEMOUSE {
            return;
        }
        let handle = raw.header.hDevice as usize;
        let mouse_size = header_size + size_of::<RAWMOUSE>() as u32;
        if read < mouse_size || raw.header.dwSize < mouse_size {
            self.capture.failed_attempt(handle);
            return;
        }
        let mouse = unsafe { raw.data.mouse };
        let buttons = unsafe { mouse.Anonymous.Anonymous };
        if self.capture.shared.delivery.enabled.load(Ordering::Relaxed) {
            let selected = self.capture.handles.get(&handle).is_some_and(|&index| {
                let device = &self.capture.device_states[index];
                device.selected && device.present
            });
            self.capture.shared.delivery.record(delivery_code, selected);
        }
        self.capture.capture_mouse(handle, mouse, ticks);
        if buttons.usButtonFlags != 0 {
            self.control_input(
                ticks,
                ControlInput::Mouse {
                    flags: buttons.usButtonFlags,
                    data: buttons.usButtonData,
                },
            );
        }
    }
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if message == WM_INPUT {
        // QPC precedes even the window-state lookup: this is the observed input-entry time.
        let mut ticks = 0i64;
        let clock_ok = unsafe { QueryPerformanceCounter(&mut ticks) } != 0 && ticks >= 0;
        let state = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *mut WindowState;
        if let Some(state) = unsafe { state.as_mut() } {
            if clock_ok {
                state.handle_input(lparam, ticks as u64, wparam & 0xff);
            } else {
                state.capture.fail();
            }
        }
        // Required for foreground RIM_INPUT cleanup, also valid for background input.
        return unsafe { DefWindowProcW(hwnd, message, wparam, lparam) };
    }
    if message == WM_NCCREATE {
        let create = unsafe { &*(lparam as *const CREATESTRUCTW) };
        unsafe { SetWindowLongPtrW(hwnd, GWLP_USERDATA, create.lpCreateParams as isize) };
        return 1;
    }
    if message == WM_NCDESTROY {
        unsafe { SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0) };
        return unsafe { DefWindowProcW(hwnd, message, wparam, lparam) };
    }
    let state = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *mut WindowState;
    if let Some(state) = unsafe { state.as_mut() } {
        match message {
            WATERMARK_MESSAGE => {
                if let Ok(ticks) = performance_ticks() {
                    state
                        .capture
                        .shared
                        .watermark
                        .publish(crate::watermark::Checkpoint {
                            generation: state.capture.generation,
                            sequence: state.capture.session_sequence,
                            capture_sequence: state.capture.capture_sequence,
                            ticks,
                            frequency: state.frequency,
                            active: state.capture.is_active(),
                            complete: state.capture.complete(),
                            dropped_packets: state.capture.shared.overflow.load(Ordering::Acquire),
                            observed: Instant::now(),
                        });
                } else {
                    state.capture.shared.watermark.cancel_request();
                    state.capture.fail();
                }
                return 0;
            }
            CONTROL_MESSAGE => {
                state.handle_commands();
                return 0;
            }
            WM_INPUT_DEVICE_CHANGE => {
                if wparam == DEVICE_REMOVAL {
                    state.discovery.remove_handle(lparam as usize);
                    if let Some(path) = state.paths_by_handle.remove(&(lparam as usize)) {
                        state.discovery.remove(&path);
                    }
                    state.capture.remove_device(lparam as usize);
                }
                if state.refresh_devices().is_err() {
                    state.capture.disconnected();
                }
                return 0;
            }
            WM_CLOSE => {
                state.capture.disconnected();
                state.capture.stop();
                unsafe { PostQuitMessage(0) };
                return 0;
            }
            _ => {}
        }
    }
    unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
}

struct NativeWindow {
    hwnd: HWND,
    instance: HINSTANCE,
    class_name: Vec<u16>,
    input_registered: bool,
}

impl NativeWindow {
    fn create(state: &mut WindowState) -> io::Result<Self> {
        let instance = unsafe { GetModuleHandleW(null()) };
        if instance.is_null() {
            return Err(io::Error::last_os_error());
        }
        let class_name: Vec<_> = format!("ClickSyncRawInput-{}", unsafe { GetCurrentThreadId() })
            .encode_utf16()
            .chain(Some(0))
            .collect();
        let class = WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            lpszClassName: class_name.as_ptr(),
            ..WNDCLASSW::default()
        };
        if unsafe { RegisterClassW(&class) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let mut window = Self {
            hwnd: null_mut(),
            instance,
            class_name,
            input_registered: false,
        };
        window.hwnd = unsafe {
            CreateWindowExW(
                0,
                window.class_name.as_ptr(),
                window.class_name.as_ptr(),
                0,
                0,
                0,
                0,
                0,
                HWND_MESSAGE,
                null_mut(),
                instance,
                (state as *mut WindowState).cast(),
            )
        };
        if window.hwnd.is_null() {
            return Err(io::Error::last_os_error());
        }
        let mouse_registration = RAWINPUTDEVICE {
            usUsagePage: 0x01,
            usUsage: 0x02,
            dwFlags: RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
            hwndTarget: window.hwnd,
        };
        let registration = [
            mouse_registration,
            RAWINPUTDEVICE {
                usUsage: 0x06,
                ..mouse_registration
            },
        ];
        if unsafe {
            RegisterRawInputDevices(registration.as_ptr(), 2, size_of::<RAWINPUTDEVICE>() as u32)
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        window.input_registered = true;
        Ok(window)
    }
}

impl Drop for NativeWindow {
    fn drop(&mut self) {
        if self.input_registered {
            let mouse_registration = RAWINPUTDEVICE {
                usUsagePage: 0x01,
                usUsage: 0x02,
                dwFlags: RIDEV_REMOVE,
                hwndTarget: null_mut(),
            };
            unsafe {
                let registration = [
                    mouse_registration,
                    RAWINPUTDEVICE {
                        usUsage: 0x06,
                        ..mouse_registration
                    },
                ];
                RegisterRawInputDevices(
                    registration.as_ptr(),
                    2,
                    size_of::<RAWINPUTDEVICE>() as u32,
                );
            }
        }
        if !self.hwnd.is_null() {
            unsafe { DestroyWindow(self.hwnd) };
        }
        unsafe { UnregisterClassW(self.class_name.as_ptr(), self.instance) };
    }
}

fn run_input_thread(
    producer: rtrb::Producer<CapturedRecord>,
    inputs: Option<rtrb::Producer<ControlInputEvent>>,
    shared: Arc<CaptureShared>,
    devices: Arc<Mutex<Vec<DeviceDescriptor>>>,
    commands: Receiver<ControlRequest>,
    shutdown: Arc<AtomicBool>,
    ready: SyncSender<io::Result<(usize, u32)>>,
) {
    let mut state = Box::new(WindowState {
        capture: CaptureState::new(producer, shared),
        inputs,
        devices,
        commands,
        shutdown,
        frequency: 0,
        discovery: devices::Discovery::default(),
        paths_by_handle: HashMap::new(),
    });
    let initialization = (|| {
        let mut frequency = 0;
        if unsafe { QueryPerformanceFrequency(&mut frequency) } == 0 || frequency <= 0 {
            return Err(io::Error::other("QueryPerformanceFrequency failed"));
        }
        state.frequency = frequency as u64;
        let window = NativeWindow::create(&mut state)?;
        state.refresh_devices()?;
        Ok(window)
    })();
    let window = match initialization {
        Ok(window) => window,
        Err(error) => {
            let _ = ready.try_send(Err(error));
            return;
        }
    };
    state
        .capture
        .shared
        .raw_input_window
        .store(window.hwnd as usize, Ordering::Release);
    if ready
        .try_send(Ok((window.hwnd as usize, unsafe { GetCurrentThreadId() })))
        .is_ok()
    {
        let mut message = MSG::default();
        while !state.shutdown.load(Ordering::Acquire) {
            let result = unsafe { GetMessageW(&mut message, null_mut(), 0, 0) };
            if result <= 0 {
                if result < 0 || !state.shutdown.load(Ordering::Acquire) {
                    state.capture.disconnected();
                }
                break;
            }
            unsafe { DispatchMessageW(&message) };
        }
    }
    state.capture.stop();
    // Clear GWLP_USERDATA and destroy the window while its boxed state is still alive.
    drop(window);
    state
        .capture
        .shared
        .raw_input_window
        .store(0, Ordering::Release);
}

fn performance_ticks() -> io::Result<u64> {
    let mut ticks = 0;
    if unsafe { QueryPerformanceCounter(&mut ticks) } == 0 || ticks < 0 {
        return Err(io::Error::other("QueryPerformanceCounter failed"));
    }
    Ok(ticks as u64)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, AtomicUsize};
    use windows_sys::Win32::UI::Input::{RAWMOUSE_0, RAWMOUSE_0_0};

    fn shared() -> Arc<CaptureShared> {
        Arc::new(CaptureShared {
            active: AtomicBool::new(false),
            generation: AtomicU64::new(0),
            captured: AtomicU64::new(0),
            queued: AtomicU64::new(0),
            overflow: AtomicU64::new(0),
            high_watermark: AtomicUsize::new(0),
            disconnected: AtomicBool::new(false),
            input_dropped: AtomicU64::new(0),
            ..CaptureShared::default()
        })
    }

    fn device(handle: usize, path: &str) -> DiscoveredDevice {
        DiscoveredDevice {
            handle,
            metadata: SessionMetadata {
                device_id: path.into(),
                button_count: 5,
                ..SessionMetadata::default()
            },
            endpoint: EndpointDescriptor::default(),
        }
    }

    fn capture(capacity: usize) -> (CaptureState, rtrb::Consumer<CapturedRecord>) {
        let (producer, consumer) = rtrb::RingBuffer::new(capacity);
        let mut capture = CaptureState::new(producer, shared());
        capture
            .replace_directory(vec![device(10, "mouse-a"), device(20, "mouse-b")])
            .unwrap();
        (capture, consumer)
    }

    #[test]
    fn identity_limit_preserves_the_entire_running_directory_on_rejection() {
        let (mut state, mut consumer) = capture(8);
        state.start(1, &[1], true).unwrap();
        for index in 2..MAX_CAPTURE_DEVICE_IDENTITIES - 1 {
            state.identities.insert(format!("MOUSE-{index}"), index);
            state.device_states.push(DeviceState {
                key: (index + 1) as u32,
                ..DeviceState::default()
            });
        }
        let count = state.device_states.len();
        let error = state
            .replace_directory(vec![
                device(30, "new-a"),
                device(40, "new-b"),
                device(10, "mouse-a"),
            ])
            .unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::OutOfMemory);
        assert_eq!(state.device_states.len(), count);
        assert_eq!(state.identities.len(), count);
        assert!(state.device_states[0].present);
        assert!(state.device_states[1].present);
        assert!(state.is_active());
        assert!(state.complete());
        state.capture_mouse(10, RAWMOUSE::default(), 100);
        assert_eq!(consumer.pop().unwrap().device_key, 1);

        // Duplicate paths use one identity slot, including case differences.
        state
            .replace_directory(vec![
                device(10, "mouse-a"),
                device(30, "new-a"),
                device(40, "NEW-A"),
            ])
            .unwrap();
        assert_eq!(state.device_states.len(), MAX_CAPTURE_DEVICE_IDENTITIES);
        assert_eq!(state.handles[&30], state.handles[&40]);
    }

    #[test]
    fn device_changes_cannot_invalidate_an_already_stopped_prefix() {
        for refresh in [false, true] {
            for stopped in [false, true] {
                let (mut state, mut consumer) = capture(8);
                state.start(1, &[1], true).unwrap();
                state.capture_mouse(10, RAWMOUSE::default(), 100);
                state.pause();
                if stopped {
                    state.stop();
                }
                if refresh {
                    state
                        .replace_directory(vec![device(20, "mouse-b")])
                        .unwrap();
                } else {
                    state.remove_device(10);
                }
                assert_eq!(state.complete(), stopped);
                assert_eq!(state.shared.disconnected.load(Ordering::Acquire), !stopped);
                assert!(!state.is_active());
                assert_eq!(consumer.pop().unwrap().session_sequence, 1);
                assert!(state.resume().is_err());
                state.start(2, &[2], true).unwrap();
                assert!(state.complete());
            }
        }
    }

    #[test]
    fn activation_requires_a_paused_source_when_continuing_a_session() {
        let (mut state, _) = capture(8);
        state.start(1, &[1], true).unwrap();
        assert!(state.resume().is_err());
        assert!(state.start(1, &[1], false).is_err());
        assert!(state.is_active());
        state.pause();
        state.resume().unwrap();
        state.pause();
        state.start(1, &[1], false).unwrap();
    }

    #[test]
    fn raw_integer_payload_and_absolute_flag_are_preserved() {
        let mouse = RAWMOUSE {
            usFlags: 0x0001 | 0x0002 | 0x0008,
            Anonymous: RAWMOUSE_0 {
                Anonymous: RAWMOUSE_0_0 {
                    usButtonFlags: 0x0411,
                    usButtonData: 0xff88,
                },
            },
            lLastX: i32::MIN,
            lLastY: i32::MAX,
            ulExtraInformation: 0xdeadbeef,
            ..RAWMOUSE::default()
        };
        let record = decode_mouse(mouse, u64::MAX, 7, 3);
        assert_eq!(record.raw_delta_x, i32::MIN);
        assert_eq!(record.raw_delta_y, i32::MAX);
        assert_eq!(record.button_flags, 0x0411);
        assert_eq!(record.button_data, 0xff88);
        assert_eq!(record.raw_mouse_flags, 0x000b);
        assert_eq!(record.extra_information, 0xdeadbeef);
        assert_eq!(record.raw_capture_ticks, u64::MAX);
        assert_eq!(record.capture_sequence, 7);
        assert_eq!(record.timing_sequence, 3);
        assert_eq!(record.movement_mode, RawMouseMovementMode::Absolute);
        assert_eq!(
            decode_mouse(RAWMOUSE::default(), 0, 1, 1).movement_mode,
            RawMouseMovementMode::Relative
        );
    }

    #[test]
    fn pause_seals_and_resume_keeps_global_and_per_device_sequences() {
        let (mut state, mut consumer) = capture(8);
        state.start(1, &[1, 2], true).unwrap();
        state.capture_mouse(10, RAWMOUSE::default(), 100);
        state.capture_mouse(20, RAWMOUSE::default(), 100);
        state.pause();
        state.capture_mouse(10, RAWMOUSE::default(), 101);
        assert_eq!(state.capture_sequence, 3);
        assert_eq!(state.session_sequence, 2);
        assert!(!state.shared.active.load(Ordering::Acquire));
        state.resume().unwrap();
        state.capture_mouse(10, RAWMOUSE::default(), 102);
        let records = [
            consumer.pop().unwrap(),
            consumer.pop().unwrap(),
            consumer.pop().unwrap(),
        ];
        assert_eq!(records.map(|item| item.record.capture_sequence), [1, 2, 4]);
        assert_eq!(records.map(|item| item.record.timing_sequence), [1, 1, 3]);
        assert_eq!(records.map(|item| item.session_sequence), [1, 2, 3]);
        assert_eq!(records.map(|item| item.device_key), [1, 2, 1]);
        assert!(consumer.pop().is_err());
    }

    #[test]
    fn overflow_is_counted_and_its_attempt_is_in_the_cutoff() {
        let (mut state, mut consumer) = capture(1);
        state.start(1, &[1], true).unwrap();
        state.capture_mouse(10, RAWMOUSE::default(), 10);
        state.capture_mouse(10, RAWMOUSE::default(), 11);
        state.capture_mouse(10, RAWMOUSE::default(), 12);
        assert_eq!(state.capture_sequence, 3);
        assert_eq!(state.session_sequence, 2);
        assert_eq!(state.shared.captured.load(Ordering::Acquire), 2);
        assert_eq!(state.shared.queued.load(Ordering::Acquire), 1);
        assert_eq!(state.shared.overflow.load(Ordering::Acquire), 1);
        assert_eq!(state.shared.high_watermark.load(Ordering::Acquire), 1);
        assert!(!state.shared.active.load(Ordering::Acquire));
        assert!(state.resume().is_err());
        assert_eq!(consumer.pop().unwrap().record.capture_sequence, 1);
        assert!(consumer.pop().is_err());
    }

    #[test]
    fn directory_refresh_preserves_keys_and_same_generation_timing() {
        let (mut state, mut consumer) = capture(8);
        state.start(1, &[1], true).unwrap();
        state.capture_mouse(10, RAWMOUSE::default(), 10);
        let directory = state
            .replace_directory(vec![device(30, "MOUSE-A"), device(10, "mouse-c")])
            .unwrap();
        assert_eq!(
            directory
                .iter()
                .map(|device| device.key)
                .collect::<Vec<_>>(),
            [1, 3]
        );
        state.capture_mouse(10, RAWMOUSE::default(), 11);
        state.capture_mouse(30, RAWMOUSE::default(), 12);
        assert_eq!(consumer.pop().unwrap().record.timing_sequence, 1);
        assert_eq!(consumer.pop().unwrap().record.timing_sequence, 2);
        assert_eq!(state.capture_sequence, 3);
        assert_eq!(state.session_sequence, 2);
    }

    #[test]
    fn selected_removal_stops_capture_and_requires_a_new_generation() {
        let (mut state, _) = capture(8);
        state.start(4, &[1], true).unwrap();
        state.remove_device(10);
        assert!(state.shared.disconnected.load(Ordering::Acquire));
        assert!(!state.shared.active.load(Ordering::Acquire));
        state
            .replace_directory(vec![device(40, "mouse-a")])
            .unwrap();
        assert!(state.resume().is_err());
        assert!(state.start(4, &[1], false).is_err());
        state.start(5, &[1], true).unwrap();
        assert!(!state.shared.disconnected.load(Ordering::Acquire));
    }

    #[test]
    fn reset_tags_new_records_without_destroying_the_old_prefix() {
        let (mut state, mut consumer) = capture(8);
        state.start(7, &[1], true).unwrap();
        state.capture_mouse(10, RAWMOUSE::default(), 10);
        state.reset(8).unwrap();
        state.start(8, &[1], true).unwrap();
        state.capture_mouse(10, RAWMOUSE::default(), 20);
        let old = consumer.pop().unwrap();
        let new = consumer.pop().unwrap();
        assert_eq!((old.generation, old.record.capture_sequence), (7, 1));
        assert_eq!((new.generation, new.record.capture_sequence), (8, 2));
        assert_eq!(new.record.timing_sequence, 2);
        assert_eq!((old.session_sequence, new.session_sequence), (1, 1));
        assert!(state.reset(7).is_err());
        assert!(state.start(8, &[1], true).is_err());
    }

    #[test]
    fn invalid_selection_is_rejected_without_changing_a_running_capture() {
        let (mut state, _) = capture(8);
        state.start(1, &[1], true).unwrap();
        for keys in [vec![], vec![0], vec![3], vec![1, 1]] {
            assert!(state.start(2, &keys, true).is_err());
            assert_eq!(state.generation, 1);
            assert!(state.is_active());
        }
        state.stop();
        assert!(state.resume().is_err());
    }

    #[test]
    fn metadata_limit_rejects_before_mutating_the_running_directory() {
        let (mut state, mut consumer) = capture(8);
        state.start(1, &[1], true).unwrap();
        let oversized = device(30, &"x".repeat(MAX_CAPTURE_DEVICE_FIELD_BYTES + 1));
        assert_eq!(
            state.replace_directory(vec![oversized]).unwrap_err().kind(),
            io::ErrorKind::OutOfMemory
        );
        assert!(state.is_active());
        assert_eq!(state.identities.len(), 2);
        state.capture_mouse(10, RAWMOUSE::default(), 100);
        assert_eq!(consumer.pop().unwrap().device_key, 1);
    }

    #[test]
    fn late_startup_notification_cannot_leave_a_message_waiter_running() {
        let shutdown = Arc::new(AtomicBool::new(false));
        let thread_id = Arc::new(AtomicU32::new(0));
        let producer_shutdown = Arc::clone(&shutdown);
        let producer_id = Arc::clone(&thread_id);
        let (ready, initialized) = mpsc::sync_channel(1);
        let (release, proceed) = mpsc::sync_channel(1);
        let (finished, completed) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            publish_thread_queue(&producer_id);
            let enter_wait = !producer_shutdown.load(Ordering::Acquire);
            ready.send(()).unwrap();
            proceed.recv().unwrap();
            if enter_wait {
                let mut message = MSG::default();
                let result = unsafe { GetMessageW(&mut message, null_mut(), 0, 0) };
                finished.send((result, message.message)).unwrap();
            }
        });
        initialized.recv_timeout(CONTROL_TIMEOUT).unwrap();
        cancel_startup_thread(&shutdown, &thread_id);
        release.send(()).unwrap();
        assert_eq!(
            completed.recv_timeout(CONTROL_TIMEOUT).unwrap(),
            (0, WM_QUIT)
        );
        thread.join().unwrap();
    }

    #[test]
    fn all_source_reports_advance_before_selection_but_not_the_session_watermark() {
        let (mut state, mut consumer) = capture(8);
        state.start(1, &[1], true).unwrap();
        state.capture_mouse(20, RAWMOUSE::default(), 10);
        state.capture_mouse(30, RAWMOUSE::default(), 11);
        state.capture_mouse(0, RAWMOUSE::default(), 12);
        state.failed_attempt(20);
        assert_eq!(state.capture_sequence, 3);
        assert_eq!(state.session_sequence, 0);
        assert_eq!(state.device_states[1].timing_sequence, 1);
        assert_eq!(state.shared.overflow.load(Ordering::Acquire), 0);
        state.capture_mouse(10, RAWMOUSE::default(), 13);
        let first = consumer.pop().unwrap();
        assert_eq!(first.record.capture_sequence, 4);
        assert_eq!(first.record.timing_sequence, 1);
        assert_eq!(first.session_sequence, 1);
        state.reset(2).unwrap();
        state.start(2, &[2], true).unwrap();
        state.capture_mouse(20, RAWMOUSE::default(), 14);
        let selected = consumer.pop().unwrap();
        assert_eq!(selected.record.capture_sequence, 5);
        assert_eq!(selected.record.timing_sequence, 2);
        assert_eq!(selected.session_sequence, 1);
    }

    #[test]
    fn pause_ack_seals_and_overflow_publishes_incomplete_status() {
        let (capture, mut consumer) = capture(1);
        let (_, commands) = mpsc::sync_channel(1);
        let mut window = WindowState {
            capture,
            inputs: None,
            devices: Arc::new(Mutex::new(Vec::new())),
            commands,
            shutdown: Arc::new(AtomicBool::new(false)),
            frequency: 10_000_000,
            discovery: devices::Discovery::default(),
            paths_by_handle: HashMap::new(),
        };
        window
            .apply_command(InputCommand::Start {
                generation: 1,
                device_keys: vec![1],
                start_fresh: true,
            })
            .unwrap();
        window.capture.capture_mouse(10, RAWMOUSE::default(), 1);
        let paused = window.apply_command(InputCommand::Pause).unwrap();
        assert_eq!(paused.cutoff, 1);
        assert!(window.capture.complete());
        window.capture.capture_mouse(10, RAWMOUSE::default(), 2);
        assert_eq!(window.capture.capture_sequence, 2);
        assert_eq!(window.capture.session_sequence, 1);
        window.apply_command(InputCommand::Resume).unwrap();
        window.capture.capture_mouse(10, RAWMOUSE::default(), 3);
        let stopped = window.apply_command(InputCommand::Stop).unwrap();
        assert_eq!(stopped.cutoff, 2);
        assert_eq!(stopped.generation, 1);
        assert!(!window.capture.complete());
        assert!(!window.capture.shared.active.load(Ordering::Acquire));
        assert_eq!(consumer.pop().unwrap().record.capture_sequence, 1);
        assert!(consumer.pop().is_err());
    }

    #[test]
    fn raw_permission_revocation_keeps_source_sequences_and_requires_explicit_resume() {
        let (capture, mut consumer) = capture(8);
        let (_, commands) = mpsc::sync_channel(1);
        let mut window = WindowState {
            capture,
            inputs: None,
            devices: Arc::new(Mutex::new(Vec::new())),
            commands,
            shutdown: Arc::new(AtomicBool::new(false)),
            frequency: 10_000_000,
            discovery: devices::Discovery::default(),
            paths_by_handle: HashMap::new(),
        };
        let shared = window.capture.shared.clone();
        let ticket = shared.permit.ticket();
        window
            .apply_permitted_command(
                InputCommand::Start {
                    generation: 1,
                    device_keys: vec![1],
                    start_fresh: true,
                },
                Some(ticket),
            )
            .unwrap();
        window.capture.capture_mouse(10, RAWMOUSE::default(), 100);
        shared.permit.set(false);
        window.capture.capture_mouse(10, RAWMOUSE::default(), 101);
        shared.permit.set(true);
        assert!(
            window
                .apply_permitted_command(
                    InputCommand::Start {
                        generation: 2,
                        device_keys: vec![99],
                        start_fresh: true,
                    },
                    Some(shared.permit.ticket())
                )
                .is_err()
        );
        assert!(
            window
                .apply_permitted_command(InputCommand::Resume, Some(ticket))
                .is_err()
        );
        window.capture.capture_mouse(10, RAWMOUSE::default(), 102);
        assert_eq!(window.apply_command(InputCommand::Pause).unwrap().cutoff, 1);
        window
            .apply_permitted_command(InputCommand::Resume, Some(shared.permit.ticket()))
            .unwrap();
        window.capture.capture_mouse(10, RAWMOUSE::default(), 103);
        assert_eq!(consumer.pop().unwrap().record.capture_sequence, 1);
        let resumed = consumer.pop().unwrap();
        assert_eq!(resumed.record.capture_sequence, 4);
        assert_eq!(resumed.record.timing_sequence, 4);
        assert_eq!(resumed.session_sequence, 2);
        assert!(consumer.pop().is_err());
        assert!(window.capture.complete());
    }

    #[test]
    fn steady_selected_report_pushes_do_not_allocate() {
        let (mut state, mut consumer) = capture(16);
        state.start(1, &[1, 2], true).unwrap();
        state.capture_mouse(10, RAWMOUSE::default(), 1);
        consumer.pop().unwrap();
        let allocations = allocation_counter::measure(|| {
            for ticks in 2..10_002 {
                let handle = if ticks % 2 == 0 { 10 } else { 20 };
                state.capture_mouse(handle, RAWMOUSE::default(), ticks);
                consumer.pop().unwrap();
            }
        });
        assert_eq!(allocations.count_total, 0);
        assert_eq!(allocations.bytes_total, 0);
        assert_eq!(state.capture_sequence, 10_001);
        super::capture_measurements::report_if_requested()
            .expect("standalone dispatch measurement");
    }

    #[test]
    fn idle_activity_is_published_without_selection_or_hot_path_allocations() {
        let (mut state, consumer) = capture(8);
        let allocations = allocation_counter::measure(|| {
            state.capture_mouse(10, RAWMOUSE::default(), 100);
            state.capture_mouse(
                20,
                RAWMOUSE {
                    lLastX: 1,
                    ..Default::default()
                },
                101,
            );
            for ticks in 102..1102 {
                state.capture_mouse(20, RAWMOUSE::default(), ticks);
            }
        });
        assert_eq!(allocations.count_total, 0);
        assert_eq!(allocations.bytes_total, 0);
        let directory = state.directory();
        assert_eq!(
            directory[0].endpoint.activity,
            EndpointActivity {
                observed: true,
                motion: false
            }
        );
        assert_eq!(
            directory[1].endpoint.activity,
            EndpointActivity {
                observed: true,
                motion: true
            }
        );
        assert_eq!(state.session_sequence, 0);
        assert_eq!(consumer.slots(), 0);
        state
            .replace_directory(vec![device(10, "mouse-a"), device(20, "mouse-b")])
            .unwrap();
        assert!(state.directory()[1].endpoint.activity.motion);
    }

    #[test]
    fn metadata_tokens_and_path_summary_match_the_reference_format() {
        use clicksync_core::devices::DevicePath;
        let parsed = DevicePath::parse("HID#VID_046D&PID_C547");
        assert_eq!(parsed.vendor_id, Some(0x046d));
        assert_eq!(parsed.product_id, Some(0xc547));
        assert_eq!(DevicePath::parse("HID#XVID_046D").vendor_id, None);
        assert_eq!(DevicePath::parse("HID#VID_XXXX").vendor_id, None);
        assert_eq!(
            DevicePath::parse("\\\\?\\HID#short#{378DE44C-56EF-11D1-BC8C-00A0C91405DD}").summary,
            "HID#short"
        );
        let long = format!("{}{}", "a".repeat(40), "b".repeat(40));
        assert_eq!(
            DevicePath::parse(&long).summary,
            format!("{}...{}", "a".repeat(24), "b".repeat(18))
        );
    }

    fn arm_motion(state: &mut CaptureState, excluded: Option<u32>, capture: bool) -> u64 {
        use crate::observation::{ArmedMotion, MotionRequest};
        let id = state.shared.motion.reserve().unwrap();
        let generation = capture.then_some(state.generation + 1);
        state.shared.motion.configure(id, generation);
        let ticket = state.shared.permit.ticket();
        if let Some(generation) = generation {
            state.reset(generation).unwrap();
            state.shared.authorize(ticket);
        }
        state.observation = Some(ArmedMotion::new(
            MotionRequest {
                id,
                excluded,
                capture_generation: generation,
                timeout: None,
            },
            ticket,
            &state.directory(),
        ));
        id
    }

    #[test]
    fn pending_motion_ignores_absolute_and_empty_reports_then_delivers_first_relative_report() {
        use crate::observation::MotionResult;
        use clicksync_core::devices::{DeviceCatalog, EndpointRole};
        for capture in [false, true] {
            for reclassified in [false, true] {
                let (mut state, mut consumer) = self::capture(8);
                let pending = || {
                    let mut result = device(10, "mouse-a");
                    result.metadata.display_name = "Input Controller".into();
                    result.endpoint.role = EndpointRole::MotionPending;
                    result
                };
                if !reclassified {
                    state
                        .replace_directory(vec![pending(), device(20, "mouse-b")])
                        .unwrap();
                }
                let id = arm_motion(&mut state, None, capture);
                if reclassified {
                    state
                        .replace_directory(vec![pending(), device(20, "mouse-b")])
                        .unwrap();
                }
                assert!(state.start(2, &[1], true).is_err());
                let allocations = allocation_counter::measure(|| {
                    state.capture_mouse(
                        10,
                        RAWMOUSE {
                            usFlags: 1,
                            lLastX: 1200,
                            lLastY: 800,
                            ..Default::default()
                        },
                        100,
                    );
                    state.capture_mouse(10, RAWMOUSE::default(), 101);
                });
                assert_eq!((allocations.count_total, allocations.bytes_total), (0, 0));
                assert!(state.shared.motion.peek(id).is_none());
                assert!(consumer.pop().is_err());
                let directory = state.directory();
                assert!(directory[0].endpoint.activity.observed);
                assert!(!directory[0].endpoint.activity.motion);
                assert!(DeviceCatalog::new(&directory).choice(1).is_none());
                let allocations = allocation_counter::measure(|| {
                    state.capture_mouse(
                        10,
                        RAWMOUSE {
                            lLastX: -7,
                            lLastY: 3,
                            ..Default::default()
                        },
                        102,
                    );
                });
                assert_eq!((allocations.count_total, allocations.bytes_total), (0, 0));
                assert!(DeviceCatalog::new(&state.directory()).choice(1).is_some());
                if capture {
                    let first = consumer.pop().expect("first relative report");
                    assert_eq!(
                        (first.device_key, first.generation, first.session_sequence),
                        (1, 1, 1)
                    );
                    assert_eq!(
                        (
                            first.record.capture_sequence,
                            first.record.timing_sequence,
                            first.record.raw_capture_ticks
                        ),
                        (3, 3, 102)
                    );
                    assert_eq!(
                        (first.record.raw_delta_x, first.record.raw_delta_y),
                        (-7, 3)
                    );
                    assert!(state.shared.motion.take(id).is_none());
                    state.shared.motion.acknowledge_owner(id);
                } else {
                    assert_eq!(state.session_sequence, 0);
                }
                let Some(MotionResult::Selected(candidate)) = state.shared.motion.take(id) else {
                    panic!("relative motion candidate missing");
                };
                assert_eq!(
                    (
                        candidate.device_key,
                        candidate.capture_sequence,
                        candidate.timing_sequence
                    ),
                    (1, 3, 3)
                );
                assert!(consumer.pop().is_err());
            }
        }
    }

    #[test]
    fn non_motion_endpoints_cannot_start_or_bind_even_when_they_report_movement() {
        use clicksync_core::devices::{DeviceCatalog, EndpointRole};
        let (mut state, mut consumer) = capture(8);
        let mut control = device(10, "mouse-a");
        control.metadata.display_name = "Input Controller".into();
        control.endpoint.role = EndpointRole::NonMotion;
        state.replace_directory(vec![control]).unwrap();
        assert!(state.start(1, &[1], true).is_err());
        let id = arm_motion(&mut state, None, false);
        let allocations = allocation_counter::measure(|| {
            state.capture_mouse(
                10,
                RAWMOUSE {
                    lLastX: 5,
                    ..Default::default()
                },
                100,
            );
        });
        assert_eq!((allocations.count_total, allocations.bytes_total), (0, 0));
        assert!(state.shared.motion.take(id).is_none());
        assert!(DeviceCatalog::new(&state.directory()).choices().is_empty());
        assert!(consumer.pop().is_err());
        assert_eq!(state.session_sequence, 0);
    }

    #[test]
    fn keyboard_auxiliary_motion_is_excluded_from_start_and_first_motion_without_allocating() {
        use clicksync_core::devices::{DeviceCatalog, EndpointRole};
        let (mut state, mut consumer) = capture(8);
        let mut keyboard = device(10, "mouse-a");
        keyboard.metadata.display_name = "GL68".into();
        keyboard.endpoint.role = EndpointRole::KeyboardAuxiliary;
        state
            .replace_directory(vec![keyboard, device(20, "mouse-b")])
            .unwrap();
        assert!(state.start(1, &[1], true).is_err());
        assert_eq!(state.generation, 0);
        let id = arm_motion(&mut state, None, false);
        let movement = RAWMOUSE {
            lLastX: 7,
            ..Default::default()
        };
        let allocations = allocation_counter::measure(|| {
            for ticks in 1..=10_000 {
                state.capture_mouse(10, movement, ticks);
            }
        });
        assert_eq!((allocations.count_total, allocations.bytes_total), (0, 0));
        assert!(state.shared.motion.take(id).is_none());
        let catalog = DeviceCatalog::new(&state.directory());
        assert!(catalog.choice(1).is_none());
        assert!(catalog.choice(2).is_some());
        let allocations = allocation_counter::measure(|| {
            state.capture_mouse(20, movement, 10_001);
        });
        assert_eq!((allocations.count_total, allocations.bytes_total), (0, 0));
        let Some(crate::observation::MotionResult::Selected(candidate)) =
            state.shared.motion.take(id)
        else {
            panic!("real mouse motion missing");
        };
        assert_eq!(
            (
                candidate.device_key,
                candidate.capture_sequence,
                candidate.timing_sequence
            ),
            (2, 10_001, 1)
        );
        assert!(consumer.pop().is_err());
        assert_eq!(state.session_sequence, 0);
        assert_eq!(state.shared.captured.load(Ordering::Acquire), 0);
        assert_eq!(state.shared.input_dropped.load(Ordering::Acquire), 0);
    }

    #[test]
    fn pending_reclassification_retires_an_unconfirmed_capture_and_preserves_its_prefix() {
        use crate::observation::MotionResult;
        use clicksync_core::devices::{DeviceCatalog, EndpointRole};
        for paused in [false, true] {
            let (mut state, mut consumer) = capture(8);
            let id = arm_motion(&mut state, None, true);
            state.capture_mouse(
                10,
                RAWMOUSE {
                    usFlags: 1,
                    lLastX: 1200,
                    ..Default::default()
                },
                100,
            );
            if paused {
                state.pause();
            }
            let mut pending = device(10, "mouse-a");
            pending.endpoint.role = EndpointRole::MotionPending;
            let directory = state
                .replace_directory(vec![pending, device(20, "mouse-b")])
                .unwrap();
            assert!(DeviceCatalog::new(&directory).choice(1).is_none());
            assert!(!state.is_active());
            assert!(!state.complete());
            assert!(state.resume().is_err());
            assert!(state.start(2, &[1], true).is_err());
            assert!(state.shared.motion.take(id).is_none());
            state.shared.motion.acknowledge_owner(id);
            assert_eq!(
                state.shared.motion.take(id),
                Some(MotionResult::DeviceRemoved)
            );
            state.capture_mouse(
                10,
                RAWMOUSE {
                    lLastX: 3,
                    ..Default::default()
                },
                101,
            );
            let first = consumer.pop().unwrap();
            assert_eq!(
                (
                    first.generation,
                    first.session_sequence,
                    first.record.raw_capture_ticks
                ),
                (1, 1, 100)
            );
            assert_eq!(first.record.movement_mode, RawMouseMovementMode::Absolute);
            assert!(consumer.pop().is_err());
            assert!(DeviceCatalog::new(&state.directory()).accepts_selection(&[1]));
            state.start(2, &[1], true).unwrap();
        }
    }

    #[test]
    fn pending_reclassification_keeps_a_capture_with_confirmed_relative_motion() {
        let (mut state, mut consumer) = capture(8);
        state.start(1, &[1], true).unwrap();
        state.capture_mouse(
            10,
            RAWMOUSE {
                lLastX: 1,
                ..Default::default()
            },
            100,
        );
        let mut pending = device(10, "mouse-a");
        pending.endpoint.role = clicksync_core::devices::EndpointRole::MotionPending;
        state
            .replace_directory(vec![pending, device(20, "mouse-b")])
            .unwrap();
        assert!(state.is_active() && state.complete());
        state.pause();
        state.resume().unwrap();
        state.capture_mouse(
            10,
            RAWMOUSE {
                lLastX: 2,
                ..Default::default()
            },
            101,
        );
        assert_eq!(consumer.pop().unwrap().session_sequence, 1);
        assert_eq!(consumer.pop().unwrap().session_sequence, 2);
        assert!(consumer.pop().is_err());
    }

    #[test]
    fn keyboard_reclassification_seals_capture_and_invalidates_pending_binding() {
        use crate::observation::MotionResult;
        use clicksync_core::devices::EndpointRole;
        for role in [EndpointRole::KeyboardAuxiliary, EndpointRole::NonMotion] {
            for (paused, stopped) in [(false, false), (true, false), (true, true)] {
                let (mut state, mut consumer) = capture(8);
                let id = arm_motion(&mut state, None, true);
                let movement = RAWMOUSE {
                    lLastX: 7,
                    ..Default::default()
                };
                state.capture_mouse(10, movement, 100);
                if paused {
                    state.pause();
                }
                if stopped {
                    state.stop();
                }
                let mut keyboard = device(10, "mouse-a");
                keyboard.endpoint.role = role;
                let directory = state
                    .replace_directory(vec![keyboard, device(20, "mouse-b")])
                    .unwrap();
                assert_eq!(directory[0].key, 1);
                assert!(directory[0].endpoint.activity.motion);
                assert!(!state.is_active());
                assert_eq!(state.complete(), stopped);
                assert_eq!(state.shared.disconnected.load(Ordering::Acquire), !stopped);
                assert!(state.resume().is_err());
                assert!(state.start(1, &[1], false).is_err());
                assert!(state.start(2, &[1], true).is_err());
                assert!(matches!(
                    state.shared.motion.peek(id),
                    Some(MotionResult::Selected(_))
                ));
                assert!(state.shared.motion.take(id).is_none());
                state.shared.motion.acknowledge_owner(id);
                assert_eq!(
                    state.shared.motion.take(id),
                    Some(MotionResult::DeviceRemoved)
                );
                state.capture_mouse(10, movement, 101);
                let accepted = consumer.pop().unwrap();
                assert_eq!(
                    (
                        accepted.generation,
                        accepted.session_sequence,
                        accepted.record.capture_sequence
                    ),
                    (1, 1, 1)
                );
                assert!(consumer.pop().is_err());
                state.start(2, &[2], true).unwrap();
                state.capture_mouse(20, movement, 102);
                let next = consumer.pop().unwrap();
                assert_eq!(
                    (
                        next.generation,
                        next.device_key,
                        next.record.capture_sequence
                    ),
                    (2, 2, 3)
                );
            }
        }
    }

    #[test]
    fn motion_exclusion_covers_physical_siblings_and_survives_directory_changes() {
        let (mut state, consumer) = capture(8);
        let grouped = |handle, path: &str, group: &str| {
            let mut result = device(handle, path);
            result.endpoint.physical_device_key = group.into();
            result
        };
        state
            .replace_directory(vec![
                grouped(10, "mouse-a", "physical-a"),
                grouped(20, "control-a", "physical-a"),
                grouped(30, "mouse-b", "physical-b"),
            ])
            .unwrap();
        let id = arm_motion(&mut state, Some(1), false);
        let movement = RAWMOUSE {
            lLastX: 7,
            ..Default::default()
        };
        let ignored = allocation_counter::measure(|| {
            state.capture_mouse(10, movement, 100);
            state.capture_mouse(20, movement, 101);
        });
        assert_eq!(ignored.count_total, 0);
        assert!(state.shared.motion.take(id).is_none());
        // The excluded endpoint may disappear while another of its collections arrives.
        state
            .replace_directory(vec![
                grouped(20, "control-a", "physical-a"),
                grouped(40, "late-control-a", "physical-a"),
                grouped(30, "mouse-b", "physical-b"),
            ])
            .unwrap();
        let accepted = allocation_counter::measure(|| {
            state.capture_mouse(20, movement, 102);
            state.capture_mouse(40, movement, 103);
            state.capture_mouse(30, movement, 104);
        });
        assert_eq!(accepted.count_total, 0);
        let Some(crate::observation::MotionResult::Selected(candidate)) =
            state.shared.motion.take(id)
        else {
            panic!("independent physical mouse was not selected");
        };
        assert_eq!(candidate.device_key, 2); // mouse-b keeps its original source key.
        assert_eq!(candidate.ticks, 104);
        assert_eq!(consumer.slots(), 0);
        assert_eq!(state.session_sequence, 0);
    }

    #[test]
    fn motion_observation_does_not_allocate_or_advance_session_or_control_watermarks() {
        let (mut state, mut consumer) = capture(8);
        let id = arm_motion(&mut state, Some(1), false);
        let allocations = allocation_counter::measure(|| {
            for ticks in 1..=10_000 {
                state.capture_mouse(20, RAWMOUSE::default(), ticks);
            }
            state.capture_mouse(
                10,
                RAWMOUSE {
                    lLastX: 7,
                    ..Default::default()
                },
                10_001,
            );
            state.capture_mouse(
                20,
                RAWMOUSE {
                    lLastX: 3,
                    lLastY: 4,
                    ..Default::default()
                },
                10_001,
            );
            state.capture_mouse(
                10,
                RAWMOUSE {
                    lLastX: 9,
                    ..Default::default()
                },
                10_001,
            );
        });
        assert_eq!((allocations.count_total, allocations.bytes_total), (0, 0));
        assert!(consumer.pop().is_err());
        assert_eq!(
            (
                state.session_sequence,
                state.shared.captured.load(Ordering::Acquire),
                state.shared.input_dropped.load(Ordering::Acquire)
            ),
            (0, 0, 0)
        );
        let Some(crate::observation::MotionResult::Selected(candidate)) =
            state.shared.motion.take(id)
        else {
            panic!("first eligible motion missing");
        };
        assert_eq!(
            (
                candidate.device_key,
                candidate.ticks,
                candidate.capture_sequence,
                candidate.timing_sequence
            ),
            (2, 10_001, 10_002, 10_001)
        );
    }

    #[test]
    fn motion_calibration_first_report_and_queue_overflow_use_existing_source_semantics_without_allocation()
     {
        let (mut state, mut consumer) = capture(2);
        let id = arm_motion(&mut state, None, true);
        let allocations = allocation_counter::measure(|| {
            state.capture_mouse(
                20,
                RAWMOUSE {
                    lLastX: 13,
                    lLastY: -7,
                    usFlags: 1,
                    ..Default::default()
                },
                100,
            );
            state.capture_mouse(
                10,
                RAWMOUSE {
                    lLastX: 55,
                    ..Default::default()
                },
                100,
            );
            state.capture_mouse(
                20,
                RAWMOUSE {
                    lLastX: 17,
                    lLastY: -9,
                    ..Default::default()
                },
                100,
            );
            state.capture_mouse(
                20,
                RAWMOUSE {
                    lLastX: 21,
                    ..Default::default()
                },
                101,
            );
        });
        assert_eq!((allocations.count_total, allocations.bytes_total), (0, 0));
        let first = consumer.pop().unwrap();
        let second = consumer.pop().unwrap();
        assert_eq!(
            (
                first.device_key,
                first.session_sequence,
                first.record.raw_capture_ticks,
                first.record.raw_delta_x,
                first.record.movement_mode
            ),
            (2, 1, 100, 13, RawMouseMovementMode::Absolute)
        );
        assert_eq!(
            (
                second.device_key,
                second.session_sequence,
                second.record.raw_capture_ticks,
                second.record.capture_sequence
            ),
            (2, 2, 100, 3)
        );
        assert!(consumer.pop().is_err());
        assert_eq!(
            (
                state.shared.queued.load(Ordering::Acquire),
                state.shared.captured.load(Ordering::Acquire),
                state.shared.overflow.load(Ordering::Acquire)
            ),
            (2, 3, 1)
        );
        assert!(!state.is_active() && !state.complete());
        assert!(state.shared.motion.take(id).is_none());
        state.shared.motion.acknowledge_owner(id);
        assert!(matches!(
            state.shared.motion.take(id),
            Some(crate::observation::MotionResult::Selected(_))
        ));
    }

    #[test]
    fn observed_device_removal_and_reused_handle_keep_source_identity_and_filter_virtual_keyboard_endpoints()
     {
        let (mut state, _consumer) = capture(8);
        let id = arm_motion(&mut state, None, false);
        state.capture_mouse(
            20,
            RAWMOUSE {
                lLastX: 1,
                ..Default::default()
            },
            100,
        );
        state.remove_device(20);
        assert_eq!(
            state.shared.motion.take(id),
            Some(crate::observation::MotionResult::DeviceRemoved)
        );
        let mut virtual_device = device(30, "virtual");
        virtual_device.metadata.is_virtual = true;
        let mut keyboard = device(40, "keyboard");
        keyboard.metadata.display_name = "Mouse / Keyboard".into();
        keyboard.endpoint.role = clicksync_core::devices::EndpointRole::KeyboardAuxiliary;
        state
            .replace_directory(vec![
                device(10, "mouse-a"),
                device(20, "new-mouse"),
                virtual_device,
                keyboard,
            ])
            .unwrap();
        let id = arm_motion(&mut state, Some(1), false);
        for handle in [30, 40, 10, 20] {
            state.capture_mouse(
                handle,
                RAWMOUSE {
                    lLastX: 5,
                    ..Default::default()
                },
                200,
            );
        }
        let Some(crate::observation::MotionResult::Selected(candidate)) =
            state.shared.motion.take(id)
        else {
            panic!("replacement device not observed");
        };
        assert_eq!(candidate.device_key, 3);
    }
}
