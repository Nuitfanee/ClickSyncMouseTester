use super::*;
use std::cell::Cell;

pub(in crate::ui) struct Frame {
    content: gpui::Entity<Content>,
    plots: [gpui::Entity<Plot>; 4],
    summary: gpui::Entity<Readout>,
    inspection: gpui::Entity<Inspection>,
    bounds: [Rc<Cell<Bounds<gpui::Pixels>>>; 6],
    revision: Rc<Cell<u64>>,
    pointer: Option<(Rc<RefCell<ChartView>>, gpui::Point<gpui::Pixels>)>,
    #[cfg(feature = "ui-restoration-probe")]
    counts: Rc<Cell<[u64; 6]>>,
}

struct Content {
    host: gpui::WeakEntity<GpuiHost>,
}

impl Render for Content {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.host
            .update(cx, |host, cx| {
                let frame = host.workspace.interaction.as_ref().unwrap();
                #[cfg(feature = "ui-restoration-probe")]
                {
                    let mut counts = frame.counts.get();
                    counts[0] += 1;
                    frame.counts.set(counts);
                }
                frame.revision.set(frame.revision.get().wrapping_add(1));
                for bounds in &frame.bounds {
                    bounds.set(Bounds::default());
                }
                host.render_workspace_content(window, cx)
            })
            .expect("workspace content belongs to its live host")
    }
}

struct Plot {
    host: gpui::WeakEntity<GpuiHost>,
    slot: usize,
    opacity: f32,
    content: gpui::WeakEntity<Content>,
    presented: Rc<Cell<Option<u64>>>,
    #[cfg(feature = "ui-restoration-probe")]
    counter: (Rc<Cell<[u64; 6]>>, usize),
}

impl Render for Plot {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let host = self.host.upgrade().expect("plot belongs to its live host");
        let host = host.read(cx);
        // Bind data here, not as a side effect of rendering the cached controls.
        let views = host.workspace_plot_views(self.slot);
        let empty = self.slot == 0 && host.workspace.detail.is_none() && views.is_empty();
        let palette = design::Palette::for_theme(host.settings.dark);
        let content = self.content.clone();
        let presented = self.presented.clone();
        let refresh_content = host.workspace.analysis.is_some();
        let pan_host = self.host.clone();
        #[cfg(feature = "ui-restoration-probe")]
        let (counts, slot) = self.counter.clone();
        chart_canvas(
            views,
            (self.slot == 0).then(|| host.outcome.clone()),
            host.settings.dark,
            host.settings.english,
            move |request, window| {
                let changed = presented.replace(Some(request)) != Some(request);
                #[cfg(feature = "ui-restoration-probe")]
                if changed {
                    let mut next = counts.get();
                    next[slot + 1] += 1;
                    counts.set(next);
                }
                if changed && refresh_content {
                    let content = content.clone();
                    window.on_next_frame(move |_, cx| {
                        let _ = content.update(cx, |_, cx| cx.notify());
                    });
                }
            },
            move |view, cx| {
                let _ = pan_host.update(cx, |host, cx| {
                    host.sync_monitor_time(&view);
                    if let Some(frame) = &host.workspace.interaction {
                        for slot in 0..4 {
                            frame.notify_plot(slot, cx);
                        }
                    }
                });
            },
        )
        .when(empty, |plot| {
            plot.child(
                div()
                    .absolute()
                    .inset_0()
                    .flex()
                    .items_center()
                    .justify_center()
                    .child(
                        div()
                            .bg(rgb(palette.background))
                            .px(px(16.0))
                            .py(px(10.0))
                            .flex()
                            .flex_col()
                            .items_center()
                            .gap(px(12.0))
                            .child(
                                div().text_size(px(22.0)).text_color(rgb(palette.text))
                                    .child(if host.lock.locked() {
                                        host.text("等待鼠标移动", "Waiting for movement")
                                    } else {
                                        host.text("准备记录轨迹", "Ready to capture")
                                    }),
                            )
                            .child(
                                div().text_size(px(14.0)).text_color(rgb(palette.muted))
                                    .child(if host.lock.locked() {
                                        host.text("移动鼠标即可显示真实采样点", "Move the mouse to show captured samples")
                                    } else {
                                        host.text("点击画布任意位置或按 S 从中心开始，移动鼠标绘制采样点",
                                            "Click the canvas or press S to start at the center, then move to draw samples")
                                    }),
                            ),
                    ),
            )
        })
        .opacity(self.opacity)
    }
}

#[derive(Default)]
struct Readout {
    text: String,
    ink: u32,
    dark: bool,
    english: bool,
}

