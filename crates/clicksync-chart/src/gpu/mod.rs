//! CPU upload descriptions and bounded, thread-bound D3D11 resources.
mod instances;
pub use instances::PreparedInstances;
mod scene;
pub use scene::*;

#[cfg(windows)]
mod renderer;
#[cfg(windows)]
pub use renderer::*;
