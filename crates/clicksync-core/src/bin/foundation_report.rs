use clicksync_core::session::{AllocationStats, MemoryEnvelope, MemoryLayout};
use clicksync_core::{
    AppendOutcome, RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SessionMetadata,
    SessionSnapshot, SharedByteBudget,
};
use serde::Serialize;
use std::sync::Arc;
use std::time::Instant;

#[derive(Serialize)]
struct Workload {
    records: usize,
    datasets: usize,
    append_elapsed_ms: f64,
    verification_elapsed_ms: f64,
    reserved_peak_bytes: usize,
    reserved_after_reset_bytes: usize,
    reserved_after_release_bytes: usize,
    allocation_sites: AllocationStats,
}

#[derive(Serialize)]
struct FoundationReport {
    stage: &'static str,
    config: SessionConfig,
    layout: MemoryLayout,
    envelope: MemoryEnvelope,
    workloads: Vec<Workload>,
}

fn append_records(session: &mut Session, count: usize) -> Result<(), Box<dyn std::error::Error>> {
    session.begin_collecting(1_000_000, true, false)?;
    for index in 0..count {
        let sequence = (index + 1) as u64;
        let record = RawMouseRecord::new(
            (index % 7) as i32 - 3,
            (index % 5) as i32 - 2,
            1_000_000 + index as u64 * 1250,
            sequence,
            sequence,
            RawMouseMovementMode::Relative,
        );
        if session.append(record)? != AppendOutcome::Appended {
            return Err("controlled foundation replay rejected a record".into());
        }
    }
    session.stop();
    Ok(())
}

fn verify_snapshot(
    snapshot: &SessionSnapshot,
    expected_count: usize,
) -> Result<(), Box<dyn std::error::Error>> {
    if snapshot.event_count() != expected_count {
        return Err("foundation snapshot record count mismatch".into());
    }
    let mut sum_x = 0;
    let mut sum_y = 0;
    for (index, event) in snapshot.events().enumerate() {
        sum_x += (index % 7) as i64 - 3;
        sum_y += (index % 5) as i64 - 2;
        if event.capture_sequence != (index + 1) as u64
            || event.timing_sequence != (index + 1) as u64
            || event.raw_capture_ticks != 1_000_000 + index as u64 * 1250
            || event.raw_relative_ticks != index as u64 * 1250
            || event.logical_ticks != index as u64 * 1250
            || event.session_cumulative_x != sum_x
            || event.session_cumulative_y != sum_y
            || event.session_segment_id != 1
        {
            return Err(format!("foundation integer prefix mismatch at record {index}").into());
        }
    }
    Ok(())
}

fn run_workload(
    config: &SessionConfig,
    count: usize,
    datasets: usize,
) -> Result<Workload, Box<dyn std::error::Error>> {
    eprintln!("foundation: records={count}, datasets={datasets}");
    let budget = SharedByteBudget::new(config.managed_memory_budget_bytes);
    let mut session = Session::new(
        config.clone(),
        Arc::clone(&budget),
        SessionMetadata::default(),
    )?;
    let append_started = Instant::now();
    append_records(&mut session, count)?;
    let first = session.snapshot()?;
    let mut peak = budget.reserved_bytes();
    let second = if datasets == 2 {
        append_records(&mut session, count)?;
        let second = session.snapshot()?;
        peak = peak.max(budget.reserved_bytes());
        Some(second)
    } else {
        None
    };
    let append_elapsed_ms = append_started.elapsed().as_secs_f64() * 1000.0;
    let verify_started = Instant::now();
    verify_snapshot(&first, count)?;
    if let Some(second) = &second {
        verify_snapshot(second, count)?;
        if second.generation() == first.generation() {
            return Err("reset did not change the dataset generation".into());
        }
    }
    let verification_elapsed_ms = verify_started.elapsed().as_secs_f64() * 1000.0;
    session.reset()?;
    verify_snapshot(&first, count)?;
    drop(first);
    drop(second);
    let reserved_after_reset_bytes = budget.reserved_bytes();
    if session.pool_bytes() > clicksync_core::session::DEFAULT_POOL_BUDGET_BYTES {
        return Err("foundation pool exceeded its byte budget".into());
    }
    let allocation_sites = session.allocation_stats();
    session.clear_pool();
    drop(session);
    if budget.reserved_bytes() != 0 {
        return Err("foundation managed reservations were not released".into());
    }
    Ok(Workload {
        records: count,
        datasets,
        append_elapsed_ms,
        verification_elapsed_ms,
        reserved_peak_bytes: peak,
        reserved_after_reset_bytes,
        reserved_after_release_bytes: budget.reserved_bytes(),
        allocation_sites,
    })
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let config = SessionConfig::default();
    let session = Session::default_with_metadata(SessionMetadata::default())?;
    let layout = session.memory_layout();
    let envelope = config.memory_envelope(2)?;
    if envelope.total_dataset_bytes >= config.managed_memory_budget_bytes {
        return Err("two target datasets do not fit the managed memory envelope".into());
    }
    let workloads = [
        (100_000, 1),
        (1_000_000, 1),
        (config.max_session_events, 1),
        (config.max_session_events, 2),
    ]
    .into_iter()
    .map(|(records, datasets)| run_workload(&config, records, datasets))
    .collect::<Result<Vec<_>, _>>()?;
    let report = FoundationReport {
        stage: "Foundation",
        config,
        layout,
        envelope,
        workloads,
    };
    println!("{}", serde_json::to_string_pretty(&report)?);
    Ok(())
}
