#[cfg(windows)]
mod windows {
    use clicksync_ipc::Pipe;
    use std::io::{self, ErrorKind};
    use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
    use std::sync::{Arc, mpsc};
    use std::thread;
    use std::time::{Duration, Instant};

    const LIMIT: Duration = Duration::from_secs(2);
    const ACK_LIMIT: Duration = Duration::from_millis(500);
    static NEXT_NAME: AtomicU64 = AtomicU64::new(0);

    fn name() -> String {
        format!(
            r"\\.\pipe\ClickSync-test-{}-{}",
            std::process::id(),
            NEXT_NAME.fetch_add(1, Ordering::Relaxed)
        )
    }

    fn deadline() -> Instant {
        Instant::now() + LIMIT
    }

    fn pair() -> (Pipe, Pipe) {
        let name = name();
        let server = Pipe::server(&name).unwrap();
        let cancel = AtomicBool::new(false);
        let client = Pipe::client(&name, deadline(), &cancel).unwrap();
        server.connect(deadline(), &cancel).unwrap();
        client.connect(deadline(), &cancel).unwrap();
        (server, client)
    }

    #[test]
    fn small_frames_round_trip() {
        let (server, client) = pair();
        let cancel = AtomicBool::new(false);
        for sequence in 0..16u32 {
            let frame = sequence.to_le_bytes();
            client.write_all(&frame, deadline(), &cancel).unwrap();
            let mut request = [0; 4];
            server
                .read_exact(&mut request, deadline(), &cancel)
                .unwrap();
            assert_eq!(request, frame);
            server.write_all(&request, deadline(), &cancel).unwrap();
            let mut response = [0; 4];
            client
                .read_exact(&mut response, deadline(), &cancel)
                .unwrap();
            assert_eq!(response, frame);
        }
    }

    #[test]
    fn reads_and_writes_cross_different_fragment_boundaries() {
        let (server, client) = pair();
        let payload: Vec<u8> = (0..192 * 1024).map(|index| (index % 251) as u8).collect();
        thread::scope(|scope| {
            let expected = &payload;
            let writer = scope.spawn(move || {
                let cancel = AtomicBool::new(false);
                let end = deadline();
                server.write_all(&expected[..64 * 1024], end, &cancel)?;
                for fragment in expected[64 * 1024..].chunks(3001) {
                    server.write_all(fragment, end, &cancel)?;
                }
                io::Result::Ok(())
            });
            let mut actual = vec![0; payload.len()];
            let cancel = AtomicBool::new(false);
            let end = deadline();
            for fragment in actual.chunks_mut(997) {
                client.read_exact(fragment, end, &cancel).unwrap();
            }
            writer.join().unwrap().unwrap();
            assert_eq!(actual, payload);
        });
    }

    #[test]
    fn read_timeout_finishes_cancellation_and_next_read_recovers() {
        let (server, client) = pair();
        let cancel = AtomicBool::new(false);
        let mut expired_buffer = [0; 8];
        let start = Instant::now();
        let error = server
            .read_exact(
                &mut expired_buffer,
                start + Duration::from_millis(60),
                &cancel,
            )
            .unwrap_err();
        assert_eq!(error.kind(), ErrorKind::TimedOut);
        assert!(start.elapsed() < ACK_LIMIT);
        expired_buffer.fill(0xa5);
        client.write_all(b"recovery", deadline(), &cancel).unwrap();
        let mut actual = [0; 8];
        server.read_exact(&mut actual, deadline(), &cancel).unwrap();
        assert_eq!(&actual, b"recovery");
        assert_eq!(expired_buffer, [0xa5; 8]);
    }

