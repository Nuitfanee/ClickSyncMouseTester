use super::*;

impl GpuiHost {
    pub(super) fn open_performance_window(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(child) = self.workspace.child {
            let _ = child.update(cx, |_, window, _| window.activate_window());
            return;
        }
        if self.workspace.opening
            || self.lock.locked()
            || self.runtime.borrow().pending_action().is_some()
        {
            return;
        }
        self.commit_edit(cx);
        if self.control_error.is_some() {
            cx.notify();
            return;
        }
        let required = if self.workspace.dual { 2 } else { 1 };
        let runtime = self.runtime.borrow();
        let catalog = &runtime.device_catalog;
        if self.selected.len() != required || !catalog.accepts_selection(&self.selected) {
            self.control_error = Some(
                self.text(
                    "请选择可用且不同的鼠标设备。",
                    "Select available, distinct mouse devices.",
                )
                .into(),
            );
            cx.notify();
            return;
        }
        drop(runtime);
        self.workspace.opening = true;
        self.device_menu = false;
        self.runtime.borrow_mut().invalidate(self.receiver);
        self.runtime
            .borrow_mut()
            .update_settings(self.settings.clone(), self.persist_settings);
        let runtime = self.runtime.clone();
        let outcome = self.outcome.clone();
        let selected = self.selected.clone();
        let imported = self.imported.clone();
        let comparison = self.comparison.clone();
        let workspace = self.workspace.for_window_handoff();
        let import_identity = self.import_identity;
        let page_generation = self.page_generation;
        let logical = self.logical;
        let view = self.view;
        let parameter_version = self.parameter_version;
        let persist_settings = self.persist_settings;
        let main = (window.window_handle(), cx.entity().downgrade());
        let display_id = window.display(cx).map(|display| display.id());
        let opened = cx.open_window(
            WindowOptions {
                titlebar: Some(TitlebarOptions {
                    title: Some(self.text("鼠标性能图表", "Mouse performance charts").into()),
                    appears_transparent: true,
                    ..Default::default()
                }),
                window_bounds: Some(WindowBounds::Maximized(window.window_bounds().get_bounds())),
                window_min_size: Some(size(px(MIN_WIDTH as f32), px(MIN_HEIGHT as f32))),
                display_id,
                ..Default::default()
            },
            move |window, cx| {
                let child = cx.new(|cx| {
                    cx.observe_window_activation(window, |host: &mut GpuiHost, window, cx| {
                        host.activation_changed(window);
                        cx.notify();
                    })
                    .detach();
                    cx.observe_window_bounds(window, |host: &mut GpuiHost, window, cx| {
                        host.bounds_changed(window);
                        cx.notify();
                    })
                    .detach();
                    // Capture controls can disappear or lose focusability after a command.
                    cx.on_focus_lost(window, |host: &mut GpuiHost, window, _| {
                        if !host.workspace.closing
                            && host.runtime.borrow().accepts_receiver(host.receiver)
                        {
                            host.focus.focus(window);
                        }
                    })
                    .detach();
                    let mut host = GpuiHost::with_runtime(
                        window,
                        outcome,
                        None,
                        true,
                        runtime.clone(),
                        None,
                        cx,
                    );
                    host.owns_runtime = false;
                    host.persist_settings = persist_settings;
                    host.page = Page::Performance;
                    host.custom_chrome = false;
                    host.selected = selected;
                    host.selection_initialized = true;
                    host.imported = imported;
                    host.comparison = comparison;
                    host.workspace = workspace;
                    host.import_identity = import_identity;
                    host.page_generation = page_generation;
                    host.logical = logical;
                    host.view = view;
                    host.parameter_version = parameter_version;
                    host.workspace.chart_window = true;
                    host.workspace.dual = host.selected.len() == 2;
                    host.workspace.return_to = Some(main);
                    if let Some(backend) = &host.runtime.borrow().backend {
                        backend.set_capture_permitted(false);
                    }
                    host.focus.focus(window);
                    host
                });
                if let Err(error) = shell::install_window_chrome(window) {
                    outcome_error(&child.read(cx).outcome, error.to_string());
                }
                let closing = child.downgrade();
                window.on_window_should_close(cx, move |window, cx| {
                    let _ =
                        closing.update(cx, |host, cx| host.return_to_performance_setup(window, cx));
                    true
                });
                runtime::AppRuntime::bind_driver(&runtime, window, &child, cx);
                window.activate_window();
                child
            },
        );
        self.workspace.opening = false;
        match opened {
            Ok(child) => {
                let diagnostics = self.capture_diagnostics.take();
                let _ = child.update(cx, |host, _, _| {
                    host.capture_diagnostics = diagnostics;
                    host.observe_capture();
                });
                self.workspace.child = Some(child);
                let _ = unsafe {
                    windows::Win32::UI::WindowsAndMessaging::ShowWindowAsync(
                        shell::hwnd(window),
                        windows::Win32::UI::WindowsAndMessaging::SW_HIDE,
                    )
                };
            }
            Err(error) => {
                self.receiver = self.runtime.borrow_mut().attach();
                runtime::AppRuntime::bind_driver(&self.runtime, window, &cx.entity(), cx);
                self.control_error = Some(error.to_string());
            }
        }
        cx.notify();
    }

