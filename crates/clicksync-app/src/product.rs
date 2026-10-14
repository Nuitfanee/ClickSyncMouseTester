use crate::backend::{CapturePhase, SessionPrefix};
pub mod performance_analysis;
pub mod performance_summary;
pub(crate) mod polling_stream;
use clicksync_chart::{
    Axis, ChartParameters, HistogramPolicy, HistogramScale, HistogramSpec, Metric, TimeBasis,
    analysis::{
        calibration::{CalibrationEngine, CalibrationFrame},
        keys::KeyEngine,
        sensitivity::{MatchFrame, Packet, SensitivityEngine},
    },
    gpu::PlotKind,
};
use performance_summary::{SummaryFrame, SummaryRequest, SummaryState};
use serde::{Deserialize, Serialize};
use std::{
    path::PathBuf,
    sync::{Arc, Condvar, Mutex},
    thread::{self, JoinHandle},
};

pub const SENSITIVITY_DISTANCE_CM: f64 = 10.0;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum Page {
    Performance,
    #[default]
    Polling,
    Keys,
    Sensitivity,
    Calibration,
    Settings,
}
impl Page {
    pub const ALL: [Self; 5] = [
        Self::Polling,
        Self::Sensitivity,
        Self::Performance,
        Self::Calibration,
        Self::Keys,
    ];
    pub fn label(self, english: bool) -> &'static str {
        match (self, english) {
            (Self::Performance, false) => "鼠标性能分析",
            (Self::Performance, true) => "Mouse Performance",
            (Self::Polling, false) => "回报率检测",
            (Self::Polling, true) => "Report Rate",
            (Self::Keys, false) => "按键双击检测",
            (Self::Keys, true) => "Key Detection",
            (Self::Sensitivity, false) => "鼠标灵敏度复制",
            (Self::Sensitivity, true) => "Sensitivity Match",
            (Self::Calibration, false) => "传感器角度校准",
            (Self::Calibration, true) => "Angle Calibration",
            (Self::Settings, false) => "设置",
            (Self::Settings, true) => "Settings",
        }
    }
    pub fn menu_meta(self, english: bool) -> &'static str {
        match (self, english) {
            (Self::Polling, false) => "REPORT RATE TEST",
            (Self::Polling, true) => "RAW INPUT REPORT MONITOR",
            (Self::Sensitivity, false) => "SENSITIVITY MATCH TOOL",
            (Self::Sensitivity, true) => "MATCH TWO MOUSE FEELS",
            (Self::Performance, _) => "MOUSE PERFORMANCE ANALYSIS",
            (Self::Calibration, false) => "ANGLE CALIBRATION",
            (Self::Calibration, true) => "MEASURE SWIPE ANGLE",
            (Self::Keys, false) => "BUTTON / WHEEL / KEY CHECK",
            (Self::Keys, true) => "BUTTON / WHEEL / KEY TIMING",
            (Self::Settings, _) => "",
        }
    }
}
pub const DEFAULT_MONITORS: [usize; 3] = [5, 0, 1];

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct Settings {
    pub english: bool,
    pub dark: bool,
    pub cpi: i32,
    pub target_cpi: i32,
    pub performance_b_cpi: i32,
    pub follow_window_ms: f64,
    pub monitor_metrics: Vec<usize>,
    pub trend_ms: f64,
    pub timing_ms: f64,
    pub mouse_threshold_ms: f64,
    pub key_threshold_ms: f64,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            english: default_english(),
            dark: true,
            cpi: 800,
            target_cpi: 800,
            performance_b_cpi: 800,
            follow_window_ms: 800.0,
            monitor_metrics: DEFAULT_MONITORS.to_vec(),
            trend_ms: 12.0,
            timing_ms: 75.0,
            mouse_threshold_ms: 80.0,
            key_threshold_ms: 80.0,
        }
    }
}
impl Settings {
    pub fn path() -> PathBuf {
        std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(std::env::temp_dir)
            .join("ClickSync/settings.json")
    }
    pub fn load() -> Self {
        Self::load_from(&Self::path())
    }
    pub fn load_from(path: &std::path::Path) -> Self {
        let mut settings: Self = std::fs::read(path)
            .ok()
            .and_then(|b| serde_json::from_slice(&b).ok())
            .unwrap_or_default();
        settings.monitor_metrics = DEFAULT_MONITORS.to_vec();
        settings.validate();
        settings
    }
    pub fn validate(&mut self) {
        if self.cpi <= 0 {
            self.cpi = 800;
        }
        if self.target_cpi <= 0 {
            self.target_cpi = 800;
        }
        if self.performance_b_cpi <= 0 {
            self.performance_b_cpi = 800;
        }
        // Presentation parameters are product defaults, not persisted user controls.
        self.follow_window_ms = 800.0;
        self.trend_ms = 12.0;
        self.timing_ms = 75.0;
        let mut metrics = Vec::new();
        for index in &self.monitor_metrics {
            if *index < METRICS.len() && *index != 11 && !metrics.contains(index) {
                metrics.push(*index);
            }
        }
        self.monitor_metrics = metrics;
        let options =
            clicksync_chart::AnalysisOptions::new(self.trend_ms, 3, self.timing_ms, 7, 0.1, 60.0);
        self.trend_ms = options.trend_window_ms;
        self.timing_ms = options.timing_window_ms;
        self.mouse_threshold_ms = KeyEngine::normalize_threshold(self.mouse_threshold_ms);
        self.key_threshold_ms = KeyEngine::normalize_threshold(self.key_threshold_ms);
    }
    pub fn parameters(
        &self,
        index: usize,
        histogram: bool,
        logical: bool,
        version: u64,
    ) -> ChartParameters {
        let index = index % METRICS.len();
        let histogram = histogram && supports_histogram(index);
        let metric = match (index, histogram) {
            (17, true) => Metric::DeltaMagnitude,
            (19, true) => Metric::VelocityMagnitude,
            _ => METRICS[index].0,
        };
        let mut parameters = ChartParameters::new(metric);
        parameters.cpi = Some(self.cpi as f64);
        parameters.parameter_version = version;
        parameters.time_basis = if logical {
            TimeBasis::Logical
        } else {
            TimeBasis::RawCapture
        };
        parameters.options =
            clicksync_chart::AnalysisOptions::new(self.trend_ms, 3, self.timing_ms, 7, 0.1, 60.0);
        parameters.histogram =
            (histogram && parameters.metric != Metric::Trajectory).then_some(HistogramSpec {
                policy: HistogramPolicy::Automatic,
                scale: HistogramScale::Percent,
            });
        parameters
    }
}

