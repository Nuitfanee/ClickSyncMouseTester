use crate::model::{RawMouseMovementMode, RawMouseRecord, SessionMetadata};
use crate::session::{
    CaptureEvidence, CaptureIntegrity, Session, SessionConfig, SessionError, SessionSnapshot,
};
use serde::{Deserialize, Serialize};
use std::fmt;
mod stream;
pub use stream::{ImportedSession, MAX_IMPORT_BYTES, export_writer, import_reader};

const MINIMUM_SUPPORTED_FORMAT_VERSION: u32 = 4;
const CURRENT_FORMAT_VERSION: u32 = 8;

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ExchangeDocument {
    #[serde(
        rename = "FormatVersion",
        alias = "formatVersion",
        alias = "format_version",
        alias = "v"
    )]
    pub format_version: u32,
    #[serde(
        rename = "ExportedAtUtc",
        alias = "exportedAtUtc",
        alias = "exported_at_utc",
        default
    )]
    pub exported_at_utc: Option<String>,
    #[serde(rename = "Mouse", alias = "mouse", alias = "m")]
    pub mouse: ExchangeMouseDocument,
    #[serde(rename = "Session", alias = "session", alias = "s")]
    pub session: ExchangeSessionDocument,
    #[serde(rename = "Segments", alias = "segments", alias = "g")]
    pub segments: Vec<ExchangeSegmentDocument>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ExchangeMouseDocument {
    #[serde(
        rename = "DisplayName",
        alias = "displayName",
        alias = "display_name",
        alias = "n"
    )]
    pub display_name: Option<String>,
    #[serde(
        rename = "DeviceId",
        alias = "deviceId",
        alias = "device_id",
        alias = "d"
    )]
    pub device_id: Option<String>,
    #[serde(
        rename = "VendorId",
        alias = "vendorId",
        alias = "vendor_id",
        alias = "vi"
    )]
    pub vendor_id: Option<i32>,
    #[serde(
        rename = "ProductId",
        alias = "productId",
        alias = "product_id",
        alias = "pi"
    )]
    pub product_id: Option<i32>,
    #[serde(
        rename = "ButtonCount",
        alias = "buttonCount",
        alias = "button_count",
        alias = "b",
        default
    )]
    pub button_count: i32,
    #[serde(
        rename = "IsVirtual",
        alias = "isVirtual",
        alias = "is_virtual",
        alias = "x",
        default
    )]
    pub is_virtual: bool,
    #[serde(
        rename = "PathSummary",
        alias = "pathSummary",
        alias = "path_summary",
        alias = "p"
    )]
    pub path_summary: Option<String>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ExchangeSessionDocument {
    #[serde(
        rename = "EffectiveCpi",
        alias = "effectiveCpi",
        alias = "effective_cpi",
        alias = "c"
    )]
    pub effective_cpi: Option<f64>,
    #[serde(
        rename = "HostTimestampFrequency",
        alias = "hostTimestampFrequency",
        alias = "host_timestamp_frequency",
        alias = "f"
    )]
    pub host_timestamp_frequency: u64,
    #[serde(
        rename = "CaptureEvidence",
        alias = "captureEvidence",
        alias = "capture_evidence",
        alias = "e",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub capture_evidence: Option<CaptureEvidence>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ExchangeSegmentDocument {
    #[serde(
        rename = "SegmentId",
        alias = "segmentId",
        alias = "segment_id",
        alias = "i"
    )]
    pub segment_id: u32,
    #[serde(
        rename = "StartedAtRawCaptureTicks",
        alias = "startedAtRawCaptureTicks",
        alias = "started_at_raw_capture_ticks",
        alias = "t"
    )]
    pub started_at_raw_capture_ticks: u64,
    #[serde(
        rename = "BaseCaptureSequence",
        alias = "baseCaptureSequence",
        alias = "base_capture_sequence",
        alias = "q"
    )]
    pub base_capture_sequence: u64,
    #[serde(
        rename = "PacketRows",
        alias = "packetRows",
        alias = "packet_rows",
        alias = "r"
    )]
    pub packet_rows: Vec<Vec<i64>>,
}

#[derive(Debug)]
pub enum ExchangeError {
    Json(serde_json::Error),
    Invalid(&'static str),
    Session(SessionError),
}

impl fmt::Display for ExchangeError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Json(error) => error.fmt(formatter),
            Self::Invalid(message) => write!(formatter, "invalid exchange document: {message}"),
            Self::Session(error) => error.fmt(formatter),
        }
    }
}

impl std::error::Error for ExchangeError {}

impl From<serde_json::Error> for ExchangeError {
    fn from(error: serde_json::Error) -> Self {
        Self::Json(error)
    }
}

