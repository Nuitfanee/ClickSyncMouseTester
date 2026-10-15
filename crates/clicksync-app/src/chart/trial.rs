//! Bounded cooperative scheduling for the production chart worker.
use super::*;

pub const MAX_DEMANDS: usize = 4;

#[derive(Clone, Debug, Default, serde::Serialize)]
pub struct TrialReport {
    pub steps: [u64; MAX_DEMANDS],
    pub completed: [u64; MAX_DEMANDS],
    pub merged: [u64; MAX_DEMANDS],
    pub discarded: [u64; MAX_DEMANDS],
    pub maximum_pending: usize,
    pub maximum_active: usize,
    pub max_step_us: [u64; MAX_DEMANDS],
    pub max_assembly_us: [u64; MAX_DEMANDS],
}

#[derive(Default)]
pub(super) struct Demand {
    target: Option<ChartRequest>,
    pending: Option<ChartRequest>,
    active: Option<ChartRequest>,
    cache: EngineCache,
    work: std::collections::HashMap<u32, clicksync_chart::Work>,
}

pub struct CooperativeTrial {
    demands: [Demand; MAX_DEMANDS],
    cursor: usize,
    quotas: [usize; MAX_DEMANDS],
    report: TrialReport,
    byte_budget: Option<Arc<clicksync_core::SharedByteBudget>>,
}

impl Default for CooperativeTrial {
    fn default() -> Self {
        Self {
            demands: std::array::from_fn(|_| Demand::default()),
            cursor: 0,
            quotas: [ANALYSIS_BLOCK; MAX_DEMANDS],
            report: TrialReport::default(),
            byte_budget: None,
        }
    }
}

impl CooperativeTrial {
    pub fn with_budget(budget: Arc<clicksync_core::SharedByteBudget>) -> Self {
        Self {
            byte_budget: Some(budget),
            ..Self::default()
        }
    }
    pub(super) fn next_slot(&self) -> usize {
        [0, 1, 0, 2, 0, 3][self.cursor]
    }

    /// Production keeps the sole latest pending request in its shared mailbox.
    /// Move it into this turn only when the active input is done or superseded.
    pub(super) fn receive_latest(
        &mut self,
        slot: usize,
        target: &ChartRequest,
        pending: &mut Option<ChartRequest>,
    ) {
        let demand = &mut self.demands[slot];
        if demand.target.as_ref().is_none_or(|old| old.id != target.id) {
            demand.target = Some(target.clone());
        }
        if demand
            .active
            .as_ref()
            .is_none_or(|active| !same_presentation(active, target))
        {
            demand.pending = pending.take();
        }
    }

    pub fn set_quota(&mut self, slot: usize, records: usize) -> Result<(), String> {
        if slot >= MAX_DEMANDS || records == 0 || records > ANALYSIS_BLOCK {
            return Err("invalid trial compute quota".into());
        }
        self.quotas[slot] = records;
        Ok(())
    }

    #[cfg(test)]
    pub fn submit(&mut self, slot: usize, request: ChartRequest) -> Result<(), String> {
        let demand = self.demands.get_mut(slot).ok_or("trial demand limit")?;
        if let Some(old) = &demand.target
            && (request.id <= old.id
                || request.source_version < old.source_version
                || (request.source_version == old.source_version
                    && (request.prefix.generation < old.prefix.generation
                        || (request.prefix.generation == old.prefix.generation
                            && request.prefix.received_sequence < old.prefix.received_sequence))))
        {
            return Err("regressed trial request".into());
        }
        if matches!(request.view, ViewMode::Follow { window_ms } if !window_ms.is_finite() || window_ms <= 0.0)
        {
            return Err("invalid trial follow window".into());
        }
        if demand.pending.is_some() {
            self.report.merged[slot] += 1;
        }
        demand.target = Some(request.clone());
        demand.pending = Some(request);
        self.report.maximum_pending = self.report.maximum_pending.max(
            self.demands
                .iter()
                .filter(|demand| demand.pending.is_some())
                .count(),
        );
        Ok(())
    }

    pub fn remove(&mut self, slot: usize) -> Result<(), String> {
        drop(self.retire(slot)?);
        Ok(())
    }

