use crate::chart::ChartResult;
use clicksync_chart::gpu::{
    GpuError, PublishedChartLayer, RenderSpec, Renderer, ResourceLimits, ResourceReport, Viewport,
};
use gpui::{
    App, Bounds, Pixels, Point, TextRun, Window, fill, font, point, px, rgb, size,
    windows_graphics::WindowsSurface,
};
use serde::Serialize;
use std::{cell::RefCell, rc::Rc, sync::Arc};

mod axes;
#[cfg(test)]
mod trajectory_tests;

#[derive(Clone)]
struct Presentation {
    surface: WindowsSurface,
    layer: PublishedChartLayer,
    result: Arc<ChartResult>,
    viewport_revision: u64,
}

#[derive(Default, Clone, Serialize)]
pub struct ChartViewReport {
    pub completed_frames: u64,
    pub discarded_layers: u64,
    pub device_rebuilds: u64,
    pub pan_events: u64,
    pub zoom_events: u64,
    pub hit_queries: u64,
    pub received_sequence: u64,
    pub processed_end: usize,
    pub layer_end: usize,
    pub device_epoch: u64,
    pub width: u32,
    pub height: u32,
    pub request_id: u64,
    pub axes_request_id: u64,
    pub logical_bounds: [f32; 4],
    pub outer_bounds: [f32; 4],
    pub resources: Option<ResourceReport>,
    pub error: Option<String>,
}

pub struct ChartView {
    result: Arc<ChartResult>,
    renderer: Option<Renderer>,
    generation: Option<(Arc<ChartResult>, RenderSpec, u64)>,
    displayed: Option<Presentation>,
    pending_presentation: Option<Presentation>,
    viewport: Viewport,
    trajectory_base: Option<Viewport>,
    viewport_revision: u64,
    bounds: Bounds<Pixels>,
    axis_gutter: f32,
    axes: axes::Cache,
    drag: Option<(Point<Pixels>, Viewport, [bool; 2])>,
    report: ChartViewReport,
    dataset: usize,
    fixed: bool,
    overlay: bool,
    overlay_viewport: Option<Viewport>,
    hover: Option<(usize, clicksync_chart::Point)>,
    histogram_hover: Option<clicksync_chart::analysis::histogram::HistogramBin>,
    selection: Option<(Arc<ChartResult>, Viewport, u64)>,
    limits: ResourceLimits,
    workspace_style: bool,
    dark: bool,
    english: bool,
    trajectory: bool,
    linked: Option<(u64, Vec<clicksync_chart::Point>)>,
}

impl ChartView {
    pub fn new(result: Arc<ChartResult>) -> Result<Self, String> {
        Self::with_resource_limits(result, ResourceLimits::default())
    }
    pub fn with_resource_limits(
        result: Arc<ChartResult>,
        limits: ResourceLimits,
    ) -> Result<Self, String> {
        let key = result
            .datasets
            .first()
            .ok_or("chart dataset is empty")?
            .device_key;
        let viewport = result.viewport(key)?;
        Ok(Self {
            result,
            renderer: None,
            generation: None,
            displayed: None,
            pending_presentation: None,
            viewport,
            trajectory_base: None,
            viewport_revision: 1,
            bounds: Bounds::default(),
            axis_gutter: 48.0,
            axes: axes::Cache::default(),
            drag: None,
            report: ChartViewReport::default(),
            dataset: 0,
            fixed: false,
            overlay: false,
            overlay_viewport: None,
            hover: None,
            histogram_hover: None,
            selection: None,
            limits,
            workspace_style: false,
            dark: false,
            english: false,
            trajectory: false,
            linked: None,
        })
    }

