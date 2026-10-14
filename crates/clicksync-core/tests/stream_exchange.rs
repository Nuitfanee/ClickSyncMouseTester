use clicksync_core::exchange::{export_writer, import_reader};
use clicksync_core::session::{CaptureEvidence, CaptureIntegrity};
use clicksync_core::{RawMouseRecord, Session, SessionConfig, SessionMetadata, SharedByteBudget};
use serde_json::{Value, json};
use std::{io::Cursor, path::PathBuf};

fn snapshot() -> clicksync_core::SessionSnapshot {
    snapshot_with_evidence(CaptureEvidence::default())
}

fn snapshot_with_evidence(evidence: CaptureEvidence) -> clicksync_core::SessionSnapshot {
    let config = SessionConfig {
        chunk_capacity: 8,
        timestamp_frequency: 10_000_000,
        ..Default::default()
    };
    let mut session = Session::new(
        config,
        SharedByteBudget::new(512 * 1024 * 1024),
        SessionMetadata {
            display_name: "交换鼠标".into(),
            device_id: "fixture-device".into(),
            vendor_id: Some(1),
            product_id: Some(2),
            ..Default::default()
        },
    )
    .unwrap();
    session.begin_collecting(100_000_000, true, false).unwrap();
    for i in 0..40 {
        if i == 20 {
            session.pause();
            session.begin_collecting(200_000_000, false, false).unwrap();
        }
        let mut record = RawMouseRecord::new(
            if i % 3 == 0 { -5 } else { 7 },
            i % 4,
            if i < 20 {
                100_000_100 + i as u64 * 10_000
            } else {
                200_000_300 + (i - 20) as u64 * 10_000
            },
            i as u64 + 100,
            i as u64 / 2 + 100,
            clicksync_core::RawMouseMovementMode::Relative,
        );
        record.timing_sequence = i as u64 / 2 + 100;
        record.button_flags = if i % 7 == 0 { 0x400 } else { 0 };
        record.button_data = (-120_i16) as u16;
        record.extra_information = 0xffccaa;
        session.append(record).unwrap();
    }
    session.stop();
    session.set_capture_evidence(evidence);
    session.snapshot().unwrap()
}
fn integers(e: clicksync_core::SessionEvent) -> [i64; 18] {
    [
        e.raw_delta_x as i64,
        e.raw_delta_y as i64,
        e.delta_x as i64,
        e.delta_y as i64,
        e.button_flags as i64,
        e.raw_mouse_flags as i64,
        e.button_data as i64,
        e.extra_information as i64,
        e.raw_capture_ticks as i64,
        e.raw_relative_ticks as i64,
        e.logical_ticks as i64,
        e.packet_kind as i64,
        e.movement_mode as i64,
        e.session_cumulative_x,
        e.session_cumulative_y,
        e.session_segment_id as i64,
        e.capture_sequence as i64,
        e.timing_sequence as i64,
    ]
}
#[test]
fn streaming_export_import_and_reference_roundtrip() {
    let snapshot = snapshot();
    let mut bytes = Vec::new();
    export_writer(&mut bytes, &snapshot, Some(800.0), "2026-10-03T00:00:00Z").unwrap();
    let doc: Value = serde_json::from_slice(&bytes).unwrap();
    let reference_input: Value =
        serde_json::from_str(include_str!("fixtures/exchange-input-v8.json")).unwrap();
    assert_eq!(doc, reference_input, "reference export input changed");
    assert_eq!(doc["v"], 8);
    assert_eq!(doc["g"].as_array().unwrap().len(), 2);
    let config = SessionConfig {
        timestamp_frequency: snapshot.timestamp_frequency(),
        chunk_capacity: 8,
        ..Default::default()
    };
    let imported = import_reader(
        Cursor::new(&bytes),
        config.clone(),
        SharedByteBudget::new(512 * 1024 * 1024),
    )
    .unwrap();
    assert_eq!(imported.effective_cpi, Some(800.0));
    assert_eq!(imported.snapshot.integrity(), CaptureIntegrity::Unknown);
    assert_eq!(
        snapshot.events().map(integers).collect::<Vec<_>>(),
        imported.snapshot.events().map(integers).collect::<Vec<_>>()
    );
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let directory = std::env::var_os("CLICKSYNC_PRODUCT_EXCHANGE_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            root.join("../..").join(format!(
                "artifacts/ProductExchange/runs/{}-{}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_millis()
            ))
        });
    std::fs::create_dir_all(&directory).unwrap();
    let native = directory.join("native-v8.json");
    std::fs::write(&native, &bytes).unwrap();
    let reference_report: Value =
        serde_json::from_str(include_str!("fixtures/exchange-expected.json")).unwrap();
    assert_eq!(
        reference_report["frequency"],
        snapshot.timestamp_frequency()
    );
    assert_eq!(
        reference_report["records"],
        json!(snapshot.events().map(integers).collect::<Vec<_>>())
    );
    let roundtrip = import_reader(
        Cursor::new(include_bytes!("fixtures/exchange-reference-v8.json")),
        config,
        SharedByteBudget::new(512 * 1024 * 1024),
    )
    .unwrap();
    assert_eq!(roundtrip.snapshot.integrity(), CaptureIntegrity::Unknown);
    assert_eq!(
        snapshot.events().map(integers).collect::<Vec<_>>(),
        roundtrip
            .snapshot
            .events()
            .map(integers)
            .collect::<Vec<_>>()
    );
    std::fs::write(directory.join("report.json"),serde_json::to_vec(&json!({"passed":true,"records":snapshot.event_count(),
        "exact_integer_fields":integers(snapshot.event_at(0).unwrap()).len(),
        "comparison":"streaming export and frozen independent reference import; exact integer fields"})).unwrap()).unwrap();
}

