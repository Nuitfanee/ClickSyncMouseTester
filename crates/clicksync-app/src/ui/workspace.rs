use super::*;
use crate::chart::{ChartRequest, ChartResult, ViewMode};
use clicksync_chart::gpu::ResourceLimits;
mod interaction;
#[cfg(feature = "ui-restoration-probe")]
pub(super) mod profile;

pub(super) const MAX_MONITORS: usize = 3;
pub(super) use crate::product::DEFAULT_MONITORS;

#[derive(Clone)]
pub(super) struct AnalysisContext {
    pub input: Arc<crate::backend::SessionPrefix>,
    pub range: Option<(f64, f64)>,
}

/// One suspended overview, not a history cache. Detail borrows its primary GPU budget.
pub(super) struct OverviewPrimary {
    pub views: Vec<Rc<RefCell<ChartView>>>,
    pub monitors: [Vec<Rc<RefCell<ChartView>>>; MAX_MONITORS],
    pub target: Option<ChartRequest>,
    pub displayed: Option<Arc<ChartResult>>,
}

#[derive(Default)]
pub(super) struct Workspace {
    pub(super) interaction: Option<interaction::Frame>,
    #[cfg(feature = "ui-restoration-probe")]
    pub capture_source: Option<clicksync_core::protocol::CaptureSource>,
    #[cfg(feature = "ui-restoration-probe")]
    pub capture_controls: [Rc<std::cell::Cell<Bounds<gpui::Pixels>>>; 3],
    pub enabled: bool,
    pub chart_window: bool,
    pub child: Option<gpui::WindowHandle<GpuiHost>>,
    pub return_to: Option<(gpui::AnyWindowHandle, gpui::WeakEntity<GpuiHost>)>,
    pub opening: bool,
    pub closing: bool,
    pub targets: [Option<ChartRequest>; 4],
    pub monitors: [Vec<Rc<RefCell<ChartView>>>; 3],
    pub analysis: Option<AnalysisContext>,
    pub latest: Option<Arc<crate::backend::SessionPrefix>>,
    pub summary: Option<Arc<crate::product::performance_summary::SummaryFrame>>,
    pub summary_target: Option<crate::product::performance_summary::SummaryRequest>,
    pub monitor_menu: bool,
    pub device_side: usize,
    pub dual: bool,
    pub admission: Option<String>,
    pub suspended: bool,
    pub submitted: [u64; 4],
    pub completed: [u64; 4],
    pub detail: Option<usize>,
    pub overview_primary: Option<OverviewPrimary>,
    pub motion: detail::PlotMotion,
    pub detail_menu: bool,
    pub data_menu: bool,
    pub detail_histogram: bool,
    pub histogram_specs: std::collections::HashMap<usize, clicksync_chart::HistogramSpec>,
    pub hidden_datasets: std::collections::HashSet<u32>,
    pub detail_options:
        std::collections::HashMap<(usize, bool), crate::product::performance_analysis::PlotOptions>,
    pub detail_viewports:
        std::collections::HashMap<(usize, bool, bool), clicksync_chart::gpu::Viewport>,
    pub overview_viewports:
        std::collections::HashMap<(usize, bool), clicksync_chart::gpu::Viewport>,
    pub detail_editors: Vec<gpui::Entity<editor::TextInput>>,
    pub detail_editor_context: Option<detail::RangeEditorContext>,
    pub range_error: Option<String>,
    pub groups: Vec<crate::product::performance_analysis::Group>,
    pub staged_group: Option<crate::product::performance_analysis::Group>,
    pub next_group_key: u32,
    pub export_group: usize,
    pub extra_views: Vec<Rc<RefCell<ChartView>>>,
    pub link: Option<crate::chart::LinkedResult>,
    pub selected_point: Option<(u64, u32, usize)>,
    pub file_context: Option<detail::FileContext>,
    pub file_epoch: u64,
    pub trajectory_bounds: Bounds<gpui::Pixels>,
    pub canvas_press: Option<gpui::Point<gpui::Pixels>>,
    pub capture_origin: Option<gpui::Point<gpui::Pixels>>,
    pub capture_viewport: Option<(u64, clicksync_chart::gpu::Viewport)>,
}

pub(super) fn limits(renderers: usize) -> ResourceLimits {
    let mut limits = ResourceLimits::default();
    let count = renderers.max(1);
    limits.readback_bytes = 0;
    limits.resident_bytes /= count;
    limits.resident_blocks /= count;
    limits.instances_per_upload /= count;
    limits.layer_bytes /= count;
    limits.readback_bytes /= count;
    limits
}

pub(super) fn view_limits(slot: usize, devices: usize, monitors: usize) -> ResourceLimits {
    let devices = devices.max(1);
    let monitors = monitors.min(MAX_MONITORS);
    let mut budget = limits(devices * (1 + monitors));
    let total = ResourceLimits::default();
    let (weight, count) = if monitors == 0 {
        (100, devices)
    } else if slot == 0 {
        (72, devices)
    } else {
        (28, devices * monitors)
    };
    budget.layer_bytes = total.layer_bytes / 100 * weight / count;
    budget.readback_bytes = 0;
    budget
}

fn changed(target: Option<&ChartRequest>, request: &ChartRequest) -> bool {
    target.is_none_or(|old| {
        old.source_version != request.source_version
            || old.parameters != request.parameters
            || old.analysis != request.analysis
            || old.dataset_cpi != request.dataset_cpi
            || old.secondary_metric != request.secondary_metric
            || old.kind != request.kind
            || old.view != request.view
            || old.prefix.generation != request.prefix.generation
            || old.prefix.received_sequence != request.prefix.received_sequence
            || old.prefix.phase != request.prefix.phase
            || old.prefix.complete != request.prefix.complete
            || old.prefix.datasets.len() != request.prefix.datasets.len()
            || old
                .prefix
                .datasets
                .iter()
                .zip(&request.prefix.datasets)
                .any(|((old_key, old), (key, current))| {
                    old_key != key || old.session_revision() != current.session_revision()
                })
    })
}

impl Workspace {
    /// Retain measurement state, never entities or graphics owned by the closing window.
    pub fn for_window_handoff(&self) -> Self {
        Self {
            enabled: self.enabled,
            dual: self.dual,
            analysis: self.analysis.clone(),
            latest: self.latest.clone(),
            detail: self.detail,
            detail_histogram: self.detail_histogram,
            histogram_specs: self.histogram_specs.clone(),
            hidden_datasets: self.hidden_datasets.clone(),
            detail_options: self.detail_options.clone(),
            detail_viewports: self.detail_viewports.clone(),
            overview_viewports: self.overview_viewports.clone(),
            groups: self.groups.clone(),
            staged_group: self.staged_group.clone(),
            next_group_key: self.next_group_key,
            export_group: self.export_group,
            file_epoch: self.file_epoch + 1,
            ..Default::default()
        }
    }

    pub fn retire(&mut self, runtime: &runtime::AppRuntime) {
        self.interaction = Default::default();
        for slot in 0..4 {
            runtime.worker.remove_view(slot);
        }
        self.targets = Default::default();
        self.overview_primary = None;
        for view in self.extra_views.drain(..) {
            view.borrow_mut().close();
        }
        for views in &mut self.monitors {
            for view in views.iter() {
                view.borrow_mut().close();
            }
            views.clear();
        }
        self.analysis = None;
        self.latest = None;
        self.summary = None;
        self.summary_target = None;
    }

    pub fn resume(&mut self) {
        self.motion.cancel();
        self.analysis = None;
        self.targets = Default::default();
        self.summary_target = None;
        self.detail = None;
        self.detail_editor_context = None;
        self.detail_menu = false;
        self.link = None;
        self.selected_point = None;
        self.detail_viewports.clear();
        self.overview_viewports.clear();
        self.file_epoch += 1;
    }
}

impl GpuiHost {
    pub(super) fn anchor_trajectory_capture(&mut self, generation: u64, resume: bool) {
        use clicksync_chart::{Coordinate, Point, gpu::Viewport};
        let bounds = self.workspace.trajectory_bounds;
        let position = self
            .workspace
            .capture_origin
            .take()
            .unwrap_or(bounds.center());
        let width = f64::from(f32::from(bounds.size.width)).max(1.0);
        let height = f64::from(f32::from(bounds.size.height)).max(1.0);
        let mut anchor = Point {
            x: Coordinate::Scalar(0.0),
            y: Coordinate::Scalar(0.0),
        };
        let mut units_per_pixel = 1.0;
        if resume {
            if self.workspace.detail.is_none()
                && let Some(view) = &self.chart
            {
                units_per_pixel = view.borrow().viewport().width / width;
            }
            if let Some((_, snapshot)) = self
                .workspace
                .latest
                .as_ref()
                .and_then(|prefix| prefix.datasets.first())
            {
                anchor = Point {
                    x: Coordinate::Integer(snapshot.summary_sum_x()),
                    y: Coordinate::Integer(snapshot.summary_sum_y()),
                };
            }
        }
        let viewport = Viewport {
            origin: Point {
                x: crate::chart_view::shift_view(
                    anchor.x,
                    -f64::from(f32::from(position.x - bounds.left())) * units_per_pixel,
                ),
                y: crate::chart_view::shift_view(
                    anchor.y,
                    -f64::from(f32::from(position.y - bounds.top())) * units_per_pixel,
                ),
            },
            width: width * units_per_pixel,
            height: height * units_per_pixel,
        };
        self.workspace.capture_viewport = Some((generation, viewport));
    }

