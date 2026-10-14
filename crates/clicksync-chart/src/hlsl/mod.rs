use serde::Serialize;
use sha2::{Digest, Sha256};

pub const SOURCE: &str = include_str!("chart.hlsl");
#[cfg(windows)]
pub const VERTEX: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/chart.vs.cso"));
#[cfg(windows)]
pub const PIXEL: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/chart.ps.cso"));

#[derive(Clone, Debug, Serialize)]
pub struct ShaderIdentity {
    pub compiler: &'static str,
    pub source_sha256: String,
    pub vertex_sha256: String,
    pub pixel_sha256: String,
    pub entries: [&'static str; 2],
    pub targets: [&'static str; 2],
    pub flags: &'static str,
    pub macros: [&'static str; 0],
    pub instance_stride: usize,
    pub constants_bytes: usize,
}

pub fn sha256(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

#[cfg(windows)]
pub fn identity() -> ShaderIdentity {
    ShaderIdentity {
        compiler: "d3dcompiler_47!D3DCompile",
        source_sha256: sha256(SOURCE.as_bytes()),
        vertex_sha256: sha256(VERTEX),
        pixel_sha256: sha256(PIXEL),
        entries: ["chart_vs", "chart_ps"],
        targets: ["vs_5_0", "ps_5_0"],
        flags: "ENABLE_STRICTNESS|OPTIMIZATION_LEVEL3",
        macros: [],
        instance_stride: std::mem::size_of::<crate::gpu::Instance>(),
        constants_bytes: 80,
    }
}
