use super::*;
use crate::chart::ChartResult;
use crate::product::performance_analysis::{Group, PlotOptions};
use std::{collections::hash_map::RandomState, hash::BuildHasher};
use workspace::compact_button;

fn default_export_filename(mouse_name: &str) -> String {
    let name: String = mouse_name
        .chars()
        .map(|ch| {
            if ch.is_control() || "<>:\"/\\|?*".contains(ch) {
                '_'
            } else {
                ch
            }
        })
        .collect();
    let name = name.trim_matches(|ch: char| ch.is_whitespace() || ch == '.');
    let name = if name.is_empty() { "Mouse" } else { name };
    const CODE_CHARACTERS: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    let code = loop {
        let mut random = RandomState::new().hash_one(mouse_name);
        let code: String = (0..4)
            .map(|_| {
                let ch = CODE_CHARACTERS[(random % CODE_CHARACTERS.len() as u64) as usize];
                random /= CODE_CHARACTERS.len() as u64;
                ch as char
            })
            .collect();
        if code.bytes().any(|ch| ch.is_ascii_uppercase())
            && code.bytes().any(|ch| ch.is_ascii_digit())
        {
            break code;
        }
    };
    format!("{name}_{code}.json")
}

#[derive(Clone, Copy, PartialEq)]
pub(super) enum Destination {
    Primary,
    Comparison,
    Query,
    Output,
    Basis,
}

pub(super) struct FileContext {
    pub operation: u64,
    pub epoch: u64,
    pub source: u64,
    pub request: Option<u64>,
    pub destination: Destination,
}

#[derive(Clone, Copy, PartialEq)]
pub(super) struct RangeEditorContext {
    source: u64,
    generation: u64,
    logical: bool,
    range: Option<(f64, f64)>,
}

#[derive(Default)]
pub(super) struct PlotMotion {
    started: Option<std::time::Instant>,
    from: f32,
}

impl PlotMotion {
    pub fn begin(&mut self, now: std::time::Instant) {
        self.from = if self.started.is_some() {
            self.opacity(now)
        } else {
            0.78
        };
        self.started = Some(now);
    }

    pub fn opacity(&self, now: std::time::Instant) -> f32 {
        let Some(started) = self.started else {
            return 1.0;
        };
        let progress = (now.saturating_duration_since(started).as_secs_f32() / 0.2).min(1.0);
        self.from + (1.0 - self.from) * (1.0 - (1.0 - progress).powi(3))
    }

    pub fn advance(&mut self, now: std::time::Instant) -> (f32, bool) {
        let opacity = self.opacity(now);
        if self
            .started
            .is_some_and(|start| now.saturating_duration_since(start) >= Duration::from_millis(200))
        {
            self.cancel();
        }
        (opacity, self.started.is_some())
    }

    pub fn cancel(&mut self) {
        self.started = None;
    }
}

impl GpuiHost {
    pub(super) fn detail_options(&self, metric: usize) -> PlotOptions {
        let mut options = self
            .workspace
            .detail_options
            .get(&(
                metric,
                self.workspace.detail.is_some() && self.workspace.detail_histogram,
            ))
            .copied()
            .unwrap_or(PlotOptions {
                lines: !matches!(metric, 0 | 1 | 5 | 17),
                trend: matches!(metric, 0 | 1 | 5 | 17),
                ..Default::default()
            });
        if options.gaps
            && let Some(analysis) = &self.workspace.analysis
            && options.gap_dataset.is_none_or(|key| {
                !analysis
                    .input
                    .datasets
                    .iter()
                    .any(|(candidate, _)| *candidate == key)
            })
        {
            options.gap_dataset = analysis.input.datasets.first().map(|(key, _)| *key);
        }
        options
    }

    pub(super) fn open_plot_detail(&mut self, metric: usize, histogram: bool) {
        if self.lock.locked()
            || self.runtime.borrow().pending_action().is_some()
            || self.workspace.analysis.is_none()
        {
            self.workspace.range_error = Some(
                self.text(
                    "请先暂停采集，再打开图表详情。",
                    "Pause capture to open plot detail.",
                )
                .into(),
            );
            return;
        }
        self.save_detail_view();
        if self.workspace.detail.is_none()
            && !histogram
            && let Some(viewport) = self
                .workspace
                .overview_viewports
                .get(&(metric, self.logical))
                .copied()
            && !self
                .workspace
                .detail_viewports
                .contains_key(&(metric, false, self.logical))
        {
            self.workspace
                .detail_viewports
                .insert((metric, false, self.logical), viewport);
        }
        if self.workspace.detail.is_none() {
            let views: Vec<_> = self
                .chart
                .take()
                .into_iter()
                .chain(self.comparison_chart.take())
                .chain(self.workspace.extra_views.drain(..))
                .collect();
            // Hidden primary pixels release their budget; CPU geometry and the
            // three monitor renderers survive the layout change.
            for view in &views {
                view.borrow_mut().close();
            }
            self.workspace.overview_primary = Some(workspace::OverviewPrimary {
                views,
                monitors: std::mem::take(&mut self.workspace.monitors),
                target: self.workspace.targets[0].take(),
                displayed: self.displayed.take(),
            });
        }
        self.retire_primary_views();
        self.workspace.detail = Some(metric);
        self.workspace.detail_histogram = histogram;
        self.workspace.detail_menu = false;
        self.clear_analysis_interaction();
        self.workspace.range_error = None;
        self.workspace.motion.begin(std::time::Instant::now());
    }

    pub(super) fn show_plot_overview(&mut self) {
        if self.workspace.detail.is_none() {
            return;
        }
        self.save_detail_view();
        self.retire_primary_views();
        self.workspace.detail = None;
        self.workspace.detail_menu = false;
        if let Some(overview) = self.workspace.overview_primary.take() {
            let devices = overview.views.len();
            for view in &overview.views {
                view.borrow_mut()
                    .set_resource_limits(workspace::view_limits(
                        0,
                        devices,
                        self.settings.monitor_metrics.len(),
                    ));
            }
            let mut views = overview.views.into_iter();
            self.chart = views.next();
            self.comparison_chart = views.next();
            self.workspace.extra_views = views.collect();
            self.workspace.monitors = overview.monitors;
            self.workspace.targets[0] = overview.target.clone();
            self.target = overview.target;
            self.displayed = overview.displayed;
        }
        self.clear_analysis_interaction();
        self.workspace.motion.begin(std::time::Instant::now());
    }

    pub(super) fn save_detail_view(&mut self) {
        if let Some(metric) = self.workspace.detail
            && let Some(chart) = self.analysis_chart()
        {
            self.workspace.detail_viewports.insert(
                (metric, self.workspace.detail_histogram, self.logical),
                chart.borrow().viewport(),
            );
        } else if self.workspace.detail.is_none() {
            if let Some(chart) = self.analysis_chart() {
                self.workspace
                    .overview_viewports
                    .insert((11, self.logical), chart.borrow().viewport());
            }
            for (index, metric) in self
                .settings
                .monitor_metrics
                .iter()
                .enumerate()
                .take(workspace::MAX_MONITORS)
            {
                if let Some(view) = self
                    .visible_views(self.workspace.monitors[index].iter().cloned())
                    .first()
                {
                    self.workspace
                        .overview_viewports
                        .insert((*metric, self.logical), view.borrow().viewport());
                }
            }
        }
    }

    fn retire_plot_views(&mut self) {
        let analysis = self.workspace.analysis.take();
        let latest = self.workspace.latest.take();
        self.workspace.retire(&self.runtime.borrow());
        self.workspace.analysis = analysis;
        self.workspace.latest = latest;
        self.clear_primary_views();
    }

    fn retire_primary_views(&mut self) {
        self.runtime.borrow().worker.remove_view(0);
        self.workspace.targets[0] = None;
        self.clear_primary_views();
    }

