#[cfg(windows)]
mod smoke {
    use clicksync_chart::{
        ChartState, Coordinate, DataVersion, DatasetId, Integrity, Point, gpu::*, hlsl,
    };
    use clicksync_core::SessionStatus;
    use serde_json::json;
    use std::{
        error::Error,
        fs::{self, File},
        io::BufWriter,
        path::{Path, PathBuf},
        sync::Arc,
        thread,
        time::{Duration, Instant, SystemTime, UNIX_EPOCH},
    };
    use windows::Win32::Graphics::Direct3D11::ID3D11DeviceContext;

    type Result<T> = std::result::Result<T, Box<dyn Error>>;

    fn scene(instances: Vec<Instance>, origin: Point, version: u64) -> Arc<PreparedScene> {
        let end = instances.len();
        let data = DataVersion {
            dataset: DatasetId(1),
            generation: 1,
            session_revision: version,
            end,
            timestamp_frequency: 1_000_000,
            status: SessionStatus::Stopped,
            source_finalized: true,
            integrity: Integrity::Complete,
        };
        Arc::new(PreparedScene {
            state: ChartState {
                processed_end: end,
                revisable_from: end,
                published_end: end,
                finished: true,
                data_version: Some(data),
                parameter_version: 1,
                content_version: version,
            },
            blocks: vec![Arc::new(PreparedBlock {
                key: BlockKey {
                    dataset: 1,
                    generation: 1,
                    parameters: 1,
                    kind: 0,
                    id: 0,
                },
                content_version: version,
                origin,
                instances: instances.into(),
            })],
        })
    }

    fn render(
        renderer: &mut Renderer,
        context: &ID3D11DeviceContext,
        scene: Arc<PreparedScene>,
        spec: RenderSpec,
    ) -> Result<PublishedChartLayer> {
        renderer.begin(context, scene, spec)?;
        let started = Instant::now();
        loop {
            if let Some(layer) = renderer.tick(context)? {
                return Ok(layer);
            }
            if started.elapsed() > Duration::from_secs(30) {
                return Err("GPU generation deadline exceeded".into());
            }
            thread::yield_now();
        }
    }

    fn read(
        renderer: &mut Renderer,
        context: &ID3D11DeviceContext,
        layer: PublishedChartLayer,
    ) -> Result<ReadbackPixels> {
        renderer.begin_readback(context, layer)?;
        let started = Instant::now();
        loop {
            if let Some(pixels) = renderer.poll_readback(context)? {
                return Ok(pixels);
            }
            if started.elapsed() > Duration::from_secs(30) {
                return Err("GPU readback deadline exceeded".into());
            }
            thread::yield_now();
        }
    }

    fn rgba(bgra: &[u8]) -> Vec<u8> {
        bgra.chunks_exact(4)
            .flat_map(|p| [p[2], p[1], p[0], p[3]])
            .collect()
    }

    fn png(path: &Path, width: u32, height: u32, pixels: &[u8]) -> Result<()> {
        let mut encoder = png::Encoder::new(BufWriter::new(File::create(path)?), width, height);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        encoder.write_header()?.write_image_data(pixels)?;
        Ok(())
    }

