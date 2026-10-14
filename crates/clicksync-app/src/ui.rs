use crate::chart_view::{ChartView, ChartViewReport};
use crate::gpui_bridge::{BridgeReport, GpuiBridge, SurfaceMetrics};
use crate::product::{AnalysisRequest, DomainWorker, METRICS, Page, Settings};
use crate::product_files::{FileRequest, FileResult, FileWorker, QuerySummary};
mod brand_assets;
mod business_input;
#[cfg(feature = "ui-restoration-probe")]
mod business_smoke;
mod calibration;
#[cfg(feature = "ui-restoration-probe")]
mod chart_capture_profile;
pub(crate) mod design;
mod detail;
mod device_picker;
pub(crate) mod editor;
mod keys;
mod motion_guide;
mod navigation;
#[cfg(feature = "ui-restoration-probe")]
mod page_profile;
mod performance_window;
mod polling;
#[cfg(feature = "ui-restoration-probe")]
mod preview;
#[cfg(feature = "ui-restoration-probe")]
mod probe;
mod runtime;
mod sensitivity;
mod shell;
mod workspace;
use clicksync_chart::analysis::keys::{KeyEngine, KeyIdentity};
use gpui::{
    Application, Bounds, Context, Render, Timer, TitlebarOptions, Window, WindowBounds,
    WindowOptions, canvas, div, prelude::*, px, rgb, size,
};
use serde::Serialize;
#[cfg(any(test, feature = "ui-restoration-probe"))]
use std::path::PathBuf;
use std::{
    cell::RefCell,
    rc::Rc,
    sync::{Arc, Mutex},
    time::Duration,
};

const DEFAULT_WIDTH: u32 = 1300;
const DEFAULT_HEIGHT: u32 = 800;
const MIN_WIDTH: u32 = 1240;
const MIN_HEIGHT: u32 = 800;

pub fn run() -> Result<(), Box<dyn std::error::Error>> {
    #[cfg(feature = "ui-restoration-probe")]
    if std::env::args().nth(1).as_deref() == Some("--preview-ui") {
        return preview::run(PathBuf::from(
            std::env::args().nth(2).ok_or("preview directory missing")?,
        ));
    }
    #[cfg(feature = "ui-restoration-probe")]
    if std::env::args().nth(1).as_deref() == Some("--profile-page-ui") {
        let mut arguments = std::env::args().skip(2);
        let report = arguments.next().ok_or("page profile path missing")?;
        if arguments.next().is_some() {
            return Err("too many page profile arguments".into());
        }
        return page_profile::run(PathBuf::from(report));
    }
    #[cfg(feature = "ui-restoration-probe")]
    if std::env::args().nth(1).as_deref() == Some("--profile-chart-workspace") {
        let path = std::env::args()
            .nth(2)
            .ok_or("chart profile path missing")?;
        let reports = std::env::args()
            .nth(3)
            .map(|n| n.parse())
            .transpose()?
            .unwrap_or(65_536);
        return workspace::profile::run(PathBuf::from(path), reports);
    }
    #[cfg(feature = "ui-restoration-probe")]
    if std::env::args().nth(1).as_deref() == Some("--profile-live-chart") {
        let report = std::env::args()
            .nth(2)
            .ok_or("live chart profile path missing")?;
        return chart_capture_profile::run(PathBuf::from(report));
    }
    #[cfg(feature = "ui-restoration-probe")]
    if std::env::args().nth(1).as_deref() == Some("--smoke-business-motion") {
        let mut arguments = std::env::args().skip(2);
        let report = arguments.next().ok_or("business report path missing")?;
        if arguments.next().is_some() {
            return Err("too many business probe arguments".into());
        }
        return business_smoke::run(PathBuf::from(report));
    }
    if let Some(argument) = std::env::args().nth(1) {
        return Err(format!("unsupported application argument: {argument}").into());
    }
    let outcome = Arc::new(Mutex::new(WindowSmokeReport::default()));
    let app_outcome = Arc::clone(&outcome);
    let launched = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        Application::new()
            .with_assets(design::Assets)
            .run(move |cx| {
                editor::TextInput::bind_keys(cx);
                if let Err(error) = design::install_fonts(cx) {
                    outcome_error(&app_outcome, error.to_string());
                    cx.quit();
                    return;
                }
                let close_outcome = Arc::clone(&app_outcome);
                cx.on_window_closed(move |cx| {
                    if cx.windows().is_empty() {
                        close_outcome.lock().expect("window outcome").window_closed = true;
                        cx.quit();
                    }
                })
                .detach();
                let bounds = Bounds::centered(
                    None,
                    size(px(DEFAULT_WIDTH as f32), px(DEFAULT_HEIGHT as f32)),
                    cx,
                );
                if let Err(error) = shell::open_startup(bounds, app_outcome.clone(), cx) {
                    outcome_error(&app_outcome, error.to_string());
                    cx.quit();
                }
            });
    }));
    if let Err(error) = launched {
        let message = error
            .downcast_ref::<String>()
            .cloned()
            .or_else(|| error.downcast_ref::<&str>().map(|value| value.to_string()))
            .unwrap_or_else(|| "GPUI platform initialization panicked".into());
        outcome_error(&outcome, message);
    }
    if let Some(error) = &outcome.lock().expect("window outcome").error {
        return Err(error.clone().into());
    }
    Ok(())
}

fn outcome_error(outcome: &Arc<Mutex<WindowSmokeReport>>, error: String) {
    outcome.lock().expect("window outcome").error = Some(error);
}

#[cfg(feature = "ui-restoration-probe")]
async fn activate_smoke_capture_window(
    handle: gpui::AnyWindowHandle,
    view: &gpui::Entity<GpuiHost>,
    cx: &mut gpui::AsyncWindowContext,
) -> gpui::Result<()> {
    handle.update(cx, |_, window, _| window.activate_window())?;
    // GPUI's Win32 activation runs asynchronously. Its active-window state can
    // change before Windows assigns the foreground window required for capture.
    let mut last_state = String::new();
    for attempt in 0..250 {
        Timer::after(Duration::from_millis(20)).await;
        let ready = handle.update(cx, |_, window, cx| {
            let host = view.read(cx);
            host.was_active
                && window.is_window_active()
                && unsafe {
                    windows::Win32::UI::WindowsAndMessaging::GetForegroundWindow().0 as usize
                        == host.lock.window_id()
                }
        })?;
        if ready {
            return Ok(());
        }
        if attempt % 10 == 9 {
            last_state = handle.update(cx, |_, window, cx| {
                let host = view.read(cx);
                let state = format!(
                    "active={}, observed_active={}, foreground={:?}, owner={}, polls={}",
                    window.is_window_active(),
                    host.was_active,
                    unsafe { windows::Win32::UI::WindowsAndMessaging::GetForegroundWindow() },
                    host.lock.window_id(),
                    host.runtime.borrow().polls
                );
                window.activate_window();
                state
            })?;
        }
    }
    gpui::private::anyhow::bail!(
        "product capture window did not enter the foreground: {last_state}"
    );
}