    fn clear_primary_views(&mut self) {
        for view in [&mut self.chart, &mut self.comparison_chart] {
            if let Some(view) = view.take() {
                view.borrow_mut().close();
            }
        }
        for view in self.workspace.extra_views.drain(..) {
            view.borrow_mut().close();
        }
        self.displayed = None;
        self.target = None;
        self.query_request = None;
        self.selection_start = None;
    }

    pub(super) fn apply_detail_result(&mut self, result: Arc<ChartResult>) {
        let metric = self.workspace.detail.unwrap();
        let saved = self
            .workspace
            .detail_viewports
            .get(&(metric, self.workspace.detail_histogram, self.logical))
            .copied();
        let devices = result.datasets.len();
        let limits = workspace::view_limits(0, devices, self.settings.monitor_metrics.len());
        for side in 0..devices {
            let target = match side {
                0 => &mut self.chart,
                1 => &mut self.comparison_chart,
                _ => {
                    if self.workspace.extra_views.is_empty() {
                        let mut view =
                            ChartView::with_resource_limits(result.clone(), limits.clone());
                        if let Ok(view) = &mut view {
                            let _ = view.select_dataset(side);
                        }
                        match view {
                            Ok(view) => {
                                self.workspace.extra_views.push(Rc::new(RefCell::new(view)))
                            }
                            Err(error) => self.control_error = Some(error),
                        }
                    }
                    continue;
                }
            };
            if let Some(view) = target {
                let mut view = view.borrow_mut();
                if let Err(error) = view.set_result(result.clone()) {
                    self.control_error = Some(error);
                }
            } else {
                let view = (|| {
                    let mut view = ChartView::with_resource_limits(result.clone(), limits.clone())?;
                    if side > 0 {
                        view.select_dataset(side)?;
                    }
                    view.set_theme(self.settings.dark, self.settings.english);
                    view.enable_trajectory();
                    if let Some(viewport) = saved {
                        view.set_viewport(viewport, true);
                    }
                    Ok::<_, String>(Rc::new(RefCell::new(view)))
                })();
                match view {
                    Ok(view) => *target = Some(view),
                    Err(error) => self.control_error = Some(error),
                }
            }
        }
        for view in &self.workspace.extra_views {
            let mut view = view.borrow_mut();
            view.set_theme(self.settings.dark, self.settings.english);
            view.enable_trajectory();
            if let Err(error) = view.set_result(result.clone()) {
                self.control_error = Some(error);
            }
        }
    }

    pub(super) fn set_analysis_range(&mut self, start: f64, end: f64) {
        if !start.is_finite() || !end.is_finite() || start < 0.0 || end < start {
            self.workspace.range_error = Some(
                self.text(
                    "范围需要有效的非负起点和不小于起点的终点。",
                    "Use a non-negative start and an end at or after the start.",
                )
                .into(),
            );
            return;
        }
        if self
            .workspace
            .analysis
            .as_ref()
            .and_then(|analysis| analysis.range)
            == Some((start, end))
        {
            self.workspace.range_error = None;
            return;
        }
        if let Some(analysis) = &mut self.workspace.analysis {
            analysis.range = Some((start, end));
        }
        self.workspace.range_error = None;
        self.workspace.overview_viewports.clear();
        self.workspace.detail_viewports.clear();
        self.invalidate_analysis();
    }

    pub(super) fn invalidate_analysis(&mut self) {
        self.workspace.targets = Default::default();
        self.clear_analysis_interaction();
    }

    fn clear_analysis_interaction(&mut self) {
        self.query = None;
        self.query_request = None;
        self.workspace.link = None;
        self.workspace.selected_point = None;
        for view in self.plot_views() {
            view.borrow_mut().clear_link();
            view.borrow_mut().clear_hover();
        }
        self.workspace.file_epoch += 1;
    }

    pub(super) fn clear_analysis_range(&mut self) {
        if let Some(analysis) = &mut self.workspace.analysis {
            analysis.range = None;
        }
        self.workspace.range_error = None;
        self.workspace.detail_editor_context = None;
        self.workspace.overview_viewports.clear();
        self.workspace.detail_viewports.clear();
        for view in self.plot_views() {
            view.borrow_mut().fit_trajectory();
        }
        self.invalidate_analysis();
    }

    fn initialize_detail_editors(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.workspace.detail_editors.is_empty() {
            return;
        }
        for index in 0..2 {
            let field = cx.new(|cx| {
                let mut field = editor::TextInput::new(cx, "");
                field.kind = editor::InputKind::Decimal;
                field.compact = true;
                field
            });
            let focus = field.read(cx).focus_handle.clone();
            cx.on_blur(&focus, window, |host, window, cx| {
                if host.workspace.detail.is_some()
                    && !host
                        .workspace
                        .detail_editors
                        .iter()
                        .any(|field| field.read(cx).focus_handle.is_focused(window))
                {
                    host.commit_analysis_editors(cx);
                    cx.notify();
                }
            })
            .detach();
            cx.subscribe_in(
                &field,
                window,
                move |host: &mut Self, _, event, window, cx| {
                    match event {
                        editor::EditEvent::Submit => {
                            host.commit_analysis_editors(cx);
                            host.focus.focus(window);
                        }
                        editor::EditEvent::Cancel => {
                            let value = host
                                .workspace
                                .analysis
                                .as_ref()
                                .and_then(|analysis| analysis.range)
                                .map(|range| if index == 0 { range.0 } else { range.1 })
                                .map(|value| format!("{value:.3}"))
                                .unwrap_or_default();
                            host.workspace.detail_editors[index]
                                .update(cx, |field, cx| field.set_value(&value, cx));
                            host.focus.focus(window);
                        }
                        editor::EditEvent::Traverse(_) => {
                            host.workspace.detail_editors[1 - index]
                                .read(cx)
                                .focus_handle
                                .focus(window);
                        }
                    }
                    cx.stop_propagation();
                    cx.notify();
                },
            )
            .detach();
            self.workspace.detail_editors.push(field);
        }
    }

    fn commit_analysis_editors(&mut self, cx: &mut Context<Self>) {
        if self
            .workspace
            .detail_editors
            .iter()
            .all(|field| field.read(cx).content.is_empty())
        {
            if self
                .workspace
                .analysis
                .as_ref()
                .is_some_and(|a| a.range.is_some())
            {
                self.clear_analysis_range();
            }
            self.workspace.range_error = None;
            return;
        }
        let values: Vec<_> = self
            .workspace
            .detail_editors
            .iter()
            .enumerate()
            .map(|(index, field)| {
                let field = field.read(cx);
                if index == 0 && field.content.is_empty() {
                    Ok(0.0)
                } else {
                    field.content.parse::<f64>()
                }
            })
            .collect();
        if let [Ok(start), Ok(end)] = values.as_slice() {
            self.set_analysis_range(*start, *end);
        } else if self
            .workspace
            .detail_editors
            .iter()
            .any(|field| !field.read(cx).content.is_empty())
        {
            self.workspace.range_error = Some(
                self.text("请输入有效的时间范围。", "Enter a valid time range.")
                    .into(),
            );
        }
    }

