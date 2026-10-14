use std::sync::atomic::Ordering;

use clicksync_capture::{CaptureShared, CapturedRecord};
use clicksync_core::protocol::CAPTURE_QUEUE_RECORDS;
use clicksync_core::{RawMouseMovementMode, RawMouseRecord};

fn record(index: u64) -> CapturedRecord {
    let device_index = index / 2;
    CapturedRecord {
        generation: 1,
        device_key: (index % 2) as u32 + 1,
        session_sequence: index + 1,
        record: RawMouseRecord::new(
            (device_index % 7) as i32 - 3,
            (device_index % 5) as i32 - 2,
            1_000_000 + device_index * 1250,
            index + 1,
            device_index + 1,
            RawMouseMovementMode::Relative,
        ),
    }
}

#[test]
fn preallocated_rtrb_steady_successful_enqueue_has_zero_heap_allocations() {
    let (mut producer, mut consumer) = rtrb::RingBuffer::new(CAPTURE_QUEUE_RECORDS);
    producer.push(record(0)).unwrap();
    assert_eq!(consumer.pop().unwrap(), record(0));
    let allocations = allocation_counter::measure(|| {
        for round in 0..32u64 {
            for index in 0..CAPTURE_QUEUE_RECORDS as u64 {
                producer
                    .push(record(round * CAPTURE_QUEUE_RECORDS as u64 + index))
                    .unwrap();
            }
            for index in 0..CAPTURE_QUEUE_RECORDS as u64 {
                assert_eq!(
                    consumer.pop().unwrap(),
                    record(round * CAPTURE_QUEUE_RECORDS as u64 + index)
                );
            }
        }
    });
    assert_eq!(allocations.count_total, 0);
    assert_eq!(allocations.bytes_total, 0);
}

#[test]
fn overflow_is_explicit_and_does_not_replace_or_reorder_the_accepted_prefix() {
    let (mut producer, mut consumer) = rtrb::RingBuffer::new(4);
    let shared = CaptureShared::default();
    shared.active.store(true, Ordering::Release);
    shared.generation.store(1, Ordering::Release);
    for index in 0..5 {
        let captured = record(index);
        shared.captured.store(index + 1, Ordering::Release);
        match producer.push(captured) {
            Ok(()) => {
                shared.queued.fetch_add(1, Ordering::Relaxed);
                shared
                    .high_watermark
                    .fetch_max(4 - producer.slots(), Ordering::Relaxed);
            }
            Err(rtrb::PushError::Full(rejected)) => {
                assert_eq!(rejected, record(4));
                shared.overflow.fetch_add(1, Ordering::Release);
                shared.active.store(false, Ordering::Release);
            }
        }
    }
    assert_eq!(shared.captured.load(Ordering::Acquire), 5);
    assert_eq!(shared.queued.load(Ordering::Acquire), 4);
    assert_eq!(shared.overflow.load(Ordering::Acquire), 1);
    assert_eq!(shared.high_watermark.load(Ordering::Acquire), 4);
    assert!(!shared.active.load(Ordering::Acquire));
    for index in 0..4 {
        assert_eq!(consumer.pop().unwrap(), record(index));
    }
    assert!(consumer.pop().is_err());
}