    pub(super) fn retire(&mut self, slot: usize) -> Result<Demand, String> {
        let demand = self.demands.get_mut(slot).ok_or("trial demand limit")?;
        self.report.discarded[slot] += u64::from(demand.active.is_some());
        Ok(std::mem::take(demand))
    }

    pub fn report(&self) -> TrialReport {
        self.report.clone()
    }

    pub(super) fn cached_values(&self) -> ([usize; MAX_DEMANDS], [usize; MAX_DEMANDS]) {
        (
            std::array::from_fn(|slot| {
                self.demands[slot]
                    .cache
                    .datasets
                    .iter()
                    .map(|(_, engine, _, _)| engine.cached_value_bytes())
                    .chain(
                        self.demands[slot]
                            .cache
                            .secondary
                            .iter()
                            .map(|(_, cached)| cached.engine.cached_value_bytes()),
                    )
                    .chain(
                        self.demands[slot]
                            .cache
                            .trends
                            .iter()
                            .map(|(_, c)| c.engine.cached_value_bytes()),
                    )
                    .sum()
            }),
            std::array::from_fn(|slot| {
                self.demands[slot].cache.datasets.len()
                    + self.demands[slot].cache.secondary.len()
                    + self.demands[slot].cache.trends.len()
            }),
        )
    }

    pub fn is_idle(&self) -> bool {
        self.demands
            .iter()
            .all(|demand| demand.active.is_none() && demand.pending.is_none())
    }

    pub fn pending_count(&self) -> usize {
        self.demands
            .iter()
            .filter(|demand| demand.pending.is_some())
            .count()
    }

    /// Trajectory gets alternate turns; every monitor gets a turn in six steps.
    /// A newer compatible prefix coalesces pending work without cancelling active work.
    #[cfg(test)]
    pub fn step(&mut self) -> Result<Option<(usize, Arc<ChartResult>)>, String> {
        match self.step_completion(&AtomicBool::new(false)) {
            Some((slot, _, answer)) => answer.map(|result| Some((slot, result))),
            None => Ok(None),
        }
    }

    pub(super) fn step_completion(
        &mut self,
        cancelled: &AtomicBool,
    ) -> Option<(usize, ChartRequest, ChartAnswer)> {
        let slot = [0, 1, 0, 2, 0, 3][self.cursor];
        let started = Instant::now();
        let answer = self.advance_slot(cancelled);
        self.report.max_step_us[slot] =
            self.report.max_step_us[slot].max(started.elapsed().as_micros() as u64);
        match answer {
            Ok(Some((_, result))) => Some((slot, result.request.clone(), Ok(result))),
            Ok(None) => None,
            Err(error) => {
                let demand = &mut self.demands[slot];
                let request = demand.active.take().or_else(|| demand.target.clone())?;
                demand.cache = EngineCache::default();
                Some((slot, request, Err(error)))
            }
        }
    }