    // Independent scalar pixel reference; no shader output is used as a baseline.
    fn reference(scene: &PreparedScene, spec: &RenderSpec) -> Vec<u8> {
        let mut pixels = vec![0.0_f64; spec.width as usize * spec.height as usize * 4];
        let sx = spec.width as f64 / spec.viewport.width;
        let sy = -(spec.height as f64) / spec.viewport.height;
        for block in &scene.blocks {
            let ox = block.origin.x.relative_to(spec.viewport.origin.x) * sx;
            let oy = spec.height as f64 + block.origin.y.relative_to(spec.viewport.origin.y) * sy;
            for instance in &*block.instances {
                let a = [
                    instance.a[0] as f64 * sx + ox,
                    instance.a[1] as f64 * sy + oy,
                ];
                let b = [
                    instance.b[0] as f64 * sx + ox,
                    instance.b[1] as f64 * sy + oy,
                ];
                for y in spec.clip[1]..spec.clip[3] {
                    for x in spec.clip[0]..spec.clip[2] {
                        let p = [x as f64 + 0.5, y as f64 + 0.5];
                        let coverage = if instance.kind == 2 {
                            f64::from(
                                p[0] >= a[0].min(b[0])
                                    && p[0] < a[0].max(b[0])
                                    && p[1] >= a[1].min(b[1])
                                    && p[1] < a[1].max(b[1]),
                            )
                        } else {
                            let d = [b[0] - a[0], b[1] - a[1]];
                            let squared = d[0] * d[0] + d[1] * d[1];
                            let t = if squared > 0.0 {
                                (((p[0] - a[0]) * d[0] + (p[1] - a[1]) * d[1]) / squared)
                                    .clamp(0.0, 1.0)
                            } else {
                                0.0
                            };
                            let distance = (p[0] - a[0] - t * d[0]).hypot(p[1] - a[1] - t * d[1]);
                            let width = if instance.kind == 0 {
                                spec.radius as f64
                            } else {
                                spec.line_width as f64 * 0.5
                            };
                            (width + 0.5 - distance).clamp(0.0, 1.0)
                        };
                        let alpha = spec.color[3] as f64 * coverage;
                        let index = (y as usize * spec.width as usize + x as usize) * 4;
                        // UNORM target quantizes at every blended draw.
                        for channel in 0..4 {
                            let source = if channel < 3 {
                                spec.color[channel] as f64 * alpha
                            } else {
                                alpha
                            };
                            pixels[index + channel] =
                                ((source + pixels[index + channel] * (1.0 - alpha)) * 255.0)
                                    .round()
                                    / 255.0;
                        }
                    }
                }
            }
        }
        pixels.iter().map(|p| (p * 255.0).round() as u8).collect()
    }

    fn check_image(
        directory: &Path,
        name: &str,
        scene: &PreparedScene,
        spec: &RenderSpec,
        pixels: ReadbackPixels,
    ) -> Result<serde_json::Value> {
        let actual = rgba(&pixels.bgra);
        let reference = reference(scene, spec);
        // Encoding is on a worker after owned readback data leaves the renderer.
        let paths = (
            directory.join(format!("{name}.png")),
            directory.join(format!("{name}-reference.png")),
        );
        let encoded = actual.clone();
        let expected = reference.clone();
        let width = spec.width;
        let height = spec.height;
        thread::spawn(move || -> std::result::Result<(), String> {
            png(&paths.0, width, height, &encoded).map_err(|e| e.to_string())?;
            png(&paths.1, width, height, &expected).map_err(|e| e.to_string())
        })
        .join()
        .map_err(|_| "PNG worker panicked")??;
        let maximum_error = actual
            .iter()
            .zip(&reference)
            .map(|(&a, &b)| a.abs_diff(b))
            .max()
            .unwrap_or(0);
        let bad_channels = actual
            .iter()
            .zip(&reference)
            .filter(|(a, b)| a.abs_diff(**b) > 2)
            .count();
        let content_pixels = actual.chunks_exact(4).filter(|p| p[3] > 0).count();
        let premultiplied = actual
            .chunks_exact(4)
            .all(|p| p[..3].iter().all(|c| *c <= p[3]));
        let report = json!({
            "name": name, "width": pixels.width, "height": pixels.height,
            "instances": scene.instance_count(), "content_pixels": content_pixels,
            "maximum_channel_error": maximum_error, "channels_over_two": bad_channels,
            "premultiplied": premultiplied, "rgba_sha256": hlsl::sha256(&actual),
            "reference_sha256": hlsl::sha256(&reference),
            "passed": bad_channels == 0 && content_pixels > 0 && premultiplied,
        });
        fs::write(
            directory.join(format!("{name}-image.json")),
            serde_json::to_vec_pretty(&report)?,
        )?;
        if !report["passed"].as_bool().unwrap() {
            return Err(format!("{name} image differs from scalar reference: {report}").into());
        }
        Ok(report)
    }