    pub(super) fn render_plot_detail(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> gpui::Div {
        self.initialize_detail_editors(window, cx);
        let palette = design::Palette::for_theme(self.settings.dark);
        let range = self
            .workspace
            .analysis
            .as_ref()
            .and_then(|analysis| analysis.range);
        let editor_context = RangeEditorContext {
            source: self.import_identity,
            generation: self
                .workspace
                .analysis
                .as_ref()
                .map_or(0, |a| a.input.generation),
            logical: self.logical,
            range,
        };
        let sync_editors = self.workspace.detail_editor_context != Some(editor_context);
        self.workspace.detail_editor_context = Some(editor_context);
        let metric = self.workspace.detail.unwrap();
        let histogram = self.workspace.detail_histogram;
        let (_, chinese, english) = METRICS[metric];
        let options = self.detail_options(metric);
        let heading = div()
            .min_h(px(30.0))
            .flex_shrink_0()
            .flex()
            .items_center()
            .gap(px(10.0))
            .child(
                div()
                    .font_family(design::DISPLAY_FONT)
                    .text_size(px(20.0))
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .child(format!(
                        "{}{}",
                        self.text(chinese, english),
                        if histogram {
                            self.text("分布", " distribution")
                        } else {
                            ""
                        }
                    )),
            )
            .child(
                div()
                    .text_size(px(12.0))
                    .text_color(rgb(palette.muted))
                    .child(crate::chart::labels::Unit::metric(METRICS[metric].0).symbol()),
            );
        let mut display = div().flex().flex_col().gap(px(6.0));
        let mut toolbar = div()
            .flex()
            .items_center()
            .gap(px(8.0))
            .flex_wrap()
            .py(px(2.0));
        let mut basis = div().flex().items_center().gap(px(2.0));
        for (logical, label) in [
            (false, self.text("采集时间", "Capture time")),
            (true, self.text("会话时间", "Session time")),
        ] {
            basis = basis.child(
                compact_button(
                    format!("detail-basis-{logical}"),
                    label,
                    palette,
                    false,
                    !self.file_busy,
                )
                .when(self.logical == logical, |button| {
                    button
                        .bg(rgb(palette.selected))
                        .text_color(rgb(palette.accent_hover))
                })
                .on_click(cx.listener(move |host, _, _, cx| {
                    if host.logical != logical {
                        host.toggle_analysis_basis();
                        cx.notify();
                    }
                })),
            );
        }
        toolbar = toolbar.child(basis);
        for (index, label) in [(0, self.text("从", "From")), (1, self.text("至", "To"))] {
            let field = self.workspace.detail_editors[index].clone();
            let value = range
                .map(|range| if index == 0 { range.0 } else { range.1 })
                .map(|value| format!("{value:.3}"))
                .unwrap_or_default();
            field.update(cx, |field, cx| {
                field.palette = palette;
                field.set_placeholder(if index == 0 {
                    "0"
                } else if self.settings.english {
                    "End"
                } else {
                    "结束"
                });
                if sync_editors && field.content.as_ref() != value {
                    field.set_value(&value, cx);
                }
            });
            toolbar = toolbar.child(
                div()
                    .flex()
                    .items_center()
                    .gap(px(8.0))
                    .flex_shrink_0()
                    .text_size(px(12.0))
                    .text_color(rgb(palette.muted))
                    .child(label)
                    .child(div().w(px(88.0)).px(px(4.0)).child(field))
                    .child("ms"),
            );
        }
        let can_clear_range = self
            .workspace
            .analysis
            .as_ref()
            .is_some_and(|analysis| analysis.range.is_some())
            || self
                .workspace
                .detail_editors
                .iter()
                .any(|field| !field.read(cx).content.is_empty());
        toolbar = toolbar.child(
            compact_button(
                "detail-clear-range",
                self.text("重置范围", "Reset range"),
                palette,
                false,
                can_clear_range,
            )
            .on_click(cx.listener(move |host, _, _, cx| {
                if can_clear_range {
                    host.clear_analysis_range();
                    cx.notify();
                }
            })),
        );
        if !histogram && metric != 11 {
            for (id, chinese, english, selected) in [
                (0, "连线", "Lines", options.lines),
                (1, "采样线", "Stems", options.stems),
                (2, "趋势线", "Trend", options.trend),
                (3, "标记缺口", "Gaps", options.gaps),
            ] {
                if id == 2 && !matches!(metric, 0 | 1 | 5 | 17) {
                    continue;
                }
                display = display.child(
                    workspace::toggle_row(
                        format!("detail-option-{id}"),
                        self.text(chinese, english),
                        palette,
                        selected,
                        true,
                    )
                    .on_click(cx.listener(move |host, _, _, cx| {
                        host.toggle_detail_option(metric, id);
                        cx.notify();
                    })),
                );
            }
            if options.gaps
                && let Some(analysis) = &self.workspace.analysis
                && analysis.input.datasets.len() > 1
            {
                let keys: Vec<_> = analysis
                    .input
                    .datasets
                    .iter()
                    .map(|(key, _)| *key)
                    .filter(|key| !self.workspace.hidden_datasets.contains(key))
                    .collect();
                let selected = keys
                    .iter()
                    .position(|key| Some(*key) == options.gap_dataset)
                    .unwrap_or(0);
                display = display.child(
                    compact_button(
                        "detail-gap-target",
                        format!(
                            "{} {}",
                            self.text("缺口目标", "Gap target"),
                            ["A", "B", "C"][analysis
                                .input
                                .datasets
                                .iter()
                                .position(|(key, _)| Some(*key) == options.gap_dataset)
                                .unwrap_or(0)
                                .min(2)]
                        ),
                        palette,
                        false,
                        true,
                    )
                    .on_click(cx.listener(move |host, _, _, cx| {
                        let mut options = host.detail_options(metric);
                        options.gap_dataset = keys.get((selected + 1) % keys.len()).copied();
                        host.workspace
                            .detail_options
                            .insert((metric, false), options);
                        host.clear_analysis_interaction();
                        cx.notify();
                    })),
                );
            }
        } else if histogram {
            let spec = self
                .workspace
                .histogram_specs
                .get(&metric)
                .copied()
                .unwrap_or(clicksync_chart::HistogramSpec {
                    policy: clicksync_chart::HistogramPolicy::Automatic,
                    scale: clicksync_chart::HistogramScale::Percent,
                });
            let mut scales = div().flex().gap(px(4.0));
            for (percent, label) in [
                (true, self.text("占比 %", "Share %")),
                (false, self.text("样本数", "Samples")),
            ] {
                let scale = if percent {
                    clicksync_chart::HistogramScale::Percent
                } else {
                    clicksync_chart::HistogramScale::Count
                };
                scales = scales.child(
                    compact_button(
                        format!("histogram-scale-{percent}"),
                        label,
                        palette,
                        false,
                        true,
                    )
                    .flex_1()
                    .when(spec.scale == scale, |button| {
                        button
                            .bg(rgb(palette.selected))
                            .text_color(rgb(palette.accent_hover))
                    })
                    .on_click(cx.listener(move |host, _, _, cx| {
                        if spec.scale != scale {
                            host.workspace
                                .histogram_specs
                                .insert(metric, clicksync_chart::HistogramSpec { scale, ..spec });
                            host.clear_analysis_interaction();
                            cx.notify();
                        }
                    })),
                );
            }
            display = display.child(scales);
            let ready = self
                .analysis_chart()
                .and_then(|chart| chart.borrow().presented_result())
                .is_some();
            display = display.child(
                workspace::toggle_row(
                    "histogram-policy",
                    self.text("锁定分箱", "Lock bins"),
                    palette,
                    matches!(spec.policy, clicksync_chart::HistogramPolicy::Fixed(_)),
                    ready,
                )
                .on_click(cx.listener(move |host, _, _, cx| {
                    if ready {
                        host.toggle_histogram_policy(metric);
                        cx.notify();
                    }
                })),
            );
        }
        let can_fit = !self.lock.locked()
            && self.runtime.borrow().pending_action().is_none()
            && self.analysis_chart().is_some();
        display = display.child(
            compact_button(
                "detail-fit",
                self.text("全局视图", "Global view"),
                palette,
                false,
                can_fit,
            )
            .justify_start()
            .px(px(8.0))
            .on_click(cx.listener(move |host, _, _, cx| {
                if can_fit && let Some(chart) = host.analysis_chart() {
                    chart.borrow_mut().fit_trajectory();
                    host.workspace
                        .interaction
                        .as_ref()
                        .unwrap()
                        .notify_plots(cx);
                }
            })),
        );
        if metric == 5 && !histogram {
            display = display.child(
                compact_button(
                    "detail-focus-values",
                    self.text("主体视图", "Focus view"),
                    palette,
                    false,
                    can_fit,
                )
                .on_click(cx.listener(move |host, _, _, cx| {
                    if can_fit && let Some(chart) = host.analysis_chart() {
                        chart.borrow_mut().fit_value_axis();
                        host.workspace
                            .interaction
                            .as_ref()
                            .unwrap()
                            .notify_plots(cx);
                    }
                })),
            );
        }
        let mut chart = self
            .workspace_chart_canvas(0, cx)
            .id("detail-plot")
            .on_mouse_down(
                gpui::MouseButton::Left,
                cx.listener(|host, event: &gpui::MouseDownEvent, _, cx| {
                    if let Some(chart) = host.analysis_chart() {
                        if event.modifiers.shift {
                            host.selection_start =
                                chart.borrow_mut().begin_selection(event.position);
                        } else if event.click_count == 2 {
                            chart.borrow_mut().fit_trajectory();
                        } else {
                            chart.borrow_mut().drag_start(event.position);
                        }
                        cx.notify();
                    }
                }),
            )
            .on_mouse_move(cx.listener(|host, event: &gpui::MouseMoveEvent, _, cx| {
                if let Some(chart) = host.analysis_chart() {
                    if event.pressed_button != Some(gpui::MouseButton::Left) {
                        chart.borrow_mut().drag_end();
                    }
                    host.queue_analysis_pointer(chart, event.position, cx);
                }
            }))
            .on_mouse_up(
                gpui::MouseButton::Left,
                cx.listener(|host, _, _, cx| {
                    host.finish_analysis_selection();
                    cx.notify();
                }),
            )
            .on_mouse_up_out(
                gpui::MouseButton::Left,
                cx.listener(|host, _, _, cx| {
                    host.finish_analysis_selection();
                    cx.notify();
                }),
            )
            .on_scroll_wheel(cx.listener(|host, event: &gpui::ScrollWheelEvent, _, cx| {
                if let Some(chart) = host.analysis_chart() {
                    chart.borrow_mut().scroll_zoom(event);
                    cx.stop_propagation();
                    host.workspace
                        .interaction
                        .as_ref()
                        .unwrap()
                        .notify_plots(cx);
                }
            }));
        chart = chart.when(self.chart.is_none(), |chart| {
            chart.child(
                div()
                    .absolute()
                    .inset_0()
                    .flex()
                    .items_center()
                    .justify_center()
                    .text_color(rgb(palette.muted))
                    .child(self.text("正在准备图表…", "Preparing plot…")),
            )
        });
        let inspector = div()
            .id("detail-inspector")
            .w(px(232.0)).flex_shrink_0().h_full().overflow_y_scroll()
            .flex().flex_col().gap(px(22.0)).pl(px(20.0)).pt(px(8.0))
            .border_l_1().border_color(rgb(palette.hairline))
            .child(self.detail_statistics(metric, palette))
            .child(div().border_t_1().border_color(rgb(palette.hairline)).pt(px(14.0))
                .child(div().mb(px(10.0)).font_weight(gpui::FontWeight::MEDIUM).child(self.text("图层与视野", "Layers & view")))
                .child(display))
            .child(div().text_size(px(12.0)).line_height(px(22.0)).text_color(rgb(palette.muted))
                .child(if histogram { self.text("拖动平移 · 滚轮缩放\n双击查看全局\nEsc 返回总览", "Drag to pan · Scroll to zoom\nDouble-click for global view\nEsc to overview") } else { self.text("拖动平移 · 滚轮缩放\nShift 拖动选择范围\n双击查看全局\nEsc 返回总览", "Drag to pan · Scroll to zoom\nShift-drag to select a range\nDouble-click for global view\nEsc to overview") }));
        div()
            .flex_1()
            .min_h_0()
            .flex()
            .flex_col()
            .gap(px(4.0))
            .child(
                div()
                    .flex()
                    .flex_shrink_0()
                    .items_start()
                    .gap_x(px(24.0))
                    .child(heading)
                    .child(self.analysis_legend(metric, cx).flex_1().min_w_0()),
            )
            .child(
                div()
                    .min_h(px(40.0))
                    .flex_shrink_0()
                    .flex()
                    .items_center()
                    .justify_between()
                    .gap(px(20.0))
                    .child(toolbar.flex_1().min_w_0())
                    .child(self.workspace_inspection_readout(cx)),
            )
            .when_some(self.workspace.range_error.clone(), |row, message| {
                row.child(
                    div()
                        .text_size(px(13.0))
                        .text_color(rgb(0xb04758))
                        .child(message),
                )
            })
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .flex()
                    .gap(px(18.0))
                    .child(chart)
                    .child(inspector),
            )
    }

