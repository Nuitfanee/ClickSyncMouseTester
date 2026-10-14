use super::*;
use crate::{Point, hlsl};
use serde::Serialize;
use std::{
    collections::{HashMap, HashSet},
    marker::PhantomData,
    mem::size_of,
    rc::Rc,
    sync::{Arc, Weak},
    thread::{self, ThreadId},
};
use windows::{
    Win32::{
        Foundation::{HMODULE, RECT},
        Graphics::{
            Direct3D::{
                D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL_11_0,
                D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
            },
            Direct3D11::*,
            Dxgi::{Common::*, IDXGIAdapter1, IDXGIDevice},
        },
    },
    core::{BOOL, Interface, s},
};

impl From<windows::core::Error> for GpuError {
    fn from(error: windows::core::Error) -> Self {
        Self::Device(error.to_string())
    }
}

#[derive(Clone, Debug, Serialize)]
pub struct DeviceIdentity {
    pub name: String,
    pub adapter_luid: String,
    pub vendor_id: u32,
    pub device_id: u32,
    pub software_emulated: bool,
    pub feature_level: u32,
}

pub fn create_offscreen_device() -> Result<(ID3D11Device, ID3D11DeviceContext), GpuError> {
    let mut device = None;
    let mut context = None;
    unsafe {
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            Some(&[D3D_FEATURE_LEVEL_11_0]),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )?;
    }
    Ok((required(device)?, required(context)?))
}

pub fn device_identity(device: &ID3D11Device) -> Result<DeviceIdentity, GpuError> {
    let dxgi: IDXGIDevice = device.cast()?;
    let adapter: IDXGIAdapter1 = unsafe { dxgi.GetAdapter()? }.cast()?;
    let desc = unsafe { adapter.GetDesc1()? };
    Ok(DeviceIdentity {
        name: String::from_utf16_lossy(&desc.Description)
            .trim_end_matches('\0')
            .into(),
        adapter_luid: format!(
            "{:08x}{:08x}",
            desc.AdapterLuid.HighPart as u32, desc.AdapterLuid.LowPart
        ),
        vendor_id: desc.VendorId,
        device_id: desc.DeviceId,
        software_emulated: desc.Flags & 2 != 0,
        feature_level: unsafe { device.GetFeatureLevel() }.0 as u32,
    })
}

fn required<T>(value: Option<T>) -> Result<T, GpuError> {
    value.ok_or_else(|| GpuError::Device("D3D11 returned no resource".into()))
}

struct LayerTarget {
    texture: ID3D11Texture2D,
    view: ID3D11RenderTargetView,
    width: u32,
    height: u32,
}

/// Keep this lease alive while a compositor uses the texture. A retained lease
/// prevents the renderer from writing it, including during resize.
#[derive(Clone)]
pub struct PublishedChartLayer {
    pub version: LayerVersion,
    target: Rc<LayerTarget>,
}

impl PublishedChartLayer {
    pub fn lease(&self) -> Rc<dyn std::any::Any> {
        self.target.clone()
    }
    pub fn texture(&self) -> ID3D11Texture2D {
        self.target.texture.clone()
    }
}

struct Resident {
    buffer: ID3D11Buffer,
    capacity: usize,
    block: Arc<PreparedBlock>,
    last_used: u64,
}

struct UploadSlot {
    buffer: ID3D11Buffer,
    completion: ID3D11Query,
    busy: bool,
}

struct Generation {
    scene: Arc<PreparedScene>,
    spec: RenderSpec,
    target: usize,
    block: usize,
    instance: usize,
    submitted: bool,
    completion: ID3D11Query,
}

#[repr(C)]
struct Constants {
    scale: [f32; 2],
    offset: [f32; 2],
    extent: [f32; 2],
    radius: f32,
    half_width: f32,
    color: [f32; 4],
    clip: [f32; 4],
    reserved: [f32; 4],
}

