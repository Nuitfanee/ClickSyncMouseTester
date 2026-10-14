use crate::{RawMouseMovementMode, RawMouseRecord};
use serde::{Deserialize, Serialize};
use std::fmt;

pub const PROTOCOL_VERSION: u16 = 3;
pub const FRAME_HEADER_BYTES: usize = 24;
pub const WIRE_RECORD_BYTES: usize = 56;
pub const MAX_FRAME_PAYLOAD_BYTES: usize = 65_536;
pub const MAX_BATCH_RECORDS: usize = 256;
pub const CAPTURE_QUEUE_RECORDS: usize = 8192;
pub const INPUT_QUEUE_EVENTS: usize = 4096;
pub const MAX_INPUT_BATCH_EVENTS: usize = 128;
pub const BATCH_FLUSH_INTERVAL_MS: u64 = 4;
pub const MAX_DEVICE_COUNT: usize = 256;
pub const MAX_CAPTURE_DEVICE_FIELD_BYTES: usize = 4096;
pub const MAX_CAPTURE_DEVICE_IDENTITIES: usize = 4096;
/// Conservative reservation for bounded identity history, catalogs and enumeration scratch.
pub const CAPTURE_METADATA_RESERVATION_BYTES: usize = 128 * 1024 * 1024;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CapturedRecord {
    pub generation: u64,
    pub device_key: u32,
    /// Contiguous selected-report watermark within this generation, not a source report number.
    pub session_sequence: u64,
    pub record: RawMouseRecord,
}

pub use crate::devices::DeviceDescriptor;

/// Control input has its own stream and never participates in mouse session watermarks.
#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
pub struct ControlInputEvent {
    pub ticks: u64,
    pub input: ControlInput,
}
#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
pub enum ControlInput {
    Mouse {
        flags: u16,
        data: u16,
    },
    Keyboard {
        virtual_key: u16,
        scan_code: u16,
        extended: bool,
        down: bool,
    },
}
#[derive(Clone, Debug, Default, Deserialize, Serialize)]
pub struct ControlInputBatch {
    pub frequency: u64,
    pub dropped: u64,
    pub events: Vec<ControlInputEvent>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
pub enum CaptureSource {
    RawInput,
    Replay {
        records_per_device: u64,
        reports_per_second: u32,
        real_time: bool,
    },
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
pub enum ControlCommand {
    /// Publish current facts and activity without querying Windows metadata.
    Discover,
    /// Explicitly reconcile the directory with the OS, reusing metadata for present paths.
    RescanDevices,
    Start {
        generation: u64,
        device_keys: Vec<u32>,
        source: CaptureSource,
        start_fresh: bool,
    },
    Pause,
    Resume,
    Stop,
    Reset {
        generation: u64,
    },
    Shutdown,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub enum CommandTag {
    Discover,
    Start,
    Pause,
    Resume,
    Stop,
    Reset,
    Shutdown,
}

impl ControlCommand {
    pub fn tag(&self) -> CommandTag {
        match self {
            Self::Discover | Self::RescanDevices => CommandTag::Discover,
            Self::Start { .. } => CommandTag::Start,
            Self::Pause => CommandTag::Pause,
            Self::Resume => CommandTag::Resume,
            Self::Stop => CommandTag::Stop,
            Self::Reset { .. } => CommandTag::Reset,
            Self::Shutdown => CommandTag::Shutdown,
        }
    }

    pub fn generation(&self) -> u64 {
        match self {
            Self::Start { generation, .. } | Self::Reset { generation } => *generation,
            _ => 0,
        }
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct CaptureAck {
    pub command: CommandTag,
    pub generation: u64,
    pub cutoff: u64,
    /// Accepted prefix after sealing; unlike cutoff, excludes a failed enqueue attempt.
    pub queued: u64,
    pub ticks: u64,
    pub timestamp_frequency: u64,
    pub complete: bool,
    pub overflow: u64,
    pub queue_high_watermark: usize,
    pub devices: Vec<DeviceDescriptor>,
    pub error: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct CaptureStatus {
    pub generation: u64,
    pub captured: u64,
    pub queued: u64,
    pub sent: u64,
    pub overflow: u64,
    pub queue_high_watermark: usize,
    pub active: bool,
    pub disconnected: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u16)]
pub enum FrameKind {
    Command = 1,
    Ack = 2,
    Batch = 3,
    Status = 4,
    Input = 5,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FrameHeader {
    pub kind: FrameKind,
    pub generation: u64,
    pub payload_bytes: usize,
    pub record_count: usize,
}

#[derive(Debug, Eq, PartialEq)]
pub struct ProtocolError(pub &'static str);

impl fmt::Display for ProtocolError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.0)
    }
}

impl std::error::Error for ProtocolError {}

impl FrameHeader {
    pub fn encode(self) -> Result<[u8; FRAME_HEADER_BYTES], ProtocolError> {
        self.validate()?;
        let mut bytes = [0; FRAME_HEADER_BYTES];
        bytes[..4].copy_from_slice(b"CSYN");
        bytes[4..6].copy_from_slice(&PROTOCOL_VERSION.to_le_bytes());
        bytes[6..8].copy_from_slice(&(self.kind as u16).to_le_bytes());
        bytes[8..12].copy_from_slice(&(self.payload_bytes as u32).to_le_bytes());
        bytes[12..16].copy_from_slice(&(self.record_count as u32).to_le_bytes());
        bytes[16..24].copy_from_slice(&self.generation.to_le_bytes());
        Ok(bytes)
    }

