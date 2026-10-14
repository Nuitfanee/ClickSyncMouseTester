//! Startup carrier, native window chrome, and the main-window handoff.
use super::*;
use gpui::private::anyhow::{anyhow, bail};
use gpui::{App, Div, FocusHandle, Stateful, WindowHandle};
use windows::Win32::{
    Foundation::{E_INVALIDARG, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM},
    Graphics::{
        Dwm::*,
        Gdi::{ClientToScreen, ScreenToClient},
    },
    UI::{
        HiDpi::GetDpiForWindow,
        Shell::{DefSubclassProc, RemoveWindowSubclass, SetWindowSubclass},
        WindowsAndMessaging::*,
    },
};

pub(super) const CAPTION_HEIGHT: f32 = 44.0;
pub(super) const RESIZE_BORDER: f32 = 6.0;
const SUBCLASS_ID: usize = 0x4353594e;

pub(super) fn hwnd(window: &Window) -> HWND {
    // CaptureLock already validates the platform handle without retaining a lock.
    HWND(
        crate::capture_lock::CaptureLock::for_window(window)
            .expect("Windows shell handle")
            .window_id() as *mut _,
    )
}

unsafe extern "system" fn chrome_proc(
    handle: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
    id: usize,
    reference: usize,
) -> LRESULT {
    if matches!(message, WM_NCLBUTTONDOWN | WM_NCLBUTTONDBLCLK) && wparam.0 as u32 == HTCAPTION {
        // A caption hit is reserved for the OS move/maximize loop. Dispatching
        // it through GPUI would let track_focus's automatic prevent_default (or
        // the curtain's mouse listener) swallow the native operation.
        return unsafe { DefWindowProcW(handle, message, wparam, lparam) };
    }
    if message == WM_WINDOWPOSCHANGING && reference != 0 {
        let position = unsafe { &mut *(lparam.0 as *mut WINDOWPOS) };
        if !position.flags.contains(SWP_NOMOVE)
            && let Some(origin) = unsafe { &mut *(reference as *mut Option<POINT>) }.take()
        {
            // GPUI splits vertical frame insets evenly when placing a window.
            // Align using the measured top inset before Windows shows this HWND.
            position.x = origin.x;
            position.y = origin.y;
        }
    }
    if message == WM_NCDESTROY {
        let _ = unsafe { RemoveWindowSubclass(handle, Some(chrome_proc), id) };
        if reference != 0 {
            drop(unsafe { Box::from_raw(reference as *mut Option<POINT>) });
        }
    }
    let original = unsafe { DefSubclassProc(handle, message, wparam, lparam) };
    if message != WM_NCHITTEST
        || unsafe { IsZoomed(handle) }.as_bool()
        || unsafe { GetWindowLongPtrW(handle, GWL_STYLE) } as u32 & WS_THICKFRAME.0 == 0
    {
        return original;
    }
    let mut rect = RECT::default();
    let mut point = POINT {
        x: lparam.0 as i16 as i32,
        y: (lparam.0 >> 16) as i16 as i32,
    };
    if unsafe { GetClientRect(handle, &mut rect) }.is_err()
        || !unsafe { ScreenToClient(handle, &mut point) }.as_bool()
    {
        return original;
    }
    let border = (RESIZE_BORDER * unsafe { GetDpiForWindow(handle) } as f32 / 96.0).round() as i32;
    let left = point.x < border;
    let right = point.x >= rect.right - border;
    let top = point.y < border;
    let bottom = point.y >= rect.bottom - border;
    let edge = match (left, right, top, bottom) {
        (true, _, true, _) => HTTOPLEFT,
        (_, true, true, _) => HTTOPRIGHT,
        (true, _, _, true) => HTBOTTOMLEFT,
        (_, true, _, true) => HTBOTTOMRIGHT,
        (true, _, _, _) => HTLEFT,
        (_, true, _, _) => HTRIGHT,
        (_, _, true, _) => HTTOP,
        (_, _, _, true) => HTBOTTOM,
        _ => HTCLIENT,
    };
    if edge != HTCLIENT {
        return LRESULT(edge as isize);
    }
    if matches!(
        original.0 as u32,
        HTLEFT | HTRIGHT | HTTOP | HTBOTTOM | HTTOPLEFT | HTTOPRIGHT | HTBOTTOMLEFT | HTBOTTOMRIGHT
    ) {
        LRESULT(HTCLIENT as isize)
    } else {
        original
    }
}