struct GpuiHost {
    bridge: Option<GpuiBridge>,
    outcome: Arc<Mutex<WindowSmokeReport>>,
    chart: Option<Rc<RefCell<ChartView>>>,
    runtime: Rc<RefCell<runtime::AppRuntime>>,
    receiver: runtime::ReceiverId,
    capture_diagnostics: Option<crate::backend::diagnostics::CaptureDiagnostics>,
    target: Option<crate::chart::ChartRequest>,
    next_request: u64,
    control_error: Option<String>,
    backend_error: Option<String>,
    page: Page,
    settings: Settings,
    selected: Vec<u32>,
    selection_initialized: bool,
    domain_frame: Option<Arc<crate::product::AnalysisFrame>>,
    #[cfg(feature = "ui-restoration-probe")]
    business_submissions: u64,
    domain_reset: u64,
    page_generation: u64,
    sensitivity_reset: u64,
    round: u64,
    round_started: f64,
    round_cancelled: u64,
    polling: polling::PollingPage,
    keys: KeyEngine,
    keys_ui: keys::KeysPage,
    business_input: business_input::BusinessInput,
    sensitivity: sensitivity::SensitivityPage,
    calibration: calibration::CalibrationPage,
    input_dropped: u64,
    lock: crate::capture_lock::CaptureLock,
    metric: usize,
    logical: bool,
    parameter_version: u64,
    view: crate::chart::ViewMode,
    file_busy: bool,
    file_message: Option<String>,
    imported: Option<(Arc<clicksync_core::SessionSnapshot>, Option<f64>)>,
    comparison: Option<(Arc<clicksync_core::SessionSnapshot>, Option<f64>)>,
    comparison_chart: Option<Rc<RefCell<ChartView>>>,
    import_identity: u64,
    displayed: Option<Arc<crate::chart::ChartResult>>,
    query: Option<QuerySummary>,
    query_request: Option<u64>,
    selection_start: Option<usize>,
    focus: gpui::FocusHandle,
    edit: Option<usize>,
    editors: Vec<gpui::Entity<editor::TextInput>>,
    last_phase: crate::backend::CapturePhase,
    persist_settings: bool,
    was_active: bool,
    device_menu: bool,
    custom_chrome: bool,
    menu: navigation::Motion,
    workspace: workspace::Workspace,
    owns_runtime: bool,
}

impl GpuiHost {
    #[cfg(feature = "ui-restoration-probe")]
    fn new(
        window: &mut Window,
        outcome: Arc<Mutex<WindowSmokeReport>>,
        result: Option<Arc<crate::chart::ChartResult>>,
        interactive: bool,
        cx: &mut Context<Self>,
    ) -> Self {
        let (runtime, error) = runtime::AppRuntime::new(interactive);
        Self::with_runtime(window, outcome, result, interactive, runtime, error, cx)
    }

    fn with_runtime(
        window: &mut Window,
        outcome: Arc<Mutex<WindowSmokeReport>>,
        result: Option<Arc<crate::chart::ChartResult>>,
        interactive: bool,
        runtime: Rc<RefCell<runtime::AppRuntime>>,
        mut control_error: Option<String>,
        cx: &mut Context<Self>,
    ) -> Self {
        let bridge = match GpuiBridge::new(window, !interactive && result.is_none()) {
            Ok(bridge) => Some(bridge),
            Err(error) => {
                outcome_error(&outcome, error.to_string());
                None
            }
        };
        let chart = result
            .map(|result| ChartView::new(result).map(|v| Rc::new(RefCell::new(v))))
            .transpose();
        let chart = match chart {
            Ok(chart) => chart,
            Err(error) => {
                outcome_error(&outcome, error);
                None
            }
        };
        if let Some(backend) = &runtime.borrow().backend {
            backend.set_capture_permitted(window.is_window_active());
        }
        let settings = runtime.borrow().settings.clone();
        let receiver = runtime.borrow_mut().attach();
        let mut keys = KeyEngine::default();
        keys.mouse_threshold_ms = settings.mouse_threshold_ms;
        keys.key_threshold_ms = settings.key_threshold_ms;
        let editors = [
            settings.cpi.to_string(),
            settings.target_cpi.to_string(),
            settings.trend_ms.to_string(),
            settings.timing_ms.to_string(),
            settings.mouse_threshold_ms.to_string(),
            settings.key_threshold_ms.to_string(),
            settings.performance_b_cpi.to_string(),
            settings.follow_window_ms.to_string(),
        ]
        .into_iter()
        .enumerate()
        .map(|(index, value)| {
            let field = cx.new(|cx| {
                let mut field = editor::TextInput::new(cx, &value);
                field.kind = if index < 2 || index == 6 {
                    editor::InputKind::Integer
                } else {
                    editor::InputKind::Decimal
                };
                field.palette = design::Palette::for_theme(settings.dark);
                field
            });
            let focus = field.read(cx).focus_handle.clone();
            cx.on_focus(&focus, window, move |host: &mut Self, _, cx| {
                if host.edit != Some(index) {
                    host.commit_edit(cx);
                    host.edit = Some(index);
                    host.editors[index].update(cx, |field, cx| field.select_entire_value(cx));
                }
            })
            .detach();
            cx.on_blur(&focus, window, move |host: &mut Self, _, cx| {
                if host.edit == Some(index) {
                    host.commit_edit(cx);
                }
            })
            .detach();
            cx.subscribe_in(
                &field,
                window,
                move |host: &mut Self, _, event, window, cx| {
                    match event {
                        editor::EditEvent::Submit => {
                            host.commit_edit(cx);
                            host.focus.focus(window);
                        }
                        editor::EditEvent::Cancel => {
                            host.cancel_edit(cx);
                            host.focus.focus(window);
                        }
                        editor::EditEvent::Traverse(backward) => {
                            host.commit_edit(cx);
                            let order = if host.page == Page::Performance && host.workspace.enabled
                            {
                                if host.workspace.dual {
                                    vec![0, 6]
                                } else {
                                    vec![0]
                                }
                            } else if host.page == Page::Keys {
                                vec![4, 5]
                            } else {
                                (0..8).collect()
                            };
                            let position =
                                order.iter().position(|item| *item == index).unwrap_or(0);
                            let next = order[if *backward {
                                (position + order.len() - 1) % order.len()
                            } else {
                                (position + 1) % order.len()
                            }];
                            host.begin_edit(next, window, cx);
                        }
                    }
                    cx.stop_propagation();
                    cx.notify();
                },
            )
            .detach();
            field
        })
        .collect();
        let lock = match crate::capture_lock::CaptureLock::for_window(window) {
            Ok(lock) => lock,
            Err(error) => {
                control_error = Some(error);
                crate::capture_lock::CaptureLock::default()
            }
        };
        let mut host = Self {
            bridge,
            outcome,
            chart,
            runtime,
            receiver,
            capture_diagnostics: None,
            target: None,
            next_request: 1,
            control_error,
            backend_error: None,
            page: Page::Performance,
            settings,
            selected: Vec::new(),
            selection_initialized: false,
            domain_frame: None,
            #[cfg(feature = "ui-restoration-probe")]
            business_submissions: 0,
            domain_reset: 0,
            page_generation: 0,
            sensitivity_reset: 0,
            round: 0,
            round_started: 0.0,
            round_cancelled: 0,
            polling: polling::PollingPage::default(),
            keys,
            keys_ui: keys::KeysPage::default(),
            business_input: business_input::BusinessInput::default(),
            sensitivity: sensitivity::SensitivityPage::default(),
            calibration: calibration::CalibrationPage::default(),
            input_dropped: 0,
            lock,
            metric: 9,
            logical: false,
            parameter_version: 0,
            view: crate::chart::ViewMode::FullSession,
            file_busy: false,
            file_message: None,
            imported: None,
            comparison: None,
            comparison_chart: None,
            import_identity: 1 << 63,
            displayed: None,
            query: None,
            query_request: None,
            selection_start: None,
            focus: cx.focus_handle(),
            edit: None,
            editors,
            last_phase: crate::backend::CapturePhase::Empty,
            persist_settings: interactive,
            was_active: window.is_window_active(),
            device_menu: false,
            custom_chrome: false,
            menu: navigation::Motion::new(false),
            workspace: workspace::Workspace {
                enabled: interactive,
                ..Default::default()
            },
            owns_runtime: true,
        };
        if interactive
            && let Some(directory) =
                std::env::var_os("CLICKSYNC_CAPTURE_REPORT_DIR").filter(|value| !value.is_empty())
        {
            host.enable_capture_diagnostics(std::path::Path::new(&directory));
        }
        host
    }

