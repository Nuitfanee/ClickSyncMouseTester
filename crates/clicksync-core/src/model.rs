use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[repr(u8)]
pub enum RawMouseMovementMode {
    #[default]
    Unknown = 0,
    Relative = 1,
    Absolute = 2,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[repr(u8)]
pub enum MousePerformancePacketKind {
    Empty = 0,
    Motion = 1,
    MotionWithButton = 2,
    MotionWithWheel = 3,
    MotionWithButtonAndWheel = 4,
    ButtonOnly = 5,
    WheelOnly = 6,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct RawMouseRecord {
    pub raw_delta_x: i32,
    pub raw_delta_y: i32,
    pub button_flags: u16,
    pub raw_mouse_flags: u16,
    pub button_data: u16,
    pub extra_information: u32,
    pub raw_capture_ticks: u64,
    pub capture_sequence: u64,
    pub timing_sequence: u64,
    pub movement_mode: RawMouseMovementMode,
}

impl RawMouseRecord {
    pub fn new(
        raw_delta_x: i32,
        raw_delta_y: i32,
        raw_capture_ticks: u64,
        capture_sequence: u64,
        timing_sequence: u64,
        movement_mode: RawMouseMovementMode,
    ) -> Self {
        Self {
            raw_delta_x,
            raw_delta_y,
            button_flags: 0,
            raw_mouse_flags: 0,
            button_data: 0,
            extra_information: 0,
            raw_capture_ticks,
            capture_sequence,
            timing_sequence,
            movement_mode,
        }
    }

    pub fn packet_kind(self) -> MousePerformancePacketKind {
        const WHEEL_BUTTON_MASK: u16 = 3072;
        let has_motion = self.raw_delta_x != 0 || self.raw_delta_y != 0;
        let has_wheel = self.button_flags & WHEEL_BUTTON_MASK != 0;
        let has_button = self.button_flags & !WHEEL_BUTTON_MASK != 0;
        match (has_motion, has_button, has_wheel) {
            (true, true, true) => MousePerformancePacketKind::MotionWithButtonAndWheel,
            (true, false, true) => MousePerformancePacketKind::MotionWithWheel,
            (true, true, false) => MousePerformancePacketKind::MotionWithButton,
            (true, false, false) => MousePerformancePacketKind::Motion,
            (false, true, true) => MousePerformancePacketKind::WheelOnly,
            (false, true, false) => MousePerformancePacketKind::ButtonOnly,
            (false, false, true) => MousePerformancePacketKind::WheelOnly,
            (false, false, false) => MousePerformancePacketKind::Empty,
        }
    }

    pub fn relative_delta(self) -> (i32, i32) {
        match self.movement_mode {
            RawMouseMovementMode::Relative => (self.raw_delta_x, self.raw_delta_y),
            RawMouseMovementMode::Unknown | RawMouseMovementMode::Absolute => (0, 0),
        }
    }
}

#[derive(Clone, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
pub struct SessionMetadata {
    pub display_name: String,
    pub device_id: String,
    pub vendor_id: Option<i32>,
    pub product_id: Option<i32>,
    pub button_count: i32,
    pub is_virtual: bool,
    pub path_summary: String,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub struct SessionEvent {
    pub raw_delta_x: i32,
    pub raw_delta_y: i32,
    pub delta_x: i32,
    pub delta_y: i32,
    pub button_flags: u16,
    pub raw_mouse_flags: u16,
    pub button_data: u16,
    pub extra_information: u32,
    pub raw_capture_ticks: u64,
    pub raw_relative_ticks: u64,
    pub logical_ticks: u64,
    pub packet_kind: MousePerformancePacketKind,
    pub movement_mode: RawMouseMovementMode,
    pub session_cumulative_x: i64,
    pub session_cumulative_y: i64,
    pub session_segment_id: u32,
    pub capture_sequence: u64,
    pub timing_sequence: u64,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub struct SessionSegment {
    pub segment_id: u32,
    pub started_at_raw_capture_ticks: u64,
}