    fn start_from_canvas(&mut self, position: gpui::Point<gpui::Pixels>, window: &Window) {
        if !self.workspace.trajectory_bounds.contains(&position)
            || self.lock.locked()
            || self.runtime.borrow().pending_action().is_some()
        {
            return;
        }
        if !self.was_active && window.is_window_active() {
            self.activation_changed(window);
        }
        let phase = self.runtime.borrow().status().map(|status| status.phase);
        if !matches!(
            phase,
            Some(
                crate::backend::CapturePhase::Empty
                    | crate::backend::CapturePhase::Paused
                    | crate::backend::CapturePhase::Stopped
            )
        ) {
            return;
        }
        self.workspace.capture_origin = Some(position);
        self.capture_command(if phase == Some(crate::backend::CapturePhase::Paused) {
            "resume"
        } else {
            "start"
        });
        self.workspace.capture_origin = None;
    }

    pub(super) fn performance_cpi(&self, key: u32, side: usize) -> Option<f64> {
        if let Some(group) = self.workspace.groups.iter().find(|group| group.key == key) {
            group.cpi
        } else if key == u32::MAX {
            self.comparison.as_ref().and_then(|(_, cpi)| *cpi)
        } else if let Some((_, cpi)) = &self.imported {
            *cpi
        } else {
            Some(if side == 0 {
                self.settings.cpi
            } else {
                self.settings.performance_b_cpi
            } as f64)
        }
    }

    pub(super) fn toggle_monitor(&mut self, metric: usize) {
        // Save against the old slot-to-metric mapping, before removing an entry.
        if self.workspace.analysis.is_some() {
            self.save_detail_view();
        }
        if let Some(index) = self
            .settings
            .monitor_metrics
            .iter()
            .position(|id| *id == metric)
        {
            self.settings.monitor_metrics.remove(index);
        } else if self.settings.monitor_metrics.len() < MAX_MONITORS {
            self.settings.monitor_metrics.push(metric);
        } else {
            self.workspace.admission = Some(
                self.text(
                    "当前窗口与共享图形预算最多同时监看三图。请先移除一图。",
                    "The window and shared graphics budget admit three monitors. Remove one first.",
                )
                .into(),
            );
            return;
        }
        self.workspace.admission = None;
        self.reconfigure_workspace();
        self.save_settings();
    }

    fn reconfigure_workspace(&mut self) {
        for slot in 1..4 {
            self.runtime.borrow().worker.remove_view(slot);
            self.workspace.targets[slot] = None;
            for view in &self.workspace.monitors[slot - 1] {
                view.borrow_mut().close();
            }
            self.workspace.monitors[slot - 1].clear();
        }
        // Resizing a sub-budget retires its GPU resources before another view is admitted.
        let devices = self
            .workspace
            .analysis
            .as_ref()
            .map(|analysis| analysis.input.datasets.len())
            .or_else(|| {
                self.workspace
                    .latest
                    .as_ref()
                    .map(|prefix| prefix.datasets.len())
            })
            .unwrap_or(self.selected.len())
            .max(1);
        for chart in self
            .chart
            .iter()
            .chain(self.comparison_chart.iter())
            .chain(&self.workspace.extra_views)
        {
            chart.borrow_mut().set_resource_limits(view_limits(
                0,
                devices,
                self.settings.monitor_metrics.len(),
            ));
        }
    }

    pub(super) fn tick_workspace(
        &mut self,
        prefix: Arc<crate::backend::SessionPrefix>,
        mut answers: Vec<(usize, Result<Arc<ChartResult>, String>)>,
        summary: Option<Arc<crate::product::performance_summary::SummaryFrame>>,
        _window: &Window,
        cx: &mut Context<Self>,
    ) -> bool {
        use crate::backend::CapturePhase;
        let mut dirty = false;
        let new_prefix = self.workspace.latest.as_ref().is_none_or(|old| {
            old.received_sequence != prefix.received_sequence || old.phase != prefix.phase
        });
        dirty |= self
            .workspace
            .latest
            .as_ref()
            .is_none_or(|old| old.phase != prefix.phase || old.complete != prefix.complete);
        self.workspace.latest = Some(prefix.clone());
        if self.workspace.analysis.is_none()
            && matches!(
                prefix.phase,
                CapturePhase::Paused | CapturePhase::Stopped | CapturePhase::Faulted
            )
        {
            self.workspace.analysis = Some(AnalysisContext {
                input: prefix.clone(),
                range: None,
            });
            dirty = true;
        }
        let final_capture = self.imported.is_none()
            && prefix.phase == CapturePhase::Stopped
            && self.workspace.analysis.as_ref().is_some_and(|analysis| {
                (analysis.input.phase != CapturePhase::Stopped
                    || analysis.input.received_sequence != prefix.received_sequence
                    || analysis.input.complete != prefix.complete
                    || analysis.input.datasets.iter().zip(&prefix.datasets).any(
                        |((_, old), (_, current))| {
                            old.session_revision() != current.session_revision()
                        },
                    ))
                    && analysis.input.generation == prefix.generation
                    && analysis.input.received_sequence <= prefix.received_sequence
                    && analysis.input.datasets.len() == prefix.datasets.len()
                    && analysis.input.datasets.iter().zip(&prefix.datasets).all(
                        |((old_key, old), (key, current))| {
                            old_key == key && old.generation() == current.generation()
                        },
                    )
            });
        if final_capture {
            self.workspace.analysis.as_mut().unwrap().input = prefix.clone();
            self.invalidate_analysis();
            dirty = true;
        }
        let prefix = self
            .workspace
            .analysis
            .as_ref()
            .map_or(prefix, |fixed| fixed.input.clone());
        let request = crate::product::performance_summary::SummaryRequest {
            prefix: prefix.clone(),
            source_version: self.import_identity,
            dataset_cpi: prefix
                .datasets
                .iter()
                .enumerate()
                .map(|(side, (key, _))| (*key, self.performance_cpi(*key, side)))
                .collect(),
            now_ms: crate::capture_lock::now_ms(),
        };
        if let Some(summary) = summary
            && self
                .workspace
                .summary_target
                .as_ref()
                .is_some_and(|target| summary.request.same_input(target))
        {
            let changed = self.workspace.summary.as_ref().is_none_or(|old| {
                !old.request.same_input(&summary.request) || old.speeds != summary.speeds
            });
            self.workspace.summary = Some(summary);
            if changed && let Some(frame) = &self.workspace.interaction {
                frame.notify_summary(cx);
            }
        }
        if new_prefix && let Some(frame) = &self.workspace.interaction {
            frame.notify_summary(cx);
        }
        let due = self.workspace.summary.as_ref().is_some_and(|frame| {
            frame
                .next_refresh_ms
                .is_some_and(|deadline| request.now_ms >= deadline)
                && self
                    .workspace
                    .summary_target
                    .as_ref()
                    .is_none_or(|target| target.now_ms <= frame.request.now_ms)
        });
        if self
            .workspace
            .summary_target
            .as_ref()
            .is_none_or(|target| !target.same_input(&request))
            || due
        {
            self.runtime
                .borrow()
                .submit_summary(self.receiver, request.clone());
            self.workspace.summary_target = Some(request);
        }
        // A minimized window keeps control, summary and drain alive; only GPU/geometry demands retire.
        let minimized = unsafe {
            windows::Win32::UI::WindowsAndMessaging::IsIconic(windows::Win32::Foundation::HWND(
                self.lock.window_id() as *mut _,
            ))
            .as_bool()
        };
        let suspended = minimized || self.menu.coverage >= 1.0;
        if suspended {
            if !self.workspace.suspended {
                if self.workspace.analysis.is_some() {
                    self.save_detail_view();
                }
                for slot in 0..4 {
                    self.runtime.borrow().worker.remove_view(slot);
                }
                self.workspace.targets = Default::default();
                for views in &mut self.workspace.monitors {
                    for view in views.iter() {
                        view.borrow_mut().close();
                    }
                    views.clear();
                }
                for chart in [&self.chart, &self.comparison_chart].into_iter().flatten() {
                    chart.borrow_mut().close();
                }
                for chart in &self.workspace.extra_views {
                    chart.borrow_mut().close();
                }
                self.workspace.suspended = true;
            }
            return dirty;
        }
        self.workspace.suspended = false;
        if self.settings.monitor_metrics.len() > MAX_MONITORS {
            self.workspace.admission = Some(self.text(
                "保存的监看选择超过三图准入上限，请调整监看选择。",
                "The saved monitor selection exceeds the three-chart limit. Adjust the selection.",
            ).into());
        }
        let count = (1 + self.settings.monitor_metrics.len().min(MAX_MONITORS))
            * prefix.datasets.len().max(1);
        for slot in 0..4 {
            let metric = if slot == 0 {
                Some(self.workspace.detail.unwrap_or(11))
            } else if self.workspace.detail.is_some() {
                None
            } else {
                self.settings.monitor_metrics.get(slot - 1).copied()
            };
            let Some(metric) = metric else { continue };
            if !prefix
                .datasets
                .iter()
                .any(|(_, snapshot)| snapshot.event_count() > 0)
            {
                continue;
            }
            let mut parameters = self.settings.parameters(
                metric,
                self.workspace.detail.is_some() && self.workspace.detail_histogram,
                self.logical,
                self.parameter_version,
            );
            if self.workspace.detail.is_some()
                && self.workspace.detail_histogram
                && let Some(spec) = self.workspace.histogram_specs.get(&metric)
            {
                parameters.histogram = Some(*spec);
            }
            let request = ChartRequest {
                id: self.next_request,
                source_version: self.import_identity,
                prefix: prefix.clone(),
                parameters,
                dataset_cpi: prefix
                    .datasets
                    .iter()
                    .enumerate()
                    .map(|(side, (key, _))| (*key, self.performance_cpi(*key, side)))
                    .collect(),
                secondary_metric: crate::product::secondary_metric(
                    metric,
                    self.workspace.detail.is_some() && self.workspace.detail_histogram,
                ),
                kind: crate::product::plot_kind(
                    metric,
                    usize::from(matches!(metric, 0 | 1 | 5 | 17)),
                    self.workspace.detail.is_some() && self.workspace.detail_histogram,
                ),
                view: if slot == 0
                    || self.workspace.detail.is_some()
                    || matches!(metric, 2 | 3 | 4 | 6 | 7 | 12 | 15)
                {
                    ViewMode::FullSession
                } else {
                    ViewMode::Follow {
                        window_ms: self.settings.follow_window_ms,
                    }
                },
                analysis: Some(crate::product::performance_analysis::PlotAnalysis {
                    range: self.workspace.analysis.as_ref().and_then(|a| a.range),
                    options: self.detail_options(metric),
                }),
            };
            if changed(self.workspace.targets[slot].as_ref(), &request) {
                self.next_request += 1;
                let cached = (slot == 0 && self.workspace.detail.is_some())
                    .then(|| self.reusable_overview_result(&request))
                    .flatten();
                let answer = if let Some(result) = cached {
                    answers.push((slot, Ok(result)));
                    Ok(())
                } else {
                    let answer = self.runtime.borrow_mut().submit_monitor(
                        self.receiver,
                        slot,
                        request.clone(),
                    );
                    self.workspace.submitted[slot] += u64::from(answer.is_ok());
                    answer
                };
                if let Err(error) = answer {
                    self.control_error = Some(error);
                    dirty = true;
                } else {
                    self.workspace.targets[slot] = Some(request.clone());
                    if slot == 0 {
                        self.target = Some(request);
                    }
                    dirty |= self.workspace.analysis.is_some();
                }
            }
        }
        for (slot, answer) in answers {
            match answer {
                Ok(result)
                    if self.workspace.targets[slot]
                        .as_ref()
                        .is_some_and(|target| result.eligible_for(target)) =>
                {
                    let previous_error = self.control_error.clone();
                    if slot == 0 {
                        dirty |= self.chart.is_none();
                        if self.workspace.detail.is_some() {
                            self.apply_detail_result(result.clone());
                        } else {
                            self.apply_workspace_primary(result.clone(), count);
                        }
                        self.displayed = Some(result);
                    } else {
                        let shared_time = self
                            .workspace
                            .monitors
                            .iter()
                            .flatten()
                            .find(|view| view.borrow().is_time_plot())
                            .map(|view| view.borrow().viewport())
                            .or_else(|| {
                                self.workspace
                                    .overview_viewports
                                    .iter()
                                    .find(|((metric, logical), _)| {
                                        *metric != 11 && *logical == self.logical
                                    })
                                    .map(|(_, viewport)| *viewport)
                            });
                        let views = &mut self.workspace.monitors[slot - 1];
                        dirty |= views.len() != result.datasets.len();
                        if views.len() != result.datasets.len() {
                            for view in views.iter() {
                                view.borrow_mut().close();
                            }
                            views.clear();
                        }
                        for side in 0..result.datasets.len() {
                            if let Some(view) = views.get(side) {
                                if let Err(error) = view.borrow_mut().set_result(result.clone()) {
                                    self.control_error = Some(error);
                                }
                            } else {
                                match new_view(result.clone(), side, slot, &self.settings) {
                                    Ok(view) => {
                                        if let Some(metric) =
                                            self.settings.monitor_metrics.get(slot - 1)
                                            && let Some(viewport) = self
                                                .workspace
                                                .overview_viewports
                                                .get(&(*metric, self.logical))
                                        {
                                            view.borrow_mut().set_viewport(*viewport, true);
                                        } else if self.workspace.analysis.is_some()
                                            && let Some(viewport) = shared_time
                                        {
                                            view.borrow_mut().sync_time_view(viewport);
                                        }
                                        views.push(view);
                                    }
                                    Err(error) => self.control_error = Some(error),
                                }
                            }
                        }
                    }
                    dirty |= self.control_error != previous_error;
                    self.workspace.completed[slot] += 1;
                    if let Some(frame) = &self.workspace.interaction {
                        frame.notify_plot(slot, cx);
                    } else {
                        dirty = true;
                    }
                }
                Ok(_) => {}
                Err(error) => {
                    self.control_error = Some(error);
                    dirty = true;
                }
            }
        }
        dirty
    }