    fn enable_capture_diagnostics(&mut self, directory: &std::path::Path) {
        if self.capture_diagnostics.is_some() {
            return;
        }
        if let Some(backend) = &self.runtime.borrow().backend {
            match backend.start_diagnostics(directory, self.lock.window_id()) {
                Ok(logger) => {
                    logger.observe_window(
                        self.lock.window_id(),
                        self.was_active,
                        self.lock.locked(),
                    );
                    self.capture_diagnostics = Some(logger);
                }
                Err(error) => self.control_error = Some(format!("capture diagnostics: {error}")),
            }
        }
    }

    fn observe_capture(&self) {
        if let Some(logger) = &self.capture_diagnostics {
            logger.observe_window(self.lock.window_id(), self.was_active, self.lock.locked());
        }
    }

    fn tick_chart(&mut self, window: &Window, cx: &mut Context<Self>) -> bool {
        use crate::{backend::CapturePhase, chart::*};
        let mut events = self.runtime.borrow_mut().poll(self.receiver);
        let mut changed = self.take_file_result(events.file.take());
        changed |= self.business_events(&mut events);
        if let Some(error) = events.settings_error.take() {
            self.control_error = Some(error);
            changed = true;
        }
        if self.was_active != window.is_window_active() {
            self.activation_changed(window);
            changed = true;
        }
        if let Some((action, answer)) = events.control.take() {
            if let Ok(ack) = &answer {
                acknowledge_capture_generation(&mut self.page_generation, action, ack);
            }
            if self.page == Page::Polling && action == "discover" && answer.is_ok() {
                self.polling_discovered();
            }
            if answer.is_ok() && matches!(action, "start" | "resume" | "reset") {
                self.workspace.retire(&self.runtime.borrow());
                self.workspace.resume();
                for view in [&mut self.chart, &mut self.comparison_chart] {
                    if let Some(view) = view.take() {
                        view.borrow_mut().close();
                    }
                }
                self.target = None;
                self.displayed = None;
            }
            match answer {
                Ok(_) if action == "start" => {
                    self.imported = None;
                    self.workspace.hidden_datasets.clear();
                    self.query = None;
                    self.query_request = None;
                    self.import_identity += 1;
                    self.domain_reset += 1;
                    self.domain_frame = None;
                    self.changed_parameters();
                }
                Ok(_) if action == "reset" => {
                    self.workspace.groups.clear();
                    self.workspace.staged_group = None;
                    self.workspace.hidden_datasets.clear();
                    self.imported = None;
                    self.import_identity += 1;
                    self.domain_reset += 1;
                    self.domain_frame = None;
                    self.keys.reset_statistics();
                    self.query = None;
                    self.query_request = None;
                    self.selection_start = None;
                }
                Ok(_) if action == "resume" => {
                    self.query = None;
                    self.query_request = None;
                    self.view = ViewMode::Follow {
                        window_ms: self.settings.follow_window_ms,
                    };
                }
                Ok(_) => {}
                Err(error) => {
                    self.control_error = Some(error);
                    self.lock.release();
                }
            }
            changed = true;
        }
        if self.lock.locked() {
            if !window.is_window_active() {
                self.capture_command("pause");
                self.keys.reset_transient();
                changed = true;
            } else if let Err(error) = self.lock.confine() {
                self.control_error = Some(error);
                self.capture_command("pause");
            }
        }
        let status = self.runtime.borrow().status();
        if let Some(status) = status {
            let current = status.generation >= self.page_generation;
            if current {
                changed |= update_backend_error(&mut self.backend_error, &status);
            }
            let catalog_changed = self
                .runtime
                .borrow_mut()
                .device_catalog
                .update(&status.devices);
            changed |= catalog_changed;
            if catalog_changed
                && self.selection_initialized
                && !self.lock.locked()
                && self.runtime.borrow().pending_action().is_none()
                && matches!(self.page, Page::Polling | Page::Performance)
                && !self.workspace.chart_window
                && !(self.page == Page::Polling && self.polling.has_session())
            {
                let runtime = self.runtime.borrow();
                reconcile_setup_selection(&mut self.selected, &runtime.device_catalog);
            }
            if !self.selection_initialized && self.page != Page::Calibration {
                let runtime = self.runtime.borrow();
                self.selected = runtime
                    .device_catalog
                    .resolve_selection(self.selected.first().copied(), runtime.preferred_device)
                    .map(|key| vec![key])
                    .unwrap_or_default();
                self.selection_initialized = !self.selected.is_empty();
            }
            if current && self.page == Page::Polling {
                changed |= self.update_polling_status(&status);
            }
            if current && self.page == Page::Sensitivity {
                changed |= self.sensitivity_status(&status, cx);
            }
            if current && self.page == Page::Calibration {
                changed |= self.calibration_status(&status);
            }
            if current && status.phase != self.last_phase {
                self.last_phase = status.phase;
                changed = true;
                if matches!(
                    status.phase,
                    CapturePhase::Stopped | CapturePhase::Paused | CapturePhase::Faulted
                ) {
                    self.lock.release();
                    if let Some(backend) = &self.runtime.borrow().backend {
                        backend.set_capture_permitted(false);
                    }
                }
            }
            let input = events.inputs.take().unwrap_or_default();
            self.input_dropped = input.dropped;
            let active = window.is_window_active();
            let mut pause = false;
            for event in input.events {
                let time = event.ticks as f64 * 1000.0 / input.frequency as f64;
                changed |= self.keys_input(&event.input, time, active, self.edit.is_some());
                match event.input {
                    clicksync_core::protocol::ControlInput::Mouse { flags, data } => {
                        let _ = data;
                        if self.lock.locked() && flags & 4 != 0 {
                            pause = true;
                        }
                    }
                    clicksync_core::protocol::ControlInput::Keyboard {
                        virtual_key,
                        scan_code: _,
                        extended: _,
                        down,
                    } => {
                        if self.lock.locked() && virtual_key == 27 && down {
                            pause = true;
                        }
                    }
                }
            }
            if pause {
                self.capture_command("pause");
                changed = true;
            }
        }
        changed |= self.polling.finish_clear(std::time::Instant::now());
        let domain_prefix = self
            .runtime
            .borrow()
            .backend
            .as_ref()
            .and_then(|b| b.latest_prefix());
        if self.page == Page::Polling
            && self.polling.has_session()
            && !self.polling.is_clearing()
            && let Some(prefix) = &domain_prefix
            && prefix.generation >= self.page_generation
        {
            self.configure_polling_sampling(prefix.clone());
            changed |= self.accept_polling_sample();
        } else {
            self.runtime.borrow().polling_stream.clear(self.receiver.0);
        }
        if let Some(prefix) = domain_prefix
            && prefix.generation >= self.page_generation
            && self.imported.is_none()
            && matches!(self.page, Page::Calibration | Page::Sensitivity)
            && (self.page != Page::Sensitivity
                || self.sensitivity.should_submit(
                    &prefix,
                    self.domain_reset,
                    self.sensitivity_reset,
                    self.round,
                    self.round_cancelled,
                    self.lock.locked(),
                ))
            && (self.page != Page::Calibration
                || self.calibration.should_submit(
                    &prefix,
                    self.domain_reset,
                    self.lock.locked() && !self.calibration.rearm_pending,
                ))
        {
            let now = if self.page == Page::Sensitivity {
                self.sensitivity.now()
            } else if self.page == Page::Calibration {
                self.calibration.now()
            } else {
                crate::capture_lock::now_ms()
            };
            #[cfg(feature = "ui-restoration-probe")]
            {
                self.business_submissions += 1;
            }
            self.runtime.borrow_mut().submit_domain(
                self.receiver,
                AnalysisRequest {
                    prefix,
                    now,
                    locked: self.lock.locked() && !self.calibration.rearm_pending,
                    start_round: (self.round > 0 && self.sensitivity.requested).then_some((
                        self.round,
                        sensitivity::parse_dpi(&self.sensitivity.target_dpi)
                            .unwrap_or(self.settings.target_cpi),
                        self.round_started,
                    )),
                    cancelled_round: self.round_cancelled,
                    reset: self.domain_reset,
                    sensitivity_reset: self.sensitivity_reset,
                },
            );
        }
        if let Some(frame) = events.domain.take() {
            changed |= self.accept_business_frame(frame);
        }
        if self.page == Page::Calibration {
            changed |= self.pump_calibration_motion();
        }
        let prefix = if let Some((snapshot, _)) = &self.imported {
            Some(crate::product_files::imported_prefix(
                snapshot.clone(),
                u32::MAX - 1,
                self.import_identity,
            ))
        } else {
            self.runtime
                .borrow()
                .backend
                .as_ref()
                .and_then(|b| b.latest_prefix())
                .filter(|prefix| prefix.generation >= self.page_generation)
        };
        if self.page == Page::Performance
            && let Some(mut prefix) = prefix
        {
            if !self.workspace.groups.is_empty() {
                let mut datasets = prefix.datasets.clone();
                datasets.extend(
                    self.workspace
                        .groups
                        .iter()
                        .map(|group| (group.key, group.snapshot.clone())),
                );
                prefix = Arc::new(crate::backend::SessionPrefix {
                    datasets,
                    ..(*prefix).clone()
                });
            }
            if !self.workspace.chart_window {
                self.workspace.latest = Some(prefix);
                self.observe_capture();
                return changed;
            }
            let mut answers = std::mem::take(&mut events.monitors);
            if let Some(answer) = events.chart.take() {
                answers.push((0, answer));
            }
            changed |= self.tick_workspace(prefix, answers, events.summary.take(), window, cx);
        }
        self.observe_capture();
        changed
    }