    fn verify(directory: &Path) -> Result<serde_json::Value> {
        fs::write(directory.join("chart.hlsl"), hlsl::SOURCE)?;
        fs::write(directory.join("chart.vs.cso"), hlsl::VERTEX)?;
        fs::write(directory.join("chart.ps.cso"), hlsl::PIXEL)?;
        let (device, context) = create_offscreen_device()?;
        let identity = device_identity(&device)?;
        fs::write(
            directory.join("device.json"),
            serde_json::to_vec_pretty(&identity)?,
        )?;
        let origin = Point {
            x: Coordinate::Integer(1_i64 << 54),
            y: Coordinate::Integer(1_i64 << 54),
        };
        let view = Viewport {
            origin,
            width: 256.0,
            height: 192.0,
        };
        let mut spec = RenderSpec::new(view, 256, 192, 1);
        spec.clip = [8, 8, 248, 184];
        spec.color = [0.8, 0.4, 0.2, 0.5];
        spec.radius = 4.0;
        spec.line_width = 3.0;
        let fixture = scene(
            vec![
                Instance::point(24.0, 24.0),
                Instance::point(1.0, 1.0),
                Instance::line([30.0, 90.0], [90.0, 90.0]),
                Instance::line([90.0, 90.0], [90.0, 90.0]),
                Instance::line([40.0, 50.0], [150.0, 140.0]),
                Instance::line([-80.0, 120.0], [300.0, 120.0]),
                Instance::line([190.0, 0.0], [190.0, 130.0]),
                Instance::bar([110.0, 0.0], [125.0, 45.0]),
                Instance::bar([135.0, 0.0], [150.0, 60.0]),
            ],
            origin,
            1,
        );
        let mut renderer = Renderer::new(device.clone(), 1, ResourceLimits::default())?;
        let layer = render(&mut renderer, &context, fixture.clone(), spec.clone())?;
        let pixels = read(&mut renderer, &context, layer.clone())?;
        let images = vec![check_image(
            directory,
            "primitives",
            &fixture,
            &spec,
            pixels,
        )?];
        let uploaded = renderer.report().uploaded_bytes;
        let validated = renderer.report().scene_validation_blocks;
        let repeated = render(&mut renderer, &context, fixture.clone(), spec.clone())?;
        if renderer.report().uploaded_bytes != uploaded {
            return Err("unchanged resident geometry was uploaded again".into());
        }
        if renderer.report().scene_validation_blocks != validated {
            return Err("unchanged scene metadata was validated again".into());
        }
        let mut invalidated = fixture.clone();
        Arc::make_mut(&mut invalidated).state.parameter_version += 1;
        if renderer.begin(&context, invalidated, spec.clone()) != Err(GpuError::InvalidScene) {
            return Err("replacement scene bypassed metadata validation".into());
        }
        let mut extended_instances = fixture.blocks[0].instances.to_vec();
        extended_instances.push(Instance::point(210.0, 150.0));
        let extended = scene(extended_instances, origin, 2);
        let appended = render(&mut renderer, &context, extended.clone(), spec.clone())?;
        if renderer.report().uploaded_bytes - uploaded != std::mem::size_of::<Instance>() as u64 {
            return Err("active-tail append did not upload just its appended range".into());
        }
        if renderer.report().partial_uploads == 0 {
            return Err("missing partial-upload evidence".into());
        }
        // Three leased layers exhaust the pool, retaining the displayed layer.
        if renderer.begin(&context, fixture.clone(), spec.clone()) != Err(GpuError::ResourceBusy) {
            return Err("leased layer limit was bypassed".into());
        }
        drop(layer);
        drop(repeated);
        drop(appended);
        let mut old_spec = spec.clone();
        old_spec.device_epoch = 2;
        if renderer.begin(&context, fixture.clone(), old_spec) != Err(GpuError::WrongEpoch) {
            return Err("old device epoch accepted".into());
        }
        // A tiny resident budget must stream the entire input through bounded uploads.
        let mut streaming = Renderer::new(
            device,
            1,
            ResourceLimits {
                resident_bytes: 0,
                resident_blocks: 0,
                instances_per_upload: 2,
                upload_slots: 2,
                draws_per_tick: 2,
                ..Default::default()
            },
        )?;
        let layer = render(&mut streaming, &context, fixture.clone(), spec.clone())?;
        let streamed = read(&mut streaming, &context, layer)?;
        let mut images = images;
        images.push(check_image(
            directory, "streamed", &fixture, &spec, streamed,
        )?);
        if images[0]["rgba_sha256"] != images[1]["rgba_sha256"] {
            return Err("resident and streamed pixels differ".into());
        }
        if streaming.report().drawn_instances != fixture.instance_count() as u64 {
            return Err("streaming omitted instances".into());
        }
        let large = scene(
            (0..10_000)
                .map(|i| Instance::point((i % 240 + 8) as f32, (i % 176 + 8) as f32))
                .collect(),
            origin,
            3,
        );
        let mut resized = spec.clone();
        resized.request_id = 4;
        resized.width = 512;
        resized.height = 384;
        resized.clip = [16, 16, 496, 368];
        let layer = render(&mut streaming, &context, large.clone(), resized)?;
        let large_pixels = read(&mut streaming, &context, layer)?;
        let expected = fixture.instance_count() as u64 + large.instance_count() as u64;
        if streaming.report().drawn_instances != expected {
            return Err("dense streaming omitted instances".into());
        }
        png(
            &directory.join("dense.png"),
            large_pixels.width,
            large_pixels.height,
            &rgba(&large_pixels.bgra),
        )?;
        let mut visible = vec![Instance::point(-1000.0, -1000.0); 1024];
        visible[256] = Instance::line([-80.0, 100.0], [300.0, 100.0]);
        visible[257] = Instance::point(24.0, 24.0);
        visible[768] = Instance::bar([50.0, 40.0], [60.0, 60.0]);
        visible[769] = Instance::point(24.0, 24.0);
        let culled = scene(visible, origin, 5);
        let before = renderer.report();
        let layer = render(&mut renderer, &context, culled.clone(), spec.clone())?;
        let pixels = read(&mut renderer, &context, layer)?;
        images.push(check_image(
            directory,
            "culled-resident",
            &culled,
            &spec,
            pixels,
        )?);
        if renderer.report().drawn_instances - before.drawn_instances != 512
            || renderer.report().culled_instances - before.culled_instances != 512
        {
            return Err("resident visibility did not exclude only the hidden pages".into());
        }
        let layer = render(&mut streaming, &context, culled.clone(), spec.clone())?;
        let pixels = read(&mut streaming, &context, layer)?;
        images.push(check_image(
            directory,
            "culled-streamed",
            &culled,
            &spec,
            pixels,
        )?);
        if images[images.len() - 1]["rgba_sha256"] != images[images.len() - 2]["rgba_sha256"] {
            return Err("culled resident/streamed primitive order differs".into());
        }
        let resources = renderer.report();
        let streaming_resources = streaming.report();
        for (report, limits) in [
            (&resources, renderer.limits()),
            (&streaming_resources, streaming.limits()),
        ] {
            if report.peak_resident_bytes > limits.resident_bytes
                || report.resident_blocks > limits.resident_blocks
                || report.layers > limits.layer_slots
                || report.peak_layer_bytes > limits.layer_bytes
                || report.peak_readback_bytes > limits.readback_bytes
                || report.upload_slots != limits.upload_slots
                || report.upload_bytes
                    != limits.upload_slots
                        * limits.instances_per_upload
                        * std::mem::size_of::<Instance>()
            {
                return Err("resource envelope exceeded".into());
            }
        }
        Ok(json!({
            "passed": true, "device": identity, "device_epoch": 1,
            "shaders": hlsl::identity(), "images": images, "resources": resources,
            "streaming_resources": streaming_resources, "limits": renderer.limits(),
            "streaming_limits": streaming.limits(),
            "checks": {
                "large_integer_origin": true, "zero_length": true, "clip": true,
                "premultiplied_blend": true, "resident_reuse": true,
                "tail_range_upload": true, "leased_layer_pressure": true, "wrong_epoch_rejected": true,
                "full_dense_streaming": true, "resize": true,
            },
        }))
    }

    pub fn main() -> Result<()> {
        let directory = std::env::var_os("CLICKSYNC_CHART_OFFSCREEN_REPORT_DIR")
            .map(PathBuf::from)
            .unwrap_or_else(|| {
                let repo = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..");
                let run = SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .unwrap()
                    .as_millis();
                repo.join(format!("artifacts/ChartOffscreen/runs/{run}"))
            });
        fs::create_dir_all(&directory)?;
        let result = verify(&directory);
        let report = match &result {
            Ok(report) => report.clone(),
            Err(error) => {
                json!({ "passed": false, "error": error.to_string(), "shaders": hlsl::identity() })
            }
        };
        fs::write(
            directory.join("offscreen.json"),
            serde_json::to_vec_pretty(&report)?,
        )?;
        println!("D3D11 offscreen report: {}", directory.display());
        result.map(|_| ())
    }
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    #[cfg(windows)]
    {
        smoke::main()
    }
    #[cfg(not(windows))]
    {
        Err("D3D11 image gate requires Windows graphics".into())
    }
}