#[test]
fn capture_evidence_roundtrips_without_changing_any_integer_field_or_frequency() {
    for evidence in [
        CaptureEvidence::complete(),
        CaptureEvidence {
            integrity: CaptureIntegrity::Incomplete,
            ignored_after_limit: 3,
            overflow: 1,
            disconnected: true,
            generation: Some(7),
            received_sequence: Some(43),
            queued: Some(43),
            cutoff: Some(44),
        },
        CaptureEvidence::default(),
    ] {
        let snapshot = snapshot_with_evidence(evidence);
        let expected: Vec<_> = snapshot.events().map(integers).collect();
        let mut bytes = Vec::new();
        export_writer(&mut bytes, &snapshot, Some(800.0), "2026-10-11T00:00:00Z").unwrap();
        let document: Value = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(
            document["s"].get("e").cloned(),
            (evidence != CaptureEvidence::default()).then(|| json!(evidence))
        );
        let config = SessionConfig {
            timestamp_frequency: snapshot.timestamp_frequency(),
            ..Default::default()
        };
        let imported = import_reader(
            Cursor::new(&bytes),
            config.clone(),
            SharedByteBudget::new(512 * 1024 * 1024),
        )
        .unwrap();
        let document =
            clicksync_core::ExchangeDocument::from_json(std::str::from_utf8(&bytes).unwrap())
                .unwrap();
        let replayed = document
            .replay(config, SharedByteBudget::new(512 * 1024 * 1024))
            .unwrap();
        for roundtrip in [&imported.snapshot, &replayed] {
            assert_eq!(roundtrip.capture_evidence(), evidence);
            assert_eq!(
                roundtrip.timestamp_frequency(),
                snapshot.timestamp_frequency()
            );
            assert_eq!(
                roundtrip.events().map(integers).collect::<Vec<_>>(),
                expected
            );
        }
    }
}

#[test]
fn legacy_evidence_is_unknown_and_cannot_be_upgraded_by_reexport() {
    let bytes = include_bytes!("fixtures/exchange-input-v8.json");
    let config = SessionConfig::default();
    let imported = import_reader(
        Cursor::new(bytes),
        config.clone(),
        SharedByteBudget::new(512 * 1024 * 1024),
    )
    .unwrap();
    let legacy = clicksync_core::ExchangeDocument::from_json(std::str::from_utf8(bytes).unwrap())
        .unwrap()
        .replay(config.clone(), SharedByteBudget::new(512 * 1024 * 1024))
        .unwrap();
    assert_eq!(imported.snapshot.integrity(), CaptureIntegrity::Unknown);
    assert_eq!(legacy.integrity(), CaptureIntegrity::Unknown);
    let mut output = Vec::new();
    export_writer(&mut output, &imported.snapshot, None, "").unwrap();
    let roundtrip = import_reader(
        Cursor::new(output),
        config,
        SharedByteBudget::new(512 * 1024 * 1024),
    )
    .unwrap();
    assert_eq!(roundtrip.snapshot.integrity(), CaptureIntegrity::Unknown);
    assert!(roundtrip.snapshot.events().eq(imported.snapshot.events()));
}