    fn accept_business_frame(&mut self, frame: Arc<crate::product::AnalysisFrame>) -> bool {
        if frame.reset != self.domain_reset
            || frame.sensitivity_reset != self.sensitivity_reset
            || self.page == Page::Calibration
                && !self.calibration.accepts_device(frame.calibration_device)
            || self.page == Page::Sensitivity
                && (frame.sensitivity_round != self.round
                    || frame.cancelled_round != self.round_cancelled)
            || !(self.imported.is_some()
                || self
                    .runtime
                    .borrow()
                    .backend
                    .as_ref()
                    .is_some_and(|backend| {
                        backend.state().generation == frame.generation
                            && frame.generation >= self.page_generation
                    }))
        {
            return false;
        }
        if self.page == Page::Sensitivity {
            self.sensitivity_frame(&frame.sensitivity);
        }
        if self.page == Page::Calibration {
            self.calibration.frame = Some(Arc::new(frame.calibration.clone()));
        }
        self.domain_frame = Some(frame);
        true
    }

    fn capture_command(&mut self, action: &'static str) {
        if self.page == Page::Calibration && matches!(action, "start" | "resume") {
            self.begin_calibration_motion();
            return;
        }
        if matches!(action, "pause" | "stop" | "reset") {
            self.runtime.borrow_mut().cancel_motion();
            if self.page == Page::Calibration {
                self.calibration.rearm_pending = false;
                self.business_input.first = None;
                self.selected.clear();
            }
        }
        let action = if self.page == Page::Performance
            && action == "resume"
            && (self.imported.is_some()
                || self
                    .runtime
                    .borrow()
                    .status()
                    .is_some_and(|status| status.selected_device_keys != self.selected))
        {
            "start"
        } else {
            action
        };
        if !self.runtime.borrow().accepts_receiver(self.receiver) {
            return;
        }
        if self.page == Page::Performance
            && self.workspace.enabled
            && !self.workspace.chart_window
            && matches!(action, "start" | "resume" | "pause" | "stop")
        {
            return;
        }
        use clicksync_core::protocol::{CaptureSource, ControlCommand};
        let runtime = self.runtime.clone();
        let services = runtime.borrow();
        let Some(backend) = &services.backend else {
            return;
        };
        let pending = services.pending_action().is_some();
        // Admit the action before changing permission or cursor ownership.
        if pending && !matches!(action, "pause" | "stop") {
            return;
        }
        if matches!(action, "pause" | "stop" | "reset") {
            backend.set_capture_permitted(false);
        }
        if matches!(action, "start" | "resume") && !self.was_active {
            self.control_error = Some("请先激活 ClickSync 窗口".into());
            return;
        }
        let status = backend.state();
        if pending {
            self.lock.release();
            let command = if action == "pause" {
                ControlCommand::Pause
            } else {
                ControlCommand::Stop
            };
            if let Err(error) = backend.enqueue(command) {
                self.control_error = Some(error);
            }
            return;
        }
        let command = match action {
            "start" => {
                if self.selected.is_empty() {
                    self.control_error = Some("没有可用的鼠标设备".into());
                    return;
                }
                if self.page == Page::Sensitivity && self.selected.len() != 2 {
                    self.control_error = Some("灵敏度匹配需要选择两只不同鼠标".into());
                    return;
                }
                if status.phase == crate::backend::CapturePhase::Collecting {
                    return;
                }
                let source = CaptureSource::RawInput;
                #[cfg(feature = "ui-restoration-probe")]
                let source = if self.page == Page::Polling {
                    self.polling.capture_source.clone().unwrap_or(source)
                } else if self.page == Page::Performance {
                    self.workspace.capture_source.clone().unwrap_or(source)
                } else if self.page == Page::Sensitivity {
                    self.sensitivity.capture_source.clone().unwrap_or(source)
                } else {
                    source
                };
                ControlCommand::Start {
                    generation: status.generation + 1,
                    device_keys: self.selected.clone(),
                    start_fresh: true,
                    source,
                }
            }
            "pause" => ControlCommand::Pause,
            "resume" => {
                if status.phase != crate::backend::CapturePhase::Paused {
                    return;
                }
                ControlCommand::Resume
            }
            "stop" => ControlCommand::Stop,
            "reset" => ControlCommand::Reset {
                generation: status.generation + 1,
            },
            _ => return,
        };
        if action == "pause" || action == "stop" || action == "reset" {
            self.lock.release();
        }
        if (action == "start" || action == "resume")
            && self.page != Page::Sensitivity
            && let Err(error) = self.lock.acquire()
        {
            self.control_error = Some(error);
            return;
        }
        if action == "start" || action == "resume" {
            backend.set_capture_permitted(true);
        }
        drop(services);
        let enqueued = runtime
            .borrow_mut()
            .enqueue_control(self.receiver, action, command);
        match enqueued {
            Ok(()) => {
                if self.page == Page::Polling {
                    self.polling_control(action);
                }
                if self.page == Page::Performance
                    && self.workspace.enabled
                    && matches!(action, "start" | "resume")
                {
                    self.workspace.motion.cancel();
                }
                if self.page == Page::Performance
                    && self.workspace.enabled
                    && matches!(action, "start" | "reset")
                {
                    self.invalidate_analysis();
                }
                if self.page == Page::Performance
                    && self.workspace.enabled
                    && matches!(action, "start" | "resume")
                {
                    self.anchor_trajectory_capture(
                        status.generation + u64::from(action == "start"),
                        action == "resume",
                    );
                }
                self.control_error = None;
            }
            Err(error) => {
                if let Some(backend) = &runtime.borrow().backend {
                    backend.set_capture_permitted(false);
                }
                self.control_error = Some(error);
                self.lock.release();
            }
        }
        self.observe_capture();
    }

