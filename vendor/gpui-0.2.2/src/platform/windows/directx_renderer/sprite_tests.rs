use super::{
    DirectXDevices, DirectXGlobalElements, DirectXRenderer, GlobalParams, PipelineState,
    RENDER_TARGET_FORMAT, Result, ShaderModule, create_blend_state, set_rasterizer_state,
    update_buffer,
};
use crate::{
    AtlasTextureId, AtlasTextureKind, AtlasTile, Bounds, ContentMask, DevicePixels, FontWeight,
    MonochromeSprite, PlatformTextSystem, Point, RenderGlyphParams, ScaledPixels, SvgAlphaMode,
    TileId, TransformationMatrix, font, platform::windows::DirectWriteTextSystem, point, px, rgb,
    size,
};
use windows::Win32::Graphics::{Direct3D11::*, Dxgi::Common::*};

const FRAME_SIZE: u32 = 64;
const ATLAS_SIZE: u32 = 32;

struct SpriteTestRenderer {
    devices: DirectXDevices,
    globals: DirectXGlobalElements,
    pipeline: PipelineState<MonochromeSprite>,
    target: ID3D11Texture2D,
    target_view: [Option<ID3D11RenderTargetView>; 1],
    staging: ID3D11Texture2D,
    frame_size: u32,
    atlas_size: u32,
}

impl SpriteTestRenderer {
    fn new(frame_size: u32, atlas_size: u32) -> Result<Self> {
        let devices = DirectXDevices::new()?;
        let device = &devices.device;
        let globals = DirectXGlobalElements::new(device)?;
        let pipeline = PipelineState::new(
            device,
            "sprite_test",
            ShaderModule::MonochromeSprite,
            1,
            create_blend_state(device)?,
        )?;
        let desc = D3D11_TEXTURE2D_DESC {
            Width: frame_size,
            Height: frame_size,
            MipLevels: 1,
            ArraySize: 1,
            Format: RENDER_TARGET_FORMAT,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: D3D11_BIND_RENDER_TARGET.0 as u32,
            ..Default::default()
        };
        let mut target = None;
        let mut target_view = None;
        let mut staging = None;
        unsafe {
            device.CreateTexture2D(&desc, None, Some(&mut target))?;
            device.CreateRenderTargetView(
                target.as_ref().unwrap(),
                None,
                Some(&mut target_view),
            )?;
            device.CreateTexture2D(
                &D3D11_TEXTURE2D_DESC {
                    Usage: D3D11_USAGE_STAGING,
                    BindFlags: 0,
                    CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
                    ..desc
                },
                None,
                Some(&mut staging),
            )?;
        }
        set_rasterizer_state(device, &devices.device_context)?;
        let font = DirectXRenderer::get_font_info();
        update_buffer(
            &devices.device_context,
            globals.global_params_buffer[0].as_ref().unwrap(),
            &[GlobalParams {
                gamma_ratios: font.gamma_ratios,
                viewport_size: [frame_size as f32; 2],
                grayscale_enhanced_contrast: font.grayscale_enhanced_contrast,
                _pad: 0,
            }],
        )?;
        Ok(Self {
            devices,
            globals,
            pipeline,
            target: target.unwrap(),
            target_view: [target_view],
            staging: staging.unwrap(),
            frame_size,
            atlas_size,
        })
    }

    fn atlas_view(
        &self,
        tile: &AtlasTile,
        alpha: &[u8],
        neighbor: u8,
    ) -> Result<[Option<ID3D11ShaderResourceView>; 1]> {
        let mut bytes = vec![neighbor; (self.atlas_size * self.atlas_size) as usize];
        let bounds = tile.bounds;
        for y in 0..bounds.size.height.0 as usize {
            let start = (bounds.origin.y.0 as usize + y) * self.atlas_size as usize
                + bounds.origin.x.0 as usize;
            let width = bounds.size.width.0 as usize;
            bytes[start..start + width].copy_from_slice(&alpha[y * width..(y + 1) * width]);
        }
        let mut texture = None;
        let mut view = None;
        unsafe {
            self.devices.device.CreateTexture2D(
                &D3D11_TEXTURE2D_DESC {
                    Width: self.atlas_size,
                    Height: self.atlas_size,
                    MipLevels: 1,
                    ArraySize: 1,
                    Format: DXGI_FORMAT_R8_UNORM,
                    SampleDesc: DXGI_SAMPLE_DESC {
                        Count: 1,
                        Quality: 0,
                    },
                    Usage: D3D11_USAGE_IMMUTABLE,
                    BindFlags: D3D11_BIND_SHADER_RESOURCE.0 as u32,
                    ..Default::default()
                },
                Some(&D3D11_SUBRESOURCE_DATA {
                    pSysMem: bytes.as_ptr().cast(),
                    SysMemPitch: self.atlas_size,
                    SysMemSlicePitch: 0,
                }),
                Some(&mut texture),
            )?;
            self.devices.device.CreateShaderResourceView(
                texture.as_ref().unwrap(),
                None,
                Some(&mut view),
            )?;
        }
        Ok([view])
    }