    fn detail_statistics(&self, metric: usize, palette: design::Palette) -> gpui::Div {
        let group = self.workspace.export_group;
        let result = self
            .analysis_chart()
            .and_then(|chart| chart.borrow().presented_result());
        let dataset = result
            .as_ref()
            .filter(|result| {
                // Style and bin changes do not change the statistics of the fixed input.
                let mut parameters =
                    self.settings
                        .parameters(metric, false, self.logical, self.parameter_version);
                parameters.histogram = result.request.parameters.histogram;
                result.request.source_version == self.import_identity
                    && result.request.parameters == parameters
                    && result.request.secondary_metric
                        == crate::product::secondary_metric(metric, self.workspace.detail_histogram)
                    && self.workspace.analysis.as_ref().is_some_and(|analysis| {
                        Arc::ptr_eq(&result.request.prefix, &analysis.input)
                            && result.request.analysis.as_ref().and_then(|a| a.range)
                                == analysis.range
                            && result
                                .request
                                .dataset_cpi
                                .iter()
                                .enumerate()
                                .all(|(side, (key, cpi))| *cpi == self.performance_cpi(*key, side))
                    })
            })
            .and_then(|result| result.datasets.get(group));
        let distribution = dataset.and_then(|dataset| dataset.distribution.as_ref());
        let mut panel = div()
            .flex()
            .flex_col()
            .gap(px(12.0))
            .child(div().font_weight(gpui::FontWeight::SEMIBOLD).child(format!(
                "{}  {}",
                self.text("统计", "Statistics"),
                ["A", "B", "C"][group.min(2)]
            )))
            .child(
                div()
                    .h(px(22.0))
                    .text_size(px(12.0))
                    .text_color(rgb(palette.muted))
                    .child(if dataset.is_none() {
                        self.text("正在计算…", "Calculating…")
                    } else if distribution.is_none() {
                        self.text("此范围暂无有效样本", "No valid samples in this range")
                    } else if self
                        .workspace
                        .analysis
                        .as_ref()
                        .is_some_and(|a| a.range.is_some())
                    {
                        self.text("所选时间范围", "Selected time range")
                    } else {
                        self.text("全部采集数据", "Full capture")
                    }),
            );
        let unit = crate::chart::labels::Unit::metric(METRICS[metric].0);
        let value = |v| {
            format!(
                "{} {}",
                unit.readout(clicksync_chart::Coordinate::Scalar(v)),
                unit.symbol()
            )
        };
        for (label, number) in [
            (
                self.text("样本数", "Samples"),
                distribution.map(|d| d.sample_count.to_string()),
            ),
            (
                self.text("均值", "Mean"),
                distribution.map(|d| value(d.average)),
            ),
            (
                self.text("标准差", "Std. deviation"),
                distribution.map(|d| value(d.standard_deviation)),
            ),
            (
                self.text("中位数", "Median"),
                distribution.map(|d| value(d.p50)),
            ),
            ("P95", distribution.map(|d| value(d.p95))),
        ] {
            panel = panel.child(
                div()
                    .h(px(22.0))
                    .flex()
                    .items_center()
                    .justify_between()
                    .gap(px(8.0))
                    .child(div().text_color(rgb(palette.muted)).child(label))
                    .child(
                        div()
                            .font_family(design::DISPLAY_FONT)
                            .font_weight(gpui::FontWeight::MEDIUM)
                            .child(number.unwrap_or_else(|| "—".into())),
                    ),
            );
        }
        panel
    }