#[test]
fn contradictory_or_duplicate_capture_evidence_is_rejected() {
    let reference: Value =
        serde_json::from_str(include_str!("fixtures/exchange-input-v8.json")).unwrap();
    for evidence in [
        json!({"integrity":"Complete","ignored_after_limit":1}),
        json!({"integrity":"Unknown","overflow":1}),
        json!({"integrity":"Complete","queued":4,"cutoff":5}),
        json!({"integrity":"Incomplete","queued":6,"cutoff":5}),
        json!({"integrity":"Incomplete","received_sequence":6,"queued":5,"cutoff":6}),
        json!({"integrity":"Complete","generation":0}),
        json!({"integrity":"Complete","queued":5}),
        json!({"integrity":"invented"}),
    ] {
        let mut document = reference.clone();
        document["s"]["e"] = evidence;
        let bytes = serde_json::to_vec(&document).unwrap();
        assert!(
            import_reader(
                Cursor::new(&bytes),
                SessionConfig::default(),
                SharedByteBudget::new(512 * 1024 * 1024),
            )
            .is_err()
        );
        assert!(
            clicksync_core::ExchangeDocument::from_json(std::str::from_utf8(&bytes).unwrap())
                .and_then(|document| document.validate())
                .is_err()
        );
    }
    let duplicate = serde_json::to_string(&reference).unwrap().replacen(
        "\"s\":{",
        "\"s\":{\"e\":{\"integrity\":\"Complete\"},\"CaptureEvidence\":{\"integrity\":\"Unknown\"},",
        1,
    );
    assert!(
        import_reader(
            Cursor::new(duplicate.as_bytes()),
            SessionConfig::default(),
            SharedByteBudget::new(512 * 1024 * 1024),
        )
        .is_err()
    );
}

#[test]
fn capture_watermark_must_cover_stored_records_and_retention_drops() {
    for evidence in [
        CaptureEvidence {
            received_sequence: Some(39),
            ..CaptureEvidence::complete()
        },
        CaptureEvidence {
            integrity: CaptureIntegrity::Incomplete,
            received_sequence: Some(40),
            ignored_after_limit: 1,
            ..CaptureEvidence::default()
        },
    ] {
        let snapshot = snapshot_with_evidence(evidence);
        assert!(export_writer(Vec::new(), &snapshot, None, "").is_err());
        let mut document: Value =
            serde_json::from_str(include_str!("fixtures/exchange-input-v8.json")).unwrap();
        document["s"]["e"] = json!(evidence);
        let bytes = serde_json::to_vec(&document).unwrap();
        assert!(
            import_reader(
                Cursor::new(&bytes),
                SessionConfig::default(),
                SharedByteBudget::new(512 * 1024 * 1024),
            )
            .is_err()
        );
        assert!(
            clicksync_core::ExchangeDocument::from_json(std::str::from_utf8(&bytes).unwrap())
                .unwrap()
                .replay(
                    SessionConfig::default(),
                    SharedByteBudget::new(512 * 1024 * 1024),
                )
                .is_err()
        );
    }
}
#[test]
fn streaming_old_lengths_aliases_unordered_fields_and_limits() {
    for (version, length) in [(4, 5), (5, 8), (6, 9), (7, 10), (8, 11)] {
        let mut row = vec![1_i64, 2, 0];
        if length >= 8 {
            row.extend([0, 0, 42]);
        }
        row.extend([10, 0]);
        if length >= 9 {
            row.push(1);
        }
        if length >= 10 {
            row.push(1);
        }
        if length >= 11 {
            row.push(100);
        }
        let doc = json!({"g":[{"r":[row],"q":100,"t":500,"i":3}],"s":{"f":10_000_000,"c":800},"m":{"n":"mouse","b":5,"x":false},"v":version});
        let imported = import_reader(
            Cursor::new(serde_json::to_vec(&doc).unwrap()),
            SessionConfig::default(),
            SharedByteBudget::new(512 * 1024 * 1024),
        )
        .unwrap();
        assert_eq!(imported.snapshot.event_count(), 1);
        assert_eq!(imported.snapshot.event_at(0).unwrap().capture_sequence, 100);
    }
    let doc = json!({"g":[{"r":[[1,2,0,1,0]],"q":100,"t":500,"i":1}],"s":{"f":10_000_000},"m":{"b":5,"x":false},"v":8});
    let config = SessionConfig {
        max_session_events: 1,
        ..Default::default()
    };
    assert!(
        import_reader(
            Cursor::new(serde_json::to_vec(&doc).unwrap()),
            config,
            SharedByteBudget::new(1)
        )
        .is_err()
    );
    let mut invalid = doc;
    invalid["g"][0]["r"][0] = json!([1, 2, 3, 4, 5, 6]);
    assert!(
        import_reader(
            Cursor::new(serde_json::to_vec(&invalid).unwrap()),
            SessionConfig::default(),
            SharedByteBudget::new(512 * 1024 * 1024)
        )
        .is_err()
    );
}