// The placeholder and cached readout share geometry; changing numbers never reflows the plots.
fn summary_layout() -> gpui::Div {
    div()
        .flex()
        .flex_wrap()
        .items_center()
        .content_center()
        .gap_x(px(20.0))
        .gap_y(px(4.0))
}

fn summary_group(english: bool) -> gpui::Div {
    div()
        .w(px(if english { 368.0 } else { 340.0 }))
        .h(px(28.0))
        .flex_shrink_0()
        .flex()
        .items_center()
        .gap(px(12.0))
        .whitespace_nowrap()
}

impl Render for Readout {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        let mut rows = summary_layout()
            .size_full()
            .overflow_hidden()
            .text_size(px(13.0));
        for group in self.text.split('\n').filter(|group| !group.is_empty()) {
            let mut row = summary_group(self.english);
            for field in group.split('\t') {
                let mut cell = div().flex().items_center().gap(px(5.0)).flex_shrink_0();
                if let Some((label, value)) = field.split_once(':') {
                    cell = cell
                        .child(div().text_color(rgb(self.ink)).child(label.to_owned()))
                        .child(
                            div()
                                .font_family(design::DISPLAY_FONT)
                                .font_weight(gpui::FontWeight::MEDIUM)
                                .child(value.to_owned()),
                        );
                } else {
                    cell = cell
                        .when(matches!(field, "A" | "B" | "C"), |cell| {
                            cell.child(div().w(px(10.0)).h(px(2.0)).bg(rgb(
                                crate::chart_view::dataset_color(
                                    self.dark,
                                    match field {
                                        "A" => 0,
                                        "B" => 1,
                                        _ => 2,
                                    },
                                ),
                            )))
                        })
                        .child(field.to_owned());
                }
                row = row.child(cell);
            }
            rows = rows.child(row);
        }
        rows
    }
}

struct Inspection {
    host: gpui::WeakEntity<GpuiHost>,
}

impl Render for Inspection {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let host = self
            .host
            .upgrade()
            .expect("inspection belongs to its live host");
        let host = host.read(cx);
        let palette = design::Palette::for_theme(host.settings.dark);
        let fields = host
            .plot_views()
            .iter()
            .find_map(|view| {
                let rows = view.borrow().hover_readout();
                (!rows.is_empty()).then_some(rows)
            })
            .unwrap_or_default();
        let mut row = div()
            .size_full()
            .flex()
            .items_center()
            .justify_end()
            .gap(px(16.0));
        for (label, value) in fields {
            let width = match label {
                "Dataset" | "数据组" => 44.0,
                "Report" | "报告序号" | "Samples" | "样本数" | "Share" | "占比" => 88.0,
                "数值区间" => 184.0,
                _ => 156.0,
            };
            row = row.child(
                div()
                    .w(px(width))
                    .flex_shrink_0()
                    .flex()
                    .flex_col()
                    .gap(px(2.0))
                    .child(
                        div()
                            .text_size(px(11.0))
                            .text_color(rgb(palette.muted))
                            .child(label),
                    )
                    .child(
                        div()
                            .text_size(px(13.0))
                            .font_family(design::DISPLAY_FONT)
                            .font_weight(gpui::FontWeight::MEDIUM)
                            .child(value),
                    ),
            );
        }
        row
    }
}

impl Frame {
    fn new(host: &gpui::Entity<GpuiHost>, cx: &mut Context<GpuiHost>) -> Self {
        #[cfg(feature = "ui-restoration-probe")]
        let counts = Rc::new(Cell::new([0; 6]));
        let content = cx.new(|cx| {
            // Direct host notifications refresh controls; plot notifications do not.
            #[cfg(feature = "ui-restoration-probe")]
            let notifications = counts.clone();
            cx.observe(host, move |_, _, cx| {
                #[cfg(feature = "ui-restoration-probe")]
                {
                    let mut counts = notifications.get();
                    counts[5] += 1;
                    notifications.set(counts);
                }
                cx.notify();
            })
            .detach();
            Content {
                host: host.downgrade(),
            }
        });
        let plots = std::array::from_fn(|slot| {
            cx.new(|_| Plot {
                host: host.downgrade(),
                slot,
                content: content.downgrade(),
                opacity: 1.0,
                presented: Rc::default(),
                #[cfg(feature = "ui-restoration-probe")]
                counter: (counts.clone(), slot),
            })
        });
        Self {
            content,
            plots,
            summary: cx.new(|_| Readout::default()),
            inspection: cx.new(|_| Inspection {
                host: host.downgrade(),
            }),
            bounds: Default::default(),
            revision: Rc::default(),
            pointer: None,
            #[cfg(feature = "ui-restoration-probe")]
            counts,
        }
    }