    pub(super) fn return_to_performance_setup(
        &mut self,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.workspace.closing {
            return;
        }
        if self.lock.locked()
            || self
                .runtime
                .borrow()
                .status()
                .is_some_and(|status| status.phase == crate::backend::CapturePhase::Collecting)
            || self
                .runtime
                .borrow()
                .pending_action()
                .is_some_and(|action| matches!(action, "start" | "resume"))
        {
            self.capture_command("pause");
        }
        self.save_detail_view();
        let workspace = self.workspace.for_window_handoff();
        self.workspace.closing = true;
        self.lock.release();
        self.workspace.retire(&self.runtime.borrow());
        self.release_surface();
        self.runtime.borrow_mut().invalidate(self.receiver);
        if self.runtime.borrow().is_closed() {
            self.workspace.return_to = None;
            return;
        }
        if let Some((main, parent)) = self.workspace.return_to.take()
            && let Some(parent) = parent.upgrade()
        {
            let runtime = self.runtime.clone();
            let diagnostics = self.capture_diagnostics.take();
            let _ = main.update(cx, |_, window, cx| {
                parent.update(cx, |host, cx| {
                    host.workspace = workspace;
                    host.settings = runtime.borrow().settings.clone();
                    host.page_generation = self.page_generation;
                    host.logical = self.logical;
                    host.view = self.view;
                    host.parameter_version = self.parameter_version;
                    host.receiver = runtime.borrow_mut().attach_after_handoff(self.receiver);
                    host.capture_diagnostics = diagnostics;
                    host.imported = self.imported.take();
                    host.comparison = self.comparison.take();
                    host.import_identity = self.import_identity;
                    host.control_error = self.control_error.take();
                    host.was_active = window.is_window_active();
                    host.observe_capture();
                    host.focus.focus(window);
                    cx.notify();
                });
                runtime::AppRuntime::bind_driver(&runtime, window, &parent, cx);
                let _ = unsafe {
                    windows::Win32::UI::WindowsAndMessaging::ShowWindowAsync(
                        shell::hwnd(window),
                        windows::Win32::UI::WindowsAndMessaging::SW_SHOW,
                    )
                };
                window.activate_window();
            });
        }
    }

    pub(super) fn close_performance_child(&mut self, cx: &mut Context<Self>) {
        if let Some(child) = self.workspace.child.take()
            && let Ok(diagnostics) = child.update(cx, |host, window, cx| {
                host.commit_edit(cx);
                host.lock.release();
                host.workspace.return_to = None;
                host.workspace.closing = true;
                host.workspace.retire(&host.runtime.borrow());
                host.release_surface();
                let diagnostics = host.capture_diagnostics.take();
                window.remove_window();
                diagnostics
            })
        {
            self.capture_diagnostics = diagnostics;
        }
    }

