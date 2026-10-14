#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod window;

use serde_json::json;
use std::{
    fs,
    path::{Path, PathBuf},
    thread,
    time::{Duration, Instant},
};

fn main() {
    if let Err(error) = run() {
        eprintln!("capture diagnostic failed: {error}");
        std::process::exit(1);
    }
}

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let directory = PathBuf::from(
        std::env::var_os("CLICKSYNC_CAPTURE_DIAGNOSTIC_DIRECTORY")
            .ok_or("CLICKSYNC_CAPTURE_DIAGNOSTIC_DIRECTORY is required")?,
    );
    if !directory.is_dir() {
        return Err("diagnostic output directory does not exist".into());
    }
    let window = window::ActivationWindow::open(
        "ClickSync Worker 诊断",
        "本窗口属于真实采集 worker。\r\n\r\n请连续快速移动鼠标，按提示完成：\r\n1. Worker 前台 20 秒\r\n2. 点击 ClickSync Monitor，让 Worker 后台 20 秒\r\n3. 点击本窗口，让 Worker 再回前台 20 秒\r\n\r\n关闭任一诊断窗口可提前停止并保存记录。",
        280,
        200,
    )?;
    let transport = thread::Builder::new()
        .name("ClickSyncCaptureDiagnosticHost".into())
        .spawn(|| {
            clicksync_capture::worker::run_from_arguments().map_err(|error| error.to_string())
        })?;
    save(
        &directory.join("worker-window.json"),
        &json!({
            "schema_version": 1,
            "process_id": std::process::id(),
            "window_handle": window.handle(),
            "foreground_process_id": window.foreground_process_id(),
            "worker_foreground": window.is_foreground(),
            "entry_point": "clicksync_capture::worker::run_from_arguments",
            "feature": "diagnostic-window",
        }),
    )?;
    let mut visible = true;
    let mut last_update = Instant::now();
    while !transport.is_finished() {
        if visible && !window.pump() {
            fs::write(
                directory.join("stop.request"),
                "User closed the worker diagnostic window.",
            )?;
            visible = false;
        }
        if visible && last_update.elapsed() >= Duration::from_millis(250) {
            let focus = if window.is_foreground() {
                "前台"
            } else {
                "后台"
            };
            if let Some(sample) = fs::read(directory.join("live.json"))
                .ok()
                .and_then(|bytes| serde_json::from_slice::<serde_json::Value>(&bytes).ok())
            {
                let rate = sample["received_records_per_second"]
                    .as_f64()
                    .unwrap_or(0.0);
                let instruction = sample["instruction"].as_str().unwrap_or("等待采集准备");
                if sample["phase"] == "waiting_device" {
                    window.set_title("ClickSync Worker 诊断 | 等待设备");
                    window.set_instructions(instruction);
                } else {
                    window.set_title(&format!(
                        "ClickSync Worker 诊断 | {focus} | {rate:.0} 包/秒"
                    ));
                    window.set_instructions(&format!(
                        "{instruction}\r\n\r\n本窗口属于采集 worker：PID {}。\r\n当前 Worker：{focus}；最近一秒入库：{rate:.0} 包/秒。\r\n\r\n请持续快速画圈或左右移动鼠标。\r\n关闭任一诊断窗口可提前停止并保存记录。",
                        std::process::id()
                    ));
                }
            }
            last_update = Instant::now();
        }
        thread::sleep(Duration::from_millis(5));
    }
    let result = transport
        .join()
        .map_err(|_| "capture diagnostic transport panicked")?;
    save(
        &directory.join("worker-result.json"),
        &json!({"schema_version": 1, "process_id": std::process::id(), "result": result}),
    )?;
    result.map_err(Into::into)
}

fn save(path: &Path, report: &serde_json::Value) -> std::io::Result<()> {
    let temporary = path.with_extension("tmp");
    fs::write(&temporary, serde_json::to_vec_pretty(report)?)?;
    fs::rename(temporary, path)
}
