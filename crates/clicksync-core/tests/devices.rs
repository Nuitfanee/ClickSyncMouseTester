use clicksync_core::SessionMetadata;
use clicksync_core::devices::{
    DeviceCatalog, DeviceDescriptor, DevicePath, DeviceSource, EndpointActivity,
    EndpointDescriptor, EndpointKind, EndpointRole,
};

fn device(key: u32, path: &str, name: &str, group: &str, buttons: i32) -> DeviceDescriptor {
    DeviceDescriptor {
        key,
        metadata: SessionMetadata {
            device_id: path.into(),
            display_name: name.into(),
            button_count: buttons,
            ..Default::default()
        },
        endpoint: EndpointDescriptor {
            physical_device_key: group.into(),
            ..Default::default()
        },
    }
}

#[test]
fn standard_hid_paths_keep_both_hardware_ids_and_collection_identity() {
    let path = DevicePath::parse(
        r"\\?\hid#vid_046d&pid_c547&mi_01&col02#7&abcdef&0&0001#{378de44c-56ef-11d1-bc8c-00a0c91405dd}",
    );
    assert_eq!(path.vendor_id, Some(0x046d));
    assert_eq!(path.product_id, Some(0xc547));
    assert_eq!(path.endpoint_token, "MI_01 / COL02");
    let sibling = DevicePath::parse(
        r"\\?\HID#VID_046D&PID_C547&MI_00#7&abcdef&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}",
    );
    assert_eq!(path.physical_device_key, sibling.physical_device_key);
    assert_ne!(path.interface_key, sibling.interface_key);
    let second_mouse = DevicePath::parse(
        r"\\?\HID#VID_046D&PID_C547&MI_00#7&other-instance&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}",
    );
    assert_ne!(path.physical_device_key, second_mouse.physical_device_key);
    for invalid in ["XVID_046D", "VID_XXXX", "VID_046DX", "VID_046"] {
        assert_eq!(DevicePath::parse(invalid).vendor_id, None);
    }
}

#[test]
fn hid_interface_identity_joins_collections_without_joining_other_interfaces_or_instances() {
    let mouse_path = r"\\?\HID#VID_1CA6&PID_3D11&MI_01&Col04#8&39e8748b&0&0003#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
    let mouse = DevicePath::parse(mouse_path);
    let keyboard = DevicePath::parse(
        r"\??\hid#vid_1ca6&pid_3d11&mi_01&col01#8&39e8748b&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}",
    );
    assert_eq!(mouse.interface_key, keyboard.interface_key);
    assert_eq!(mouse.physical_device_key, keyboard.physical_device_key);
    let separate_interface = DevicePath::parse(&mouse_path.replace("MI_01", "MI_00"));
    assert_eq!(
        mouse.physical_device_key,
        separate_interface.physical_device_key
    );
    assert_ne!(mouse.interface_key, separate_interface.interface_key);
    let other_instance = DevicePath::parse(&mouse_path.replace("39e8748b", "another-instance"));
    assert_ne!(mouse.interface_key, other_instance.interface_key);
}

#[test]
fn keyboard_auxiliary_role_overrides_buttons_motion_and_product_name() {
    let mut keyboard = device(1, "keyboard-mouse", "GL68", "physical-a", 8);
    keyboard.endpoint.role = EndpointRole::KeyboardAuxiliary;
    keyboard.endpoint.activity.observe(10, 5);
    let mouse = device(2, "mouse", "Mouse", "physical-b", 8);
    let devices = [keyboard, mouse];
    let catalog = DeviceCatalog::new(&devices);
    assert!(!devices[0].eligible_for_motion());
    assert!(devices[1].eligible_for_motion());
    assert!(catalog.choice(1).is_none());
    assert!(catalog.choice(2).is_some());
    assert!(!catalog.accepts_selection(&[1]));
    assert!(!catalog.accepts_selection(&[1, 2]));
    assert!(!catalog.can_choose(1, None));
    assert_eq!(catalog.resolve_same_device(1), None);
    assert!(catalog.accepts_selection(&[2]));
}

#[test]
fn motion_confirmation_is_a_shared_raw_input_policy_not_a_replay_constraint() {
    let mut endpoint = EndpointDescriptor::default();
    for role in [
        EndpointRole::Unclassified,
        EndpointRole::KeyboardAuxiliary,
        EndpointRole::NonMotion,
        EndpointRole::MotionPending,
    ] {
        endpoint.role = role;
        endpoint.source = DeviceSource::RawInput;
        assert_eq!(
            endpoint.requires_motion_confirmation(),
            role == EndpointRole::MotionPending
        );
        endpoint.source = DeviceSource::Replay;
        assert!(!endpoint.requires_motion_confirmation());
    }
}

