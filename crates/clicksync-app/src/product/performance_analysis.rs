use clicksync_chart::TimeBasis;
use clicksync_core::SessionSnapshot;
use std::{ops::Range, sync::Arc};

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct PlotOptions {
    pub lines: bool,
    pub stems: bool,
    pub trend: bool,
    pub gaps: bool,
    pub gap_dataset: Option<u32>,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct PlotAnalysis {
    pub range: Option<(f64, f64)>,
    pub options: PlotOptions,
}

#[derive(Clone)]
pub struct Group {
    pub key: u32,
    pub snapshot: Arc<SessionSnapshot>,
    pub cpi: Option<f64>,
    pub identity: String,
}

pub fn time_ms(snapshot: &SessionSnapshot, index: usize, logical: bool) -> Option<f64> {
    let event = snapshot.event_at(index)?;
    let basis = if logical {
        TimeBasis::Logical
    } else {
        TimeBasis::RawCapture
    };
    Some(basis.ticks(event) as f64 * 1000.0 / snapshot.timestamp_frequency() as f64)
}

/// Exact report endpoints only; never interpolate across a pause or a missing report.
pub fn time_index(snapshot: &SessionSnapshot, time: f64, logical: bool) -> Option<usize> {
    if !time.is_finite() || time < 0.0 {
        return None;
    }
    (0..snapshot.event_count()).find(|index| {
        time_ms(snapshot, *index, logical).is_some_and(|value| (value - time).abs() <= 0.000001)
    })
}

pub fn index_range(
    snapshot: &SessionSnapshot,
    range: Option<(f64, f64)>,
    logical: bool,
) -> Range<usize> {
    let Some((start, end)) = range else {
        return 0..snapshot.event_count();
    };
    let mut result = snapshot.event_count()..0;
    for index in 0..snapshot.event_count() {
        if time_ms(snapshot, index, logical).is_some_and(|time| time >= start && time <= end) {
            result.start = result.start.min(index);
            result.end = index + 1;
        }
    }
    if result.start > result.end {
        0..0
    } else {
        result
    }
}

pub fn convert_range(
    snapshot: &SessionSnapshot,
    range: (f64, f64),
    from_logical: bool,
) -> Option<(f64, f64)> {
    convert_group_range(&[snapshot], range, from_logical)
}

pub fn convert_group_range(
    snapshots: &[&SessionSnapshot],
    range: (f64, f64),
    from_logical: bool,
) -> Option<(f64, f64)> {
    // An interval in an unordered raw timeline may not remain contiguous in
    // logical time. Refuse that conversion instead of changing its reports.
    let mut mapped: Option<(f64, f64)> = None;
    for snapshot in snapshots {
        for index in 0..snapshot.event_count() {
            let time = time_ms(snapshot, index, from_logical)?;
            if time >= range.0 && time <= range.1 {
                let time = time_ms(snapshot, index, !from_logical)?;
                mapped = Some(mapped.map_or((time, time), |(a, b)| (a.min(time), b.max(time))));
            }
        }
    }
    let mapped = mapped?;
    for snapshot in snapshots {
        for index in 0..snapshot.event_count() {
            let from = time_ms(snapshot, index, from_logical)?;
            let to = time_ms(snapshot, index, !from_logical)?;
            if (from >= range.0 && from <= range.1) != (to >= mapped.0 && to <= mapped.1) {
                return None;
            }
        }
    }
    Some(mapped)
}

pub fn group_identity(snapshot: &SessionSnapshot, cpi: Option<f64>) -> String {
    struct Fingerprint(u64);
    impl std::io::Write for Fingerprint {
        fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
            for byte in bytes {
                self.0 = (self.0 ^ u64::from(*byte)).wrapping_mul(0x100000001b3);
            }
            Ok(bytes.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }
    let mut hash = Fingerprint(0xcbf29ce484222325);
    serde_json::to_writer(
        &mut hash,
        &(
            snapshot.metadata(),
            snapshot.timestamp_frequency(),
            snapshot.segments(),
            snapshot.capture_evidence(),
        ),
    )
    .expect("serializable session identity");
    for event in snapshot.events() {
        serde_json::to_writer(&mut hash, &event).expect("serializable report");
    }
    format!(
        "{}-{:016x}-{:?}",
        snapshot.event_count(),
        hash.0,
        cpi.map(f64::to_bits)
    )
}

pub fn same_group(
    a: &SessionSnapshot,
    a_cpi: Option<f64>,
    b: &SessionSnapshot,
    b_cpi: Option<f64>,
) -> bool {
    a_cpi.map(f64::to_bits) == b_cpi.map(f64::to_bits)
        && a.timestamp_frequency() == b.timestamp_frequency()
        && a.capture_evidence() == b.capture_evidence()
        && a.metadata() == b.metadata()
        && a.segments() == b.segments()
        && a.event_count() == b.event_count()
        && a.events().eq(b.events())
}

pub fn filter_query(
    query: &mut clicksync_chart::ExactQueryResult,
    range: Option<(f64, f64)>,
    basis: TimeBasis,
    frequency: u64,
) {
    let Some((start, end)) = range else { return };
    let selected = |event| {
        let time = basis.ticks(event) as f64 * 1000.0 / frequency as f64;
        time >= start && time <= end
    };
    query.samples.retain(|sample| {
        query
            .records
            .get(sample.source_index - query.requested_range.start)
            .is_some_and(|event| selected(*event))
    });
    query.records.retain(|event| selected(*event));
    query.histogram = None;
}

pub fn report_gaps(
    snapshot: &SessionSnapshot,
    basis: TimeBasis,
    range: Option<(f64, f64)>,
) -> Vec<[f64; 2]> {
    let frequency = snapshot.timestamp_frequency() as f64;
    let mut intervals = Vec::new();
    for index in 1..snapshot.event_count() {
        let a = snapshot.event_at(index - 1).unwrap();
        let b = snapshot.event_at(index).unwrap();
        if basis == TimeBasis::RawCapture && a.session_segment_id != b.session_segment_id {
            continue;
        }
        let start = basis.ticks(a);
        let end = basis.ticks(b);
        if end <= start {
            continue;
        }
        let end_ms = end as f64 * 1000.0 / frequency;
        if range.is_some_and(|(a, b)| end_ms < a || end_ms > b) {
            continue;
        }
        intervals.push([start as f64 * 1000.0 / frequency, end_ms]);
    }
    let mut durations: Vec<_> = intervals.iter().map(|[a, b]| b - a).collect();
    durations.sort_by(f64::total_cmp);
    let median = if durations.is_empty() {
        0.0
    } else {
        let position = (durations.len() - 1) as f64 / 2.0;
        let lower = position.floor() as usize;
        let upper = position.ceil() as usize;
        durations[lower] + (durations[upper] - durations[lower]) * position.fract()
    };
    intervals.retain(|[a, b]| b - a > (median * 3.0).max(0.5));
    intervals.sort_by(|a, b| a[0].total_cmp(&b[0]));
    intervals
}

#[cfg(test)]
mod tests;