pub(super) fn install_window_chrome(window: &Window) -> gpui::Result<()> {
    if !unsafe { SetWindowSubclass(hwnd(window), Some(chrome_proc), SUBCLASS_ID, 0) }.as_bool() {
        bail!("could not install the native window chrome");
    }
    Ok(())
}

fn align_initial_client_origin(window: &Window, origin: POINT) -> gpui::Result<()> {
    let handle = hwnd(window);
    let mut outer = RECT::default();
    let mut client = POINT::default();
    unsafe { GetWindowRect(handle, &mut outer) }?;
    if !unsafe { ClientToScreen(handle, &mut client) }.as_bool() {
        bail!("could not measure the main window client origin");
    }
    let pending = Box::new(Some(POINT {
        x: origin.x - (client.x - outer.left),
        y: origin.y - (client.y - outer.top),
    }));
    let reference = Box::into_raw(pending);
    if !unsafe { SetWindowSubclass(handle, Some(chrome_proc), SUBCLASS_ID, reference as usize) }
        .as_bool()
    {
        drop(unsafe { Box::from_raw(reference) });
        bail!("could not align the startup handoff client origin");
    }
    // The subclass consumes the position once; WM_NCDESTROY releases its state.
    Ok(())
}

pub(super) fn toggle_maximize(window: &Window) {
    let command = if unsafe { IsZoomed(hwnd(window)) }.as_bool() {
        SW_RESTORE
    } else {
        SW_MAXIMIZE
    };
    let _ = unsafe { ShowWindowAsync(hwnd(window), command) };
}

pub(super) fn main_options(bounds: Bounds<gpui::Pixels>) -> WindowOptions {
    WindowOptions {
        show: false,
        titlebar: Some(TitlebarOptions {
            title: Some("ClickSync".into()),
            appears_transparent: true,
            ..Default::default()
        }),
        window_bounds: Some(WindowBounds::Windowed(bounds)),
        window_min_size: Some(size(px(MIN_WIDTH as f32), px(MIN_HEIGHT as f32))),
        ..Default::default()
    }
}

fn set_system_transitions_disabled(handle: HWND, disabled: bool) -> gpui::Result<()> {
    // The startup curtain already owns the handoff animation. DWM's normal
    // window show animation would scale/fade that same curtain a second time.
    let disabled = i32::from(disabled);
    unsafe {
        DwmSetWindowAttribute(
            handle,
            DWMWA_TRANSITIONS_FORCEDISABLED,
            &disabled as *const _ as _,
            std::mem::size_of_val(&disabled) as u32,
        )?;
    }
    Ok(())
}

fn configure_startup_chrome(handle: HWND) -> gpui::Result<()> {
    // Transparent client pixels do not suppress DWM's rectangular frame/shadow.
    unsafe {
        DwmSetWindowAttribute(
            handle,
            DWMWA_NCRENDERING_POLICY,
            &DWMNCRP_DISABLED as *const _ as _,
            std::mem::size_of_val(&DWMNCRP_DISABLED) as u32,
        )?;
    }
    for (attribute, value) in [
        (DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND.0 as u32),
        (DWMWA_BORDER_COLOR, 0xffff_fffe_u32), // DWMWA_COLOR_NONE
    ] {
        let result = unsafe {
            DwmSetWindowAttribute(
                handle,
                attribute,
                &value as *const _ as _,
                std::mem::size_of_val(&value) as u32,
            )
        };
        // Windows 10 has no corner/border-color attributes; NC rendering above
        // already removes its frame. Propagate other failures.
        if let Err(error) = result
            && error.code() != E_INVALIDARG
        {
            return Err(error.into());
        }
    }
    Ok(())
}

pub(super) struct StartupShell {
    pub runtime: Rc<RefCell<runtime::AppRuntime>>,
    pub main: Option<WindowHandle<GpuiHost>>,
    outcome: Arc<Mutex<WindowSmokeReport>>,
    error: Option<String>,
    focus: FocusHandle,
    pub transferring: bool,
    pub menu: navigation::Motion,
}