    fn activation_changed(&mut self, window: &Window) {
        self.was_active = window.is_window_active();
        if self.workspace.closing
            || self.workspace.opening
            || self.workspace.child.is_some()
            || !self.runtime.borrow().accepts_receiver(self.receiver)
        {
            return;
        }
        self.observe_capture();
        if self.was_active {
            return;
        }
        if let Some(backend) = &self.runtime.borrow().backend {
            backend.set_capture_permitted(false);
        }
        let activating = self
            .runtime
            .borrow()
            .pending_action()
            .is_some_and(|action| matches!(action, "start" | "resume"));
        let business_collecting = self.page == Page::Sensitivity
            && self
                .runtime
                .borrow()
                .status()
                .is_some_and(|status| status.phase == crate::backend::CapturePhase::Collecting);
        if self.lock.locked() || activating || business_collecting {
            self.capture_command("pause");
        }
        if self.page == Page::Sensitivity {
            self.runtime.borrow_mut().cancel_motion();
            self.business_input.binding_slot = None;
            self.cancel_sensitivity_round();
        }
        self.keys.reset_transient();
        self.keys_ui.reset_transient();
        self.cancel_selection();
        self.observe_capture();
    }

    fn text(&self, chinese: &'static str, english: &'static str) -> &'static str {
        if self.settings.english {
            english
        } else {
            chinese
        }
    }
    fn changed_parameters(&mut self) {
        self.parameter_version += 1;
        self.workspace.targets = Default::default();
        self.target = None;
        self.query = None;
        self.query_request = None;
        self.cancel_selection();
        if let Some(chart) = &self.chart {
            let viewport = chart.borrow().viewport();
            chart.borrow_mut().set_viewport(viewport, false);
        }
    }
    fn navigate(&mut self, page: Page) {
        if !self.can_navigate() || page == Page::Settings {
            return;
        }
        if self.page != page {
            self.business_input = business_input::BusinessInput::default();
            match self.page {
                Page::Polling => {
                    self.reset_for_navigation();
                    self.polling = polling::PollingPage::default();
                    self.domain_frame = None;
                }
                Page::Performance => {
                    self.workspace.retire(&self.runtime.borrow());
                    self.reset_for_navigation();
                    self.imported = None;
                    self.comparison = None;
                    self.displayed = None;
                    for chart in [&self.chart, &self.comparison_chart].into_iter().flatten() {
                        chart.borrow_mut().close();
                    }
                    self.chart = None;
                    self.comparison_chart = None;
                    self.view = crate::chart::ViewMode::FullSession;
                    self.metric = 11;
                    self.logical = false;
                    self.parameter_version += 1;
                }
                Page::Sensitivity => {
                    self.reset_for_navigation();
                    self.round = 0;
                    self.round_started = 0.0;
                    self.sensitivity_reset += 1;
                    self.domain_frame = None;
                    self.sensitivity.reset();
                }
                Page::Calibration => {
                    self.reset_for_navigation();
                    self.domain_frame = None;
                    self.calibration = calibration::CalibrationPage::default();
                }
                Page::Keys => {
                    self.keys.reset_statistics();
                    self.keys.reset_custom();
                    self.keys.reset_transient();
                    self.keys_ui = keys::KeysPage::default();
                }
                Page::Settings => {}
            }
            self.round_cancelled += 1;
            self.keys.reset_transient();
            self.cancel_selection();
            self.runtime.borrow_mut().invalidate(self.receiver);
            self.receiver = self.runtime.borrow_mut().attach();
            self.file_busy = false;
            self.query_request = None;
            self.import_identity += 1;
            self.domain_reset += 1;
            self.target = None;
            self.page = page;
            self.edit = None;
            self.device_menu = false;
            self.control_error = None;
            self.file_message = None;
            self.query = None;
            self.selected.clear();
            self.selection_initialized = false;
        }
    }
    fn reset_for_navigation(&mut self) {
        self.lock.release();
        self.page_generation = self.runtime.borrow_mut().request_page_reset();
    }
    fn can_navigate(&self) -> bool {
        !self.workspace.chart_window
            && !self.workspace.opening
            && self.workspace.child.is_none()
            && !self.lock.locked()
            && !self
                .runtime
                .borrow()
                .pending_action()
                .is_some_and(|action| matches!(action, "start" | "resume"))
    }
    fn open_menu(&mut self) {
        if !self.can_navigate() {
            return;
        }
        let brand = navigation::preferred_brand(&self.runtime.borrow(), &self.selected);
        self.keys.picking = false;
        self.device_menu = false;
        self.menu.show(brand);
    }
    fn menu_action(
        &mut self,
        action: navigation::Action,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match action {
            navigation::Action::Close => {
                self.menu.close(None);
            }
            navigation::Action::Select(page) => {
                if self.menu.accepts_input() && self.can_navigate() {
                    self.navigate(page);
                    self.menu.close(Some(page));
                    self.focus.focus(window);
                }
            }
            navigation::Action::Language if self.menu.accepts_input() => {
                self.settings.english = !self.settings.english;
                self.save_settings();
            }
            navigation::Action::Theme if self.menu.accepts_input() => {
                self.settings.dark = !self.settings.dark;
                self.save_settings();
            }
            navigation::Action::Github if self.menu.accepts_input() => {
                cx.open_url("https://github.com/Nuitfanee/ClickSyncMouseTester")
            }
            navigation::Action::Hover(index, hovered) => self.menu.entry_hover(index, hovered),
            navigation::Action::Press(page) => self.menu.pressed = page,
            navigation::Action::HeaderHover(index, hovered) => {
                self.menu.header_hover(index, hovered)
            }
            navigation::Action::Pointer(y) => self.menu.pointer(y),
            _ => {}
        }
        cx.notify();
    }
    fn save_settings(&mut self) {
        self.settings.validate();
        self.keys.mouse_threshold_ms = self.settings.mouse_threshold_ms;
        self.keys.key_threshold_ms = self.settings.key_threshold_ms;
        self.runtime
            .borrow_mut()
            .update_settings(self.settings.clone(), self.persist_settings);
    }
    fn take_file_result(&mut self, result: Option<Result<FileResult, String>>) -> bool {
        let Some(result) = result else {
            return false;
        };
        self.file_busy = false;
        if self.workspace.enabled
            && self.workspace.chart_window
            && self.take_analysis_file_result(&result)
        {
            return true;
        }
        match result {
            Ok(
                FileResult::ImportedAnalysis { .. }
                | FileResult::Duplicate { .. }
                | FileResult::ConvertedRange { .. },
            ) => {}
            Ok(FileResult::Saved(path)) => self.file_message = Some(path.display().to_string()),
            Ok(FileResult::Query(query)) => {
                if self.query_request == Some(query.request_id) {
                    self.query = Some(query);
                }
                self.query_request = None;
            }
            Err(error) => self.control_error = Some(error),
        }
        true
    }
    fn start_round(&mut self) {
        if self.page == Page::Sensitivity && !self.sensitivity_can_start() {
            return;
        }
        if self.selected.len() != 2 {
            self.control_error = Some(
                self.text("请选择两只不同鼠标", "Select two different mice")
                    .into(),
            );
            return;
        }
        if self
            .domain_frame
            .as_ref()
            .is_some_and(|f| f.sensitivity.active.is_some() || f.sensitivity.result.is_some())
        {
            return;
        }
        let status = self.runtime.borrow().status();
        if let Some(status) = status
            && status.phase != crate::backend::CapturePhase::Collecting
        {
            if sensitivity::can_resume_capture(&status, &self.selected) {
                self.capture_command("resume");
            } else {
                self.capture_command("start");
            }
        }
        self.round += 1;
        self.round_started = self.sensitivity.now();
        self.sensitivity.requested = true;
        self.sensitivity.copied = false;
    }
    fn reset_measurements(&mut self) {
        if self.lock.locked() || self.page == Page::Sensitivity && self.sensitivity.requested {
            self.capture_command("pause");
        }
        self.sensitivity.requested = false;
        self.sensitivity.frame = None;
        self.sensitivity.expired = false;
        self.sensitivity.copied = false;
        self.sensitivity_reset += 1;
        if let Some(frame) = &mut self.domain_frame {
            let frame = Arc::make_mut(frame);
            frame.sensitivity =
                clicksync_chart::analysis::sensitivity::SensitivityEngine::default().frame();
            frame.sensitivity_reset = self.sensitivity_reset;
        }
    }
    fn cancel_selection(&mut self) {
        self.selection_start = None;
        for chart in [&self.chart, &self.comparison_chart].into_iter().flatten() {
            chart.borrow_mut().end_selection();
        }
    }
    fn edit_value(&self, index: usize) -> String {
        match index {
            0 => self.settings.cpi.to_string(),
            1 => self.settings.target_cpi.to_string(),
            2 => self.settings.trend_ms.to_string(),
            3 => self.settings.timing_ms.to_string(),
            4 => keys::format_threshold(self.settings.mouse_threshold_ms),
            5 => keys::format_threshold(self.settings.key_threshold_ms),
            6 => self.settings.performance_b_cpi.to_string(),
            _ => self.settings.follow_window_ms.to_string(),
        }
    }
    fn begin_edit(&mut self, index: usize, window: &mut Window, cx: &mut Context<Self>) {
        self.commit_edit(cx);
        self.edit = Some(index);
        let value = self.edit_value(index);
        self.editors[index].update(cx, |field, cx| {
            field.set_value(&value, cx);
            field.select_entire_value(cx);
            field.focus_handle.focus(window);
        });
    }
    fn cancel_edit(&mut self, cx: &mut Context<Self>) {
        if let Some(index) = self.edit.take() {
            let value = self.edit_value(index);
            self.editors[index].update(cx, |field, cx| field.set_value(&value, cx));
        }
    }
    fn commit_edit(&mut self, cx: &mut Context<Self>) {
        if let Some(index) = self.edit.take() {
            let text = self.editors[index].read(cx).content.to_string();
            if self.page == Page::Keys && matches!(index, 4 | 5) {
                let value = KeyEngine::normalize_threshold(text.trim().parse().unwrap_or(80.0));
                if index == 4 {
                    self.settings.mouse_threshold_ms = value;
                } else {
                    self.settings.key_threshold_ms = value;
                }
                self.save_settings();
                self.control_error = None;
                let value = self.edit_value(index);
                self.editors[index].update(cx, |field, cx| field.set_value(&value, cx));
                return;
            }
            let Ok(value) = text.parse::<f64>() else {
                self.control_error = Some(
                    self.text("请输入有效的正数", "Enter a valid positive number")
                        .into(),
                );
                let value = self.edit_value(index);
                self.editors[index].update(cx, |field, cx| field.set_value(&value, cx));
                return;
            };
            if !value.is_finite()
                || value <= 0.0
                || ((index < 2 || index == 6) && (value.fract() != 0.0 || value > i32::MAX as f64))
            {
                self.control_error = Some(
                    self.text(
                        "请输入有效的正数；DPI 必须为整数",
                        "Enter a positive number; DPI must be an integer",
                    )
                    .into(),
                );
                let value = self.edit_value(index);
                self.editors[index].update(cx, |field, cx| field.set_value(&value, cx));
                return;
            }
            let old_dpi = (self.settings.cpi, self.settings.target_cpi);
            match index {
                0 => self.settings.cpi = value as i32,
                1 => self.settings.target_cpi = value as i32,
                2 => self.settings.trend_ms = value,
                3 => self.settings.timing_ms = value,
                4 => self.settings.mouse_threshold_ms = value,
                5 => self.settings.key_threshold_ms = value,
                6 => self.settings.performance_b_cpi = value as i32,
                _ => self.settings.follow_window_ms = value,
            }
            self.settings.validate();
            if old_dpi != (self.settings.cpi, self.settings.target_cpi) {
                self.reset_measurements();
            }
            self.changed_parameters();
            self.save_settings();
            self.control_error = None;
            let value = self.edit_value(index);
            self.editors[index].update(cx, |field, cx| field.set_value(&value, cx));
        }
    }
    fn keyboard(
        &mut self,
        event: &gpui::KeyDownEvent,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let key = event.keystroke.key.as_str();
        if self.menu.visible() {
            if key == "enter" && !event.is_held && self.menu.interactive() {
                self.menu.close(None);
            }
            cx.stop_propagation();
            cx.notify();
            return;
        }
        if self.edit.is_some()
            || self
                .sensitivity
                .editor
                .iter()
                .any(|field| field.read(cx).focus_handle.is_focused(window))
            || self
                .workspace
                .detail_editors
                .iter()
                .any(|field| field.read(cx).focus_handle.is_focused(window))
        {
            // EntityInputHandler and the focused editor own text and editing
            // keys. Raw Input does not append this text a second time.
            return;
        } else if self.device_menu
            || self.workspace.monitor_menu
            || self.workspace.detail_menu
            || self.workspace.data_menu
        {
            if key == "escape" && !event.is_held {
                self.device_menu = false;
                self.workspace.monitor_menu = false;
                self.workspace.detail_menu = false;
                self.workspace.data_menu = false;
                cx.notify();
            }
            cx.stop_propagation();
            return;
        } else if self.page == Page::Keys && self.keys.picking {
            // The independent Raw Input path owns key identity and picking,
            // including Escape and the ignored first release.
            cx.stop_propagation();
            return;
        } else if !event.is_held {
            if self.page == Page::Performance
                && self.workspace.enabled
                && self.lock.locked()
                && !event.keystroke.modifiers.control
                && !event.keystroke.modifiers.alt
                && !event.keystroke.modifiers.platform
                && !event.keystroke.modifiers.shift
                && let Ok(preset) = key.parse::<usize>()
                && (1..=3).contains(&preset)
            {
                self.toggle_monitor(workspace::DEFAULT_MONITORS[preset - 1]);
                cx.stop_propagation();
                cx.notify();
                return;
            }
            match key {
                "escape" => {
                    if self.keys.picking {
                        self.keys.picking = false;
                    } else if self.workspace.chart_window
                        && self.workspace.detail.is_some()
                        && !self.lock.locked()
                    {
                        self.show_plot_overview();
                    } else {
                        self.capture_command("pause");
                    }
                }
                "s" if !event.keystroke.modifiers.control
                    && !event.keystroke.modifiers.alt
                    && !event.keystroke.modifiers.platform
                    && !event.keystroke.modifiers.shift
                    && (self.page == Page::Sensitivity
                        || (self.page == Page::Performance && self.workspace.chart_window)) =>
                {
                    if self.page == Page::Sensitivity {
                        self.start_round();
                    } else if self.lock.locked() {
                        self.capture_command("pause");
                    } else {
                        if !self.was_active && window.is_window_active() {
                            self.activation_changed(window);
                        }
                        let resume = self.runtime.borrow().backend.as_ref().is_some_and(|b| {
                            b.state().phase == crate::backend::CapturePhase::Paused
                        }) && self.imported.is_none();
                        self.capture_command(if resume { "resume" } else { "start" });
                    }
                }
                "q" if event.keystroke.modifiers.control => {
                    if self.workspace.chart_window {
                        self.return_to_performance_setup(window, cx);
                    } else {
                        self.close_performance_child(cx);
                        self.capture_command("stop");
                    }
                    window.remove_window();
                }
                _ => {}
            }
        }
        cx.notify();
    }

    fn bounds_changed(&mut self, window: &Window) {
        self.outcome
            .lock()
            .expect("window outcome")
            .bounds_callbacks += 1;
        self.sync_bridge(window);
    }

    fn sync_bridge(&mut self, window: &Window) {
        if let Some(bridge) = &mut self.bridge {
            if let Err(error) = bridge.sync(window) {
                outcome_error(&self.outcome, error.to_string());
                bridge.close();
            }
            self.outcome.lock().expect("window outcome").bridge = Some(bridge.report());
        }
    }

    fn release_surface(&mut self) {
        if let Some(backend) = &self.runtime.borrow().backend {
            backend.set_capture_permitted(false);
        }
        self.lock.release();
        self.observe_capture();
        if let Some(chart) = &self.chart {
            self.outcome.lock().expect("window outcome").chart = Some(chart.borrow().report());
            chart.borrow_mut().close();
        }
        if let Some(bridge) = &mut self.bridge {
            bridge.close();
            self.outcome.lock().expect("window outcome").bridge = Some(bridge.report());
        }
    }

    fn shutdown(&mut self) {
        if !self.owns_runtime && self.workspace.closing {
            drop(self.capture_diagnostics.take());
            return;
        }
        self.workspace.retire(&self.runtime.borrow());
        self.release_surface();
        self.runtime.borrow_mut().invalidate(self.receiver);
        if self.owns_runtime
            && let Err(error) = self.runtime.borrow_mut().shutdown()
        {
            outcome_error(&self.outcome, error);
        }
        // GPUI tasks may retain this entity after the window closes. Finalize
        // diagnostics explicitly after capture drains, before the event loop exits.
        drop(self.capture_diagnostics.take());
    }
}