    pub(super) fn set_opacity(&self, opacity: f32, cx: &mut Context<GpuiHost>) {
        for plot in &self.plots {
            plot.update(cx, |plot, cx| {
                if plot.opacity != opacity {
                    plot.opacity = opacity;
                    cx.notify();
                }
            });
        }
    }

    pub(in crate::ui) fn notify_plot(&self, slot: usize, cx: &mut Context<GpuiHost>) {
        self.plots[slot].update(cx, |_, cx| cx.notify());
    }

    pub(in crate::ui) fn notify_plots(&self, cx: &mut Context<GpuiHost>) {
        for slot in 0..4 {
            self.notify_plot(slot, cx);
        }
    }
    pub(super) fn notify_summary(&self, cx: &mut Context<GpuiHost>) {
        self.summary.update(cx, |_, cx| cx.notify());
    }

    #[cfg(feature = "ui-restoration-probe")]
    pub(in crate::ui) fn counts(&self) -> [u64; 6] {
        self.counts.get()
    }

    #[cfg(feature = "ui-restoration-probe")]
    pub(in crate::ui) fn layout_report(&self) -> serde_json::Value {
        serde_json::json!({
            "counts": self.counts(),
            "revision": self.revision.get(),
            "bounds": self.bounds.each_ref().map(|bounds| {
                let bounds = bounds.get();
                [f32::from(bounds.left()), f32::from(bounds.top()),
                    f32::from(bounds.size.width), f32::from(bounds.size.height)]
            }),
        })
    }
}