#[test]
fn shared_receiver_name_does_not_override_motion_evidence_or_endpoint_role() {
    let mut mouse = device(
        1,
        "receiver-mouse",
        "Keyboard / Mouse Receiver",
        "receiver",
        5,
    );
    mouse.endpoint.role = EndpointRole::MotionPending;
    assert!(mouse.eligible_for_motion());
    assert!(
        DeviceCatalog::new(std::slice::from_ref(&mouse))
            .choices()
            .is_empty()
    );
    mouse.endpoint.activity.observe(1, 0);
    assert!(DeviceCatalog::new(std::slice::from_ref(&mouse)).accepts_selection(&[1]));
    for role in [EndpointRole::KeyboardAuxiliary, EndpointRole::NonMotion] {
        mouse.endpoint.role = role;
        assert!(!mouse.eligible_for_motion());
        assert!(
            DeviceCatalog::new(std::slice::from_ref(&mouse))
                .choices()
                .is_empty()
        );
    }
}

#[test]
fn non_motion_mouse_class_endpoint_is_not_a_selectable_device() {
    let mut control = device(
        1,
        "mouse-class-control",
        "Input Controller",
        "physical-a",
        8,
    );
    control.endpoint.role = EndpointRole::NonMotion;
    control.endpoint.activity.observe(5, 5);
    assert!(!control.eligible_for_motion());
    let catalog = DeviceCatalog::new(&[control]);
    assert!(catalog.choices().is_empty());
    assert!(!catalog.accepts_selection(&[1]));
    assert!(!catalog.can_choose(1, None));
}

#[test]
fn ambiguous_endpoints_need_motion_without_hiding_an_independent_idle_mouse() {
    let mut ambiguous = device(1, "mixed", "Input Device", "shared-receiver", 8);
    ambiguous.endpoint.role = EndpointRole::MotionPending;
    let idle = device(2, "idle-mouse", "Mouse", "another-device", 5);
    assert!(ambiguous.eligible_for_motion());
    for buttons in [8, 0] {
        ambiguous.metadata.button_count = buttons;
        ambiguous.endpoint.activity = EndpointActivity::default();
        let mut catalog = DeviceCatalog::new(&[ambiguous.clone(), idle.clone()]);
        assert_eq!(catalog.choices().len(), 1);
        assert!(catalog.choice(2).is_some());
        assert!(!catalog.accepts_selection(&[1]));
        ambiguous.endpoint.activity.observe(0, 0);
        catalog.update(&[ambiguous.clone(), idle.clone()]);
        assert!(catalog.choice(1).is_none());
        ambiguous.endpoint.activity.observe(1, 0);
        catalog.update(&[ambiguous.clone(), idle.clone()]);
        assert_eq!(catalog.choice(1).unwrap().kind, EndpointKind::MotionCapable);
        assert!(catalog.accepts_selection(&[1, 2]));
        assert!(catalog.choice(2).is_some());
    }
}

#[test]
fn reclassification_removes_selection_and_keeps_a_same_device_motion_replacement() {
    let initial = device(1, "initial", "Device", "group-a", 8);
    let mut catalog = DeviceCatalog::new(std::slice::from_ref(&initial));
    assert_eq!(catalog.resolve_selection(Some(1), Some(1)), Some(1));
    for role in [
        EndpointRole::KeyboardAuxiliary,
        EndpointRole::NonMotion,
        EndpointRole::MotionPending,
    ] {
        let mut retired = initial.clone();
        retired.endpoint.role = role;
        catalog.update(std::slice::from_ref(&retired));
        assert_eq!(catalog.resolve_selection(Some(1), Some(1)), None);
        let mut replacement = device(2, "replacement", "Device", "group-a", 5);
        replacement.endpoint.activity.observe(1, 0);
        catalog.update(&[retired, replacement]);
        assert_eq!(catalog.resolve_same_device(1), Some(2));
    }
}

#[test]
fn endpoint_role_roundtrips_and_absent_role_defaults_to_unclassified() {
    let mut endpoint = EndpointDescriptor {
        role: EndpointRole::KeyboardAuxiliary,
        ..Default::default()
    };
    let mut json = serde_json::to_value(&endpoint).unwrap();
    assert_eq!(
        serde_json::from_value::<EndpointDescriptor>(json.clone()).unwrap(),
        endpoint
    );
    json.as_object_mut().unwrap().remove("role");
    endpoint.role = EndpointRole::Unclassified;
    assert_eq!(
        serde_json::from_value::<EndpointDescriptor>(json).unwrap(),
        endpoint
    );
    for role in [EndpointRole::NonMotion, EndpointRole::MotionPending] {
        endpoint.role = role;
        assert_eq!(
            serde_json::from_value::<EndpointDescriptor>(serde_json::to_value(&endpoint).unwrap())
                .unwrap(),
            endpoint
        );
    }
}