    pub fn report(&self) -> ChartViewReport {
        let mut report = self.report.clone();
        if let Some(presentation) = &self.displayed {
            let version = &presentation.layer.version;
            report.processed_end = version.processed_end;
            report.layer_end = version.published_end;
            report.request_id = version.spec.request_id;
            report.device_epoch = version.spec.device_epoch;
            report.width = version.spec.width;
            report.height = version.spec.height;
        }
        report
    }
    #[cfg(feature = "ui-restoration-probe")]
    pub(crate) fn result_for_report(&self) -> Arc<ChartResult> {
        self.result.clone()
    }
    pub fn set_result(&mut self, result: Arc<ChartResult>) -> Result<(), String> {
        let range_changed = self.result.request.analysis.as_ref().and_then(|a| a.range)
            != result.request.analysis.as_ref().and_then(|a| a.range);
        let axes_changed = self.result.request.parameters.metric
            != result.request.parameters.metric
            || self.result.request.parameters.time_basis != result.request.parameters.time_basis
            || self.result.request.parameters.histogram != result.request.parameters.histogram
            || self.result.request.kind != result.request.kind;
        if range_changed || axes_changed {
            self.selection = None;
            if !self.trajectory {
                self.fixed = false;
            }
        }
        if axes_changed {
            self.axis_gutter = 48.0;
        }
        let replaced = self.result.request.source_version != result.request.source_version
            || self.result.request.prefix.generation != result.request.prefix.generation
            || self.result.datasets.get(self.dataset).map(|d| d.device_key)
                != result.datasets.get(self.dataset).map(|d| d.device_key);
        if replaced {
            self.axis_gutter = 48.0;
            self.displayed = None;
            self.pending_presentation = None;
            self.selection = None;
            self.fixed = false;
            self.trajectory_base = None;
        }
        let same_input = !replaced
            && !range_changed
            && Arc::ptr_eq(&self.result.request.prefix, &result.request.prefix)
            && self.result.request.parameters == result.request.parameters
            && self.result.request.dataset_cpi == result.request.dataset_cpi
            && self.result.request.secondary_metric == result.request.secondary_metric
            && self.result.request.kind == result.request.kind
            && self.result.request.view == result.request.view;
        if !self.fixed && self.selection.is_none() && !same_input {
            let next = if self.workspace_style {
                result.workspace_viewport(result.datasets[self.dataset].device_key)?
            } else {
                result.viewport(result.datasets[self.dataset].device_key)?
            };
            self.viewport = if self.trajectory && !replaced {
                stable_trajectory(self.trajectory_base.unwrap_or(self.viewport), next)
            } else if self.workspace_style
                && self.is_time_plot()
                && !replaced
                && !range_changed
                && self.result.request.parameters == result.request.parameters
                && self.result.request.dataset_cpi == result.request.dataset_cpi
                && self.result.request.secondary_metric == result.request.secondary_metric
                && self.result.request.kind == result.request.kind
                && (matches!(result.request.view, crate::chart::ViewMode::Follow { .. })
                    || (result.request.view == crate::chart::ViewMode::FullSession
                        && result.request.prefix.phase == crate::backend::CapturePhase::Collecting))
                && self
                    .result
                    .datasets
                    .iter()
                    .zip(&result.datasets)
                    .all(|(old, new)| {
                        !old.geometry.is_empty()
                            && old.interval_band.is_some() == new.interval_band.is_some()
                    })
            {
                stable_value_axis(self.viewport, next)
            } else {
                next
            };
            self.trajectory_base = None;
        }
        let unchanged_visible = self.fixed
            && self.result.request.source_version == result.request.source_version
            && self.result.request.prefix.generation == result.request.prefix.generation
            && self.result.request.parameters == result.request.parameters
            && self.result.request.dataset_cpi == result.request.dataset_cpi
            && self.result.request.secondary_metric == result.request.secondary_metric
            && self.result.request.kind == result.request.kind
            && self.result.request.analysis == result.request.analysis
            && same_visible_geometry(
                &self.result.datasets[self.dataset],
                &result.datasets[self.dataset],
                self.viewport,
            );
        self.result = result;
        if !unchanged_visible {
            self.viewport_revision += 1;
        }
        Ok(())
    }
    pub fn close(&mut self) {
        self.axes = axes::Cache::default();
        self.drag = None;
        self.linked = None;
        self.displayed = None;
        self.pending_presentation = None;
        self.selection = None;
        self.generation = None;
        self.renderer = None;
    }
    pub fn set_resource_limits(&mut self, limits: ResourceLimits) {
        if self.limits.resident_bytes != limits.resident_bytes
            || self.limits.layer_bytes != limits.layer_bytes
            || self.limits.instances_per_upload != limits.instances_per_upload
        {
            self.close();
            self.limits = limits;
            self.viewport_revision += 1;
        }
    }
    pub fn set_theme(&mut self, dark: bool, english: bool) {
        if !self.workspace_style || self.dark != dark {
            self.viewport_revision += 1;
        }
        if !self.workspace_style
            && !self.fixed
            && let Ok(viewport) = self
                .result
                .workspace_viewport(self.result.datasets[self.dataset].device_key)
        {
            self.viewport = viewport;
            self.trajectory_base = None;
        }
        self.workspace_style = true;
        self.dark = dark;
        self.english = english;
    }
    pub fn enable_trajectory(&mut self) {
        self.trajectory = self.result.request.kind == clicksync_chart::gpu::PlotKind::Trajectory;
    }
    pub(crate) fn start_trajectory(&mut self, viewport: Viewport) -> Result<(), String> {
        let required = self.result.workspace_viewport(self.dataset_key())?;
        self.set_viewport(stable_trajectory(viewport, required), false);
        Ok(())
    }
    pub fn fit_trajectory(&mut self) {
        self.drag_end();
        self.selection = None;
        let key = self.result.datasets[self.dataset].device_key;
        let fitted = if self.workspace_style {
            self.result.full_workspace_viewport(key)
        } else {
            self.result.viewport(key)
        };
        if let Ok(viewport) = fitted {
            self.set_viewport(viewport, true);
        }
    }
    pub(crate) fn fit_value_axis(&mut self) {
        if let Ok(fitted) = self.result.workspace_viewport(self.dataset_key()) {
            let mut viewport = self.viewport;
            viewport.origin.y = fitted.origin.y;
            viewport.height = fitted.height;
            self.set_viewport(viewport, true);
        }
    }
    fn fit_trajectory_aspect(&mut self, aspect: f64) {
        // Keep the data/user viewport separate from padding added for the
        // current plot aspect, so alternating sizes cannot compound it.
        let base = *self.trajectory_base.get_or_insert(self.viewport);
        let next = equal_scale(base, aspect);
        if self.viewport != next {
            self.viewport = next;
            self.viewport_revision += 1;
        }
    }
    pub fn select_dataset(&mut self, index: usize) -> Result<(), String> {
        if index >= self.result.datasets.len() {
            return Err("unknown chart dataset".into());
        }
        self.dataset = index;
        self.overlay = index > 0;
        self.viewport = self
            .result
            .viewport(self.result.datasets[index].device_key)?;
        self.trajectory_base = None;
        self.viewport_revision += 1;
        Ok(())
    }
    pub fn viewport(&self) -> Viewport {
        self.viewport
    }

    pub(crate) fn is_time_plot(&self) -> bool {
        !matches!(
            self.result.request.kind,
            clicksync_chart::gpu::PlotKind::Trajectory | clicksync_chart::gpu::PlotKind::Histogram
        )
    }

    pub(crate) fn sync_time_view(&mut self, source: Viewport) {
        if self.is_time_plot() {
            let mut next = self.viewport;
            next.origin.x = shift_view(next.origin.x, source.origin.x.relative_to(next.origin.x));
            next.width = source.width;
            self.set_viewport(next, true);
        }
    }

    pub(crate) fn reveal_linked_time(&mut self) -> bool {
        if !self.is_time_plot() || self.is_dragging() || self.is_selecting() {
            return false;
        }
        let Some((request, points)) = &self.linked else {
            return false;
        };
        if self.displayed_request_id() != Some(*request) {
            return false;
        }
        let Some(point) = points.first() else {
            return false;
        };
        self.reveal_time(point.x)
    }