    #[test]
    fn blocked_data_write_does_not_delay_separate_control_ack() {
        let (data_server, _data_client) = pair();
        let (control_server, control_client) = pair();
        let cancel = Arc::new(AtomicBool::new(false));
        let (started_tx, started_rx) = mpsc::channel();
        let (result_tx, result_rx) = mpsc::channel();
        thread::scope(|scope| {
            let writer_cancel = Arc::clone(&cancel);
            let writer = scope.spawn(move || {
                let data = vec![0x5a; 256 * 1024];
                started_tx.send(()).unwrap();
                result_tx
                    .send(data_server.write_all(&data, deadline(), &writer_cancel))
                    .unwrap();
            });
            started_rx.recv_timeout(ACK_LIMIT).unwrap();
            assert!(matches!(
                result_rx.recv_timeout(Duration::from_millis(40)),
                Err(mpsc::RecvTimeoutError::Timeout)
            ));
            let controller = scope.spawn(move || {
                let cancel = AtomicBool::new(false);
                let mut command = [0; 4];
                let end = deadline();
                control_server.read_exact(&mut command, end, &cancel)?;
                assert_eq!(&command, b"STOP");
                control_server.write_all(b"ACK", end, &cancel)
            });
            let start = Instant::now();
            let end = start + ACK_LIMIT;
            let control_cancel = AtomicBool::new(false);
            control_client
                .write_all(b"STOP", end, &control_cancel)
                .unwrap();
            let mut ack = [0; 3];
            control_client
                .read_exact(&mut ack, end, &control_cancel)
                .unwrap();
            assert_eq!(&ack, b"ACK");
            assert!(start.elapsed() < ACK_LIMIT);
            assert!(matches!(
                result_rx.try_recv(),
                Err(mpsc::TryRecvError::Empty)
            ));
            cancel.store(true, Ordering::Release);
            assert_eq!(
                result_rx
                    .recv_timeout(ACK_LIMIT)
                    .unwrap()
                    .unwrap_err()
                    .kind(),
                ErrorKind::Interrupted
            );
            writer.join().unwrap();
            controller.join().unwrap().unwrap();
        });
    }

    #[test]
    fn atomic_cancel_interrupts_pending_read_and_allows_reuse() {
        let (server, client) = pair();
        let reader = server.clone();
        let cancel = Arc::new(AtomicBool::new(false));
        let (started_tx, started_rx) = mpsc::channel();
        let (result_tx, result_rx) = mpsc::channel();
        thread::scope(|scope| {
            let reader_cancel = Arc::clone(&cancel);
            let worker = scope.spawn(move || {
                started_tx.send(()).unwrap();
                let mut byte = [0];
                result_tx
                    .send(reader.read_exact(&mut byte, deadline(), &reader_cancel))
                    .unwrap();
            });
            started_rx.recv_timeout(ACK_LIMIT).unwrap();
            assert!(matches!(
                result_rx.recv_timeout(Duration::from_millis(40)),
                Err(mpsc::RecvTimeoutError::Timeout)
            ));
            let start = Instant::now();
            cancel.store(true, Ordering::Release);
            assert_eq!(
                result_rx
                    .recv_timeout(ACK_LIMIT)
                    .unwrap()
                    .unwrap_err()
                    .kind(),
                ErrorKind::Interrupted
            );
            assert!(start.elapsed() < ACK_LIMIT);
            worker.join().unwrap();
        });
        cancel.store(false, Ordering::Release);
        client.write_all(b"x", deadline(), &cancel).unwrap();
        let mut byte = [0];
        server.read_exact(&mut byte, deadline(), &cancel).unwrap();
        assert_eq!(byte, [b'x']);
    }

    #[test]
    fn cancel_io_ex_interrupts_a_read_on_another_clone() {
        let (server, client) = pair();
        let reader = server.clone();
        let (started_tx, started_rx) = mpsc::channel();
        let (result_tx, result_rx) = mpsc::channel();
        thread::scope(|scope| {
            let worker = scope.spawn(move || {
                started_tx.send(()).unwrap();
                let mut byte = [0];
                result_tx
                    .send(reader.read_exact(&mut byte, deadline(), &AtomicBool::new(false)))
                    .unwrap();
            });
            started_rx.recv_timeout(ACK_LIMIT).unwrap();
            assert!(matches!(
                result_rx.recv_timeout(Duration::from_millis(40)),
                Err(mpsc::RecvTimeoutError::Timeout)
            ));
            server.cancel().unwrap();
            assert_eq!(
                result_rx
                    .recv_timeout(ACK_LIMIT)
                    .unwrap()
                    .unwrap_err()
                    .kind(),
                ErrorKind::Interrupted
            );
            worker.join().unwrap();
        });
        server.cancel().unwrap();
        let cancel = AtomicBool::new(false);
        client.write_all(b"x", deadline(), &cancel).unwrap();
        let mut byte = [0];
        server.read_exact(&mut byte, deadline(), &cancel).unwrap();
        assert_eq!(byte, [b'x']);
    }