    fn reusable_overview_result(&self, target: &ChartRequest) -> Option<Arc<ChartResult>> {
        self.workspace
            .overview_primary
            .as_ref()
            .and_then(|overview| overview.displayed.clone())
            .into_iter()
            .chain(
                self.workspace
                    .monitors
                    .iter()
                    .chain(
                        self.workspace
                            .overview_primary
                            .iter()
                            .flat_map(|overview| overview.monitors.iter()),
                    )
                    .filter_map(|views| views.first()?.borrow().presented_result()),
            )
            .find(|result| {
                let mut request = result.request.clone();
                request.view = target.view;
                // Only layout differs: source, exact prefix, phase, parameters,
                // range and draw options must still match the requested result.
                !changed(Some(&request), target)
            })
            .map(|result| Arc::new(result.with_view(target.id, target.view)))
    }

    fn apply_workspace_primary(&mut self, result: Arc<ChartResult>, _count: usize) {
        let capture_viewport = self
            .workspace
            .capture_viewport
            .filter(|(generation, _)| *generation == result.request.prefix.generation);
        for side in 0..result.datasets.len().min(2) {
            let view = if side == 0 {
                &mut self.chart
            } else {
                &mut self.comparison_chart
            };
            if let Some(view) = view {
                let mut view = view.borrow_mut();
                view.set_resource_limits(view_limits(
                    0,
                    result.datasets.len(),
                    self.settings.monitor_metrics.len(),
                ));
                view.set_theme(self.settings.dark, self.settings.english);
                if let Err(error) = view.set_result(result.clone()) {
                    self.control_error = Some(error);
                }
            } else {
                match new_view(result.clone(), side, 0, &self.settings) {
                    Ok(chart) => {
                        if let Some(viewport) =
                            self.workspace.overview_viewports.get(&(11, self.logical))
                        {
                            chart.borrow_mut().set_viewport(*viewport, true);
                        }
                        *view = Some(chart);
                    }
                    Err(error) => self.control_error = Some(error),
                }
            }
        }
        if result.datasets.len() < 2
            && let Some(view) = self.comparison_chart.take()
        {
            view.borrow_mut().close();
        }
        if result.datasets.len() > 2 {
            if let Some(view) = self.workspace.extra_views.first() {
                let mut view = view.borrow_mut();
                view.set_resource_limits(view_limits(
                    0,
                    result.datasets.len(),
                    self.settings.monitor_metrics.len(),
                ));
                if let Err(error) = view.set_result(result.clone()) {
                    self.control_error = Some(error);
                }
            } else {
                match new_view(result, 2, 0, &self.settings) {
                    Ok(view) => self.workspace.extra_views.push(view),
                    Err(error) => self.control_error = Some(error),
                }
            }
        } else {
            for view in self.workspace.extra_views.drain(..) {
                view.borrow_mut().close();
            }
        }
        if let Some((_, viewport)) = capture_viewport {
            for view in self
                .chart
                .iter()
                .chain(self.comparison_chart.iter())
                .chain(&self.workspace.extra_views)
            {
                if let Err(error) = view.borrow_mut().start_trajectory(viewport) {
                    self.control_error = Some(error);
                }
            }
            self.workspace.capture_viewport = None;
        }
    }