    fn advance_slot(
        &mut self,
        cancelled: &AtomicBool,
    ) -> Result<Option<(usize, Arc<ChartResult>)>, String> {
        const ORDER: [usize; 6] = [0, 1, 0, 2, 0, 3];
        let slot = ORDER[self.cursor];
        self.cursor = (self.cursor + 1) % ORDER.len();
        let demand = &mut self.demands[slot];
        if demand.active.as_ref().is_some_and(|active| {
            demand
                .target
                .as_ref()
                .is_none_or(|target| !same_presentation(active, target))
        }) {
            demand.active = None;
            demand.cache = EngineCache::default();
            self.report.discarded[slot] += 1;
        }
        if demand.active.is_none() {
            demand.active = demand.pending.take();
            demand.work.clear();
        }
        let Some(request) = demand.active.as_ref() else {
            return Ok(None);
        };
        self.report.maximum_active = 1;
        self.report.steps[slot] += 1;
        let identity = (
            request.source_version,
            request.prefix.generation,
            request.parameters.clone(),
            request.dataset_cpi.clone(),
            request.secondary_metric,
            request.kind,
        );
        if demand.cache.identity.as_ref() != Some(&identity) {
            demand.cache = EngineCache {
                identity: Some(identity),
                ..Default::default()
            };
        }
        if let Some(budget) = &self.byte_budget {
            let required = cache_capacity(request)?;
            if required > demand.cache.managed_bytes {
                let reservation = budget
                    .try_reserve(required - demand.cache.managed_bytes)
                    .map_err(|error| error.to_string())?;
                demand.cache.managed.push(Arc::new(reservation));
                demand.cache.managed_bytes = required;
            }
        }
        let mut remaining = self.quotas[slot];
        for (key, snapshot) in &request.prefix.datasets {
            if !demand.cache.datasets.iter().any(|(id, _, _, _)| id == key) {
                demand.cache.datasets.push((
                    *key,
                    ChartEngine::new(request.parameters_for(*key), ANALYSIS_BLOCK)
                        .map_err(|error| error.to_string())?,
                    None,
                    None,
                ));
            }
            let (_, engine, _, _) = demand
                .cache
                .datasets
                .iter_mut()
                .find(|(id, _, _, _)| id == key)
                .expect("trial engine");
            let prefix = fixed(*key, snapshot, request.prefix.complete);
            remaining = advance_part(
                engine,
                prefix,
                remaining,
                demand.work.entry(*key).or_default(),
            )?;
            if let Some(metric) = request.secondary_metric {
                if !demand.cache.secondary.iter().any(|(id, _)| id == key) {
                    let mut parameters = request.parameters_for(*key);
                    parameters.metric = metric;
                    demand
                        .cache
                        .secondary
                        .push((*key, SecondaryCache::new(parameters)?));
                }
                let (_, secondary) = demand
                    .cache
                    .secondary
                    .iter_mut()
                    .find(|(id, _)| id == key)
                    .expect("trial secondary engine");
                remaining = advance_part(
                    &mut secondary.engine,
                    prefix,
                    remaining,
                    demand.work.entry(*key).or_default(),
                )?;
            }
            for metric in request.trend_metrics() {
                let identity = (*key, metric);
                if !demand.cache.trends.iter().any(|(id, _)| *id == identity) {
                    let mut parameters = request.parameters_for(*key);
                    parameters.metric = metric;
                    parameters.histogram = None;
                    demand
                        .cache
                        .trends
                        .push((identity, SecondaryCache::new(parameters)?));
                }
                let (_, trend) = demand
                    .cache
                    .trends
                    .iter_mut()
                    .find(|(id, _)| *id == identity)
                    .unwrap();
                remaining = advance_part(
                    &mut trend.engine,
                    prefix,
                    remaining,
                    demand.work.entry(*key).or_default(),
                )?;
            }
        }
        let complete = request.prefix.datasets.iter().all(|(key, snapshot)| {
            demand.cache.datasets.iter().any(|(id, engine, _, _)| {
                id == key && engine.state().processed_end == snapshot.event_count()
            }) && (request.secondary_metric.is_none()
                || demand.cache.secondary.iter().any(|(id, cached)| {
                    id == key && cached.engine.state().processed_end == snapshot.event_count()
                }))
                && request.trend_metrics().all(|metric| {
                    demand.cache.trends.iter().any(|((id, kind), cache)| {
                        id == key
                            && *kind == metric
                            && cache.engine.state().processed_end == snapshot.event_count()
                    })
                })
        });
        if !complete {
            return Ok(None);
        }
        let request = demand.active.take().expect("completed trial");
        let mailbox = Arc::new((
            Mutex::new(Mailbox {
                target: demand.target.clone(),
                ..Default::default()
            }),
            Condvar::new(),
        ));
        let started = Instant::now();
        let scene_leases = self
            .byte_budget
            .as_ref()
            .map(|budget| reserve_scene(&mut demand.cache, &request, budget))
            .transpose()?
            .unwrap_or_default();
        let mut result =
            demand
                .cache
                .analyze(request, cancelled, &mailbox, self.byte_budget.as_ref())?;
        result.byte_budget = self.byte_budget.clone();
        result.managed = demand.cache.managed.clone();
        result.managed.extend(scene_leases);
        self.report.max_assembly_us[slot] =
            self.report.max_assembly_us[slot].max(started.elapsed().as_micros() as u64);
        for dataset in &mut result.datasets {
            if let Some(work) = demand.work.get(&dataset.device_key) {
                add_work(&mut dataset.work, work);
            }
        }
        let result = Arc::new(result);
        self.report.completed[slot] += 1;
        Ok(Some((slot, result)))
    }
}