impl From<SessionError> for ExchangeError {
    fn from(error: SessionError) -> Self {
        Self::Session(error)
    }
}

impl ExchangeDocument {
    pub fn from_json(input: &str) -> Result<Self, ExchangeError> {
        let document = serde_json::from_str(input)?;
        Ok(document)
    }

    pub fn to_json(&self) -> Result<String, ExchangeError> {
        Ok(serde_json::to_string(self)?)
    }

    pub fn validate(&self) -> Result<(), ExchangeError> {
        if !(MINIMUM_SUPPORTED_FORMAT_VERSION..=CURRENT_FORMAT_VERSION)
            .contains(&self.format_version)
        {
            return Err(ExchangeError::Invalid("unsupported format version"));
        }
        if self.session.host_timestamp_frequency == 0 {
            return Err(ExchangeError::Invalid(
                "host timestamp frequency must be greater than zero",
            ));
        }
        validate_capture_evidence(self.session.capture_evidence.as_ref())?;
        if self.segments.is_empty() {
            return Err(ExchangeError::Invalid("no session segments"));
        }
        let mut previous_segment_id = 0;
        let mut previous_start = 0;
        let mut previous_base_sequence = 0;
        for segment in &self.segments {
            if segment.segment_id == 0 || segment.segment_id <= previous_segment_id {
                return Err(ExchangeError::Invalid(
                    "segment identifiers are not increasing",
                ));
            }
            if segment.started_at_raw_capture_ticks < previous_start
                || segment.base_capture_sequence < previous_base_sequence
            {
                return Err(ExchangeError::Invalid("segment anchors are out of order"));
            }
            if segment.packet_rows.is_empty() {
                return Err(ExchangeError::Invalid("segment has no packet rows"));
            }
            previous_segment_id = segment.segment_id;
            previous_start = segment.started_at_raw_capture_ticks;
            previous_base_sequence = segment.base_capture_sequence;
        }
        Ok(())
    }

    pub fn replay(
        &self,
        config: SessionConfig,
        budget: std::sync::Arc<crate::SharedByteBudget>,
    ) -> Result<SessionSnapshot, ExchangeError> {
        self.validate()?;
        let source_frequency = self.session.host_timestamp_frequency;
        if source_frequency == 0 {
            return Err(ExchangeError::Invalid("source timestamp frequency is zero"));
        }
        let metadata = SessionMetadata {
            display_name: self.mouse.display_name.clone().unwrap_or_default(),
            device_id: self.mouse.device_id.clone().unwrap_or_default(),
            vendor_id: self.mouse.vendor_id,
            product_id: self.mouse.product_id,
            button_count: self.mouse.button_count,
            is_virtual: self.mouse.is_virtual,
            path_summary: self.mouse.path_summary.clone().unwrap_or_default(),
        };
        let mut session = Session::new(config, budget, metadata)?;
        for (index, segment) in self.segments.iter().enumerate() {
            let started_at = convert_ticks(
                segment.started_at_raw_capture_ticks,
                source_frequency,
                session.config().timestamp_frequency,
            )?;
            session.begin_collecting(started_at, index == 0, false)?;
            let mut current_raw_ticks = started_at;
            let mut current_capture_sequence = segment.base_capture_sequence;
            let mut current_timing_sequence = 0;
            for row in &segment.packet_rows {
                if row.len() != 5
                    && row.len() != 8
                    && row.len() != 9
                    && row.len() != 10
                    && row.len() != 11
                {
                    return Err(ExchangeError::Invalid("invalid compact packet row length"));
                }
                let raw_mouse_flags = if row.len() >= 8 {
                    value_u16(row[3])?
                } else {
                    0
                };
                let button_data = if row.len() >= 8 {
                    value_u16(row[4])?
                } else {
                    0
                };
                let extra_information = if row.len() >= 8 {
                    value_u32(row[5])?
                } else {
                    0
                };
                let raw_delta_x = value_i32(row[0])?;
                let raw_delta_y = value_i32(row[1])?;
                let button_flags = value_u16(row[2])?;
                let raw_tick_delta = value_u64(row[if row.len() >= 8 { 6 } else { 3 }])?;
                let capture_sequence_delta = value_u64(row[if row.len() >= 8 { 7 } else { 4 }])?;
                let timing_sequence = if row.len() >= 11 {
                    value_u64(row[10])?
                } else {
                    current_capture_sequence
                        .checked_add(capture_sequence_delta)
                        .ok_or(ExchangeError::Invalid("capture sequence overflow"))?
                };
                current_raw_ticks = current_raw_ticks
                    .checked_add(convert_ticks(
                        raw_tick_delta,
                        source_frequency,
                        session.config().timestamp_frequency,
                    )?)
                    .ok_or(ExchangeError::Invalid("raw timestamp overflow"))?;
                current_capture_sequence = current_capture_sequence
                    .checked_add(capture_sequence_delta)
                    .ok_or(ExchangeError::Invalid("capture sequence overflow"))?;
                if timing_sequence < current_timing_sequence {
                    return Err(ExchangeError::Invalid("timing sequences are out of order"));
                }
                current_timing_sequence = timing_sequence;
                let movement_mode = if row.len() >= 10 {
                    movement_mode_from_value(row[9])?
                } else {
                    RawMouseMovementMode::Unknown
                };
                let record = RawMouseRecord {
                    raw_delta_x,
                    raw_delta_y,
                    button_flags,
                    raw_mouse_flags,
                    button_data,
                    extra_information,
                    raw_capture_ticks: current_raw_ticks,
                    capture_sequence: current_capture_sequence,
                    timing_sequence,
                    movement_mode,
                };
                if session.append(record)? != crate::AppendOutcome::Appended {
                    return Err(ExchangeError::Invalid(
                        "exchange exceeds the session event limit",
                    ));
                }
            }
        }
        session.stop();
        validate_capture_event_count(
            self.session.capture_evidence.as_ref(),
            session.event_count(),
        )?;
        session.set_capture_evidence(self.session.capture_evidence.unwrap_or_default());
        Ok(session.snapshot()?)
    }
}