pub(super) fn open_startup(
    bounds: Bounds<gpui::Pixels>,
    outcome: Arc<Mutex<WindowSmokeReport>>,
    cx: &mut App,
) -> gpui::Result<WindowHandle<StartupShell>> {
    let (runtime, error) = runtime::AppRuntime::new(true);
    let quit_runtime = runtime.clone();
    let quit_outcome = outcome.clone();
    cx.on_app_quit(move |_| {
        if let Err(error) = quit_runtime.borrow_mut().shutdown() {
            outcome_error(&quit_outcome, error);
        }
        std::future::ready(())
    })
    .detach();
    runtime::AppRuntime::start_driver(&runtime, cx);
    let opened = cx.open_window(
        WindowOptions {
            show: false,
            titlebar: None,
            kind: gpui::WindowKind::PopUp,
            is_resizable: false,
            is_minimizable: false,
            window_bounds: Some(WindowBounds::Windowed(bounds)),
            window_background: gpui::WindowBackgroundAppearance::Transparent,
            ..Default::default()
        },
        move |window, cx| {
            let shell = cx.new(|cx| StartupShell {
                runtime,
                main: None,
                outcome,
                error,
                focus: cx.focus_handle(),
                transferring: false,
                menu: navigation::Motion::new(true),
            });
            shell.read(cx).focus.focus(window);
            unsafe {
                SetWindowPos(
                    hwnd(window),
                    Some(HWND_TOPMOST),
                    0,
                    0,
                    0,
                    0,
                    SWP_NOMOVE | SWP_NOSIZE,
                )
            }
            .ok();
            shell
        },
    )?;
    opened.update(cx, |_, window, _| {
        configure_startup_chrome(hwnd(window))?;
        install_window_chrome(window)?;
        window.activate_window();
        Ok::<_, gpui::private::anyhow::Error>(())
    })??;
    Ok(opened)
}

impl StartupShell {
    pub fn handoff(&mut self, page: Page, window: &mut Window, cx: &mut Context<Self>) {
        if self.transferring || !self.menu.accepts_input() {
            return;
        }
        let mut client_origin = POINT::default();
        if !unsafe { ClientToScreen(hwnd(window), &mut client_origin) }.as_bool() {
            let error = "could not measure the startup window client origin";
            self.error = Some(error.into());
            outcome_error(&self.outcome, error.into());
            cx.notify();
            return;
        }
        self.menu.close(Some(page));
        let menu = self.menu.transfer_to_main();
        self.transferring = true;
        let runtime = self.runtime.clone();
        let outcome = self.outcome.clone();
        let error = self.error.clone();
        let opened = cx.open_window(main_options(window.bounds()), move |window, cx| {
            let host = cx.new(|cx| {
                cx.observe_window_bounds(window, |host: &mut GpuiHost, window, cx| {
                    host.bounds_changed(window);
                    cx.notify();
                })
                .detach();
                cx.observe_window_activation(window, |host: &mut GpuiHost, window, cx| {
                    host.activation_changed(window);
                    cx.notify();
                })
                .detach();
                cx.on_app_quit(|host: &mut GpuiHost, _| {
                    host.shutdown();
                    std::future::ready(())
                })
                .detach();
                let mut host = GpuiHost::with_runtime(
                    window,
                    outcome.clone(),
                    None,
                    true,
                    runtime.clone(),
                    error,
                    cx,
                );
                host.custom_chrome = true;
                host.page = page;
                host.menu = menu;
                host.focus.focus(window);
                host
            });
            if let Err(error) = install_window_chrome(window) {
                outcome_error(&outcome, error.to_string());
            }
            runtime::AppRuntime::bind_driver(&runtime, window, &host, cx);
            GpuiHost::bind_main_close(window, &host, cx);
            host
        });
        let opened = opened.and_then(|main| {
            main.update(cx, |_, window, _| {
                let configured = set_system_transitions_disabled(hwnd(window), true)
                    .and_then(|_| align_initial_client_origin(window, client_origin));
                if configured.is_ok() {
                    window.activate_window();
                } else {
                    window.remove_window();
                }
                configured
            })??;
            Ok(main)
        });
        let main = match opened {
            Ok(main) => main,
            Err(error) => {
                self.transferring = false;
                self.menu.show(self.menu.brand.clone());
                self.error = Some(error.to_string());
                outcome_error(&self.outcome, error.to_string());
                cx.notify();
                return;
            }
        };
        self.main = Some(main);
        let startup = window.window_handle();
        let outcome = self.outcome.clone();
        window
            .spawn(cx, async move |cx| {
                let transition: gpui::Result<()> = async {
                    for _ in 0..250 {
                        Timer::after(Duration::from_millis(20)).await;
                        let ready = main.update(cx, |host, window, _| {
                            host.focus.focus(window);
                            window.is_window_active()
                                && unsafe { GetForegroundWindow() } == hwnd(window)
                        })?;
                        if ready {
                            main.update(cx, |_, window, _| {
                                set_system_transitions_disabled(hwnd(window), false)
                            })??;
                            startup.update(cx, |_, window, _| window.remove_window())?;
                            return Ok(());
                        }
                    }
                    Err(anyhow!("startup-to-main foreground handoff timed out"))
                }
                .await;
                if let Err(error) = transition {
                    outcome_error(&outcome, error.to_string());
                    let _ = cx.update(|_, cx| cx.quit());
                }
            })
            .detach();
    }
}