    pub fn decode(bytes: &[u8; FRAME_HEADER_BYTES]) -> Result<Self, ProtocolError> {
        if &bytes[..4] != b"CSYN" || u16::from_le_bytes([bytes[4], bytes[5]]) != PROTOCOL_VERSION {
            return Err(ProtocolError("invalid protocol magic or version"));
        }
        let kind = match u16::from_le_bytes([bytes[6], bytes[7]]) {
            1 => FrameKind::Command,
            2 => FrameKind::Ack,
            3 => FrameKind::Batch,
            4 => FrameKind::Status,
            5 => FrameKind::Input,
            _ => return Err(ProtocolError("unknown frame kind")),
        };
        let header = Self {
            kind,
            generation: u64::from_le_bytes(
                bytes[16..24].try_into().expect("fixed generation field"),
            ),
            payload_bytes: u32::from_le_bytes(
                bytes[8..12].try_into().expect("fixed payload size field"),
            ) as usize,
            record_count: u32::from_le_bytes(
                bytes[12..16].try_into().expect("fixed record count field"),
            ) as usize,
        };
        header.validate()?;
        Ok(header)
    }

    fn validate(self) -> Result<(), ProtocolError> {
        if self.payload_bytes > MAX_FRAME_PAYLOAD_BYTES {
            return Err(ProtocolError("frame exceeds the payload byte limit"));
        }
        if self.kind == FrameKind::Batch {
            if self.generation == 0
                || self.record_count == 0
                || self.record_count > MAX_BATCH_RECORDS
                || self.payload_bytes != self.record_count * WIRE_RECORD_BYTES
            {
                return Err(ProtocolError(
                    "invalid fixed-record batch length or generation",
                ));
            }
        } else if self.record_count != 0 || self.payload_bytes == 0 {
            return Err(ProtocolError("invalid control or status frame length"));
        }
        Ok(())
    }
}

impl CapturedRecord {
    pub fn encode(self) -> Result<[u8; WIRE_RECORD_BYTES], ProtocolError> {
        self.validate()?;
        let mut bytes = [0; WIRE_RECORD_BYTES];
        bytes[0..8].copy_from_slice(&self.record.raw_capture_ticks.to_le_bytes());
        bytes[8..16].copy_from_slice(&self.record.capture_sequence.to_le_bytes());
        bytes[16..24].copy_from_slice(&self.record.timing_sequence.to_le_bytes());
        bytes[24..28].copy_from_slice(&self.record.raw_delta_x.to_le_bytes());
        bytes[28..32].copy_from_slice(&self.record.raw_delta_y.to_le_bytes());
        bytes[32..36].copy_from_slice(&self.record.extra_information.to_le_bytes());
        bytes[36..40].copy_from_slice(&self.device_key.to_le_bytes());
        bytes[40..42].copy_from_slice(&self.record.button_flags.to_le_bytes());
        bytes[42..44].copy_from_slice(&self.record.raw_mouse_flags.to_le_bytes());
        bytes[44..46].copy_from_slice(&self.record.button_data.to_le_bytes());
        bytes[46] = self.record.movement_mode as u8;
        bytes[48..56].copy_from_slice(&self.session_sequence.to_le_bytes());
        Ok(bytes)
    }

