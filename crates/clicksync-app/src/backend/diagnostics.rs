use super::*;
use clicksync_capture::{CaptureShared, delivery::DeliverySnapshot};
use std::{
    fs::OpenOptions,
    io::{BufWriter, Write},
};
use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId};

/// No event payloads or file I/O on the capture/UI threads. Counters are sampled
/// independently and cannot prove USB delivery or uninterrupted foreground time.
pub struct CaptureDiagnostics {
    observation: Arc<Mutex<UiObservation>>,
    stop: mpsc::SyncSender<()>,
    thread: Option<JoinHandle<Result<(), String>>>,
    pub path: PathBuf,
}

#[derive(Clone, Copy)]
struct UiObservation {
    window_id: usize,
    flags: u64,
}

impl CaptureDiagnostics {
    pub fn observe(&self, active: bool, locked: bool) {
        let mut observation = self.observation.lock().expect("diagnostic observation");
        let window_id = observation.window_id;
        Self::update_observation(&mut observation, window_id, active, locked);
    }

    pub fn observe_window(&self, window_id: usize, active: bool, locked: bool) {
        let mut observation = self.observation.lock().expect("diagnostic observation");
        Self::update_observation(&mut observation, window_id, active, locked);
    }

    fn update_observation(
        observation: &mut UiObservation,
        window_id: usize,
        active: bool,
        locked: bool,
    ) {
        let flags = u64::from(active) | u64::from(locked) << 1;
        if observation.window_id != window_id || observation.flags & 3 != flags {
            observation.window_id = window_id;
            observation.flags = (observation.flags & !3).wrapping_add(4) | flags;
        }
    }
}

impl Drop for CaptureDiagnostics {
    fn drop(&mut self) {
        let _ = self.stop.try_send(());
        if let Some(thread) = self.thread.take() {
            match thread.join() {
                Ok(Ok(())) => {}
                Ok(Err(error)) => eprintln!("capture diagnostics failed: {error}"),
                Err(_) => eprintln!("capture diagnostics thread panicked"),
            }
        }
    }
}

impl Backend {
    pub fn start_diagnostics(
        &self,
        directory: &Path,
        window_id: usize,
    ) -> Result<CaptureDiagnostics, String> {
        if directory.as_os_str().is_empty() {
            return Err("capture diagnostic directory must not be empty".into());
        }
        let shared = self
            .local_shared
            .clone()
            .ok_or("diagnostics require in-process capture")?;
        std::fs::create_dir_all(directory).map_err(|e| e.to_string())?;
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|e| e.to_string())?;
        let path = directory.join(format!(
            "capture-{}-{}.jsonl",
            std::process::id(),
            stamp.as_nanos()
        ));
        let file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)
            .map_err(|e| e.to_string())?;
        let (stop, receiver) = mpsc::sync_channel(1);
        let observation = Arc::new(Mutex::new(UiObservation {
            window_id,
            flags: 0,
        }));
        let observed = observation.clone();
        let state = self.state.clone();
        let raw_input_window = shared.raw_input_window.load(Ordering::Acquire);
        shared.delivery.enabled.store(true, Ordering::Release);
        let thread = thread::Builder::new().name("clicksync-capture-diagnostics".into())
            .spawn(move || {
                let mut output = BufWriter::new(file);
                writeln!(output, "{}", serde_json::json!({
                    "type":"identity","schema_version":2,"app_process_id":std::process::id(),
                    "capture_process_id":std::process::id(),"window_id":window_id,
                    "source":if raw_input_window == 0 { "controlled replay" } else { "Windows Raw Input" },
                    "raw_input_window":raw_input_window,"started_unix_ms":stamp.as_millis(),
                    "counter_scope":"cumulative decoded mouse reports since diagnostics enabled; selected counters follow current producer selection, including paused periods",
                    "boundary":"sampled process/foreground identity and independent counters; rates spanning observed transitions are null; no USB-loss or uninterrupted-focus guarantee",
                })).map_err(|e| e.to_string())?;
                let origin = Instant::now();
                let mut previous = None;
                loop {
                    let stopped = !matches!(receiver.recv_timeout(Duration::from_millis(250)), Err(mpsc::RecvTimeoutError::Timeout));
                    let observation = *observed.lock().expect("diagnostic observation");
                    let sample = Sample::read(&state, &shared, observation, origin);
                    write_sample(&mut output, &sample, previous.as_ref(), stopped)?;
                    output.flush().map_err(|e| e.to_string())?;
                    previous = Some(sample);
                    if stopped { return Ok(()); }
                }
            }).map_err(|e| e.to_string())?;
        Ok(CaptureDiagnostics {
            observation,
            stop,
            thread: Some(thread),
            path,
        })
    }
}