#[test]
fn motion_classification_hides_only_nonmotion_siblings_and_keeps_other_mice() {
    let mut motion = device(1, "mouse-a", "Mouse A", "physical-a", 8);
    motion.endpoint.activity.observe(1, 0);
    let mut control = device(2, "control-a", "Mouse A", "physical-a", 8);
    control.endpoint.activity.observe(0, 0);
    let silent = device(3, "silent-a", "Mouse A", "physical-a", 5);
    let independent = device(4, "mouse-b", "Mouse B", "physical-b", 5);
    let mut zero_buttons = device(5, "unknown", "Unknown", "physical-c", 0);
    let mut keyboard = device(6, "keyboard", "Mouse / Keyboard", "physical-d", 5);
    keyboard.endpoint.role = EndpointRole::KeyboardAuxiliary;
    keyboard.endpoint.activity.observe(4, 0);
    let mut virtual_mouse = device(7, "virtual", "Virtual mouse", "physical-e", 5);
    virtual_mouse.metadata.is_virtual = true;
    let mut devices = vec![
        silent,
        independent,
        control,
        motion,
        zero_buttons.clone(),
        keyboard,
        virtual_mouse,
    ];
    let mut catalog = DeviceCatalog::new(&devices);
    assert_eq!(
        catalog
            .choices()
            .iter()
            .map(|choice| choice.key)
            .collect::<Vec<_>>(),
        [1, 4]
    );
    assert_eq!(catalog.choice(1).unwrap().kind, EndpointKind::MotionCapable);
    assert_eq!(catalog.choice(4).unwrap().kind, EndpointKind::Unknown);
    assert!(!catalog.update(&devices));
    zero_buttons.endpoint.activity.observe(0, 1);
    devices[4] = zero_buttons;
    assert!(catalog.update(&devices));
    assert_eq!(catalog.choice(5).unwrap().kind, EndpointKind::MotionCapable);
    assert_eq!(catalog.resolve_selection(Some(4), None), Some(4));
    assert_eq!(catalog.resolve_same_device(2), Some(1));
    assert_eq!(catalog.resolve_same_device(7), None);
}

#[test]
fn duplicate_names_sort_by_identity_and_pair_selection_uses_physical_identity() {
    let mut left = device(20, "path-b", "Same Mouse", "container:one", 5);
    let mut sibling = device(30, "path-c", "Same Mouse", "container:one", 5);
    let mut right = device(10, "path-a", "Same Mouse", "container:two", 5);
    for mouse in [&mut left, &mut sibling, &mut right] {
        mouse.endpoint.activity.observe(1, 1);
    }
    let first = DeviceCatalog::new(&[left.clone(), right.clone(), sibling.clone()]);
    let second = DeviceCatalog::new(&[sibling, right, left]);
    assert_eq!(first.choices(), second.choices());
    assert_eq!(first.choice(10).unwrap().label, "Same Mouse #1");
    assert_eq!(first.choice(20).unwrap().label, "Same Mouse #2");
    assert!(!first.distinct_devices(20, 30));
    assert!(first.distinct_devices(20, 10));
    assert!(first.accepts_selection(&[20, 10]));
    assert!(!first.can_choose(20, Some(30)));
    assert!(!first.can_choose(10, Some(10)));
    assert!(first.can_choose(10, Some(99)));
    assert!(!first.accepts_selection(&[10, 99]));
    assert_eq!(first.label(10), Some("Same Mouse #1"));
    for keys in [&[][..], &[20, 30], &[10, 10], &[0], &[99]] {
        assert!(!first.accepts_selection(keys));
    }
    assert_eq!(first.resolve_selection(Some(10), Some(20)), Some(20));
}

#[test]
fn replay_is_an_explicit_source_and_capability_evidence_is_monotonic() {
    let mut replay = device(1, "replay", "Replay mouse", "", 5);
    replay.metadata.is_virtual = true;
    assert!(DeviceCatalog::new(&[replay.clone()]).choices().is_empty());
    replay.endpoint.source = DeviceSource::Replay;
    assert_eq!(DeviceCatalog::new(&[replay]).choices().len(), 1);
    let mut activity = EndpointActivity::default();
    assert!(activity.observe(0, 0));
    assert!(!activity.observe(0, 0));
    assert!(activity.observe(0, -1));
    assert!(!activity.observe(0, 0));
    assert!(activity.motion);
}
