//! Windows D3D11 access serialized with GPUI's window renderer.

use anyhow::{Result, ensure};
use std::{any::Any, marker::PhantomData, rc::Rc, thread::ThreadId};
use windows::{
    Win32::Graphics::{
        Direct3D11::{
            D3D11_BIND_SHADER_RESOURCE, ID3D11Device, ID3D11DeviceContext,
            ID3D11ShaderResourceView, ID3D11Texture2D,
        },
        Dxgi::Common::DXGI_FORMAT_B8G8R8A8_UNORM,
    },
    core::Interface,
};

/// Borrowed device and immediate context for one graphics-thread operation.
pub struct WindowsGraphicsContext<'a> {
    /// Device currently backing this GPUI window.
    pub device: &'a ID3D11Device,
    /// Immediate context, used only within this callback.
    pub immediate_context: &'a ID3D11DeviceContext,
    /// Increases after successful device reconstruction.
    pub device_epoch: u64,
    pub(crate) _thread_bound: PhantomData<Rc<()>>,
}

/// Read-only premultiplied BGRA texture composited in GPUI scene order.
#[derive(Clone)]
pub struct WindowsSurface {
    pub(crate) texture: ID3D11Texture2D,
    pub(crate) shader_view: ID3D11ShaderResourceView,
    pub(crate) device_epoch: u64,
    pub(crate) thread: ThreadId,
    _thread_bound: PhantomData<Rc<()>>,
    keep_alive: Option<Rc<dyn Any>>,
}

impl std::fmt::Debug for WindowsSurface {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WindowsSurface").field("device_epoch", &self.device_epoch).finish()
    }
}

/// Diagnostic pixels copied from the actual GPUI composition before Present.
pub struct WindowsFrame {
    /// Device-pixel width.
    pub width: u32,
    /// Device-pixel height.
    pub height: u32,
    /// Row-packed premultiplied BGRA8 pixels.
    pub bgra: Vec<u8>,
}

impl WindowsSurface {
    /// Wrap a completed immutable layer made on this borrowed device.
    pub fn new(graphics: &WindowsGraphicsContext<'_>, texture: ID3D11Texture2D) -> Result<Self> {
        let mut description = Default::default();
        unsafe { texture.GetDesc(&mut description) };
        ensure!(
            description.Format == DXGI_FORMAT_B8G8R8A8_UNORM
                && description.SampleDesc.Count == 1
                && description.MipLevels == 1
                && description.ArraySize == 1
                && description.BindFlags & D3D11_BIND_SHADER_RESOURCE.0 as u32 != 0
                && description.Width > 0
                && description.Height > 0,
            "surface requires a nonempty single-sampled BGRA8 texture"
        );
        let texture_device = unsafe { texture.GetDevice()? };
        ensure!(
            texture_device.as_raw() == graphics.device.as_raw(),
            "surface texture belongs to a different D3D11 device"
        );
        let mut shader_view = None;
        unsafe {
            graphics
                .device
                .CreateShaderResourceView(&texture, None, Some(&mut shader_view))?
        };
        Ok(Self {
            texture,
            shader_view: shader_view
                .ok_or_else(|| anyhow::anyhow!("surface shader view was not created"))?,
            device_epoch: graphics.device_epoch,
            thread: std::thread::current().id(),
            _thread_bound: PhantomData,
            keep_alive: None,
        })
    }

    /// Device generation in which the texture was produced.
    pub fn device_epoch(&self) -> u64 {
        self.device_epoch
    }

    /// Scene clones retain the external producer's resource lease.
    pub fn retain(mut self, owner: Rc<dyn Any>) -> Self {
        self.keep_alive = Some(owner);
        self
    }
}

impl PartialEq for WindowsSurface {
    fn eq(&self, other: &Self) -> bool {
        self.texture == other.texture && self.device_epoch == other.device_epoch
    }
}

impl Eq for WindowsSurface {}
