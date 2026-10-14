use crate::backend::{Backend, CapturePhase, SessionPrefix};
mod bounds;
pub use bounds::BoundsWork;
mod link;
pub use link::LinkedResult;
pub(crate) use link::histogram_bin;
pub mod labels;
use clicksync_chart::{
    ChartEngine, ChartParameters, DatasetId, ExactQueryResult, FixedPrefix, Integrity,
    gpu::{PlotKind, PreparedScene},
    query,
};
use clicksync_core::{SessionEvent, SessionSnapshot, SessionStatus};
use std::{
    ops::Range,
    sync::{
        Arc, Condvar, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

const ANALYSIS_BLOCK: usize = 8192;

#[cfg(test)]
#[path = "chart/bounds_tests.rs"]
mod bounds_tests;
#[cfg(test)]
#[path = "chart/multiview_tests.rs"]
mod multiview_tests;

pub mod trial;

#[derive(Clone)]
pub struct ChartRequest {
    pub id: u64,
    pub source_version: u64,
    pub prefix: Arc<SessionPrefix>,
    pub parameters: ChartParameters,
    pub dataset_cpi: Vec<(u32, Option<f64>)>,
    pub secondary_metric: Option<clicksync_chart::Metric>,
    pub kind: PlotKind,
    pub view: ViewMode,
    pub analysis: Option<crate::product::performance_analysis::PlotAnalysis>,
}

impl ChartRequest {
    pub(crate) fn trend_metrics(&self) -> impl Iterator<Item = clicksync_chart::Metric> {
        use clicksync_chart::Metric;
        let enabled = self.analysis.as_ref().is_some_and(|a| a.options.trend)
            && !matches!(self.kind, PlotKind::Trajectory | PlotKind::Histogram);
        [Some(self.parameters.metric), self.secondary_metric]
            .into_iter()
            .flatten()
            .filter_map(move |metric| match (enabled, metric) {
                (true, Metric::Count(axis)) => Some(Metric::CountTrend(axis)),
                (true, Metric::Interval) => Some(Metric::IntervalTrend),
                _ => None,
            })
    }

    pub fn parameters_for(&self, key: u32) -> ChartParameters {
        let mut parameters = self.parameters.clone();
        if let Some((_, cpi)) = self.dataset_cpi.iter().find(|(id, _)| *id == key) {
            parameters.cpi = *cpi;
        }
        parameters
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ViewMode {
    Follow { window_ms: f64 },
    Fixed(clicksync_chart::gpu::Viewport),
    FullSession,
}

#[derive(Clone)]
pub struct DatasetChart {
    pub device_key: u32,
    pub scene: Arc<PreparedScene>,
    pub work: clicksync_chart::Work,
    pub geometry: Vec<Arc<clicksync_chart::GeometryBlock>>,
    pub hit_index: Arc<clicksync_chart::query::hit::HitIndex>,
    pub histogram: Vec<clicksync_chart::analysis::histogram::HistogramBin>,
    pub plot_origin: clicksync_chart::Point,
    pub plot_bounds: [f64; 4],
    pub interval_band: Option<[f64; 3]>,
    pub bounds_work: BoundsWork,
    pub gaps: Vec<[f64; 2]>,
    pub distribution: Option<clicksync_chart::analysis::statistics::Distribution>,
    link_index: Option<Arc<link::LinkIndex>>,
}

pub struct ChartResult {
    pub request: ChartRequest,
    pub datasets: Vec<DatasetChart>,
    pub byte_budget: Option<Arc<clicksync_core::SharedByteBudget>>,
    managed: Vec<Arc<clicksync_core::BudgetReservation>>,
}

impl ChartResult {
    /// View policy changes presentation only; retain the immutable payload and its leases.
    pub(crate) fn with_view(&self, id: u64, view: ViewMode) -> Self {
        Self {
            request: ChartRequest {
                id,
                view,
                ..self.request.clone()
            },
            datasets: self.datasets.clone(),
            byte_budget: self.byte_budget.clone(),
            managed: self.managed.clone(),
        }
    }

    pub fn viewport(&self, device_key: u32) -> Result<clicksync_chart::gpu::Viewport, String> {
        self.viewport_for(device_key, self.request.view)
    }

    fn viewport_for(
        &self,
        device_key: u32,
        mode: ViewMode,
    ) -> Result<clicksync_chart::gpu::Viewport, String> {
        use clicksync_chart::{Point, gpu::Viewport};
        if let ViewMode::Fixed(viewport) = mode {
            return Ok(viewport);
        }
        let dataset = &self
            .datasets
            .iter()
            .find(|d| d.device_key == device_key)
            .ok_or("unknown chart dataset")?;
        let origin = dataset.plot_origin;
        let mut bounds = [0.0_f64; 4];
        for dataset in &self.datasets {
            let x = dataset.plot_origin.x.relative_to(origin.x);
            let y = dataset.plot_origin.y.relative_to(origin.y);
            bounds[0] = bounds[0].min(x + dataset.plot_bounds[0]);
            bounds[1] = bounds[1].max(x + dataset.plot_bounds[1]);
            bounds[2] = bounds[2].min(y + dataset.plot_bounds[2]);
            bounds[3] = bounds[3].max(y + dataset.plot_bounds[3]);
        }
        let mut width = (bounds[1] - bounds[0]).max(1.0);
        let height = (bounds[3] - bounds[2]).max(1.0);
        if let ViewMode::Follow { window_ms } = mode {
            bounds[0] = (bounds[1] - window_ms).max(bounds[0]);
            width = window_ms;
        }
        let (x, width) = padded_axis(origin.x, bounds[0], bounds[1], width, 0.02);
        let (y, height) = padded_axis(origin.y, bounds[2], bounds[3], height, 0.05);
        Ok(Viewport {
            origin: Point { x, y },
            width,
            height,
        })
    }

    pub fn workspace_viewport(
        &self,
        device_key: u32,
    ) -> Result<clicksync_chart::gpu::Viewport, String> {
        self.workspace_viewport_for(device_key, self.request.view, true)
    }

    pub fn full_workspace_viewport(
        &self,
        device_key: u32,
    ) -> Result<clicksync_chart::gpu::Viewport, String> {
        self.workspace_viewport_for(device_key, ViewMode::FullSession, false)
    }

    fn workspace_viewport_for(
        &self,
        device_key: u32,
        mode: ViewMode,
        focus: bool,
    ) -> Result<clicksync_chart::gpu::Viewport, String> {
        let mut viewport = self.viewport_for(device_key, mode)?;
        if matches!(mode, ViewMode::Fixed(_)) {
            return Ok(viewport);
        }
        if matches!(mode, ViewMode::Follow { .. }) {
            let full = self.viewport_for(device_key, ViewMode::FullSession)?;
            if full.width < viewport.width {
                viewport.origin.x = full.origin.x;
                viewport.width = full.width;
            }
        }
        if self.request.kind == PlotKind::Trajectory {
            return Ok(viewport);
        }
        if focus
            && self.request.kind != PlotKind::Histogram
            && self
                .datasets
                .iter()
                .filter(|dataset| !dataset.geometry.is_empty())
                .all(|dataset| dataset.interval_band.is_some())
        {
            let mut band: Option<[f64; 2]> = None;
            for values in self
                .datasets
                .iter()
                .filter_map(|dataset| dataset.interval_band)
            {
                let padding = (values[2] - values[0]).max(values[1] * 0.2).max(0.002) * 0.15;
                let low = (values[0] - padding).max(0.0);
                let high = values[2] + padding;
                band = Some(band.map_or([low, high], |old| [old[0].min(low), old[1].max(high)]));
            }
            if let Some([low, high]) = band {
                viewport.origin.y = clicksync_chart::Coordinate::Scalar(low);
                viewport.height = high - low;
                return Ok(viewport);
            }
        }
        let origin = self
            .datasets
            .iter()
            .find(|d| d.device_key == device_key)
            .ok_or("unknown chart dataset")?
            .plot_origin;
        let mut minimum = 0.0_f64;
        let mut maximum = 0.0_f64;
        for dataset in &self.datasets {
            let offset = dataset.plot_origin.y.relative_to(origin.y);
            minimum = minimum.min(offset + dataset.plot_bounds[2]);
            maximum = maximum.max(offset + dataset.plot_bounds[3]);
        }
        let floor = match self.request.parameters.metric {
            clicksync_chart::Metric::Interval | clicksync_chart::Metric::IntervalTrend => 0.5,
            clicksync_chart::Metric::Velocity(_) | clicksync_chart::Metric::PathSpeed => 0.01,
            _ => 1.0,
        };
        let span = (maximum - minimum)
            .max(maximum.abs().max(minimum.abs()) * 0.1)
            .max(floor);
        (viewport.origin.y, viewport.height) = padded_axis(origin.y, minimum, maximum, span, 0.05);
        Ok(viewport)
    }

    pub fn eligible_for(&self, target: &ChartRequest) -> bool {
        same_presentation(&self.request, target)
            && self.request.prefix.received_sequence <= target.prefix.received_sequence
            && self.datasets.iter().all(|d| {
                target.prefix.datasets.iter().any(|(key, snapshot)| {
                    *key == d.device_key
                        && snapshot.generation() == d.scene.state.data_version.unwrap().generation
                        && snapshot.event_count() >= d.scene.state.processed_end
                        && fixed(*key, snapshot, target.prefix.complete)
                            .version()
                            .integrity
                            == d.scene.state.data_version.unwrap().integrity
                })
            })
    }

    pub fn query(&self, device_key: u32, range: Range<usize>) -> Result<ExactQueryResult, String> {
        let snapshot = self.snapshot(device_key)?;
        query(
            fixed(device_key, snapshot, self.request.prefix.complete),
            range,
            &self.request.parameters_for(device_key),
        )
        .map_err(|error| error.to_string())
    }
    pub fn query_secondary(
        &self,
        device_key: u32,
        range: Range<usize>,
    ) -> Result<Option<ExactQueryResult>, String> {
        let Some(metric) = self.request.secondary_metric else {
            return Ok(None);
        };
        let mut parameters = self.request.parameters_for(device_key);
        parameters.metric = metric;
        query(
            fixed(
                device_key,
                self.snapshot(device_key)?,
                self.request.prefix.complete,
            ),
            range,
            &parameters,
        )
        .map(Some)
        .map_err(|e| e.to_string())
    }

    pub fn snapshot(&self, device_key: u32) -> Result<&SessionSnapshot, String> {
        self.request
            .prefix
            .datasets
            .iter()
            .find(|(key, _)| *key == device_key)
            .map(|(_, snapshot)| &**snapshot)
            .ok_or_else(|| "unknown chart dataset".into())
    }

    /// Export readers keep the request's original fixed prefix even after reset.
    pub fn export_events(
        &self,
        device_key: u32,
    ) -> Result<impl ExactSizeIterator<Item = SessionEvent> + '_, String> {
        Ok(self.snapshot(device_key)?.events())
    }
}

fn fixed(key: u32, snapshot: &SessionSnapshot, complete: bool) -> FixedPrefix<'_> {
    let prefix = FixedPrefix::new(DatasetId(key as u64), snapshot);
    if !complete && prefix.version().integrity == Integrity::Complete {
        prefix.with_integrity(Integrity::Incomplete)
    } else {
        prefix
    }
}

/// Stop acknowledgment is followed by owner drain before freezing the final
/// prefix. Capture cancellation/control never uses the chart queue.
pub fn stop_and_freeze(backend: &Backend, timeout: Duration) -> Result<Arc<SessionPrefix>, String> {
    let started = Instant::now();
    let ack = backend.execute(clicksync_core::protocol::ControlCommand::Stop, timeout)?;
    loop {
        let remaining = timeout
            .checked_sub(started.elapsed())
            .ok_or("capture drain deadline exceeded")?;
        let prefix = backend.capture_prefix(remaining)?;
        if prefix.generation != ack.generation {
            return Err("capture generation changed during drain".into());
        }
        if prefix.received_sequence == ack.queued
            && matches!(prefix.phase, CapturePhase::Stopped | CapturePhase::Faulted)
            && prefix
                .datasets
                .iter()
                .all(|(_, snapshot)| snapshot.status() == SessionStatus::Stopped)
        {
            return Ok(prefix);
        }
        thread::sleep(Duration::from_millis(2));
    }
}

type ChartAnswer = Result<Arc<ChartResult>, String>;

#[derive(Clone, Copy, Debug, Default, serde::Serialize)]
pub struct SchedulerReport {
    pub active_requests: usize,
    pub pending_requests: usize,
    pub merged_targets: u64,
    pub processed_targets: u64,
    pub discarded_targets: u64,
    pub view_steps: [u64; trial::MAX_DEMANDS],
    pub view_completions: [u64; trial::MAX_DEMANDS],
    pub max_step_us: [u64; trial::MAX_DEMANDS],
    pub max_assembly_us: [u64; trial::MAX_DEMANDS],
    pub cached_value_bytes: [usize; trial::MAX_DEMANDS],
    pub cached_engines: [usize; trial::MAX_DEMANDS],
    pub peak_cached_value_bytes: usize,
}

#[derive(Default)]
struct Mailbox {
    target: Option<ChartRequest>,
    report: SchedulerReport,
    demands: [DemandMailbox; trial::MAX_DEMANDS],
}

#[derive(Default)]
struct DemandMailbox {
    owner: u64,
    revision: u64,
    target: Option<ChartRequest>,
    pending: Option<ChartRequest>,
    result: Option<ChartAnswer>,
    quota: usize,
}

pub struct ChartWorker {
    mailbox: Arc<(Mutex<Mailbox>, Condvar)>,
    cancelled: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl ChartWorker {
    pub fn spawn() -> Self {
        Self::spawn_with_budget(clicksync_core::SharedByteBudget::new(
            clicksync_core::SessionConfig::default().managed_memory_budget_bytes,
        ))
    }
    pub fn spawn_with_budget(budget: Arc<clicksync_core::SharedByteBudget>) -> Self {
        let mailbox = Arc::new((Mutex::new(Mailbox::default()), Condvar::new()));
        let cancelled = Arc::new(AtomicBool::new(false));
        let thread = {
            let mailbox = mailbox.clone();
            let cancelled = cancelled.clone();
            thread::spawn(move || {
                let mut scheduler = trial::CooperativeTrial::with_budget(budget);
                let mut revisions = [0; trial::MAX_DEMANDS];
                while !cancelled.load(Ordering::Acquire) {
                    {
                        let (lock, wake) = &*mailbox;
                        let mut state = lock.lock().expect("chart mailbox");
                        while scheduler.is_idle()
                            && state.demands.iter().all(|d| d.pending.is_none())
                            && state
                                .demands
                                .iter()
                                .enumerate()
                                .all(|(slot, d)| revisions[slot] == d.revision)
                            && !cancelled.load(Ordering::Acquire)
                        {
                            state = wake.wait(state).expect("chart mailbox wake");
                        }
                        if cancelled.load(Ordering::Acquire) {
                            break;
                        }
                        let next_slot = scheduler.next_slot();
                        for (slot, demand) in state.demands.iter_mut().enumerate() {
                            if demand.quota > 0 {
                                scheduler
                                    .set_quota(slot, demand.quota)
                                    .expect("validated chart quota");
                            }
                            if revisions[slot] != demand.revision {
                                scheduler.remove(slot).expect("valid chart slot");
                                revisions[slot] = demand.revision;
                            }
                            if slot == next_slot
                                && let Some(target) = &demand.target
                            {
                                scheduler.receive_latest(slot, target, &mut demand.pending);
                            }
                        }
                        state.report.active_requests = usize::from(!scheduler.is_idle());
                        state.report.pending_requests = scheduler.pending_count();
                    }
                    let completion = scheduler.step_completion(&cancelled);
                    let mut state = mailbox.0.lock().expect("chart mailbox");
                    state.report.active_requests = 0;
                    state.report.view_steps = scheduler.report().steps;
                    state.report.view_completions = scheduler.report().completed;
                    state.report.max_step_us = scheduler.report().max_step_us;
                    state.report.max_assembly_us = scheduler.report().max_assembly_us;
                    (state.report.cached_value_bytes, state.report.cached_engines) =
                        scheduler.cached_values();
                    state.report.peak_cached_value_bytes = state
                        .report
                        .peak_cached_value_bytes
                        .max(state.report.cached_value_bytes.iter().sum());
                    state.report.pending_requests = scheduler.pending_count()
                        + state
                            .demands
                            .iter()
                            .filter(|demand| demand.pending.is_some())
                            .count();
                    if let Some((slot, request, answer)) = completion {
                        state.report.processed_targets += 1;
                        let demand = &mut state.demands[slot];
                        if !cancelled.load(Ordering::Acquire)
                            && revisions[slot] == demand.revision
                            && demand
                                .target
                                .as_ref()
                                .is_some_and(|target| same_presentation(&request, target))
                        {
                            demand.result = Some(answer);
                        } else {
                            state.report.discarded_targets += 1;
                        }
                    }
                }
            })
        };
        Self {
            mailbox,
            cancelled,
            thread: Some(thread),
        }
    }

    pub fn submit(&self, request: ChartRequest) -> Result<(), String> {
        self.submit_for(0, request)
    }

    pub fn submit_for(&self, owner: u64, request: ChartRequest) -> Result<(), String> {
        self.submit_view(owner, 0, request)
    }

    pub fn submit_view(
        &self,
        owner: u64,
        slot: usize,
        request: ChartRequest,
    ) -> Result<(), String> {
        if self.cancelled.load(Ordering::Acquire) {
            return Err("chart worker cancelled".into());
        }
        match request.view {
            ViewMode::Follow { window_ms } if !window_ms.is_finite() || window_ms <= 0.0 => {
                return Err("invalid follow window".into());
            }
            ViewMode::Fixed(view)
                if !view.width.is_finite()
                    || view.width <= 0.0
                    || !view.height.is_finite()
                    || view.height <= 0.0 =>
            {
                return Err("invalid fixed viewport".into());
            }
            _ => {}
        }
        let mut state = self.mailbox.0.lock().expect("chart mailbox");
        let demand = state
            .demands
            .get_mut(slot)
            .ok_or("chart view admission limit")?;
        if let Some(target) = &demand.target {
            if request.id <= target.id {
                return Err("regressed chart request".into());
            }
            if request.source_version < target.source_version
                || (request.source_version == target.source_version
                    && request.prefix.generation < target.prefix.generation)
            {
                return Err("old chart generation".into());
            }
            if request.source_version == target.source_version
                && request.prefix.generation == target.prefix.generation
                && request.prefix.received_sequence < target.prefix.received_sequence
            {
                return Err("regressed chart prefix".into());
            }
        }
        let merged = demand.pending.is_some();
        if demand.result.as_ref().is_some_and(|answer| {
            demand.owner != owner || answer.as_ref().is_ok_and(|r| !r.eligible_for(&request))
        }) {
            demand.result = None;
        }
        if demand.owner != owner {
            demand.revision += 1;
        }
        demand.target = Some(request.clone());
        demand.owner = owner;
        demand.pending = Some(request);
        state.report.merged_targets += u64::from(merged);
        state.report.pending_requests =
            state.demands.iter().filter(|d| d.pending.is_some()).count();
        self.mailbox.1.notify_one();
        Ok(())
    }

    /// Retire one view without stopping the application worker.
    pub fn remove_view(&self, slot: usize) {
        let mut state = self.mailbox.0.lock().expect("chart mailbox");
        if let Some(demand) = state.demands.get_mut(slot) {
            let revision = demand.revision + 1;
            *demand = DemandMailbox {
                revision,
                ..Default::default()
            };
        }
        state.report.pending_requests = state
            .demands
            .iter()
            .filter(|demand| demand.pending.is_some())
            .count();
        self.mailbox.1.notify_one();
    }

    pub fn take_result(&self) -> Option<Result<Arc<ChartResult>, String>> {
        self.take_completion().map(|(_, answer)| answer)
    }

    /// The owner is captured when submitted, including for failed computation.
    pub fn take_completion(&self) -> Option<(u64, ChartAnswer)> {
        self.take_view_completion(0)
    }

    pub fn take_view_completion(&self, slot: usize) -> Option<(u64, ChartAnswer)> {
        let mut state = self.mailbox.0.lock().expect("chart mailbox");
        let demand = state.demands.get_mut(slot)?;
        Some((demand.owner, demand.result.take()?))
    }

    #[cfg(test)]
    fn set_view_quota(&self, slot: usize, quota: usize) {
        self.mailbox.0.lock().unwrap().demands[slot].quota = quota;
    }

    pub fn report(&self) -> SchedulerReport {
        self.mailbox.0.lock().expect("chart mailbox").report
    }

    pub fn cancel(&self) {
        self.cancelled.store(true, Ordering::Release);
        self.mailbox.1.notify_all();
    }
    pub fn shutdown(&mut self) {
        self.cancel();
        if let Some(thread) = self.thread.take()
            && let Err(error) = thread.join()
        {
            eprintln!("chart worker panicked: {error:?}");
        }
    }
}

impl Drop for ChartWorker {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn same_presentation(a: &ChartRequest, b: &ChartRequest) -> bool {
    a.source_version == b.source_version
        && a.prefix.generation == b.prefix.generation
        && a.prefix.datasets.len() == b.prefix.datasets.len()
        && a.prefix
            .datasets
            .iter()
            .zip(&b.prefix.datasets)
            .all(|((ka, sa), (kb, sb))| ka == kb && sa.generation() == sb.generation())
        && a.parameters == b.parameters
        && a.dataset_cpi == b.dataset_cpi
        && a.secondary_metric == b.secondary_metric
        && a.kind == b.kind
        && a.view == b.view
        && a.analysis == b.analysis
}

fn padded_axis(
    origin: clicksync_chart::Coordinate,
    minimum: f64,
    maximum: f64,
    span: f64,
    padding: f64,
) -> (clicksync_chart::Coordinate, f64) {
    let start = shifted(origin, minimum - span * padding);
    // Integer and tick origins quantize padding; cover the upper bound after that shift.
    let extent =
        (span * (1.0 + 2.0 * padding)).max(origin.relative_to(start) + maximum + span * padding);
    (start, extent)
}

fn shifted(coordinate: clicksync_chart::Coordinate, offset: f64) -> clicksync_chart::Coordinate {
    use clicksync_chart::Coordinate;
    match coordinate {
        Coordinate::Integer(value) => {
            Coordinate::Integer(value.saturating_add(offset.floor() as i64))
        }
        Coordinate::Time { ticks, frequency } => Coordinate::Time {
            ticks: (ticks as i128 + (offset * frequency as f64 / 1000.0).floor() as i128)
                .clamp(0, u64::MAX as i128) as u64,
            frequency,
        },
        Coordinate::Scalar(value) => Coordinate::Scalar(value + offset),
    }
}

type EngineIdentity = (
    u64,
    u64,
    ChartParameters,
    Vec<(u32, Option<f64>)>,
    Option<clicksync_chart::Metric>,
    PlotKind,
);
type CachedDataset = (
    u32,
    ChartEngine,
    Option<Arc<PreparedScene>>,
    Option<Arc<clicksync_chart::query::hit::HitIndex>>,
);

#[derive(Default)]
struct EngineCache {
    identity: Option<EngineIdentity>,
    datasets: Vec<CachedDataset>,
    secondary: Vec<(u32, SecondaryCache)>,
    trends: Vec<((u32, clicksync_chart::Metric), SecondaryCache)>,
    bounds: std::collections::HashMap<u32, bounds::BoundsCache>,
    analysis_scenes: std::collections::HashMap<u32, AnalysisCache>,
    statistics: std::collections::HashMap<u32, CachedStatistics>,
    scene_leases: std::collections::HashMap<usize, SceneBlockLease>,
    leased_analysis: Option<Option<crate::product::performance_analysis::PlotAnalysis>>,
    managed: Vec<Arc<clicksync_core::BudgetReservation>>,
    managed_bytes: usize,
}

struct CachedStatistics {
    version: clicksync_chart::DataVersion,
    range: Option<(f64, f64)>,
    distribution: Option<clicksync_chart::analysis::statistics::Distribution>,
}

struct SceneBlockLease {
    geometry: Arc<clicksync_chart::GeometryBlock>,
    lease: Arc<clicksync_core::BudgetReservation>,
}

struct SecondaryCache {
    engine: ChartEngine,
    scene: Option<PreparedScene>,
    tagged: Vec<Arc<clicksync_chart::gpu::PreparedBlock>>,
}

impl SecondaryCache {
    fn new(parameters: ChartParameters) -> Result<Self, String> {
        Ok(Self {
            engine: ChartEngine::new(parameters, ANALYSIS_BLOCK).map_err(|e| e.to_string())?,
            scene: None,
            tagged: Vec::new(),
        })
    }

    fn scene(
        &mut self,
        kind: PlotKind,
        tag: u32,
    ) -> Result<Vec<Arc<clicksync_chart::gpu::PreparedBlock>>, String> {
        let scene = PreparedScene::from_engine_reusing(&self.engine, kind, self.scene.as_ref())
            .map_err(|e| e.to_string())?;
        let tagged = scene
            .blocks
            .iter()
            .map(|block| {
                let mut key = block.key;
                key.kind |= tag;
                self.tagged
                    .get(block.key.id)
                    .filter(|old| {
                        old.key == key
                            && old.content_version == block.content_version
                            && old.origin == block.origin
                    })
                    .cloned()
                    .unwrap_or_else(|| {
                        let mut copy = (**block).clone();
                        copy.key = key;
                        Arc::new(copy)
                    })
            })
            .collect::<Vec<_>>();
        self.scene = Some(scene);
        self.tagged = tagged.clone();
        Ok(tagged)
    }
}

impl EngineCache {
    fn analyze(
        &mut self,
        request: ChartRequest,
        cancelled: &AtomicBool,
        mailbox: &Arc<(Mutex<Mailbox>, Condvar)>,
    ) -> Result<ChartResult, String> {
        let identity = (
            request.source_version,
            request.prefix.generation,
            request.parameters.clone(),
            request.dataset_cpi.clone(),
            request.secondary_metric,
            request.kind,
        );
        if self.identity.as_ref() != Some(&identity) {
            self.datasets.clear();
            self.secondary.clear();
            self.trends.clear();
            self.bounds.clear();
            self.analysis_scenes.clear();
            self.statistics.clear();
            self.identity = Some(identity);
        }
        let valid = || {
            !cancelled.load(Ordering::Acquire)
                && mailbox
                    .0
                    .lock()
                    .expect("chart mailbox")
                    .target
                    .as_ref()
                    .is_some_and(|target| same_presentation(&request, target))
        };
        let mut datasets = Vec::with_capacity(request.prefix.datasets.len());
        for (key, snapshot) in &request.prefix.datasets {
            if !self.datasets.iter().any(|(id, _, _, _)| id == key) {
                self.datasets.push((
                    *key,
                    ChartEngine::new(request.parameters_for(*key), ANALYSIS_BLOCK)
                        .map_err(|e| e.to_string())?,
                    None,
                    None,
                ));
            }
            let (_, engine, previous, previous_hit) = self
                .datasets
                .iter_mut()
                .find(|(id, _, _, _)| id == key)
                .unwrap();
            let prefix = fixed(*key, snapshot, request.prefix.complete);
            let mut end = engine.state().processed_end;
            let mut work = clicksync_chart::Work::default();
            while end < prefix.len() {
                if !valid() {
                    return Err("chart target superseded".into());
                }
                end = (end + ANALYSIS_BLOCK).min(prefix.len());
                let delta = engine
                    .advance(prefix.with_end(end).map_err(|e| e.to_string())?)
                    .map_err(|e| e.to_string())?;
                add_work(&mut work, &delta.work);
            }
            if !valid() {
                return Err("chart target superseded".into());
            }
            let delta = if matches!(
                request.prefix.phase,
                CapturePhase::Stopped | CapturePhase::Faulted
            ) && snapshot.status() == SessionStatus::Stopped
            {
                engine.finish(prefix)
            } else {
                engine.advance(prefix)
            }
            .map_err(|e| e.to_string())?;
            add_work(&mut work, &delta.work);
            let mut scene =
                PreparedScene::from_engine_reusing(engine, request.kind, previous.as_deref())
                    .map_err(|e| e.to_string())?;
            let mut geometry = engine.geometry().blocks().to_vec();
            if let Some(metric) = request.secondary_metric {
                if !self.secondary.iter().any(|(id, _)| id == key) {
                    let mut parameters = request.parameters_for(*key);
                    parameters.metric = metric;
                    self.secondary
                        .push((*key, SecondaryCache::new(parameters)?));
                }
                let (_, cached) = self.secondary.iter_mut().find(|(id, _)| id == key).unwrap();
                let secondary = &mut cached.engine;
                let mut end = secondary.state().processed_end;
                while end < prefix.len() {
                    if !valid() {
                        return Err("chart target superseded".into());
                    }
                    end = (end + ANALYSIS_BLOCK).min(prefix.len());
                    let delta = secondary
                        .advance(prefix.with_end(end).map_err(|e| e.to_string())?)
                        .map_err(|e| e.to_string())?;
                    add_work(&mut work, &delta.work);
                }
                if !valid() {
                    return Err("chart target superseded".into());
                }
                let delta = if matches!(
                    request.prefix.phase,
                    CapturePhase::Stopped | CapturePhase::Faulted
                ) && snapshot.status() == SessionStatus::Stopped
                {
                    secondary.finish(prefix)
                } else {
                    secondary.advance(prefix)
                }
                .map_err(|e| e.to_string())?;
                add_work(&mut work, &delta.work);
                geometry.extend(secondary.geometry().blocks().iter().cloned());
                scene.blocks.extend(cached.scene(request.kind, 1 << 16)?);
            }
            let distribution = if request.analysis.is_some()
                && matches!(
                    request.prefix.phase,
                    CapturePhase::Paused | CapturePhase::Stopped | CapturePhase::Faulted
                ) {
                let range = request
                    .analysis
                    .as_ref()
                    .and_then(|analysis| analysis.range);
                let version = prefix.version();
                if self
                    .statistics
                    .get(key)
                    .is_none_or(|cached| cached.version != version || cached.range != range)
                {
                    let selected = |sample: &clicksync_chart::Sample| {
                        range.is_none_or(|(start, end)| {
                            crate::product::performance_analysis::time_ms(
                                snapshot,
                                sample.source_index,
                                request.parameters.time_basis
                                    == clicksync_chart::TimeBasis::Logical,
                            )
                            .is_some_and(|time| time >= start && time <= end)
                        })
                    };
                    let primary = engine.geometry().samples().filter(selected);
                    let distribution = if matches!(
                        request.parameters.metric,
                        clicksync_chart::Metric::Velocity(_)
                            | clicksync_chart::Metric::VelocityMagnitude
                            | clicksync_chart::Metric::PathSpeed
                    ) && request.secondary_metric.is_some()
                    {
                        let secondary = &self
                            .secondary
                            .iter()
                            .find(|(id, _)| id == key)
                            .expect("prepared secondary")
                            .1
                            .engine;
                        clicksync_chart::analysis::statistics::distribution(
                            primary
                                .zip(secondary.geometry().samples().filter(selected))
                                .map(|(a, b)| a.point.y.value().hypot(b.point.y.value())),
                        )
                    } else {
                        clicksync_chart::analysis::statistics::distribution_with_dispersion(
                            primary.map(|sample| {
                                let value = sample.point.y.value();
                                if matches!(
                                    request.parameters.metric,
                                    clicksync_chart::Metric::Velocity(_)
                                        | clicksync_chart::Metric::VelocityMagnitude
                                        | clicksync_chart::Metric::PathSpeed
                                ) {
                                    value.abs()
                                } else {
                                    value
                                }
                            }),
                            request.parameters.metric != clicksync_chart::Metric::Frequency,
                        )
                    };
                    if !valid() {
                        return Err("chart target superseded".into());
                    }
                    self.statistics.insert(
                        *key,
                        CachedStatistics {
                            version,
                            range,
                            distribution,
                        },
                    );
                }
                self.statistics
                    .get(key)
                    .and_then(|cached| cached.distribution.clone())
            } else {
                None
            };
            let scene = Arc::new(scene);
            *previous = Some(scene.clone());
            let hit_index = Arc::new(clicksync_chart::query::hit::HitIndex::prepare(
                &geometry,
                previous_hit.as_deref(),
            ));
            *previous_hit = Some(hit_index.clone());
            datasets.push(DatasetChart {
                device_key: *key,
                scene,
                work,
                geometry,
                hit_index,
                histogram: engine.histogram().map_or_else(Vec::new, |h| h.bins()),
                plot_origin: clicksync_chart::Point {
                    x: clicksync_chart::Coordinate::Scalar(0.0),
                    y: clicksync_chart::Coordinate::Scalar(0.0),
                },
                plot_bounds: [0.0; 4],
                interval_band: None,
                bounds_work: BoundsWork::default(),
                gaps: Vec::new(),
                distribution,
                link_index: None,
            });
        }
        if request.kind == PlotKind::Histogram && datasets.len() > 1 {
            let inputs: Vec<_> = datasets
                .iter()
                .map(|d| (d.scene.state.clone(), d.geometry.clone()))
                .collect();
            let scenes = PreparedScene::comparison_histograms(
                request.parameters.metric,
                request
                    .parameters
                    .histogram
                    .ok_or("missing histogram parameters")?,
                &inputs,
            )
            .map_err(|e| e.to_string())?;
            for (dataset, (scene, bins)) in datasets.iter_mut().zip(scenes) {
                dataset.scene = Arc::new(scene);
                dataset.histogram = bins;
            }
        }
        if let Some(analysis) = &request.analysis {
            for dataset in &mut datasets {
                let snapshot = request
                    .prefix
                    .datasets
                    .iter()
                    .find(|(key, _)| *key == dataset.device_key)
                    .unwrap()
                    .1
                    .clone();
                let reuse = analysis.range.is_none()
                    && match request.kind {
                        PlotKind::Trajectory | PlotKind::Histogram => true,
                        PlotKind::Points => !analysis.options.lines && !analysis.options.stems,
                        PlotKind::Lines => analysis.options.lines && !analysis.options.stems,
                        PlotKind::Stems => false,
                    };
                if !reuse {
                    let cache = self.analysis_scenes.entry(dataset.device_key).or_default();
                    let (geometry, scene, histogram) =
                        analysis_scene(dataset, &snapshot, &request, analysis, cache)?;
                    dataset.geometry = geometry;
                    dataset.scene = Arc::new(scene);
                    dataset.histogram = histogram;
                    dataset.hit_index = Arc::new(clicksync_chart::query::hit::HitIndex::prepare(
                        &dataset.geometry,
                        cache.hit.as_deref().or(Some(dataset.hit_index.as_ref())),
                    ));
                    cache.hit = Some(dataset.hit_index.clone());
                }
                if analysis.options.gaps
                    && analysis
                        .options
                        .gap_dataset
                        .is_none_or(|key| key == dataset.device_key)
                    && request.kind != PlotKind::Histogram
                    && request.kind != PlotKind::Trajectory
                {
                    dataset.gaps = crate::product::performance_analysis::report_gaps(
                        &snapshot,
                        request.parameters.time_basis,
                        analysis.range,
                    );
                }
                for (side, metric) in request.trend_metrics().enumerate() {
                    let (_, cached) = self
                        .trends
                        .iter_mut()
                        .find(|((key, kind), _)| *key == dataset.device_key && *kind == metric)
                        .ok_or("missing prepared trend")?;
                    let prefix = fixed(dataset.device_key, &snapshot, request.prefix.complete);
                    let delta = if matches!(
                        request.prefix.phase,
                        CapturePhase::Stopped | CapturePhase::Faulted
                    ) && snapshot.status() == SessionStatus::Stopped
                    {
                        cached.engine.finish(prefix)
                    } else {
                        cached.engine.advance(prefix)
                    }
                    .map_err(|e| e.to_string())?;
                    add_work(&mut dataset.work, &delta.work);
                    let mut blocks =
                        cached.scene(PlotKind::Lines, (1 << 17) | ((side as u32) << 16))?;
                    if let Some((start, end)) = analysis.range {
                        for (block, geometry) in
                            blocks.iter_mut().zip(cached.engine.geometry().blocks())
                        {
                            let selected = |sample: &clicksync_chart::Sample| {
                                crate::product::performance_analysis::time_ms(
                                    &snapshot,
                                    sample.source_index,
                                    request.parameters.time_basis
                                        == clicksync_chart::TimeBasis::Logical,
                                )
                                .is_some_and(|time| time >= start && time <= end)
                            };
                            let local = |sample: clicksync_chart::Sample| {
                                [
                                    sample.point.x.relative_to(block.origin.x) as f32,
                                    sample.point.y.relative_to(block.origin.y) as f32,
                                ]
                            };
                            let instances = geometry
                                .segments()
                                .filter(|(a, b)| selected(a) && selected(b))
                                .map(|(a, b)| {
                                    clicksync_chart::gpu::Instance::line(local(a), local(b))
                                })
                                .collect::<Vec<_>>();
                            let filtered = Arc::make_mut(block);
                            filtered.instances = instances.into();
                            filtered.content_version = request.id;
                        }
                    }
                    Arc::make_mut(&mut dataset.scene).blocks.extend(blocks);
                }
            }
            if request.kind == PlotKind::Histogram && datasets.len() > 1 {
                let inputs: Vec<_> = datasets
                    .iter()
                    .map(|d| (d.scene.state.clone(), d.geometry.clone()))
                    .collect();
                let scenes = PreparedScene::comparison_histograms(
                    request.parameters.metric,
                    request
                        .parameters
                        .histogram
                        .ok_or("missing histogram parameters")?,
                    &inputs,
                )
                .map_err(|e| e.to_string())?;
                for (dataset, (scene, bins)) in datasets.iter_mut().zip(scenes) {
                    dataset.scene = Arc::new(scene);
                    dataset.histogram = bins;
                }
            }
        }
        // Measure only the final scene: measuring the base first evicts every
        // stable trend block, forcing a full-history rescan on each live prefix.
        for dataset in &mut datasets {
            (
                dataset.plot_origin,
                dataset.plot_bounds,
                dataset.bounds_work,
            ) = self
                .bounds
                .entry(dataset.device_key)
                .or_default()
                .measure(&dataset.scene, &valid)?;
        }
        if matches!(
            request.parameters.metric,
            clicksync_chart::Metric::Interval | clicksync_chart::Metric::IntervalTrend
        ) && request.kind != PlotKind::Histogram
        {
            for dataset in &mut datasets {
                dataset.interval_band = bounds::interval_band(&dataset.geometry);
            }
        }
        if request.analysis.is_some()
            && matches!(
                request.prefix.phase,
                CapturePhase::Paused | CapturePhase::Stopped | CapturePhase::Faulted
            )
        {
            for dataset in &mut datasets {
                if !valid() {
                    return Err("chart target superseded".into());
                }
                let snapshot = &request
                    .prefix
                    .datasets
                    .iter()
                    .find(|(key, _)| *key == dataset.device_key)
                    .unwrap()
                    .1;
                dataset.link_index = Some(Arc::new(link::LinkIndex::prepare(
                    dataset,
                    snapshot,
                    &request.parameters,
                    request.prefix.datasets.len() > 1,
                )));
            }
        }
        Ok(ChartResult {
            request,
            datasets,
            byte_budget: None,
            managed: Vec::new(),
        })
    }
}

type AnalysisScene = (
    Vec<Arc<clicksync_chart::GeometryBlock>>,
    PreparedScene,
    Vec<clicksync_chart::analysis::histogram::HistogramBin>,
);

type AnalysisIdentity = (Option<(f64, f64)>, bool, bool);

struct AnalysisBlock {
    original: Arc<clicksync_chart::GeometryBlock>,
    geometry: Option<Arc<clicksync_chart::GeometryBlock>>,
    prepared: Option<Arc<clicksync_chart::gpu::PreparedBlock>>,
}

#[derive(Default)]
struct AnalysisCache {
    identity: Option<AnalysisIdentity>,
    blocks: std::collections::HashMap<usize, AnalysisBlock>,
    hit: Option<Arc<clicksync_chart::query::hit::HitIndex>>,
}

fn analysis_scene(
    dataset: &DatasetChart,
    snapshot: &SessionSnapshot,
    request: &ChartRequest,
    analysis: &crate::product::performance_analysis::PlotAnalysis,
    cache: &mut AnalysisCache,
) -> Result<AnalysisScene, String> {
    use clicksync_chart::gpu::{BlockKey, Instance, PreparedBlock};
    use clicksync_chart::{Coordinate, GeometryBlock};
    let logical = request.parameters.time_basis == clicksync_chart::TimeBasis::Logical;
    if request.kind == PlotKind::Trajectory && analysis.range.is_none() {
        return Ok((
            dataset.geometry.clone(),
            (*dataset.scene).clone(),
            Vec::new(),
        ));
    }
    let selected = |sample: &clicksync_chart::Sample| {
        analysis.range.is_none_or(|(start, end)| {
            crate::product::performance_analysis::time_ms(snapshot, sample.source_index, logical)
                .is_some_and(|time| time >= start && time <= end)
        })
    };
    let identity = (
        analysis.range,
        analysis.options.lines,
        analysis.options.stems,
    );
    if cache.identity != Some(identity) {
        *cache = AnalysisCache {
            identity: Some(identity),
            ..Default::default()
        };
    }
    let mut previous = std::mem::take(&mut cache.blocks);
    let mut current = std::collections::HashMap::with_capacity(dataset.geometry.len());
    let mut geometry = Vec::new();
    let mut blocks = Vec::new();
    let primary_blocks = dataset
        .scene
        .blocks
        .iter()
        .filter(|block| block.key.kind & (1 << 16) == 0)
        .count();
    for (block_index, original) in dataset.geometry.iter().enumerate() {
        let secondary = block_index >= primary_blocks && request.secondary_metric.is_some();
        let pointer = Arc::as_ptr(original) as usize;
        if let Some(mut cached) = previous.remove(&pointer) {
            debug_assert!(Arc::ptr_eq(&cached.original, original));
            if let Some(geometry_block) = &cached.geometry {
                geometry.push(geometry_block.clone());
            }
            if let Some(prepared) = &mut cached.prepared {
                if prepared.key.id != blocks.len() {
                    let mut copy = (**prepared).clone();
                    copy.key.id = blocks.len();
                    *prepared = Arc::new(copy);
                }
                blocks.push(prepared.clone());
            }
            current.insert(pointer, cached);
            continue;
        }
        let mut filtered = Vec::new();
        if analysis.range.is_some() {
            for sample in &original.samples {
                if selected(sample) {
                    filtered.push(*sample);
                }
            }
        }
        if analysis.range.is_some() && filtered.is_empty() {
            current.insert(
                pointer,
                AnalysisBlock {
                    original: original.clone(),
                    geometry: None,
                    prepared: None,
                },
            );
            continue;
        }
        let origin = original.origin;
        let block = if analysis.range.is_none() {
            original.clone()
        } else {
            let relative_points: Vec<_> = filtered
                .iter()
                .map(|sample| {
                    [
                        sample.point.x.relative_to(origin.x),
                        sample.point.y.relative_to(origin.y),
                    ]
                })
                .collect();
            let mut bounds = [0.0_f64; 4];
            for p in &relative_points {
                bounds[0] = bounds[0].min(p[0]);
                bounds[1] = bounds[1].max(p[0]);
                bounds[2] = bounds[2].min(p[1]);
                bounds[3] = bounds[3].max(p[1]);
            }
            let connector = original.connector.filter(|sample| {
                selected(sample) && filtered[0].source_index == original.samples[0].source_index
            });
            Arc::new(GeometryBlock {
                id: geometry.len(),
                content_version: request.id,
                source_range: filtered.first().unwrap().source_index
                    ..filtered.last().unwrap().source_index + 1,
                origin,
                connector,
                samples: filtered,
                relative_points,
                bounds,
            })
        };
        let mut prepared = None;
        if request.kind != PlotKind::Histogram {
            let mut instances = Vec::new();
            let local = |sample: &clicksync_chart::Sample| {
                [
                    sample.point.x.relative_to(origin.x) as f32,
                    sample.point.y.relative_to(origin.y) as f32,
                ]
            };
            if analysis.options.stems && request.kind != PlotKind::Trajectory {
                instances.extend(block.samples.iter().map(|sample| {
                    let p = local(sample);
                    Instance::stem(
                        [p[0], Coordinate::Scalar(0.0).relative_to(origin.y) as f32],
                        p,
                    )
                }));
            }
            if request.kind != PlotKind::Trajectory && analysis.options.lines {
                instances.extend(
                    original
                        .segments()
                        .filter(|(a, b)| selected(a) && selected(b))
                        .map(|(a, b)| {
                            if request.kind == PlotKind::Points {
                                Instance::connector(local(&a), local(&b))
                            } else {
                                Instance::line(local(&a), local(&b))
                            }
                        }),
                );
            }
            if request.kind == PlotKind::Trajectory
                || !analysis.options.lines
                || request.kind == PlotKind::Points
            {
                instances.extend(block.samples.iter().map(|sample| {
                    let p = local(sample);
                    Instance::point(p[0], p[1])
                }));
            }
            let version = dataset.scene.state.data_version.unwrap();
            let prepared_block = Arc::new(PreparedBlock {
                key: BlockKey {
                    dataset: version.dataset.0,
                    generation: version.generation,
                    parameters: dataset.scene.state.parameter_version,
                    kind: request.kind as u32
                        | if secondary { 1 << 16 } else { 0 }
                        | if request.kind == PlotKind::Trajectory {
                            1 << 18
                        } else {
                            0
                        },
                    id: blocks.len(),
                },
                content_version: request.id,
                origin,
                instances: instances.into(),
            });
            blocks.push(prepared_block.clone());
            prepared = Some(prepared_block);
        }
        geometry.push(block.clone());
        current.insert(
            pointer,
            AnalysisBlock {
                original: original.clone(),
                geometry: Some(block),
                prepared,
            },
        );
    }
    cache.blocks = current; // one current input, never a cache of past ranges or styles
    if request.kind == PlotKind::Histogram {
        let scenes = PreparedScene::comparison_histograms(
            request.parameters.metric,
            request
                .parameters
                .histogram
                .ok_or("missing histogram parameters")?,
            &[(dataset.scene.state.clone(), geometry.clone())],
        )
        .map_err(|e| e.to_string())?;
        let (scene, bins) = scenes.into_iter().next().unwrap();
        Ok((geometry, scene, bins))
    } else {
        if request.kind == PlotKind::Trajectory {
            if analysis.range.is_none() {
                return Ok((
                    dataset.geometry.clone(),
                    (*dataset.scene).clone(),
                    Vec::new(),
                ));
            }
            let mut background = dataset.scene.blocks.clone();
            background.extend(blocks);
            blocks = background;
            geometry = dataset.geometry.clone();
        }
        let scene = PreparedScene {
            state: dataset.scene.state.clone(),
            blocks,
        };
        scene.validate().map_err(|e| e.to_string())?;
        Ok((geometry, scene, Vec::new()))
    }
}

#[cfg(test)]
fn scene_bounds(scene: &PreparedScene) -> (clicksync_chart::Point, [f64; 4]) {
    use clicksync_chart::{Coordinate, Point};
    let origin = scene.blocks.first().map(|b| b.origin).unwrap_or(Point {
        x: Coordinate::Scalar(0.0),
        y: Coordinate::Scalar(0.0),
    });
    let mut bounds = [0.0_f64; 4];
    for block in &scene.blocks {
        let x = block.origin.x.relative_to(origin.x);
        let y = block.origin.y.relative_to(origin.y);
        for instance in &*block.instances {
            for p in [instance.a, instance.b] {
                bounds[0] = bounds[0].min(x + p[0] as f64);
                bounds[1] = bounds[1].max(x + p[0] as f64);
                bounds[2] = bounds[2].min(y + p[1] as f64);
                bounds[3] = bounds[3].max(y + p[1] as f64);
            }
        }
    }
    (origin, bounds)
}

fn add_work(total: &mut clicksync_chart::Work, next: &clicksync_chart::Work) {
    total.new_record_reads += next.new_record_reads;
    total.context_record_reads += next.context_record_reads;
    total.derived_evaluations += next.derived_evaluations;
    total.smoothing_evaluations += next.smoothing_evaluations;
    total.revisited_samples += next.revisited_samples;
    total.geometry_samples_written += next.geometry_samples_written;
    total.histogram_rebins += next.histogram_rebins;
    total.automatic_samples_sorted += next.automatic_samples_sorted;
}
