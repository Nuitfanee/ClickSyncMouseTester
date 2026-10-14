//! Two-pass seekable import: scan anchors without retaining rows, then append each bounded row.
use super::*;
use serde::de::{DeserializeSeed, Error as _, IgnoredAny, MapAccess, SeqAccess, Visitor};
use std::io::{Read, Seek, SeekFrom, Write};
use std::sync::Arc;

pub const MAX_IMPORT_BYTES: u64 = 256 * 1024 * 1024;
pub struct ImportedSession {
    pub snapshot: SessionSnapshot,
    pub effective_cpi: Option<f64>,
}
mod scan;
struct Scan {
    version: u32,
    mouse: ExchangeMouseDocument,
    session: ExchangeSessionDocument,
    segments: Vec<Anchor>,
}
struct Anchor {
    id: u32,
    ticks: u64,
    sequence: u64,
    offset: u64,
}
pub fn import_reader(
    mut reader: impl Read + Seek,
    config: SessionConfig,
    budget: Arc<crate::SharedByteBudget>,
) -> Result<ImportedSession, ExchangeError> {
    let size = reader
        .seek(SeekFrom::End(0))
        .map_err(serde_json::Error::io)?;
    if size > MAX_IMPORT_BYTES {
        return Err(ExchangeError::Invalid("file exceeds the import byte limit"));
    }
    reader.rewind().map_err(serde_json::Error::io)?;
    let scan = scan::scan(&mut reader, config.max_session_events)?;
    if !(MINIMUM_SUPPORTED_FORMAT_VERSION..=CURRENT_FORMAT_VERSION).contains(&scan.version) {
        return Err(ExchangeError::Invalid("unsupported format version"));
    }
    if scan.session.host_timestamp_frequency == 0 || scan.segments.is_empty() {
        return Err(ExchangeError::Invalid(
            "missing segments or timestamp frequency",
        ));
    }
    validate_capture_evidence(scan.session.capture_evidence.as_ref())?;
    if scan.segments.len() > config.max_session_events {
        return Err(ExchangeError::Invalid(
            "exchange exceeds the session event limit",
        ));
    }
    let mut previous = (0, 0, 0);
    for a in &scan.segments {
        if a.id == 0 || a.id <= previous.0 || a.ticks < previous.1 || a.sequence < previous.2 {
            return Err(ExchangeError::Invalid("segment anchors are out of order"));
        }
        previous = (a.id, a.ticks, a.sequence);
    }
    let metadata = SessionMetadata {
        display_name: scan.mouse.display_name.unwrap_or_default(),
        device_id: scan.mouse.device_id.unwrap_or_default(),
        vendor_id: scan.mouse.vendor_id,
        product_id: scan.mouse.product_id,
        button_count: scan.mouse.button_count,
        is_virtual: scan.mouse.is_virtual,
        path_summary: scan.mouse.path_summary.unwrap_or_default(),
    };
    let mut session = Session::new(config, budget, metadata)?;
    for (index, anchor) in scan.segments.iter().enumerate() {
        let ticks = convert_ticks(
            anchor.ticks,
            scan.session.host_timestamp_frequency,
            session.config().timestamp_frequency,
        )?;
        session.begin_collecting(ticks, index == 0, false)?;
        reader
            .seek(SeekFrom::Start(anchor.offset))
            .map_err(serde_json::Error::io)?;
        let mut decoder = serde_json::Deserializer::from_reader(&mut reader);
        let mut row_state = RowState {
            session: &mut session,
            frequency: scan.session.host_timestamp_frequency,
            ticks,
            sequence: anchor.sequence,
            timing: 0,
            count: 0,
        };
        SegmentSeed(&mut row_state).deserialize(&mut decoder)?;
        if row_state.count == 0 {
            return Err(ExchangeError::Invalid("segment has no packet rows"));
        }
    }
    session.stop();
    validate_capture_event_count(
        scan.session.capture_evidence.as_ref(),
        session.event_count(),
    )?;
    session.set_capture_evidence(scan.session.capture_evidence.unwrap_or_default());
    Ok(ImportedSession {
        snapshot: session.snapshot()?,
        effective_cpi: scan.session.effective_cpi,
    })
}
struct RowState<'a> {
    session: &'a mut Session,
    frequency: u64,
    ticks: u64,
    sequence: u64,
    timing: u64,
    count: usize,
}
struct SegmentSeed<'a, 'b>(&'a mut RowState<'b>);
impl<'de> DeserializeSeed<'de> for SegmentSeed<'_, '_> {
    type Value = ();
    fn deserialize<D: serde::Deserializer<'de>>(self, d: D) -> Result<(), D::Error> {
        d.deserialize_map(self)
    }
}
impl<'de> Visitor<'de> for SegmentSeed<'_, '_> {
    type Value = ();
    fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
        f.write_str("segment")
    }
    fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<(), M::Error> {
        let mut found = false;
        while let Some(key) = map.next_key::<String>()? {
            if key.eq_ignore_ascii_case("PacketRows")
                || key.eq_ignore_ascii_case("packet_rows")
                || key.eq_ignore_ascii_case("r")
            {
                if found {
                    return Err(M::Error::custom("duplicate rows"));
                }
                found = true;
                map.next_value_seed(RowsSeed(self.0))?;
            } else {
                map.next_value::<IgnoredAny>()?;
            }
        }
        if !found {
            return Err(M::Error::custom("missing rows"));
        }
        Ok(())
    }
}
struct RowsSeed<'a, 'b>(&'a mut RowState<'b>);
impl<'de> DeserializeSeed<'de> for RowsSeed<'_, '_> {
    type Value = ();
    fn deserialize<D: serde::Deserializer<'de>>(self, d: D) -> Result<(), D::Error> {
        d.deserialize_seq(self)
    }
}
impl<'de> Visitor<'de> for RowsSeed<'_, '_> {
    type Value = ();
    fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
        f.write_str("packet rows")
    }
    fn visit_seq<S: SeqAccess<'de>>(self, mut seq: S) -> Result<(), S::Error> {
        while let Some(row) = seq.next_element::<Option<BoundedRow>>()? {
            if let Some(row) = row {
                self.0
                    .append(&row.values[..row.len])
                    .map_err(S::Error::custom)?;
            }
        }
        Ok(())
    }
}
struct BoundedRow {
    values: [i64; 11],
    len: usize,
}
impl<'de> Deserialize<'de> for BoundedRow {
    fn deserialize<D: serde::Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        struct RowVisitor;
        impl<'de> Visitor<'de> for RowVisitor {
            type Value = BoundedRow;
            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("5, 8, 9, 10 or 11 integer columns")
            }
            fn visit_seq<S: SeqAccess<'de>>(self, mut seq: S) -> Result<BoundedRow, S::Error> {
                let mut row = BoundedRow {
                    values: [0; 11],
                    len: 0,
                };
                while let Some(value) = seq.next_element()? {
                    if row.len == 11 {
                        return Err(S::Error::custom("invalid compact packet row length"));
                    }
                    row.values[row.len] = value;
                    row.len += 1;
                }
                if ![5, 8, 9, 10, 11].contains(&row.len) {
                    return Err(S::Error::custom("invalid compact packet row length"));
                }
                Ok(row)
            }
        }
        d.deserialize_seq(RowVisitor)
    }
}
impl RowState<'_> {
    fn append(&mut self, row: &[i64]) -> Result<(), ExchangeError> {
        let long = row.len() >= 8;
        let delta = value_u64(row[if long { 6 } else { 3 }])?;
        self.ticks = self
            .ticks
            .checked_add(convert_ticks(
                delta,
                self.frequency,
                self.session.config().timestamp_frequency,
            )?)
            .ok_or(ExchangeError::Invalid("raw timestamp overflow"))?;
        self.sequence = self
            .sequence
            .checked_add(value_u64(row[if long { 7 } else { 4 }])?)
            .ok_or(ExchangeError::Invalid("capture sequence overflow"))?;
        let timing = if row.len() >= 11 {
            value_u64(row[10])?
        } else {
            self.sequence
        };
        if timing < self.timing {
            return Err(ExchangeError::Invalid("timing sequences are out of order"));
        }
        self.timing = timing;
        let record = RawMouseRecord {
            raw_delta_x: value_i32(row[0])?,
            raw_delta_y: value_i32(row[1])?,
            button_flags: value_u16(row[2])?,
            raw_mouse_flags: if long { value_u16(row[3])? } else { 0 },
            button_data: if long { value_u16(row[4])? } else { 0 },
            extra_information: if long { value_u32(row[5])? } else { 0 },
            raw_capture_ticks: self.ticks,
            capture_sequence: self.sequence,
            timing_sequence: timing,
            movement_mode: if row.len() >= 10 {
                movement_mode_from_value(row[9])?
            } else {
                RawMouseMovementMode::Unknown
            },
        };
        if self.session.append(record)? != crate::AppendOutcome::Appended {
            return Err(ExchangeError::Invalid(
                "exchange exceeds the session event limit",
            ));
        }
        self.count += 1;
        Ok(())
    }
}
pub fn export_writer(
    mut writer: impl Write,
    snapshot: &SessionSnapshot,
    effective_cpi: Option<f64>,
    exported_at_utc: &str,
) -> Result<(), ExchangeError> {
    if snapshot.event_count() == 0 {
        return Err(ExchangeError::Invalid("no session data to export"));
    }
    let metadata = snapshot.metadata();
    let mouse = serde_json::json!({
        "n":metadata.display_name,"d":metadata.device_id,"vi":metadata.vendor_id,"pi":metadata.product_id,
        "b":metadata.button_count,"x":metadata.is_virtual,"p":metadata.path_summary
    });
    let evidence = snapshot.capture_evidence();
    validate_capture_evidence(Some(&evidence))?;
    validate_capture_event_count(Some(&evidence), snapshot.event_count())?;
    let mut session = serde_json::json!({"c":effective_cpi,"f":snapshot.timestamp_frequency()});
    if evidence != CaptureEvidence::default() {
        session["e"] = serde_json::to_value(evidence)?;
    }
    checked_long(snapshot.timestamp_frequency())?;
    write!(writer, "{{\"v\":8,\"t\":").map_err(serde_json::Error::io)?;
    serde_json::to_writer(&mut writer, exported_at_utc)?;
    write!(writer, ",\"m\":").map_err(serde_json::Error::io)?;
    serde_json::to_writer(&mut writer, &mouse)?;
    write!(writer, ",\"s\":").map_err(serde_json::Error::io)?;
    serde_json::to_writer(&mut writer, &session)?;
    write!(writer, ",\"g\":[").map_err(serde_json::Error::io)?;
    let mut exported = 0;
    let mut events = snapshot.events().peekable();
    while let Some(first) = events.peek().copied() {
        let source_id = first.session_segment_id;
        let start = snapshot
            .segment(source_id)
            .map_or(first.raw_capture_ticks, |s| s.started_at_raw_capture_ticks);
        if exported > 0 {
            write!(writer, ",").map_err(serde_json::Error::io)?;
        }
        exported += 1;
        checked_long(start)?;
        checked_long(first.capture_sequence)?;
        write!(
            writer,
            "{{\"i\":{exported},\"t\":{start},\"q\":{},\"r\":[",
            first.capture_sequence
        )
        .map_err(serde_json::Error::io)?;
        let (mut ticks, mut sequence, mut count) = (start, first.capture_sequence, 0);
        while events
            .peek()
            .is_some_and(|e| e.session_segment_id == source_id)
        {
            let e = events.next().unwrap();
            if count > 0 {
                write!(writer, ",").map_err(serde_json::Error::io)?;
            }
            let row = [
                e.raw_delta_x as i64,
                e.raw_delta_y as i64,
                e.button_flags as i64,
                e.raw_mouse_flags as i64,
                e.button_data as i64,
                e.extra_information as i64,
                checked_long(
                    e.raw_capture_ticks
                        .checked_sub(ticks)
                        .ok_or(ExchangeError::Invalid("raw capture ticks out of order"))?,
                )?,
                checked_long(
                    e.capture_sequence
                        .checked_sub(sequence)
                        .ok_or(ExchangeError::Invalid("capture sequences out of order"))?,
                )?,
                e.packet_kind as i64,
                e.movement_mode as i64,
                checked_long(e.timing_sequence)?,
            ];
            serde_json::to_writer(&mut writer, &row)?;
            ticks = e.raw_capture_ticks;
            sequence = e.capture_sequence;
            count += 1;
        }
        write!(writer, "]}}").map_err(serde_json::Error::io)?;
    }
    write!(writer, "]}}").map_err(serde_json::Error::io)?;
    Ok(())
}
fn checked_long(value: u64) -> Result<i64, ExchangeError> {
    i64::try_from(value)
        .map_err(|_| ExchangeError::Invalid("exchange integer exceeds signed 64-bit range"))
}