    #[test]
    fn a_pending_reader_does_not_hold_the_write_lock() {
        let (server, client) = pair();
        let reader = server.clone();
        let (started_tx, started_rx) = mpsc::channel();
        let (result_tx, result_rx) = mpsc::channel();
        thread::scope(|scope| {
            let worker = scope.spawn(move || {
                started_tx.send(()).unwrap();
                let mut response = [0; 3];
                let result = reader.read_exact(&mut response, deadline(), &AtomicBool::new(false));
                result_tx.send((result, response)).unwrap();
            });
            started_rx.recv_timeout(ACK_LIMIT).unwrap();
            assert!(matches!(
                result_rx.recv_timeout(Duration::from_millis(40)),
                Err(mpsc::RecvTimeoutError::Timeout)
            ));
            let cancel = AtomicBool::new(false);
            let end = Instant::now() + ACK_LIMIT;
            server.write_all(b"request", end, &cancel).unwrap();
            let mut request = [0; 7];
            client.read_exact(&mut request, end, &cancel).unwrap();
            assert_eq!(&request, b"request");
            client.write_all(b"ACK", end, &cancel).unwrap();
            let (result, response) = result_rx.recv_timeout(ACK_LIMIT).unwrap();
            result.unwrap();
            assert_eq!(&response, b"ACK");
            worker.join().unwrap();
        });
    }

    #[test]
    fn read_lock_contention_obeys_its_own_deadline() {
        let (server, client) = pair();
        let reader = server.clone();
        let (started_tx, started_rx) = mpsc::channel();
        let (result_tx, result_rx) = mpsc::channel();
        thread::scope(|scope| {
            let worker = scope.spawn(move || {
                started_tx.send(()).unwrap();
                let mut byte = [0];
                result_tx
                    .send(reader.read_exact(&mut byte, deadline(), &AtomicBool::new(false)))
                    .unwrap();
            });
            started_rx.recv_timeout(ACK_LIMIT).unwrap();
            assert!(matches!(
                result_rx.recv_timeout(Duration::from_millis(40)),
                Err(mpsc::RecvTimeoutError::Timeout)
            ));
            let cancel = AtomicBool::new(false);
            let start = Instant::now();
            assert_eq!(
                server
                    .read_exact(&mut [0], start + Duration::from_millis(60), &cancel)
                    .unwrap_err()
                    .kind(),
                ErrorKind::TimedOut
            );
            assert!(start.elapsed() < ACK_LIMIT);
            assert!(matches!(
                result_rx.try_recv(),
                Err(mpsc::TryRecvError::Empty)
            ));
            client.write_all(b"x", deadline(), &cancel).unwrap();
            result_rx.recv_timeout(ACK_LIMIT).unwrap().unwrap();
            worker.join().unwrap();
        });
    }

    #[test]
    fn peer_process_ids_match_the_connected_process() {
        let (server, client) = pair();
        assert_eq!(server.peer_process_id().unwrap(), std::process::id());
        assert_eq!(client.peer_process_id().unwrap(), std::process::id());
    }

    #[test]
    fn connect_timeout_can_be_followed_by_a_real_connection() {
        let name = name();
        let server = Pipe::server(&name).unwrap();
        let cancel = AtomicBool::new(false);
        let start = Instant::now();
        assert_eq!(
            server
                .connect(start + Duration::from_millis(60), &cancel)
                .unwrap_err()
                .kind(),
            ErrorKind::TimedOut
        );
        assert!(start.elapsed() < ACK_LIMIT);
        let client = Pipe::client(&name, deadline(), &cancel).unwrap();
        server.connect(deadline(), &cancel).unwrap();
        client.write_all(b"x", deadline(), &cancel).unwrap();
        let mut byte = [0];
        server.read_exact(&mut byte, deadline(), &cancel).unwrap();
        assert_eq!(byte, [b'x']);
    }

    #[test]
    fn client_retries_missing_and_busy_pipes_only_until_deadline() {
        let cancel = AtomicBool::new(false);
        let start = Instant::now();
        let error = Pipe::client(&name(), start + Duration::from_millis(60), &cancel)
            .err()
            .expect("missing pipe must not open");
        assert_eq!(error.kind(), ErrorKind::TimedOut);
        assert!(start.elapsed() < ACK_LIMIT);

        let name = name();
        let server = Pipe::server(&name).unwrap();
        let _client = Pipe::client(&name, deadline(), &cancel).unwrap();
        server.connect(deadline(), &cancel).unwrap();
        let start = Instant::now();
        let error = Pipe::client(&name, start + Duration::from_millis(60), &cancel)
            .err()
            .expect("busy pipe must not open");
        assert_eq!(error.kind(), ErrorKind::TimedOut);
        assert!(start.elapsed() < ACK_LIMIT);
    }