    fn reveal_time(&mut self, time: clicksync_chart::Coordinate) -> bool {
        let offset = time.relative_to(self.viewport.origin.x);
        if !offset.is_finite() || (0.0..=self.viewport.width).contains(&offset) {
            return false;
        }
        let margin = self.viewport.width * 0.08;
        let shift = if offset < margin {
            offset - self.viewport.width * 0.25
        } else if offset > self.viewport.width - margin {
            offset - self.viewport.width * 0.75
        } else {
            return false;
        };
        let mut next = self.viewport;
        next.origin.x = shift_view(next.origin.x, shift);
        self.set_viewport(next, true);
        true
    }
    pub fn dataset_key(&self) -> u32 {
        self.result.datasets[self.dataset].device_key
    }
    pub fn set_overlay(&mut self, overlay: bool) {
        self.overlay = overlay;
        if !overlay {
            self.overlay_viewport = None;
        }
    }
    pub(crate) fn set_overlay_viewport(&mut self, viewport: Option<Viewport>) {
        self.overlay_viewport = viewport;
    }
    #[cfg(feature = "ui-restoration-probe")]
    pub(crate) fn bounds_for_trial(&self) -> Bounds<Pixels> {
        self.bounds
    }
    pub fn set_viewport(&mut self, viewport: Viewport, fixed: bool) {
        if self.viewport != viewport || self.fixed != fixed {
            self.viewport = viewport;
            self.trajectory_base = None;
            self.fixed = fixed;
            self.viewport_revision += 1;
        }
    }
    pub fn displayed_request_id(&self) -> Option<u64> {
        self.displayed.as_ref().map(|p| p.result.request.id)
    }
    pub fn presented_result(&self) -> Option<Arc<ChartResult>> {
        self.displayed.as_ref().map(|p| p.result.clone())
    }
    pub fn presented_viewport(&self) -> Option<Viewport> {
        self.displayed.as_ref().map(|p| self.paint_viewport(p))
    }
    fn paint_viewport(&self, presentation: &Presentation) -> Viewport {
        if let Some(viewport) = self.overlay_viewport.filter(|_| self.overlay) {
            viewport
        } else if self.fixed && self.selection.is_none() {
            self.viewport
        } else {
            presentation.layer.version.spec.viewport
        }
    }
    pub fn hit(&self) -> Option<(Arc<ChartResult>, u32, usize)> {
        Some((
            self.displayed.as_ref()?.result.clone(),
            self.displayed.as_ref()?.result.datasets[self.dataset].device_key,
            self.hover?.0,
        ))
    }
    pub fn histogram_hit(
        &self,
    ) -> Option<(
        Arc<ChartResult>,
        u32,
        clicksync_chart::analysis::histogram::HistogramBin,
    )> {
        Some((
            self.displayed.as_ref()?.result.clone(),
            self.displayed.as_ref()?.result.datasets[self.dataset].device_key,
            self.histogram_hover?,
        ))
    }
    pub fn set_dataset_link(
        &mut self,
        request: u64,
        key: u32,
        mut points: Vec<clicksync_chart::Point>,
    ) {
        if self.result.datasets[self.dataset].device_key == key {
            if self.result.request.kind == clicksync_chart::gpu::PlotKind::Histogram
                && self.result.datasets.len() > 1
            {
                let count = self.result.datasets.len() as f64;
                let bins = &self.result.datasets[self.dataset].histogram;
                for point in &mut points {
                    if let Some(bin) = crate::chart::histogram_bin(bins, point.x.value())
                        .and_then(|index| bins.get(index))
                    {
                        let width = bin.maximum - bin.minimum;
                        let offset = (self.dataset as f64 + 0.5) * width / count
                            - width / 2.0
                            - 0.82 / count * width / 2.0;
                        point.x = clicksync_chart::Coordinate::Scalar(point.x.value() + offset);
                    }
                }
            }
            self.linked = Some((request, points));
        }
    }
    pub fn clear_link(&mut self) {
        self.linked = None;
    }
    pub(crate) fn clear_hover(&mut self) {
        self.hover = None;
        self.histogram_hover = None;
    }
    pub fn ready(&self) -> bool {
        self.displayed.as_ref().is_some_and(|p| {
            self.selection.as_ref().map_or_else(
                // A group shares the GPU request ID, not each view's local revision.
                || p.viewport_revision == self.viewport_revision,
                |s| p.layer.version.spec.request_id == s.2,
            )
        })
    }
    pub fn begin_selection(&mut self, position: Point<Pixels>) -> Option<usize> {
        self.move_pointer(position);
        let start = self.hover?.0;
        self.hold_presentation()?;
        Some(start)
    }
    pub fn hold_presentation(&mut self) -> Option<u64> {
        let displayed = self.displayed.as_ref()?;
        self.selection = Some((
            displayed.result.clone(),
            self.paint_viewport(displayed),
            displayed.layer.version.spec.request_id,
        ));
        Some(displayed.result.request.id)
    }
    pub fn end_selection(&mut self) {
        if self.selection.take().is_some() {
            if !self.fixed
                && let Ok(viewport) = self
                    .result
                    .viewport(self.result.datasets[self.dataset].device_key)
            {
                self.viewport = viewport;
                self.trajectory_base = None;
            }
            self.viewport_revision += 1;
        }
    }
    pub fn is_selecting(&self) -> bool {
        self.selection.is_some()
    }
    pub fn can_overlay(&self, primary: &Self) -> bool {
        match (&self.displayed, &primary.displayed) {
            (Some(a), Some(b)) => {
                a.result.request.id == b.result.request.id
                    && a.result.request.source_version == b.result.request.source_version
                    && self.paint_viewport(a) == primary.paint_viewport(b)
                    && a.layer.version.spec.y_down == b.layer.version.spec.y_down
                    && a.layer.version.spec.width == b.layer.version.spec.width
                    && a.layer.version.spec.height == b.layer.version.spec.height
                    && a.layer.version.spec.device_epoch == b.layer.version.spec.device_epoch
            }
            _ => false,
        }
    }

    pub(crate) fn group_target(&self) -> Option<(Arc<ChartResult>, Viewport, u64)> {
        let (result, spec) = self
            .pending_presentation
            .as_ref()
            .map(|p| (&p.result, &p.layer.version.spec))
            .or_else(|| {
                self.generation
                    .as_ref()
                    .map(|(result, spec, _)| (result, spec))
            })?;
        result
            .eligible_for(&self.result.request)
            .then(|| (result.clone(), spec.viewport, spec.request_id))
    }

    pub(crate) fn group_ready(&self, primary: &Self) -> bool {
        let candidate = self
            .pending_presentation
            .as_ref()
            .or(self.displayed.as_ref());
        let other = primary
            .pending_presentation
            .as_ref()
            .or(primary.displayed.as_ref());
        candidate.zip(other).is_some_and(|(a, b)| {
            a.result.request.id == b.result.request.id
                && a.result.request.source_version == b.result.request.source_version
                && a.layer.version.spec.viewport == b.layer.version.spec.viewport
                && a.layer.version.spec.width == b.layer.version.spec.width
                && a.layer.version.spec.height == b.layer.version.spec.height
                && a.layer.version.spec.device_epoch == b.layer.version.spec.device_epoch
                && a.layer.version.spec.y_down == b.layer.version.spec.y_down
                && a.result.eligible_for(&self.result.request)
        })
    }

