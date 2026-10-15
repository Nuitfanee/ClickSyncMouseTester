//! Opt-in visual contact sheets. No capture, preferences, assertions or baselines.
use super::*;

pub(super) fn run(directory: PathBuf) -> Result<(), Box<dyn std::error::Error>> {
    std::fs::create_dir_all(&directory)?;
    let filter = std::env::var("CLICKSYNC_PREVIEW_FILTER").ok();
    let make_snapshot =
        |display_name: &str,
         count: u64,
         disconnected: bool|
         -> Result<Arc<clicksync_core::SessionSnapshot>, Box<dyn std::error::Error>> {
            let mut session =
                clicksync_core::Session::default_with_metadata(clicksync_core::SessionMetadata {
                    display_name: display_name.into(),
                    ..Default::default()
                })?;
            session.begin_collecting(100, true, false)?;
            for index in 0..count {
                session.append(clicksync_core::RawMouseRecord::new(
                    (6.0 * (index as f64 * 0.018).sin()).round() as i32,
                    (4.0 * (index as f64 * 0.011).cos()).round() as i32,
                    100 + index * 1250,
                    index + 1,
                    index + 1,
                    clicksync_core::RawMouseMovementMode::Relative,
                ))?;
            }
            if disconnected {
                let mut evidence = session.capture_evidence();
                evidence.disconnected = true;
                session.set_capture_evidence(evidence);
                session.stop();
            }
            Ok(Arc::new(session.snapshot()?))
        };
    let snapshot = make_snapshot("LAMZU Thorn V2", 2048, false)?;
    let long_snapshot = make_snapshot(
        "LAMZU Thorn V2 8K Wireless Gaming Mouse — Performance Receiver",
        2048,
        false,
    )?;
    let capture_names = ["Scyrox Turbo 8k dongle", "PIXART VAXEE 8K Wireless"];
    let capture_empty = [
        make_snapshot(capture_names[0], 0, false)?,
        make_snapshot(capture_names[1], 0, false)?,
    ];
    let capture_data = [
        make_snapshot(capture_names[0], 2048, false)?,
        make_snapshot(capture_names[1], 2048, false)?,
    ];
    let disconnected_data = [
        make_snapshot(capture_names[0], 2048, true)?,
        make_snapshot(capture_names[1], 2048, true)?,
    ];
    let failure = Arc::new(Mutex::new(Some(
        "Preview cancelled before export completed".to_owned(),
    )));
    let saved = failure.clone();
    Application::new().with_assets(design::Assets).run(move |cx| {
        editor::TextInput::bind_keys(cx);
        if let Err(error) = design::install_fonts(cx) {
            *failure.lock().unwrap() = Some(error.to_string());
            cx.quit();
            return;
        }
        cx.on_window_closed(|cx| {
            if cx.windows().is_empty() {
                cx.quit();
            }
        })
        .detach();
        let failed = failure.clone();
        let opened = cx.open_window(
            WindowOptions { show: true, ..shell::main_options(Bounds::centered(None, size(px(DEFAULT_WIDTH as f32), px(DEFAULT_HEIGHT as f32)), cx)) },
            move |window, cx| {
                let host = cx.new(|cx| {
                    let mut host = GpuiHost::new(window, Arc::new(Mutex::new(Default::default())), None, false, cx);
                    host.persist_settings = false;
                    host.workspace.enabled = true;
                    host.settings.monitor_metrics = workspace::DEFAULT_MONITORS.to_vec();
                    host
                });
                let handle = window.window_handle();
                let rendered = host.clone();
                window.spawn(cx, async move |cx| {
                    let result: gpui::Result<()> = async {
                        let report = Arc::new(Mutex::new(serde_json::json!({"frames": [], "boundary": "Visual fixtures, not physical measurements. No regression suite or capture."})));
                        for (variant, dark, english) in [("light-zh", false, false), ("dark-zh", true, false), ("light-en", false, true), ("dark-en", true, true)] {
                            for name in ["sensitivity-setup", "sensitivity-setup-wide", "sensitivity-setup-long-name", "sensitivity-setup-waiting", "sensitivity-setup-no-device", "sensitivity-setup-no-other-device", "sensitivity-setup-timeout", "sensitivity", "sensitivity-preparing", "sensitivity-collecting", "sensitivity-finishing", "sensitivity-failure", "sensitivity-expired", "sensitivity-result", "sensitivity-large-result", "sensitivity-wide", "sensitivity-long-name", "calibration", "calibration-result", "keys", "keys-idle", "keys-picking", "keys-pressed", "keys-editing", "keys-long-name", "keys-large-counts", "keys-wide", "keys-motion", "setup", "overview", "overview-hover", "metrics", "datasets", "monitors", "detail", "statistics-trajectory", "statistics-path", "statistics-xy-counts", "statistics-xy-cumulative", "statistics-xy-speed", "statistics-interval", "statistics-histogram", "detail-datasets", "count-hover", "layers", "histogram", "histogram-hover", "histogram-locked", "interval-hover", "comparison", "comparison-histogram", "minimum-detail", "wide-detail", "wide-overview", "long-name", "long-name-data", "long-name-detail", "frequency-three-overview", "frequency-three", "frequency-three-wide", "frequency-three-hover", "overview-three-wide", "maximum-overview", "capture-ready", "capture-paused", "capture-stopped", "minimum-capture-ready", "minimum-capture-paused", "minimum-capture-stopped", "disconnected-sensitivity-setup", "disconnected-sensitivity", "disconnected-polling", "disconnected-polling-reconnected", "disconnected-capture", "disconnected-capture-reconnected"] {
                                if filter.as_ref().is_some_and(|filter| !name.starts_with(filter.as_str())) { continue; }
                                if name == "keys-motion" && filter.as_deref() != Some("keys-motion") { continue; }
                                if name == "keys-motion" && variant != "light-zh" { continue; }
                                handle.update(cx, |_, window, cx| rendered.update(cx, |host, cx| {
                                    host.settings.dark = dark;
                                    window.resize(size(px(MIN_WIDTH as f32), px(MIN_HEIGHT as f32)));
                                    host.runtime.borrow_mut().device_catalog.update(&(1..=2).map(|key| clicksync_core::devices::DeviceDescriptor {
                                        key, metadata: clicksync_core::SessionMetadata { display_name: format!("Mouse {}", if key == 1 { "A" } else { "B" }), device_id: format!("preview-{key}"), button_count: 5, ..Default::default() }, ..Default::default()
                                    }).collect::<Vec<_>>());
                                    host.settings.english = english;
                                    host.custom_chrome = name.starts_with("sensitivity") || name.starts_with("keys") || matches!(name, "calibration" | "calibration-result" | "setup") || (name.starts_with("disconnected") && !name.contains("capture"));
                                    host.control_error = None;
                                    host.backend_error = None;
                                    host.workspace.preview_status = None;
                                    host.workspace.detail_menu = false;
                                    host.workspace.data_menu = false;
                                    host.workspace.monitor_menu = false;
                                    match name {
                                        name if name.starts_with("sensitivity-setup") || name == "disconnected-sensitivity-setup" => {
                                            host.page = Page::Sensitivity;
                                            host.sensitivity = sensitivity::SensitivityPage::default();
                                            host.business_input = business_input::BusinessInput::default();
                                            host.control_error = None;
                                            if name == "disconnected-sensitivity-setup" {
                                                host.sensitivity.disconnected = [true; 2];
                                            }
                                            if name == "sensitivity-setup-waiting" { host.business_input.binding_slot = Some(1); }
                                            if matches!(name, "sensitivity-setup-no-device" | "sensitivity-setup-no-other-device") {
                                                host.sensitivity.binding_issues[1] = Some(sensitivity::BindingIssue::Error("no eligible motion device".into()));
                                                if name == "sensitivity-setup-no-other-device" { host.business_input.bindings[0] = Some(1); }
                                            }
                                            if name == "sensitivity-setup-timeout" {
                                                host.sensitivity.binding_issues[1] = Some(sensitivity::BindingIssue::Motion(clicksync_capture::observation::MotionResult::TimedOut));
                                            }
                                            if name == "sensitivity-setup-wide" { window.resize(size(px(1600.0), px(900.0))); }
                                            if name == "sensitivity-setup-long-name" {
                                                host.business_input.bindings = [Some(1), Some(2)];
                                                host.sensitivity.target_dpi = "2300".into();
                                                host.runtime.borrow_mut().device_catalog.update(&(1..=2).map(|key| clicksync_core::devices::DeviceDescriptor {
                                                    key, metadata: clicksync_core::SessionMetadata { display_name: format!("LAMZU Thorn V2 8K Wireless Gaming Mouse — Performance Receiver {key}"), device_id: format!("preview-{key}"), button_count: 5, ..Default::default() }, ..Default::default()
                                                }).collect::<Vec<_>>());
                                            }
                                        }
                                        name if name.starts_with("sensitivity") || name == "disconnected-sensitivity" => {
                                            use clicksync_chart::analysis::sensitivity::{MatchFrame, MatchResult, MeasurementState, Stage, Failure};
                                            host.page = Page::Sensitivity;
                                            host.sensitivity = sensitivity::SensitivityPage::default();
                                            host.sensitivity.step = sensitivity::Step::Measure;
                                            host.sensitivity.target_dpi = "2300".into();
                                            host.sensitivity.guide = Some(cx.new(|_| motion_guide::MotionGuide::preview(design::Palette::for_theme(dark))));
                                            host.business_input.bindings = [Some(1), Some(2)];
                                            host.was_active = true;
                                            match name {
                                                "disconnected-sensitivity" => {
                                                    host.business_input.bindings = [Some(1), None];
                                                    host.sensitivity.disconnected = [false, true];
                                                    host.sensitivity.target_dpi.clear();
                                                }
                                                "sensitivity-preparing" | "sensitivity-collecting" | "sensitivity-finishing" => {
                                                    host.sensitivity.requested = true;
                                                    host.sensitivity.frame = Some(MatchFrame {
                                                        active: Some(MeasurementState {
                                                            stage: match name { "sensitivity-preparing" => Stage::Preparing, "sensitivity-finishing" => Stage::Finishing, _ => Stage::Collecting },
                                                            progress: if name == "sensitivity-collecting" { 0.47 } else if name == "sensitivity-finishing" { 1.0 } else { 0.0 },
                                                            target_path_length: 4256.0, target_goal: 2300.0 * 10.0 / 2.54, duration_ms: 916.5,
                                                            packet_counts: if name == "sensitivity-preparing" { [0, 0] } else { [504, 512] },
                                                        }), ..Default::default()
                                                    });
                                                }
                                                "sensitivity-result" | "sensitivity-large-result" => {
                                                    host.sensitivity.frame = Some(MatchFrame { active: None,
                                                        result: Some(MatchResult {
                                                            scale: if name == "sensitivity-large-result" { f64::from(i32::MAX) / 2300.0 } else { 1.0203 },
                                                            recommended_target_dpi: if name == "sensitivity-large-result" { i32::MAX } else { 2347 },
                                                            duration_ms: 1816.5, packet_counts: [1004, 1012], direction_delta_degrees: 0.1, overlap_ratio: 0.98, residual: 0.01, segment_spread_percent: 0.2, lag_ms: 2.0 }),
                                                        failure: Failure::None, results_expired: false });
                                                }
                                                "sensitivity-failure" => host.sensitivity.frame = Some(MatchFrame { failure: Failure::PathShapeMismatch, ..Default::default() }),
                                                "sensitivity-expired" => host.sensitivity.expired = true,
                                                "sensitivity-wide" => window.resize(size(px(1600.0), px(900.0))),
                                                "sensitivity-long-name" => {
                                                    host.runtime.borrow_mut().device_catalog.update(&(1..=2).map(|key| clicksync_core::devices::DeviceDescriptor {
                                                        key, metadata: clicksync_core::SessionMetadata { display_name: format!("LAMZU Thorn V2 8K Wireless Gaming Mouse — Performance Receiver {key}"), device_id: format!("preview-{key}"), button_count: 5, ..Default::default() }, ..Default::default()
                                                    }).collect::<Vec<_>>());
                                                }
                                                _ => {}
                                            }
                                        }
                                        "disconnected-polling" | "disconnected-polling-reconnected" => {
                                            let reconnected = name.ends_with("reconnected");
                                            host.page = Page::Polling;
                                            host.polling = polling::PollingPage::disconnected_preview(reconnected);
                                            host.selected = if reconnected { vec![1] } else { Vec::new() };
                                            if !reconnected { host.runtime.borrow_mut().device_catalog.update(&[]); }
                                        }
                                        "disconnected-capture" | "disconnected-capture-reconnected" => {
                                            let reconnected = name.ends_with("reconnected");
                                            host.page = Page::Performance;
                                            host.workspace.chart_window = true;
                                            host.show_plot_overview();
                                            host.workspace.retire(&host.runtime.borrow());
                                            host.workspace.groups.clear();
                                            host.imported = None;
                                            host.selected = vec![1, 2];
                                            if !reconnected { host.runtime.borrow_mut().device_catalog.update(&[]); }
                                            host.workspace.latest = Some(Arc::new(crate::backend::SessionPrefix {
                                                generation: 1, received_sequence: 4096, received_records: 4096,
                                                phase: crate::backend::CapturePhase::Faulted, complete: false,
                                                datasets: disconnected_data.iter().enumerate().map(|(side, data)| (side as u32 + 1, data.clone())).collect(),
                                            }));
                                            host.workspace.preview_status = Some(crate::backend::AppStatus {
                                                generation: 1, phase: crate::backend::CapturePhase::Faulted, complete: false,
                                                capture_fault: Some(crate::backend::CaptureFault::DeviceDisconnected),
                                                error: Some("selected input device disconnected".into()), selected_device_keys: host.selected.clone(),
                                                devices: host.runtime.borrow().device_catalog.choices().iter().map(|choice| clicksync_core::devices::DeviceDescriptor { key: choice.key, ..Default::default() }).collect(),
                                                ..Default::default()
                                            });
                                        }
                                        "calibration" => { host.page = Page::Calibration; host.calibration = calibration::CalibrationPage::default(); }
                                        "calibration-result" => {
                                            use clicksync_chart::analysis::calibration::{CalibrationFrame, TraceStroke};
                                            host.calibration.frame = Some(Arc::new(CalibrationFrame { status: "ResultReady", locked: false, has_data: true, recommended_angle: Some(-1.8), swipe_count: 36, sample_count: 4128, accepted_strokes: 37, rejected_strokes: 2, stroke_spread_degrees: Some(0.2), direction_difference_degrees: Some(0.3), traces: (0..12).map(|stroke| TraceStroke { points: (0..100).map(|x| [x as f64 * 8.0, x as f64 * 0.26 + (x as f64 * 0.16 + stroke as f64).sin() * 6.0]).collect(), current: false }).collect(), quality_reason: "Good" }));
                                        }
                                        name if name.starts_with("keys") => {
                                            host.page = Page::Keys;
                                            host.keys = KeyEngine::default();
                                            host.keys_ui = keys::KeysPage::default();
                                            host.edit = None;
                                            host.focus.focus(window);
                                            for index in [4, 5] {
                                                let value = host.edit_value(index);
                                                host.editors[index].update(cx, |field, cx| field.set_value(&value, cx));
                                            }
                                            if !matches!(name, "keys-idle" | "keys-motion") {
                                                for (i, stats) in host.keys.buttons.iter_mut().enumerate() {
                                                    for click in 0..9 { let t = 400.0 * click as f64 + i as f64 * 14.7; stats.down(t, 80.0); stats.up(t + 62.9 + i as f64 * 8.2); }
                                                }
                                                host.keys.wheel_up = 32; host.keys.wheel_down = 17;
                                            }
                                            if name == "keys-motion" {
                                                let key = KeyIdentity { virtual_key: 65, scan_code: 30, extended: false };
                                                host.pick_custom_key(window, cx);
                                                host.keys_keyboard(key, true, 4000.0, true, false);
                                                host.keys_keyboard(key, false, 4050.0, true, false);
                                            }
                                            if name == "keys-picking" { host.pick_custom_key(window, cx); }
                                            if matches!(name, "keys-pressed" | "keys-long-name" | "keys-large-counts") {
                                                let key = KeyIdentity { virtual_key: 65, scan_code: 30, extended: false };
                                                host.pick_custom_key(window, cx);
                                                host.keys_keyboard(key, true, 4000.0, true, false);
                                                host.keys_keyboard(key, false, 4050.0, true, false);
                                                host.keys_keyboard(key, true, 4200.0, true, false);
                                                host.keys_keyboard(key, false, 4230.0, true, false);
                                                host.keys_keyboard(key, true, 4250.0, true, false);
                                                host.keys_mouse(1, 0, 4300.0);
                                            }
                                            if name == "keys-long-name" { host.keys_ui.name = "Right Control / Extended keyboard input".into(); }
                                            if name == "keys-large-counts" {
                                                host.keys.wheel_up = 1_234; host.keys.wheel_down = 567;
                                                for stats in host.keys.buttons.iter_mut().chain(std::iter::once(&mut host.keys.custom)) {
                                                    stats.down_count = 1_234_567; stats.up_count = 1_234_566; stats.double_click_count = 123_456;
                                                }
                                            }
                                            if name == "keys-editing" { host.begin_edit(4, window, cx); }
                                            if name == "keys-wide" { window.resize(size(px(1600.0), px(960.0))); }
                                        }
                                        "setup" => { host.page = Page::Performance; host.workspace.chart_window = false; }
                                        "overview" => {
                                            host.page = Page::Performance;
                                            host.workspace.chart_window = true;
                                            host.show_plot_overview();
                                            host.imported = Some((snapshot.clone(), Some(800.0)));
                                            host.workspace.groups.clear();
                                            host.workspace.detail_options.clear();
                                            host.workspace.histogram_specs.clear();
                                            host.workspace.hidden_datasets.clear();
                                            host.workspace.analysis = None;
                                            host.import_identity += 1;
                                        }
                                        "metrics" => host.workspace.detail_menu = true,
                                        "datasets" | "detail-datasets" | "long-name-data" => host.workspace.data_menu = true,
                                        "monitors" => host.workspace.monitor_menu = true,
                                        name if name.starts_with("statistics-") => {
                                            host.page = Page::Performance;
                                            host.workspace.chart_window = true;
                                            host.show_plot_overview();
                                            host.workspace.retire(&host.runtime.borrow());
                                            host.imported = Some((snapshot.clone(), Some(800.0)));
                                            host.workspace.groups.clear();
                                            host.workspace.detail_options.clear();
                                            host.workspace.histogram_specs.clear();
                                            host.workspace.hidden_datasets.clear();
                                            host.workspace.analysis = None;
                                            host.import_identity += 1;
                                            host.workspace.detail = Some(match name {
                                                "statistics-trajectory" => 11,
                                                "statistics-path" => 12,
                                                "statistics-xy-counts" => 17,
                                                "statistics-xy-cumulative" => 18,
                                                "statistics-xy-speed" => 19,
                                                "statistics-interval" => 5,
                                                "statistics-histogram" => 0,
                                                _ => unreachable!(),
                                            });
                                            host.workspace.detail_histogram = name == "statistics-histogram";
                                        }
                                        "detail" => host.open_plot_detail(0, false),
                                        "histogram" => host.open_plot_detail(0, true),
                                        "histogram-locked" => host.toggle_histogram_policy(0),
                                        "interval-hover" => host.open_plot_detail(5, false),
                                        "overview-hover" | "count-hover" | "histogram-hover" | "frequency-three-hover" => {},
                                        "layers" => {
                                            host.toggle_detail_option(0, 0);
                                            host.toggle_detail_option(0, 1);
                                        }
                                        "comparison" => {
                                            host.show_plot_overview();
                                            let group = crate::product::performance_analysis::Group { key: 0, snapshot: snapshot.clone(), cpi: Some(1600.0), identity: "preview-b".into() };
                                            host.add_comparison_group(group);
                                        }
                                        "comparison-histogram" => host.open_plot_detail(0, true),
                                        "minimum-detail" => host.open_plot_detail(1, false),
                                        "wide-detail" => window.resize(size(px(1600.0), px(900.0))),
                                        "wide-overview" => {
                                            window.resize(size(px(1600.0), px(900.0)));
                                            host.show_plot_overview();
                                        }
                                        "long-name" => host.add_comparison_group(crate::product::performance_analysis::Group {
                                            key: 0, snapshot: long_snapshot.clone(), cpi: Some(3200.0), identity: "preview-c".into(),
                                        }),
                                        "long-name-detail" => host.open_plot_detail(16, false),
                                        "frequency-three-overview" => {
                                            host.show_plot_overview();
                                            host.imported = Some((capture_data[0].clone(), Some(2300.0)));
                                            host.workspace.groups.clear();
                                            host.workspace.analysis = None;
                                            host.import_identity += 1;
                                            for (index, cpi) in [(1, 800.0), (0, 2300.0)] {
                                                host.add_comparison_group(crate::product::performance_analysis::Group {
                                                    key: 0, snapshot: capture_data[index].clone(), cpi: Some(cpi),
                                                    identity: format!("frequency-{index}"),
                                                });
                                            }
                                        }
                                        "frequency-three" => host.open_plot_detail(8, false),
                                        "frequency-three-wide" => window.resize(size(px(1600.0), px(900.0))),
                                        "overview-three-wide" => {
                                            window.resize(size(px(1600.0), px(900.0)));
                                            host.show_plot_overview();
                                        }
                                        "maximum-overview" => window.resize(size(px(1920.0), px(1000.0))),
                                        name if name.contains("capture-") => {
                                            if !name.starts_with("minimum-") {
                                                window.resize(size(px(1600.0), px(900.0)));
                                            }
                                            if name.ends_with("ready") {
                                                host.show_plot_overview();
                                                host.workspace.retire(&host.runtime.borrow());
                                                for chart in [&mut host.chart, &mut host.comparison_chart] {
                                                    if let Some(chart) = chart.take() { chart.borrow_mut().close(); }
                                                }
                                                host.workspace.groups.clear();
                                                host.workspace.hidden_datasets.clear();
                                                host.imported = None;
                                                host.selected = vec![1, 2];
                                                host.settings.cpi = 2300;
                                                host.settings.performance_b_cpi = 800;
                                            }
                                            let empty = name.ends_with("ready");
                                            let datasets = if empty { &capture_empty } else { &capture_data };
                                            host.workspace.latest = Some(Arc::new(crate::backend::SessionPrefix {
                                                generation: 1,
                                                received_sequence: if empty { 0 } else { 4096 },
                                                received_records: if empty { 0 } else { 4096 },
                                                phase: if empty { crate::backend::CapturePhase::Empty }
                                                    else if name.ends_with("paused") { crate::backend::CapturePhase::Paused }
                                                    else { crate::backend::CapturePhase::Stopped },
                                                complete: true,
                                                datasets: datasets.iter().enumerate().map(|(side, data)| (side as u32 + 1, data.clone())).collect(),
                                            }));
                                        }
                                        _ => unreachable!(),
                                    }
                                    host.focus.focus(window);
                                    cx.notify();
                                }))?;
                                // Wait on production result/transition readiness, not a baseline.
                                let started = std::time::Instant::now();
                                loop {
                                    Timer::after(Duration::from_millis(16)).await;
                                    let ready = handle.update(cx, |_, window, cx| rendered.update(cx, |host, cx| {
                                        let capture_fixture = name.contains("capture-") || name == "disconnected-capture";
                                        let ready_fixture = capture_fixture && name.ends_with("ready");
                                        if capture_fixture && !ready_fixture {
                                            let events = host.runtime.borrow_mut().poll(host.receiver);
                                            let mut answers = events.monitors;
                                            if let Some(answer) = events.chart { answers.push((0, answer)); }
                                            if host.tick_workspace(host.workspace.latest.clone().unwrap(), answers, events.summary, window, cx) { cx.notify(); }
                                        } else if !capture_fixture && host.page == Page::Performance && host.tick_chart(window, cx) { cx.notify(); }
                                        (ready_fixture || host.page != Page::Performance || !host.workspace.chart_window || {
                                            let views = host.plot_views();
                                            let groups = host.workspace.analysis.as_ref().map_or(0, |a| a.input.datasets.len());
                                            let slots = if host.workspace.detail.is_some() { 1 } else { 1 + host.settings.monitor_metrics.len() };
                                            groups > 0 && views.len() == groups * slots && views.iter().all(|view| {
                                                let view = view.borrow();
                                                view.ready() && view.presented_result().is_some_and(|result| {
                                                    host.workspace.targets.iter().flatten().any(|target| result.eligible_for(target))
                                                })
                                            })
                                                && host.workspace.motion.opacity(std::time::Instant::now()) >= 1.0

                                        }) && host.sensitivity.motion.settled()
                                        && (host.page != Page::Sensitivity || host.sensitivity.guide.as_ref().is_none_or(|guide| guide.read(cx).preview_ready()))
                                        && (host.page != Page::Calibration || host.calibration.guide.as_ref().is_none_or(|guide| guide.read(cx).preview_ready()))
                                    }))?;
                                    if ready { break; }
                                    if started.elapsed() > Duration::from_secs(10) {
                                        let diagnostics = handle.update(cx, |_, _, cx| {
                                            let host = rendered.read(cx);
                                            serde_json::json!({"error":host.control_error, "ready":host.plot_views().iter().map(|view| (view.borrow().ready(), view.borrow().presented_result().is_some())).collect::<Vec<_>>(), "sensitivity_settled":host.sensitivity.motion.settled(), "page":format!("{:?}",host.page), "guide":host.calibration.guide.as_ref().map(|g|g.read(cx).preview_ready()), "views":host.plot_views().iter().map(|view| view.borrow().report()).collect::<Vec<_>>(), "detail":host.workspace.detail, "motion":host.workspace.motion.opacity(std::time::Instant::now()), "statistics_pending":host.file_busy})
                                        })?;
                                        std::fs::write(directory.join("render-error.json"), serde_json::to_vec_pretty(&diagnostics)?)?;
                                        return Err(gpui::private::anyhow::anyhow!("Preview did not settle: {name}: {diagnostics}"));
                                    }
                                }
                                if name.ends_with("hover") {
                                    handle.update(cx, |_, _, cx| rendered.update(cx, |host, cx| {
                                        if let Some(chart) = host.analysis_chart() {
                                            let bounds = chart.borrow().bounds_for_trial();
                                            let position = bounds.origin + gpui::point(bounds.size.width * 0.45, bounds.size.height * 0.5);
                                            host.queue_analysis_pointer(chart, position, cx);
                                        }
                                    }))?;
                                }
                                let recording = std::time::Instant::now();
                                let mut recorded_frames = Vec::new();
                                for frame_index in 0..if name == "keys-motion" { 76 } else { 1 } {
                                    if name == "keys-motion" {
                                        handle.update(cx, |_, _, cx| rendered.update(cx, |host, cx| {
                                            let key = KeyIdentity { virtual_key: 65, scan_code: 30, extended: false };
                                            let time = 4300.0 + frame_index as f64 * 50.0;
                                            match frame_index {
                                                3 => host.keys_mouse(1, 0, time),
                                                6 => host.keys_mouse(2, 0, time),
                                                12 => host.keys_keyboard(key, true, time, true, false),
                                                15 => host.keys_keyboard(key, false, time, true, false),
                                                21 | 23 | 25 | 37 => host.keys_mouse(0x0400, 120, time),
                                                31 | 33 => host.keys_mouse(0x0400, (-120i16) as u16, time),
                                                43 | 44 => host.keys_mouse(3, 0, time),
                                                49 => host.key_action_feedback(1, true, cx),
                                                52 => host.key_action_feedback(1, false, cx),
                                                _ => {}
                                            }
                                            cx.notify();
                                        }))?;
                                    }
                                    handle.update(cx, |_, window, _| { window.request_windows_frame_capture()?; window.refresh(); Ok::<_, gpui::private::anyhow::Error>(()) })??;
                                    let started = std::time::Instant::now();
                                    loop {
                                        Timer::after(Duration::from_millis(16)).await;
                                        if let Some(frame) = handle.update(cx, |_, window, _| window.take_windows_frame())?? {
                                            let file = if name == "keys-motion" { format!("{variant}-{name}-{frame_index:03}.png") } else { format!("{variant}-{name}.png") };
                                            let (width, height) = (frame.width, frame.height);
                                            let elapsed_ms = recording.elapsed().as_secs_f64() * 1000.0;
                                            if name == "keys-motion" {
                                                recorded_frames.push((file.clone(), frame));
                                            } else {
                                                write_probe_frame(&directory.join(&file), frame, cx.background_executor()).await?;
                                            }
                                            let layout = handle.update(cx, |_, _, cx| rendered.read(cx).workspace.interaction.as_ref().map(|frame| frame.layout_report()))?;
                                            report.lock().unwrap()["frames"].as_array_mut().unwrap().push(serde_json::json!({"file":file,"width":width,"height":height,"layout":layout,"elapsed_ms":elapsed_ms}));
                                            break;
                                        }
                                        if started.elapsed() > Duration::from_secs(5) { return Err(gpui::private::anyhow::anyhow!("Frame capture timed out: {name}")); }
                                    }
                                }
                                if !recorded_frames.is_empty() {
                                    let path = directory.join("capture.json");
                                    let captured = serde_json::to_vec_pretty(&*report.lock().unwrap())?;
                                    cx.background_spawn(async move { std::fs::write(path, captured) }).await?;
                                }
                                // Keep capture cadence independent of PNG export; awaiting yields to window messages.
                                for (file, frame) in recorded_frames {
                                    write_probe_frame(&directory.join(file), frame, cx.background_executor()).await?;
                                }
                            }
                        }
                        std::fs::write(directory.join("preview.json"), serde_json::to_vec_pretty(&*report.lock().unwrap())?)?;
                        Ok(())
                    }.await;
                    *failed.lock().unwrap() = result.err().map(|error| error.to_string());
                    let _ = cx.update(|_, cx| cx.quit());
                }).detach();
                host
            },
        );
        if let Err(error) = opened { *failure.lock().unwrap() = Some(error.to_string()); cx.quit(); }
    });
    if let Some(error) = saved.lock().unwrap().take() {
        return Err(error.into());
    }
    Ok(())
}
