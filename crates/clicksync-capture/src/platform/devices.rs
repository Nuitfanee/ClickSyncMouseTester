//! Cold-path Windows discovery. No metadata APIs run from WM_INPUT.
mod hid;

use super::*;
use clicksync_core::devices::{DevicePath, EndpointDescriptor, EndpointRole};
use std::mem::size_of_val;
use windows_sys::Win32::Devices::DeviceAndDriverInstallation::*;
use windows_sys::Win32::Devices::Properties::{DEVPKEY_Device_ContainerId, DEVPROP_TYPE_GUID};
use windows_sys::Win32::Foundation::ERROR_NOT_FOUND;

const MAX_PROPERTY_BYTES: usize = MAX_CAPTURE_DEVICE_FIELD_BYTES;

#[derive(Clone)]
struct Facts {
    handle: usize,
    name: String,
    // None retries a failed lookup; Some(None) caches a confirmed missing property.
    container: Option<Option<String>>,
    relative_motion: Option<bool>,
}

#[derive(Default)]
pub(super) struct Discovery {
    facts: HashMap<String, Facts>,
    keyboard_functions: HashMap<String, KeyboardFacts>,
}

struct KeyboardFacts {
    handle: usize,
    typing_matrix: Option<bool>,
    container: Option<Option<String>>,
}

impl Discovery {
    pub fn enumerate(&mut self) -> io::Result<Vec<DiscoveredDevice>> {
        // Validate the complete raw directory before touching the metadata cache.
        let endpoints = raw_endpoints()?;
        let present: HashMap<_, _> = endpoints
            .iter()
            .map(|raw| (raw.identity.as_str(), raw.handle as usize))
            .collect();
        self.facts
            .retain(|path, facts| present.get(path.as_str()) == Some(&facts.handle));
        self.keyboard_functions
            .retain(|path, facts| present.get(path.as_str()) == Some(&facts.handle));
        let mut typing_interfaces = HashSet::new();
        let mut keyboard_interfaces = HashSet::new();
        let mut keyboard_groups = HashSet::new();
        let mut keyboard_topology_complete = true;
        // Share one SetupAPI set across mouse and keyboard topology lookups.
        let mut setup = None;
        for raw in endpoints.iter().filter(|raw| raw.kind == RIM_TYPEKEYBOARD) {
            let parsed = DevicePath::parse(&raw.path);
            let facts = self
                .keyboard_functions
                .entry(raw.identity.clone())
                .or_insert_with(|| KeyboardFacts {
                    handle: raw.handle as usize,
                    typing_matrix: None,
                    container: None,
                });
            let container = cached_query(&mut facts.container, || {
                setup
                    .get_or_insert_with(DeviceInfoSet::new)
                    .as_ref()
                    .and_then(|set| set.properties(&raw.identity))
                    .map(|(_, container)| container)
            })
            .as_ref()
            .and_then(|container| container.as_deref());
            keyboard_topology_complete &= container.is_some();
            keyboard_interfaces.insert(parsed.interface_key.clone());
            keyboard_groups.insert(parsed.physical_device_key);
            if let Some(container) = container {
                keyboard_groups.insert(container.to_owned());
            }
            if *cached_query(&mut facts.typing_matrix, || {
                hid::typing_keyboard(&raw.identity)
            }) == Some(true)
            {
                typing_interfaces.insert(parsed.interface_key);
            }
        }
        let mut devices = Vec::with_capacity(endpoints.len().min(MAX_DEVICE_COUNT));
        for raw in endpoints.iter().filter(|raw| raw.kind == RIM_TYPEMOUSE) {
            let parsed = DevicePath::parse(&raw.path);
            let facts = self
                .facts
                .entry(raw.identity.clone())
                .or_insert_with(|| Facts {
                    handle: raw.handle as usize,
                    name: String::new(),
                    container: None,
                    relative_motion: None,
                });
            let mut setup_name = String::new();
            let container = cached_query(&mut facts.container, || {
                setup
                    .get_or_insert_with(DeviceInfoSet::new)
                    .as_ref()
                    .and_then(|set| set.properties(&raw.identity))
                    .map(|(name, container)| {
                        setup_name = name;
                        container
                    })
            })
            .as_ref()
            .and_then(|container| container.as_deref());
            if facts.name.is_empty() {
                let (manufacturer, product) = hid::names(&raw.identity);
                facts.name = resolve_name(&manufacturer, &product, &setup_name, &parsed);
            }
            let relative_motion = *cached_query(&mut facts.relative_motion, || {
                hid::relative_mouse(&raw.identity)
            });
            let keyboard_related = related_keyboard(
                &parsed,
                container,
                &keyboard_interfaces,
                &keyboard_groups,
                keyboard_topology_complete,
            );
            let role = endpoint_role(
                &parsed,
                &typing_interfaces,
                relative_motion,
                keyboard_related,
            );
            devices.push(DiscoveredDevice {
                handle: raw.handle as usize,
                metadata: SessionMetadata {
                    display_name: facts.name.clone(),
                    device_id: raw.path.clone(),
                    vendor_id: parsed.vendor_id,
                    product_id: parsed.product_id,
                    button_count: raw.button_count,
                    is_virtual: is_virtual(&raw.identity, &facts.name),
                    path_summary: parsed.summary,
                },
                endpoint: EndpointDescriptor {
                    role,
                    physical_device_key: container
                        .unwrap_or(&parsed.physical_device_key)
                        .to_owned(),
                    endpoint_token: parsed.endpoint_token,
                    ..Default::default()
                },
            });
        }
        Ok(devices)
    }