    fn render_workspace_content(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> gpui::Stateful<gpui::Div> {
        let palette = design::Palette::for_theme(self.settings.dark);
        let phase = self
            .workspace
            .analysis
            .as_ref()
            .map(|analysis| analysis.input.phase)
            .or_else(|| self.runtime.borrow().status().map(|status| status.phase))
            .unwrap_or(crate::backend::CapturePhase::Empty);
        let locked = self.lock.locked();
        let pending = self.runtime.borrow().pending_action().is_some();
        let configurable = !locked && !pending;
        let viewport_width = f32::from(window.viewport_size().width);
        let gutter = (viewport_width * 0.014).clamp(16.0, 28.0);
        let width = viewport_width - gutter * 2.0;
        let bottom = viewport_width - 32.0 < 1120.0;
        let sidebar_width = (width * 0.29).clamp(320.0, 500.0);
        let detail = self.workspace.detail.is_some();
        let can_open_detail = configurable && self.workspace.analysis.is_some();
        let visible_sides: Vec<_> = self
            .workspace
            .analysis
            .as_ref()
            .map(|analysis| {
                analysis
                    .input
                    .datasets
                    .iter()
                    .enumerate()
                    .filter(|(_, (key, _))| !self.workspace.hidden_datasets.contains(key))
                    .map(|(side, _)| side)
                    .collect()
            })
            .unwrap_or_else(|| {
                let count =
                    if self.workspace.dual || self.selected.len() == 2 || self.comparison.is_some()
                    {
                        2
                    } else {
                        1
                    };
                (0..count).collect()
            });
        let mut actions = div().flex().items_center().gap(px(8.0));
        for (action, chinese, english) in [("stop", "结束", "End"), ("reset", "重置", "Reset")]
        {
            let enabled = !pending
                && match action {
                    "stop" => matches!(
                        phase,
                        crate::backend::CapturePhase::Collecting
                            | crate::backend::CapturePhase::Paused
                    ),
                    "reset" => {
                        !locked
                            && (phase != crate::backend::CapturePhase::Empty
                                || self.imported.is_some()
                                || !self.workspace.groups.is_empty())
                    }
                    _ => true,
                };
            actions = actions.child(
                compact_button(action, self.text(chinese, english), palette, false, enabled)
                    .bg(rgb(palette.selected))
                    .text_color(rgb(palette.accent))
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .when(enabled, |button| {
                        button.hover(move |style| {
                            style
                                .bg(rgb(palette.selected))
                                .text_color(rgb(palette.accent_hover))
                        })
                    })
                    .map(|button| {
                        #[cfg(feature = "ui-restoration-probe")]
                        let button = button.relative().child(probe::control_bounds(
                            self.workspace.capture_controls[if action == "stop" { 1 } else { 2 }]
                                .clone(),
                        ));
                        button
                    })
                    .on_click(cx.listener(move |host, _, _, cx| {
                        if enabled {
                            host.capture_command(action);
                            cx.notify();
                        }
                    })),
            );
        }
        if locked || (phase == crate::backend::CapturePhase::Paused && self.imported.is_none()) {
            let action = if locked { "pause" } else { "resume" };
            let enabled = !pending;
            actions = div()
                .flex()
                .items_center()
                .gap(px(8.0))
                .child(
                    compact_button(
                        action,
                        if locked {
                            self.text("暂停 · S", "Pause · S")
                        } else {
                            self.text("继续 · S", "Resume · S")
                        },
                        palette,
                        false,
                        enabled,
                    )
                    .map(|button| {
                        #[cfg(feature = "ui-restoration-probe")]
                        let button = button.relative().child(probe::control_bounds(
                            self.workspace.capture_controls[0].clone(),
                        ));
                        button
                    })
                    .on_click(cx.listener(move |host, _, _, cx| {
                        if enabled {
                            host.capture_command(action);
                            cx.notify();
                        }
                    })),
                )
                .child(actions);
        }
        let actions = actions.window_control_area(gpui::WindowControlArea::Client);
        let back = compact_button(
            "chart-back",
            if detail {
                self.text("← 总览", "← Overview")
            } else {
                self.text("← 设备配置", "← Setup")
            },
            palette,
            true,
            !locked && !pending,
        )
        .window_control_area(gpui::WindowControlArea::Client)
        .on_click(cx.listener(|host, _, window, cx| {
            if host.lock.locked() || host.runtime.borrow().pending_action().is_some() {
                return;
            }
            if host.workspace.detail.is_some() {
                host.show_plot_overview();
                cx.notify();
            } else {
                host.return_to_performance_setup(window, cx);
                window.remove_window();
            }
        }));
        let mut monitor_choices = div().flex().flex_col().gap(px(2.0));
        if self.workspace.monitor_menu {
            for index in catalog() {
                let (_, chinese, english) = METRICS[index];
                let selected = self.settings.monitor_metrics.contains(&index);
                monitor_choices = monitor_choices.child(
                    toggle_row(
                        format!("monitor-option-{index}"),
                        self.text(chinese, english),
                        palette,
                        selected,
                        configurable,
                    )
                    .on_click(cx.listener(move |host, _, _, cx| {
                        if configurable {
                            host.toggle_monitor(index);
                            cx.notify();
                        }
                    })),
                );
            }
        }
        let choices = div()
            .relative()
            .flex()
            .flex_shrink_0()
            .items_center()
            .gap(px(4.0))
            .child(
                compact_button(
                    "workspace-analysis",
                    self.text("查看指标 ▾", "Explore metrics ▾"),
                    palette,
                    false,
                    configurable && self.workspace.analysis.is_some(),
                )
                .when(self.workspace.detail_menu, |button| {
                    button
                        .bg(rgb(palette.selected))
                        .text_color(rgb(palette.accent_hover))
                })
                .on_click(cx.listener(|host, _, _, cx| {
                    if host.workspace.analysis.is_some()
                        && !host.lock.locked()
                        && host.runtime.borrow().pending_action().is_none()
                    {
                        host.workspace.detail_menu = !host.workspace.detail_menu;
                        host.workspace.monitor_menu = false;
                        host.workspace.data_menu = false;
                        cx.notify();
                    }
                }))
                .map(|button| div().relative().child(button))
                .when(self.workspace.detail_menu, |row| {
                    row.child(gpui::deferred(self.analysis_menu(cx)).with_priority(3))
                }),
            )
            .child(
                compact_button(
                    "workspace-data",
                    self.text("数据导入与导出 ▾", "Import & export ▾"),
                    palette,
                    false,
                    configurable,
                )
                .when(self.workspace.data_menu, |button| {
                    button
                        .bg(rgb(palette.selected))
                        .text_color(rgb(palette.accent_hover))
                })
                .on_click(cx.listener(|host, _, _, cx| {
                    if !host.lock.locked() && host.runtime.borrow().pending_action().is_none() {
                        host.workspace.data_menu = !host.workspace.data_menu;
                        host.workspace.detail_menu = false;
                        host.workspace.monitor_menu = false;
                        cx.notify();
                    }
                }))
                .map(|button| div().relative().child(button))
                .when(self.workspace.data_menu, |row| {
                    row.child(gpui::deferred(self.analysis_data_menu(cx)).with_priority(3))
                }),
            )
            .when(!detail, |choices| {
                choices.child(
                    compact_button(
                        "workspace-monitors",
                        format!(
                            "{} {} / 3 ▾",
                            self.text("实时图表", "Live charts"),
                            self.settings.monitor_metrics.len()
                        ),
                        palette,
                        false,
                        configurable && !detail,
                    )
                    .when(self.workspace.monitor_menu, |button| {
                        button
                            .bg(rgb(palette.selected))
                            .text_color(rgb(palette.accent_hover))
                    })
                    .on_click(cx.listener(move |host, _, _, cx| {
                        if configurable && !detail {
                            host.workspace.monitor_menu = !host.workspace.monitor_menu;
                            host.workspace.detail_menu = false;
                            host.workspace.data_menu = false;
                            host.device_menu = false;
                            cx.notify();
                        }
                    }))
                    .map(|button| div().relative().child(button))
                    .when(self.workspace.monitor_menu, |row| {
                        row.child(
                            gpui::deferred(
                                div()
                                    .absolute()
                                    .top(px(42.0))
                                    .left_0()
                                    .w(px(292.0))
                                    .max_h(px(520.0))
                                    .id("workspace-monitor-popup")
                                    .rounded(px(12.0))
                                    .shadow_lg()
                                    .overflow_y_scroll()
                                    .p(px(8.0))
                                    .bg(rgb(palette.panel))
                                    .border_1()
                                    .border_color(rgb(palette.hairline))
                                    .occlude()
                                    .on_mouse_down_out(cx.listener(|host, _, _, cx| {
                                        host.workspace.monitor_menu = false;
                                        cx.notify();
                                    }))
                                    .child(
                                        div()
                                            .px(px(8.0))
                                            .py(px(6.0))
                                            .text_size(px(12.0))
                                            .text_color(rgb(palette.muted))
                                            .child(self.text(
                                                "选择侧栏图表，最多 3 项",
                                                "Choose up to 3 sidebar charts",
                                            )),
                                    )
                                    .when_some(self.workspace.admission.clone(), |row, message| {
                                        row.child(
                                            div()
                                                .px(px(8.0))
                                                .py(px(6.0))
                                                .text_size(px(12.0))
                                                .text_color(rgb(palette.accent_hover))
                                                .child(message),
                                        )
                                    })
                                    .child(monitor_choices),
                            )
                            .with_priority(2),
                        )
                    }),
                )
            })
            .window_control_area(gpui::WindowControlArea::Client);
        let choices = choices;
        let mut analysis = if detail {
            self.render_plot_detail(window, cx)
        } else {
            let mut monitors = div()
                .flex()
                .gap(px(8.0))
                .when(!bottom, |row| row.flex_col());
            for (index, metric) in self
                .settings
                .monitor_metrics
                .clone()
                .iter()
                .take(MAX_MONITORS)
                .copied()
                .enumerate()
            {
                let (_, chinese, english) = METRICS[metric];
                let unit = metric_unit(metric);
                let views = self.visible_views(self.workspace.monitors[index].iter().cloned());
                let ready = views
                    .first()
                    .and_then(|view| view.borrow().presented_result())
                    .map_or(0, |r| r.request.prefix.received_sequence);
                let target = self.workspace.targets[index + 1]
                    .as_ref()
                    .map_or(0, |r| r.prefix.received_sequence);
                let can_fit = configurable && !views.is_empty();
                let mut title_row = div()
                    .id(("monitor-detail", metric))
                    .rounded(px(6.0))
                    .px(px(8.0))
                    .when(can_open_detail, |row| {
                        row.cursor_pointer()
                            .focusable()
                            .hover(move |style| style.bg(rgb(palette.hover)))
                            .active(move |style| style.bg(rgb(palette.selected)))
                            .focus(move |style| style.bg(rgb(palette.selected)))
                    })
                    .on_click(cx.listener(move |host, _, _, cx| {
                        if can_open_detail {
                            host.open_plot_detail(metric, false);
                            cx.notify();
                        }
                    }))
                    .min_h(px(36.0))
                    .flex_1()
                    .min_w_0()
                    .flex()
                    .flex_wrap()
                    .gap_x(px(8.0))
                    .gap_y(px(4.0))
                    .items_center()
                    .justify_between()
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap(px(10.0))
                            .child(
                                div()
                                    .text_size(px(14.0))
                                    .font_weight(gpui::FontWeight::MEDIUM)
                                    .child(self.text(chinese, english)),
                            )
                            .child(
                                div()
                                    .font_family(design::DISPLAY_FONT)
                                    .text_size(px(12.0))
                                    .text_color(rgb(palette.muted))
                                    .child(unit),
                            ),
                    )
                    .child(
                        div()
                            .w(px(if self.settings.english { 54.0 } else { 36.0 }))
                            .flex_shrink_0()
                            .whitespace_nowrap()
                            .text_size(px(12.0))
                            .text_color(rgb(palette.muted))
                            .child(if phase == crate::backend::CapturePhase::Collecting {
                                self.text("实时", "Live")
                            } else if ready < target {
                                self.text("更新中", "Updating")
                            } else {
                                ""
                            }),
                    );
                let paired = crate::product::secondary_metric(metric, false).is_some();
                if paired || visible_sides.len() > 1 {
                    let mut labels = div().flex().flex_wrap().gap(px(6.0));
                    for &side in &visible_sides {
                        let side = side.min(2);
                        let color = crate::chart_view::dataset_color(self.settings.dark, side);
                        if paired {
                            let axes = if visible_sides.len() == 1 {
                                ["X", "Y"]
                            } else {
                                [["A.X", "A.Y"], ["B.X", "B.Y"], ["C.X", "C.Y"]][side]
                            };
                            labels = labels.child(legend(axes[0], color, palette)).child(legend(
                                axes[1],
                                [0x55adcf, 0xe6b850, 0x9abbae][side],
                                palette,
                            ));
                        } else {
                            labels = labels.child(legend(["A", "B", "C"][side], color, palette));
                        }
                    }
                    title_row = title_row.child(labels);
                }
                let mut plot_actions = div().flex().items_center().gap(px(2.0)).child(
                    compact_button(
                        format!("monitor-fit-{metric}"),
                        self.text("全局视图", "Global view"),
                        palette,
                        false,
                        can_fit,
                    )
                    .h(px(30.0))
                    .px(px(6.0))
                    .on_click(cx.listener(move |host, _, _, cx| {
                        cx.stop_propagation();
                        if !can_fit {
                            return;
                        }
                        let views =
                            host.visible_views(host.workspace.monitors[index].iter().cloned());
                        if let Some(view) = views.first() {
                            view.borrow_mut().fit_trajectory();
                            host.sync_monitor_time(view);
                            host.workspace
                                .interaction
                                .as_ref()
                                .unwrap()
                                .notify_plots(cx);
                        }
                    })),
                );
                if metric == 5 {
                    plot_actions = plot_actions.child(
                        compact_button(
                            format!("monitor-focus-{metric}"),
                            self.text("主体视图", "Focus view"),
                            palette,
                            false,
                            can_fit,
                        )
                        .h(px(30.0))
                        .px(px(6.0))
                        .on_click(cx.listener(move |host, _, _, cx| {
                            cx.stop_propagation();
                            if !can_fit {
                                return;
                            }
                            if let Some(view) = host
                                .visible_views(host.workspace.monitors[index].iter().cloned())
                                .first()
                            {
                                view.borrow_mut().fit_value_axis();
                                host.workspace
                                    .interaction
                                    .as_ref()
                                    .unwrap()
                                    .notify_plot(index + 1, cx);
                            }
                        })),
                    );
                }
                let title_row = div()
                    .flex()
                    .items_baseline()
                    .flex_shrink_0()
                    .gap(px(8.0))
                    .child(title_row)
                    .child(plot_actions);
                monitors = monitors.child(
                    div()
                        .flex_1()
                        .min_w_0()
                        .min_h(px(160.0))
                        .flex()
                        .flex_col()
                        .overflow_hidden()
                        .child(title_row)
                        .child(
                            self.workspace_chart_canvas(index + 1, cx)
                                .id(("monitor-hover", index))
                                .on_mouse_down(
                                    gpui::MouseButton::Left,
                                    cx.listener(
                                        move |host, event: &gpui::MouseDownEvent, _, cx| {
                                            if !host.lock.locked()
                                                && let Some(view) = host
                                                    .visible_views(
                                                        host.workspace.monitors[index]
                                                            .iter()
                                                            .cloned(),
                                                    )
                                                    .first()
                                                    .cloned()
                                            {
                                                if event.click_count == 2 {
                                                    view.borrow_mut().drag_end();
                                                    host.open_plot_detail(metric, false);
                                                } else {
                                                    view.borrow_mut().drag_start(event.position);
                                                }
                                                cx.notify();
                                            }
                                        },
                                    ),
                                )
                                .on_mouse_move(cx.listener(
                                    move |host, event: &gpui::MouseMoveEvent, _, cx| {
                                        if !host.lock.locked()
                                            && let Some(view) = host
                                                .visible_views(
                                                    host.workspace.monitors[index].iter().cloned(),
                                                )
                                                .first()
                                                .cloned()
                                        {
                                            if event.pressed_button != Some(gpui::MouseButton::Left)
                                            {
                                                view.borrow_mut().drag_end();
                                            }
                                            host.queue_analysis_pointer(
                                                view.clone(),
                                                event.position,
                                                cx,
                                            );
                                        }
                                    },
                                ))
                                .on_mouse_up(
                                    gpui::MouseButton::Left,
                                    cx.listener(move |host, _, _, _| {
                                        for view in &host.workspace.monitors[index] {
                                            view.borrow_mut().drag_end();
                                        }
                                    }),
                                )
                                .on_mouse_up_out(
                                    gpui::MouseButton::Left,
                                    cx.listener(move |host, _, _, _| {
                                        for view in &host.workspace.monitors[index] {
                                            view.borrow_mut().drag_end();
                                        }
                                    }),
                                )
                                .on_scroll_wheel(cx.listener(
                                    move |host, event: &gpui::ScrollWheelEvent, _, cx| {
                                        if !host.lock.locked()
                                            && let Some(view) = host
                                                .visible_views(
                                                    host.workspace.monitors[index].iter().cloned(),
                                                )
                                                .first()
                                                .cloned()
                                        {
                                            view.borrow_mut().scroll_zoom(event);
                                            host.sync_monitor_time(&view);
                                            cx.stop_propagation();
                                            host.workspace
                                                .interaction
                                                .as_ref()
                                                .unwrap()
                                                .notify_plots(cx);
                                        }
                                    },
                                )),
                        ),
                );
            }
            let trace = self.workspace_chart_canvas(0, cx);
            let trajectory_host = cx.entity().downgrade();
            let main = div()
                .flex_1()
                .min_h(px(360.0))
                .min_w_0()
                .flex()
                .flex_col()
                .overflow_hidden()
                .child(
                    div()
                        .min_h(px(36.0))
                        .flex_shrink_0()
                        .flex()
                        .items_center()
                        .justify_between()
                        .gap(px(12.0))
                        .child(
                            div()
                                .flex()
                                .items_center()
                                .flex_shrink_0()
                                .gap(px(10.0))
                                .child(
                                    div()
                                        .id("trajectory-detail")
                                        .rounded(px(6.0))
                                        .min_h(px(30.0))
                                        .flex()
                                        .items_center()
                                        .when(can_open_detail, |title| {
                                            title
                                                .cursor_pointer()
                                                .focusable()
                                                .hover(move |style| {
                                                    style.text_color(rgb(palette.accent_hover))
                                                })
                                                .active(move |style| {
                                                    style.text_color(rgb(palette.accent))
                                                })
                                                .focus(move |style| style.bg(rgb(palette.selected)))
                                        })
                                        .text_size(px(18.0))
                                        .font_weight(gpui::FontWeight::MEDIUM)
                                        .child(self.text("移动轨迹", "Trajectory"))
                                        .on_click(cx.listener(move |host, _, _, cx| {
                                            if can_open_detail {
                                                host.open_plot_detail(11, false);
                                                cx.notify();
                                            }
                                        })),
                                )
                                .child(
                                    div()
                                        .font_family(design::DISPLAY_FONT)
                                        .text_size(px(12.0))
                                        .text_color(rgb(palette.muted))
                                        .child("count"),
                                ),
                        )
                        .child(self.workspace_summary_readout())
                        .child(
                            compact_button(
                                "fit-trajectory",
                                self.text("全局视图", "Global view"),
                                palette,
                                false,
                                configurable && self.analysis_chart().is_some(),
                            )
                            .h(px(30.0))
                            .py_0()
                            .border_0()
                            .on_click(cx.listener(|host, _, _, cx| {
                                if host.lock.locked()
                                    || host.runtime.borrow().pending_action().is_some()
                                {
                                    return;
                                }
                                if let Some(chart) = host.analysis_chart() {
                                    chart.borrow_mut().fit_trajectory();
                                    host.workspace
                                        .interaction
                                        .as_ref()
                                        .unwrap()
                                        .notify_plot(0, cx);
                                }
                            })),
                        ),
                )
                .child(
                    div()
                        .flex_1()
                        .min_h_0()
                        .relative()
                        .flex()
                        .flex_col()
                        .child(trace)
                        .child(
                            canvas(
                                move |bounds, _, cx| {
                                    let _ = trajectory_host.update(cx, |host, _| {
                                        host.workspace.trajectory_bounds =
                                            crate::chart_view::workspace_plot_bounds(bounds);
                                    });
                                },
                                |_, _, _, _| {},
                            )
                            .absolute()
                            .inset_0()
                            .size_full(),
                        )
                        .id("workspace-trajectory")
                        .on_mouse_down(
                            gpui::MouseButton::Left,
                            cx.listener(|host, event: &gpui::MouseDownEvent, window, cx| {
                                if !host.lock.locked() {
                                    host.workspace.canvas_press = None;
                                    if let Some(chart) = host.analysis_chart() {
                                        if event.click_count == 2 {
                                            chart.borrow_mut().drag_end();
                                            host.open_plot_detail(11, false);
                                            cx.notify();
                                        } else {
                                            if host.workspace.analysis.is_none()
                                                && !event.modifiers.shift
                                                && !event.modifiers.control
                                            {
                                                host.workspace.canvas_press = Some(event.position);
                                            }
                                            chart.borrow_mut().drag_start(event.position);
                                        }
                                    } else {
                                        host.start_from_canvas(event.position, window);
                                        cx.notify();
                                    }
                                }
                            }),
                        )
                        .on_mouse_move(cx.listener(|host, event: &gpui::MouseMoveEvent, _, cx| {
                            if !host.lock.locked()
                                && let Some(chart) = host.analysis_chart()
                            {
                                if event.pressed_button != Some(gpui::MouseButton::Left) {
                                    chart.borrow_mut().drag_end();
                                }
                                host.queue_analysis_pointer(chart, event.position, cx);
                            }
                        }))
                        .on_mouse_up(
                            gpui::MouseButton::Left,
                            cx.listener(|host, event: &gpui::MouseUpEvent, window, cx| {
                                if let Some(chart) = host.analysis_chart() {
                                    chart.borrow_mut().drag_end();
                                }
                                if let Some(start) = host.workspace.canvas_press.take()
                                    && f32::from(event.position.x - start.x).abs() < 3.0
                                    && f32::from(event.position.y - start.y).abs() < 3.0
                                {
                                    host.start_from_canvas(start, window);
                                    cx.notify();
                                }
                            }),
                        )
                        .on_mouse_up_out(
                            gpui::MouseButton::Left,
                            cx.listener(|host, _, _, _| {
                                host.workspace.canvas_press = None;
                                if let Some(chart) = host.analysis_chart() {
                                    chart.borrow_mut().drag_end();
                                }
                            }),
                        )
                        .on_scroll_wheel(cx.listener(
                            |host, event: &gpui::ScrollWheelEvent, _, cx| {
                                if !host.lock.locked()
                                    && let Some(chart) = host.analysis_chart()
                                {
                                    chart.borrow_mut().scroll_zoom(event);
                                    cx.stop_propagation();
                                    host.workspace
                                        .interaction
                                        .as_ref()
                                        .unwrap()
                                        .notify_plot(0, cx);
                                }
                            },
                        )),
                );
            let plots = div()
                .flex()
                .flex_1()
                .min_h_0()
                .gap(px(12.0))
                .when(bottom, |row| row.flex_col())
                .child(main)
                .when(!self.settings.monitor_metrics.is_empty(), |row| {
                    row.child(
                        monitors
                            .when(bottom, |row| row.h(px(210.0)).flex_shrink_0())
                            .when(!bottom, |row| row.w(px(sidebar_width)).flex_shrink_0()),
                    )
                });
            div()
                .flex()
                .flex_col()
                .flex_1()
                .min_h_0()
                .gap(px(4.0))
                .child(self.analysis_header(11, cx))
                .child(plots)
        };
        let (opacity, animating) = self.workspace.motion.advance(std::time::Instant::now());
        if animating {
            window.request_animation_frame();
        }
        analysis = analysis.opacity(opacity);
        self.workspace
            .interaction
            .as_ref()
            .unwrap()
            .set_opacity(opacity, cx);
        let capture_error = self
            .runtime
            .borrow()
            .status()
            .filter(|status| status.generation >= self.page_generation)
            .and_then(|status| capture_banner(&status, self.settings.english));
        let state = div()
            .flex()
            .flex_shrink_0()
            .items_center()
            .gap(px(8.0))
            .text_size(px(13.0))
            .text_color(rgb(palette.muted))
            .child(
                div()
                    .w(px(6.0))
                    .h(px(6.0))
                    .rounded_full()
                    .bg(rgb(if locked {
                        palette.accent
                    } else {
                        palette.muted
                    })),
            )
            .child(if pending {
                self.text("处理中…", "Pending…").to_owned()
            } else {
                localized_state(&format!("{phase:?}"), self.settings.english).to_owned()
            })
            .when(
                self.workspace
                    .analysis
                    .as_ref()
                    .map(|analysis| &analysis.input)
                    .or(self.workspace.latest.as_ref())
                    .is_some_and(|prefix| !prefix.complete),
                |row| {
                    row.child(
                        div()
                            .text_color(rgb(0xb04758))
                            .child(self.text("数据不完整", "Incomplete data")),
                    )
                },
            )
            .when(
                self.workspace
                    .analysis
                    .as_ref()
                    .zip(self.workspace.latest.as_ref())
                    .is_some_and(|(analysis, latest)| {
                        analysis.input.phase != latest.phase
                            || analysis.input.received_sequence != latest.received_sequence
                    }),
                |row| {
                    row.child(
                        compact_button(
                            "adopt-final-prefix",
                            self.text("查看最新结果", "View latest"),
                            palette,
                            false,
                            !locked && !pending,
                        )
                        .h(px(24.0))
                        .border_0()
                        .on_click(cx.listener(|host, _, _, cx| {
                            host.workspace.analysis = None;
                            host.workspace.targets = Default::default();
                            cx.notify();
                        })),
                    )
                },
            );
        let state = state;
        let caption = div()
            .h(px(shell::CAPTION_HEIGHT))
            .w_full()
            .flex()
            .flex_shrink_0()
            .items_center()
            .border_b_1()
            .border_color(rgb(palette.hairline))
            .window_control_area(gpui::WindowControlArea::Drag)
            .child(
                div()
                    .h_full()
                    .flex_1()
                    .min_w_0()
                    .pl(px(gutter - 12.0))
                    .pr(px(16.0))
                    .flex()
                    .items_center()
                    .justify_between()
                    .gap(px(12.0))
                    .child(
                        div()
                            .flex()
                            .flex_shrink_0()
                            .items_center()
                            .gap(px(8.0))
                            .child(back)
                            .child(choices),
                    )
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .text_size(px(12.0))
                            .text_color(rgb(palette.muted))
                            .overflow_hidden()
                            .text_ellipsis()
                            .child(
                                (if self.file_busy {
                                    self.text("正在处理文件…", "Processing file…")
                                } else if let Some(message) = &self.file_message {
                                    message.as_str()
                                } else if locked {
                                    self.text("右键或 Esc 暂停", "Right click or Esc to pause")
                                } else if detail {
                                    ""
                                } else if self.imported.is_some() {
                                    self.text("双击图表查看详情", "Double-click a chart to explore")
                                } else if phase == crate::backend::CapturePhase::Paused {
                                    self.text("拖动浏览 · 滚轮缩放", "Drag to pan · Scroll to zoom")
                                } else if phase == crate::backend::CapturePhase::Stopped {
                                    self.text(
                                        "点击轨迹开始新记录",
                                        "Click the trajectory to start a new capture",
                                    )
                                } else {
                                    ""
                                })
                                .to_owned(),
                            ),
                    )
                    .child(
                        div()
                            .flex()
                            .flex_shrink_0()
                            .items_center()
                            .gap(px(16.0))
                            .window_control_area(gpui::WindowControlArea::Client)
                            .child(state)
                            .child(actions),
                    ),
            )
            .child(
                caption_button("chart-minimize", "−", palette)
                    .on_click(|_, window, _| window.minimize_window()),
            )
            .child(
                caption_button(
                    "chart-maximize",
                    if window.is_maximized() { "▢" } else { "□" },
                    palette,
                )
                .on_click(|_, window, _| shell::toggle_maximize(window)),
            )
            .child(
                caption_button("chart-close", "×", palette).on_click(cx.listener(
                    |host, _, window, cx| {
                        host.return_to_performance_setup(window, cx);
                        window.remove_window();
                    },
                )),
            );
        let body = div()
            .flex_1()
            .min_h_0()
            .flex()
            .flex_col()
            .px(px(gutter))
            .pt(px(8.0))
            .pb(px(8.0))
            .gap(px(6.0))
            .when_some(self.control_error.clone(), |row, message| {
                row.child(
                    div()
                        .text_size(px(12.0))
                        .text_color(rgb(0xb04758))
                        .child(message),
                )
            })
            .when_some(capture_error, |row, message| {
                row.child(
                    div()
                        .text_size(px(12.0))
                        .text_color(rgb(0xb04758))
                        .child(message),
                )
            })
            .child(analysis);
        div()
            .id("live-workspace")
            .size_full()
            .flex()
            .flex_col()
            .bg(rgb(palette.background))
            .text_color(rgb(palette.text))
            .font_family(design::Typography::body_font(self.settings.english))
            .text_size(px(13.0))
            .child(caption)
            .child(body)
    }

    fn workspace_summary_text(&self) -> String {
        let summary_prefix = self
            .workspace
            .analysis
            .as_ref()
            .map(|analysis| &analysis.input)
            .or_else(|| {
                self.workspace
                    .summary
                    .as_ref()
                    .filter(|summary| {
                        summary.request.source_version == self.import_identity
                            && self.workspace.latest.as_ref().is_some_and(|latest| {
                                latest.generation == summary.request.prefix.generation
                                    && latest.phase == summary.request.prefix.phase
                            })
                    })
                    .map(|summary| &summary.request.prefix)
            })
            .or(self.workspace.latest.as_ref())
            .filter(|prefix| !prefix.datasets.is_empty());
        summary_prefix.map_or_else(
            || {
                (0..self.selected.len().max(1))
                    .map(|side| {
                        format!(
                            "{}\t{}:0\t{}:0.00 cm\t{}:—",
                            ["A", "B", "C"][side.min(2)],
                            self.text("报告", "reports"),
                            self.text("路径", "path"),
                            self.text("速度", "speed"),
                        )
                    })
                    .collect::<Vec<_>>()
                    .join("\n")
            },
            |prefix| {
                prefix
                    .datasets
                    .iter()
                    .enumerate()
                    .map(|(side, (key, snapshot))| {
                        let cpi = self.performance_cpi(*key, side);
                        let path_cm = cpi.map_or("—".into(), |cpi| {
                            format!("{:.2} cm", snapshot.summary_path_counts() * 2.54 / cpi)
                        });
                        let speed = self
                            .workspace
                            .summary
                            .as_ref()
                            .filter(|summary| {
                                summary.request.source_version == self.import_identity
                                    && summary.request.dataset_cpi.get(side) == Some(&(*key, cpi))
                                    && summary.request.prefix.generation == prefix.generation
                                    && summary.request.prefix.received_sequence
                                        == prefix.received_sequence
                                    && summary.request.prefix.phase == prefix.phase
                            })
                            .and_then(|summary| summary.speeds.get(side).copied().flatten());
                        format!(
                            "{}\t{}:{}\t{}:{}\t{}:{}",
                            ["A", "B", "C"][side.min(2)],
                            self.text("报告", "reports"),
                            snapshot.event_count(),
                            self.text("路径", "path"),
                            path_cm,
                            self.text("速度", "speed"),
                            speed.map_or("—".into(), |v| format!("{v:.3} m/s"))
                        )
                    })
                    .collect::<Vec<_>>()
                    .join("\n")
            },
        )
    }

    pub(super) fn select_workspace_device(&mut self, side: usize, key: u32) {
        if side > 1
            || self.lock.locked()
            || self.runtime.borrow().pending_action().is_some()
            || !self
                .runtime
                .borrow()
                .device_catalog
                .can_choose(key, self.selected.get(1 - side).copied())
        {
            return;
        }
        if self.selected.get(side) == Some(&key) {
            return;
        }
        if side < self.selected.len() {
            self.selected[side] = key;
        } else if side == self.selected.len() {
            self.selected.push(key);
        } else {
            return;
        }
        self.runtime.borrow_mut().preferred_device = self.selected.first().copied();
        self.capture_command("reset");
    }
}

