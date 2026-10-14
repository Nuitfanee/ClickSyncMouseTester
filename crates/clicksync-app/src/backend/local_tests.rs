use super::*;
use clicksync_capture::runtime::CaptureRuntime;
use clicksync_core::protocol::CaptureSource;

const WAIT: Duration = Duration::from_secs(12);

fn wait_until(mut ready: impl FnMut() -> bool) {
    let deadline = Instant::now() + WAIT;
    while !ready() {
        assert!(Instant::now() < deadline, "local loop regression deadline");
        thread::yield_now();
    }
}

fn inspect<T: Send + 'static>(
    backend: &Backend,
    read: impl FnOnce(&mut SessionOwner, &mut Vec<CapturedRecord>) -> T + Send + 'static,
) -> T {
    let (reply, receiver) = mpsc::sync_channel(1);
    assert!(
        backend
            .ingestion
            .send(IngestMessage::Inspect(Box::new(move |owner, batch| {
                let _ = reply.send(read(owner, batch));
            })))
            .is_ok()
    );
    backend.wake_owner();
    receiver.recv_timeout(WAIT).unwrap()
}

fn start(generation: u64) -> ControlCommand {
    ControlCommand::Start {
        generation,
        device_keys: vec![1],
        source: CaptureSource::Replay {
            records_per_device: 6,
            reports_per_second: 8000,
            real_time: false,
        },
        start_fresh: true,
    }
}

fn motion(backend: &Backend, generation: u64) -> (u64, mpsc::Receiver<Result<CaptureAck, String>>) {
    let shared = backend.local_shared.as_ref().unwrap();
    let id = shared.motion.reserve().unwrap();
    let (reply, receiver) = mpsc::sync_channel(1);
    assert!(
        backend
            .requests
            .send(DriverRequest::Motion {
                request: clicksync_capture::observation::MotionRequest {
                    id,
                    excluded: None,
                    capture_generation: Some(generation),
                    timeout: None,
                },
                permit: shared.permit.ticket(),
                reply,
            })
            .is_ok()
    );
    (id, receiver)
}

struct Fixture {
    backend: Backend,
    blocker: clicksync_core::BudgetReservation,
    retired_reader: Option<Arc<SessionSnapshot>>,
    old: Arc<SessionSnapshot>,
    _old_prefix: Arc<SessionPrefix>,
    expected: SessionSnapshot,
}

