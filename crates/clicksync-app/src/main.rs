#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    if let Err(error) = clicksync_app::ui::run() {
        eprintln!("ClickSync failed: {error}");
        std::process::exit(1);
    }
}