    pub(crate) fn present_group(&mut self) {
        if let Some(presentation) = self.pending_presentation.take() {
            if self
                .displayed
                .as_ref()
                .is_none_or(|p| p.result.request.id != presentation.result.request.id)
            {
                self.hover = None;
                self.histogram_hover = None;
            }
            self.displayed = Some(presentation);
        }
    }

    pub fn drag_start(&mut self, position: Point<Pixels>) {
        let axes = self.interaction_axes(position);
        if axes != [false, false] {
            self.end_selection();
            self.drag = Some((position, self.viewport, axes));
        }
    }
    pub fn drag_end(&mut self) {
        self.drag = None;
    }
    pub fn is_dragging(&self) -> bool {
        self.drag.is_some()
    }
    fn interaction_axes(&self, position: Point<Pixels>) -> [bool; 2] {
        let x = position.x >= self.bounds.left() && position.x <= self.bounds.right();
        let y = position.y >= self.bounds.top() && position.y <= self.bounds.bottom();
        if x && y {
            [true, true]
        } else if x
            && position.y > self.bounds.bottom()
            && position.y <= self.bounds.bottom() + px(28.0)
        {
            [true, false]
        } else if y
            && position.x < self.bounds.left()
            && position.x >= px(self.report.outer_bounds[0])
        {
            [false, true]
        } else {
            [false, false]
        }
    }

    pub(crate) fn move_group_pointer(
        views: &[Rc<RefCell<Self>>],
        position: Point<Pixels>,
    ) -> Option<(Rc<RefCell<Self>>, bool)> {
        let primary = views.first()?;
        if views.len() == 1 || primary.borrow().is_selecting() || primary.borrow().is_dragging() {
            let changed = primary.borrow_mut().move_pointer(position);
            return Some((primary.clone(), changed));
        }
        let mut selected = 0;
        let mut nearest = f64::INFINITY;
        let mut hover = None;
        let mut histogram_hover = None;
        for (index, view) in views.iter().enumerate() {
            if index > 0 && !view.borrow().can_overlay(&primary.borrow()) {
                continue;
            }
            let mut view = view.borrow_mut();
            let previous = (view.hover, view.histogram_hover);
            view.move_pointer(position);
            if let Some(distance) = view.hover_distance(position)
                && distance < nearest
            {
                selected = index;
                nearest = distance;
                hover = view.hover;
                histogram_hover = view.histogram_hover;
            }
            // Query every visible layer once, but commit only the winning hover.
            (view.hover, view.histogram_hover) = previous;
        }
        let mut changed = false;
        for (index, view) in views.iter().enumerate() {
            let mut view = view.borrow_mut();
            let next = if index == selected {
                (hover, histogram_hover)
            } else {
                (None, None)
            };
            changed |= (view.hover, view.histogram_hover) != next;
            (view.hover, view.histogram_hover) = next;
        }
        Some((views[selected].clone(), changed))
    }

    fn hover_distance(&self, position: Point<Pixels>) -> Option<f64> {
        let presentation = self.displayed.as_ref()?;
        let viewport = self.paint_viewport(presentation);
        let spec = &presentation.layer.version.spec;
        let x = f64::from(f32::from(position.x - self.bounds.origin.x));
        let y = f64::from(f32::from(position.y - self.bounds.origin.y));
        let width = f64::from(f32::from(self.bounds.size.width));
        let height = f64::from(f32::from(self.bounds.size.height));
        let value = if let Some((_, point)) = self.hover {
            point
        } else {
            let bin = self.histogram_hover?;
            let count = presentation.result.datasets.len() as f64;
            let bin_width = bin.maximum - bin.minimum;
            let half = bin_width * if count > 1.0 { 0.82 / count } else { 1.0 } * 0.5;
            let center = (bin.minimum + bin.maximum) * 0.5
                + if count > 1.0 {
                    (self.dataset as f64 + 0.5) * bin_width / count - bin_width / 2.0 - half
                } else {
                    0.0
                };
            clicksync_chart::Point {
                x: clicksync_chart::Coordinate::Scalar(
                    (viewport.origin.x.value() + x / width * viewport.width)
                        .clamp(center - half, center + half),
                ),
                y: clicksync_chart::Coordinate::Scalar(
                    (viewport.origin.y.value() + spec.y_fraction(y / height) * viewport.height)
                        .clamp(0.0, bin.value),
                ),
            }
        };
        let dx = value.x.relative_to(viewport.origin.x) / viewport.width * width - x;
        let dy =
            spec.y_fraction(value.y.relative_to(viewport.origin.y) / viewport.height) * height - y;
        Some(dx * dx + dy * dy)
    }

    pub fn move_pointer(&mut self, position: Point<Pixels>) -> bool {
        if let Some((start, viewport, axes)) = self.drag {
            let dx = if axes[0] {
                f32::from(position.x - start.x)
            } else {
                0.0
            };
            let dy = if axes[1] {
                f32::from(position.y - start.y)
            } else {
                0.0
            };
            // Derive from the press viewport; per-event integer rounding loses slow drags.
            let previous = self.viewport;
            self.pan_from(viewport, dx, dy);
            self.hover = None;
            self.histogram_hover = None;
            return self.viewport != previous;
        }
        let old_hover = self.hover;
        let old_histogram = self.histogram_hover;
        if self.bounds.contains(&position)
            && let Some(presentation) = &self.displayed
        {
            let dataset = &presentation.result.datasets[self.dataset];
            let viewport = self.paint_viewport(presentation);
            let x = f32::from(position.x - self.bounds.origin.x) as f64
                / f32::from(self.bounds.size.width) as f64;
            let y = presentation.layer.version.spec.y_fraction(
                f32::from(position.y - self.bounds.origin.y) as f64
                    / f32::from(self.bounds.size.height) as f64,
            );
            if presentation.result.request.kind == clicksync_chart::gpu::PlotKind::Histogram {
                let value = viewport.origin.x.value() + x * viewport.width;
                self.histogram_hover = crate::chart::histogram_bin(&dataset.histogram, value)
                    .and_then(|index| dataset.histogram.get(index))
                    .copied()
                    .filter(|bin| bin.count > 0);
                self.hover = None;
            } else {
                self.histogram_hover = None;
                self.hover = dataset
                    .hit_index
                    .nearest(
                        viewport.origin,
                        [x * viewport.width, y * viewport.height],
                        [
                            f32::from(self.bounds.size.width) as f64 / viewport.width,
                            f32::from(self.bounds.size.height) as f64 / viewport.height,
                        ],
                    )
                    .map(|sample| (sample.source_index, sample.point));
            }
            if self.hover.is_some() || self.histogram_hover.is_some() {
                self.report.hit_queries += 1;
            }
        } else {
            self.hover = None;
            self.histogram_hover = None;
        }
        self.hover != old_hover || self.histogram_hover != old_histogram
    }