/// All context calls must be made inside the bridge's isolated graphics entry.
/// Each tick does a bounded number of draws. Busy slots return to the caller.
pub struct Renderer {
    device: ID3D11Device,
    epoch: u64,
    owner: ThreadId,
    _thread_bound: PhantomData<Rc<()>>,
    limits: ResourceLimits,
    report: ResourceReport,
    vertex: ID3D11VertexShader,
    pixel: ID3D11PixelShader,
    layout: ID3D11InputLayout,
    constants: ID3D11Buffer,
    blend: ID3D11BlendState,
    rasterizer: ID3D11RasterizerState,
    layers: Vec<Rc<LayerTarget>>,
    resident: HashMap<BlockKey, Resident>,
    uploads: Vec<UploadSlot>,
    generating: Option<Generation>,
    displayed: Option<PublishedChartLayer>,
    readback: Option<Readback>,
    serial: u64,
    validated_scene: Weak<PreparedScene>,
}

impl Renderer {
    pub fn new(device: ID3D11Device, epoch: u64, limits: ResourceLimits) -> Result<Self, GpuError> {
        if epoch == 0
            || limits.upload_slots == 0
            || limits.instances_per_upload == 0
            || limits.draws_per_tick == 0
            || limits.layer_slots < 2
            || limits.instances_per_upload > u32::MAX as usize / size_of::<Instance>()
        {
            return Err(GpuError::InvalidScene);
        }
        let mut vertex = None;
        let mut pixel = None;
        let mut layout = None;
        let mut blend = None;
        let mut rasterizer = None;
        unsafe {
            device.CreateVertexShader(hlsl::VERTEX, None, Some(&mut vertex))?;
            device.CreatePixelShader(hlsl::PIXEL, None, Some(&mut pixel))?;
            let inputs = [
                D3D11_INPUT_ELEMENT_DESC {
                    SemanticName: s!("POSITION"),
                    SemanticIndex: 0,
                    Format: DXGI_FORMAT_R32G32_FLOAT,
                    InputSlotClass: D3D11_INPUT_PER_INSTANCE_DATA,
                    InstanceDataStepRate: 1,
                    ..Default::default()
                },
                D3D11_INPUT_ELEMENT_DESC {
                    SemanticName: s!("POSITION"),
                    SemanticIndex: 1,
                    Format: DXGI_FORMAT_R32G32_FLOAT,
                    AlignedByteOffset: 8,
                    InputSlotClass: D3D11_INPUT_PER_INSTANCE_DATA,
                    InstanceDataStepRate: 1,
                    ..Default::default()
                },
                D3D11_INPUT_ELEMENT_DESC {
                    SemanticName: s!("KIND"),
                    Format: DXGI_FORMAT_R32_UINT,
                    AlignedByteOffset: 16,
                    InputSlotClass: D3D11_INPUT_PER_INSTANCE_DATA,
                    InstanceDataStepRate: 1,
                    ..Default::default()
                },
            ];
            device.CreateInputLayout(&inputs, hlsl::VERTEX, Some(&mut layout))?;
            let mut desc = D3D11_BLEND_DESC::default();
            desc.RenderTarget[0] = D3D11_RENDER_TARGET_BLEND_DESC {
                BlendEnable: true.into(),
                SrcBlend: D3D11_BLEND_ONE,
                DestBlend: D3D11_BLEND_INV_SRC_ALPHA,
                BlendOp: D3D11_BLEND_OP_ADD,
                SrcBlendAlpha: D3D11_BLEND_ONE,
                DestBlendAlpha: D3D11_BLEND_INV_SRC_ALPHA,
                BlendOpAlpha: D3D11_BLEND_OP_ADD,
                RenderTargetWriteMask: D3D11_COLOR_WRITE_ENABLE_ALL.0 as u8,
            };
            device.CreateBlendState(&desc, Some(&mut blend))?;
            device.CreateRasterizerState(
                &D3D11_RASTERIZER_DESC {
                    FillMode: D3D11_FILL_SOLID,
                    CullMode: D3D11_CULL_NONE,
                    DepthClipEnable: true.into(),
                    ScissorEnable: true.into(),
                    ..Default::default()
                },
                Some(&mut rasterizer),
            )?;
        }
        let constants = buffer(
            &device,
            size_of::<Constants>(),
            D3D11_BIND_CONSTANT_BUFFER,
            false,
        )?;
        let mut uploads = Vec::new();
        for _ in 0..limits.upload_slots {
            uploads.push(UploadSlot {
                buffer: buffer(
                    &device,
                    limits.instances_per_upload * size_of::<Instance>(),
                    D3D11_BIND_VERTEX_BUFFER,
                    true,
                )?,
                completion: query(&device)?,
                busy: false,
            });
        }
        let report = ResourceReport {
            upload_slots: limits.upload_slots,
            upload_bytes: limits.upload_slots * limits.instances_per_upload * size_of::<Instance>(),
            ..Default::default()
        };
        Ok(Self {
            device,
            epoch,
            owner: thread::current().id(),
            _thread_bound: PhantomData,
            limits,
            report,
            vertex: required(vertex)?,
            pixel: required(pixel)?,
            layout: required(layout)?,
            constants,
            blend: required(blend)?,
            rasterizer: required(rasterizer)?,
            layers: Vec::new(),
            resident: HashMap::new(),
            uploads,
            generating: None,
            displayed: None,
            readback: None,
            serial: 0,
            validated_scene: Weak::new(),
        })
    }