impl Drop for GpuiHost {
    fn drop(&mut self) {
        self.shutdown();
    }
}

impl Render for GpuiHost {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.sync_bridge(window);
        if self.menu.visible() {
            let bounds = window.viewport_size();
            let geometry = navigation::geometry(f32::from(bounds.width), f32::from(bounds.height));
            if self
                .menu
                .update(std::time::Instant::now(), geometry, &self.settings)
            {
                window.request_animation_frame();
            }
        }
        self.outcome.lock().expect("window outcome").frame_count += 1;
        if self.custom_chrome && self.menu.visible() && self.menu.coverage >= 1.0 {
            return shell::frame(div().id("covered-product").size_full(), self, window, cx);
        }
        if self.page == Page::Keys {
            let content = self.render_keys(window, cx);
            return if self.custom_chrome {
                shell::frame(content, self, window, cx)
            } else {
                content
            };
        }
        if self.page == Page::Polling {
            let content = self.render_polling(window, cx);
            return if self.custom_chrome {
                shell::frame(content, self, window, cx)
            } else {
                content
            };
        }
        if self.page == Page::Sensitivity {
            let content = self.render_sensitivity(window, cx);
            return if self.custom_chrome {
                shell::frame(content, self, window, cx)
            } else {
                content
            };
        }
        if self.page == Page::Calibration {
            let content = self.render_calibration(window, cx);
            return if self.custom_chrome {
                shell::frame(content, self, window, cx)
            } else {
                content
            };
        }
        if self.page == Page::Performance && self.workspace.enabled {
            let content = if self.workspace.chart_window {
                self.render_workspace(window, cx)
            } else {
                self.render_performance_setup(window, cx)
            };
            return if self.custom_chrome {
                shell::frame(content, self, window, cx)
            } else {
                content
            };
        }
        let content = self.render_performance_setup(window, cx);
        if self.custom_chrome {
            shell::frame(content, self, window, cx)
        } else {
            content
        }
    }
}