    pub(super) fn bind_main_close(
        window: &mut Window,
        host: &gpui::Entity<Self>,
        cx: &mut gpui::App,
    ) {
        let closing = host.downgrade();
        window.on_window_should_close(cx, move |_, cx| {
            let _ = closing.update(cx, |host, cx| {
                host.close_performance_child(cx);
                host.shutdown();
            });
            true
        });
    }

    pub(super) fn render_performance_setup(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> gpui::Stateful<gpui::Div> {
        let palette = design::Palette::for_theme(self.settings.dark);
        let pending = self.runtime.borrow().pending_action().is_some()
            || self.workspace.opening
            || self.workspace.child.is_some();
        let display_size =
            ((f32::from(window.viewport_size().width) - 136.0) / 8.0).clamp(96.0, 124.0);
        let mut modes = div().flex().flex_col().gap(px(10.0));
        for (dual, zh, en) in [
            (false, "单鼠标", "Single mouse"),
            (true, "双鼠标对比", "Compare two mice"),
        ] {
            let selected = self.workspace.dual == dual;
            modes = modes.child(
                div()
                    .id(if dual { "mode-dual" } else { "mode-single" })
                    .h(px(56.0))
                    .flex()
                    .items_center()
                    .gap(px(14.0))
                    .font_weight(gpui::FontWeight::BOLD)
                    .text_size(px(28.0))
                    .text_color(rgb(if selected {
                        palette.text
                    } else {
                        palette.muted
                    }))
                    .when(!pending, |item| {
                        item.cursor_pointer()
                            .hover(move |style| style.text_color(rgb(palette.accent)))
                            .active(|style| style.opacity(0.65))
                    })
                    .when(pending, |item| item.opacity(0.4))
                    .child(
                        div()
                            .w(px(22.0))
                            .flex_shrink_0()
                            .font_family(design::DISPLAY_FONT)
                            .text_size(px(22.0))
                            .text_color(rgb(palette.accent))
                            .child(if selected { "→" } else { "" }),
                    )
                    .child(self.text(zh, en))
                    .on_click(cx.listener(move |host, _, _, cx| {
                        if pending || host.workspace.dual == dual {
                            return;
                        }
                        host.workspace.dual = dual;
                        host.device_menu = false;
                        if !dual {
                            host.selected.truncate(1);
                        }
                        cx.notify();
                    }))
                    .map(|item| item),
            );
        }
        let catalog_choices = self.runtime.borrow().device_catalog.choices().to_vec();
        let no_devices = catalog_choices.is_empty();
        // Keep both device slots in the centered composition when B is absent.
        let mut devices = div()
            .h(px(190.0))
            .flex_shrink_0()
            .flex()
            .flex_col()
            .gap(px(36.0));
        for side in 0..if self.workspace.dual { 2 } else { 1 } {
            let anchor = device_picker::Anchor::default();
            let name = self.selected.get(side).and_then(|key| {
                self.runtime
                    .borrow()
                    .device_catalog
                    .label(*key)
                    .map(str::to_owned)
            });
            let index = if side == 0 { 0 } else { 6 };
            let value = self.edit_value(index);
            let editor = self.editors[index].clone();
            editor.update(cx, |field, cx| {
                field.compact = false;
                field.raised = false;
                field.enabled = !pending;
                field.palette = palette;
                if self.edit != Some(index) && field.content.as_ref() != value {
                    field.set_value(&value, cx);
                }
            });
            let mut choices = div().flex().flex_col().gap(px(4.0));
            for choice in &catalog_choices {
                let key = choice.key;
                if !self
                    .runtime
                    .borrow()
                    .device_catalog
                    .can_choose(key, self.selected.get(1 - side).copied())
                {
                    continue;
                }
                choices = choices.child(
                    device_picker::option(
                        format!("setup-choice-{side}-{key}"),
                        choice,
                        self.selected.get(side) == Some(&key),
                        palette,
                        self.settings.english,
                    )
                    .border_0()
                    .on_click(cx.listener(move |host, _, _, cx| {
                        cx.stop_propagation();
                        host.select_workspace_device(side, key);
                        host.device_menu = false;
                        cx.notify();
                    })),
                );
            }
            if no_devices {
                choices = choices.child(
                    div()
                        .p(px(12.0))
                        .text_size(px(14.0))
                        .line_height(px(22.0))
                        .text_color(rgb(palette.muted))
                        .child(self.text(
                            "请连接并移动鼠标，未出现时重新检测。",
                            "Connect and move a mouse. Rescan if it does not appear.",
                        )),
                );
            }
            devices = devices.child(
                div()
                    .h(px(77.0))
                    .flex_shrink_0()
                    .flex()
                    .items_start()
                    .gap(px(48.0))
                    .child(
                        div()
                            .relative()
                            .flex_1()
                            .min_w_0()
                            .flex()
                            .flex_col()
                            .gap(px(12.0))
                            .child(setup_label(
                                format!(
                                    "{} {}",
                                    self.text("鼠标", "MOUSE"),
                                    if side == 0 { "A" } else { "B" }
                                ),
                                palette,
                            ))
                            .child(
                                device_picker::text_trigger(
                                    format!("setup-device-{side}"),
                                    name.as_deref(),
                                    palette,
                                    self.settings.english,
                                    !pending,
                                    &anchor,
                                )
                                .on_click(cx.listener(
                                    move |host, _, _, cx| {
                                        if pending {
                                            return;
                                        }
                                        host.device_menu =
                                            !host.device_menu || host.workspace.device_side != side;
                                        host.workspace.device_side = side;
                                        cx.notify();
                                    },
                                )),
                            )
                            .when(
                                self.device_menu && self.workspace.device_side == side,
                                |field| {
                                    field.child(
                                        gpui::deferred(
                                            div()
                                                .id(("setup-menu", side))
                                                .absolute()
                                                .top(px(82.0))
                                                .w_full()
                                                .flex()
                                                .flex_col()
                                                .border_1()
                                                .border_color(rgb(palette.hairline))
                                                .bg(rgb(palette.panel))
                                                .occlude()
                                                .child(
                                                    device_picker::popup(
                                                        format!("setup-popup-{side}"),
                                                        palette,
                                                        anchor,
                                                        cx.listener(|host, _, _, cx| {
                                                            host.device_menu = false;
                                                            cx.notify();
                                                        }),
                                                    )
                                                    .relative()
                                                    .top_0()
                                                    .border_0()
                                                    .max_h(px(if side == 0 {
                                                        280.0
                                                    } else {
                                                        192.0
                                                    }))
                                                    .child(choices),
                                                )
                                                .when(catalog_choices.len() > 3, |menu| {
                                                    menu.child(
                                                        div()
                                                            .h(px(30.0))
                                                            .px(px(12.0))
                                                            .flex()
                                                            .items_center()
                                                            .text_size(px(13.0))
                                                            .text_color(rgb(palette.muted))
                                                            .child(self.text(
                                                                "滚动查看全部设备",
                                                                "Scroll to see all devices",
                                                            )),
                                                    )
                                                }),
                                        )
                                        .with_priority(2),
                                    )
                                },
                            ),
                    )
                    .child(
                        div()
                            .w(px(136.0))
                            .flex_shrink_0()
                            .flex()
                            .flex_col()
                            .gap(px(12.0))
                            .child(setup_label("DPI", palette))
                            .child(editor)
                            .map(|field| field),
                    )
                    .map(|row| row),
            );
        }
        let ready = {
            let runtime = self.runtime.borrow();
            !pending
                && self.selected.len() == if self.workspace.dual { 2 } else { 1 }
                && runtime.device_catalog.accepts_selection(&self.selected)
        };
        let next_button = design::button(
            "performance-next",
            self.text("进入图表  →", "OPEN CHARTS  →"),
            palette,
            self.settings.english,
            true,
            ready,
        )
        .w(px(184.0))
        .h(px(44.0))
        .flex()
        .items_center()
        .justify_center()
        .on_click(cx.listener(move |host, _, window, cx| {
            if ready {
                host.open_performance_window(window, cx);
            }
        }));
        let refresh = div()
            .id("setup-refresh")
            .flex()
            .items_center()
            .gap(px(8.0))
            .h(px(36.0))
            .text_size(px(16.0))
            .when(!pending, |action| {
                action
                    .cursor_pointer()
                    .hover(move |style| style.text_color(rgb(palette.accent)))
                    .active(|style| style.opacity(0.65))
            })
            .when(pending, |action| action.opacity(0.4))
            .child(
                div()
                    .font_family(design::DISPLAY_FONT)
                    .text_size(px(20.0))
                    .text_color(rgb(palette.accent))
                    .child("+"),
            )
            .child(self.text("重新检测设备", "Scan devices"))
            .on_click(cx.listener(move |host, _, _, cx| {
                if !pending
                    && let Err(error) = host.runtime.borrow_mut().enqueue_control(
                        host.receiver,
                        "discover",
                        clicksync_core::protocol::ControlCommand::RescanDevices,
                    )
                {
                    host.control_error = Some(error);
                }
                cx.notify();
            }));
        let status = if let Some(error) = &self.control_error {
            error.clone()
        } else if pending {
            self.text("正在准备设备…", "Preparing devices…").into()
        } else if no_devices {
            self.text(
                "连接并移动鼠标后即可继续。",
                "Connect and move a mouse to continue.",
            )
            .into()
        } else if ready {
            String::new()
        } else if self.workspace.dual {
            self.text(
                "请选择两只不同的鼠标。",
                "Select two distinct mice to continue.",
            )
            .into()
        } else {
            self.text("请选择一只鼠标。", "Select a mouse to continue.")
                .into()
        };
        div().id("performance-setup").size_full().flex().flex_col()
            .px(px(68.0)).pt(px(32.0)).pb(px(20.0))
            .bg(rgb(palette.background)).text_color(rgb(palette.text))
            .font_family(design::Typography::body_font(self.settings.english))
            .child(div().flex_shrink_0().font_family(design::DISPLAY_FONT)
                .font_weight(gpui::FontWeight::BOLD).text_size(px(display_size))
                .line_height(px(display_size * 1.12)).child("PERFORMANCE")
                )
            .child(
                div().flex_1().min_h_0().flex().items_center()
                    .child(div().w_full().flex().items_start().gap(px(88.0))
                    .child(
                        div().w(px(320.0)).flex_shrink_0().flex().flex_col().gap(px(24.0))
                            .child(setup_label(self.text("测试模式", "TEST MODE"), palette))
                            .child(modes)
                            .child(div().h(px(48.0)).flex_shrink_0().pl(px(36.0))
                                .text_size(px(14.0)).line_height(px(24.0))
                                .text_color(rgb(palette.muted))
                                .child(if self.workspace.dual {
                                    ""
                                } else {
                                    self.text("记录移动轨迹、速度与报告时间间隔。",
                                        "Record trajectory, speed and report intervals.")
                                })
                                ),
                    )
                    .child(
                        div().flex_1().min_w_0().flex().flex_col().gap(px(24.0))
                            .child(devices)
                            .child(div().min_h(px(44.0)).flex_shrink_0()
                                .text_size(px(13.0)).line_height(px(22.0))
                                .text_color(rgb(palette.muted))
                                .child(if no_devices {
                                    self.text("请连接并移动鼠标，未出现时重新检测。",
                                        "Connect and move a mouse. Rescan if it does not appear.")
                                } else {
                                    self.text("DPI 与鼠标当前设置一致，用于计算移动速度。",
                                        "Match each DPI to the mouse's current setting for accurate speed.")
                                })
                                )
                            .child(refresh),
                    )),
            )
            .child(div().h(px(64.0)).flex_shrink_0().border_t_1()
                .border_color(rgb(palette.hairline)).pt(px(16.0))
                .flex().items_center().justify_between().gap(px(24.0))
                .child(div().flex_1().min_w_0().text_size(px(13.0)).line_height(px(22.0))
                    .text_color(rgb(if self.control_error.is_some() { palette.accent } else { palette.muted }))
                    .child(status))
                .child(next_button))
    }
}

fn setup_label(text: impl Into<gpui::SharedString>, palette: design::Palette) -> gpui::Div {
    div()
        .text_size(px(13.0))
        .line_height(px(20.0))
        .text_color(rgb(palette.muted))
        .child(text.into())
}
