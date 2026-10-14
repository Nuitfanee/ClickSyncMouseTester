//! Thread-bound access to GPUI's actual Windows device and immutable chart surfaces.

use gpui::{Window, windows_graphics::WindowsSurface};
use serde::Serialize;
use std::thread::{self, ThreadId};
use windows::{
    Win32::Graphics::{
        Direct3D11::{
            D3D11_BIND_SHADER_RESOURCE, D3D11_SUBRESOURCE_DATA, D3D11_TEXTURE2D_DESC,
            D3D11_USAGE_IMMUTABLE,
        },
        Dxgi::{
            Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC},
            IDXGIAdapter1, IDXGIDevice,
        },
    },
    core::Interface,
};

pub const GPUI_VERSION: &str = "0.2.2";
pub const GPUI_REVISION: &str = "69e2130295c2649963eb639fc70b4f2ee8ea1624";
pub const TEST_TEXTURE_WIDTH: u32 = 512;
pub const TEST_TEXTURE_HEIGHT: u32 = 256;

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct GraphicsDeviceIdentity {
    pub backend: &'static str,
    pub name: String,
    pub adapter_luid: String,
    pub vendor_id: u32,
    pub device_id: u32,
    pub software_emulated: bool,
    pub feature_level: u32,
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize)]
pub struct SurfaceMetrics {
    pub logical_width: f32,
    pub logical_height: f32,
    pub scale_factor: f32,
    pub device_width: u32,
    pub device_height: u32,
}

impl SurfaceMetrics {
    pub fn from_window(window: &Window) -> Self {
        Self::new(
            f32::from(window.viewport_size().width),
            f32::from(window.viewport_size().height),
            window.scale_factor(),
        )
    }

    pub fn new(width: f32, height: f32, scale_factor: f32) -> Self {
        Self {
            logical_width: width,
            logical_height: height,
            scale_factor,
            device_width: (width * scale_factor).round().max(0.0) as u32,
            device_height: (height * scale_factor).round().max(0.0) as u32,
        }
    }
}

#[derive(Clone, Debug, Serialize)]
pub struct BridgeReport {
    pub gpui_version: &'static str,
    pub gpui_revision: &'static str,
    pub device: GraphicsDeviceIdentity,
    pub device_epoch: u64,
    pub graphics_thread: String,
    pub metrics: SurfaceMetrics,
    pub texture_width: u32,
    pub texture_height: u32,
    pub texture_bytes: usize,
    pub texture_format: &'static str,
    pub premultiplied_alpha: bool,
    pub live_surfaces: usize,
    pub surface_rebuilds: u64,
}

pub struct GpuiBridge {
    device: GraphicsDeviceIdentity,
    device_epoch: u64,
    thread: ThreadId,
    metrics: SurfaceMetrics,
    surface: Option<WindowsSurface>,
    rebuilds: u64,
}

impl GpuiBridge {
    pub fn new(window: &Window, with_fixture: bool) -> gpui::Result<Self> {
        let (device, device_epoch, surface) = window.with_windows_graphics(|graphics| {
            let dxgi_device: IDXGIDevice = graphics.device.cast()?;
            let adapter: IDXGIAdapter1 = unsafe { dxgi_device.GetAdapter()? }.cast()?;
            let description = unsafe { adapter.GetDesc1()? };
            let device = GraphicsDeviceIdentity {
                backend: "D3D11",
                name: String::from_utf16_lossy(&description.Description)
                    .trim_end_matches('\0')
                    .to_owned(),
                adapter_luid: format!(
                    "{:08x}{:08x}",
                    description.AdapterLuid.HighPart as u32, description.AdapterLuid.LowPart
                ),
                vendor_id: description.VendorId,
                device_id: description.DeviceId,
                software_emulated: description.Flags & 2 != 0,
                feature_level: unsafe { graphics.device.GetFeatureLevel() }.0 as u32,
            };
            let surface = with_fixture
                .then(|| create_test_surface(graphics))
                .transpose()?;
            Ok((device, graphics.device_epoch, surface))
        })?;
        Ok(Self {
            device,
            device_epoch,
            thread: thread::current().id(),
            metrics: SurfaceMetrics::from_window(window),
            surface,
            rebuilds: 0,
        })
    }

    pub fn sync(&mut self, window: &Window) -> gpui::Result<()> {
        gpui::private::anyhow::ensure!(
            self.thread == thread::current().id(),
            "wrong graphics thread"
        );
        self.metrics = SurfaceMetrics::from_window(window);
        let epoch = window.with_windows_graphics(|graphics| Ok(graphics.device_epoch))?;
        if epoch != self.device_epoch {
            let rebuilt = Self::new(window, self.surface.is_some())?;
            self.device = rebuilt.device;
            self.device_epoch = rebuilt.device_epoch;
            self.surface = rebuilt.surface;
            self.rebuilds += 1;
        }
        Ok(())
    }

