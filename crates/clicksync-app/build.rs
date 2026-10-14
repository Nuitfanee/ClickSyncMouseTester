#[cfg(windows)]
fn main() {
    use std::{env, fs, path::PathBuf};

    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=Cargo.toml");
    println!("cargo:rerun-if-env-changed=CLICKSYNC_PRODUCT_VERSION");
    let version =
        env::var("CLICKSYNC_PRODUCT_VERSION").unwrap_or_else(|_| env!("CARGO_PKG_VERSION").into());
    let components: Vec<u16> = version
        .split('.')
        .map(|part| part.parse().expect("numeric Windows version component"))
        .collect();
    assert!(
        (2..=4).contains(&components.len()),
        "Windows product version requires two to four numeric components"
    );
    let mut numbers = [0; 4];
    numbers[..components.len()].copy_from_slice(&components);
    let numeric = numbers.map(|part| part.to_string()).join(",");
    let file_version = numbers.map(|part| part.to_string()).join(".");
    let icon = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").expect("Cargo manifest directory"))
        .join("../../assets/icons/Application.ico");
    println!("cargo:rerun-if-changed={}", icon.display());
    let resource = PathBuf::from(env::var_os("OUT_DIR").expect("Cargo output directory"))
        .join("clicksync-app.rc");
    fs::write(
        &resource,
        format!(
            r#"
1 ICON "{}"
1 VERSIONINFO
FILEVERSION {numeric}
PRODUCTVERSION {numeric}
FILEFLAGSMASK 0x3fL
FILEFLAGS 0x0L
FILEOS 0x40004L
FILETYPE 0x1L
FILESUBTYPE 0x0L
BEGIN
    BLOCK "StringFileInfo"
    BEGIN
        BLOCK "040904B0"
        BEGIN
            VALUE "FileDescription", "ClickSync Mouse Tester\0"
            VALUE "FileVersion", "{file_version}\0"
            VALUE "InternalName", "clicksync-app\0"
            VALUE "OriginalFilename", "clicksync-app.exe\0"
            VALUE "ProductName", "ClickSyncMouseTester\0"
            VALUE "ProductVersion", "{version}\0"
        END
    END
    BLOCK "VarFileInfo"
    BEGIN
        VALUE "Translation", 0x0409, 1200
    END
END
"#,
            icon.to_string_lossy().replace('\\', "/")
        ),
    )
    .expect("write Windows product resources");
    embed_resource::compile_for(&resource, ["clicksync-app"], embed_resource::NONE)
        .manifest_required()
        .expect("compile Windows product resources");
}

#[cfg(not(windows))]
fn main() {}