    pub fn pan(&mut self, dx: f32, dy: f32) {
        self.pan_from(self.viewport, dx, dy);
    }

    fn pan_from(&mut self, mut viewport: Viewport, dx: f32, dy: f32) {
        let width = f32::from(self.bounds.size.width).max(1.0);
        let height = f32::from(self.bounds.size.height).max(1.0);
        viewport.origin.x = shift_view(
            viewport.origin.x,
            -(dx as f64) * viewport.width / width as f64,
        );
        viewport.origin.y = shift_view(
            viewport.origin.y,
            if self
                .displayed
                .as_ref()
                .map_or(self.trajectory, |p| p.layer.version.spec.y_down)
            {
                -(dy as f64) * viewport.height / height as f64
            } else {
                dy as f64 * viewport.height / height as f64
            },
        );
        if self.viewport != viewport {
            self.end_selection();
            self.set_viewport(viewport, true);
            self.report.pan_events += 1;
        }
    }

    pub fn zoom(&mut self, factor: f64) {
        self.zoom_at(factor, self.bounds.center(), false, false);
    }

    pub(crate) fn scroll_zoom(&mut self, event: &gpui::ScrollWheelEvent) {
        let delta = event.delta.pixel_delta(px(16.0));
        // Windows routes Shift+wheel and horizontal wheels through the X component.
        let distance = if delta.y != px(0.0) { delta.y } else { delta.x };
        self.zoom_at(
            (-f32::from(distance) as f64 * 0.002).exp(),
            event.position,
            event.modifiers.shift,
            event.modifiers.control,
        );
    }

    pub fn zoom_at(
        &mut self,
        factor: f64,
        position: Point<Pixels>,
        shift_key: bool,
        control: bool,
    ) {
        if !factor.is_finite() || factor <= 0.0 {
            return;
        }
        let mut axes = self.interaction_axes(position);
        if axes == [false, false] {
            return;
        }
        self.end_selection();
        self.drag_end();
        let factor = factor.clamp(0.1, 10.0);
        self.clear_hover();
        if self.trajectory {
            axes = [true, true];
        } else if shift_key != control {
            axes = [shift_key, control];
        } else if axes == [true, true] && !shift_key && self.is_time_plot() {
            // A time zoom never silently switches to a different physical quantity.
            axes = [true, false];
        }
        let mut width = self.viewport.width;
        let mut height = self.viewport.height;
        if axes[0] {
            width = (width * factor).clamp(0.0001, 1e15);
        }
        if axes[1] {
            height = (height * factor).clamp(0.0001, 1e15);
        }
        if self.trajectory {
            let ratio = (width / self.viewport.width).max(height / self.viewport.height);
            width = self.viewport.width * ratio;
            height = self.viewport.height * ratio;
        }
        if width == self.viewport.width && height == self.viewport.height {
            return;
        }
        let x = (f64::from(f32::from(position.x - self.bounds.left()))
            / f64::from(f32::from(self.bounds.size.width)).max(1.0))
        .clamp(0.0, 1.0);
        let mut y = (f64::from(f32::from(position.y - self.bounds.top()))
            / f64::from(f32::from(self.bounds.size.height)).max(1.0))
        .clamp(0.0, 1.0);
        if !self.trajectory {
            y = 1.0 - y;
        }
        self.viewport.origin.x =
            shift_view(self.viewport.origin.x, (self.viewport.width - width) * x);
        self.viewport.origin.y =
            shift_view(self.viewport.origin.y, (self.viewport.height - height) * y);
        self.viewport.width = width;
        self.viewport.height = height;
        self.trajectory_base = None;
        self.viewport_revision += 1;
        self.fixed = true;
        self.report.zoom_events += 1;
    }

    pub fn prepaint(&mut self, outer: Bounds<Pixels>, window: &Window) {
        self.prepare(outer, window, None, false);
    }

    pub(crate) fn prepaint_group(
        &mut self,
        outer: Bounds<Pixels>,
        window: &Window,
        target: Option<(Arc<ChartResult>, Viewport, u64)>,
    ) {
        self.prepare(outer, window, target, true);
    }