fn default_english() -> bool {
    #[cfg(windows)]
    {
        let language = unsafe { windows::Win32::Globalization::GetUserDefaultUILanguage() };
        language & 0x03ff != 0x04
    }
    #[cfg(not(windows))]
    {
        true
    }
}
pub const METRICS: [(Metric, &str, &str); 20] = [
    (Metric::Count(Axis::X), "X 计数", "X counts"),
    (Metric::Count(Axis::Y), "Y 计数", "Y counts"),
    (Metric::DeltaMagnitude, "位移大小", "Delta magnitude"),
    (Metric::CountTrend(Axis::X), "X 趋势", "X trend"),
    (Metric::CountTrend(Axis::Y), "Y 趋势", "Y trend"),
    (Metric::Interval, "报告时间间隔", "Report interval"),
    (Metric::RawFrequency, "原始频率", "Raw frequency"),
    (Metric::IntervalTrend, "间隔趋势", "Interval trend"),
    (Metric::Frequency, "报告频率", "Report frequency"),
    (Metric::Cumulative(Axis::X), "X 累计", "X cumulative"),
    (Metric::Cumulative(Axis::Y), "Y 累计", "Y cumulative"),
    (Metric::Trajectory, "轨迹", "Trajectory"),
    (Metric::PathLength, "路径长度", "Path length"),
    (Metric::Velocity(Axis::X), "X 速度", "X velocity"),
    (Metric::Velocity(Axis::Y), "Y 速度", "Y velocity"),
    (Metric::VelocityMagnitude, "速度大小", "Velocity magnitude"),
    (Metric::PathSpeed, "路径速度", "Path speed"),
    (Metric::Count(Axis::X), "XY 计数", "XY counts"),
    (Metric::Cumulative(Axis::X), "XY 累计", "XY cumulative"),
    (Metric::Velocity(Axis::X), "XY 速度", "XY velocity"),
];
pub fn secondary_metric(index: usize, histogram: bool) -> Option<Metric> {
    if histogram && supports_histogram(index) {
        return None;
    }
    match index {
        17 => Some(Metric::Count(Axis::Y)),
        18 => Some(Metric::Cumulative(Axis::Y)),
        19 => Some(Metric::Velocity(Axis::Y)),
        _ => None,
    }
}
fn supports_histogram(index: usize) -> bool {
    index != 18 && METRICS[index % METRICS.len()].0 != Metric::Trajectory
}
pub fn plot_kind(index: usize, style: usize, histogram: bool) -> PlotKind {
    let metric = METRICS[index % METRICS.len()].0;
    if metric == Metric::Trajectory {
        PlotKind::Trajectory
    } else if histogram && supports_histogram(index) {
        PlotKind::Histogram
    } else {
        match style % 3 {
            0 => PlotKind::Lines,
            1 => PlotKind::Points,
            _ => PlotKind::Stems,
        }
    }
}
#[derive(Clone)]
pub struct AnalysisRequest {
    pub prefix: Arc<SessionPrefix>,
    pub now: f64,
    pub locked: bool,
    pub start_round: Option<(u64, i32, f64)>,
    pub cancelled_round: u64,
    pub reset: u64,
    pub sensitivity_reset: u64,
}
#[derive(Clone, Serialize)]
pub struct AnalysisFrame {
    pub generation: u64,
    pub reset: u64,
    pub sensitivity_reset: u64,
    pub sensitivity_round: u64,
    pub cancelled_round: u64,
    pub received_sequence: u64,
    pub processed: [usize; 2],
    pub calibration: CalibrationFrame,
    pub calibration_device: Option<u32>,
    pub sensitivity: MatchFrame,
}
#[derive(Default)]
struct DomainState {
    identity: Option<(u64, u64)>,
    processed: [usize; 2],
    segment: u32,
    calibration: CalibrationEngine,
    calibration_device: Option<u32>,
    sensitivity: SensitivityEngine,
    sensitivity_reset: u64,
    round: u64,
    cancelled_round: u64,
    phase: Option<CapturePhase>,
}
impl DomainState {
    fn advance(
        &mut self,
        request: &AnalysisRequest,
        cancel: &std::sync::atomic::AtomicBool,
    ) -> Option<AnalysisFrame> {
        if self.identity != Some((request.prefix.generation, request.reset)) {
            // An empty prepare prefix has no device yet. Keep the measurement
            // until the next first-motion latch identifies its actual source.
            let calibration = self
                .identity
                .filter(|(_, reset)| *reset == request.reset)
                .map(|_| {
                    (
                        std::mem::take(&mut self.calibration),
                        self.calibration_device,
                    )
                });
            *self = Self::default();
            if let Some((mut calibration, device)) = calibration {
                calibration.set_locked(false);
                self.calibration = calibration;
                self.calibration_device = device;
            }
            self.identity = Some((request.prefix.generation, request.reset));
            self.sensitivity_reset = request.sensitivity_reset;
        }
        if let Some((device, _)) = request.prefix.datasets.first() {
            if self
                .calibration_device
                .is_some_and(|previous| previous != *device)
            {
                self.calibration.reset();
            }
            self.calibration_device = Some(*device);
        }
        if self.sensitivity_reset != request.sensitivity_reset {
            self.sensitivity.reset();
            self.sensitivity_reset = request.sensitivity_reset;
            self.round = 0;
        }
        if self.cancelled_round != request.cancelled_round {
            self.sensitivity.cancel();
            self.cancelled_round = request.cancelled_round;
        }
        if let Some((id, target_dpi, time)) = request.start_round
            && id != self.round
        {
            self.sensitivity
                .start(target_dpi, SENSITIVITY_DISTANCE_CM, time);
            self.round = id;
        }
        let datasets = &request.prefix.datasets;
        if self.phase != Some(request.prefix.phase) && request.locked {
            self.calibration.set_locked(true);
        }
        loop {
            let next = (0..datasets.len().min(2))
                .filter_map(|side| {
                    datasets[side]
                        .1
                        .event_at(self.processed[side])
                        .map(|event| (side, event))
                })
                .min_by_key(|(_, e)| e.raw_capture_ticks);
            let Some((side, event)) = next else {
                break;
            };
            if self.processed[side].is_multiple_of(4096)
                && cancel.load(std::sync::atomic::Ordering::Acquire)
            {
                return None;
            }
            let frequency = datasets[side].1.timestamp_frequency();
            let time = event.raw_capture_ticks as f64 * 1000.0 / frequency as f64;
            if side == 0 {
                if event.session_segment_id != self.segment {
                    if self.segment != 0 {
                        self.calibration.set_locked(false);
                        self.calibration.set_locked(request.locked);
                    }
                    self.segment = event.session_segment_id;
                }
                self.calibration
                    .push(event.raw_delta_x, event.raw_delta_y, time);
            }
            self.sensitivity.push(
                side == 1,
                Packet {
                    time,
                    dx: event.raw_delta_x,
                    dy: event.raw_delta_y,
                },
            );
            self.processed[side] += 1;
        }
        self.phase = Some(request.prefix.phase);
        self.calibration.set_locked(request.locked);
        self.sensitivity.update(request.now);
        Some(AnalysisFrame {
            generation: request.prefix.generation,
            reset: request.reset,
            sensitivity_reset: request.sensitivity_reset,
            sensitivity_round: self.round,
            cancelled_round: self.cancelled_round,
            received_sequence: request.prefix.received_sequence,
            processed: self.processed,
            calibration: self.calibration.frame(request.now),
            calibration_device: self.calibration_device,
            sensitivity: self.sensitivity.frame(),
        })
    }
}
#[derive(Default)]
struct Mailbox {
    pending: Option<(u64, DomainRequest)>,
    result: Option<(u64, Arc<AnalysisFrame>)>,
    summary: Option<(u64, Arc<SummaryFrame>)>,
    closing: bool,
}
enum DomainRequest {
    Business(AnalysisRequest),
    Performance(SummaryRequest),
}
pub struct DomainWorker {
    mailbox: Arc<(Mutex<Mailbox>, Condvar)>,
    cancel: Arc<std::sync::atomic::AtomicBool>,
    thread: Option<JoinHandle<()>>,
}
impl DomainWorker {
    pub fn spawn() -> Self {
        let mailbox = Arc::new((Mutex::new(Mailbox::default()), Condvar::new()));
        let cancel = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let worker_mailbox = mailbox.clone();
        let worker_cancel = cancel.clone();
        let thread = thread::spawn(move || {
            let mut state = DomainState::default();
            let mut summary = SummaryState::default();
            loop {
                let (receiver, request) = {
                    let mut mailbox = worker_mailbox.0.lock().unwrap();
                    while mailbox.pending.is_none() && !mailbox.closing {
                        mailbox = worker_mailbox.1.wait(mailbox).unwrap();
                    }
                    if mailbox.closing {
                        break;
                    }
                    mailbox.pending.take().unwrap()
                };
                match request {
                    DomainRequest::Business(request) => {
                        if let Some(frame) = state.advance(&request, &worker_cancel) {
                            worker_mailbox.0.lock().unwrap().result =
                                Some((receiver, Arc::new(frame)));
                        }
                    }
                    DomainRequest::Performance(request) => {
                        if let Some(frame) = summary.advance(request, &worker_cancel) {
                            worker_mailbox.0.lock().unwrap().summary =
                                Some((receiver, Arc::new(frame)));
                        }
                    }
                }
            }
        });
        Self {
            mailbox,
            cancel,
            thread: Some(thread),
        }
    }
    pub fn submit(&self, request: AnalysisRequest) {
        self.submit_for(0, request);
    }
    pub fn submit_for(&self, receiver: u64, request: AnalysisRequest) {
        self.mailbox.0.lock().unwrap().pending = Some((receiver, DomainRequest::Business(request)));
        self.mailbox.1.notify_one();
    }
    pub fn take(&self) -> Option<Arc<AnalysisFrame>> {
        self.take_completion().map(|(_, frame)| frame)
    }
    pub fn take_completion(&self) -> Option<(u64, Arc<AnalysisFrame>)> {
        self.mailbox.0.lock().unwrap().result.take()
    }
    pub fn submit_summary_for(&self, receiver: u64, request: SummaryRequest) {
        self.mailbox.0.lock().unwrap().pending =
            Some((receiver, DomainRequest::Performance(request)));
        self.mailbox.1.notify_one();
    }
    pub fn take_summary_completion(&self) -> Option<(u64, Arc<SummaryFrame>)> {
        self.mailbox.0.lock().unwrap().summary.take()
    }
    pub fn shutdown(&mut self) {
        self.cancel
            .store(true, std::sync::atomic::Ordering::Release);
        self.mailbox.0.lock().unwrap().closing = true;
        self.mailbox.1.notify_one();
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}
impl Drop for DomainWorker {
    fn drop(&mut self) {
        self.shutdown();
    }
}

/// Deterministic product preview, using the same domain state and algorithms as
/// capture. The window report labels this as controlled data, separately from
/// its actual Raw Input registration and control lifecycle.
pub fn smoke_domain_frame() -> Result<Arc<AnalysisFrame>, String> {
    use clicksync_core::{
        RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SessionMetadata,
        SharedByteBudget,
    };
    let config = SessionConfig {
        timestamp_frequency: 1_000_000,
        chunk_capacity: 64,
        ..Default::default()
    };
    let mut session = Session::new(
        config.clone(),
        SharedByteBudget::new(config.managed_memory_budget_bytes),
        SessionMetadata::default(),
    )
    .map_err(|e| e.to_string())?;
    session
        .begin_collecting(1_000_000, true, false)
        .map_err(|e| e.to_string())?;
    let mut state = DomainState::default();
    let mut final_frame = None;
    let mut now = 1000.0;
    for stroke in 0..44 {
        for i in 0..60 {
            let sequence = stroke * 60 + i + 1;
            let direction = if stroke % 2 == 0 { 1 } else { -1 };
            now = 1000.0 + sequence as f64 * 3.0;
            session
                .append(RawMouseRecord::new(
                    10 * direction,
                    direction,
                    (now * 1000.0) as u64,
                    sequence as u64,
                    sequence as u64,
                    RawMouseMovementMode::Relative,
                ))
                .map_err(|e| e.to_string())?;
        }
        final_frame = state.advance(
            &AnalysisRequest {
                prefix: Arc::new(SessionPrefix {
                    generation: 1,
                    received_sequence: session.event_count() as u64,
                    received_records: session.event_count() as u64,
                    phase: CapturePhase::Collecting,
                    complete: true,
                    datasets: vec![(1, Arc::new(session.snapshot().map_err(|e| e.to_string())?))],
                }),
                now,
                locked: true,
                start_round: None,
                cancelled_round: 0,
                reset: 0,
                sensitivity_reset: 0,
            },
            &std::sync::atomic::AtomicBool::new(false),
        );
    }
    let mut frame = final_frame.ok_or("product domain preview cancelled")?;
    state.calibration.set_locked(false);
    frame.calibration = state.calibration.frame(now);
    let mut sensitivity = SensitivityEngine::default();
    sensitivity.start(254, SENSITIVITY_DISTANCE_CM, 0.0);
    for i in 0..110 {
        let time = 500.0 + i as f64 * 10.0;
        sensitivity.push(
            false,
            Packet {
                time,
                dx: 20,
                dy: 0,
            },
        );
        sensitivity.push(
            true,
            Packet {
                time,
                dx: 10,
                dy: 0,
            },
        );
    }
    sensitivity.update(2000.0);
    frame.sensitivity = sensitivity.frame();
    if frame.calibration.swipe_count < 30 || frame.sensitivity.result.is_none() {
        return Err("product preview did not reach domain acceptance states".into());
    }
    Ok(Arc::new(frame))
}

#[cfg(test)]
mod settings_tests {
    use super::*;