// The real producer is sealed before the owner starts. Its exact records are split
// at a chosen commit boundary, so neither scheduling nor sleeps inject the fault.
fn blocked_loop(preload: usize, retired: bool) -> Fixture {
    let (mut runtime, mut reader) = CaptureRuntime::spawn(true).unwrap();
    let shared = runtime.shared();
    let devices = runtime
        .command(ControlCommand::Discover, 0)
        .unwrap()
        .devices;
    let ack = runtime.command(start(7), shared.permit.ticket()).unwrap();
    assert!(ack.error.is_none());
    wait_until(|| shared.queued.load(Ordering::Acquire) == 6);
    let stop = runtime.command(ControlCommand::Stop, 0).unwrap();
    assert_eq!(stop.queued, 6);
    let mut records = Vec::new();
    reader.drain(&mut records);
    assert_eq!(records.len(), 6);
    let config = SessionConfig {
        max_session_events: 128,
        warning_event_threshold: 64,
        chunk_capacity: 4,
        timestamp_frequency: ack.timestamp_frequency,
        ..Default::default()
    };
    let mut reference = Session::new(
        config.clone(),
        SharedByteBudget::new(config.managed_memory_budget_bytes),
        Default::default(),
    )
    .unwrap();
    reference.begin_collecting(ack.ticks, true, false).unwrap();
    for record in &records {
        reference.append(record.record).unwrap();
    }
    reference.stop();
    let expected = reference.snapshot().unwrap();
    let state = Arc::new(Mutex::new(AppStatus {
        generation: 7,
        phase: CapturePhase::Collecting,
        complete: true,
        selected_device_keys: vec![1],
        devices,
        ..Default::default()
    }));
    let budget = SharedByteBudget::new(config.managed_memory_budget_bytes);
    let first = Arc::new(Mutex::new(None));
    let all = Arc::new(Mutex::new(None));
    let cancelled = Arc::new(AtomicBool::new(false));
    let delay = Arc::new(AtomicU64::new(0));
    let inputs = Arc::new(Mutex::new(InputPublication::default()));
    let (requests, request_receiver) = mpsc::sync_channel(COMMAND_QUEUE_CAPACITY);
    let (ingestion, controls) = mpsc::sync_channel(COMMAND_QUEUE_CAPACITY);
    let (ready, seeded) = mpsc::sync_channel(1);
    let seed_state = state.clone();
    let seed_first = first.clone();
    let seed_all = all.clone();
    assert!(
        ingestion
            .send(IngestMessage::Inspect(Box::new(move |owner, batch| {
                owner.generation = 7;
                let mut session =
                    Session::new(config.clone(), owner.budget.clone(), Default::default()).unwrap();
                session.begin_collecting(ack.ticks, true, false).unwrap();
                owner.sessions.push((1, session));
                owner
                    .append_records(
                        7,
                        &records[..preload],
                        &seed_state,
                        &seed_first,
                        &AtomicU64::new(0),
                    )
                    .unwrap();
                batch.extend_from_slice(&records[preload..]);
                let old = owner.publish_snapshot(&seed_first).unwrap();
                let old_prefix = owner.fixed_prefix(&seed_state).unwrap();
                *seed_all.lock().unwrap() = Some(old_prefix.clone());
                let retired_reader = retired.then(|| {
                    let mut large = Session::new(
                        SessionConfig {
                            chunk_capacity: 64,
                            ..config
                        },
                        owner.budget.clone(),
                        Default::default(),
                    )
                    .unwrap();
                    large.begin_collecting(1_000_000, true, false).unwrap();
                    for index in 0..64 {
                        large
                            .append(clicksync_core::protocol::replay_record(index, 7, 1).record)
                            .unwrap();
                    }
                    let snapshot = Arc::new(large.snapshot().unwrap());
                    owner.retired_snapshots.push(snapshot.clone());
                    snapshot
                });
                let blocker = owner
                    .budget
                    .try_reserve(owner.budget.available_bytes())
                    .unwrap();
                let _ = ready.send((blocker, retired_reader, old, old_prefix));
            })))
            .is_ok()
    );
    let publication = SnapshotPublication {
        budget: budget.clone(),
        first: first.clone(),
        all: all.clone(),
    };
    let owner_requests = requests.clone();
    let owner_state = state.clone();
    let owner_cancelled = cancelled.clone();
    let owner_delay = delay.clone();
    let owner_inputs = inputs.clone();
    let owner_thread = thread::spawn(move || {
        local::ingest_local(
            reader,
            controls,
            owner_requests,
            owner_state,
            publication,
            owner_cancelled,
            owner_delay,
            owner_inputs,
        )
    });
    let driver_ingestion = ingestion.clone();
    let driver_state = state.clone();
    let driver_cancelled = cancelled.clone();
    let driver_thread = thread::spawn(move || {
        drive_controls(
            ControlTransport::Local(Box::new(runtime)),
            request_receiver,
            driver_ingestion,
            driver_state,
            driver_cancelled,
        )
    });
    let backend = Backend {
        byte_budget: budget,
        requests,
        ingestion,
        state,
        latest_snapshot: first,
        latest_prefix: all,
        cancelled,
        ingestion_delay_ms: delay,
        data: None,
        control: None,
        local_shared: Some(shared),
        child: None,
        threads: vec![owner_thread, driver_thread],
        inputs,
    };
    let (blocker, retired_reader, old, old_prefix) = seeded.recv_timeout(WAIT).unwrap();
    Fixture {
        backend,
        blocker,
        retired_reader,
        old,
        _old_prefix: old_prefix,
        expected,
    }
}

fn assert_sealed(backend: &Backend, expected: &SessionSnapshot) {
    wait_until(|| {
        inspect(backend, |owner, _| {
            owner.received_sequence == 6
                && owner.seal.is_none()
                && owner.sessions[0].1.status() == clicksync_core::SessionStatus::Stopped
        })
    });
    let prefix = backend.capture_prefix(WAIT).unwrap();
    assert_eq!(prefix.received_sequence, 6);
    assert!(!prefix.complete);
    assert!(prefix.datasets[0].1.events().eq(expected.events()));
    assert_eq!(
        prefix.datasets[0].1.capture_evidence().ignored_after_limit,
        0
    );
}

