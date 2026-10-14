use std::collections::HashSet;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use clicksync_capture::CaptureShared;
use clicksync_capture::platform::{InputCommand, InputService};
use clicksync_core::devices::{DeviceCatalog, DevicePath, DeviceSource};
use clicksync_core::protocol::{CAPTURE_QUEUE_RECORDS, CapturedRecord, MAX_DEVICE_COUNT};

#[test]
#[ignore = "requires a real Windows Raw Input registration; run explicitly in the Capture gate"]
fn real_window_initialization_device_enumeration_and_control_smoke() {
    let (producer, mut consumer) = rtrb::RingBuffer::<CapturedRecord>::new(CAPTURE_QUEUE_RECORDS);
    let shared = Arc::new(CaptureShared::default());
    let devices = Arc::new(Mutex::new(Vec::new()));
    let service = InputService::spawn(producer, Arc::clone(&shared), Arc::clone(&devices))
        .expect("real Raw Input window initialization");
    let (competing_producer, _competing_consumer) =
        rtrb::RingBuffer::<CapturedRecord>::new(CAPTURE_QUEUE_RECORDS);
    let competing = InputService::spawn(
        competing_producer,
        Arc::new(CaptureShared::default()),
        Arc::new(Mutex::new(Vec::new())),
    );
    assert_eq!(
        competing
            .err()
            .expect("duplicate registration must be rejected")
            .kind(),
        std::io::ErrorKind::AlreadyExists
    );
    let directory = devices.lock().unwrap().clone();
    assert!(directory.len() <= MAX_DEVICE_COUNT);
    let mut keys = HashSet::new();
    for device in &directory {
        assert!(device.key > 0);
        assert!(keys.insert(device.key));
        assert!(!device.metadata.device_id.is_empty());
        assert!(!device.metadata.display_name.is_empty());
        assert!(device.fields_are_bounded());
        assert_eq!(device.endpoint.source, DeviceSource::RawInput);
        let parsed = DevicePath::parse(&device.metadata.device_id);
        assert_eq!(device.metadata.vendor_id, parsed.vendor_id);
        assert_eq!(device.metadata.product_id, parsed.product_id);
        assert!(!device.physical_device_key().is_empty());
        if !device.eligible_for_motion() {
            assert!(
                service
                    .command(InputCommand::Start {
                        generation: 1,
                        device_keys: vec![device.key],
                        start_fresh: true,
                    })
                    .is_err()
            );
        }
    }
    service.command(InputCommand::Discover).unwrap();
    service.command(InputCommand::RescanDevices).unwrap();
    let initial = service.command(InputCommand::Pause).unwrap();
    assert_eq!(initial.cutoff, 0);
    assert!(initial.ticks > 0);
    assert!(initial.frequency > 0);
    assert_eq!(shared.overflow.load(Ordering::Acquire), 0);
    assert!(!shared.disconnected.load(Ordering::Acquire));
    assert!(!shared.active.load(Ordering::Acquire));
    let requested = Instant::now();
    clicksync_capture::platform::request_watermark(&shared);
    let watermark = loop {
        if let Some(mark) = shared.watermark.latest() {
            break mark;
        }
        assert!(requested.elapsed() < Duration::from_secs(3));
        std::thread::sleep(Duration::from_millis(1));
    };
    assert_eq!(watermark.sequence, 0);
    assert_eq!(watermark.frequency, initial.frequency);
    assert!(watermark.ticks >= initial.ticks);
    assert!(watermark.complete);
    assert!(!watermark.active);
    assert!(!shared.watermark.requested());

    let invalid = service.command(InputCommand::Start {
        generation: 1,
        device_keys: vec![u32::MAX],
        start_fresh: true,
    });
    assert_eq!(
        invalid.unwrap_err().kind(),
        std::io::ErrorKind::InvalidInput
    );
    let reset = service
        .command(InputCommand::Reset { generation: 41 })
        .unwrap();
    assert_eq!(reset.generation, 41);
    assert_eq!(reset.cutoff, 0);

    let mut observed_packets = 0;
    let mut previous_source_sequence = 0;
    let mut previous_device_sequence = 0;
    let catalog = DeviceCatalog::new(&directory);
    if let Some(device) = catalog.choices().first() {
        let started = service
            .command(InputCommand::Start {
                generation: 41,
                device_keys: vec![device.key],
                start_fresh: true,
            })
            .unwrap();
        assert_eq!(started.generation, 41);
        assert_eq!(started.cutoff, 0);
        let paused = service.command(InputCommand::Pause).unwrap();
        assert_eq!(shared.overflow.load(Ordering::Acquire), 0);
        assert!(!shared.disconnected.load(Ordering::Acquire));
        assert!(!shared.active.load(Ordering::Acquire));
        let resumed = service.command(InputCommand::Resume).unwrap();
        assert_eq!(resumed.generation, 41);
        assert_eq!(resumed.cutoff, paused.cutoff);
        let stopped = service.command(InputCommand::Stop).unwrap();
        assert_eq!(shared.overflow.load(Ordering::Acquire), 0);
        assert!(!shared.disconnected.load(Ordering::Acquire));
        assert_eq!(stopped.frequency, initial.frequency);
        assert!(!shared.active.load(Ordering::Acquire));
        while let Ok(captured) = consumer.pop() {
            observed_packets += 1;
            assert_eq!(captured.generation, 41);
            assert_eq!(captured.device_key, device.key);
            assert_eq!(captured.session_sequence, observed_packets);
            assert!(captured.record.capture_sequence > previous_source_sequence);
            assert!(captured.record.timing_sequence > previous_device_sequence);
            previous_source_sequence = captured.record.capture_sequence;
            previous_device_sequence = captured.record.timing_sequence;
            assert!(captured.record.raw_capture_ticks >= started.ticks);
            assert!(captured.record.raw_capture_ticks <= stopped.ticks);
        }
        assert_eq!(observed_packets, stopped.cutoff);
        assert_eq!(shared.overflow.load(Ordering::Acquire), 0);
    }
    let reset = service
        .command(InputCommand::Reset { generation: 42 })
        .unwrap();
    assert_eq!(reset.generation, 42);
    assert_eq!(reset.cutoff, 0);
    assert_eq!(shared.overflow.load(Ordering::Acquire), 0);
    assert!(!shared.disconnected.load(Ordering::Acquire));
    service.command(InputCommand::Shutdown).unwrap();
    let before_drop = Instant::now();
    drop(service);
    assert!(before_drop.elapsed() < Duration::from_secs(6));
    assert!(!shared.active.load(Ordering::Acquire));
    let (producer, _consumer) = rtrb::RingBuffer::new(CAPTURE_QUEUE_RECORDS);
    let restarted = InputService::spawn(
        producer,
        Arc::new(CaptureShared::default()),
        Arc::new(Mutex::new(Vec::new())),
    )
    .expect("Drop released the input thread and process-wide registration");
    drop(restarted);
    eprintln!(
        "Raw Input initialization/enumeration smoke: {} mouse devices; \
         {observed_packets} actual packets observed; high-report-rate hardware not verified",
        directory.len()
    );
}