impl StartupShell {
    fn menu_action(
        &mut self,
        action: navigation::Action,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match action {
            navigation::Action::Close => self.handoff(Page::Polling, window, cx),
            navigation::Action::Select(page) => self.handoff(page, window, cx),
            navigation::Action::Language | navigation::Action::Theme
                if self.menu.accepts_input() =>
            {
                let mut runtime = self.runtime.borrow_mut();
                let mut settings = runtime.settings.clone();
                if matches!(action, navigation::Action::Language) {
                    settings.english = !settings.english;
                } else {
                    settings.dark = !settings.dark;
                }
                runtime.update_settings(settings, true);
            }
            navigation::Action::Github if self.menu.accepts_input() => {
                cx.open_url("https://github.com/Nuitfanee/ClickSyncMouseTester")
            }
            navigation::Action::Hover(index, hovered) => self.menu.entry_hover(index, hovered),
            navigation::Action::Press(Some(page)) => self.handoff(page, window, cx),
            navigation::Action::Press(None) => self.menu.pressed = None,
            navigation::Action::HeaderHover(index, hovered) => {
                self.menu.header_hover(index, hovered)
            }
            navigation::Action::Pointer(position) => self.menu.pointer(position),
            _ => {}
        }
        cx.notify();
    }
}
impl Render for StartupShell {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let settings = self.runtime.borrow().settings.clone();
        let bounds = window.viewport_size();
        let geometry = navigation::geometry(f32::from(bounds.width), f32::from(bounds.height));
        let opening = self.menu.phase == navigation::Phase::Opening;
        if self
            .menu
            .update(std::time::Instant::now(), geometry, &settings)
        {
            window.request_animation_frame();
        }
        if opening && self.menu.interactive() {
            let _ = unsafe {
                SetWindowPos(
                    hwnd(window),
                    Some(HWND_NOTOPMOST),
                    0,
                    0,
                    0,
                    0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
                )
            };
        }
        div()
            .id("startup-root")
            .relative()
            .size_full()
            .track_focus(&self.focus)
            .on_key_down(
                cx.listener(|shell, event: &gpui::KeyDownEvent, window, cx| {
                    if !event.is_held && event.keystroke.key == "enter" && shell.menu.interactive()
                    {
                        shell.handoff(Page::Polling, window, cx);
                    }
                    cx.stop_propagation();
                }),
            )
            .child(navigation::menu(
                &self.menu,
                &settings,
                Page::Polling,
                geometry,
                Self::menu_action,
                cx,
            ))
            .when_some(self.error.clone(), |element, error| element.child(error))
    }
}