    pub(super) fn analysis_header(&self, metric: usize, cx: &mut Context<Self>) -> gpui::Div {
        div()
            .min_h(px(44.0))
            .flex_shrink_0()
            .flex()
            .items_center()
            .justify_between()
            .gap(px(20.0))
            .child(self.analysis_legend(metric, cx).flex_1().min_w_0())
            .child(self.workspace_inspection_readout(cx))
    }

    fn analysis_legend(&self, metric: usize, cx: &mut Context<Self>) -> gpui::Div {
        let palette = design::Palette::for_theme(self.settings.dark);
        let prefix = self
            .workspace
            .analysis
            .as_ref()
            .map(|analysis| &analysis.input)
            .or(self.workspace.latest.as_ref())
            .filter(|prefix| !prefix.datasets.is_empty());
        let datasets: Vec<_> = if let Some(prefix) = prefix {
            prefix
                .datasets
                .iter()
                .map(|(key, snapshot)| (*key, snapshot.metadata().display_name.clone()))
                .collect()
        } else {
            let runtime = self.runtime.borrow();
            self.selected
                .iter()
                .map(|key| {
                    (
                        *key,
                        runtime.device_catalog.label(*key).unwrap_or("—").to_owned(),
                    )
                })
                .collect()
        };
        let visible_count = datasets
            .iter()
            .filter(|(key, _)| !self.workspace.hidden_datasets.contains(key))
            .count();
        let can_inspect = self.workspace.analysis.is_some()
            && !self.lock.locked()
            && self.runtime.borrow().pending_action().is_none();
        let mut row = div()
            .flex()
            .flex_wrap()
            .items_center()
            .gap_x(px(16.0))
            .gap_y(px(4.0))
            .text_size(px(13.0));
        for (side, (key, name)) in datasets.iter().enumerate() {
            let key = *key;
            let visible = !self.workspace.hidden_datasets.contains(&key);
            let can_toggle = can_inspect && (!visible || visible_count > 1);
            let color = crate::chart_view::dataset_color(self.settings.dark, side);
            row = row.child(
                div()
                    .id(("analysis-legend", key))
                    .max_w_full()
                    .flex_shrink_0()
                    .min_h(px(30.0))
                    .rounded(px(6.0))
                    .flex()
                    .items_center()
                    .gap(px(8.0))
                    .when(can_toggle, |item| {
                        item.cursor_pointer()
                            .focusable()
                            .hover(move |style| style.bg(rgb(palette.hover)))
                            .active(move |style| style.bg(rgb(palette.selected)))
                            .focus(move |style| style.bg(rgb(palette.selected)))
                    })
                    .on_click(cx.listener(move |host, _, _, cx| {
                        if can_toggle {
                            host.toggle_analysis_dataset(key);
                            cx.notify();
                        }
                    }))
                    .child(div().w(px(14.0)).h(px(2.0)).flex_shrink_0().bg(rgb(color)))
                    .child(["A", "B", "C"][side.min(2)])
                    .child(div().min_w_0().child(name.clone()))
                    .child(
                        div()
                            .flex_shrink_0()
                            .text_size(px(12.0))
                            .text_color(rgb(palette.muted))
                            .child(format!(
                                "{} DPI",
                                self.performance_cpi(key, side)
                                    .map_or_else(|| "—".into(), |value| format!("{value:.0}"))
                            )),
                    )
                    .when(
                        crate::product::secondary_metric(metric, self.workspace.detail_histogram)
                            .is_some(),
                        |item| {
                            item.child(
                                div()
                                    .flex_shrink_0()
                                    .text_size(px(12.0))
                                    .text_color(rgb(palette.muted))
                                    .child("X"),
                            )
                            .child(
                                div()
                                    .w(px(14.0))
                                    .h(px(2.0))
                                    .flex_shrink_0()
                                    .bg(rgb([0x55adcf, 0xe6b850, 0x9abbae][side.min(2)])),
                            )
                            .child(
                                div()
                                    .text_size(px(12.0))
                                    .text_color(rgb(palette.muted))
                                    .child("Y"),
                            )
                        },
                    )
                    .when(datasets.len() > 1, |item| {
                        item.child(
                            workspace::toggle_indicator(visible, palette)
                                .when(!can_toggle, |switch| switch.opacity(0.4)),
                        )
                    })
                    .when(!visible, |item| item.text_color(rgb(palette.muted))),
            );
        }
        row
    }

    pub(super) fn toggle_histogram_policy(&mut self, metric: usize) {
        let Some(result) = self
            .analysis_chart()
            .and_then(|chart| chart.borrow().presented_result())
        else {
            return;
        };
        let Some(mut spec) = self
            .workspace
            .histogram_specs
            .get(&metric)
            .copied()
            .or(result.request.parameters.histogram)
        else {
            return;
        };
        if matches!(spec.policy, clicksync_chart::HistogramPolicy::Fixed(_)) {
            spec.policy = clicksync_chart::HistogramPolicy::Automatic;
        } else {
            let bins = &result.datasets[0].histogram;
            let (Some(first), Some(last)) = (bins.first(), bins.last()) else {
                return;
            };
            spec.policy =
                clicksync_chart::HistogramPolicy::Fixed(clicksync_chart::HistogramLayout {
                    minimum: first.minimum,
                    maximum: last.maximum,
                    width: (last.maximum - first.minimum) / bins.len() as f64,
                    bins: bins.len(),
                });
        }
        self.workspace.histogram_specs.insert(metric, spec);
        self.clear_analysis_interaction();
    }

    pub(super) fn toggle_detail_option(&mut self, metric: usize, id: usize) {
        let mut options = self.detail_options(metric);
        match id {
            0 => options.lines = !options.lines,
            1 => options.stems = !options.stems,
            2 => options.trend = !options.trend,
            _ => options.gaps = !options.gaps,
        }
        self.workspace
            .detail_options
            .insert((metric, self.workspace.detail_histogram), options);
        // Request comparison will invalidate only this metric, not the other view slots.
        self.clear_analysis_interaction();
    }

