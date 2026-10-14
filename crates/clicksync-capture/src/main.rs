#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    if let Err(error) = clicksync_capture::worker::run_from_arguments() {
        eprintln!("capture worker failed: {error}");
        std::process::exit(1);
    }
}