    fn check(&self, context: &ID3D11DeviceContext) -> Result<(), GpuError> {
        if self.owner != thread::current().id() {
            return Err(GpuError::WrongThread);
        }
        if unsafe { context.GetDevice()? } != self.device {
            return Err(GpuError::WrongDevice);
        }
        unsafe {
            self.device.GetDeviceRemovedReason()?;
        }
        Ok(())
    }

    pub fn epoch(&self) -> u64 {
        self.epoch
    }
    pub fn limits(&self) -> &ResourceLimits {
        &self.limits
    }
    pub fn displayed(&self) -> Option<PublishedChartLayer> {
        self.displayed.clone()
    }
    pub fn is_generating(&self) -> bool {
        self.generating.is_some()
    }

    pub fn report(&self) -> ResourceReport {
        let mut report = self.report.clone();
        report.resident_blocks = self.resident.len();
        report.resident_bytes = self
            .resident
            .values()
            .map(|r| r.capacity * size_of::<Instance>())
            .sum();
        report.layers = self.layers.len();
        report.layer_bytes = self
            .layers
            .iter()
            .map(|l| l.width as usize * l.height as usize * 4)
            .sum();
        report.busy_upload_slots = self.uploads.iter().filter(|s| s.busy).count();
        report.readback_bytes = self.readback.as_ref().map_or(0, |r| {
            r.layer.target.width as usize * r.layer.target.height as usize * 4
        });
        report
    }

    pub fn begin(
        &mut self,
        context: &ID3D11DeviceContext,
        scene: Arc<PreparedScene>,
        spec: RenderSpec,
    ) -> Result<(), GpuError> {
        self.check(context)?;
        let scene_changed = self
            .validated_scene
            .upgrade()
            .is_none_or(|old| !Arc::ptr_eq(&old, &scene));
        if scene_changed {
            scene.validate()?;
            self.report.scene_validation_blocks += scene.blocks.len() as u64;
        }
        spec.validate()?;
        if spec.device_epoch != self.epoch {
            return Err(GpuError::WrongEpoch);
        }
        if self.generating.is_some() {
            return self.busy();
        }
        let bytes = spec.width as usize * spec.height as usize * 4;
        if spec.width > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION
            || spec.height > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION
            || bytes > self.limits.layer_bytes
        {
            return self.busy();
        }
        let available = self.layers.iter().position(|l| {
            Rc::strong_count(l) == 1 && l.width == spec.width && l.height == spec.height
        });
        let target = if let Some(index) = available {
            index
        } else {
            // Only unleased targets are removed; external consumers keep a lease.
            self.layers.retain(|l| {
                Rc::strong_count(l) > 1 || (l.width == spec.width && l.height == spec.height)
            });
            let allocated: usize = self
                .layers
                .iter()
                .map(|l| l.width as usize * l.height as usize * 4)
                .sum();
            if self.layers.len() >= self.limits.layer_slots
                || allocated + bytes > self.limits.layer_bytes
            {
                return self.busy();
            }
            let target = texture(&self.device, spec.width, spec.height, false)?;
            let mut view = None;
            unsafe {
                self.device
                    .CreateRenderTargetView(&target, None, Some(&mut view))?;
            }
            self.layers.push(Rc::new(LayerTarget {
                texture: target,
                view: required(view)?,
                width: spec.width,
                height: spec.height,
            }));
            self.layers.len() - 1
        };
        let completion = query(&self.device)?;
        unsafe {
            context.ClearRenderTargetView(&self.layers[target].view, &[0.0; 4]);
        }
        self.serial += 1;
        // Old session/parameter caches do not consume a second resident budget.
        if scene_changed {
            let keys: HashSet<_> = scene.blocks.iter().map(|block| block.key).collect();
            self.resident.retain(|key, _| keys.contains(key));
            // Weak identity detects Arc::make_mut replacements without retaining old histories.
            self.validated_scene = Arc::downgrade(&scene);
        }
        self.generating = Some(Generation {
            scene,
            spec,
            target,
            block: 0,
            instance: 0,
            submitted: false,
            completion,
        });
        self.report.peak_layer_bytes = self.report.peak_layer_bytes.max(self.report().layer_bytes);
        Ok(())
    }