    pub(super) fn analysis_menu(&self, cx: &mut Context<Self>) -> gpui::Stateful<gpui::Div> {
        let palette = design::Palette::for_theme(self.settings.dark);
        let mut items = div().flex().flex_col().gap(px(12.0));
        for (zh, en, metrics, histogram) in [
            (
                "移动与速度",
                "Motion & speed",
                &[11, 16, 13, 14, 19][..],
                false,
            ),
            (
                "采样与计数",
                "Timing & counts",
                &[5, 8, 0, 1, 17, 9, 10, 18][..],
                false,
            ),
            ("分布", "Distribution", &[5, 0, 1, 2][..], true),
            (
                "其他指标",
                "Other metrics",
                &[6, 7, 3, 4, 2, 12, 15][..],
                false,
            ),
        ] {
            let mut choices = div().flex().flex_wrap().gap(px(4.0));
            for &metric in metrics {
                let (_, chinese, english) = METRICS[metric];
                let selected = self.workspace.detail == Some(metric)
                    && self.workspace.detail_histogram == histogram;
                choices = choices.child(
                    compact_button(
                        format!(
                            "{}-{metric}",
                            if histogram {
                                "detail-histogram"
                            } else if [6, 7, 3, 4, 2, 12, 15].contains(&metric) {
                                "detail-extra"
                            } else {
                                "detail-type"
                            }
                        ),
                        self.text(chinese, english),
                        palette,
                        false,
                        true,
                    )
                    .w(px(154.0))
                    .justify_start()
                    .bg(rgb(if selected {
                        palette.selected
                    } else {
                        palette.panel
                    }))
                    .when(selected, |item| item.text_color(rgb(palette.accent_hover)))
                    .on_click(cx.listener(move |host, _, _, cx| {
                        host.open_plot_detail(metric, histogram);
                        cx.stop_propagation();
                        cx.notify();
                    })),
                );
            }
            items = items.child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(6.0))
                    .child(
                        div()
                            .px(px(10.0))
                            .text_size(px(12.0))
                            .font_weight(gpui::FontWeight::MEDIUM)
                            .text_color(rgb(palette.muted))
                            .child(self.text(zh, en)),
                    )
                    .child(choices),
            );
        }
        div()
            .absolute()
            .top(px(42.0))
            .left_0()
            .rounded(px(12.0))
            .shadow_lg()
            .w(px(340.0))
            .max_h(px(640.0))
            .id("detail-types")
            .overflow_y_scroll()
            .occlude()
            .p(px(8.0))
            .bg(rgb(palette.panel))
            .border_1()
            .border_color(rgb(palette.hairline))
            .on_mouse_down_out(cx.listener(|host, _, _, cx| {
                host.workspace.detail_menu = false;
                cx.notify();
            }))
            .child(items)
    }

    pub(super) fn analysis_data_menu(&self, cx: &mut Context<Self>) -> gpui::Stateful<gpui::Div> {
        let palette = design::Palette::for_theme(self.settings.dark);
        let mut items = div().flex().flex_col().gap(px(4.0));
        for (action, chinese, english) in [
            ("import", "导入数据 JSON…", "Import data JSON…"),
            ("compare", "导入比较数据 JSON…", "Import comparison JSON…"),
            ("export", "导出完整数据 JSON…", "Export full data JSON…"),
        ] {
            if self.workspace.detail.is_some() && action == "import" {
                continue;
            }
            let enabled = !self.file_busy
                && (action != "export"
                    || self.workspace.analysis.as_ref().is_some_and(|analysis| {
                        analysis
                            .input
                            .datasets
                            .get(self.workspace.export_group)
                            .is_some_and(|(_, snapshot)| snapshot.event_count() > 0)
                    }))
                && (action != "compare" || self.workspace.analysis.is_some());
            items = items.child(
                compact_button(
                    format!("dataset-{action}"),
                    self.text(chinese, english),
                    palette,
                    false,
                    enabled,
                )
                .justify_start()
                .bg(rgb(palette.panel))
                .on_click(cx.listener(move |host, _, _, cx| {
                    if enabled {
                        host.prompt_analysis_file(action, cx);
                    }
                    cx.notify();
                })),
            );
        }
        if self.workspace.analysis.is_some() {
            items = items.child(div().px(px(12.0)).py(px(6.0)).text_size(px(12.0))
                .text_color(rgb(palette.muted))
                .child(self.text("导出所选数据组的全部记录，不受时间范围限制。",
                    "Exports all records of the selected dataset, independent of the time range.")));
        }
        if let Some(analysis) = &self.workspace.analysis {
            items = items.child(
                div()
                    .mt(px(8.0))
                    .px(px(10.0))
                    .py(px(8.0))
                    .border_t_1()
                    .border_color(rgb(palette.hairline))
                    .text_size(px(12.0))
                    .text_color(rgb(palette.muted))
                    .child(self.text("统计与导出使用的数据组", "Dataset for statistics & export")),
            );
            for (index, (key, snapshot)) in analysis.input.datasets.iter().enumerate() {
                let cpi = self
                    .performance_cpi(*key, index)
                    .map_or_else(|| "—".into(), |value| format!("{value:.0}"));
                let name = format!(
                    "{} · {} · {cpi} DPI",
                    ["A", "B", "C"][index.min(2)],
                    snapshot.metadata().display_name
                );
                let removable = self.workspace.groups.iter().any(|group| group.key == *key);
                let key = *key;
                items = items.child(
                    div()
                        .flex()
                        .items_center()
                        .justify_between()
                        .child(
                            compact_button(
                                format!("dataset-select-{index}"),
                                name,
                                palette,
                                self.workspace.export_group == index,
                                true,
                            )
                            .flex_1()
                            .min_w_0()
                            .h_auto()
                            .min_h(px(40.0))
                            .py(px(8.0))
                            .justify_start()
                            .bg(rgb(if self.workspace.export_group == index {
                                palette.selected
                            } else {
                                palette.panel
                            }))
                            .text_color(rgb(palette.text))
                            .on_click(cx.listener(
                                move |host, _, _, cx| {
                                    host.workspace.export_group = index;
                                    cx.notify();
                                },
                            )),
                        )
                        .when(removable, |row| {
                            row.child(
                                compact_button(
                                    format!("dataset-remove-{key}"),
                                    self.text("移除", "Remove"),
                                    palette,
                                    false,
                                    !self.file_busy,
                                )
                                .on_click(cx.listener(
                                    move |host, _, _, cx| {
                                        if !host.file_busy {
                                            host.remove_comparison_group(key);
                                        }
                                        cx.notify();
                                    },
                                )),
                            )
                        }),
                );
            }
        }
        if self.workspace.staged_group.is_some() {
            items = items
                .child(
                    div()
                        .px(px(8.0))
                        .py(px(6.0))
                        .text_size(px(12.0))
                        .child(self.text(
                            "比较组已满。替换最早导入的一组？",
                            "Comparison slots are full. Replace the oldest import?",
                        )),
                )
                .child(
                    div()
                        .flex()
                        .gap(px(8.0))
                        .child(
                            compact_button(
                                "comparison-replace",
                                self.text("替换", "Replace"),
                                palette,
                                true,
                                true,
                            )
                            .on_click(cx.listener(|host, _, _, cx| {
                                host.finish_comparison_replace(true);
                                cx.notify();
                            })),
                        )
                        .child(
                            compact_button(
                                "comparison-cancel",
                                self.text("取消", "Cancel"),
                                palette,
                                false,
                                true,
                            )
                            .on_click(cx.listener(|host, _, _, cx| {
                                host.finish_comparison_replace(false);
                                cx.notify();
                            })),
                        ),
                );
        }
        div()
            .id("analysis-data-menu")
            .absolute()
            .top(px(42.0))
            .left_0()
            .rounded(px(12.0))
            .shadow_lg()
            .w(px(372.0))
            .max_h(px(580.0))
            .overflow_y_scroll()
            .p(px(8.0))
            .bg(rgb(palette.panel))
            .border_1()
            .border_color(rgb(palette.hairline))
            .occlude()
            .on_mouse_down_out(cx.listener(|host, _, _, cx| {
                host.workspace.data_menu = false;
                cx.notify();
            }))
            .child(items)
    }

    pub(super) fn submit_analysis_file(
        &mut self,
        request: FileRequest,
        destination: Destination,
        presented: Option<u64>,
    ) {
        let operation = match &request {
            FileRequest::ImportAnalysis { operation, .. }
            | FileRequest::ConvertRange { operation, .. } => *operation,
            _ => self.next_request,
        };
        self.next_request += 1;
        match self
            .runtime
            .borrow_mut()
            .submit_file(self.receiver, request)
        {
            Ok(()) => {
                self.file_busy = true;
                self.workspace.file_context = Some(FileContext {
                    operation,
                    epoch: self.workspace.file_epoch,
                    source: self.import_identity,
                    request: presented,
                    destination,
                });
            }
            Err(error) => self.control_error = Some(error),
        }
    }

    pub(super) fn prompt_analysis_file(&mut self, action: &'static str, cx: &mut Context<Self>) {
        if self.file_busy
            || self.lock.locked()
            || self.runtime.borrow().pending_action().is_some()
            || (action == "import" && self.workspace.detail.is_some())
            || (action == "compare" && self.workspace.analysis.is_none())
            || !matches!(action, "import" | "compare" | "export")
        {
            return;
        }
        self.workspace.data_menu = false;
        self.file_message = None;
        let operation = self.next_request;
        let epoch = self.workspace.file_epoch;
        let source = self.import_identity;
        if matches!(action, "import" | "compare") {
            let budget = self
                .runtime
                .borrow()
                .backend
                .as_ref()
                .map(|backend| backend.byte_budget())
                .unwrap_or_else(|| {
                    clicksync_core::SharedByteBudget::new(
                        clicksync_core::SessionConfig::default().managed_memory_budget_bytes,
                    )
                });
            let existing = if action == "compare" {
                self.workspace
                    .analysis
                    .as_ref()
                    .map_or_else(Vec::new, |analysis| {
                        analysis
                            .input
                            .datasets
                            .iter()
                            .enumerate()
                            .map(|(side, (key, snapshot))| {
                                (snapshot.clone(), self.performance_cpi(*key, side))
                            })
                            .collect()
                    })
            } else {
                Vec::new()
            };
            let destination = if action == "import" {
                Destination::Primary
            } else {
                Destination::Comparison
            };
            let chosen = cx.prompt_for_paths(gpui::PathPromptOptions {
                files: true,
                directories: false,
                multiple: false,
                prompt: Some("ClickSync JSON".into()),
            });
            self.file_busy = true;
            cx.spawn(async move |host, cx| {
                let answer = chosen.await;
                let _ = host.update(cx, |host, cx| {
                    host.file_busy = false;
                    cx.notify();
                    if host.workspace.file_epoch != epoch || host.import_identity != source {
                        return;
                    }
                    match answer {
                        Ok(Ok(Some(paths))) if !paths.is_empty() => host.submit_analysis_file(
                            FileRequest::ImportAnalysis {
                                path: paths[0].clone(),
                                operation,
                                budget: budget.clone(),
                                existing: existing.clone(),
                            },
                            destination,
                            None,
                        ),
                        Ok(Err(error)) => host.control_error = Some(error.to_string()),
                        _ => {}
                    }
                    cx.notify();
                });
            })
            .detach();
            return;
        }
        let Some(analysis) = &self.workspace.analysis else {
            return;
        };
        let Some((key, snapshot)) = analysis.input.datasets.get(self.workspace.export_group) else {
            return;
        };
        // Export the selected fixed input, never a viewport or filtered chart payload.
        let snapshot = snapshot.clone();
        let cpi = self.performance_cpi(*key, self.workspace.export_group);
        let directory = std::env::current_dir().unwrap_or_else(|_| std::env::temp_dir());
        let filename = default_export_filename(&snapshot.metadata().display_name);
        let chosen = cx.prompt_for_new_path(&directory, Some(&filename));
        self.file_busy = true;
        cx.spawn(async move |host, cx| {
            let answer = chosen.await;
            let _ = host.update(cx, |host, cx| {
                host.file_busy = false;
                match answer {
                    Ok(Ok(Some(path))) => {
                        host.submit_analysis_file(
                            FileRequest::Export {
                                path,
                                snapshot: snapshot.clone(),
                                cpi,
                            },
                            Destination::Output,
                            None,
                        );
                        if let Some(context) = &mut host.workspace.file_context {
                            context.epoch = epoch;
                            context.source = source;
                        }
                    }
                    Ok(Err(error))
                        if host.workspace.file_epoch == epoch && host.import_identity == source =>
                    {
                        host.control_error = Some(error.to_string())
                    }
                    _ => {}
                }
                cx.notify();
            });
        })
        .detach();
    }

    pub(super) fn take_analysis_file_result(
        &mut self,
        result: &Result<FileResult, String>,
    ) -> bool {
        let Some(context) = self.workspace.file_context.take() else {
            return false;
        };
        if context.epoch != self.workspace.file_epoch || context.source != self.import_identity {
            return true;
        }
        if context.destination == Destination::Query
            && context
                .request
                .is_none_or(|request| !self.analysis_request_is_current(request))
        {
            self.query_request = None;
            return true;
        }
        match result {
            Ok(FileResult::ImportedAnalysis { group, operation })
                if *operation == context.operation =>
            {
                if context.destination == Destination::Primary {
                    self.imported = Some((group.snapshot.clone(), group.cpi));
                    self.workspace.export_group = 0;
                    self.workspace.groups.clear();
                    self.workspace.staged_group = None;
                    self.comparison = None;
                    self.workspace.analysis = None;
                    self.adopt_analysis_input();
                } else {
                    self.add_comparison_group(group.clone());
                }
            }
            Ok(FileResult::Query(query)) => {
                if context.request == Some(query.request_id)
                    && self.analysis_request_is_current(query.request_id)
                {
                    self.query = Some(query.clone());
                }
                self.query_request = None;
            }
            Ok(FileResult::Saved(path)) => {
                self.file_message = Some(format!(
                    "{} · {}",
                    self.text("已导出", "Exported"),
                    path.display()
                ))
            }
            Ok(FileResult::Duplicate { operation }) if *operation == context.operation => {
                self.file_message = Some(
                    self.text("该数据组已存在。", "This dataset is already present.")
                        .into(),
                );
            }
            Ok(FileResult::ConvertedRange { operation, range })
                if *operation == context.operation =>
            {
                if let Some((start, end)) = range {
                    self.logical = !self.logical;
                    self.set_analysis_range(*start, *end);
                } else {
                    self.workspace.range_error = Some(self.text(
                        "这个范围在另一时间基准中无法保持同一组报告。请清除范围后切换。",
                        "This range cannot keep the same reports in the other time basis. Clear the range first.").into());
                }
            }
            Err(error) => self.control_error = Some(error.clone()),
            _ => {}
        }
        true
    }

    fn adopt_analysis_input(&mut self) {
        let fixed = self
            .workspace
            .analysis
            .as_ref()
            .map(|analysis| (analysis.input.clone(), analysis.range));
        self.save_detail_view();
        self.retire_plot_views();
        self.workspace.analysis = None;
        self.workspace.file_epoch += 1;
        self.import_identity += 1;
        self.workspace.detail_viewports.clear();
        self.workspace.link = None;
        self.workspace.overview_viewports.clear();
        self.workspace.selected_point = None;
        self.workspace.hidden_datasets.retain(|key| {
            self.workspace.groups.iter().any(|group| group.key == *key)
                || self.selected.contains(key)
        });
        self.query = None;
        if let Some((input, range)) = fixed {
            let mut datasets = if let Some((snapshot, _)) = &self.imported {
                vec![(u32::MAX - 1, snapshot.clone())]
            } else {
                input
                    .datasets
                    .iter()
                    .filter(|(key, _)| self.selected.contains(key))
                    .cloned()
                    .collect()
            };
            datasets.extend(
                self.workspace
                    .groups
                    .iter()
                    .map(|group| (group.key, group.snapshot.clone())),
            );
            self.workspace.analysis = Some(workspace::AnalysisContext {
                input: Arc::new(crate::backend::SessionPrefix {
                    datasets,
                    ..(*input).clone()
                }),
                range,
            });
        }
    }

    fn analysis_request_is_current(&self, request: u64) -> bool {
        self.plot_views().iter().any(|chart| {
            chart.borrow().presented_result().is_some_and(|result| {
                result.request.id == request
                    && self
                        .workspace
                        .targets
                        .iter()
                        .flatten()
                        .any(|target| result.eligible_for(target))
            })
        })
    }

    fn toggle_analysis_basis(&mut self) {
        if self.file_busy {
            return;
        }
        if let Some(analysis) = &self.workspace.analysis
            && let Some(range) = analysis.range
        {
            let snapshots = analysis
                .input
                .datasets
                .iter()
                .map(|(_, snapshot)| snapshot.clone())
                .collect();
            self.submit_analysis_file(
                FileRequest::ConvertRange {
                    snapshots,
                    range,
                    logical: self.logical,
                    operation: self.next_request,
                },
                Destination::Basis,
                None,
            );
        } else {
            self.save_detail_view();
            self.retire_plot_views();
            self.logical = !self.logical;
            self.workspace.targets = Default::default();
            self.workspace.file_epoch += 1;
            self.workspace.link = None;
            self.query = None;
        }
    }

    pub(super) fn add_comparison_group(&mut self, mut group: Group) {
        // The FileWorker checks full input equality before this UI handoff.
        let limit = if self.workspace.dual && self.imported.is_none() {
            1
        } else {
            2
        };
        if self.workspace.groups.len() == limit {
            self.workspace.staged_group = Some(group);
            self.workspace.data_menu = true;
            return;
        }
        self.workspace.next_group_key = self.workspace.next_group_key.max(1000) + 1;
        group.key = u32::MAX - self.workspace.next_group_key;
        self.workspace.groups.push(group);
        self.adopt_analysis_input();
    }

    pub(super) fn finish_comparison_replace(&mut self, replace: bool) {
        if let Some(group) = self.workspace.staged_group.take()
            && replace
        {
            if !self.workspace.groups.is_empty() {
                self.workspace.groups.remove(0);
            }
            self.add_comparison_group(group);
        }
    }

    pub(super) fn remove_comparison_group(&mut self, key: u32) {
        self.workspace.groups.retain(|group| group.key != key);
        self.workspace.export_group = 0;
        self.adopt_analysis_input();
    }

    pub(super) fn update_analysis_hover_for(&mut self, view: Rc<RefCell<ChartView>>) -> bool {
        if self.workspace.analysis.is_none() {
            return false;
        }
        let report = view
            .borrow()
            .hit()
            .map(|(result, key, index)| (result, key, index, None));
        let report = report.or_else(|| {
            view.borrow()
                .histogram_hit()
                .map(|(result, key, bin)| (result, key, bin.minimum.to_bits() as usize, Some(bin)))
        });
        let Some((result, key, index, bin)) = report else {
            let changed = self.workspace.selected_point.is_some() || self.workspace.link.is_some();
            self.workspace.selected_point = None;
            self.workspace.link = None;
            for view in self.plot_views() {
                view.borrow_mut().clear_link();
                view.borrow_mut().clear_hover();
            }
            return changed;
        };
        if self.workspace.selected_point == Some((result.request.id, key, index)) {
            return false;
        }
        let current = |result: &ChartResult| {
            matches!(
                result.request.prefix.phase,
                crate::backend::CapturePhase::Paused
                    | crate::backend::CapturePhase::Stopped
                    | crate::backend::CapturePhase::Faulted
            ) && self
                .workspace
                .targets
                .iter()
                .flatten()
                .any(|target| result.eligible_for(target))
        };
        if !current(&result) {
            return false;
        }
        self.workspace.selected_point = Some((result.request.id, key, index));
        let mut charts = Vec::new();
        let views = self.plot_views();
        for view in &views {
            if let Some(presented) = view.borrow().presented_result()
                && current(&presented)
                && charts
                    .iter()
                    .all(|chart: &Arc<ChartResult>| chart.request.id != presented.request.id)
            {
                charts.push(presented);
            }
        }
        for view in &views {
            view.borrow_mut().clear_link();
        }
        for other in &views {
            if !Rc::ptr_eq(other, &view) {
                other.borrow_mut().clear_hover();
            }
        }
        self.workspace.link = None;
        match result.linked(key, index, &charts, bin) {
            Ok(link) => {
                for view in &views {
                    let mut view = view.borrow_mut();
                    if let Some(presented) = view.presented_result() {
                        for (request, key, points) in &link.markers {
                            if *request == presented.request.id {
                                view.set_dataset_link(*request, *key, points.clone());
                            }
                        }
                    }
                }
                // Keep the trajectory spatially stable; only linked time windows follow.
                if !view.borrow().is_time_plot()
                    && let Some(target) = self
                        .workspace
                        .monitors
                        .iter()
                        .flatten()
                        .filter(|target| {
                            !self
                                .workspace
                                .hidden_datasets
                                .contains(&target.borrow().dataset_key())
                        })
                        .find(|target| target.borrow_mut().reveal_linked_time())
                {
                    self.sync_monitor_time(target);
                }
                self.workspace.link = Some(link);
            }
            Err(error) => self.control_error = Some(error),
        }
        true
    }

    pub(super) fn plot_views(&self) -> Vec<Rc<RefCell<ChartView>>> {
        self.chart
            .iter()
            .chain(self.comparison_chart.iter())
            .chain(&self.workspace.extra_views)
            .chain(self.workspace.monitors.iter().flatten())
            .cloned()
            .collect()
    }

    pub(super) fn visible_views(
        &self,
        views: impl Iterator<Item = Rc<RefCell<ChartView>>>,
    ) -> Vec<Rc<RefCell<ChartView>>> {
        views
            .filter(|view| {
                !self
                    .workspace
                    .hidden_datasets
                    .contains(&view.borrow().dataset_key())
            })
            .collect()
    }

    pub(super) fn analysis_chart(&self) -> Option<Rc<RefCell<ChartView>>> {
        self.visible_views(
            self.chart
                .iter()
                .chain(self.comparison_chart.iter())
                .chain(&self.workspace.extra_views)
                .cloned(),
        )
        .into_iter()
        .next()
    }

    pub(super) fn toggle_analysis_dataset(&mut self, key: u32) {
        let Some(analysis) = &self.workspace.analysis else {
            return;
        };
        if self.workspace.hidden_datasets.remove(&key) {
            self.clear_analysis_interaction();
            return;
        }
        if analysis
            .input
            .datasets
            .iter()
            .filter(|(key, _)| !self.workspace.hidden_datasets.contains(key))
            .count()
            <= 1
        {
            return;
        }
        self.workspace.hidden_datasets.insert(key);
        let replacement = analysis
            .input
            .datasets
            .iter()
            .find(|(candidate, _)| !self.workspace.hidden_datasets.contains(candidate))
            .map(|(candidate, _)| *candidate);
        for options in self.workspace.detail_options.values_mut() {
            if options.gap_dataset == Some(key) {
                options.gap_dataset = replacement;
            }
        }
        for view in self.plot_views() {
            if view.borrow().dataset_key() == key {
                view.borrow_mut().close();
            }
        }
        self.clear_analysis_interaction();
    }

    fn finish_analysis_selection(&mut self) {
        let Some(chart) = self.analysis_chart() else {
            return;
        };
        chart.borrow_mut().drag_end();
        let Some(start) = self.selection_start.take() else {
            return;
        };
        if let Some((result, key, end)) = chart.borrow().hit() {
            let snapshot = result.snapshot(key).unwrap();
            if let (Some(start), Some(end)) = (
                crate::product::performance_analysis::time_ms(snapshot, start, self.logical),
                crate::product::performance_analysis::time_ms(snapshot, end, self.logical),
            ) {
                self.set_analysis_range(start.min(end), start.max(end));
            }
        }
        chart.borrow_mut().end_selection();
    }
}