    fn draw(
        &mut self,
        sprite: &MonochromeSprite,
        atlas: &[Option<ID3D11ShaderResourceView>],
        background: [f32; 4],
    ) -> Result<Vec<u8>> {
        let context = &self.devices.device_context;
        unsafe {
            context.ClearRenderTargetView(self.target_view[0].as_ref().unwrap(), &background);
            context.OMSetRenderTargets(Some(&self.target_view), None);
        }
        self.pipeline
            .update_buffer(&self.devices.device, context, std::slice::from_ref(sprite))?;
        self.pipeline.draw_with_texture(
            context,
            atlas,
            &[D3D11_VIEWPORT {
                Width: self.frame_size as f32,
                Height: self.frame_size as f32,
                MaxDepth: 1.0,
                ..Default::default()
            }],
            &self.globals.global_params_buffer,
            &self.globals.sampler,
            1,
        )?;
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        let mut bytes = vec![0; (self.frame_size * self.frame_size * 4) as usize];
        unsafe {
            context.CopyResource(&self.staging, &self.target);
            context.Map(&self.staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))?;
            for y in 0..self.frame_size as usize {
                std::ptr::copy_nonoverlapping(
                    mapped.pData.cast::<u8>().add(y * mapped.RowPitch as usize),
                    bytes.as_mut_ptr().add(y * self.frame_size as usize * 4),
                    self.frame_size as usize * 4,
                );
            }
            context.Unmap(&self.staging, 0);
        }
        Ok(bytes)
    }
}