fn sample_capacity(request: &ChartRequest) -> Result<usize, String> {
    request
        .prefix
        .datasets
        .iter()
        .try_fold(0_usize, |sum, (_, snapshot)| {
            let count = snapshot
                .event_count()
                .max(ANALYSIS_BLOCK)
                .checked_next_power_of_two()
                .ok_or("analysis capacity overflow")?;
            sum.checked_add(count)
                .ok_or_else(|| "analysis capacity overflow".into())
        })
}

fn cache_capacity(request: &ChartRequest) -> Result<usize, String> {
    // Vec growth is charged to a power-of-two ceiling. Six sample vectors
    // cover raw/output, velocity pairs and revision temporaries; scalar
    // filters, cumulative values, fixed statistics scratch and segment directories are bounded.
    let unit = std::mem::size_of::<clicksync_chart::Sample>() * 6
        + std::mem::size_of::<f64>() * 16
        + std::mem::size_of::<Range<usize>>() * 2;
    sample_capacity(request)?
        .checked_mul(
            unit * (1
                + usize::from(request.secondary_metric.is_some())
                + request.trend_metrics().count()),
        )
        .ok_or_else(|| "analysis capacity overflow".into())
}

fn reserve_scene(
    cache: &mut EngineCache,
    request: &ChartRequest,
    budget: &Arc<clicksync_core::SharedByteBudget>,
) -> Result<Vec<Arc<clicksync_core::BudgetReservation>>, String> {
    // A stable source block produces shared geometry/instances/indices, not a
    // second history-sized allocation per result. Keep its existing conservative unit.
    let unit = std::mem::size_of::<clicksync_chart::Sample>() * 4
        + std::mem::size_of::<clicksync_chart::gpu::Instance>() * 8
        + std::mem::size_of::<(f64, usize)>() * 2
        + std::mem::size_of::<Range<usize>>()
        + 256;
    if cache.leased_analysis.as_ref() != Some(&request.analysis) {
        cache.scene_leases.clear();
        cache.leased_analysis = Some(request.analysis.clone());
    }
    let mut previous = std::mem::take(&mut cache.scene_leases);
    let mut current = std::collections::HashMap::new();
    let mut leases = Vec::new();
    let engines = cache
        .datasets
        .iter()
        .map(|(_, engine, _, _)| (engine, unit))
        .chain(
            cache
                .secondary
                .iter()
                .map(|(_, cached)| (&cached.engine, unit)),
        )
        .chain(cache.trends.iter().map(|(_, cached)| {
            (
                &cached.engine,
                std::mem::size_of::<clicksync_chart::gpu::Instance>() * 4 + 256,
            )
        }));
    for (engine, unit) in engines {
        for geometry in engine.geometry().blocks() {
            let pointer = Arc::as_ptr(geometry) as usize;
            let entry = if let Some(entry) = previous.remove(&pointer) {
                debug_assert!(Arc::ptr_eq(&entry.geometry, geometry));
                entry
            } else {
                let bytes = geometry
                    .samples
                    .capacity()
                    .max(32)
                    .checked_mul(unit)
                    .ok_or("scene capacity overflow")?;
                SceneBlockLease {
                    geometry: geometry.clone(),
                    lease: Arc::new(
                        budget
                            .try_reserve(bytes)
                            .map_err(|error| error.to_string())?,
                    ),
                }
            };
            leases.push(entry.lease.clone());
            current.insert(pointer, entry);
        }
    }
    // Comparison time indices belong to each result; shared gap indices own their leases.
    let arrays_per_report = if request.analysis.is_some() {
        usize::from(
            request.prefix.datasets.len() > 1 && request.prefix.phase != CapturePhase::Collecting,
        ) * 64
    } else {
        0
    };
    let metadata = request.prefix.datasets.iter().try_fold(
        current
            .len()
            .checked_mul(512)
            .and_then(|n| n.checked_add(4096))
            .ok_or("scene capacity overflow")?,
        |sum, (_, snapshot)| {
            snapshot
                .event_count()
                .checked_mul(arrays_per_report)
                .and_then(|n| sum.checked_add(n))
                .ok_or("scene capacity overflow")
        },
    )?;
    leases.push(Arc::new(
        budget
            .try_reserve(metadata)
            .map_err(|error| error.to_string())?,
    ));
    // Retired tails can still be held by the engine's previous scene during assembly.
    // Only this completed result retains these extra leases; they do not enter the next cache.
    leases.extend(previous.into_values().map(|entry| entry.lease));
    cache.scene_leases = current;
    Ok(leases)
}