    fn busy<T>(&mut self) -> Result<T, GpuError> {
        self.report.pressure_returns += 1;
        Err(GpuError::ResourceBusy)
    }

    /// No CPU wait and no cancellation of submitted work.
    pub fn tick(
        &mut self,
        context: &ID3D11DeviceContext,
    ) -> Result<Option<PublishedChartLayer>, GpuError> {
        self.check(context)?;
        for slot in &mut self.uploads {
            if slot.busy && completed(context, &slot.completion)? {
                slot.busy = false;
            }
        }
        let Some(mut work) = self.generating.take() else {
            return Ok(None);
        };
        let result = self.tick_generation(context, &mut work);
        match result {
            Ok(true) => {
                let state = &work.scene.state;
                let layer = PublishedChartLayer {
                    version: LayerVersion {
                        data: state.data_version.ok_or(GpuError::InvalidScene)?,
                        processed_end: state.processed_end,
                        published_end: state.published_end,
                        content_version: state.content_version,
                        parameter_version: state.parameter_version,
                        finished: state.finished,
                        spec: work.spec,
                    },
                    target: self.layers[work.target].clone(),
                };
                self.displayed = Some(layer.clone());
                self.report.completed_layers += 1;
                Ok(Some(layer))
            }
            Ok(false) => {
                self.generating = Some(work);
                Ok(None)
            }
            Err(error) => {
                self.generating = Some(work);
                Err(error)
            }
        }
    }

    fn tick_generation(
        &mut self,
        context: &ID3D11DeviceContext,
        work: &mut Generation,
    ) -> Result<bool, GpuError> {
        if work.submitted {
            return completed(context, &work.completion);
        }
        self.bind(context, work);
        let mut draws = 0;
        while draws < self.limits.draws_per_tick {
            let Some(block) = work.scene.blocks.get(work.block) else {
                break;
            };
            if block.instances.is_empty() || !block.instances.intersects(block.origin, &work.spec) {
                self.report.culled_instances += block.instances.len() as u64;
                work.block += 1;
                work.instance = 0;
                continue;
            }
            let Some(visible) =
                block
                    .instances
                    .visible_range(work.instance, block.origin, &work.spec)
            else {
                self.report.culled_instances += (block.instances.len() - work.instance) as u64;
                work.block += 1;
                work.instance = 0;
                continue;
            };
            self.report.culled_instances += (visible.start - work.instance) as u64;
            work.instance = visible.start;
            let resident = self.resident_buffer(context, block)?;
            let (buffer, count, offset, slot) = if let Some(buffer) = resident {
                (
                    buffer,
                    visible.len(),
                    (visible.start * size_of::<Instance>()) as u32,
                    None,
                )
            } else {
                let Some(index) = self.uploads.iter().position(|s| !s.busy) else {
                    unsafe {
                        context.Flush();
                    }
                    return Ok(false);
                };
                let count = visible.len().min(self.limits.instances_per_upload);
                let data = &block.instances[work.instance..work.instance + count];
                let buffer = self.uploads[index].buffer.clone();
                let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
                unsafe {
                    context.Map(&buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, Some(&mut mapped))?;
                    std::ptr::copy_nonoverlapping(
                        data.as_ptr().cast::<u8>(),
                        mapped.pData.cast(),
                        std::mem::size_of_val(data),
                    );
                    context.Unmap(&buffer, 0);
                }
                self.report.uploaded_bytes += std::mem::size_of_val(data) as u64;
                (buffer, count, 0, Some(index))
            };
            let mut spec = work.spec.clone();
            if spec.style_version == 1 && block.key.kind & (1 << 16) != 0 {
                let alpha = spec.color[3];
                spec.color = if spec.color[0] > 0.8 && spec.color[1] < 0.5 {
                    [0.93, 0.76, 0.34, alpha]
                } else if spec.color[1] > spec.color[0] && spec.color[2] < spec.color[1] {
                    [0.6, 0.73, 0.68, alpha]
                } else {
                    [0.333, 0.678, 0.812, alpha]
                };
            }
            if spec.style_version == 1 && block.key.kind & (1 << 17) != 0 {
                spec.color = [0.55, 0.55, 0.58, 0.58];
            }
            if spec.style_version == 1 && block.key.kind & (1 << 18) != 0 {
                spec.color[3] = 0.95;
            }
            self.set_constants(context, block.origin, &spec);
            let stride = size_of::<Instance>() as u32;
            let buffers = [Some(buffer)];
            unsafe {
                context.IASetVertexBuffers(
                    0,
                    1,
                    Some(buffers.as_ptr()),
                    Some(&stride),
                    Some(&offset),
                );
                context.DrawInstanced(6, count as u32, 0, 0);
                if let Some(index) = slot {
                    context.End(&self.uploads[index].completion);
                    self.uploads[index].busy = true;
                }
            }
            self.report.draw_calls += 1;
            draws += 1;
            self.report.drawn_instances += count as u64;
            work.instance += count;
            if work.instance == block.instances.len() {
                work.block += 1;
                work.instance = 0;
            }
        }
        if work.block == work.scene.blocks.len() {
            unsafe {
                context.End(&work.completion);
                context.Flush();
            }
            work.submitted = true;
        } else {
            unsafe {
                context.Flush();
            }
        }
        Ok(false)
    }