    pub fn remove(&mut self, path: &str) {
        self.facts
            .remove(&clicksync_core::devices::path_normalize(path));
    }

    pub fn remove_handle(&mut self, handle: usize) {
        self.facts.retain(|_, facts| facts.handle != handle);
        self.keyboard_functions
            .retain(|_, facts| facts.handle != handle);
    }
}

fn cached_query<T>(cached: &mut Option<T>, query: impl FnOnce() -> Option<T>) -> &Option<T> {
    if cached.is_none() {
        *cached = query();
    }
    cached
}

fn related_keyboard(
    path: &DevicePath,
    container: Option<&str>,
    interfaces: &HashSet<String>,
    groups: &HashSet<String>,
    keyboard_topology_complete: bool,
) -> Option<bool> {
    if interfaces.contains(&path.interface_key)
        || groups.contains(&path.physical_device_key)
        || container.is_some_and(|container| groups.contains(container))
    {
        Some(true)
    } else {
        (keyboard_topology_complete && container.is_some()).then_some(false)
    }
}

fn endpoint_role(
    path: &DevicePath,
    typing_interfaces: &HashSet<String>,
    relative_motion: Option<bool>,
    keyboard_related: Option<bool>,
) -> EndpointRole {
    if typing_interfaces.contains(&path.interface_key) {
        EndpointRole::KeyboardAuxiliary
    } else {
        match (relative_motion, keyboard_related) {
            (Some(false), _) => EndpointRole::NonMotion,
            (Some(true), Some(false)) => EndpointRole::Unclassified,
            _ => EndpointRole::MotionPending,
        }
    }
}

struct RawEndpoint {
    handle: HANDLE,
    kind: u32,
    path: String,
    identity: String,
    button_count: i32,
}

fn raw_endpoints() -> io::Result<Vec<RawEndpoint>> {
    let list = raw_devices()?;
    if [RIM_TYPEMOUSE, RIM_TYPEKEYBOARD]
        .iter()
        .any(|kind| list.iter().filter(|raw| raw.dwType == *kind).count() > MAX_DEVICE_COUNT)
    {
        return Err(io::Error::new(
            io::ErrorKind::OutOfMemory,
            "too many Raw Input devices of one input type",
        ));
    }
    list.into_iter()
        .filter(|raw| matches!(raw.dwType, RIM_TYPEMOUSE | RIM_TYPEKEYBOARD))
        .map(|raw| {
            let path = read_path(raw.hDevice)?;
            Ok(RawEndpoint {
                handle: raw.hDevice,
                kind: raw.dwType,
                identity: clicksync_core::devices::path_normalize(&path),
                button_count: if raw.dwType == RIM_TYPEMOUSE {
                    read_button_count(raw.hDevice)?
                } else {
                    0
                },
                path,
            })
        })
        .collect()
}