impl GpuiHost {
    pub(in crate::ui) fn render_workspace(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> gpui::Stateful<gpui::Div> {
        let host = cx.entity();
        if self.workspace.interaction.is_none() {
            self.workspace.interaction = Some(Frame::new(&host, cx));
        }
        if let Some((view, position)) = self.workspace.interaction.as_mut().unwrap().pointer.take()
        {
            let views = self.workspace_plot_views(self.workspace_plot_slot(&view));
            let current = !self.lock.locked()
                && !view.borrow().is_dragging()
                && views.iter().any(|current| Rc::ptr_eq(current, &view));
            let (view, hit_changed) = if current {
                ChartView::move_group_pointer(&views, position).unwrap()
            } else {
                (view, false)
            };
            let previous_error = self.control_error.clone();
            // A different chart can own the link even when this chart's local hit is unchanged.
            let linked_changed = current && self.update_analysis_hover_for(view);
            let changed = hit_changed || linked_changed;
            if changed {
                let frame = self.workspace.interaction.as_ref().unwrap();
                frame.notify_plots(cx);
                frame.inspection.update(cx, |_, cx| cx.notify());
                if self.control_error != previous_error {
                    let host = host.downgrade();
                    window.on_next_frame(move |_, cx| {
                        let _ = host.update(cx, |_, cx| cx.notify());
                    });
                }
            }
        }
        self.sync_summary_readout(cx);
        let frame = self.workspace.interaction.as_ref().unwrap();
        let content = frame.content.clone();
        let overlays = frame.plots.iter().cloned().map(gpui::AnyView::from).chain([
            gpui::AnyView::from(frame.summary.clone()),
            gpui::AnyView::from(frame.inspection.clone()),
        ]);
        let mut root = div()
            .id("workspace-frame")
            .when(!self.custom_chrome, |root| {
                root.track_focus(&self.focus).on_key_down(
                    cx.listener(|host, event, window, cx| host.keyboard(event, window, cx)),
                )
            })
            .size_full()
            .relative()
            .font_family(design::Typography::body_font(self.settings.english))
            .text_size(px(13.0))
            .text_color(rgb(design::Palette::for_theme(self.settings.dark).text))
            .child(gpui::AnyView::from(content).cached(div().size_full().style().clone()));
        for (slot, view) in overlays.enumerate() {
            let bounds = frame.bounds[slot].clone();
            let revision = frame.revision.clone();
            root = root.child(
                canvas(
                    move |_, window, cx| {
                        let bounds = bounds.get();
                        if bounds.size.width <= px(0.0) || bounds.size.height <= px(0.0) {
                            return None;
                        }
                        // Content can change layout/state during this frame's prepaint.
                        let mut element = div()
                            .id(("plot-frame", revision.get()))
                            .size_full()
                            .child(view.cached(div().size_full().style().clone()))
                            .into_any_element();
                        element.prepaint_as_root(bounds.origin, bounds.size.into(), window, cx);
                        Some(element)
                    },
                    |_, element, window, cx| {
                        if let Some(mut element) = element {
                            element.paint(window, cx);
                        }
                    },
                )
                .absolute()
                .inset_0()
                .size_full(),
            );
        }
        root.on_mouse_move(cx.listener(|host, event: &gpui::MouseMoveEvent, _, cx| {
            if event.pressed_button.is_some() || host.workspace.link.is_none() {
                return;
            }
            let outside = host.workspace.interaction.as_ref().is_some_and(|frame| {
                frame.bounds[..4]
                    .iter()
                    .all(|bounds| !bounds.get().contains(&event.position))
            });
            if outside && let Some(chart) = host.analysis_chart() {
                host.queue_analysis_pointer(chart, event.position, cx);
            }
        }))
    }

    pub(in crate::ui) fn workspace_chart_canvas(
        &self,
        slot: usize,
        cx: &mut Context<Self>,
    ) -> gpui::Div {
        let frame = self.workspace.interaction.as_ref().unwrap();
        frame.notify_plot(slot, cx);
        let bounds = frame.bounds[slot].clone();
        div()
            .size_full()
            .flex_1()
            .min_h_0()
            .relative()
            .child(canvas(move |actual, _, _| bounds.set(actual), |_, _, _, _| {}).size_full())
    }

    pub(in crate::ui::workspace) fn workspace_plot_views(
        &self,
        slot: usize,
    ) -> Vec<Rc<RefCell<ChartView>>> {
        if slot == 0 {
            self.visible_views(
                self.chart
                    .iter()
                    .chain(self.comparison_chart.iter())
                    .chain(&self.workspace.extra_views)
                    .cloned(),
            )
        } else {
            self.visible_views(self.workspace.monitors[slot - 1].iter().cloned())
        }
    }

    fn workspace_plot_slot(&self, view: &Rc<RefCell<ChartView>>) -> usize {
        if self.workspace.detail.is_some() {
            0
        } else {
            self.workspace
                .monitors
                .iter()
                .position(|views| views.iter().any(|candidate| Rc::ptr_eq(candidate, view)))
                .map_or(0, |slot| slot + 1)
        }
    }

    pub(in crate::ui) fn queue_analysis_pointer(
        &mut self,
        view: Rc<RefCell<ChartView>>,
        position: gpui::Point<gpui::Pixels>,
        cx: &mut Context<Self>,
    ) {
        let slot = self.workspace_plot_slot(&view);
        let Some(frame) = &mut self.workspace.interaction else {
            return;
        };
        frame.pointer = Some((view, position));
        // Wake a mounted plot, including detail where the overview summary is not mounted.
        frame.plots[slot].update(cx, |_, cx| cx.notify());
    }

    pub(in crate::ui) fn sync_monitor_time(&self, source: &Rc<RefCell<ChartView>>) {
        if self.workspace.detail.is_some()
            || !source.borrow().is_time_plot()
            || !self
                .workspace
                .monitors
                .iter()
                .flatten()
                .any(|view| Rc::ptr_eq(view, source))
        {
            return;
        }
        let viewport = source.borrow().viewport();
        for view in self.workspace.monitors.iter().flatten() {
            if !Rc::ptr_eq(view, source) {
                view.borrow_mut().sync_time_view(viewport);
            }
        }
    }

    fn sync_summary_readout(&self, cx: &mut Context<Self>) {
        if let Some(frame) = &self.workspace.interaction {
            let text = self.workspace_summary_text();
            frame.summary.update(cx, |summary, _| {
                summary.text = text;
                summary.ink = design::Palette::for_theme(self.settings.dark).muted;
                summary.dark = self.settings.dark;
                summary.english = self.settings.english;
            });
        }
    }

    pub(in crate::ui) fn workspace_summary_readout(&self) -> gpui::Div {
        let bounds = self.workspace.interaction.as_ref().unwrap().bounds[4].clone();
        let groups = self
            .workspace
            .analysis
            .as_ref()
            .map(|a| a.input.datasets.len())
            .or_else(|| self.workspace.latest.as_ref().map(|p| p.datasets.len()))
            .filter(|count| *count > 0)
            .unwrap_or(self.selected.len())
            .max(1);
        summary_layout()
            .flex_1()
            .min_w_0()
            .relative()
            .children((0..groups).map(|_| summary_group(self.settings.english)))
            .child(
                canvas(move |actual, _, _| bounds.set(actual), |_, _, _, _| {})
                    .absolute()
                    .inset_0()
                    .size_full(),
            )
    }

    pub(in crate::ui) fn workspace_inspection_readout(&self, cx: &mut Context<Self>) -> gpui::Div {
        let frame = self.workspace.interaction.as_ref().unwrap();
        frame.inspection.update(cx, |_, cx| cx.notify());
        let bounds = frame.bounds[5].clone();
        div()
            .w(px(556.0))
            .h(px(40.0))
            .flex_shrink_0()
            .child(canvas(move |actual, _, _| bounds.set(actual), |_, _, _, _| {}).size_full())
    }
}