    fn resident_buffer(
        &mut self,
        context: &ID3D11DeviceContext,
        block: &Arc<PreparedBlock>,
    ) -> Result<Option<ID3D11Buffer>, GpuError> {
        // Resident capacity is a separate budget, not the size of a streaming slot.
        let Some(capacity) = block.instances.len().checked_next_power_of_two() else {
            return Err(GpuError::InvalidScene);
        };
        let bytes = capacity * size_of::<Instance>();
        if bytes > self.limits.resident_bytes || self.limits.resident_blocks == 0 {
            return Ok(None);
        }
        if let Some(entry) = self.resident.get_mut(&block.key) {
            if entry.block.content_version == block.content_version
                && Arc::ptr_eq(&entry.block, block)
            {
                self.report.reused_blocks += 1;
                entry.last_used = self.serial;
                return Ok(Some(entry.buffer.clone()));
            }
            if entry.capacity >= block.instances.len() && entry.block.origin == block.origin {
                let prefix = entry
                    .block
                    .instances
                    .iter()
                    .zip(block.instances.iter())
                    .take_while(|(a, b)| a == b)
                    .count();
                let changed = &block.instances[prefix..];
                if !changed.is_empty() {
                    update_instances(context, &entry.buffer, prefix, changed);
                    self.report.uploaded_bytes += std::mem::size_of_val(changed) as u64;
                    self.report.partial_uploads += u64::from(prefix > 0);
                }
                entry.block = block.clone();
                entry.last_used = self.serial;
                return Ok(Some(entry.buffer.clone()));
            }
        }
        self.resident.remove(&block.key);
        while self.resident.len() >= self.limits.resident_blocks
            || self.report().resident_bytes + bytes > self.limits.resident_bytes
        {
            let Some(key) = self
                .resident
                .iter()
                .min_by_key(|(_, r)| r.last_used)
                .map(|(k, _)| *k)
            else {
                return Ok(None);
            };
            self.resident.remove(&key);
        }
        let buffer = buffer(&self.device, bytes, D3D11_BIND_VERTEX_BUFFER, false)?;
        update_instances(context, &buffer, 0, &block.instances);
        self.report.uploaded_bytes += std::mem::size_of_val(&*block.instances) as u64;
        self.resident.insert(
            block.key,
            Resident {
                buffer: buffer.clone(),
                capacity,
                block: block.clone(),
                last_used: self.serial,
            },
        );
        self.report.peak_resident_bytes = self
            .report
            .peak_resident_bytes
            .max(self.report().resident_bytes);
        Ok(Some(buffer))
    }