fn raw_devices() -> io::Result<Vec<RAWINPUTDEVICELIST>> {
    for _ in 0..3 {
        let mut count = 0;
        if unsafe {
            GetRawInputDeviceList(
                null_mut(),
                &mut count,
                size_of::<RAWINPUTDEVICELIST>() as u32,
            )
        } == u32::MAX
        {
            return Err(io::Error::last_os_error());
        }
        if count as usize > MAX_ENUMERATED_DEVICES {
            return Err(io::Error::new(
                io::ErrorKind::OutOfMemory,
                "Raw Input directory is too large",
            ));
        }
        if count == 0 {
            return Ok(Vec::new());
        }
        let mut list = vec![RAWINPUTDEVICELIST::default(); count as usize];
        let found = unsafe {
            GetRawInputDeviceList(
                list.as_mut_ptr(),
                &mut count,
                size_of::<RAWINPUTDEVICELIST>() as u32,
            )
        };
        if found == u32::MAX {
            let error = io::Error::last_os_error();
            if error.raw_os_error() == Some(122) {
                continue;
            }
            return Err(error);
        }
        if found as usize > list.len() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid Raw Input directory length",
            ));
        }
        list.truncate(found as usize);
        return Ok(list);
    }
    Err(io::Error::new(
        io::ErrorKind::WouldBlock,
        "Raw Input directory kept changing",
    ))
}

fn read_path(handle: HANDLE) -> io::Result<String> {
    let mut units = 0;
    if unsafe { GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, null_mut(), &mut units) }
        == u32::MAX
    {
        return Err(io::Error::last_os_error());
    }
    if units == 0 || units as usize >= MAX_DEVICE_PATH_UNITS {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid Raw Input path length",
        ));
    }
    let mut buffer = vec![0u16; units as usize + 1];
    units = buffer.len() as u32;
    if unsafe {
        GetRawInputDeviceInfoW(
            handle,
            RIDI_DEVICENAME,
            buffer.as_mut_ptr().cast(),
            &mut units,
        )
    } == u32::MAX
    {
        return Err(io::Error::last_os_error());
    }
    let path = decode_wide(&buffer)
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "invalid Raw Input path"))?;
    if path.is_empty() || path.len() > MAX_CAPTURE_DEVICE_FIELD_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Raw Input path exceeds its bound",
        ));
    }
    Ok(path)
}

fn read_button_count(handle: HANDLE) -> io::Result<i32> {
    // The complete Win32 union is 32 bytes; using a mouse-only struct loses the query.
    let mut info = RID_DEVICE_INFO {
        cbSize: size_of::<RID_DEVICE_INFO>() as u32,
        ..Default::default()
    };
    let mut bytes = info.cbSize;
    if unsafe {
        GetRawInputDeviceInfoW(
            handle,
            RIDI_DEVICEINFO,
            (&mut info as *mut RID_DEVICE_INFO).cast(),
            &mut bytes,
        )
    } == u32::MAX
    {
        return Err(io::Error::last_os_error());
    }
    if info.dwType != RIM_TYPEMOUSE || bytes != info.cbSize {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid Raw Input mouse information",
        ));
    }
    Ok(unsafe { info.Anonymous.mouse.dwNumberOfButtons }.min(i32::MAX as u32) as i32)
}

struct DeviceInfoSet(HDEVINFO);

impl DeviceInfoSet {
    fn new() -> Option<Self> {
        let handle = unsafe { SetupDiCreateDeviceInfoList(null(), null_mut()) };
        (handle != -1 && handle != 0).then_some(Self(handle))
    }

    fn properties(&self, path: &str) -> Option<(String, Option<String>)> {
        let wide: Vec<_> = path.encode_utf16().chain([0]).collect();
        let mut interface = SP_DEVICE_INTERFACE_DATA {
            cbSize: size_of::<SP_DEVICE_INTERFACE_DATA>() as u32,
            ..Default::default()
        };
        if unsafe { SetupDiOpenDeviceInterfaceW(self.0, wide.as_ptr(), 0, &mut interface) } == 0 {
            return None;
        }
        let mut info = SP_DEVINFO_DATA {
            cbSize: size_of::<SP_DEVINFO_DATA>() as u32,
            ..Default::default()
        };
        let mut required = 0;
        unsafe {
            SetupDiGetDeviceInterfaceDetailW(
                self.0,
                &interface,
                null_mut(),
                0,
                &mut required,
                &mut info,
            )
        };
        if required == 0 || required as usize > MAX_PROPERTY_BYTES * 2 + 8 {
            return None;
        }
        let mut buffer = vec![0u64; (required as usize).div_ceil(8)];
        let detail = buffer
            .as_mut_ptr()
            .cast::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>();
        unsafe { (*detail).cbSize = size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32 };
        if unsafe {
            SetupDiGetDeviceInterfaceDetailW(
                self.0,
                &interface,
                detail,
                required,
                &mut required,
                &mut info,
            )
        } == 0
        {
            return None;
        }
        let name = self
            .registry_text(&info, SPDRP_FRIENDLYNAME)
            .filter(|value| !sanitize_name(value).is_empty())
            .or_else(|| self.registry_text(&info, SPDRP_DEVICEDESC))
            .unwrap_or_default();
        let mut container = windows_sys::core::GUID::from_u128(0);
        let mut property_type = 0;
        let mut required = 0;
        let read = unsafe {
            SetupDiGetDevicePropertyW(
                self.0,
                &info,
                &DEVPKEY_Device_ContainerId,
                &mut property_type,
                (&mut container as *mut windows_sys::core::GUID).cast(),
                size_of::<windows_sys::core::GUID>() as u32,
                &mut required,
                0,
            )
        };
        if read == 0 {
            return (io::Error::last_os_error().raw_os_error() == Some(ERROR_NOT_FOUND as i32))
                .then_some((name, None));
        }
        if property_type != DEVPROP_TYPE_GUID
            || required as usize != size_of::<windows_sys::core::GUID>()
        {
            return None;
        }
        let physical = container_key(container);
        Some((name, physical))
    }