#[test]
fn local_loop_reclaims_retired_budget_publishes_commits_and_keeps_old_snapshot_fixed() {
    let mut fixture = blocked_loop(3, true);
    wait_until(|| {
        let state = fixture.backend.state();
        state.phase == CapturePhase::Faulted
            && state.received_sequence == 4
            && fixture.backend.latest_prefix().is_none()
    });
    let state = fixture.backend.state();
    assert_eq!(state.received_records, 4);
    assert_eq!(state.dataset_records, [4]);
    assert!(fixture.backend.latest_snapshot().is_none());
    assert_eq!(fixture.old.event_count(), 3);
    assert_eq!(fixture.old.integrity(), CaptureIntegrity::Complete);
    let limit = fixture.backend.byte_budget.limit_bytes();
    drop(fixture.retired_reader.take());
    // The blocker stays held: only the owner's retired-reader cleanup can free space.
    assert_sealed(&fixture.backend, &fixture.expected);
    assert_eq!(fixture.backend.byte_budget.limit_bytes(), limit);
    assert_eq!(fixture.old.integrity(), CaptureIntegrity::Complete);
    drop(fixture.blocker);
    fixture.backend.shutdown().unwrap();
}

#[test]
fn local_loop_prepare_timeouts_cancel_normal_and_motion_handshakes_without_losing_suffix() {
    let mut fixture = blocked_loop(3, false);
    wait_until(|| fixture.backend.state().phase == CapturePhase::Faulted);
    let original_error = fixture.backend.state().error;
    let error = fixture
        .backend
        .execute(ControlCommand::Resume, WAIT)
        .unwrap_err();
    assert!(error.contains("did not prepare"), "{error}");
    wait_until(|| !fixture.backend.state().command_pending);
    assert!(!inspect(&fixture.backend, |owner, _| owner.prepared));
    let (_, reply) = motion(&fixture.backend, 8);
    let error = reply.recv_timeout(WAIT).unwrap().unwrap_err();
    assert!(error.contains("did not prepare"), "{error}");
    wait_until(|| !fixture.backend.state().command_pending);
    assert!(!inspect(&fixture.backend, |owner, _| owner.prepared));
    assert_eq!(fixture.backend.state().error, original_error);
    drop(fixture.blocker);
    assert_sealed(&fixture.backend, &fixture.expected);
    assert!(!inspect(&fixture.backend, |owner, batch| owner.prepared
        || !batch.is_empty()));
    fixture
        .backend
        .execute(ControlCommand::Reset { generation: 8 }, WAIT)
        .unwrap();
    fixture.backend.execute(start(9), WAIT).unwrap();
    wait_until(|| fixture.backend.state().received_records == 6);
    fixture.backend.shutdown().unwrap();
}

#[test]
fn local_loop_retries_seal_without_new_records_and_shutdown_timeout_preserves_owner() {
    let mut fixture = blocked_loop(6, false);
    let ack = fixture.backend.execute(ControlCommand::Stop, WAIT).unwrap();
    assert_eq!(ack.queued, 6);
    wait_until(|| {
        inspect(&fixture.backend, |owner, _| {
            owner.seal.is_some()
                && owner.sessions[0].1.status() == clicksync_core::SessionStatus::Stopped
        })
    });
    let revision = inspect(&fixture.backend, |owner, _| {
        owner.sessions[0].1.session_revision()
    });
    assert!(
        fixture
            .backend
            .drain_local_shutdown_with_timeout(Duration::from_millis(100))
            .is_err()
    );
    assert!(!fixture.backend.cancelled.load(Ordering::Acquire));
    assert_eq!(fixture.backend.threads.len(), 2);
    fixture
        .backend
        .execute(ControlCommand::Discover, WAIT)
        .unwrap();
    assert_eq!(
        inspect(&fixture.backend, |owner, _| owner.sessions[0]
            .1
            .session_revision()),
        revision
    );
    drop(fixture.blocker);
    assert_sealed(&fixture.backend, &fixture.expected);
    fixture.backend.shutdown().unwrap();
}