    #[test]
    fn monitor_defaults_are_interval_x_and_y_without_overriding_live_choices() {
        let mut settings = Settings::default();
        assert_eq!(settings.monitor_metrics, [5, 0, 1]);
        assert_eq!(
            settings
                .monitor_metrics
                .iter()
                .map(|&index| METRICS[index].0)
                .collect::<Vec<_>>(),
            [
                Metric::Interval,
                Metric::Count(Axis::X),
                Metric::Count(Axis::Y)
            ]
        );
        settings.monitor_metrics = vec![16, 17];
        settings.validate();
        assert_eq!(settings.monitor_metrics, [16, 17]);
    }

    #[test]
    fn loading_settings_initializes_monitors_and_preserves_other_preferences() {
        let path = std::env::temp_dir().join(format!(
            "clicksync-monitor-defaults-{}.json",
            std::process::id()
        ));
        let settings = Settings {
            cpi: 1600,
            monitor_metrics: vec![16, 5, 17],
            ..Default::default()
        };
        std::fs::write(&path, serde_json::to_vec(&settings).unwrap()).unwrap();
        let loaded = Settings::load_from(&path);
        std::fs::remove_file(path).unwrap();
        assert_eq!(loaded.monitor_metrics, [5, 0, 1]);
        assert_eq!(loaded.cpi, settings.cpi);
        assert_eq!(loaded.dark, settings.dark);
        assert_eq!(loaded.english, settings.english);
    }
}

#[cfg(test)]
mod business_tests {
    use super::*;
    #[test]
    fn calibration_rearm_preserves_only_the_same_devices_accepted_samples() {
        let prefix = |generation, key, start: f64, strokes| {
            let config = clicksync_core::SessionConfig {
                timestamp_frequency: 1_000_000,
                ..Default::default()
            };
            let mut session = clicksync_core::Session::new(
                config,
                clicksync_core::SharedByteBudget::new(4 * 1024 * 1024 * 1024),
                clicksync_core::SessionMetadata::default(),
            )
            .unwrap();
            session
                .begin_collecting((start * 1000.0) as u64, true, false)
                .unwrap();
            for stroke in 0..strokes {
                let direction = if stroke % 2 == 0 { 1 } else { -1 };
                for i in 0..60 {
                    let sequence = stroke * 60 + i + 1;
                    session
                        .append(clicksync_core::RawMouseRecord::new(
                            direction * 10,
                            direction,
                            (start * 1000.0) as u64 + sequence as u64 * 3000,
                            sequence as u64,
                            sequence as u64,
                            clicksync_core::RawMouseMovementMode::Relative,
                        ))
                        .unwrap();
                }
            }
            session.pause();
            Arc::new(SessionPrefix {
                generation,
                received_sequence: session.event_count() as u64,
                received_records: session.event_count() as u64,
                phase: CapturePhase::Paused,
                complete: true,
                datasets: vec![(key, Arc::new(session.snapshot().unwrap()))],
            })
        };
        let mut request = AnalysisRequest {
            prefix: prefix(1, 1, 1000.0, 20),
            now: 4600.0,
            locked: false,
            start_round: None,
            cancelled_round: 0,
            reset: 1,
            sensitivity_reset: 0,
        };
        let mut state = DomainState::default();
        let cancel = std::sync::atomic::AtomicBool::new(false);
        let before = state.advance(&request, &cancel).unwrap();
        assert!(before.calibration.swipe_count > 10 && before.calibration.swipe_count < 30);
        assert!(before.calibration.recommended_angle.is_none());
        // Prepare can publish an empty generation before the first report.
        request.prefix = Arc::new(SessionPrefix {
            generation: 2,
            received_sequence: 0,
            received_records: 0,
            phase: CapturePhase::Paused,
            complete: true,
            datasets: Vec::new(),
        });
        let preparing = state.advance(&request, &cancel).unwrap();
        assert_eq!(preparing.calibration_device, Some(1));
        assert_eq!(
            preparing.calibration.sample_count,
            before.calibration.sample_count
        );
        request.prefix = prefix(2, 1, 5000.0, 20);
        request.now = 8600.0;
        let after = state.advance(&request, &cancel).unwrap();
        assert_eq!(after.calibration.sample_count, 2400);
        assert!(after.calibration.swipe_count >= 30);
        assert!(after.calibration.recommended_angle.is_some());
        assert!(after.calibration.traces.iter().all(|trace| !trace.current));
        request.prefix = prefix(3, 2, 9000.0, 20);
        request.now = 12600.0;
        let changed_device = state.advance(&request, &cancel).unwrap();
        assert_eq!(changed_device.calibration_device, Some(2));
        assert_eq!(changed_device.calibration.sample_count, 1200);
        assert_eq!(
            changed_device.calibration.swipe_count,
            before.calibration.swipe_count
        );
        assert_eq!(
            changed_device.calibration.accepted_strokes,
            before.calibration.accepted_strokes
        );
        assert!(changed_device.calibration.recommended_angle.is_none());
        request.prefix = prefix(4, 2, 13000.0, 0);
        request.reset += 1;
        let cleared = state.advance(&request, &cancel).unwrap();
        assert!(!cleared.calibration.has_data);
        assert_eq!(cleared.calibration.sample_count, 0);
        assert!(cleared.calibration.recommended_angle.is_none());
    }
    #[test]
    fn measurement_reset_and_new_round_in_one_request_still_starts_the_real_engine() {
        let mut session = clicksync_core::Session::new(
            clicksync_core::SessionConfig::default(),
            clicksync_core::SharedByteBudget::new(4 * 1024 * 1024 * 1024),
            clicksync_core::SessionMetadata::default(),
        )
        .unwrap();
        session.begin_collecting(1, true, false).unwrap();
        let prefix = Arc::new(SessionPrefix {
            generation: 1,
            received_sequence: 0,
            received_records: 0,
            phase: CapturePhase::Collecting,
            complete: true,
            datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
        });
        let mut request = AnalysisRequest {
            prefix,
            now: 1000.0,
            locked: false,
            start_round: Some((1, 100, 1000.0)),
            cancelled_round: 0,
            reset: 0,
            sensitivity_reset: 0,
        };
        let mut state = DomainState::default();
        let cancel = std::sync::atomic::AtomicBool::new(false);
        let active = state
            .advance(&request, &cancel)
            .unwrap()
            .sensitivity
            .active
            .unwrap();
        assert!((active.target_goal - 393.7007874015748).abs() < 1e-10);
        assert_eq!(
            active.stage,
            clicksync_chart::analysis::sensitivity::Stage::Preparing
        );
        request.sensitivity_reset = 1;
        request.start_round = Some((2, 100, 2000.0));
        request.now = 2000.0;
        assert!(
            state
                .advance(&request, &cancel)
                .unwrap()
                .sensitivity
                .active
                .is_some()
        );
    }
}
