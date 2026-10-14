use clicksync_app::{
    backend::{Backend, CapturePhase},
    product_files::{FileRequest, FileResult, execute, imported_prefix},
};
use clicksync_core::{
    RawMouseMovementMode, RawMouseRecord, Session, SessionConfig, SessionSnapshot,
    SharedByteBudget,
    protocol::{CaptureSource, ControlCommand},
    session::{CaptureEvidence, CaptureIntegrity},
};
use std::{
    path::{Path, PathBuf},
    sync::Arc,
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

const TIMEOUT: Duration = Duration::from_secs(8);

fn directory(name: &str) -> PathBuf {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../artifacts/CaptureIntegrity")
        .join(format!(
            "{name}-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
    std::fs::create_dir_all(&path).unwrap();
    std::fs::write(
        path.join("report.json"),
        r#"{"passed":false,"status":"started"}"#,
    )
    .unwrap();
    path
}

fn import(path: &Path, existing: Vec<(Arc<SessionSnapshot>, Option<f64>)>) -> Arc<SessionSnapshot> {
    let result = execute(FileRequest::ImportAnalysis {
        path: path.to_owned(),
        operation: 1,
        budget: SharedByteBudget::new(SessionConfig::default().managed_memory_budget_bytes),
        existing,
    })
    .unwrap();
    let FileResult::ImportedAnalysis { group, .. } = result else {
        panic!("capture evidence must remain part of import identity");
    };
    group.snapshot
}

fn export(path: &Path, snapshot: Arc<SessionSnapshot>) {
    assert!(matches!(
        execute(FileRequest::Export {
            path: path.to_owned(),
            snapshot,
            cpi: Some(800.0),
        })
        .unwrap(),
        FileResult::Saved(_)
    ));
}

fn wait(backend: &Backend, condition: impl Fn(&clicksync_app::backend::AppStatus) -> bool) {
    let deadline = Instant::now() + TIMEOUT;
    loop {
        let status = backend.state();
        assert!(status.error.is_none(), "{status:?}");
        if condition(&status) {
            break;
        }
        assert!(Instant::now() < deadline, "{status:?}");
        thread::sleep(Duration::from_millis(2));
    }
}

#[test]
fn capture_owner_disk_export_import_preserves_both_devices_and_fixed_evidence() {
    let directory = directory("dual-roundtrip");
    let mut backend = Backend::spawn_in_process(true).unwrap();
    backend
        .execute(
            ControlCommand::Start {
                generation: 1,
                device_keys: vec![1, 2],
                source: CaptureSource::Replay {
                    records_per_device: 800,
                    reports_per_second: 8000,
                    real_time: true,
                },
                start_fresh: true,
            },
            TIMEOUT,
        )
        .unwrap();
    wait(&backend, |status| status.received_records == 1600);
    let ack = backend.execute(ControlCommand::Stop, TIMEOUT).unwrap();
    wait(&backend, |status| status.phase == CapturePhase::Stopped);
    let prefix = backend.capture_prefix(TIMEOUT).unwrap();
    assert!(ack.complete && prefix.complete);
    assert_eq!(
        (ack.cutoff, ack.queued, prefix.received_sequence),
        (1600, 1600, 1600)
    );
    assert_eq!(prefix.datasets.len(), 2);
    backend
        .execute(ControlCommand::Reset { generation: 2 }, TIMEOUT)
        .unwrap();
    for (side, (key, snapshot)) in prefix.datasets.iter().enumerate() {
        assert_eq!(snapshot.event_count(), 800);
        for (index, event) in snapshot.events().enumerate() {
            assert_eq!(event.capture_sequence, (index * 2 + side + 1) as u64);
            assert_eq!(event.timing_sequence, index as u64 + 1);
            assert_eq!(event.raw_capture_ticks, 1_000_000 + index as u64 * 1250);
            assert_eq!(event.raw_delta_x, (index % 7) as i32 - 3);
            assert_eq!(event.raw_delta_y, (index % 5) as i32 - 2);
            assert_eq!(
                (
                    event.button_flags,
                    event.raw_mouse_flags,
                    event.button_data,
                    event.extra_information
                ),
                (0, 0, 0, 0)
            );
        }
        let evidence = snapshot.capture_evidence();
        assert_eq!(evidence.integrity, CaptureIntegrity::Complete);
        assert_eq!(evidence.generation, Some(1));
        assert_eq!(evidence.received_sequence, Some(1600));
        assert_eq!((evidence.queued, evidence.cutoff), (Some(1600), Some(1600)));
        let path = directory.join(format!("device-{key}.json"));
        export(&path, snapshot.clone());
        let roundtrip = import(&path, Vec::new());
        assert_eq!(
            roundtrip.timestamp_frequency(),
            snapshot.timestamp_frequency()
        );
        assert_eq!(roundtrip.capture_evidence(), evidence);
        assert_eq!(roundtrip.event_count(), snapshot.event_count());
        assert!(roundtrip.events().eq(snapshot.events()));
        assert!(imported_prefix(roundtrip, *key, 10).complete);
    }
    backend.shutdown().unwrap();
    std::fs::write(
        directory.join("report.json"),
        r#"{"passed":true,"source":"in_process_paced_replay","records":1600,"same_frequency":true,"all_session_event_fields_equal":true}"#,
    )
    .unwrap();
}

#[test]
fn retention_export_preserves_nonzero_raw_fields_and_does_not_deduplicate_as_complete() {
    let directory = directory("retention");
    let mut session = Session::new(
        SessionConfig {
            max_session_events: 2,
            warning_event_threshold: 1,
            ..SessionConfig::default()
        },
        SharedByteBudget::new(SessionConfig::default().managed_memory_budget_bytes),
        Default::default(),
    )
    .unwrap();
    session.begin_collecting(100, true, false).unwrap();
    for index in 0..2 {
        let mut record = RawMouseRecord::new(
            i32::MIN + index,
            i32::MAX - index,
            100,
            index as u64 + 1,
            index as u64 + 1,
            RawMouseMovementMode::Absolute,
        );
        record.raw_mouse_flags = 0xb;
        record.button_flags = 0x411;
        record.button_data = 0xff88;
        record.extra_information = 0xdead_beef;
        session.append(record).unwrap();
    }
    let complete = Arc::new(session.snapshot().unwrap());
    session
        .append(RawMouseRecord::new(
            1,
            2,
            101,
            3,
            3,
            RawMouseMovementMode::Relative,
        ))
        .unwrap();
    session.stop();
    let truncated = Arc::new(session.snapshot().unwrap());
    assert_eq!(truncated.integrity(), CaptureIntegrity::Incomplete);
    assert_eq!(complete.integrity(), CaptureIntegrity::Complete);
    let path = directory.join("truncated.json");
    export(&path, truncated.clone());
    let roundtrip = import(&path, vec![(complete, Some(800.0))]);
    assert_eq!(roundtrip.capture_evidence(), truncated.capture_evidence());
    assert_eq!(roundtrip.capture_evidence().ignored_after_limit, 1);
    assert!(roundtrip.events().eq(truncated.events()));
    assert!(!imported_prefix(roundtrip, 1, 10).complete);
    std::fs::write(directory.join("report.json"), r#"{"passed":true}"#).unwrap();
}

#[test]
fn legacy_file_remains_unknown_through_product_import_export_and_deduplication() {
    let directory = directory("legacy");
    let path = directory.join("legacy.json");
    std::fs::write(
        &path,
        include_bytes!("../../clicksync-core/tests/fixtures/exchange-input-v8.json"),
    )
    .unwrap();
    let legacy = import(&path, Vec::new());
    assert_eq!(legacy.integrity(), CaptureIntegrity::Unknown);
    assert!(!imported_prefix(legacy.clone(), 1, 10).complete);
    let reexport = directory.join("reexport.json");
    export(&reexport, legacy.clone());
    let roundtrip = import(&reexport, Vec::new());
    assert_eq!(roundtrip.integrity(), CaptureIntegrity::Unknown);
    assert!(roundtrip.events().eq(legacy.events()));
    let mut known: serde_json::Value =
        serde_json::from_slice(&std::fs::read(&reexport).unwrap()).unwrap();
    known["s"]["e"]["integrity"] = serde_json::json!("Complete");
    let known_path = directory.join("known.json");
    std::fs::write(&known_path, serde_json::to_vec(&known).unwrap()).unwrap();
    let complete = import(&known_path, Vec::new());
    assert_eq!(complete.integrity(), CaptureIntegrity::Complete);
    let again = import(&path, vec![(complete, Some(800.0))]);
    assert_eq!(again.integrity(), CaptureIntegrity::Unknown);
    std::fs::write(directory.join("report.json"), r#"{"passed":true}"#).unwrap();
}

#[test]
fn identical_raw_prefixes_with_distinct_fault_evidence_are_not_duplicates() {
    let directory = directory("fault-identity");
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    session.begin_collecting(100, true, false).unwrap();
    for index in 0..3 {
        session
            .append(RawMouseRecord::new(
                1,
                -2,
                100 + index,
                index + 1,
                index + 1,
                RawMouseMovementMode::Relative,
            ))
            .unwrap();
    }
    session.stop();
    let complete = Arc::new(session.snapshot().unwrap());
    let mut evidence = CaptureEvidence {
        integrity: CaptureIntegrity::Incomplete,
        overflow: 1,
        generation: Some(1),
        received_sequence: Some(3),
        queued: Some(3),
        cutoff: Some(4),
        ..CaptureEvidence::default()
    };
    let mut snapshots = vec![complete.clone()];
    for fault in 0..3 {
        if fault == 1 {
            evidence.overflow = 2;
            evidence.cutoff = Some(5);
        } else if fault == 2 {
            evidence.disconnected = true;
        }
        session.set_capture_evidence(evidence);
        snapshots.push(Arc::new(session.snapshot().unwrap()));
    }
    let mut existing = Vec::new();
    for (index, snapshot) in snapshots.iter().enumerate() {
        let path = directory.join(format!("evidence-{index}.json"));
        export(&path, snapshot.clone());
        let roundtrip = import(&path, existing.clone());
        assert!(roundtrip.events().eq(complete.events()));
        assert_eq!(roundtrip.capture_evidence(), snapshot.capture_evidence());
        let prefix = imported_prefix(roundtrip.clone(), 1, 10);
        assert_eq!(prefix.generation, 10);
        assert_eq!(prefix.received_sequence, 3);
        assert_eq!(prefix.complete, index == 0);
        assert_eq!(snapshot.generation(), complete.generation());
        existing.push((roundtrip, Some(800.0)));
        assert!(matches!(
            execute(FileRequest::ImportAnalysis {
                path,
                operation: 42,
                budget: SharedByteBudget::new(SessionConfig::default().managed_memory_budget_bytes),
                existing: existing.clone(),
            })
            .unwrap(),
            FileResult::Duplicate { operation: 42 }
        ));
    }
    assert_eq!(complete.integrity(), CaptureIntegrity::Complete);
    std::fs::write(directory.join("report.json"), r#"{"passed":true}"#).unwrap();
}