    fn registry_text(&self, info: &SP_DEVINFO_DATA, property: u32) -> Option<String> {
        let mut buffer = [0u16; MAX_PROPERTY_BYTES / 2];
        let mut required = 0;
        if unsafe {
            SetupDiGetDeviceRegistryPropertyW(
                self.0,
                info,
                property,
                null_mut(),
                buffer.as_mut_ptr().cast(),
                size_of_val(&buffer) as u32,
                &mut required,
            )
        } == 0
        {
            return None;
        }
        decode_wide(&buffer).filter(|text| text.len() <= MAX_CAPTURE_DEVICE_FIELD_BYTES)
    }
}

fn container_key(container: windows_sys::core::GUID) -> Option<String> {
    (container.data1 != 0
        || container.data2 != 0
        || container.data3 != 0
        || container.data4 != [0; 8])
        .then(|| {
            format!(
                "container:{:08X}-{:04X}-{:04X}-{:02X}{:02X}-{:02X}{:02X}{:02X}{:02X}{:02X}{:02X}",
                container.data1,
                container.data2,
                container.data3,
                container.data4[0],
                container.data4[1],
                container.data4[2],
                container.data4[3],
                container.data4[4],
                container.data4[5],
                container.data4[6],
                container.data4[7]
            )
        })
}

impl Drop for DeviceInfoSet {
    fn drop(&mut self) {
        unsafe { SetupDiDestroyDeviceInfoList(self.0) };
    }
}

fn decode_wide(buffer: &[u16]) -> Option<String> {
    let length = buffer.iter().position(|unit| *unit == 0)?;
    String::from_utf16(&buffer[..length]).ok()
}

fn sanitize_name(value: &str) -> String {
    let text = value
        .chars()
        .filter(|ch| !ch.is_control())
        .map(|ch| if ch == '_' { ' ' } else { ch })
        .collect::<String>();
    let text = text.split_whitespace().collect::<Vec<_>>().join(" ");
    const GENERIC: [&str; 8] = [
        "hid-compliant mouse",
        "usb input device",
        "hid-compliant device",
        "hid mouse",
        "mouse",
        "mouse device",
        "符合 hid 标准的鼠标",
        "usb 输入设备",
    ];
    if GENERIC.contains(&text.to_lowercase().as_str()) {
        String::new()
    } else {
        text
    }
}

fn resolve_name(manufacturer: &str, product: &str, setup: &str, path: &DevicePath) -> String {
    let manufacturer = sanitize_name(manufacturer);
    let product = sanitize_name(product);
    let hid = match (manufacturer.is_empty(), product.is_empty()) {
        (false, false)
            if !product
                .to_lowercase()
                .contains(&manufacturer.to_lowercase()) =>
        {
            format!("{manufacturer} {product}")
        }
        (_, false) => product,
        (_, true) => String::new(),
    };
    let setup = sanitize_name(setup);
    [hid, setup, manufacturer]
        .into_iter()
        .find(|name| !name.is_empty())
        .unwrap_or_else(|| match (path.vendor_id, path.product_id) {
            (Some(vendor), Some(product)) => {
                format!("HID Mouse (VID_{vendor:04X}/PID_{product:04X})")
            }
            _ => "HID Mouse".into(),
        })
}