    fn prepare(
        &mut self,
        outer: Bounds<Pixels>,
        window: &Window,
        target: Option<(Arc<ChartResult>, Viewport, u64)>,
        grouped: bool,
    ) {
        self.report.outer_bounds = [
            f32::from(outer.origin.x),
            f32::from(outer.origin.y),
            f32::from(outer.size.width),
            f32::from(outer.size.height),
        ];
        let (left, top, right, bottom) = if self.workspace_style {
            let unit = crate::chart::labels::Unit::axes(
                &self.result.request.parameters,
                self.result.request.kind,
            )[1];
            let label_width = self
                .axes
                .gutter(unit, self.viewport, f32::from(outer.size.height));
            // Streaming tick labels must not shrink/grow the render target on
            // alternate frames. Reserve the widest label seen for these axes.
            self.axis_gutter = self.axis_gutter.max(label_width.clamp(48.0, 156.0));
            (
                self.axis_gutter,
                10.0,
                if self.result.request.kind == clicksync_chart::gpu::PlotKind::Histogram {
                    44.0
                } else {
                    8.0
                },
                28.0,
            )
        } else {
            (60.0, 24.0, 20.0, 40.0)
        };
        self.bounds = Bounds {
            origin: outer.origin + point(px(left), px(top)),
            size: size(
                (outer.size.width - px(left + right)).max(px(1.0)),
                (outer.size.height - px(top + bottom)).max(px(1.0)),
            ),
        };
        self.report.logical_bounds = [
            f32::from(self.bounds.origin.x),
            f32::from(self.bounds.origin.y),
            f32::from(self.bounds.size.width),
            f32::from(self.bounds.size.height),
        ];
        // Overlays inherit the primary's fitted viewport; fitting twice can drift by an ULP.
        if self.trajectory && !(grouped && self.overlay) {
            let aspect = f32::from(self.bounds.size.width) as f64
                / f32::from(self.bounds.size.height).max(1.0) as f64;
            self.fit_trajectory_aspect(aspect);
        }
        let scale = window.scale_factor();
        let width = (f32::from(self.bounds.size.width) * scale).round() as u32;
        let height = (f32::from(self.bounds.size.height) * scale).round() as u32;
        let (desired_result, desired_viewport, desired_revision) = self
            .selection
            .clone()
            .or(target)
            .unwrap_or_else(|| (self.result.clone(), self.viewport, self.viewport_revision));
        let answer = window.with_windows_graphics(|graphics| {
            if self
                .renderer
                .as_ref()
                .is_none_or(|r| r.epoch() != graphics.device_epoch)
            {
                if self.renderer.is_some() {
                    self.report.device_rebuilds += 1;
                }
                self.displayed = None;
                self.pending_presentation = None;
                self.generation = None;
                self.renderer = Some(Renderer::new(
                    graphics.device.clone(),
                    graphics.device_epoch,
                    self.limits.clone(),
                )?);
            }
            let renderer = self.renderer.as_mut().unwrap();
            let mut spec = RenderSpec::new(desired_viewport, width, height, graphics.device_epoch);
            spec.y_down = self.trajectory;
            spec.request_id = desired_revision;
            spec.radius = if self.workspace_style {
                if self.trajectory { 1.5 } else { 1.15 }
            } else {
                2.0
            } * scale;
            spec.line_width = if self.workspace_style { 1.0 } else { 1.5 } * scale;
            if self.workspace_style {
                spec.color = plot_color(self.dark, self.dataset, self.trajectory);
                spec.style_version = 1;
            } else if self.overlay {
                spec.color = [0.9, 0.3, 0.15, 0.85];
            }
            if self.pending_presentation.as_ref().is_some_and(|p| {
                !p.result.eligible_for(&desired_result.request)
                    || !same_surface(&p.layer.version.spec, &spec)
            }) {
                self.pending_presentation = None;
            }
            if let Some(layer) = renderer.tick(graphics.immediate_context)? {
                let (result, _, viewport_revision) = self
                    .generation
                    .take()
                    .ok_or_else(|| gpui::private::anyhow::anyhow!("missing generation version"))?;
                let valid = result.eligible_for(&desired_result.request)
                    && same_surface(&layer.version.spec, &spec)
                    && (self.selection.is_none() || layer.version.spec.viewport == spec.viewport);
                if valid {
                    let surface =
                        WindowsSurface::new(graphics, layer.texture())?.retain(layer.lease());
                    self.report.completed_frames += 1;
                    self.report.processed_end = layer.version.processed_end;
                    self.report.layer_end = layer.version.published_end;
                    self.report.device_epoch = graphics.device_epoch;
                    self.report.width = width;
                    self.report.height = height;
                    self.report.request_id = layer.version.spec.request_id;
                    if !grouped
                        && self
                            .displayed
                            .as_ref()
                            .is_none_or(|p| p.result.request.id != result.request.id)
                    {
                        self.hover = None;
                        self.histogram_hover = None;
                    }
                    let presentation = Presentation {
                        surface,
                        layer,
                        result,
                        viewport_revision,
                    };
                    if grouped {
                        self.pending_presentation = Some(presentation);
                    } else {
                        self.displayed = Some(presentation);
                    }
                } else {
                    self.report.discarded_layers += 1;
                }
            }
            let current = self
                .pending_presentation
                .as_ref()
                .or(self.displayed.as_ref())
                .is_some_and(|p| {
                    p.layer.version.spec == spec
                        // A group must advance data identity together, even for unchanged pixels.
                        && (!grouped || p.result.request.id == desired_result.request.id)
                });
            if !renderer.is_generating() && !current {
                match renderer.begin(
                    graphics.immediate_context,
                    desired_result.datasets[self.dataset].scene.clone(),
                    spec.clone(),
                ) {
                    Ok(()) => {
                        self.generation =
                            Some((desired_result.clone(), spec.clone(), self.viewport_revision));
                        // Submit the next layer in the same frame that publishes the previous one.
                        // A fresh generation cannot complete until a subsequent nonblocking poll.
                        let completed = renderer.tick(graphics.immediate_context)?;
                        debug_assert!(completed.is_none());
                    }
                    Err(GpuError::ResourceBusy) => {}
                    Err(error) => return Err(error.into()),
                }
            }
            self.report.received_sequence = desired_result.request.prefix.received_sequence;
            self.report.resources = Some(renderer.report());
            if renderer.is_generating() || !current {
                window.request_animation_frame();
            }
            Ok(())
        });
        if let Err(error) = answer {
            self.report.error = Some(error.to_string());
        }
    }

