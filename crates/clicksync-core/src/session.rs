use crate::budget::{BudgetExceeded, BudgetReservation, SharedByteBudget};
use crate::model::{RawMouseRecord, SessionEvent, SessionMetadata, SessionSegment};
use serde::{Deserialize, Serialize};
use std::fmt;
use std::mem::size_of;
use std::sync::Arc;

pub const DEFAULT_MAX_SESSION_EVENTS: usize = 16_000_000;
pub const DEFAULT_WARNING_EVENT_THRESHOLD: usize = 8_000_000;
pub const DEFAULT_CHUNK_CAPACITY: usize = 4096;
pub const DEFAULT_MANAGED_MEMORY_BUDGET_BYTES: usize = 4 * 1024 * 1024 * 1024;
pub const DEFAULT_POOL_BUDGET_BYTES: usize = 4 * 1024 * 1024;
pub const DEFAULT_TIMESTAMP_FREQUENCY: u64 = 10_000_000;
const DIRECTORY_PAGE_CAPACITY: usize = 64;
const ARC_ALLOCATION_HEADER_BYTES: usize = 2 * size_of::<usize>();

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct SessionConfig {
    pub max_session_events: usize,
    pub warning_event_threshold: usize,
    pub chunk_capacity: usize,
    pub managed_memory_budget_bytes: usize,
    pub timestamp_frequency: u64,
}

impl Default for SessionConfig {
    fn default() -> Self {
        Self {
            max_session_events: DEFAULT_MAX_SESSION_EVENTS,
            warning_event_threshold: DEFAULT_WARNING_EVENT_THRESHOLD,
            chunk_capacity: DEFAULT_CHUNK_CAPACITY,
            managed_memory_budget_bytes: DEFAULT_MANAGED_MEMORY_BUDGET_BYTES,
            timestamp_frequency: DEFAULT_TIMESTAMP_FREQUENCY,
        }
    }
}

impl SessionConfig {
    pub fn validate(&self) -> Result<(), SessionError> {
        if self.max_session_events == 0
            || self.warning_event_threshold == 0
            || self.chunk_capacity == 0
            || self.managed_memory_budget_bytes == 0
            || self.timestamp_frequency == 0
            || self.timestamp_frequency > i64::MAX as u64
        {
            return Err(SessionError::InvalidConfig(
                "invalid event limit, warning threshold, chunk size, budget, or clock frequency",
            ));
        }
        Ok(())
    }