#[cfg(test)]
mod filename_tests {
    use super::default_export_filename;

    #[test]
    fn export_filename_uses_mouse_name_and_four_character_alphanumeric_code() {
        for name in ["罗技 G PRO X", "Razer_Viper_V3"] {
            let filename = default_export_filename(name);
            let stem = filename.strip_suffix(".json").unwrap();
            let (mouse_name, code) = stem.rsplit_once('_').unwrap();
            assert_eq!(mouse_name, name);
            assert_eq!(code.len(), 4);
            assert!(
                code.bytes()
                    .all(|byte| byte.is_ascii_uppercase() || byte.is_ascii_digit())
            );
            assert!(code.bytes().any(|byte| byte.is_ascii_uppercase()));
            assert!(code.bytes().any(|byte| byte.is_ascii_digit()));
        }
    }

    #[test]
    fn export_filename_sanitizes_invalid_characters_and_empty_names() {
        for (name, expected) in [
            (" .Mouse<>:\"/\\|?*\n. ", "Mouse__________"),
            ("", "Mouse"),
            (" . ", "Mouse"),
            (" \t. ", "_"),
            ("\r\n", "__"),
        ] {
            let filename = default_export_filename(name);
            let stem = filename.strip_suffix(".json").unwrap();
            let (mouse_name, _) = stem.rsplit_once('_').unwrap();
            assert_eq!(mouse_name, expected, "input: {name:?}");
        }
    }
}