pub(super) fn compact_button(
    id: impl Into<gpui::SharedString>,
    label: impl Into<gpui::SharedString>,
    palette: design::Palette,
    primary: bool,
    enabled: bool,
) -> gpui::Stateful<gpui::Div> {
    div()
        .id(id.into())
        .h(px(34.0))
        .rounded(px(7.0))
        .px(px(12.0))
        .flex_shrink_0()
        .flex()
        .items_center()
        .justify_center()
        .text_size(px(13.0))
        .line_height(px(20.0))
        .font_weight(if primary {
            gpui::FontWeight::SEMIBOLD
        } else {
            gpui::FontWeight::NORMAL
        })
        .bg(rgb(if primary {
            palette.accent
        } else {
            palette.background
        }))
        .text_color(rgb(if primary {
            palette.accent_text
        } else {
            palette.text
        }))
        .when(enabled, |button| {
            button
                .focusable()
                .cursor_pointer()
                .hover(move |style| {
                    style.bg(rgb(if primary {
                        palette.accent_hover
                    } else {
                        palette.hover
                    }))
                })
                .active(move |style| {
                    style
                        .bg(rgb(if primary {
                            palette.accent
                        } else {
                            palette.selected
                        }))
                        .opacity(0.8)
                })
                .focus(move |style| {
                    style.bg(rgb(if primary {
                        palette.accent_hover
                    } else {
                        palette.selected
                    }))
                })
        })
        .when(!enabled, |button| button.opacity(0.35))
        .child(div().min_w_0().max_w_full().child(label.into()))
}

