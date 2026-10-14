use super::*;
use crate::{ActionRegistry, Keymap};
use std::hint::black_box;

fn frame() -> Frame {
    Frame::new(DispatchTree::new(
        Rc::new(RefCell::new(Keymap::default())),
        Rc::new(ActionRegistry::default()),
    ))
}

fn hitbox(id: u64, x: f32, y: f32, width: f32, height: f32) -> Hitbox {
    let bounds = Bounds::new(point(px(x), px(y)), size(px(width), px(height)));
    Hitbox {
        id: HitboxId(id),
        bounds,
        content_mask: ContentMask { bounds },
        behavior: HitboxBehavior::Normal,
    }
}

fn control(frame: &mut Frame, area: WindowControlArea, hitbox: Hitbox) {
    frame.hitboxes.push(hitbox.clone());
    frame.window_control_hitboxes.push((area, hitbox));
}

fn reference(frame: &Frame, position: Point<Pixels>) -> Option<WindowControlArea> {
    let hit_test = frame.hit_test(position);
    frame
        .window_control_hitboxes
        .iter()
        .rev()
        .find(|(_, hitbox)| hit_test.ids.contains(&hitbox.id))
        .map(|(area, _)| *area)
}

#[test]
fn window_control_queries_preserve_masks_priority_and_occlusion() {
    for behavior in [
        HitboxBehavior::Normal,
        HitboxBehavior::BlockMouse,
        HitboxBehavior::BlockMouseExceptScroll,
    ] {
        for reverse_paint_order in [false, true] {
            let mut frame = frame();
            control(
                &mut frame,
                WindowControlArea::Drag,
                hitbox(90, -120.0, -40.0, 360.0, 44.0),
            );
            control(
                &mut frame,
                WindowControlArea::Client,
                hitbox(5, -100.0, -40.0, 32.0, 44.0),
            );
            let mut overlay = hitbox(2, -60.0, -40.0, 100.0, 44.0);
            overlay.behavior = behavior;
            overlay.content_mask.bounds =
                Bounds::new(point(px(-40.0), px(-40.0)), size(px(40.0), px(44.0)));
            frame.hitboxes.push(overlay);
            control(
                &mut frame,
                WindowControlArea::Max,
                hitbox(80, -20.0, -40.0, 32.0, 44.0),
            );
            if reverse_paint_order {
                frame.window_control_hitboxes.reverse();
            }
            for x in (-140..=260).step_by(4) {
                for y in (-48..=12).step_by(4) {
                    let position = point(px(x as f32), px(y as f32));
                    assert_eq!(
                        frame.hit_test_window_control(position),
                        reference(&frame, position),
                        "position {position:?}, blocker {behavior:?}, reversed {reverse_paint_order}"
                    );
                }
            }
        }
    }
}

#[test]
fn window_control_queries_include_blockers_and_scroll_through_overlays() {
    let mut frame = frame();
    control(
        &mut frame,
        WindowControlArea::Drag,
        hitbox(30, 0.0, 0.0, 300.0, 44.0),
    );
    let mut overlay = hitbox(1, 20.0, 0.0, 100.0, 44.0);
    overlay.behavior = HitboxBehavior::BlockMouseExceptScroll;
    frame.hitboxes.push(overlay.clone());
    let position = point(px(50.0), px(22.0));
    assert_eq!(
        frame.hit_test_window_control(position),
        Some(WindowControlArea::Drag)
    );
    frame.hitboxes.last_mut().unwrap().behavior = HitboxBehavior::BlockMouse;
    assert_eq!(frame.hit_test_window_control(position), None);
    overlay.behavior = HitboxBehavior::BlockMouse;
    frame
        .window_control_hitboxes
        .push((WindowControlArea::Client, overlay));
    assert_eq!(
        frame.hit_test_window_control(position),
        Some(WindowControlArea::Client)
    );
    frame.clear();
    assert_eq!(frame.hit_test_window_control(position), None);
}

#[test]
fn window_control_queries_preserve_replayed_non_monotonic_ids() {
    let mut frame = frame();
    control(
        &mut frame,
        WindowControlArea::Drag,
        hitbox(500, 0.0, 0.0, 300.0, 44.0),
    );
    control(
        &mut frame,
        WindowControlArea::Client,
        hitbox(3, 20.0, 0.0, 32.0, 44.0),
    );
    let position = point(px(30.0), px(22.0));
    assert_eq!(
        frame.hit_test_window_control(position),
        Some(WindowControlArea::Client)
    );
    // Simulate cached prepaint/paint replay without regenerating hitbox IDs.
    let mut replayed = self::frame();
    replayed.hitboxes.extend(frame.hitboxes.iter().cloned());
    replayed
        .window_control_hitboxes
        .extend(frame.window_control_hitboxes.iter().cloned());
    frame.clear();
    assert_eq!(
        replayed.hit_test_window_control(position),
        Some(WindowControlArea::Client)
    );
}

fn benchmark_frame(body_hitboxes: usize) -> Frame {
    let mut frame = frame();
    control(
        &mut frame,
        WindowControlArea::Drag,
        hitbox(0, 0.0, 0.0, 1300.0, 44.0),
    );
    control(
        &mut frame,
        WindowControlArea::Client,
        hitbox(1, 900.0, 0.0, 100.0, 44.0),
    );
    for index in 0..body_hitboxes {
        frame.hitboxes.push(hitbox(
            index as u64 + 2,
            (index % 32) as f32 * 40.0,
            48.0 + (index / 32) as f32 * 4.0,
            40.0,
            100.0,
        ));
    }
    frame
}

#[test]
#[ignore = "CPU microbenchmark; run explicitly with --ignored --nocapture"]
fn window_control_query_benchmark() {
    const QUERIES: usize = 2048;
    println!(
        "window control entry bytes: {}",
        std::mem::size_of::<(WindowControlArea, Hitbox)>()
    );
    for body_hitboxes in [64, 512, 4096] {
        let frame = benchmark_frame(body_hitboxes);
        for (name, position) in [
            ("caption", point(px(600.0), px(22.0))),
            ("client-control", point(px(950.0), px(22.0))),
            ("body", point(px(600.0), px(100.0))),
        ] {
            assert_eq!(
                frame.hit_test_window_control(position),
                reference(&frame, position)
            );
            let mut baseline = Vec::new();
            let mut optimized = Vec::new();
            for _ in 0..9 {
                let start = Instant::now();
                for _ in 0..QUERIES {
                    black_box(reference(black_box(&frame), black_box(position)));
                }
                baseline.push(start.elapsed().as_nanos() as f64 / QUERIES as f64);
                let start = Instant::now();
                for _ in 0..QUERIES {
                    black_box(black_box(&frame).hit_test_window_control(black_box(position)));
                }
                optimized.push(start.elapsed().as_nanos() as f64 / QUERIES as f64);
            }
            baseline.sort_by(f64::total_cmp);
            optimized.sort_by(f64::total_cmp);
            println!(
                "{body_hitboxes} body hitboxes / {name}: baseline {:.1} ns, optimized {:.1} ns, ratio {:.2}",
                baseline[4],
                optimized[4],
                baseline[4] / optimized[4]
            );
        }
    }
}