fn reconcile_setup_selection(
    selected: &mut Vec<u32>,
    catalog: &clicksync_core::devices::DeviceCatalog,
) {
    for index in 0..selected.len() {
        let Some(replacement) = catalog.resolve_same_device(selected[index]) else {
            selected.truncate(index);
            return;
        };
        selected[index] = replacement;
        if !catalog.accepts_selection(&selected[..=index]) {
            selected.truncate(index);
            return;
        }
    }
}

fn acknowledge_capture_generation(
    minimum: &mut u64,
    action: &str,
    ack: &clicksync_core::protocol::CaptureAck,
) {
    if ack.error.is_none() && matches!(action, "start" | "resume" | "reset") {
        // A control reply precedes owner publication; never consume the retired generation.
        *minimum = (*minimum).max(ack.generation);
    }
}

fn capture_incomplete(status: &crate::backend::AppStatus) -> bool {
    status.phase == crate::backend::CapturePhase::Faulted
        || !status.complete
        || status.overflow > 0
        || status.ignored_after_limit > 0
}

fn capture_status_error(status: &crate::backend::AppStatus) -> Option<&str> {
    status
        .error
        .as_deref()
        .filter(|error| !error.is_empty())
        .or_else(|| {
            if status.phase == crate::backend::CapturePhase::Faulted {
                Some("Capture failed; reset and measure again.")
            } else if capture_incomplete(status) {
                Some("Capture data is incomplete; reset and measure again.")
            } else {
                None
            }
        })
}

fn update_backend_error(error: &mut Option<String>, status: &crate::backend::AppStatus) -> bool {
    let next = capture_status_error(status);
    if error.as_deref() == next {
        return false;
    }
    *error = next.map(str::to_owned);
    true
}

fn displayed_error(control: &Option<String>, backend: &Option<String>) -> Option<String> {
    match (control, backend) {
        (Some(control), Some(backend)) if control != backend => {
            Some(format!("{control}\n{backend}"))
        }
        (Some(error), _) | (_, Some(error)) => Some(error.clone()),
        _ => None,
    }
}
fn localized_state(value: &str, english: bool) -> &str {
    if english {
        return value;
    }
    match value {
        "Empty" => "就绪",
        "Collecting" => "采集中",
        "Paused" => "已暂停",
        "Stopped" => "已停止",
        "Faulted" => "采集失败",
        "Stabilizing" => "稳定确认",
        "None" => "—",
        "Excellent" => "优秀",
        "Good" => "良好",
        "Fair" => "一般",
        "Poor" => "较差",
        "Timeout" => "超时",
        "TooFast" => "移动过快",
        "InsufficientPackets" => "数据包不足",
        "Unsynchronized" => "两只鼠标未同步",
        "ExcessiveCurvature" => "路径弯曲过大",
        "DirectionMismatch" => "方向不一致",
        "PathShapeMismatch" => "路径形状不一致",
        _ => value,
    }
}