    pub fn memory_envelope(&self, datasets: usize) -> Result<MemoryEnvelope, SessionError> {
        self.validate()?;
        let chunks = self.max_session_events.div_ceil(self.chunk_capacity);
        let directory_pages = chunks.div_ceil(DIRECTORY_PAGE_CAPACITY);
        let event_payload_bytes = checked_bytes(
            chunks
                .checked_mul(self.chunk_capacity)
                .ok_or(SessionError::ArithmeticOverflow)?,
            size_of::<SessionEvent>(),
        )?;
        let headers_bytes = checked_bytes(chunks, chunk_header_bytes())?;
        let directory_bytes = directory_bytes(directory_pages)?
            .checked_add(checked_bytes(directory_pages, page_bytes())?)
            .ok_or(SessionError::ArithmeticOverflow)?;
        let dataset_bytes = event_payload_bytes
            .checked_add(headers_bytes)
            .and_then(|bytes| bytes.checked_add(directory_bytes))
            .ok_or(SessionError::ArithmeticOverflow)?;
        Ok(MemoryEnvelope {
            datasets,
            chunks_per_dataset: chunks,
            event_payload_bytes_per_dataset: event_payload_bytes,
            chunk_header_bytes_per_dataset: headers_bytes,
            directory_bytes_per_dataset: directory_bytes,
            dataset_bytes,
            total_dataset_bytes: checked_bytes(datasets, dataset_bytes)?,
            managed_budget_bytes: self.managed_memory_budget_bytes,
            pool_limit_bytes: DEFAULT_POOL_BUDGET_BYTES,
        })
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub struct MemoryEnvelope {
    pub datasets: usize,
    pub chunks_per_dataset: usize,
    pub event_payload_bytes_per_dataset: usize,
    pub chunk_header_bytes_per_dataset: usize,
    pub directory_bytes_per_dataset: usize,
    pub dataset_bytes: usize,
    pub total_dataset_bytes: usize,
    pub managed_budget_bytes: usize,
    pub pool_limit_bytes: usize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub enum SessionStatus {
    Empty,
    Collecting,
    Paused,
    Stopped,
}

/// Completeness within the software's observed capture scope, not hardware delivery.
#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
pub enum CaptureIntegrity {
    Complete,
    Incomplete,
    #[default]
    Unknown,
}

/// Fixed evidence for this dataset. Watermarks, when present, cover the whole capture
/// generation (including the other selected device); retention drops are per dataset.
#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
pub struct CaptureEvidence {
    pub integrity: CaptureIntegrity,
    #[serde(default)]
    pub ignored_after_limit: u64,
    #[serde(default)]
    pub overflow: u64,
    #[serde(default)]
    pub disconnected: bool,
    #[serde(default)]
    pub generation: Option<u64>,
    #[serde(default)]
    pub received_sequence: Option<u64>,
    #[serde(default)]
    pub queued: Option<u64>,
    #[serde(default)]
    pub cutoff: Option<u64>,
}

impl CaptureEvidence {
    pub fn complete() -> Self {
        Self {
            integrity: CaptureIntegrity::Complete,
            ..Self::default()
        }
    }

    pub fn has_loss(&self) -> bool {
        self.ignored_after_limit > 0 || self.overflow > 0 || self.disconnected
    }
}

#[derive(Debug, Eq, PartialEq)]
pub enum SessionError {
    InvalidConfig(&'static str),
    BudgetExceeded(BudgetExceeded),
    ArithmeticOverflow,
    AllocationFailed,
}

impl fmt::Display for SessionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidConfig(message) => write!(formatter, "invalid session config: {message}"),
            Self::BudgetExceeded(error) => error.fmt(formatter),
            Self::ArithmeticOverflow => write!(formatter, "session arithmetic overflow"),
            Self::AllocationFailed => write!(formatter, "session allocation failed"),
        }
    }
}

impl std::error::Error for SessionError {}

impl From<BudgetExceeded> for SessionError {
    fn from(error: BudgetExceeded) -> Self {
        Self::BudgetExceeded(error)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AppendOutcome {
    Appended,
    IgnoredWhileStopped,
    PausedAtLimit,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub struct MemoryLayout {
    pub raw_record_bytes: usize,
    pub session_event_bytes: usize,
    pub segment_bytes: usize,
    pub active_chunk_payload_bytes: usize,
    pub sealed_chunk_header_bytes: usize,
    pub directory_page_bytes: usize,
    pub directory_root_bytes: usize,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize)]
pub struct AllocationStats {
    pub chunks_allocated: usize,
    pub chunks_reused: usize,
    pub directory_roots_allocated: usize,
    pub directory_pages_allocated: usize,
    pub snapshot_tail_copies: usize,
    pub segment_buffers_allocated: usize,
}

struct Chunk {
    events: Vec<SessionEvent>,
    _reservation: BudgetReservation,
}

impl Chunk {
    fn new(capacity: usize, budget: &Arc<SharedByteBudget>) -> Result<Self, SessionError> {
        let bytes = checked_bytes(capacity, size_of::<SessionEvent>())?
            .checked_add(chunk_header_bytes())
            .ok_or(SessionError::ArithmeticOverflow)?;
        let reservation = budget.try_reserve(bytes)?;
        let events = allocate_vec(capacity)?;
        Ok(Self {
            events,
            _reservation: reservation,
        })
    }
}

struct DirectoryPage {
    chunks: Vec<Arc<Chunk>>,
    _reservation: BudgetReservation,
}

impl DirectoryPage {
    fn new(budget: &Arc<SharedByteBudget>) -> Result<Self, SessionError> {
        let reservation = budget.try_reserve(page_bytes())?;
        Ok(Self {
            chunks: allocate_vec(DIRECTORY_PAGE_CAPACITY)?,
            _reservation: reservation,
        })
    }

    fn copy(&self, budget: &Arc<SharedByteBudget>) -> Result<Self, SessionError> {
        let mut page = Self::new(budget)?;
        page.chunks.extend(self.chunks.iter().cloned());
        Ok(page)
    }
}

struct Directory {
    pages: Vec<Option<Arc<DirectoryPage>>>,
    sealed_count: usize,
    _reservation: BudgetReservation,
}

impl Directory {
    fn new(page_count: usize, budget: &Arc<SharedByteBudget>) -> Result<Self, SessionError> {
        let reservation = budget.try_reserve(directory_bytes(page_count)?)?;
        let mut pages = allocate_vec(page_count)?;
        pages.resize_with(page_count, || None);
        Ok(Self {
            pages,
            sealed_count: 0,
            _reservation: reservation,
        })
    }

    fn copy(&self, budget: &Arc<SharedByteBudget>) -> Result<Self, SessionError> {
        let mut directory = Self::new(self.pages.len(), budget)?;
        directory.pages.clone_from_slice(&self.pages);
        directory.sealed_count = self.sealed_count;
        Ok(directory)
    }

    fn event_at(&self, index: usize, chunk_capacity: usize) -> Option<SessionEvent> {
        let chunk_index = index / chunk_capacity;
        let page = self
            .pages
            .get(chunk_index / DIRECTORY_PAGE_CAPACITY)?
            .as_ref()?;
        page.chunks
            .get(chunk_index % DIRECTORY_PAGE_CAPACITY)?
            .events
            .get(index % chunk_capacity)
            .copied()
    }
}

struct SegmentBuffer {
    segments: Vec<SessionSegment>,
    _reservation: BudgetReservation,
}

impl SegmentBuffer {
    fn copy_with_capacity(
        previous: &[SessionSegment],
        capacity: usize,
        budget: &Arc<SharedByteBudget>,
    ) -> Result<Self, SessionError> {
        let bytes = checked_bytes(capacity, size_of::<SessionSegment>())?
            .checked_add(size_of::<Self>() + ARC_ALLOCATION_HEADER_BYTES)
            .ok_or(SessionError::ArithmeticOverflow)?;
        let reservation = budget.try_reserve(bytes)?;
        let mut segments = allocate_vec(capacity)?;
        segments.extend_from_slice(previous);
        Ok(Self {
            segments,
            _reservation: reservation,
        })
    }
}

struct MetadataOwner {
    metadata: SessionMetadata,
    _reservation: BudgetReservation,
}

pub struct Session {
    config: SessionConfig,
    budget: Arc<SharedByteBudget>,
    metadata: Arc<MetadataOwner>,
    generation: u64,
    status: SessionStatus,
    finalized: bool,
    capture_evidence: CaptureEvidence,
    session_revision: u64,
    raw_origin_ticks: Option<u64>,
    last_logical_ticks: u64,
    segment_raw_start_ticks: u64,
    segment_logical_start_ticks: u64,
    segment_has_anchor: bool,
    current_segment_id: u32,
    events_count: usize,
    summary_sum_x: i64,
    summary_sum_y: i64,
    summary_path_counts: f64,
    directory: Option<Arc<Directory>>,
    active_chunk: Option<Chunk>,
    segments: Option<Arc<SegmentBuffer>>,
    pool: Vec<Chunk>,
    pool_bytes: usize,
    _pool_directory_reservation: BudgetReservation,
    allocation_stats: AllocationStats,
}

impl Session {
    pub fn new(
        config: SessionConfig,
        budget: Arc<SharedByteBudget>,
        metadata: SessionMetadata,
    ) -> Result<Self, SessionError> {
        config.validate()?;
        let metadata_bytes = metadata.display_name.capacity()
            + metadata.device_id.capacity()
            + metadata.path_summary.capacity()
            + size_of::<MetadataOwner>()
            + ARC_ALLOCATION_HEADER_BYTES;
        let metadata_reservation = budget.try_reserve(metadata_bytes)?;
        let metadata = Arc::new(MetadataOwner {
            metadata,
            _reservation: metadata_reservation,
        });
        let chunk_bytes = checked_bytes(config.chunk_capacity, size_of::<SessionEvent>())?
            .checked_add(chunk_header_bytes())
            .ok_or(SessionError::ArithmeticOverflow)?;
        let pool_capacity = (DEFAULT_POOL_BUDGET_BYTES / chunk_bytes)
            .min(config.max_session_events.div_ceil(config.chunk_capacity));
        let pool_directory_reservation =
            budget.try_reserve(checked_bytes(pool_capacity, size_of::<Chunk>())?)?;
        let pool = allocate_vec(pool_capacity)?;
        Ok(Self {
            config,
            budget,
            metadata,
            generation: 0,
            status: SessionStatus::Empty,
            finalized: false,
            capture_evidence: CaptureEvidence::complete(),
            session_revision: 0,
            raw_origin_ticks: None,
            last_logical_ticks: 0,
            segment_raw_start_ticks: 0,
            segment_logical_start_ticks: 0,
            segment_has_anchor: false,
            current_segment_id: 0,
            events_count: 0,
            summary_sum_x: 0,
            summary_sum_y: 0,
            summary_path_counts: 0.0,
            directory: None,
            active_chunk: None,
            segments: None,
            pool,
            pool_bytes: 0,
            _pool_directory_reservation: pool_directory_reservation,
            allocation_stats: AllocationStats::default(),
        })
    }

    pub fn default_with_metadata(metadata: SessionMetadata) -> Result<Self, SessionError> {
        let config = SessionConfig::default();
        let budget = SharedByteBudget::new(config.managed_memory_budget_bytes);
        Self::new(config, budget, metadata)
    }

    pub fn metadata(&self) -> &SessionMetadata {
        &self.metadata.metadata
    }

    pub fn config(&self) -> &SessionConfig {
        &self.config
    }

    pub fn budget(&self) -> &Arc<SharedByteBudget> {
        &self.budget
    }

    pub fn generation(&self) -> u64 {
        self.generation
    }

    pub fn status(&self) -> SessionStatus {
        self.status
    }

    pub fn finalized(&self) -> bool {
        self.finalized
    }

    pub fn capture_evidence(&self) -> CaptureEvidence {
        self.capture_evidence
    }

    pub fn integrity(&self) -> CaptureIntegrity {
        self.capture_evidence.integrity
    }

    pub fn set_capture_evidence(&mut self, mut evidence: CaptureEvidence) {
        // Later ACKs and metadata cannot erase loss already observed by this writer.
        evidence.ignored_after_limit = evidence
            .ignored_after_limit
            .max(self.capture_evidence.ignored_after_limit);
        evidence.overflow = evidence.overflow.max(self.capture_evidence.overflow);
        evidence.disconnected |= self.capture_evidence.disconnected;
        if self.integrity() == CaptureIntegrity::Incomplete || evidence.has_loss() {
            evidence.integrity = CaptureIntegrity::Incomplete;
        } else if self.integrity() == CaptureIntegrity::Unknown
            && evidence.integrity == CaptureIntegrity::Complete
        {
            evidence.integrity = CaptureIntegrity::Unknown;
        }
        if evidence.integrity != self.integrity() {
            self.session_revision = self.session_revision.wrapping_add(1);
        }
        self.capture_evidence = evidence;
    }

    fn record_retention_drop(&mut self) {
        let mut evidence = self.capture_evidence;
        evidence.ignored_after_limit = evidence.ignored_after_limit.saturating_add(1);
        evidence.integrity = CaptureIntegrity::Incomplete;
        self.set_capture_evidence(evidence);
    }

    pub fn session_revision(&self) -> u64 {
        self.session_revision
    }

    pub fn event_count(&self) -> usize {
        self.events_count
    }

    pub fn can_continue(&self) -> bool {
        self.events_count > 0 && !self.finalized
    }

    pub fn warning_reached(&self) -> bool {
        self.events_count >= self.config.warning_event_threshold
    }

    pub fn summary_sum_x(&self) -> i64 {
        self.summary_sum_x
    }

    pub fn summary_sum_y(&self) -> i64 {
        self.summary_sum_y
    }

    pub fn summary_path_counts(&self) -> f64 {
        self.summary_path_counts
    }

    pub fn allocation_stats(&self) -> AllocationStats {
        self.allocation_stats
    }

    pub fn pool_bytes(&self) -> usize {
        self.pool_bytes
    }

    pub fn clear_pool(&mut self) {
        self.pool.clear();
        self.pool_bytes = 0;
    }

    pub fn reset(&mut self) -> Result<(), SessionError> {
        let generation = self
            .generation
            .checked_add(1)
            .ok_or(SessionError::ArithmeticOverflow)?;
        if let Some(chunk) = self.active_chunk.take() {
            self.recycle_chunk(chunk);
        }
        if let Some(directory) = self.directory.take()
            && let Ok(directory) = Arc::try_unwrap(directory)
        {
            for page in directory.pages.into_iter().flatten() {
                if let Ok(page) = Arc::try_unwrap(page) {
                    for chunk in page.chunks {
                        if let Ok(chunk) = Arc::try_unwrap(chunk) {
                            self.recycle_chunk(chunk);
                        }
                    }
                }
            }
        }
        self.segments = None;
        self.generation = generation;
        self.status = SessionStatus::Empty;
        self.finalized = false;
        self.capture_evidence = CaptureEvidence::complete();
        self.session_revision = self.session_revision.wrapping_add(1);
        self.raw_origin_ticks = None;
        self.last_logical_ticks = 0;
        self.segment_raw_start_ticks = 0;
        self.segment_logical_start_ticks = 0;
        self.segment_has_anchor = false;
        self.current_segment_id = 0;
        self.events_count = 0;
        self.summary_sum_x = 0;
        self.summary_sum_y = 0;
        self.summary_path_counts = 0.0;
        Ok(())
    }

    pub fn begin_collecting(
        &mut self,
        started_at_raw_capture_ticks: u64,
        start_fresh: bool,
        use_started_at_as_origin: bool,
    ) -> Result<(), SessionError> {
        if started_at_raw_capture_ticks > i64::MAX as u64 {
            return Err(SessionError::ArithmeticOverflow);
        }
        if start_fresh {
            self.reset()?;
        }
        let segment_id = self
            .current_segment_id
            .checked_add(1)
            .ok_or(SessionError::ArithmeticOverflow)?;
        self.prepare_segment_buffer()?;
        Arc::get_mut(
            self.segments
                .as_mut()
                .ok_or(SessionError::AllocationFailed)?,
        )
        .ok_or(SessionError::AllocationFailed)?
        .segments
        .push(SessionSegment {
            segment_id,
            started_at_raw_capture_ticks,
        });
        self.status = SessionStatus::Collecting;
        self.finalized = false;
        self.segment_raw_start_ticks = started_at_raw_capture_ticks;
        self.segment_logical_start_ticks = self.last_logical_ticks;
        self.segment_has_anchor = use_started_at_as_origin || self.events_count > 0;
        if use_started_at_as_origin && self.raw_origin_ticks.is_none() {
            self.raw_origin_ticks = Some(started_at_raw_capture_ticks);
        }
        self.current_segment_id = segment_id;
        self.session_revision = self.session_revision.wrapping_add(1);
        Ok(())
    }

    pub fn pause(&mut self) {
        self.status = SessionStatus::Paused;
        self.segment_has_anchor = false;
        self.session_revision = self.session_revision.wrapping_add(1);
    }

    pub fn stop(&mut self) {
        self.status = SessionStatus::Stopped;
        self.finalized = self.events_count > 0;
        self.segment_has_anchor = false;
        self.session_revision = self.session_revision.wrapping_add(1);
    }

    pub fn append(&mut self, mut record: RawMouseRecord) -> Result<AppendOutcome, SessionError> {
        if self.status != SessionStatus::Collecting {
            if self.events_count >= self.config.max_session_events {
                self.record_retention_drop();
            }
            return Ok(AppendOutcome::IgnoredWhileStopped);
        }
        if self.events_count >= self.config.max_session_events {
            self.record_retention_drop();
            self.pause();
            return Ok(AppendOutcome::PausedAtLimit);
        }
        if record.raw_capture_ticks > i64::MAX as u64
            || record.capture_sequence > i64::MAX as u64
            || record.timing_sequence > i64::MAX as u64
        {
            return Err(SessionError::ArithmeticOverflow);
        }
        if record.movement_mode == crate::model::RawMouseMovementMode::Unknown {
            record.movement_mode = if record.raw_mouse_flags & 1 == 1 {
                crate::model::RawMouseMovementMode::Absolute
            } else {
                crate::model::RawMouseMovementMode::Relative
            };
        }
        let segment_anchor = if !self.segment_has_anchor && self.events_count == 0 {
            record.raw_capture_ticks
        } else {
            self.segment_raw_start_ticks
        };
        let segment_elapsed_ticks = record.raw_capture_ticks.saturating_sub(segment_anchor);
        // Match ResolveLogicalTicks: unchecked signed addition, then monotonic clamping.
        let logical_ticks = (self.segment_logical_start_ticks as i64)
            .wrapping_add(segment_elapsed_ticks as i64)
            .max(self.last_logical_ticks as i64) as u64;
        let raw_origin = self.raw_origin_ticks.unwrap_or(record.raw_capture_ticks);
        let (delta_x, delta_y) = record.relative_delta();
        let summary_sum_x = self
            .summary_sum_x
            .checked_add(i64::from(delta_x))
            .ok_or(SessionError::ArithmeticOverflow)?;
        let summary_sum_y = self
            .summary_sum_y
            .checked_add(i64::from(delta_y))
            .ok_or(SessionError::ArithmeticOverflow)?;
        let path_counts = ((f64::from(delta_x) * f64::from(delta_x))
            + (f64::from(delta_y) * f64::from(delta_y)))
        .sqrt();
        let event = SessionEvent {
            raw_delta_x: record.raw_delta_x,
            raw_delta_y: record.raw_delta_y,
            delta_x,
            delta_y,
            button_flags: record.button_flags,
            raw_mouse_flags: record.raw_mouse_flags,
            button_data: record.button_data,
            extra_information: record.extra_information,
            raw_capture_ticks: record.raw_capture_ticks,
            raw_relative_ticks: record.raw_capture_ticks.saturating_sub(raw_origin),
            logical_ticks,
            packet_kind: record.packet_kind(),
            movement_mode: record.movement_mode,
            session_cumulative_x: summary_sum_x,
            session_cumulative_y: summary_sum_y,
            session_segment_id: self.current_segment_id,
            capture_sequence: record.capture_sequence,
            timing_sequence: if record.timing_sequence == 0 {
                record.capture_sequence
            } else {
                record.timing_sequence
            },
        };
        self.prepare_active_chunk()?;
        self.active_chunk
            .as_mut()
            .ok_or(SessionError::AllocationFailed)?
            .events
            .push(event);
        self.events_count += 1;
        self.summary_sum_x = summary_sum_x;
        self.summary_sum_y = summary_sum_y;
        self.summary_path_counts += path_counts;
        self.raw_origin_ticks = Some(raw_origin);
        self.segment_raw_start_ticks = segment_anchor;
        self.segment_has_anchor = true;
        self.last_logical_ticks = logical_ticks;
        self.session_revision = self.session_revision.wrapping_add(1);
        Ok(AppendOutcome::Appended)
    }

    pub fn snapshot(&mut self) -> Result<SessionSnapshot, SessionError> {
        let tail = if let Some(active) = self.active_chunk.as_ref() {
            let mut copy = Chunk::new(active.events.len(), &self.budget)?;
            copy.events.extend_from_slice(&active.events);
            self.allocation_stats.snapshot_tail_copies += 1;
            Some(Arc::new(copy))
        } else {
            None
        };
        Ok(SessionSnapshot {
            generation: self.generation,
            status: self.status,
            finalized: self.finalized,
            capture_evidence: self.capture_evidence,
            can_continue: self.can_continue(),
            session_revision: self.session_revision,
            event_count: self.events_count,
            chunk_capacity: self.config.chunk_capacity,
            timestamp_frequency: self.config.timestamp_frequency,
            summary_sum_x: self.summary_sum_x,
            summary_sum_y: self.summary_sum_y,
            summary_path_counts: self.summary_path_counts,
            metadata: Arc::clone(&self.metadata),
            directory: self.directory.as_ref().map(Arc::clone),
            tail,
            segments: self.segments.as_ref().map(Arc::clone),
        })
    }

    pub fn memory_layout(&self) -> MemoryLayout {
        let page_count = self
            .config
            .max_session_events
            .div_ceil(self.config.chunk_capacity)
            .div_ceil(DIRECTORY_PAGE_CAPACITY);
        MemoryLayout {
            raw_record_bytes: size_of::<RawMouseRecord>(),
            session_event_bytes: size_of::<SessionEvent>(),
            segment_bytes: size_of::<SessionSegment>(),
            active_chunk_payload_bytes: self.config.chunk_capacity * size_of::<SessionEvent>(),
            sealed_chunk_header_bytes: chunk_header_bytes(),
            directory_page_bytes: page_bytes(),
            directory_root_bytes: directory_bytes(page_count).unwrap_or(usize::MAX),
        }
    }

    fn prepare_segment_buffer(&mut self) -> Result<(), SessionError> {
        let previous = self
            .segments
            .as_ref()
            .map_or(&[][..], |buffer| buffer.segments.as_slice());
        let needs_copy = self.segments.as_ref().is_none_or(|buffer| {
            Arc::strong_count(buffer) > 1 || buffer.segments.len() == buffer.segments.capacity()
        });
        if needs_copy {
            let capacity = (previous.len().saturating_add(1))
                .next_power_of_two()
                .max(4);
            let buffer = SegmentBuffer::copy_with_capacity(previous, capacity, &self.budget)?;
            self.segments = Some(Arc::new(buffer));
            self.allocation_stats.segment_buffers_allocated += 1;
        }
        Ok(())
    }

    fn prepare_active_chunk(&mut self) -> Result<(), SessionError> {
        if self
            .active_chunk
            .as_ref()
            .is_some_and(|chunk| chunk.events.len() < self.config.chunk_capacity)
        {
            return Ok(());
        }
        if self.active_chunk.is_some() {
            self.prepare_directory_slot()?;
            let directory = Arc::get_mut(
                self.directory
                    .as_mut()
                    .ok_or(SessionError::AllocationFailed)?,
            )
            .ok_or(SessionError::AllocationFailed)?;
            let page_index = directory.sealed_count / DIRECTORY_PAGE_CAPACITY;
            let page = Arc::get_mut(
                directory.pages[page_index]
                    .as_mut()
                    .ok_or(SessionError::AllocationFailed)?,
            )
            .ok_or(SessionError::AllocationFailed)?;
            page.chunks.push(Arc::new(
                self.active_chunk
                    .take()
                    .ok_or(SessionError::AllocationFailed)?,
            ));
            directory.sealed_count += 1;
        }
        self.active_chunk = if let Some(chunk) = self.pool.pop() {
            self.pool_bytes -= chunk._reservation.bytes();
            self.allocation_stats.chunks_reused += 1;
            Some(chunk)
        } else {
            let chunk = Chunk::new(self.config.chunk_capacity, &self.budget)?;
            self.allocation_stats.chunks_allocated += 1;
            Some(chunk)
        };
        Ok(())
    }

    fn prepare_directory_slot(&mut self) -> Result<(), SessionError> {
        if self.directory.is_none() {
            let pages = self
                .config
                .max_session_events
                .div_ceil(self.config.chunk_capacity)
                .div_ceil(DIRECTORY_PAGE_CAPACITY);
            self.directory = Some(Arc::new(Directory::new(pages, &self.budget)?));
            self.allocation_stats.directory_roots_allocated += 1;
        } else if self
            .directory
            .as_ref()
            .is_some_and(|directory| Arc::strong_count(directory) > 1)
        {
            let copy = self
                .directory
                .as_ref()
                .ok_or(SessionError::AllocationFailed)?
                .copy(&self.budget)?;
            self.directory = Some(Arc::new(copy));
            self.allocation_stats.directory_roots_allocated += 1;
        }
        let directory = Arc::get_mut(
            self.directory
                .as_mut()
                .ok_or(SessionError::AllocationFailed)?,
        )
        .ok_or(SessionError::AllocationFailed)?;
        let page_index = directory.sealed_count / DIRECTORY_PAGE_CAPACITY;
        let slot = directory
            .pages
            .get_mut(page_index)
            .ok_or(SessionError::ArithmeticOverflow)?;
        if let Some(page) = slot {
            if Arc::strong_count(page) > 1 {
                let copy = page.copy(&self.budget)?;
                *slot = Some(Arc::new(copy));
                self.allocation_stats.directory_pages_allocated += 1;
            }
        } else {
            *slot = Some(Arc::new(DirectoryPage::new(&self.budget)?));
            self.allocation_stats.directory_pages_allocated += 1;
        }
        Ok(())
    }

    fn recycle_chunk(&mut self, mut chunk: Chunk) {
        let bytes = chunk._reservation.bytes();
        if chunk.events.capacity() == self.config.chunk_capacity
            && self.pool_bytes.saturating_add(bytes) <= DEFAULT_POOL_BUDGET_BYTES
            && self.pool.len() < self.pool.capacity()
        {
            chunk.events.clear();
            self.pool.push(chunk);
            self.pool_bytes += bytes;
        }
    }
}

pub struct SessionSnapshot {
    generation: u64,
    status: SessionStatus,
    finalized: bool,
    capture_evidence: CaptureEvidence,
    can_continue: bool,
    session_revision: u64,
    event_count: usize,
    chunk_capacity: usize,
    timestamp_frequency: u64,
    summary_sum_x: i64,
    summary_sum_y: i64,
    summary_path_counts: f64,
    metadata: Arc<MetadataOwner>,
    directory: Option<Arc<Directory>>,
    tail: Option<Arc<Chunk>>,
    segments: Option<Arc<SegmentBuffer>>,
}

impl Clone for SessionSnapshot {
    fn clone(&self) -> Self {
        Self {
            generation: self.generation,
            status: self.status,
            finalized: self.finalized,
            capture_evidence: self.capture_evidence,
            can_continue: self.can_continue,
            session_revision: self.session_revision,
            event_count: self.event_count,
            chunk_capacity: self.chunk_capacity,
            timestamp_frequency: self.timestamp_frequency,
            summary_sum_x: self.summary_sum_x,
            summary_sum_y: self.summary_sum_y,
            summary_path_counts: self.summary_path_counts,
            metadata: Arc::clone(&self.metadata),
            directory: self.directory.as_ref().map(Arc::clone),
            tail: self.tail.as_ref().map(Arc::clone),
            segments: self.segments.as_ref().map(Arc::clone),
        }
    }
}

impl SessionSnapshot {
    pub fn capture_evidence(&self) -> CaptureEvidence {
        self.capture_evidence
    }

    pub fn integrity(&self) -> CaptureIntegrity {
        self.capture_evidence.integrity
    }

    pub fn generation(&self) -> u64 {
        self.generation
    }

    pub fn status(&self) -> SessionStatus {
        self.status
    }

    pub fn finalized(&self) -> bool {
        self.finalized
    }

    pub fn can_continue(&self) -> bool {
        self.can_continue
    }

    pub fn session_revision(&self) -> u64 {
        self.session_revision
    }

    pub fn event_count(&self) -> usize {
        self.event_count
    }

    pub fn timestamp_frequency(&self) -> u64 {
        self.timestamp_frequency
    }

    pub fn summary_sum_x(&self) -> i64 {
        self.summary_sum_x
    }

    pub fn summary_sum_y(&self) -> i64 {
        self.summary_sum_y
    }

    pub fn summary_path_counts(&self) -> f64 {
        self.summary_path_counts
    }

    pub fn metadata(&self) -> &SessionMetadata {
        &self.metadata.metadata
    }

    pub fn segments(&self) -> &[SessionSegment] {
        self.segments
            .as_ref()
            .map_or(&[], |segments| segments.segments.as_slice())
    }

    pub fn segment(&self, id: u32) -> Option<&SessionSegment> {
        let segments = self.segments();
        segments
            .binary_search_by_key(&id, |segment| segment.segment_id)
            .ok()
            .map(|index| &segments[index])
    }

    pub fn events(&self) -> impl ExactSizeIterator<Item = SessionEvent> + '_ {
        (0..self.event_count).map(|index| {
            self.event_at(index)
                .expect("published snapshot prefix has all records")
        })
    }

    pub fn event_at(&self, index: usize) -> Option<SessionEvent> {
        if index >= self.event_count {
            return None;
        }
        let sealed_events = self
            .directory
            .as_ref()
            .map_or(0, |directory| directory.sealed_count * self.chunk_capacity);
        if index < sealed_events {
            self.directory
                .as_ref()?
                .event_at(index, self.chunk_capacity)
        } else {
            self.tail
                .as_ref()?
                .events
                .get(index - sealed_events)
                .copied()
        }
    }

    pub fn tail_event_count(&self) -> usize {
        self.tail.as_ref().map_or(0, |tail| tail.events.len())
    }

    pub fn managed_snapshot_bytes(&self) -> usize {
        self.tail
            .as_ref()
            .map_or(0, |tail| tail._reservation.bytes())
    }

    pub fn shares_committed_directory_with(&self, other: &Self) -> bool {
        match (&self.directory, &other.directory) {
            (Some(first), Some(second)) => Arc::ptr_eq(first, second),
            (None, None) => true,
            _ => false,
        }
    }
}

fn allocate_vec<T>(capacity: usize) -> Result<Vec<T>, SessionError> {
    let mut values = Vec::new();
    values
        .try_reserve_exact(capacity)
        .map_err(|_| SessionError::AllocationFailed)?;
    Ok(values)
}

fn checked_bytes(count: usize, item_bytes: usize) -> Result<usize, SessionError> {
    count
        .checked_mul(item_bytes)
        .ok_or(SessionError::ArithmeticOverflow)
}

fn chunk_header_bytes() -> usize {
    size_of::<Chunk>() + ARC_ALLOCATION_HEADER_BYTES
}

fn page_bytes() -> usize {
    DIRECTORY_PAGE_CAPACITY * size_of::<Arc<Chunk>>()
        + size_of::<DirectoryPage>()
        + ARC_ALLOCATION_HEADER_BYTES
}

fn directory_bytes(pages: usize) -> Result<usize, SessionError> {
    checked_bytes(pages, size_of::<Option<Arc<DirectoryPage>>>())?
        .checked_add(size_of::<Directory>() + ARC_ALLOCATION_HEADER_BYTES)
        .ok_or(SessionError::ArithmeticOverflow)
}