#[test]
fn local_driver_discards_stale_fault_stop_using_source_generation_not_lagging_owner() {
    let mut backend = Backend::spawn_in_process(true).unwrap();
    backend
        .execute(ControlCommand::Reset { generation: 7 }, WAIT)
        .unwrap();
    let (entered, at_barrier) = mpsc::sync_channel(1);
    let (release, gate) = mpsc::sync_channel(1);
    assert!(
        backend
            .ingestion
            .send(IngestMessage::Inspect(Box::new(move |_, _| {
                entered.send(()).unwrap();
                gate.recv_timeout(WAIT).unwrap();
            })))
            .is_ok()
    );
    backend.wake_owner();
    at_barrier.recv_timeout(WAIT).unwrap();
    let reset = backend
        .enqueue_tracked(ControlCommand::Reset { generation: 8 })
        .unwrap();
    assert_eq!(reset.recv_timeout(WAIT).unwrap().unwrap().generation, 8);
    assert_eq!(backend.state().generation, 7);
    assert!(
        backend
            .requests
            .send(DriverRequest::FaultStop { generation: 7 })
            .is_ok()
    );
    // Discover is a driver FIFO barrier after the obsolete Stop.
    backend.execute(ControlCommand::Discover, WAIT).unwrap();
    release.send(()).unwrap();
    assert_eq!(inspect(&backend, |owner, _| owner.generation), 8);
    wait_until(|| backend.state().generation == 8);
    assert_eq!(backend.state().phase, CapturePhase::Empty);
    backend.execute(start(9), WAIT).unwrap();
    assert!(
        backend
            .requests
            .send(DriverRequest::FaultStop { generation: 8 })
            .is_ok()
    );
    backend.execute(ControlCommand::Discover, WAIT).unwrap();
    assert_eq!(
        inspect(&backend, |owner, _| owner.sessions[0].1.status()),
        clicksync_core::SessionStatus::Collecting
    );
    backend.execute(ControlCommand::Stop, WAIT).unwrap();
    wait_until(|| backend.state().phase == CapturePhase::Stopped);
    let (id, reply) = motion(&backend, 10);
    assert_eq!(reply.recv_timeout(WAIT).unwrap().unwrap().generation, 10);
    assert!(
        backend
            .requests
            .send(DriverRequest::FaultStop { generation: 9 })
            .is_ok()
    );
    backend.execute(ControlCommand::Discover, WAIT).unwrap();
    assert!(backend.local_shared.as_ref().unwrap().motion.is_current(id));
    backend.cancel_motion(id);
    backend.shutdown().unwrap();
}

#[test]
fn local_owner_cancellation_is_ordered_before_the_next_prepare_after_a_successful_reply() {
    let mut backend = Backend::spawn_in_process(true).unwrap();
    let (reply, receiver) = mpsc::sync_channel(1);
    assert!(
        backend
            .ingestion
            .send(IngestMessage::Prepare(reply))
            .is_ok()
    );
    backend.wake_owner();
    receiver.recv_timeout(WAIT).unwrap();
    assert!(inspect(&backend, |owner, _| owner.prepared));
    // Model cancellation immediately after owner reply, before driver activation.
    drop(receiver);
    assert!(
        backend
            .ingestion
            .send(IngestMessage::Rejected("cancelled prepare".into()))
            .is_ok()
    );
    assert!(!inspect(&backend, |owner, _| owner.prepared));
    backend.execute(start(1), WAIT).unwrap();
    wait_until(|| backend.state().received_records == 6);
    backend.shutdown().unwrap();
}