#[test]
fn mixed_case_null_rows_and_sorted_segments_preserve_legacy_import() {
    let document = json!({
        "FoRmAtVeRsIoN":8, "MoUsE":{"DiSpLaYnAmE":"大小写", "BuTtOnCoUnT":5},
        "SeSsIoN":{"HoStTiMeStAmPfReQuEnCy":3, "EfFeCtIvEcPi":1600},
        "SeGmEnTs":[
            {"SeGmEnTiD":2, "StArTeDaTrAwCaPtUrEtIcKs":30, "BaSeCaPtUrEsEqUeNcE":200,
             "PaCkEtRoWs":[null,[3,4,0,1,0]]},
            null,
            {"SeGmEnTiD":1, "StArTeDaTrAwCaPtUrEtIcKs":3, "BaSeCaPtUrEsEqUeNcE":100,
             "PaCkEtRoWs":[[1,2,0,1,0],null,[1,2,0,1,1]]}
        ]
    });
    let imported = import_reader(
        Cursor::new(serde_json::to_vec_pretty(&document).unwrap()),
        SessionConfig {
            timestamp_frequency: 10,
            ..Default::default()
        },
        SharedByteBudget::new(512 * 1024 * 1024),
    )
    .unwrap();
    assert_eq!(imported.effective_cpi, Some(1600.0));
    assert_eq!(imported.snapshot.metadata().display_name, "大小写");
    let events: Vec<_> = imported.snapshot.events().collect();
    assert_eq!(
        events
            .iter()
            .map(|e| e.raw_capture_ticks)
            .collect::<Vec<_>>(),
        [13, 16, 103]
    );
    assert_eq!(
        events
            .iter()
            .map(|e| e.capture_sequence)
            .collect::<Vec<_>>(),
        [100, 101, 200]
    );
}

#[test]
fn segment_lookup_and_export_keep_empty_segment_anchors_and_old_snapshot_identity() {
    let mut session = Session::default_with_metadata(Default::default()).unwrap();
    for id in 1..=257_u32 {
        let ticks = id as u64 * 1000;
        session.begin_collecting(ticks, false, false).unwrap();
        if id % 3 == 0 {
            session
                .append(RawMouseRecord::new(
                    id as i32,
                    0,
                    ticks + 1,
                    id as u64,
                    id as u64,
                    clicksync_core::RawMouseMovementMode::Relative,
                ))
                .unwrap();
        }
        session.pause();
    }
    session.stop();
    let snapshot = session.snapshot().unwrap();
    session.reset().unwrap();
    assert!(session.snapshot().unwrap().segment(3).is_none());
    assert!(snapshot.segment(0).is_none());
    assert!(snapshot.segment(258).is_none());
    for segment in snapshot.segments() {
        assert_eq!(snapshot.segment(segment.segment_id), Some(segment));
    }
    let mut bytes = Vec::new();
    export_writer(&mut bytes, &snapshot, None, "2026-10-03").unwrap();
    let json: Value = serde_json::from_slice(&bytes).unwrap();
    let segments = json["g"].as_array().unwrap();
    assert_eq!(segments.len(), 257 / 3);
    for (index, segment) in segments.iter().enumerate() {
        assert_eq!(segment["t"], ((index + 1) * 3 * 1000) as u64);
        assert_eq!(segment["r"][0][6], 1);
    }
}

#[test]
fn streaming_exchange_preserves_signed_maximum_and_rejects_source_overflow() {
    let config = SessionConfig::default();
    let mut session = Session::new(
        config.clone(),
        SharedByteBudget::new(config.managed_memory_budget_bytes),
        SessionMetadata::default(),
    )
    .unwrap();
    session.begin_collecting(1, true, false).unwrap();
    let mut record = RawMouseRecord::new(
        1,
        2,
        2,
        1,
        1,
        clicksync_core::RawMouseMovementMode::Relative,
    );
    record.timing_sequence = i64::MAX as u64 + 1;
    assert_eq!(
        session.append(record),
        Err(clicksync_core::SessionError::ArithmeticOverflow)
    );
    record.timing_sequence = i64::MAX as u64;
    session.append(record).unwrap();
    session.stop();
    let mut bytes = Vec::new();
    export_writer(
        &mut bytes,
        &session.snapshot().unwrap(),
        Some(800.0),
        "2026-10-03",
    )
    .unwrap();
    let document: Value = serde_json::from_slice(&bytes).unwrap();
    assert_eq!(document["g"][0]["r"][0][10], json!(i64::MAX));
    let imported = import_reader(
        Cursor::new(bytes),
        config.clone(),
        SharedByteBudget::new(config.managed_memory_budget_bytes),
    )
    .unwrap();
    assert_eq!(
        imported.snapshot.event_at(0).unwrap().timing_sequence,
        i64::MAX as u64
    );
}