struct Sample {
    elapsed: f64,
    state: AppStatus,
    delivery: DeliverySnapshot,
    generation: u64,
    queued: u64,
    captured: u64,
    permit: u64,
    active: bool,
    observation: u64,
    target_window: usize,
    foreground_window: usize,
    foreground_pid: u32,
}
impl Sample {
    fn read(
        state: &Mutex<AppStatus>,
        shared: &CaptureShared,
        observation: UiObservation,
        origin: Instant,
    ) -> Self {
        let foreground = unsafe { GetForegroundWindow() };
        let mut foreground_pid = 0;
        unsafe { GetWindowThreadProcessId(foreground, Some(&mut foreground_pid)) };
        Self {
            elapsed: origin.elapsed().as_secs_f64(),
            state: state.lock().expect("application status lock").clone(),
            delivery: shared.delivery.snapshot(),
            generation: shared.generation.load(Ordering::Acquire),
            queued: shared.queued.load(Ordering::Acquire),
            captured: shared.captured.load(Ordering::Acquire),
            permit: shared.permit.ticket(),
            active: shared.is_active(),
            observation: observation.flags,
            target_window: observation.window_id,
            foreground_window: foreground.0 as usize,
            foreground_pid,
        }
    }
    fn rates(&self, before: &Self) -> Option<(f64, f64)> {
        let elapsed = self.elapsed - before.elapsed;
        if elapsed <= 0.0
            || self.generation != before.generation
            || self.generation != self.state.generation
            || before.generation != before.state.generation
            || self.permit != before.permit
            || self.observation != before.observation
            || self.target_window != before.target_window
            || self.foreground_window != before.foreground_window
            || self.state.phase != before.state.phase
            || self.active != before.active
            || self.state.command_pending
            || before.state.command_pending
            || self.state.selected_device_keys != before.state.selected_device_keys
        {
            return None;
        }
        let count =
            |d: DeliverySnapshot| d.selected_foreground + d.selected_background + d.selected_other;
        Some((
            count(self.delivery).checked_sub(count(before.delivery))? as f64 / elapsed,
            self.state
                .received_records
                .checked_sub(before.state.received_records)? as f64
                / elapsed,
        ))
    }
}
fn write_sample(
    output: &mut impl Write,
    s: &Sample,
    previous: Option<&Sample>,
    final_sample: bool,
) -> Result<(), String> {
    let rates = previous.and_then(|p| s.rates(p));
    let d = s.delivery;
    writeln!(output, "{}", serde_json::json!({
        "type":"sample","elapsed_seconds":s.elapsed,"final_sample":final_sample,
        "foreground_pid":s.foreground_pid,"foreground_window":s.foreground_window,
        "target_window":s.target_window,"target_foreground":s.foreground_window==s.target_window,
        "ui_observation_version":s.observation>>2,"ui_active":s.observation&1!=0,
        "cursor_locked":s.observation&2!=0,
        "producer_generation":s.generation,"permit_ticket":s.permit,"producer_active":s.active,
        "producer_captured":s.captured,"producer_queued":s.queued,
        "accepted_backlog":if s.generation==s.state.generation {Some(s.queued.saturating_sub(s.state.received_sequence))} else {None},
        "mouse_rim_input":d.foreground,"mouse_rim_inputsink":d.background,"mouse_other":d.other,
        "selected_rim_input":d.selected_foreground,"selected_rim_inputsink":d.selected_background,
        "selected_other":d.selected_other,"selected_delivery_per_second":rates.map(|r|r.0),
        "ingested_per_second":rates.map(|r|r.1),"state":s.state,
    })).map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn sampled_rates_reject_generation_focus_and_permission_boundaries() {
        let shared = CaptureShared::default();
        let state = Mutex::new(AppStatus::default());
        let origin = Instant::now();
        let observation = UiObservation {
            window_id: 123,
            flags: 3,
        };
        let mut a = Sample::read(&state, &shared, observation, origin);
        let mut b = Sample::read(&state, &shared, observation, origin);
        a.elapsed = 1.0;
        b.elapsed = 2.0;
        a.foreground_window = 123;
        b.foreground_window = 123;
        b.delivery.selected_foreground = 4000;
        b.state.received_records = 4000;
        assert_eq!(b.rates(&a), Some((4000.0, 4000.0)));
        b.permit += 2;
        assert!(b.rates(&a).is_none());
        b.permit = a.permit;
        b.observation += 4;
        assert!(b.rates(&a).is_none());
        b.observation = a.observation;
        b.target_window = 456;
        assert!(b.rates(&a).is_none());
        b.foreground_window = 456;
        let mut output = Vec::new();
        write_sample(&mut output, &b, Some(&a), false).unwrap();
        let sample: serde_json::Value = serde_json::from_slice(&output).unwrap();
        assert_eq!(sample["target_window"], 456);
        assert_eq!(sample["target_foreground"], true);
        assert!(sample["ingested_per_second"].is_null());
        b.target_window = a.target_window;
        b.generation += 1;
        assert!(b.rates(&a).is_none());
    }

    #[test]
    fn empty_output_directory_is_rejected_and_replay_is_labeled() {
        let mut backend = Backend::spawn_in_process(true).unwrap();
        assert!(backend.start_diagnostics(Path::new(""), 1).is_err());
        let directory = std::env::temp_dir().join(format!(
            "clicksync-diagnostics-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let logger = backend.start_diagnostics(&directory, 1).unwrap();
        let path = logger.path.clone();
        logger.observe(true, false);
        logger.observe_window(2, true, false);
        backend.shutdown().unwrap();
        drop(logger);
        let lines: Vec<serde_json::Value> = std::fs::read_to_string(&path)
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();
        assert_eq!(lines[0]["source"], "controlled replay");
        assert_eq!(lines[0]["schema_version"], 2);
        assert_eq!(lines.last().unwrap()["final_sample"], true);
        assert_eq!(lines.last().unwrap()["producer_active"], false);
        assert_eq!(lines.last().unwrap()["ui_active"], true);
        assert_eq!(lines.last().unwrap()["cursor_locked"], false);
        assert_eq!(lines.last().unwrap()["target_window"], 2);
        assert_eq!(lines.last().unwrap()["ui_observation_version"], 2);
        std::fs::remove_file(path).unwrap();
        std::fs::remove_dir(directory).unwrap();
    }
}