fn is_virtual(path: &str, name: &str) -> bool {
    let name = name.to_uppercase();
    ["ROOT", "VIRTUAL", "RDP_MOU", "VMWARE", "VMBUS"]
        .iter()
        .any(|token| path.contains(token))
        || ["VIRTUAL", "REMOTE DESKTOP", "RDP"]
            .iter()
            .any(|token| name.contains(token))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keyboard_topology_requires_motion_for_related_interfaces_not_other_instances() {
        let keyboard = DevicePath::parse(
            r"\\?\HID#VID_ABCD&PID_1234&MI_00&COL01#8&instance-a&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}",
        );
        let interfaces = HashSet::from([keyboard.interface_key.clone()]);
        let groups = HashSet::from([
            keyboard.physical_device_key.clone(),
            "container:shared-receiver".into(),
        ]);
        let mouse_path = r"\\?\HID#VID_ABCD&PID_1234&MI_01&COL03#8&instance-a&0&0002#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
        for (path, physical, mixed) in [
            (mouse_path.to_owned(), "unavailable", true),
            (
                mouse_path.replace("instance-a", "different-interface-instance"),
                "container:shared-receiver",
                true,
            ),
            (
                mouse_path.replace("instance-a", "another-device"),
                "container:another-device",
                false,
            ),
        ] {
            let parsed = DevicePath::parse(&path);
            let related = related_keyboard(&parsed, Some(physical), &interfaces, &groups, true);
            assert_eq!(related, Some(mixed));
            // Array-format or unreadable keyboard descriptors provide no typing matrix.
            assert_eq!(
                endpoint_role(&parsed, &HashSet::new(), Some(true), related),
                if mixed {
                    EndpointRole::MotionPending
                } else {
                    EndpointRole::Unclassified
                }
            );
        }
        let sibling = DevicePath::parse(&mouse_path.replace("MI_01", "MI_00"));
        assert_eq!(
            related_keyboard(&sibling, None, &interfaces, &HashSet::new(), false),
            Some(true)
        );
    }

    #[test]
    fn unavailable_topology_is_not_evidence_of_an_independent_mouse() {
        let mouse = DevicePath::parse(r"\\?\HID#VID_ABCD&PID_1234&MI_01#8&mouse-instance&0&0000");
        for (container, keyboard_complete, expected) in [
            (None, true, None),
            (Some("container:mouse"), false, None),
            (Some("container:mouse"), true, Some(false)),
        ] {
            let related = related_keyboard(
                &mouse,
                container,
                &HashSet::new(),
                &HashSet::new(),
                keyboard_complete,
            );
            assert_eq!(related, expected);
            assert_eq!(
                endpoint_role(&mouse, &HashSet::new(), Some(true), related),
                if expected == Some(false) {
                    EndpointRole::Unclassified
                } else {
                    EndpointRole::MotionPending
                }
            );
        }
        let groups = HashSet::from(["container:receiver".to_owned()]);
        assert_eq!(
            related_keyboard(
                &mouse,
                Some("container:receiver"),
                &HashSet::new(),
                &groups,
                false
            ),
            Some(true)
        );
    }

    #[test]
    fn typing_keyboard_role_matches_its_hid_interface_and_instance_only() {
        let keyboard = DevicePath::parse(
            r"\\?\HID#VID_1CA6&PID_3D11&MI_01&COL01#8&39e8748b&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}",
        );
        let typing_interfaces = HashSet::from([keyboard.interface_key]);
        let mouse_path = r"\\?\HID#VID_1CA6&PID_3D11&MI_01&COL04#8&39e8748b&0&0003#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
        assert_eq!(
            endpoint_role(
                &DevicePath::parse(mouse_path),
                &typing_interfaces,
                Some(true),
                Some(true)
            ),
            EndpointRole::KeyboardAuxiliary
        );
        // A separate macro interface or another physical instance is not the keyboard's mouse.
        for path in [
            mouse_path.replace("MI_01", "MI_00"),
            mouse_path.replace("39e8748b", "another-instance"),
            mouse_path.replace("VID_1CA6", "VID_3057"),
        ] {
            assert_eq!(
                endpoint_role(
                    &DevicePath::parse(&path),
                    &typing_interfaces,
                    Some(true),
                    Some(false)
                ),
                EndpointRole::Unclassified
            );
        }
    }

    #[test]
    fn mouse_class_endpoint_without_relative_motion_is_non_motion() {
        let path = DevicePath::parse(
            r"\\?\HID#VID_ABCD&PID_1234&MI_01&COL03#8&instance&0&0001#{378de44c-56ef-11d1-bc8c-00a0c91405dd}",
        );
        assert_eq!(
            endpoint_role(&path, &HashSet::new(), Some(false), Some(false)),
            EndpointRole::NonMotion
        );
        assert_eq!(
            endpoint_role(&path, &HashSet::new(), Some(true), Some(false)),
            EndpointRole::Unclassified
        );
        for (relative_motion, keyboard_related) in [
            (None, Some(false)),
            (None, Some(true)),
            (Some(true), Some(true)),
            (Some(true), None),
        ] {
            assert_eq!(
                endpoint_role(&path, &HashSet::new(), relative_motion, keyboard_related),
                EndpointRole::MotionPending
            );
        }
    }

    #[test]
    fn failed_capability_query_retries_but_confirmed_absence_is_cached() {
        let mut cached = None;
        assert_eq!(*cached_query(&mut cached, || None), None);
        assert_eq!(*cached_query(&mut cached, || Some(true)), Some(true));
        assert_eq!(
            *cached_query(&mut cached, || panic!("cached success")),
            Some(true)
        );
        cached = None;
        assert_eq!(*cached_query(&mut cached, || Some(false)), Some(false));
        assert_eq!(
            *cached_query(&mut cached, || panic!("cached absence")),
            Some(false)
        );
    }

    #[test]
    fn topology_cache_retries_failure_and_retains_confirmed_missing_container() {
        let mut container: Option<Option<String>> = None;
        assert_eq!(cached_query(&mut container, || None), &None);
        assert_eq!(
            cached_query(&mut container, || Some(Some("container:receiver".into()))),
            &Some(Some("container:receiver".into()))
        );
        assert_eq!(
            cached_query(&mut container, || panic!("cached container")),
            &Some(Some("container:receiver".into()))
        );
        container = None;
        assert_eq!(cached_query(&mut container, || Some(None)), &Some(None));
        assert_eq!(
            cached_query(&mut container, || panic!("confirmed absence")),
            &Some(None)
        );
    }

    #[test]
    fn keyboard_removal_invalidates_only_its_cached_descriptor_facts() {
        let mut discovery = Discovery::default();
        for (path, handle) in [("keyboard-a", 10), ("keyboard-b", 20)] {
            discovery.keyboard_functions.insert(
                path.into(),
                KeyboardFacts {
                    handle,
                    typing_matrix: Some(true),
                    container: Some(Some(path.into())),
                },
            );
            discovery.facts.insert(
                path.into(),
                Facts {
                    handle,
                    name: String::new(),
                    container: Some(Some(path.into())),
                    relative_motion: Some(true),
                },
            );
        }
        discovery.remove_handle(10);
        assert!(!discovery.keyboard_functions.contains_key("keyboard-a"));
        assert!(discovery.keyboard_functions.contains_key("keyboard-b"));
        assert!(!discovery.facts.contains_key("keyboard-a"));
        assert!(discovery.facts.contains_key("keyboard-b"));
    }

    #[test]
    fn names_and_virtual_identity_use_bounded_hardware_facts() {
        let path = DevicePath::parse(r"\\?\HID#VID_3057&PID_2002&MI_01");
        assert_eq!(
            resolve_name("PIXART", "VAXEE_8K  Wireless", "HID-compliant mouse", &path),
            "PIXART VAXEE 8K Wireless"
        );
        assert_eq!(
            resolve_name("Razer", "Razer Viper", "", &path),
            "Razer Viper"
        );
        assert_eq!(
            resolve_name("Razer", "", "Viper V4 Pro", &path),
            "Viper V4 Pro"
        );
        assert_eq!(
            resolve_name("", "", "Mouse", &path),
            "HID Mouse (VID_3057/PID_2002)"
        );
        assert!(is_virtual(
            r"\\?\HID#GVINPUT&COL01",
            "DJP Inc. Virtual Multitouch Device"
        ));
        assert_eq!(size_of::<RID_DEVICE_INFO>(), 32);
        assert_eq!(sanitize_name("  名称_ \t Mouse\0  "), "名称 Mouse");
        assert!(container_key(windows_sys::core::GUID::from_u128(0)).is_none());
        assert_eq!(
            container_key(windows_sys::core::GUID::from_u128(1)).as_deref(),
            Some("container:00000000-0000-0000-0000-000000000001")
        );
    }
}