fn advance_part(
    engine: &mut ChartEngine,
    prefix: FixedPrefix<'_>,
    remaining: usize,
    work: &mut clicksync_chart::Work,
) -> Result<usize, String> {
    let start = engine.state().processed_end;
    let end = (start + remaining).min(prefix.len());
    if end > start {
        let delta = engine
            .advance(prefix.with_end(end).map_err(|error| error.to_string())?)
            .map_err(|error| error.to_string())?;
        add_work(work, &delta.work);
    }
    Ok(remaining - (end - start))
}

#[cfg(test)]
mod tests {
    use super::*;
    use clicksync_chart::{Axis, Metric};
    use clicksync_core::{RawMouseMovementMode, RawMouseRecord, Session};

    pub(super) fn prefix(count: usize) -> Arc<SessionPrefix> {
        let mut session = Session::default_with_metadata(Default::default()).unwrap();
        session.begin_collecting(100, true, false).unwrap();
        for i in 0..count {
            session
                .append(RawMouseRecord::new(
                    (i % 7) as i32 - 3,
                    (i % 5) as i32 - 2,
                    100 + i as u64 * 1250,
                    i as u64 + 1,
                    i as u64 + 1,
                    RawMouseMovementMode::Relative,
                ))
                .unwrap();
        }
        Arc::new(SessionPrefix {
            generation: 1,
            received_sequence: count as u64,
            received_records: count as u64,
            phase: CapturePhase::Collecting,
            complete: true,
            datasets: vec![(1, Arc::new(session.snapshot().unwrap()))],
        })
    }

    fn request(slot: usize, input: Arc<SessionPrefix>, id: u64) -> ChartRequest {
        let metrics = [
            Metric::Trajectory,
            Metric::PathSpeed,
            Metric::Interval,
            Metric::Count(Axis::X),
        ];
        ChartRequest {
            id,
            source_version: 1,
            prefix: input,
            parameters: ChartParameters::new(metrics[slot]),
            dataset_cpi: vec![(1, Some(800.0))],
            secondary_metric: (slot == 3).then_some(Metric::Count(Axis::Y)),
            kind: PlotKind::Lines,
            view: ViewMode::FullSession,
            analysis: None,
        }
    }