    fn bind(&self, context: &ID3D11DeviceContext, work: &Generation) {
        let s = &work.spec;
        unsafe {
            context.OMSetRenderTargets(Some(&[Some(self.layers[work.target].view.clone())]), None);
            context.OMSetBlendState(&self.blend, None, u32::MAX);
            context.OMSetDepthStencilState(None, 0);
            context.RSSetState(&self.rasterizer);
            context.RSSetViewports(Some(&[D3D11_VIEWPORT {
                Width: s.width as f32,
                Height: s.height as f32,
                MaxDepth: 1.0,
                ..Default::default()
            }]));
            context.RSSetScissorRects(Some(&[RECT {
                left: s.clip[0] as i32,
                top: s.clip[1] as i32,
                right: s.clip[2] as i32,
                bottom: s.clip[3] as i32,
            }]));
            context.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context.IASetInputLayout(&self.layout);
            context.VSSetShader(&self.vertex, None);
            context.PSSetShader(&self.pixel, None);
            context.GSSetShader(None, None);
            context.HSSetShader(None, None);
            context.DSSetShader(None, None);
            context.VSSetConstantBuffers(0, Some(&[Some(self.constants.clone())]));
            context.PSSetConstantBuffers(0, Some(&[Some(self.constants.clone())]));
        }
    }

    fn set_constants(&self, context: &ID3D11DeviceContext, origin: Point, spec: &RenderSpec) {
        let scale = [
            spec.width as f64 / spec.viewport.width,
            (if spec.y_down { 1.0 } else { -1.0 }) * spec.height as f64 / spec.viewport.height,
        ];
        let constants = Constants {
            scale: [scale[0] as f32, scale[1] as f32],
            offset: [
                (origin.x.relative_to(spec.viewport.origin.x) * scale[0]) as f32,
                ((if spec.y_down { 0.0 } else { spec.height as f64 })
                    + origin.y.relative_to(spec.viewport.origin.y) * scale[1])
                    as f32,
            ],
            extent: [spec.width as f32, spec.height as f32],
            radius: spec.radius,
            half_width: spec.line_width * 0.5,
            color: spec.color,
            clip: spec.clip.map(|v| v as f32),
            reserved: [spec.style_version as f32, 0.55, 0.55, 0.58],
        };
        unsafe {
            context.UpdateSubresource(
                &self.constants,
                0,
                None,
                (&constants as *const Constants).cast(),
                0,
                0,
            );
        }
    }

    pub fn begin_readback(
        &mut self,
        context: &ID3D11DeviceContext,
        layer: PublishedChartLayer,
    ) -> Result<(), GpuError> {
        self.check(context)?;
        if layer.version.spec.device_epoch != self.epoch {
            return Err(GpuError::WrongEpoch);
        }
        if unsafe { layer.target.texture.GetDevice()? } != self.device {
            return Err(GpuError::WrongDevice);
        }
        let bytes = layer.target.width as usize * layer.target.height as usize * 4;
        if self.readback.is_some() || bytes > self.limits.readback_bytes {
            return self.busy();
        }
        let staging = texture(&self.device, layer.target.width, layer.target.height, true)?;
        let completion = query(&self.device)?;
        unsafe {
            context.CopyResource(&staging, &layer.target.texture);
            context.End(&completion);
            context.Flush();
        }
        self.readback = Some(Readback {
            layer,
            staging,
            completion,
        });
        self.report.peak_readback_bytes = self.report.peak_readback_bytes.max(bytes);
        Ok(())
    }

