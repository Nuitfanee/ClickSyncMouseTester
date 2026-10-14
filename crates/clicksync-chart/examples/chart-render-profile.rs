//! Bounded offscreen rendering measurements and an inspectable image, not a display FPS test.
#[cfg(windows)]
use clicksync_chart::{
    ChartState, Coordinate, DataVersion, DatasetId, Integrity, Point,
    gpu::{
        BlockKey, Instance, PlotKind, PreparedBlock, PreparedScene, RenderSpec, Renderer,
        ResourceLimits, Viewport, create_offscreen_device, device_identity,
    },
};
#[cfg(windows)]
use std::{
    path::PathBuf,
    sync::Arc,
    time::{Duration, Instant},
};
#[cfg(windows)]
use windows::Win32::Graphics::Direct3D11::*;

#[cfg(windows)]
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let output = PathBuf::from(
        std::env::args_os()
            .nth(1)
            .ok_or("supply an output directory")?,
    );
    std::fs::create_dir_all(&output)?;
    let args: Vec<_> = std::env::args().skip(2).collect();
    let count = args.first().map_or(Ok(65_536), |v| v.parse::<usize>())?;
    let frames = args.get(1).map_or(Ok(128), |v| v.parse::<u64>())?;
    let span = args.get(2).map_or(Ok(8000.0), |v| v.parse::<f64>())?;
    assert!(count.is_multiple_of(4096) && (4096..=1_048_576).contains(&count));
    assert!((32..=1024).contains(&frames) && span.is_finite() && span > 0.0);
    let scene = Arc::new(PreparedScene {
        state: ChartState {
            processed_end: count,
            revisable_from: count,
            published_end: count,
            finished: true,
            data_version: Some(DataVersion {
                dataset: DatasetId(1),
                generation: 1,
                end: count,
                session_revision: 1,
                timestamp_frequency: 10_000_000,
                status: clicksync_core::SessionStatus::Stopped,
                source_finalized: true,
                integrity: Integrity::Complete,
            }),
            parameter_version: 1,
            content_version: 1,
        },
        blocks: (0..count / 4096)
            .map(|id| {
                let mut instances = Vec::new();
                for index in id * 4096..(id + 1) * 4096 {
                    let x = index as f32;
                    let y = (x * 0.002).sin() * 12.0;
                    instances.push(Instance::stem([x, 0.0], [x, y.round()]));
                }
                for index in id * 4096..(id + 1) * 4096 {
                    let x = index as f32;
                    let y = (x * 0.002).sin() * 12.0;
                    instances.push(Instance::point(x, y.round()));
                }
                Arc::new(PreparedBlock {
                    key: BlockKey {
                        dataset: 1,
                        generation: 1,
                        parameters: 1,
                        kind: PlotKind::Points as u32,
                        id,
                    },
                    content_version: 1,
                    origin: Point {
                        x: Coordinate::Scalar(0.0),
                        y: Coordinate::Scalar(0.0),
                    },
                    instances: instances.into(),
                })
            })
            .collect(),
    });
    let (device, context) = create_offscreen_device()?;
    let identity = device_identity(&device)?;
    let make_query = |kind| -> Result<ID3D11Query, windows::core::Error> {
        let mut query = None;
        unsafe {
            device.CreateQuery(
                &D3D11_QUERY_DESC {
                    Query: kind,
                    MiscFlags: 0,
                },
                Some(&mut query),
            )?;
        }
        Ok(query.unwrap())
    };
    let disjoint = make_query(D3D11_QUERY_TIMESTAMP_DISJOINT)?;
    let gpu_start = make_query(D3D11_QUERY_TIMESTAMP)?;
    let gpu_end = make_query(D3D11_QUERY_TIMESTAMP)?;
    let clock_done = make_query(D3D11_QUERY_EVENT)?;
    let limits = ResourceLimits {
        resident_bytes: 12 * 1024 * 1024,
        resident_blocks: 192,
        instances_per_upload: 1536,
        ..ResourceLimits::default()
    };
    let mut renderer = Renderer::new(device, 1, limits)?;
    let mut spec = RenderSpec::new(
        Viewport {
            origin: Point {
                x: Coordinate::Scalar(0.0),
                y: Coordinate::Scalar(-16.0),
            },
            width: span,
            height: 32.0,
        },
        1600,
        800,
        1,
    );
    spec.style_version = 1;
    spec.color = [0.39, 0.2, 1.0, 0.66];
    spec.radius = 1.15;
    spec.line_width = 1.0;
    let mut times = Vec::new();
    let mut gpu_times = Vec::new();
    let mut disjoint_frames = 0;
    let mut ticks = Vec::new();
    let mut warm_uploads = 0;
    for frame in 0..frames {
        spec.request_id = frame;
        spec.viewport.origin.x = Coordinate::Scalar(frame as f64 * 2.0);
        let before = renderer.report();
        let started = Instant::now();
        unsafe {
            context.Begin(&disjoint);
            context.End(&gpu_start);
        }
        renderer.begin(&context, scene.clone(), spec.clone())?;
        let mut polls = 0;
        let layer = loop {
            polls += 1;
            if let Some(layer) = renderer.tick(&context)? {
                break layer;
            }
            if started.elapsed() > Duration::from_secs(10) {
                return Err("render deadline".into());
            }
            std::thread::yield_now();
        };
        if frame > 0 {
            times.push(started.elapsed().as_secs_f64() * 1000.0);
            ticks.push(polls);
            warm_uploads += renderer.report().uploaded_bytes - before.uploaded_bytes;
        }
        unsafe {
            context.End(&gpu_end);
            context.End(&disjoint);
            context.End(&clock_done);
            context.Flush();
        }
        loop {
            let mut done = windows::core::BOOL(0);
            unsafe {
                context.GetData(
                    &clock_done,
                    Some((&mut done as *mut windows::core::BOOL).cast()),
                    std::mem::size_of_val(&done) as u32,
                    D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
                )?;
            }
            if done.as_bool() {
                break;
            }
            if started.elapsed() > Duration::from_secs(10) {
                return Err("GPU timestamp deadline".into());
            }
            std::thread::yield_now();
        }
        let mut timing = D3D11_QUERY_DATA_TIMESTAMP_DISJOINT::default();
        let (mut first, mut last) = (0u64, 0u64);
        unsafe {
            context.GetData(
                &disjoint,
                Some((&mut timing as *mut D3D11_QUERY_DATA_TIMESTAMP_DISJOINT).cast()),
                std::mem::size_of_val(&timing) as u32,
                D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
            )?;
            context.GetData(
                &gpu_start,
                Some((&mut first as *mut u64).cast()),
                8,
                D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
            )?;
            context.GetData(
                &gpu_end,
                Some((&mut last as *mut u64).cast()),
                8,
                D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
            )?;
        }
        if timing.Disjoint.as_bool() || timing.Frequency == 0 {
            disjoint_frames += 1;
        } else if frame > 0 {
            gpu_times.push((last - first) as f64 * 1000.0 / timing.Frequency as f64);
        }
        if frame == frames - 1 {
            renderer.begin_readback(&context, layer)?;
            let readback = loop {
                if let Some(pixels) = renderer.poll_readback(&context)? {
                    break pixels;
                }
                if started.elapsed() > Duration::from_secs(10) {
                    return Err("readback deadline".into());
                }
                std::thread::yield_now();
            };
            let mut pixels = readback.bgra;
            for p in pixels.chunks_exact_mut(4) {
                let alpha = f32::from(p[3]) / 255.0;
                for channel in &mut p[..3] {
                    *channel = (f32::from(*channel) + 248.0 * (1.0 - alpha))
                        .round()
                        .min(255.0) as u8;
                }
                p.swap(0, 2);
                p[3] = 255;
            }
            let file = std::fs::File::create(output.join("stems.png"))?;
            let mut encoder = png::Encoder::new(file, 1600, 800);
            encoder.set_color(png::ColorType::Rgba);
            encoder.set_depth(png::BitDepth::Eight);
            encoder.write_header()?.write_image_data(&pixels)?;
        }
    }
    assert_eq!(
        warm_uploads, 0,
        "panning cached blocks must not reupload them"
    );
    times.sort_by(f64::total_cmp);
    gpu_times.sort_by(f64::total_cmp);
    assert!(!gpu_times.is_empty(), "no valid GPU timestamps");
    let report = serde_json::json!({
        "device": identity,
        "samples": count, "visible_x_span": spec.viewport.width, "frames": times.len(),
        "offscreen_completion_ms_median": times[times.len()/2],
        "offscreen_completion_ms_p95": times[times.len()*95/100],
        "offscreen_completion_ms_p99": times[(times.len()*99).div_ceil(100)-1],
        "warm_pan_uploaded_bytes": warm_uploads,
        "gpu_command_interval_ms": {
            "n": gpu_times.len(), "p50": gpu_times[gpu_times.len()/2],
            "p95": gpu_times[(gpu_times.len()*95).div_ceil(100)-1],
            "p99": gpu_times[(gpu_times.len()*99).div_ceil(100)-1],
            "max": gpu_times.last(), "disjoint_frames": disjoint_frames,
            "boundary": "D3D11 timestamp interval includes GPU scheduling and idle gaps between CPU submissions; not shader-only busy time or scanout",
        },
        "nonblocking_polls": ticks,
        "resources": renderer.report(),
        "note": "Synthetic offscreen workload; not hardware input or display frame rate."
    });
    std::fs::write(
        output.join("profile.json"),
        serde_json::to_vec_pretty(&report)?,
    )?;
    println!("{report}");
    Ok(())
}

#[cfg(not(windows))]
fn main() {
    eprintln!("This D3D11 rendering profile requires Windows.");
    std::process::exit(1);
}