    pub fn decode(generation: u64, bytes: &[u8; WIRE_RECORD_BYTES]) -> Result<Self, ProtocolError> {
        if bytes[47] != 0 {
            return Err(ProtocolError("wire record reserved byte is not zero"));
        }
        let movement_mode = match bytes[46] {
            0 => RawMouseMovementMode::Unknown,
            1 => RawMouseMovementMode::Relative,
            2 => RawMouseMovementMode::Absolute,
            _ => return Err(ProtocolError("invalid movement mode")),
        };
        let value = Self {
            generation,
            device_key: u32::from_le_bytes(bytes[36..40].try_into().expect("device key field")),
            session_sequence: u64::from_le_bytes(
                bytes[48..56].try_into().expect("session sequence field"),
            ),
            record: RawMouseRecord {
                raw_capture_ticks: u64::from_le_bytes(
                    bytes[0..8].try_into().expect("raw timestamp field"),
                ),
                capture_sequence: u64::from_le_bytes(
                    bytes[8..16].try_into().expect("capture sequence field"),
                ),
                timing_sequence: u64::from_le_bytes(
                    bytes[16..24].try_into().expect("timing sequence field"),
                ),
                raw_delta_x: i32::from_le_bytes(bytes[24..28].try_into().expect("delta x field")),
                raw_delta_y: i32::from_le_bytes(bytes[28..32].try_into().expect("delta y field")),
                extra_information: u32::from_le_bytes(
                    bytes[32..36].try_into().expect("extra information field"),
                ),
                button_flags: u16::from_le_bytes([bytes[40], bytes[41]]),
                raw_mouse_flags: u16::from_le_bytes([bytes[42], bytes[43]]),
                button_data: u16::from_le_bytes([bytes[44], bytes[45]]),
                movement_mode,
            },
        };
        value.validate()?;
        Ok(value)
    }

    fn validate(self) -> Result<(), ProtocolError> {
        if self.generation == 0
            || self.device_key == 0
            || self.session_sequence == 0
            || self.record.raw_capture_ticks > i64::MAX as u64
            || self.record.capture_sequence == 0
            || self.record.capture_sequence > i64::MAX as u64
            || self.record.timing_sequence > i64::MAX as u64
        {
            return Err(ProtocolError("invalid wire integer or identity"));
        }
        Ok(())
    }
}

pub fn replay_record(index: u64, generation: u64, device_key: u32) -> CapturedRecord {
    CapturedRecord {
        generation,
        device_key,
        session_sequence: index + 1,
        record: RawMouseRecord::new(
            (index % 7) as i32 - 3,
            (index % 5) as i32 - 2,
            1_000_000 + index * 1250,
            index + 1,
            index + 1,
            RawMouseMovementMode::Relative,
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fixed_wire_encoding_preserves_all_integer_fields() {
        let record = CapturedRecord {
            generation: 17,
            device_key: 4,
            session_sequence: 9,
            record: RawMouseRecord {
                raw_delta_x: i32::MIN,
                raw_delta_y: i32::MAX,
                button_flags: u16::MAX,
                raw_mouse_flags: u16::MAX,
                button_data: 65416,
                extra_information: u32::MAX,
                raw_capture_ticks: i64::MAX as u64,
                capture_sequence: 918,
                timing_sequence: 66,
                movement_mode: RawMouseMovementMode::Absolute,
            },
        };
        assert_eq!(
            CapturedRecord::decode(record.generation, &record.encode().unwrap()).unwrap(),
            record
        );
        assert_eq!(WIRE_RECORD_BYTES, 56);
    }

    #[test]
    fn batch_header_rejects_unbounded_and_inconsistent_sizes() {
        let valid = FrameHeader {
            kind: FrameKind::Batch,
            generation: 9,
            record_count: MAX_BATCH_RECORDS,
            payload_bytes: MAX_BATCH_RECORDS * WIRE_RECORD_BYTES,
        };
        assert_eq!(
            FrameHeader::decode(&valid.encode().unwrap()).unwrap(),
            valid
        );
        assert!(
            FrameHeader {
                record_count: MAX_BATCH_RECORDS + 1,
                ..valid
            }
            .encode()
            .is_err()
        );
        let mut invalid = valid.encode().unwrap();
        invalid[4..6].copy_from_slice(&2u16.to_le_bytes());
        assert!(FrameHeader::decode(&invalid).is_err());
        invalid = valid.encode().unwrap();
        invalid[4] = 0xff;
        assert!(FrameHeader::decode(&invalid).is_err());
        invalid = valid.encode().unwrap();
        invalid[8..12].copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(FrameHeader::decode(&invalid).is_err());
    }
}