    #[test]
    fn four_metrics_share_input_complete_fairly_and_reuse_incremental_geometry() {
        let input = prefix(17_000);
        let mut trial = CooperativeTrial::default();
        trial.set_quota(2, 256).unwrap();
        for slot in 0..MAX_DEMANDS {
            trial.submit(slot, request(slot, input.clone(), 1)).unwrap();
        }
        let mut results: [Option<Arc<ChartResult>>; MAX_DEMANDS] = std::array::from_fn(|_| None);
        for turn in 0..1000 {
            if turn < 100 {
                trial
                    .submit(0, request(0, input.clone(), turn as u64 + 2))
                    .unwrap();
            }
            if let Some((slot, result)) = trial.step().unwrap() {
                assert!(Arc::ptr_eq(&result.request.prefix, &input));
                assert_eq!(result.datasets[0].scene.state.processed_end, 17_000);
                results[slot] = Some(result);
            }
            if trial.is_idle() {
                break;
            }
        }
        assert!(trial.is_idle());
        assert!(results.iter().all(Option::is_some));
        let report = trial.report();
        assert!(report.completed.iter().all(|count| *count > 0));
        assert!(report.steps[2] > report.steps[1]);
        assert_eq!(report.discarded, [0; MAX_DEMANDS]);
        assert_eq!(report.maximum_active, 1);
        assert_eq!(report.maximum_pending, MAX_DEMANDS);
        assert!(report.merged[0] > 0);
        assert!(
            trial
                .submit(MAX_DEMANDS, request(0, input.clone(), 200))
                .is_err()
        );
        assert!(trial.submit(0, request(0, input, 1)).is_err());
        let extended = prefix(25_000);
        for slot in 0..MAX_DEMANDS {
            trial
                .submit(slot, request(slot, extended.clone(), 201))
                .unwrap();
        }
        let mut extensions: [Option<Arc<ChartResult>>; MAX_DEMANDS] = std::array::from_fn(|_| None);
        for _ in 0..1000 {
            if let Some((slot, result)) = trial.step().unwrap() {
                extensions[slot] = Some(result);
            }
            if trial.is_idle() {
                break;
            }
        }
        assert!(trial.is_idle());
        for slot in 0..MAX_DEMANDS {
            let before = results[slot].as_ref().unwrap();
            let after = extensions[slot].as_ref().unwrap();
            assert_eq!(after.datasets[0].scene.state.processed_end, 25_000);
            assert!(Arc::ptr_eq(
                &before.datasets[0].geometry[0],
                &after.datasets[0].geometry[0]
            ));
            assert!(
                before.datasets[0].scene.blocks[0]
                    .instances
                    .shares_storage_with(&after.datasets[0].scene.blocks[0].instances)
            );
        }
        trial.remove(1).unwrap();
        assert!(trial.is_idle());
    }

    #[test]
    fn final_bounds_reuse_stable_base_and_trend_blocks() {
        use crate::product::performance_analysis::{PlotAnalysis, PlotOptions};
        let mut trial = CooperativeTrial::default();
        let mut previous: Option<Arc<ChartResult>> = None;
        for (id, count) in [(1, 25_000), (2, 25_128), (3, 25_128)] {
            let mut request = request(3, prefix(count), id);
            request.kind = PlotKind::Points;
            request.analysis = Some(PlotAnalysis {
                range: None,
                options: PlotOptions {
                    lines: false,
                    trend: true,
                    ..Default::default()
                },
            });
            trial.submit(3, request).unwrap();
            let result = (0..1000)
                .find_map(|_| trial.step().unwrap().map(|(_, r)| r))
                .unwrap();
            let dataset = &result.datasets[0];
            let (origin, bounds) = scene_bounds(&dataset.scene);
            assert_eq!(dataset.plot_origin, origin);
            assert_eq!(
                dataset.plot_bounds.map(f64::to_bits),
                bounds.map(f64::to_bits)
            );
            if let Some(previous) = previous {
                let previous = &previous.datasets[0].scene.blocks;
                let changed: usize = dataset
                    .scene
                    .blocks
                    .iter()
                    .filter(|block| !previous.iter().any(|old| Arc::ptr_eq(old, block)))
                    .map(|block| block.instances.len())
                    .sum();
                assert_eq!(dataset.bounds_work.scanned_instances, changed);
                assert!(dataset.bounds_work.reused_blocks > 0);
            }
            if id == 3 {
                assert_eq!(dataset.bounds_work.scanned_instances, 0);
            }
            previous = Some(result);
        }
    }

