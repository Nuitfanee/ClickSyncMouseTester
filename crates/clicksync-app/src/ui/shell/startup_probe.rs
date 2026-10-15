use super::*;
use gpui::private::anyhow::ensure;
use std::time::Instant;

pub(in crate::ui) fn run(path: PathBuf) -> Result<(), Box<dyn std::error::Error>> {
    let failure = Arc::new(Mutex::new(Some(
        "startup probe exited before completion".to_string(),
    )));
    let saved = failure.clone();
    let outcome = Arc::new(Mutex::new(WindowSmokeReport::default()));
    let saved_outcome = outcome.clone();
    Application::new().with_assets(design::Assets).run(move |cx| {
        if let Err(error) = design::install_fonts(cx) {
            *failure.lock().unwrap() = Some(error.to_string());
            cx.quit();
            return;
        }
        let bounds = Bounds::centered(None, size(px(DEFAULT_WIDTH as f32), px(DEFAULT_HEIGHT as f32)), cx);
        let opened = open_startup_with(bounds, outcome.clone(), cx, || {
            std::thread::sleep(Duration::from_millis(1500));
            runtime::StartupServices::new(true)
        });
        let startup = match opened {
            Ok(startup) => startup,
            Err(error) => {
                *failure.lock().unwrap() = Some(error.to_string());
                cx.quit();
                return;
            }
        };
        cx.spawn(async move |cx| {
            let result: gpui::Result<()> = async {
                let start = Instant::now();
                let mut loading_presentations = 0;
                let mut queued = false;
                let main = loop {
                    Timer::after(Duration::from_millis(20)).await;
                    let main = startup.update(cx, |shell, window, cx| {
                        if shell.runtime.is_none() {
                            loading_presentations = loading_presentations.max(window.windows_presentation_count());
                            if !queued && loading_presentations > 0 {
                                shell.menu_action(navigation::Action::Language, window, cx);
                                shell.handoff(Page::Performance, window, cx);
                                queued = true;
                            }
                        }
                        shell.main
                    })?;
                    if let Some(main) = main { break main; }
                    ensure!(start.elapsed() < Duration::from_secs(15), "startup initialization deadline");
                };
                ensure!(queued && loading_presentations > 1, "carrier did not animate while services initialized");
                let first = loop {
                    Timer::after(Duration::from_millis(20)).await;
                    let state = main.update(cx, |host, window, _| {
                        (window.windows_presentation_count(), host.menu.coverage,
                         host.runtime.borrow().driver_starts, host.settings.english,
                         unsafe { GetForegroundWindow() } == hwnd(window))
                    })?;
                    if startup.update(cx, |_, _, _| ()).is_err() {
                        break state;
                    }
                    ensure!(start.elapsed() < Duration::from_secs(20), "startup handoff deadline");
                };
                ensure!(first.0 > 0, "carrier removed before main submitted a frame");
                ensure!(first.1 > 0.9 || motion_preference::reduced(), "window construction consumed exit time");
                ensure!(first.2 == 1, "startup created multiple runtime drivers");
                ensure!(first.3 != Settings::default().english, "loading-time language edit was overwritten");
                Timer::after(Duration::from_millis(1100)).await;
                main.update(cx, |host, _, _| {
                    ensure!(!host.menu.visible(), "handoff curtain did not settle");
                    Ok::<_, gpui::private::anyhow::Error>(())
                })??;
                ensure!(outcome.lock().unwrap().error.is_none(), "startup reported an error");
                if let Some(parent) = path.parent() { std::fs::create_dir_all(parent)?; }
                std::fs::write(path, serde_json::to_vec_pretty(&serde_json::json!({
                    "passed": true,
                    "loading_presentations": loading_presentations,
                    "main_presentations_at_carrier_removal": first.0,
                    "exit_coverage_at_carrier_removal": first.1,
                    "driver_starts": first.2,
                    "early_choice_and_preference_preserved": true,
                    "main_foreground_at_carrier_removal": first.4,
                    "boundary": "Real startup/main windows and backend with 1500ms background initialization delay; DXGI submission, not physical scan-out."
                }))?)?;
                Ok(())
            }.await;
            *failure.lock().unwrap() = result.err().map(|error| error.to_string());
            let _ = cx.update(|cx| cx.quit());
        }).detach();
    });
    if let Some(error) = saved.lock().unwrap().take() {
        return Err(saved_outcome
            .lock()
            .unwrap()
            .error
            .clone()
            .unwrap_or(error)
            .into());
    }
    Ok(())
}