fn validate_capture_evidence(evidence: Option<&CaptureEvidence>) -> Result<(), ExchangeError> {
    let Some(evidence) = evidence else {
        return Ok(());
    };
    if (evidence.has_loss() && evidence.integrity != CaptureIntegrity::Incomplete)
        || evidence.generation == Some(0)
        || evidence.queued.is_some() != evidence.cutoff.is_some()
        || evidence
            .queued
            .zip(evidence.cutoff)
            .is_some_and(|(queued, cutoff)| {
                queued > cutoff
                    || (evidence.integrity == CaptureIntegrity::Complete && queued != cutoff)
            })
        || evidence
            .received_sequence
            .zip(evidence.queued)
            .is_some_and(|(received, queued)| received > queued)
    {
        return Err(ExchangeError::Invalid("inconsistent capture evidence"));
    }
    Ok(())
}

fn validate_capture_event_count(
    evidence: Option<&CaptureEvidence>,
    event_count: usize,
) -> Result<(), ExchangeError> {
    if let Some(evidence) = evidence
        && let Some(received) = evidence.received_sequence
        && (event_count as u64 > received
            || evidence.ignored_after_limit > received.saturating_sub(event_count as u64))
    {
        return Err(ExchangeError::Invalid(
            "capture evidence does not cover the stored dataset",
        ));
    }
    Ok(())
}

pub fn convert_ticks(
    ticks: u64,
    source_frequency: u64,
    destination_frequency: u64,
) -> Result<u64, ExchangeError> {
    if source_frequency == 0 || destination_frequency == 0 {
        return Err(ExchangeError::Invalid("timestamp frequency is zero"));
    }
    if ticks == 0 || source_frequency == destination_frequency {
        return Ok(ticks);
    }
    let numerator = u128::from(ticks)
        .checked_mul(u128::from(destination_frequency))
        .ok_or(ExchangeError::Invalid("timestamp conversion overflow"))?;
    let denominator = u128::from(source_frequency);
    let quotient = numerator / denominator;
    let remainder = numerator % denominator;
    let rounded = if remainder.saturating_mul(2) >= denominator {
        quotient.saturating_add(1)
    } else {
        quotient
    };
    u64::try_from(rounded).map_err(|_| ExchangeError::Invalid("timestamp conversion overflow"))
}

fn value_i32(value: i64) -> Result<i32, ExchangeError> {
    i32::try_from(value).map_err(|_| ExchangeError::Invalid("integer value outside i32 range"))
}

fn value_u16(value: i64) -> Result<u16, ExchangeError> {
    u16::try_from(value).map_err(|_| ExchangeError::Invalid("integer value outside u16 range"))
}

fn value_u32(value: i64) -> Result<u32, ExchangeError> {
    u32::try_from(value).map_err(|_| ExchangeError::Invalid("integer value outside u32 range"))
}

fn value_u64(value: i64) -> Result<u64, ExchangeError> {
    u64::try_from(value).map_err(|_| ExchangeError::Invalid("negative or invalid integer value"))
}

fn movement_mode_from_value(value: i64) -> Result<RawMouseMovementMode, ExchangeError> {
    match value {
        0 => Ok(RawMouseMovementMode::Unknown),
        1 => Ok(RawMouseMovementMode::Relative),
        2 => Ok(RawMouseMovementMode::Absolute),
        _ => Err(ExchangeError::Invalid("invalid movement mode")),
    }
}
