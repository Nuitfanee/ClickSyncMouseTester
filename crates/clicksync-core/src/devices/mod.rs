//! Device facts and selection policy, independent of the input source and UI.
mod catalog;
mod path;

pub use catalog::{DeviceCatalog, DeviceChoice, EndpointKind};
pub use path::DevicePath;
pub use path::normalize as path_normalize;

use crate::SessionMetadata;
use crate::protocol::MAX_CAPTURE_DEVICE_FIELD_BYTES;
use serde::{Deserialize, Serialize};
use std::borrow::Cow;

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
pub enum DeviceSource {
    #[default]
    RawInput,
    Replay,
}

/// Discovery's function classification, separate from the OS's Raw Input device type.
#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
pub enum EndpointRole {
    #[default]
    Unclassified,
    KeyboardAuxiliary,
    /// A readable HID descriptor explicitly lacks relative X/Y motion.
    NonMotion,
    /// Mixed keyboard/mouse functions or unavailable descriptor; wait for actual motion.
    MotionPending,
}

/// Capability evidence is monotonic for a source identity, including while idle.
#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
pub struct EndpointActivity {
    pub observed: bool,
    pub motion: bool,
}

impl EndpointActivity {
    /// Constant work, no allocation, and no dependency on capture selection.
    pub fn observe(&mut self, delta_x: i32, delta_y: i32) -> bool {
        let previous = *self;
        self.observed = true;
        self.motion |= delta_x != 0 || delta_y != 0;
        *self != previous
    }
}

#[derive(Clone, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
pub struct EndpointDescriptor {
    pub source: DeviceSource,
    #[serde(default)]
    pub role: EndpointRole,
    pub physical_device_key: String,
    pub endpoint_token: String,
    pub activity: EndpointActivity,
}

impl EndpointDescriptor {
    pub fn requires_motion_confirmation(&self) -> bool {
        self.source == DeviceSource::RawInput && self.role == EndpointRole::MotionPending
    }
}

#[derive(Clone, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
pub struct DeviceDescriptor {
    pub key: u32,
    pub metadata: SessionMetadata,
    #[serde(default)]
    pub endpoint: EndpointDescriptor,
}

impl DeviceDescriptor {
    pub fn fields_are_bounded(&self) -> bool {
        [
            &self.metadata.device_id,
            &self.metadata.display_name,
            &self.metadata.path_summary,
            &self.endpoint.physical_device_key,
            &self.endpoint.endpoint_token,
        ]
        .iter()
        .all(|text| text.len() <= MAX_CAPTURE_DEVICE_FIELD_BYTES)
    }

    pub fn eligible_for_motion(&self) -> bool {
        self.endpoint.source == DeviceSource::Replay
            || (!matches!(
                self.endpoint.role,
                EndpointRole::KeyboardAuxiliary | EndpointRole::NonMotion
            ) && eligible_metadata(&self.metadata))
    }

    pub fn physical_device_key(&self) -> Cow<'_, str> {
        if self.endpoint.physical_device_key.is_empty() {
            Cow::Owned(DevicePath::parse(&self.metadata.device_id).physical_device_key)
        } else {
            Cow::Borrowed(&self.endpoint.physical_device_key)
        }
    }
}

pub fn eligible_metadata(metadata: &SessionMetadata) -> bool {
    !metadata.is_virtual
}