#[derive(Default, Serialize)]
struct WindowSmokeReport {
    frame_count: u64,
    window_closed: bool,
    bounds_callbacks: u64,
    sizes: Vec<SurfaceMetrics>,
    bridge: Option<BridgeReport>,
    error: Option<String>,
    chart: Option<ChartViewReport>,
}

#[cfg(feature = "ui-restoration-probe")]
fn write_probe_frame(
    path: &std::path::Path,
    frame: gpui::windows_graphics::WindowsFrame,
    executor: &gpui::BackgroundExecutor,
) -> gpui::Task<gpui::Result<()>> {
    let path = path.to_owned();
    // Pixel conversion, compression and disk writes must never occupy the window thread.
    executor.spawn(async move {
        let mut encoder = png::Encoder::new(
            std::io::BufWriter::new(std::fs::File::create(path)?),
            frame.width,
            frame.height,
        );
        encoder.set_color(png::ColorType::Rgba);
        let pixels: Vec<_> = frame
            .bgra
            .chunks_exact(4)
            .flat_map(|p| [p[2], p[1], p[0], p[3]])
            .collect();
        let mut writer = encoder.write_header()?;
        writer.write_image_data(&pixels)?;
        writer.finish()?;
        Ok(())
    })
}

#[cfg(all(test, feature = "ui-restoration-probe"))]
mod probe_frame_tests {
    use super::*;

    #[test]
    fn background_png_export_preserves_pixels_and_propagates_write_errors() {
        let executor = gpui::background_executor();
        let directory =
            std::env::temp_dir().join(format!("clicksync-probe-png-{}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        let path = directory.join("frame.png");
        let frame = || gpui::windows_graphics::WindowsFrame {
            width: 2,
            height: 1,
            bgra: vec![10, 20, 30, 255, 40, 50, 60, 255],
        };
        executor
            .block(write_probe_frame(&path, frame(), &executor))
            .unwrap();
        let decoder = png::Decoder::new(std::io::Cursor::new(std::fs::read(&path).unwrap()));
        let mut reader = decoder.read_info().unwrap();
        let mut pixels = vec![0; reader.output_buffer_size().unwrap()];
        let info = reader.next_frame(&mut pixels).unwrap();
        assert_eq!((info.width, info.height), (2, 1));
        assert_eq!(pixels, [30, 20, 10, 255, 60, 50, 40, 255]);
        assert!(
            executor
                .block(write_probe_frame(&directory, frame(), &executor))
                .is_err(),
            "a directory cannot be a PNG destination"
        );
        std::fs::remove_file(path).unwrap();
        std::fs::remove_dir(directory).unwrap();
    }
}

#[cfg(test)]
mod capture_error_tests {
    use super::*;
    use crate::backend::{AppStatus, CapturePhase};

    #[test]
    fn setup_reclassification_replaces_siblings_and_clears_invalid_slots_without_shifting_b() {
        use clicksync_core::devices::{DeviceCatalog, DeviceDescriptor, EndpointRole};
        let descriptor = |key, group: &str| {
            let mut device = DeviceDescriptor {
                key,
                ..Default::default()
            };
            device.metadata.device_id = format!("device-{key}");
            device.metadata.display_name = "Input Device".into();
            device.metadata.button_count = 5;
            device.endpoint.physical_device_key = group.into();
            device
        };
        let mut retired = descriptor(1, "group-a");
        let second = descriptor(2, "group-b");
        for role in [
            EndpointRole::NonMotion,
            EndpointRole::MotionPending,
            EndpointRole::KeyboardAuxiliary,
        ] {
            retired.endpoint.role = role;
            let catalog = DeviceCatalog::new(&[retired.clone(), second.clone()]);
            let mut selected = vec![1, 2];
            reconcile_setup_selection(&mut selected, &catalog);
            assert!(selected.is_empty());
            let mut selected = vec![2, 1];
            reconcile_setup_selection(&mut selected, &catalog);
            assert_eq!(selected, [2]);
        }
        let mut replacement = descriptor(3, "group-a");
        replacement.endpoint.activity.observe(1, 0);
        let catalog = DeviceCatalog::new(&[retired, second, replacement]);
        let mut selected = vec![1, 2];
        reconcile_setup_selection(&mut selected, &catalog);
        assert_eq!(selected, [3, 2]);
        let mut duplicate = vec![1, 3];
        reconcile_setup_selection(&mut duplicate, &catalog);
        assert_eq!(duplicate, [3]);
        let mut disconnected = vec![2, 999];
        reconcile_setup_selection(&mut disconnected, &catalog);
        assert_eq!(disconnected, [2]);
    }

    #[test]
    fn capture_ack_floor_never_regresses_or_advances_for_a_rejection() {
        use clicksync_core::protocol::{CaptureAck, CommandTag};
        let mut ack = CaptureAck {
            command: CommandTag::Start,
            generation: 12,
            cutoff: 0,
            queued: 0,
            ticks: 0,
            timestamp_frequency: 1,
            complete: true,
            overflow: 0,
            queue_high_watermark: 0,
            devices: Vec::new(),
            error: None,
        };
        for action in ["start", "resume", "reset"] {
            let mut minimum = 11;
            acknowledge_capture_generation(&mut minimum, action, &ack);
            assert_eq!(minimum, 12);
            minimum = 13;
            acknowledge_capture_generation(&mut minimum, action, &ack);
            assert_eq!(minimum, 13);
        }
        let mut minimum = 11;
        acknowledge_capture_generation(&mut minimum, "discover", &ack);
        assert_eq!(minimum, 11);
        ack.error = Some("command rejected".into());
        acknowledge_capture_generation(&mut minimum, "start", &ack);
        assert_eq!(minimum, 11);
    }

    #[test]
    fn backend_errors_are_independent_from_control_errors_across_ack_and_generation_changes() {
        let healthy = AppStatus {
            complete: true,
            ..Default::default()
        };
        let mut backend = None;
        let control = Some("same diagnostic".to_owned());
        assert!(!update_backend_error(&mut backend, &healthy));
        let mut fault = AppStatus {
            phase: CapturePhase::Faulted,
            error: control.clone(),
            ..healthy.clone()
        };
        assert!(update_backend_error(&mut backend, &fault));
        assert_eq!(displayed_error(&control, &backend), control);
        assert!(!update_backend_error(&mut backend, &fault));
        assert!(update_backend_error(&mut backend, &healthy));
        assert_eq!(displayed_error(&control, &backend), control);
        // A successful ACK can clear control text before the old owner state retires.
        assert!(update_backend_error(&mut backend, &fault));
        assert!(displayed_error(&None, &backend).is_some());
        assert!(update_backend_error(&mut backend, &healthy));
        assert!(displayed_error(&None, &backend).is_none());
        fault.error = Some("new capture fault".into());
        assert!(update_backend_error(&mut backend, &fault));
        assert_eq!(
            displayed_error(&control, &backend).as_deref(),
            Some("same diagnostic\nnew capture fault")
        );
        assert_eq!(control.as_deref(), Some("same diagnostic"));
        fault.error = None;
        assert!(update_backend_error(&mut backend, &fault));
        assert!(backend.unwrap().contains("Capture failed"));
    }
}