#[cfg(feature = "ui-restoration-probe")]
#[test]
fn local_loop_defers_motion_seals_until_initialization_without_losing_first_reports() {
    use clicksync_capture::observation::MotionReport;

    for block_generation in [true, false] {
        let mut backend = Backend::spawn_in_process(true).unwrap();
        let (blocker, readers) = inspect(&backend, move |owner, _| {
            if block_generation {
                let mut session = Session::new(
                    SessionConfig::default(),
                    owner.budget.clone(),
                    Default::default(),
                )
                .unwrap();
                let readers: Vec<_> = (0..MAX_RETIRED_SNAPSHOTS)
                    .map(|_| Arc::new(session.snapshot().unwrap()))
                    .collect();
                owner.retired_snapshots.extend(readers.iter().cloned());
                (None, readers)
            } else {
                (
                    Some(
                        owner
                            .budget
                            .try_reserve(owner.budget.available_bytes())
                            .unwrap(),
                    ),
                    Vec::new(),
                )
            }
        });
        let (id, reply) = motion(&backend, 1);
        let armed = reply.recv_timeout(WAIT).unwrap().unwrap();
        let mut records = [
            clicksync_core::RawMouseRecord::new(
                13,
                -7,
                200,
                1,
                1,
                clicksync_core::RawMouseMovementMode::Relative,
            ),
            clicksync_core::RawMouseRecord::new(
                -21,
                11,
                201,
                2,
                2,
                clicksync_core::RawMouseMovementMode::Relative,
            ),
        ];
        records[0].button_flags = 0x400;
        records[0].button_data = (-120i16) as u16;
        records[0].raw_mouse_flags = 2;
        records[0].extra_information = 0xfef1_0012;
        backend
            .replay_motion(
                records
                    .iter()
                    .map(|record| MotionReport {
                        device_key: 1,
                        record: *record,
                    })
                    .collect(),
            )
            .unwrap();
        wait_until(|| backend.state().phase == CapturePhase::Faulted);
        let original_error = backend.state().error;
        assert!(original_error.is_some());
        assert_eq!(
            backend
                .local_shared
                .as_ref()
                .unwrap()
                .queued
                .load(Ordering::Acquire),
            2
        );
        assert!(backend.take_motion(id).is_none());
        // Both the old-generation and same-generation initialization failures must
        // retain these real ACKs. A later Pause must not weaken Stop/Shutdown.
        let mut commands = vec![
            ControlCommand::Pause,
            ControlCommand::Stop,
            ControlCommand::Pause,
        ];
        if !block_generation {
            commands.push(ControlCommand::Shutdown);
        }
        for command in commands {
            let ack = backend.execute(command, WAIT).unwrap();
            assert_eq!((ack.generation, ack.queued, ack.cutoff), (1, 2, 2));
        }
        assert_eq!(inspect(&backend, |owner, _| owner.received_sequence), 0);
        assert!(inspect(&backend, |owner, _| owner.sessions.is_empty()));
        assert_eq!(
            inspect(&backend, |owner, _| owner.generation),
            u64::from(!block_generation)
        );
        assert!(
            backend
                .local_shared
                .as_ref()
                .unwrap()
                .motion
                .peek(id)
                .is_some()
        );

        // No sleep controls the interleaving: only releasing the injected resource
        // makes the real owner loop able to initialize, append, and seal.
        drop(blocker);
        drop(readers);
        backend.wake_owner();
        wait_until(|| {
            inspect(&backend, |owner, batch| {
                owner.received_sequence == 2
                    && batch.is_empty()
                    && owner.seal.is_none()
                    && owner.sessions.first().is_some_and(|(_, session)| {
                        session.status() == clicksync_core::SessionStatus::Stopped
                    })
            })
        });
        let prefix = backend.capture_prefix(WAIT).unwrap();
        assert!(!prefix.complete);
        assert_eq!(prefix.phase, CapturePhase::Faulted);
        assert_eq!(
            prefix.datasets[0].1.integrity(),
            CaptureIntegrity::Incomplete
        );
        let status = backend.state();
        assert_eq!(status.phase, CapturePhase::Faulted);
        assert!(!status.complete);
        assert_eq!(status.error, original_error);
        let config = SessionConfig {
            timestamp_frequency: armed.timestamp_frequency,
            ..Default::default()
        };
        let mut reference = Session::new(
            config.clone(),
            SharedByteBudget::new(config.managed_memory_budget_bytes),
            Default::default(),
        )
        .unwrap();
        reference
            .begin_collecting(records[0].raw_capture_ticks, true, false)
            .unwrap();
        for record in records {
            reference.append(record).unwrap();
        }
        reference.stop();
        let expected = reference.snapshot().unwrap();
        assert_eq!(
            (
                prefix.generation,
                prefix.received_sequence,
                prefix.received_records
            ),
            (1, 2, 2)
        );
        assert!(prefix.datasets[0].1.events().eq(expected.events()));
        let evidence = prefix.datasets[0].1.capture_evidence();
        assert_eq!(evidence.integrity, CaptureIntegrity::Incomplete);
        assert_eq!(
            (
                evidence.queued,
                evidence.cutoff,
                evidence.ignored_after_limit
            ),
            (Some(2), Some(2), 0)
        );
        assert!(
            backend
                .local_shared
                .as_ref()
                .unwrap()
                .motion
                .peek(id)
                .is_none()
        );
        if block_generation {
            backend.shutdown().unwrap();
        } else {
            // Shutdown already ran on the real driver; finish its owner/thread cleanup.
            backend.cancel_and_join().unwrap();
        }
    }
}