    #[test]
    fn first_instance_and_name_limits_are_enforced() {
        let pipe_name = name();
        let _server = Pipe::server(&pipe_name).unwrap();
        assert!(Pipe::server(&pipe_name).is_err());
        for invalid in [
            "ClickSync-wrong-prefix".to_owned(),
            r"\\remote\pipe\ClickSync-test".to_owned(),
            r"\\.\pipe\Other-test".to_owned(),
            r"\\.\pipe\ClickSync-".to_owned(),
            format!("{pipe_name}\\child"),
            format!("{pipe_name}\0tail"),
            format!("{pipe_name}{}", "x".repeat(201)),
            format!("{pipe_name}{}", "\u{1f600}".repeat(101)),
        ] {
            assert_eq!(
                Pipe::server(&invalid).err().unwrap().kind(),
                ErrorKind::InvalidInput
            );
            assert_eq!(
                Pipe::client(&invalid, deadline(), &AtomicBool::new(false))
                    .err()
                    .unwrap()
                    .kind(),
                ErrorKind::InvalidInput
            );
        }
        let mut maximum = name();
        maximum.push_str(&"x".repeat(200 - maximum.encode_utf16().count()));
        let _maximum_server = Pipe::server(&maximum).unwrap();
    }

    #[test]
    fn expired_and_cancelled_empty_io_does_not_report_success() {
        let (server, client) = pair();
        let cancel = AtomicBool::new(false);
        let expired = Instant::now();
        assert_eq!(
            server
                .read_exact(&mut [], expired, &cancel)
                .unwrap_err()
                .kind(),
            ErrorKind::TimedOut
        );
        assert_eq!(
            server.write_all(&[], expired, &cancel).unwrap_err().kind(),
            ErrorKind::TimedOut
        );
        assert_eq!(
            server.connect(expired, &cancel).unwrap_err().kind(),
            ErrorKind::TimedOut
        );
        cancel.store(true, Ordering::Release);
        assert_eq!(
            client
                .read_exact(&mut [], deadline(), &cancel)
                .unwrap_err()
                .kind(),
            ErrorKind::Interrupted
        );
        assert_eq!(
            client
                .write_all(&[], deadline(), &cancel)
                .unwrap_err()
                .kind(),
            ErrorKind::Interrupted
        );
        assert_eq!(
            client.connect(deadline(), &cancel).unwrap_err().kind(),
            ErrorKind::Interrupted
        );
        assert_eq!(
            Pipe::client(&name(), deadline(), &cancel)
                .err()
                .unwrap()
                .kind(),
            ErrorKind::Interrupted
        );
    }
}

#[cfg(not(windows))]
#[test]
fn non_windows_operations_are_explicitly_unsupported() {
    use clicksync_ipc::Pipe;
    use std::io::ErrorKind;
    use std::sync::atomic::AtomicBool;
    use std::time::Instant;

    let end = Instant::now();
    let cancel = AtomicBool::new(false);
    assert_eq!(
        Pipe::server("unused").err().unwrap().kind(),
        ErrorKind::Unsupported
    );
    assert_eq!(
        Pipe::client("unused", end, &cancel).err().unwrap().kind(),
        ErrorKind::Unsupported
    );
    let pipe = Pipe;
    assert_eq!(
        pipe.connect(end, &cancel).unwrap_err().kind(),
        ErrorKind::Unsupported
    );
    assert_eq!(
        pipe.read_exact(&mut [], end, &cancel).unwrap_err().kind(),
        ErrorKind::Unsupported
    );
    assert_eq!(
        pipe.write_all(&[], end, &cancel).unwrap_err().kind(),
        ErrorKind::Unsupported
    );
    assert_eq!(pipe.cancel().unwrap_err().kind(), ErrorKind::Unsupported);
    assert_eq!(
        pipe.peer_process_id().unwrap_err().kind(),
        ErrorKind::Unsupported
    );
}