    pub fn surface(&self) -> Option<WindowsSurface> {
        self.surface.clone()
    }

    pub fn close(&mut self) {
        self.surface = None;
    }

    pub fn report(&self) -> BridgeReport {
        BridgeReport {
            gpui_version: GPUI_VERSION,
            gpui_revision: GPUI_REVISION,
            device: self.device.clone(),
            device_epoch: self.device_epoch,
            graphics_thread: format!("{:?}", self.thread),
            metrics: self.metrics,
            texture_width: self.surface.as_ref().map_or(0, |_| TEST_TEXTURE_WIDTH),
            texture_height: self.surface.as_ref().map_or(0, |_| TEST_TEXTURE_HEIGHT),
            texture_bytes: self.surface.as_ref().map_or(0, |_| {
                (TEST_TEXTURE_WIDTH * TEST_TEXTURE_HEIGHT * 4) as usize
            }),
            texture_format: "DXGI_FORMAT_B8G8R8A8_UNORM",
            premultiplied_alpha: true,
            live_surfaces: usize::from(self.surface.is_some()),
            surface_rebuilds: self.rebuilds,
        }
    }
}

fn create_test_surface(
    graphics: &gpui::windows_graphics::WindowsGraphicsContext<'_>,
) -> gpui::Result<WindowsSurface> {
    let pixels = test_texture();
    let description = D3D11_TEXTURE2D_DESC {
        Width: TEST_TEXTURE_WIDTH,
        Height: TEST_TEXTURE_HEIGHT,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: D3D11_USAGE_IMMUTABLE,
        BindFlags: D3D11_BIND_SHADER_RESOURCE.0 as u32,
        ..Default::default()
    };
    let initial = D3D11_SUBRESOURCE_DATA {
        pSysMem: pixels.as_ptr().cast(),
        SysMemPitch: TEST_TEXTURE_WIDTH * 4,
        SysMemSlicePitch: 0,
    };
    let mut texture = None;
    unsafe {
        graphics
            .device
            .CreateTexture2D(&description, Some(&initial), Some(&mut texture))?
    };
    WindowsSurface::new(
        graphics,
        texture.ok_or_else(|| gpui::private::anyhow::anyhow!("D3D11 texture was not created"))?,
    )
}

fn test_texture() -> Vec<u8> {
    let mut pixels = vec![0; (TEST_TEXTURE_WIDTH * TEST_TEXTURE_HEIGHT * 4) as usize];
    for y in 0..TEST_TEXTURE_HEIGHT {
        for x in 0..TEST_TEXTURE_WIDTH {
            let color = if (24..=488).contains(&x) && (y == 24 || y == 232)
                || (24..=232).contains(&y) && (x == 24 || x == 488)
                || (x > 24 && x < 488 && y > 24 && y < 232 && (x % 64 == 0 || y % 64 == 0))
            {
                [150, 125, 100, 255]
            } else if (72..240).contains(&x) && (76..104).contains(&y) {
                [16, 48, 112, 128]
            } else if (x as i32 - 3 * y as i32 / 2 - 64).abs() < 3 && (48..208).contains(&y) {
                [232, 163, 25, 255]
            } else {
                [0; 4]
            };
            let offset = ((y * TEST_TEXTURE_WIDTH + x) * 4) as usize;
            pixels[offset..offset + 4].copy_from_slice(&color);
        }
    }
    pixels
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dimensions_preserve_dpi_rounding_and_zero_extent() {
        let metrics = SurfaceMetrics::new(1000.0, 680.0, 1.25);
        assert_eq!((metrics.device_width, metrics.device_height), (1250, 850));
        assert_eq!(SurfaceMetrics::new(0.0, 0.0, 1.0).device_width, 0);
    }

    #[test]
    fn surface_fixture_is_bounded_and_has_premultiplied_alpha() {
        let bytes = test_texture();
        assert_eq!(
            bytes.len(),
            (TEST_TEXTURE_WIDTH * TEST_TEXTURE_HEIGHT * 4) as usize
        );
        for pixel in bytes.chunks_exact(4) {
            assert!(pixel[..3].iter().all(|channel| *channel <= pixel[3]));
        }
        assert!(bytes.chunks_exact(4).any(|pixel| pixel[3] == 0));
        assert!(bytes.chunks_exact(4).any(|pixel| pixel[3] == 128));
        assert!(bytes.chunks_exact(4).any(|pixel| pixel[3] == 255));
    }
}