pub(super) fn toggle_indicator(selected: bool, palette: design::Palette) -> gpui::Div {
    div()
        .w(px(28.0))
        .h(px(16.0))
        .flex_shrink_0()
        .rounded_full()
        .relative()
        .bg(rgb(if selected {
            palette.accent
        } else {
            palette.muted
        }))
        .child(
            div()
                .absolute()
                .top(px(3.0))
                .left(px(if selected { 15.0 } else { 3.0 }))
                .size(px(10.0))
                .rounded_full()
                .bg(rgb(0xffffff)),
        )
}

pub(super) fn toggle_row(
    id: impl Into<gpui::SharedString>,
    label: impl Into<gpui::SharedString>,
    palette: design::Palette,
    selected: bool,
    enabled: bool,
) -> gpui::Stateful<gpui::Div> {
    div()
        .id(id.into())
        .h(px(36.0))
        .px(px(8.0))
        .rounded(px(6.0))
        .flex()
        .items_center()
        .justify_between()
        .gap(px(12.0))
        .when(enabled, |row| {
            row.focusable()
                .cursor_pointer()
                .hover(move |style| style.bg(rgb(palette.hover)))
                .active(move |style| style.bg(rgb(palette.selected)))
                .focus(move |style| style.bg(rgb(palette.selected)))
        })
        .when(!enabled, |row| row.opacity(0.4))
        .child(label.into())
        .child(toggle_indicator(selected, palette))
}