    pub fn paint(&mut self, window: &mut Window, cx: &mut App) {
        let Some(presentation) = &self.displayed else {
            return;
        };
        let plot = self.bounds;
        let spec = &presentation.layer.version.spec;
        let viewport = self.paint_viewport(presentation);
        let units = crate::chart::labels::Unit::axes(
            &presentation.result.request.parameters,
            presentation.result.request.kind,
        );
        if !self.overlay {
            let dataset = &presentation.result.datasets[self.dataset];
            self.axes.paint(
                axes::Spec {
                    viewport,
                    plot,
                    outer_right: px(self.report.outer_bounds[0] + self.report.outer_bounds[2]),
                    units,
                    kind: presentation.result.request.kind,
                    interval_ms: dataset.interval_band.map(|band| band[1]),
                    y_down: spec.y_down,
                    trajectory: self.trajectory,
                    workspace: self.workspace_style,
                    dark: self.dark,
                    scale_factor: window.scale_factor(),
                },
                &dataset.histogram,
                window,
                cx,
            );
        }
        for [start, end] in &presentation.result.datasets[self.dataset].gaps {
            let left = (start - viewport.origin.x.value()) / viewport.width;
            let right = (end - viewport.origin.x.value()) / viewport.width;
            if right < 0.0 || left > 1.0 {
                continue;
            }
            window.paint_quad(fill(
                Bounds {
                    origin: plot.origin + point(plot.size.width * left.max(0.0) as f32, px(0.0)),
                    size: size(
                        plot.size.width * (right.min(1.0) - left.max(0.0)).max(0.0) as f32,
                        plot.size.height,
                    ),
                },
                gpui::rgba(if self.dark { 0xe6b85022 } else { 0xb3821922 }),
            ));
        }
        let layer_view = spec.viewport;
        let surface_width = plot.size.width * (layer_view.width / viewport.width) as f32;
        let surface_height = plot.size.height * (layer_view.height / viewport.height) as f32;
        let offset_x = layer_view.origin.x.relative_to(viewport.origin.x) / viewport.width;
        let offset_y = layer_view.origin.y.relative_to(viewport.origin.y) / viewport.height;
        let surface_bounds = Bounds {
            origin: plot.origin
                + point(
                    plot.size.width * offset_x as f32,
                    if spec.y_down {
                        plot.size.height * offset_y as f32
                    } else {
                        plot.size.height * (1.0 - offset_y as f32) - surface_height
                    },
                ),
            size: size(surface_width, surface_height),
        };
        window.with_content_mask(Some(gpui::ContentMask { bounds: plot }), |window| {
            window.paint_windows_surface(surface_bounds, presentation.surface.clone());
        });
        self.report.axes_request_id = presentation.layer.version.spec.request_id;
        if !self.workspace_style {
            text(
                format!(
                    "数据 {} · 分析 {} · 图层 {} · GPU {}",
                    presentation.result.request.prefix.received_sequence,
                    presentation.layer.version.processed_end,
                    presentation.layer.version.published_end,
                    presentation.layer.version.spec.device_epoch
                ),
                point(plot.origin.x, plot.origin.y - px(22.0)),
                window,
                cx,
            );
        }
        if self.trajectory
            && presentation
                .result
                .request
                .analysis
                .as_ref()
                .is_none_or(|analysis| analysis.range.is_none())
        {
            let dataset = &presentation.result.datasets[self.dataset];
            let last = dataset
                .geometry
                .iter()
                .rev()
                .flat_map(|block| block.samples.iter().rev())
                .next();
            let project = |sample: &clicksync_chart::Sample| {
                point(
                    plot.origin.x
                        + plot.size.width
                            * (sample.point.x.relative_to(viewport.origin.x) / viewport.width)
                                as f32,
                    plot.origin.y
                        + plot.size.height
                            * spec.y_fraction(
                                sample.point.y.relative_to(viewport.origin.y) / viewport.height,
                            ) as f32,
                )
            };
            if let Some(last) = last {
                let position = project(last);
                window.with_content_mask(Some(gpui::ContentMask { bounds: plot }), |window| {
                    window.paint_quad(gpui::fill(
                        Bounds {
                            origin: position - point(px(3.0), px(3.0)),
                            size: size(px(6.0), px(6.0)),
                        },
                        rgb(if self.overlay {
                            0xef765a
                        } else {
                            crate::ui::design::Palette::for_theme(self.dark).accent
                        }),
                    ));
                });
            }
        }
        if let Some((request, points)) = &self.linked
            && *request == presentation.result.request.id
        {
            window.with_content_mask(Some(gpui::ContentMask { bounds: plot }), |window| {
                for value in points {
                    let position = point(
                        plot.origin.x
                            + plot.size.width
                                * (value.x.relative_to(viewport.origin.x) / viewport.width) as f32,
                        plot.origin.y
                            + plot.size.height
                                * spec.y_fraction(
                                    value.y.relative_to(viewport.origin.y) / viewport.height,
                                ) as f32,
                    );
                    if !self.trajectory {
                        window.paint_quad(fill(
                            Bounds {
                                origin: point(position.x, plot.origin.y),
                                size: size(px(1.0), plot.size.height),
                            },
                            gpui::rgba(if self.dark { 0xffffff55 } else { 0x22222255 }),
                        ));
                    }
                    window.paint_quad(fill(
                        Bounds {
                            origin: position - point(px(4.0), px(4.0)),
                            size: size(px(8.0), px(8.0)),
                        },
                        rgb(if self.dark { 0xffffff } else { 0x111111 }),
                    ));
                }
            });
        }
    }

    /// Read only the displayed hit; the workspace owns its fixed readout location.
    pub(crate) fn hover_readout(&self) -> Vec<(&'static str, String)> {
        let Some(presentation) = &self.displayed else {
            return Vec::new();
        };
        let units = crate::chart::labels::Unit::axes(
            &presentation.result.request.parameters,
            presentation.result.request.kind,
        );
        let readout = self.hover;
        let mut rows = Vec::new();
        if presentation.result.datasets.len() > 1
            && (readout.is_some() || self.histogram_hover.is_some())
        {
            rows.push((
                if self.english { "Dataset" } else { "数据组" },
                ["A", "B", "C"][self.dataset.min(2)].to_owned(),
            ));
        }
        if let Some(bin) = self.histogram_hover {
            rows.push((
                if self.english {
                    "Value"
                } else {
                    "数值区间"
                },
                format!(
                    "{} {}",
                    units[0].histogram_range(
                        presentation.result.request.parameters.metric,
                        bin,
                        presentation.result.datasets[self.dataset].histogram.last() == Some(&bin)
                    ),
                    units[0].symbol()
                ),
            ));
            rows.push((
                if self.english { "Samples" } else { "样本数" },
                bin.count.to_string(),
            ));
            if units[1] == crate::chart::labels::Unit::Percent {
                rows.push((
                    if self.english { "Share" } else { "占比" },
                    format!("{:.2}%", bin.value),
                ));
            }
        } else if presentation.result.request.kind != clicksync_chart::gpu::PlotKind::Histogram
            && let Some((index, point)) = readout
        {
            rows.push((
                if self.english {
                    "Report"
                } else {
                    "报告序号"
                },
                format!("#{index}"),
            ));
            rows.push((
                if self.trajectory {
                    "X"
                } else if self.english {
                    "Time"
                } else {
                    "时间"
                },
                format!("{} {}", units[0].readout(point.x), units[0].symbol()),
            ));
            rows.push((
                if self.trajectory {
                    "Y"
                } else if self.english {
                    "Value"
                } else {
                    "数值"
                },
                format!("{} {}", units[1].readout(point.y), units[1].symbol()),
            ));
        }
        rows
    }
}