    #[test]
    fn styled_and_ranged_scenes_reuse_stable_geometry_and_invalidate_only_changed_options() {
        use crate::product::performance_analysis::{PlotAnalysis, PlotOptions};
        let mut trial = CooperativeTrial::default();
        let mut previous: Option<Arc<ChartResult>> = None;
        for (id, count, range, stems) in [
            (1, 25_000, None, true),
            (2, 25_128, None, true),
            (3, 25_128, None, true),
            (4, 25_128, Some((5.0, 2400.0)), true),
            (5, 25_128, Some((5.0, 2400.0)), true),
            (6, 25_128, Some((5.0, 2400.0)), false),
        ] {
            let mut request = request(3, prefix(count), id);
            request.kind = PlotKind::Points;
            request.analysis = Some(PlotAnalysis {
                range,
                options: PlotOptions {
                    lines: true,
                    stems,
                    ..Default::default()
                },
            });
            let mut fresh = CooperativeTrial::default();
            fresh.submit(3, request.clone()).unwrap();
            let expected = (0..1000)
                .find_map(|_| fresh.step().unwrap().map(|(_, r)| r))
                .unwrap();
            trial.submit(3, request).unwrap();
            let actual = (0..1000)
                .find_map(|_| trial.step().unwrap().map(|(_, r)| r))
                .unwrap();
            let instances = |r: &ChartResult| {
                r.datasets[0]
                    .scene
                    .blocks
                    .iter()
                    .flat_map(|b| b.instances.iter().copied())
                    .collect::<Vec<_>>()
            };
            assert_eq!(instances(&actual), instances(&expected));
            assert_eq!(
                actual.datasets[0].plot_bounds.map(f64::to_bits),
                expected.datasets[0].plot_bounds.map(f64::to_bits)
            );
            if let Some(old) = &previous {
                if matches!(id, 2 | 3 | 5) {
                    assert!(Arc::ptr_eq(
                        &old.datasets[0].scene.blocks[0],
                        &actual.datasets[0].scene.blocks[0]
                    ));
                    assert!(Arc::ptr_eq(
                        &old.datasets[0].geometry[0],
                        &actual.datasets[0].geometry[0]
                    ));
                }
                if matches!(id, 3 | 5) {
                    assert_eq!(actual.datasets[0].bounds_work.scanned_instances, 0);
                    assert_eq!(actual.datasets[0].work.new_record_reads, 0);
                }
                if matches!(id, 4 | 6) {
                    assert!(!Arc::ptr_eq(
                        &old.datasets[0].scene.blocks[0],
                        &actual.datasets[0].scene.blocks[0]
                    ));
                }
            }
            previous = Some(actual);
        }
    }

    #[test]
    fn scene_budget_charges_stable_blocks_once_and_retains_leases_for_old_results() {
        let budget = clicksync_core::SharedByteBudget::new(256 * 1024 * 1024);
        let mut trial = CooperativeTrial::with_budget(budget.clone());
        let mut results = Vec::new();
        let mut previous_bytes = 0;
        for (id, count) in [(1, 25_000), (2, 25_128), (3, 25_128)] {
            trial.submit(0, request(0, prefix(count), id)).unwrap();
            let result = (0..1000)
                .find_map(|_| trial.step().unwrap().map(|(_, r)| r))
                .unwrap();
            if id > 1 {
                assert!(
                    budget.reserved_bytes() - previous_bytes < 16 * 1024 * 1024,
                    "a new result reserved the entire immutable history again"
                );
            }
            if id == 3 {
                assert!(budget.reserved_bytes() - previous_bytes < 64 * 1024);
            }
            previous_bytes = budget.reserved_bytes();
            results.push(result);
        }
        trial.remove(0).unwrap();
        assert!(budget.reserved_bytes() > 0);
        drop(results);
        assert_eq!(budget.reserved_bytes(), 0);
    }

    #[test]
    fn removing_one_inflight_view_keeps_other_demands_and_replacement_rejects_old_identity() {
        let input = prefix(10_000);
        let mut trial = CooperativeTrial::default();
        trial.set_quota(0, 256).unwrap();
        for slot in 0..MAX_DEMANDS {
            trial.submit(slot, request(slot, input.clone(), 1)).unwrap();
        }
        assert!(trial.step().unwrap().is_none());
        trial.remove(0).unwrap();
        let mut replacement = request(1, input, 2);
        replacement.source_version = 2;
        trial.submit(1, replacement).unwrap();
        let mut completed = [false; MAX_DEMANDS];
        for _ in 0..1000 {
            if let Some((slot, result)) = trial.step().unwrap() {
                assert_ne!(slot, 0);
                if slot == 1 {
                    assert_eq!(result.request.source_version, 2);
                }
                completed[slot] = true;
            }
            if trial.is_idle() {
                break;
            }
        }
        assert!(trial.is_idle());
        assert_eq!(completed, [false, true, true, true]);
        assert_eq!(trial.report().discarded[0], 1);
    }
}