fn caption_button(
    id: &'static str,
    label: &'static str,
    palette: design::Palette,
) -> gpui::Stateful<gpui::Div> {
    div()
        .id(id)
        .group(id)
        .w(px(44.0))
        .h(px(shell::CAPTION_HEIGHT - 1.0))
        .flex_shrink_0()
        .flex()
        .items_center()
        .justify_center()
        .font_family(design::DISPLAY_FONT)
        .text_size(px(18.0))
        .window_control_area(gpui::WindowControlArea::Client)
        .cursor_pointer()
        .hover(move |style| {
            if id == "chart-close" {
                style.bg(rgb(0xc42b3b)).text_color(rgb(0xffffff))
            } else {
                style.bg(rgb(palette.hover))
            }
        })
        .active(|style| style.opacity(0.7))
        .child(
            design::window_icon(label)
                .text_color(rgb(palette.text))
                .when(id == "chart-close", |icon| {
                    icon.group_hover(id, |style| style.text_color(rgb(0xffffff)))
                }),
        )
}

fn capture_banner(status: &crate::backend::AppStatus, english: bool) -> Option<String> {
    let message = capture_status_error(status)?;
    let title = match (capture_incomplete(status), english) {
        (true, false) => "采集中断：",
        (true, true) => "Capture interrupted:",
        (false, false) => "采集提示：",
        (false, true) => "Capture notice:",
    };
    Some(format!("{title} {message}"))
}