#[test]
fn monochrome_sprite_sampling_isolated_from_neighboring_texels() -> Result<()> {
    let mut renderer = SpriteTestRenderer::new(FRAME_SIZE, ATLAS_SIZE)?;
    let mut differing_pixels = 0;
    let mut comparisons = 0;
    let mut maximum_delta = 0;
    for (width, height, opaque) in [(18, 22, false), (18, 22, true), (1, 1, true)] {
        let alpha: Vec<u8> = (0..width * height)
            .map(|i| {
                if opaque
                    || (2..width - 2).contains(&(i % width))
                        && (2..height - 2).contains(&(i / width))
                {
                    255
                } else {
                    0
                }
            })
            .collect();
        // Include the atlas boundary to exercise WRAP as well as neighboring allocations.
        for origin in [
            point(DevicePixels(3), DevicePixels(4)),
            point(DevicePixels(0), DevicePixels(0)),
        ] {
            let tile = AtlasTile {
                texture_id: AtlasTextureId {
                    index: 0,
                    kind: AtlasTextureKind::Monochrome,
                },
                tile_id: TileId(0),
                padding: 0,
                bounds: Bounds::new(origin, size(DevicePixels(width), DevicePixels(height))),
            };
            let clean = renderer.atlas_view(&tile, &alpha, 0)?;
            let poisoned = renderer.atlas_view(&tile, &alpha, 255)?;
            for scale in [0.5, 1.0, 1.25, 2.0] {
                for mode in [SvgAlphaMode::ContrastCorrected, SvgAlphaMode::Coverage] {
                    for color in [0x5c35ff, 0x13151a] {
                        for phase in 0..=32 {
                            let fraction = phase as f32 / 32.0;
                            let sprite = MonochromeSprite {
                                order: 0,
                                alpha_mode: mode as u32,
                                bounds: Bounds::new(
                                    point(ScaledPixels(8.0), ScaledPixels(8.0)),
                                    size(
                                        ScaledPixels(width as f32 * scale),
                                        ScaledPixels(height as f32 * scale),
                                    ),
                                ),
                                content_mask: ContentMask {
                                    bounds: Bounds::new(
                                        Point::default(),
                                        size(
                                            ScaledPixels(FRAME_SIZE as f32),
                                            ScaledPixels(FRAME_SIZE as f32),
                                        ),
                                    ),
                                },
                                color: rgb(color).into(),
                                tile: tile.clone(),
                                transformation: TransformationMatrix::unit().translate(point(
                                    ScaledPixels(fraction),
                                    ScaledPixels(1.0 - fraction),
                                )),
                            };
                            let expected = renderer.draw(&sprite, &clean, [1.0; 4])?;
                            let actual = renderer.draw(&sprite, &poisoned, [1.0; 4])?;
                            // The normal-size tile must still draw; isolation must not hide the sprite.
                            if width > 1 || scale >= 1.0 {
                                assert!(
                                    expected.chunks_exact(4).any(|pixel| pixel[..3] != [255; 3])
                                );
                            }
                            let differences = expected
                                .chunks_exact(4)
                                .zip(actual.chunks_exact(4))
                                .filter(|(a, b)| a != b)
                                .count();
                            maximum_delta = maximum_delta.max(
                                expected
                                    .iter()
                                    .zip(&actual)
                                    .map(|(a, b)| a.abs_diff(*b))
                                    .max()
                                    .unwrap(),
                            );
                            differing_pixels += differences;
                            comparisons += 1;
                            if differences > 0 && differing_pixels == differences {
                                if let Some(directory) = std::env::var_os("GPUI_SPRITE_REPORT_DIR")
                                {
                                    for (name, bytes) in
                                        [("clean.bgra", &expected), ("poisoned.bgra", &actual)]
                                    {
                                        std::fs::write(
                                            std::path::Path::new(&directory).join(name),
                                            bytes,
                                        )?;
                                    }
                                    eprintln!(
                                        "first differing frame: {width}x{height}, scale={scale}, phase={phase}, mode={mode:?}"
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    eprintln!(
        "sprite sampling: comparisons={comparisons}, differing_pixels={differing_pixels}, maximum_channel_delta={maximum_delta}"
    );
    assert_eq!(
        differing_pixels, 0,
        "atlas neighbors must not affect any sprite pixel"
    );
    Ok(())
}

#[test]
fn monochrome_sprite_sampling_preserves_texel_centers_and_single_texel_tiles() -> Result<()> {
    let mut renderer = SpriteTestRenderer::new(FRAME_SIZE, ATLAS_SIZE)?;
    for (width, display_width, alpha) in [
        (
            8,
            8,
            (0..64).map(|i| (i * 37 % 256) as u8).collect::<Vec<_>>(),
        ),
        (1, 4, vec![255]),
        (8, 8, vec![0; 64]),
    ] {
        let tile = AtlasTile {
            texture_id: AtlasTextureId {
                index: 0,
                kind: AtlasTextureKind::Monochrome,
            },
            tile_id: TileId(0),
            padding: 0,
            bounds: Bounds::new(
                point(DevicePixels(3), DevicePixels(4)),
                size(DevicePixels(width), DevicePixels(width)),
            ),
        };
        let atlas = renderer.atlas_view(&tile, &alpha, 255)?;
        let sprite = MonochromeSprite {
            order: 0,
            alpha_mode: SvgAlphaMode::Coverage as u32,
            bounds: Bounds::new(
                point(ScaledPixels(8.0), ScaledPixels(8.0)),
                size(
                    ScaledPixels(display_width as f32),
                    ScaledPixels(display_width as f32),
                ),
            ),
            content_mask: ContentMask {
                bounds: Bounds::new(
                    Point::default(),
                    size(
                        ScaledPixels(FRAME_SIZE as f32),
                        ScaledPixels(FRAME_SIZE as f32),
                    ),
                ),
            },
            color: rgb(0).into(),
            tile,
            transformation: TransformationMatrix::unit(),
        };
        let frame = renderer.draw(&sprite, &atlas, [1.0; 4])?;
        for y in 0..FRAME_SIZE as usize {
            for x in 0..FRAME_SIZE as usize {
                let expected = if (8..8 + display_width).contains(&(x as i32))
                    && (8..8 + display_width).contains(&(y as i32))
                {
                    255 - alpha[if width == 1 {
                        0
                    } else {
                        (y - 8) * width as usize + x - 8
                    }]
                } else {
                    255
                };
                let start = (y * FRAME_SIZE as usize + x) * 4;
                assert_eq!(
                    &frame[start..start + 4],
                    &[expected, expected, expected, 255],
                    "tile {width}x{width}, pixel ({x}, {y})"
                );
            }
        }
    }
    Ok(())
}

#[test]
fn monochrome_sprite_sampling_real_chinese_glyphs_stays_isolated_at_rest() -> Result<()> {
    let mut renderer = SpriteTestRenderer::new(256, 256)?;
    let text = DirectWriteTextSystem::new(&renderer.devices)?;
    let mut descriptor = font("Microsoft YaHei UI");
    descriptor.weight = FontWeight::BOLD;
    let font_id = text.font_id(&descriptor)?;
    let mut comparisons = 0;
    let mut idle_comparisons = 0;
    let mut differing_frames = 0;
    for character in ['灵', '敏'] {
        let glyph_id = text.glyph_for_char(font_id, character).unwrap();
        assert_ne!(glyph_id.0, 0, "the test font must contain {character}");
        for font_size in [68.0 * 116.0 / 144.0, 68.0] {
            for scale_factor in [1.0, 1.25, 1.5, 1.75, 2.0] {
                for x in 0..crate::SUBPIXEL_VARIANTS_X {
                    for y in 0..crate::SUBPIXEL_VARIANTS_Y {
                        let params = RenderGlyphParams {
                            font_id,
                            glyph_id,
                            font_size: px(font_size),
                            subpixel_variant: point(x, y),
                            scale_factor,
                            is_emoji: false,
                        };
                        let raster_bounds = text.glyph_raster_bounds(&params)?;
                        let (raster_size, alpha) = text.rasterize_glyph(&params, raster_bounds)?;
                        let tile = AtlasTile {
                            texture_id: AtlasTextureId {
                                index: 0,
                                kind: AtlasTextureKind::Monochrome,
                            },
                            tile_id: TileId(0),
                            padding: 0,
                            bounds: Bounds::new(
                                point(DevicePixels(3), DevicePixels(4)),
                                raster_size,
                            ),
                        };
                        let clean = renderer.atlas_view(&tile, &alpha, 0)?;
                        let poisoned = renderer.atlas_view(&tile, &alpha, 255)?;
                        for (ink, background) in [
                            (0xffffff, [0.0, 0.0, 0.0, 1.0]),
                            (0x1e3a89, [0.0, 0.0, 0.0, 1.0]),
                            (0x000000, [30.0 / 255.0, 58.0 / 255.0, 137.0 / 255.0, 1.0]),
                        ] {
                            // End with repeated zero offsets while only atlas neighbors change.
                            let mut settled = None;
                            for offset in [-0.96875, -0.75, -0.5, -0.25, -0.03125, 0.0, 0.0] {
                                let sprite = MonochromeSprite {
                                    order: 0,
                                    alpha_mode: SvgAlphaMode::ContrastCorrected as u32,
                                    bounds: Bounds::new(
                                        point(ScaledPixels(16.0), ScaledPixels(16.0)),
                                        raster_size.map(|p| ScaledPixels(p.0 as f32)),
                                    ),
                                    content_mask: ContentMask {
                                        bounds: Bounds::new(
                                            Point::default(),
                                            size(ScaledPixels(256.0), ScaledPixels(256.0)),
                                        ),
                                    },
                                    color: rgb(ink).into(),
                                    tile: tile.clone(),
                                    transformation: TransformationMatrix::unit()
                                        .translate(point(ScaledPixels(0.0), ScaledPixels(offset))),
                                };
                                let expected = renderer.draw(&sprite, &clean, background)?;
                                let actual = renderer.draw(&sprite, &poisoned, background)?;
                                if expected != actual {
                                    differing_frames += 1;
                                    if differing_frames == 1 {
                                        eprintln!(
                                            "glyph leak: {character}, font_size={font_size}, scale={scale_factor}, variant=({x},{y}), offset={offset}"
                                        );
                                        if let Some(directory) =
                                            std::env::var_os("GPUI_SPRITE_REPORT_DIR")
                                        {
                                            for (name, bytes) in [
                                                ("glyph-clean.bgra", &expected),
                                                ("glyph-poisoned.bgra", &actual),
                                            ] {
                                                std::fs::write(
                                                    std::path::Path::new(&directory).join(name),
                                                    bytes,
                                                )?;
                                            }
                                        }
                                    }
                                }
                                comparisons += 1;
                                if offset == 0.0 {
                                    idle_comparisons += 1;
                                    if let Some(previous) = settled.as_ref() {
                                        assert_eq!(
                                            previous, &expected,
                                            "settled glyph must not change across redraws"
                                        );
                                    }
                                    settled = Some(expected);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    eprintln!(
        "Chinese glyph sampling: comparisons={comparisons}, idle_comparisons={idle_comparisons}, differing_frames={differing_frames}"
    );
    assert_eq!(
        differing_frames, 0,
        "atlas neighbors must not draw below or beside a cached glyph"
    );
    Ok(())
}
