use super::DeviceDescriptor;
use std::collections::HashMap;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EndpointKind {
    MotionCapable,
    Unknown,
    ControlOnly,
    Inactive,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DeviceChoice {
    pub key: u32,
    pub label: String,
    pub detail: String,
    pub kind: EndpointKind,
    pub physical_device_key: String,
}

/// Rebuilds only when device facts or capability evidence change.
#[derive(Default)]
pub struct DeviceCatalog {
    descriptors: Vec<DeviceDescriptor>,
    choices: Vec<DeviceChoice>,
}

impl DeviceCatalog {
    pub fn new(devices: &[DeviceDescriptor]) -> Self {
        let mut result = Self::default();
        result.update(devices);
        result
    }

    pub fn update(&mut self, devices: &[DeviceDescriptor]) -> bool {
        if self.descriptors == devices {
            return false;
        }
        self.choices = choices(devices);
        self.descriptors = devices.to_vec();
        true
    }

    pub fn choices(&self) -> &[DeviceChoice] {
        &self.choices
    }

    pub fn choice(&self, key: u32) -> Option<&DeviceChoice> {
        self.choices.iter().find(|choice| choice.key == key)
    }

    pub fn label(&self, key: u32) -> Option<&str> {
        self.choice(key)
            .map(|choice| choice.label.as_str())
            .or_else(|| {
                self.descriptors
                    .iter()
                    .find(|device| device.key == key)
                    .map(|device| device.metadata.display_name.as_str())
            })
    }

    /// A disconnected opposite side may be repaired; final admission checks both sides.
    pub fn can_choose(&self, key: u32, opposite: Option<u32>) -> bool {
        key != 0
            && self.choice(key).is_some()
            && opposite.is_none_or(|other| {
                self.choice(other).is_none() || self.distinct_devices(key, other)
            })
    }

    pub fn distinct_devices(&self, left: u32, right: u32) -> bool {
        match (self.choice(left), self.choice(right)) {
            (Some(left), Some(right)) => {
                left.key != right.key
                    && (left.physical_device_key.is_empty()
                        || right.physical_device_key.is_empty()
                        || left.physical_device_key != right.physical_device_key)
            }
            _ => false,
        }
    }

    pub fn resolve_selection(&self, previous: Option<u32>, preferred: Option<u32>) -> Option<u32> {
        preferred
            .and_then(|key| self.resolve_same_device(key))
            .or_else(|| previous.and_then(|key| self.resolve_same_device(key)))
            .or_else(|| self.choices.first().map(|choice| choice.key))
    }

    pub fn accepts_selection(&self, keys: &[u32]) -> bool {
        !keys.is_empty()
            && keys.iter().enumerate().all(|(index, key)| {
                *key != 0
                    && self.choice(*key).is_some()
                    && keys[..index]
                        .iter()
                        .all(|previous| self.distinct_devices(*previous, *key))
            })
    }

    /// A hidden sibling can hand its preference to the same physical mouse.
    pub fn resolve_same_device(&self, key: u32) -> Option<u32> {
        if self.choice(key).is_some() {
            return Some(key);
        }
        let group = self
            .descriptors
            .iter()
            .find(|device| device.key == key)?
            .physical_device_key();
        (!group.is_empty())
            .then(|| {
                self.choices
                    .iter()
                    .find(|choice| choice.physical_device_key == group)
            })
            .flatten()
            .map(|choice| choice.key)
    }
}

struct Entry<'a> {
    device: &'a DeviceDescriptor,
    group: String,
    name: String,
    identity: String,
    kind: EndpointKind,
}

fn choices(devices: &[DeviceDescriptor]) -> Vec<DeviceChoice> {
    let mut entries: Vec<_> = devices
        .iter()
        .filter(|device| device.eligible_for_motion())
        .map(|device| Entry {
            device,
            group: device.physical_device_key().into_owned(),
            name: device.metadata.display_name.to_lowercase(),
            identity: device.metadata.device_id.to_uppercase(),
            kind: EndpointKind::Unknown,
        })
        .collect();
    let mut motion_groups = HashMap::new();
    for entry in &entries {
        if !entry.group.is_empty() {
            *motion_groups.entry(entry.group.clone()).or_insert(false) |=
                entry.device.endpoint.activity.motion;
        }
    }
    for entry in &mut entries {
        entry.kind = classify(
            entry.device,
            motion_groups
                .get(entry.group.as_str())
                .copied()
                .unwrap_or(false),
        );
    }
    entries.retain(|entry| match entry.kind {
        EndpointKind::MotionCapable => true,
        EndpointKind::Unknown => {
            entry.device.metadata.button_count > 0
                && !entry.device.endpoint.requires_motion_confirmation()
        }
        EndpointKind::ControlOnly | EndpointKind::Inactive => false,
    });
    // Assign duplicate ordinals from source identities, never from discovery order.
    entries.sort_by(|left, right| left.identity.cmp(&right.identity));
    let duplicate_key = |entry: &Entry<'_>| {
        (
            entry.name.clone(),
            entry.device.metadata.vendor_id,
            entry.device.metadata.product_id,
        )
    };
    let mut counts = HashMap::new();
    for entry in &entries {
        *counts.entry(duplicate_key(entry)).or_insert(0usize) += 1;
    }
    let mut indices = HashMap::new();
    let mut labelled = Vec::with_capacity(entries.len());
    for entry in entries {
        let duplicate = duplicate_key(&entry);
        let index = indices.entry(duplicate.clone()).or_insert(0usize);
        *index += 1;
        let name = &entry.device.metadata.display_name;
        let label = if counts[&duplicate] > 1 {
            format!("{name} #{index}")
        } else {
            name.clone()
        };
        let priority = (
            entry.kind != EndpointKind::MotionCapable,
            entry.name.starts_with("hid mouse"),
            entry.name,
            entry.identity,
        );
        labelled.push((
            priority,
            DeviceChoice {
                key: entry.device.key,
                label,
                detail: detail(entry.device),
                kind: entry.kind,
                physical_device_key: entry.group,
            },
        ));
    }
    labelled.sort_by(|left, right| left.0.cmp(&right.0));
    labelled.into_iter().map(|(_, choice)| choice).collect()
}

fn classify(device: &DeviceDescriptor, group_has_motion: bool) -> EndpointKind {
    match (
        device.endpoint.activity.motion,
        group_has_motion,
        device.endpoint.activity.observed,
    ) {
        (true, _, _) => EndpointKind::MotionCapable,
        (false, true, true) => EndpointKind::ControlOnly,
        (false, true, false) => EndpointKind::Inactive,
        (false, false, _) => EndpointKind::Unknown,
    }
}

fn detail(device: &DeviceDescriptor) -> String {
    let mut parts = Vec::with_capacity(2);
    if let (Some(vendor), Some(product)) = (device.metadata.vendor_id, device.metadata.product_id) {
        parts.push(format!("VID_{vendor:04X} / PID_{product:04X}"));
    }
    if !device.endpoint.endpoint_token.is_empty() {
        parts.push(device.endpoint.endpoint_token.clone());
    }
    if parts.is_empty() {
        parts.push(device.metadata.path_summary.clone());
    }
    parts.join(" · ")
}