fn legend(label: &'static str, color: u32, palette: design::Palette) -> gpui::Div {
    div()
        .flex()
        .items_center()
        .gap(px(6.0))
        .text_size(px(12.0))
        .text_color(rgb(palette.muted))
        .child(div().w(px(12.0)).h(px(2.0)).bg(rgb(color)))
        .child(label)
}

fn metric_unit(metric: usize) -> &'static str {
    crate::chart::labels::Unit::metric(METRICS[metric].0).symbol()
}

pub(super) fn catalog() -> [usize; 20] {
    // The original display order, followed by native-only component metrics.
    [
        0, 1, 17, 5, 8, 13, 14, 19, 16, 9, 10, 18, 11, 6, 7, 3, 4, 2, 12, 15,
    ]
}

fn new_view(
    result: Arc<ChartResult>,
    side: usize,
    slot: usize,
    settings: &Settings,
) -> Result<Rc<RefCell<ChartView>>, String> {
    let budget = view_limits(slot, result.datasets.len(), settings.monitor_metrics.len());
    let mut view = ChartView::with_resource_limits(result, budget)?;
    if side > 0 {
        view.select_dataset(side)?;
    }
    view.set_theme(settings.dark, settings.english);
    view.enable_trajectory();
    Ok(Rc::new(RefCell::new(view)))
}

pub(super) fn chart_canvas(
    views: Vec<Rc<RefCell<ChartView>>>,
    outcome: Option<Arc<Mutex<WindowSmokeReport>>>,
    dark: bool,
    english: bool,
    on_present: impl Fn(u64, &mut Window) + 'static,
    on_pan: impl Fn(Rc<RefCell<ChartView>>, &mut gpui::App) + 'static,
) -> gpui::Div {
    let prepare = views.clone();
    let paint = views.clone();
    let on_pan = Rc::new(on_pan);
    div().size_full().flex_1().min_h_0().relative().child(
        canvas(
            move |bounds, window, _| {
                let mut viewport = None;
                let grouped = prepare.len() > 1;
                // All visible datasets finish one common result before advancing together.
                let mut target = prepare.iter().find_map(|view| view.borrow().group_target());
                for (index, view) in prepare.iter().enumerate() {
                    let mut view = view.borrow_mut();
                    view.set_overlay(index > 0);
                    view.set_theme(dark, english);
                    if let Some(viewport) = viewport {
                        view.set_viewport(viewport, true);
                    }
                    if grouped {
                        view.prepaint_group(bounds, window, target.clone());
                    } else {
                        // A single dataset can publish and start its next layer in one pass.
                        view.prepaint(bounds, window);
                    }
                    if target.is_none() {
                        target = view.group_target();
                    }
                    viewport = Some(view.viewport());
                }
                if grouped
                    && let Some(primary) = prepare.first()
                    && prepare
                        .iter()
                        .all(|view| view.borrow().group_ready(&primary.borrow()))
                {
                    for view in &prepare {
                        view.borrow_mut().present_group();
                    }
                }
                if let Some(primary) = prepare.first() {
                    let primary = primary.borrow();
                    for view in prepare.iter().skip(1) {
                        view.borrow_mut()
                            .set_overlay_viewport(primary.presented_viewport());
                    }
                    if !primary.ready() {
                        window.request_animation_frame();
                    }
                    if let Some(result) = primary.presented_result() {
                        on_present(result.request.id, window);
                    }
                }
            },
            move |bounds, _, window, cx| {
                if let Some(view) = paint.first() {
                    let view = view.clone();
                    let owner = window.current_view();
                    let on_pan = on_pan.clone();
                    window.on_mouse_event(move |event: &gpui::MouseMoveEvent, phase, _, cx| {
                        if phase == gpui::DispatchPhase::Capture && view.borrow().is_dragging() {
                            if event.pressed_button == Some(gpui::MouseButton::Left) {
                                let moved = view.borrow_mut().move_pointer(event.position);
                                if moved {
                                    on_pan(view.clone(), cx);
                                    cx.notify(owner);
                                }
                                // A pressed chart owns movement even outside its hitbox.
                                cx.stop_propagation();
                            } else {
                                view.borrow_mut().drag_end();
                            }
                        }
                    });
                }
                if paint.is_empty() {
                    let plot = crate::chart_view::workspace_plot_bounds(bounds);
                    let palette = design::Palette::for_theme(dark);
                    for index in 0..=4 {
                        let fraction = index as f32 / 4.0;
                        window.paint_quad(gpui::fill(
                            gpui::Bounds {
                                origin: plot.origin
                                    + gpui::point(plot.size.width * fraction, px(0.0)),
                                size: size(px(1.0), plot.size.height),
                            },
                            rgb(palette.hairline),
                        ));
                        window.paint_quad(gpui::fill(
                            gpui::Bounds {
                                origin: plot.origin
                                    + gpui::point(px(0.0), plot.size.height * fraction),
                                size: size(plot.size.width, px(1.0)),
                            },
                            rgb(palette.hairline),
                        ));
                    }
                }
                for (index, view) in paint.iter().enumerate() {
                    if index > 0 && !view.borrow().can_overlay(&paint[0].borrow()) {
                        continue;
                    }
                    view.borrow_mut().paint(window, cx);
                    if index == 0
                        && let Some(outcome) = &outcome
                    {
                        outcome.lock().unwrap().chart = Some(view.borrow().report());
                    }
                }
            },
        )
        .size_full(),
    )
}

#[cfg(test)]
mod capture_banner_tests {
    use super::*;

    #[test]
    fn window_handoff_retains_analysis_and_groups_without_window_ownership() {
        let snapshot = Arc::new(
            clicksync_core::Session::default_with_metadata(Default::default())
                .unwrap()
                .snapshot()
                .unwrap(),
        );
        let input = Arc::new(crate::backend::SessionPrefix {
            generation: 7,
            received_sequence: 0,
            received_records: 0,
            phase: crate::backend::CapturePhase::Paused,
            complete: true,
            datasets: vec![(1, snapshot.clone())],
        });
        let workspace = Workspace {
            enabled: true,
            dual: true,
            chart_window: true,
            opening: true,
            closing: true,
            analysis: Some(AnalysisContext {
                input: input.clone(),
                range: Some((1.0, 2.0)),
            }),
            latest: Some(input.clone()),
            detail: Some(5),
            detail_menu: true,
            hidden_datasets: [1001].into(),
            groups: vec![crate::product::performance_analysis::Group {
                key: 1001,
                snapshot: snapshot.clone(),
                cpi: Some(1600.0),
                identity: "comparison".into(),
            }],
            next_group_key: 1001,
            file_epoch: 4,
            ..Default::default()
        };
        let retained = workspace.for_window_handoff();
        assert!(retained.enabled && retained.dual);
        assert!(!retained.chart_window && !retained.opening && !retained.closing);
        assert!(!retained.detail_menu);
        assert!(retained.child.is_none() && retained.return_to.is_none());
        assert!(retained.interaction.is_none() && retained.detail_editors.is_empty());
        assert!(retained.targets.iter().all(Option::is_none));
        assert!(retained.monitors.iter().all(Vec::is_empty));
        assert_eq!(retained.detail, Some(5));
        let analysis = retained.analysis.as_ref().unwrap();
        assert!(Arc::ptr_eq(&analysis.input, &input));
        assert_eq!(analysis.range, Some((1.0, 2.0)));
        assert!(Arc::ptr_eq(retained.latest.as_ref().unwrap(), &input));
        assert!(Arc::ptr_eq(&retained.groups[0].snapshot, &snapshot));
        assert_eq!(retained.groups[0].cpi, Some(1600.0));
        assert_eq!(retained.hidden_datasets, workspace.hidden_datasets);
        assert_eq!(retained.next_group_key, workspace.next_group_key);
        assert_eq!(retained.file_epoch, 5);
        assert_eq!(retained.for_window_handoff().next_group_key, 1001);
    }

    #[test]
    fn a_control_rejection_is_not_a_capture_interruption() {
        let status = crate::backend::AppStatus {
            complete: true,
            error: Some("device selection was rejected".into()),
            ..Default::default()
        };
        assert_eq!(
            capture_banner(&status, true).as_deref(),
            Some("Capture notice: device selection was rejected")
        );
        assert!(
            capture_banner(&status, false)
                .unwrap()
                .starts_with("采集提示：")
        );
        let fault = crate::backend::AppStatus {
            complete: false,
            ..status
        };
        assert!(
            capture_banner(&fault, true)
                .unwrap()
                .starts_with("Capture interrupted:")
        );
        assert!(
            capture_banner(&fault, false)
                .unwrap()
                .starts_with("采集中断：")
        );
    }
}