pub(super) fn frame(
    content: Stateful<Div>,
    host: &GpuiHost,
    window: &Window,
    cx: &mut Context<GpuiHost>,
) -> Stateful<Div> {
    let palette = design::Palette::for_theme(host.settings.dark);
    let button = |id: &'static str, label: &'static str, width: f32| {
        div()
            .id(id)
            .w(px(width))
            .h(px(CAPTION_HEIGHT))
            .flex()
            .items_center()
            .justify_center()
            .cursor_pointer()
            .hover(move |style| {
                if id == "window-close" {
                    style.bg(rgb(0xc42b3b)).text_color(rgb(0xffffff))
                } else {
                    style.bg(rgb(palette.hover))
                }
            })
            .active(|style| style.opacity(0.75))
            .when(id.starts_with("window-"), |button| {
                button.child(design::window_icon(label).text_color(rgb(palette.text)))
            })
            .when(!id.starts_with("window-"), |button| button.child(label))
    };
    let header = div()
        .id("window-caption")
        .flex()
        .w_full()
        .h(px(CAPTION_HEIGHT))
        .flex_shrink_0()
        .border_b_1()
        .border_color(rgb(palette.hairline))
        .child(
            button("navigation-toggle", "☰", 48.0)
                .bg(rgb(palette.curtain))
                .text_color(rgb(0xffffff))
                .opacity(if host.can_navigate() { 1.0 } else { 0.2 })
                .on_click(cx.listener(|host, _, window, cx| {
                    if host.can_navigate() {
                        if host.edit.is_some() {
                            host.commit_edit(cx);
                            host.edit = None;
                        }
                        host.open_menu();
                        host.focus.focus(window);
                    }
                    cx.notify();
                })),
        )
        .child(
            div()
                .flex_1()
                .h_full()
                .flex()
                .items_center()
                .gap(px(18.0))
                .px(px(18.0))
                .window_control_area(gpui::WindowControlArea::Drag)
                .child(div().w(px(10.0)).h(px(10.0)).bg(rgb(palette.accent)))
                .child(host.page.label(host.settings.english))
                .child(
                    div()
                        .flex()
                        .items_center()
                        .gap(px(10.0))
                        .child(
                            div()
                                .text_color(rgb(palette.muted))
                                .font_family(design::DISPLAY_FONT)
                                .text_size(px(11.0))
                                .child(concat!("BY NUITFANEE v", env!("CARGO_PKG_VERSION"))),
                        )
                        .child(
                            div()
                                .id("header-github")
                                .size(px(22.0))
                                .flex_shrink_0()
                                .flex()
                                .items_center()
                                .justify_center()
                                .cursor_pointer()
                                .window_control_area(gpui::WindowControlArea::Client)
                                .hover(|style| style.opacity(0.7))
                                .on_click(|_, _, cx| {
                                    cx.open_url(
                                        "https://github.com/Nuitfanee/ClickSyncMouseTester",
                                    );
                                    cx.stop_propagation();
                                })
                                .child(
                                    gpui::svg()
                                        .path("brand/github.svg")
                                        .size(px(16.0))
                                        .text_color(rgb(palette.text)),
                                ),
                        ),
                ),
        )
        .child(
            button(
                "header-language",
                if host.settings.english {
                    "EN / 简中"
                } else {
                    "简中 / EN"
                },
                105.0,
            )
            .on_click(cx.listener(|host, _, _, cx| {
                host.settings.english = !host.settings.english;
                host.save_settings();
                cx.notify();
            })),
        )
        .child(
            button(
                "header-theme",
                if host.settings.dark {
                    host.text("深色 / 浅色", "DARK / LIGHT")
                } else {
                    host.text("浅色 / 深色", "LIGHT / DARK")
                },
                132.0,
            )
            .on_click(cx.listener(|host, _, _, cx| {
                host.settings.dark = !host.settings.dark;
                host.save_settings();
                cx.notify();
            })),
        )
        .child(
            button("window-minimize", "−", 40.0).on_click(|_, window, _| window.minimize_window()),
        )
        .child(
            button(
                "window-maximize",
                if window.is_maximized() { "▢" } else { "□" },
                40.0,
            )
            .on_click(|_, window, _| toggle_maximize(window)),
        )
        .child(
            button("window-close", "×", 44.0).on_click(cx.listener(|host, _, window, cx| {
                host.close_performance_child(cx);
                host.shutdown();
                window.remove_window();
            })),
        );
    let mut root = div()
        .id("product-root")
        .relative()
        .size_full()
        .flex()
        .flex_col()
        .bg(rgb(palette.background))
        .text_color(rgb(palette.text))
        .font_family(design::Typography::body_font(host.settings.english))
        .text_size(px(12.0))
        .track_focus(&host.focus)
        .on_key_down(cx.listener(|host, event, window, cx| host.keyboard(event, window, cx)))
        .child(header)
        .child(
            div()
                .flex_1()
                .min_h_0()
                .w_full()
                .flex()
                .flex_col()
                .overflow_hidden()
                .child(content),
        );
    if host.menu.visible() {
        let bounds = window.viewport_size();
        let geometry = navigation::geometry(f32::from(bounds.width), f32::from(bounds.height));
        root = root.child(navigation::menu(
            &host.menu,
            &host.settings,
            host.page,
            geometry,
            GpuiHost::menu_action,
            cx,
        ));
    }
    root
}