    /// Transfers owned pixels to a worker; image encoding/file I/O is external.
    pub fn poll_readback(
        &mut self,
        context: &ID3D11DeviceContext,
    ) -> Result<Option<ReadbackPixels>, GpuError> {
        self.check(context)?;
        let Some(readback) = &self.readback else {
            return Ok(None);
        };
        if !completed(context, &readback.completion)? {
            return Ok(None);
        }
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        let mapping = unsafe {
            context.Map(
                &readback.staging,
                0,
                D3D11_MAP_READ,
                D3D11_MAP_FLAG_DO_NOT_WAIT.0 as u32,
                Some(&mut mapped),
            )
        };
        if let Err(error) = mapping {
            if error.code().0 as u32 == 0x887a000a {
                return Ok(None);
            }
            return Err(error.into());
        }
        let width = readback.layer.target.width;
        let height = readback.layer.target.height;
        let stride = width as usize * 4;
        let mut bgra = vec![0; stride * height as usize];
        unsafe {
            for y in 0..height as usize {
                std::ptr::copy_nonoverlapping(
                    mapped.pData.cast::<u8>().add(y * mapped.RowPitch as usize),
                    bgra.as_mut_ptr().add(y * stride),
                    stride,
                );
            }
            context.Unmap(&readback.staging, 0);
        }
        let version = readback.layer.version.clone();
        self.readback = None;
        Ok(Some(ReadbackPixels {
            width,
            height,
            bgra,
            version,
        }))
    }
}

struct Readback {
    layer: PublishedChartLayer,
    staging: ID3D11Texture2D,
    completion: ID3D11Query,
}

pub struct ReadbackPixels {
    pub width: u32,
    pub height: u32,
    pub bgra: Vec<u8>,
    pub version: LayerVersion,
}

fn query(device: &ID3D11Device) -> Result<ID3D11Query, GpuError> {
    let mut query = None;
    unsafe {
        device.CreateQuery(
            &D3D11_QUERY_DESC {
                Query: D3D11_QUERY_EVENT,
                MiscFlags: 0,
            },
            Some(&mut query),
        )?;
    }
    required(query)
}

fn completed(context: &ID3D11DeviceContext, query: &ID3D11Query) -> Result<bool, GpuError> {
    // The generated wrapper accepts S_FALSE as Ok; inspect the event BOOL.
    let mut done = BOOL(0);
    unsafe {
        context.GetData(
            query,
            Some((&mut done as *mut BOOL).cast()),
            size_of::<BOOL>() as u32,
            D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
        )?;
    }
    Ok(done.as_bool())
}

fn buffer(
    device: &ID3D11Device,
    bytes: usize,
    bind: D3D11_BIND_FLAG,
    dynamic: bool,
) -> Result<ID3D11Buffer, GpuError> {
    let desc = D3D11_BUFFER_DESC {
        ByteWidth: u32::try_from(bytes).map_err(|_| GpuError::InvalidScene)?,
        Usage: if dynamic {
            D3D11_USAGE_DYNAMIC
        } else {
            D3D11_USAGE_DEFAULT
        },
        BindFlags: bind.0 as u32,
        CPUAccessFlags: if dynamic {
            D3D11_CPU_ACCESS_WRITE.0 as u32
        } else {
            0
        },
        ..Default::default()
    };
    let mut buffer = None;
    unsafe {
        device.CreateBuffer(&desc, None, Some(&mut buffer))?;
    }
    required(buffer)
}

fn update_instances(
    context: &ID3D11DeviceContext,
    buffer: &ID3D11Buffer,
    first: usize,
    instances: &[Instance],
) {
    let region = D3D11_BOX {
        left: (first * size_of::<Instance>()) as u32,
        right: ((first + instances.len()) * size_of::<Instance>()) as u32,
        top: 0,
        bottom: 1,
        front: 0,
        back: 1,
    };
    unsafe {
        context.UpdateSubresource(buffer, 0, Some(&region), instances.as_ptr().cast(), 0, 0);
    }
}

fn texture(
    device: &ID3D11Device,
    width: u32,
    height: u32,
    staging: bool,
) -> Result<ID3D11Texture2D, GpuError> {
    let desc = D3D11_TEXTURE2D_DESC {
        Width: width,
        Height: height,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: if staging {
            D3D11_USAGE_STAGING
        } else {
            D3D11_USAGE_DEFAULT
        },
        BindFlags: if staging {
            0
        } else {
            (D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE).0 as u32
        },
        CPUAccessFlags: if staging {
            D3D11_CPU_ACCESS_READ.0 as u32
        } else {
            0
        },
        ..Default::default()
    };
    let mut texture = None;
    unsafe {
        device.CreateTexture2D(&desc, None, Some(&mut texture))?;
    }
    required(texture)
}