pub(crate) fn workspace_plot_bounds(outer: Bounds<Pixels>) -> Bounds<Pixels> {
    Bounds {
        origin: outer.origin + point(px(48.0), px(10.0)),
        size: size(
            (outer.size.width - px(56.0)).max(px(1.0)),
            (outer.size.height - px(38.0)).max(px(1.0)),
        ),
    }
}

fn equal_scale(mut view: Viewport, aspect: f64) -> Viewport {
    let width = view.width.max(view.height * aspect);
    let height = view.height.max(view.width / aspect);
    view.origin.x = shift(view.origin.x, -(width - view.width) / 2.0);
    view.origin.y = shift(view.origin.y, -(height - view.height) / 2.0);
    view.width = width;
    view.height = height;
    view
}

fn same_surface(a: &RenderSpec, b: &RenderSpec) -> bool {
    a.width == b.width
        && a.height == b.height
        && a.device_epoch == b.device_epoch
        && a.y_down == b.y_down
        && a.radius == b.radius
        && a.line_width == b.line_width
        && a.color == b.color
        && a.clip == b.clip
        && a.style_version == b.style_version
}

fn stable_value_axis(current: Viewport, mut required: Viewport) -> Viewport {
    let bottom = required.origin.y.relative_to(current.origin.y);
    let top = bottom + required.height;
    let padding = current.height.max(required.height) * 0.1;
    let bottom = if bottom < 0.0 {
        let expanded = bottom - padding;
        if current.origin.y.value() >= 0.0 && required.origin.y.value() >= 0.0 {
            expanded.max(-current.origin.y.value())
        } else {
            expanded
        }
    } else {
        0.0
    };
    let top = if top > current.height {
        top + padding
    } else {
        current.height
    };
    // Keep X following live data; Y only expands with room for boundary jitter.
    required.origin.y = shift_view(current.origin.y, bottom);
    required.height = top - bottom;
    required
}

fn stable_trajectory(current: Viewport, required: Viewport) -> Viewport {
    let x = required.origin.x.relative_to(current.origin.x);
    let y = required.origin.y.relative_to(current.origin.y);
    if x >= 0.0
        && y >= 0.0
        && x + required.width <= current.width
        && y + required.height <= current.height
    {
        return current;
    }
    let left = x.min(0.0);
    let top = y.min(0.0);
    let width = current.width.max(x + required.width) - left;
    let height = current.height.max(y + required.height) - top;
    // Grow only as far as the full trajectory requires, without doubling jumps.
    let scale = (width / current.width).max(height / current.height);
    Viewport {
        origin: clicksync_chart::Point {
            x: shift_view(
                current.origin.x,
                left - (current.width * scale - width) / 2.0,
            ),
            y: shift_view(
                current.origin.y,
                top - (current.height * scale - height) / 2.0,
            ),
        },
        width: current.width * scale,
        height: current.height * scale,
    }
}

fn same_visible_geometry(
    a: &crate::chart::DatasetChart,
    b: &crate::chart::DatasetChart,
    viewport: Viewport,
) -> bool {
    if a.histogram != b.histogram {
        return false;
    }
    let visible = |block: &&Arc<clicksync_chart::GeometryBlock>| {
        let x = block.origin.x.relative_to(viewport.origin.x);
        x + block.bounds[1] >= 0.0 && x + block.bounds[0] <= viewport.width
    };
    let mut left = a.geometry.iter().filter(visible);
    let mut right = b.geometry.iter().filter(visible);
    loop {
        match (left.next(), right.next()) {
            (None, None) => return true,
            (Some(a), Some(b))
                if Arc::ptr_eq(a, b)
                    || (a.id == b.id
                        && a.content_version == b.content_version
                        && a.origin == b.origin
                        && a.source_range == b.source_range) => {}
            _ => return false,
        }
    }
}

fn shift(coordinate: clicksync_chart::Coordinate, offset: f64) -> clicksync_chart::Coordinate {
    use clicksync_chart::Coordinate;
    match coordinate {
        Coordinate::Integer(value) => {
            Coordinate::Integer(value.saturating_add(offset.round() as i64))
        }
        Coordinate::Time { ticks, frequency } => Coordinate::Time {
            ticks: (ticks as i128 + (offset * frequency as f64 / 1000.0).round() as i128)
                .clamp(0, u64::MAX as i128) as u64,
            frequency,
        },
        Coordinate::Scalar(value) => Coordinate::Scalar(value + offset),
    }
}

pub(crate) fn shift_view(
    coordinate: clicksync_chart::Coordinate,
    offset: f64,
) -> clicksync_chart::Coordinate {
    use clicksync_chart::Coordinate;
    if offset == 0.0 {
        return coordinate;
    }
    // Viewports may sit between integer samples; retain the integer domain for huge origins.
    match coordinate {
        Coordinate::Integer(value) if value.unsigned_abs() < (1_u64 << 52) => {
            Coordinate::Scalar(value as f64 + offset)
        }
        _ => shift(coordinate, offset),
    }
}

fn text(value: String, position: Point<Pixels>, window: &mut Window, cx: &mut App) {
    chart_text(value, position, window, cx, false, false);
}

pub(crate) fn dataset_color(dark: bool, side: usize) -> u32 {
    match side {
        1 => 0xef765a,
        2 => 0x59b38f,
        _ => crate::ui::design::Palette::for_theme(dark).accent,
    }
}

pub(crate) fn plot_color(dark: bool, side: usize, trajectory: bool) -> [f32; 4] {
    let color = dataset_color(dark, side);
    [
        (color >> 16) as f32 / 255.0,
        ((color >> 8) & 255) as f32 / 255.0,
        (color & 255) as f32 / 255.0,
        if trajectory { 0.72 } else { 0.66 },
    ]
}

fn chart_text(
    value: String,
    position: Point<Pixels>,
    window: &mut Window,
    cx: &mut App,
    workspace: bool,
    dark: bool,
) {
    chart_text_aligned(value, position, 0.0, window, cx, workspace, dark);
}

fn chart_text_aligned(
    value: String,
    mut position: Point<Pixels>,
    align: f32,
    window: &mut Window,
    cx: &mut App,
    workspace: bool,
    dark: bool,
) {
    let line = axes::shape_label(value, workspace, dark, window);
    position.x -= line.width * align;
    if let Err(error) = line.paint(position, px(16.0), window, cx) {
        eprintln!("chart labels: {error}");
    }
}
