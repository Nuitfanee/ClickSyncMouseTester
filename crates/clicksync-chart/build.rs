#[cfg(windows)]
fn main() {
    use std::{env, fs, path::PathBuf, slice};
    use windows::{
        Win32::Graphics::Direct3D::{
            Fxc::{D3DCOMPILE_ENABLE_STRICTNESS, D3DCOMPILE_OPTIMIZATION_LEVEL3, D3DCompile},
            ID3DBlob, ID3DInclude,
        },
        core::s,
    };
    const SOURCE: &[u8] = include_bytes!("src/hlsl/chart.hlsl");
    println!("cargo:rerun-if-changed=src/hlsl/chart.hlsl");
    let output = PathBuf::from(env::var_os("OUT_DIR").expect("Cargo OUT_DIR"));
    for (entry, target, name) in [
        (s!("chart_vs"), s!("vs_5_0"), "chart.vs.cso"),
        (s!("chart_ps"), s!("ps_5_0"), "chart.ps.cso"),
    ] {
        let mut code: Option<ID3DBlob> = None;
        let mut errors: Option<ID3DBlob> = None;
        let result = unsafe {
            D3DCompile(
                SOURCE.as_ptr().cast(),
                SOURCE.len(),
                s!("clicksync-chart/chart.hlsl"),
                None,
                None::<&ID3DInclude>,
                entry,
                target,
                D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3,
                0,
                &mut code,
                Some(&mut errors),
            )
        };
        if let Err(error) = result {
            let diagnostics = errors.map(|blob| unsafe {
                String::from_utf8_lossy(slice::from_raw_parts(
                    blob.GetBufferPointer().cast(),
                    blob.GetBufferSize(),
                ))
                .into_owned()
            });
            panic!("HLSL compilation failed: {error}\n{diagnostics:?}");
        }
        let code = code.expect("D3DCompile bytecode");
        let bytes = unsafe {
            slice::from_raw_parts(code.GetBufferPointer().cast::<u8>(), code.GetBufferSize())
        };
        fs::write(output.join(name), bytes).expect("write compiled HLSL");
    }
}

#[cfg(not(windows))]
fn main() {}
