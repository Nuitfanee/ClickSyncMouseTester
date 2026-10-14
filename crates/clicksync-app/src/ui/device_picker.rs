//! Shared device presentation; classification and identity live in core::devices.
use super::*;
use clicksync_core::devices::{DeviceChoice, EndpointKind};

#[derive(Clone, Default)]
pub(super) struct Anchor(Rc<std::cell::Cell<Bounds<gpui::Pixels>>>);

pub(super) fn text_trigger(
    id: impl Into<gpui::SharedString>,
    label: Option<&str>,
    palette: design::Palette,
    english: bool,
    enabled: bool,
    anchor: &Anchor,
) -> gpui::Stateful<gpui::Div> {
    trigger_content(id, label, english, anchor)
        .h(px(45.0))
        .border_b_1()
        .border_color(rgb(palette.hairline))
        .text_size(px(22.0))
        .font_weight(gpui::FontWeight::BOLD)
        .when(enabled, |row| {
            row.cursor_pointer()
                .hover(move |style| style.text_color(rgb(palette.accent)))
                .active(|style| style.opacity(0.7))
        })
        .when(!enabled, |row| row.opacity(0.4))
}

fn trigger_content(
    id: impl Into<gpui::SharedString>,
    label: Option<&str>,
    english: bool,
    anchor: &Anchor,
) -> gpui::Stateful<gpui::Div> {
    let anchor = anchor.clone();
    let label = label.map_or_else(
        || {
            if english {
                "Select mouse".into()
            } else {
                "选择鼠标".into()
            }
        },
        str::to_owned,
    );
    div()
        .id(id.into())
        .relative()
        .w_full()
        .flex()
        .items_center()
        .gap(px(12.0))
        .child(div().min_w_0().flex_1().truncate().child(label))
        .child(div().flex_shrink_0().text_size(px(11.0)).child("▾"))
        .child(
            canvas(move |bounds, _, _| anchor.0.set(bounds), |_, _, _, _| {})
                .absolute()
                .inset_0()
                .size_full(),
        )
}

pub(super) fn option(
    id: impl Into<gpui::SharedString>,
    choice: &DeviceChoice,
    selected: bool,
    palette: design::Palette,
    english: bool,
) -> gpui::Stateful<gpui::Div> {
    div()
        .id(id.into())
        .relative()
        .w_full()
        .min_w_0()
        .flex_shrink_0()
        .flex()
        .flex_col()
        .gap(px(4.0))
        .p(px(12.0))
        .border_1()
        .border_color(rgb(palette.hairline))
        .bg(rgb(if selected {
            palette.selected
        } else {
            palette.background
        }))
        .cursor_pointer()
        .hover(move |style| style.bg(rgb(palette.hover)))
        .active(|style| style.opacity(0.8))
        .child(
            div()
                .w_full()
                .text_size(px(design::Typography::for_language(english).combo))
                .child(choice.label.clone()),
        )
        .child(
            div()
                .w_full()
                .text_size(px(11.0))
                .text_color(rgb(palette.muted))
                .child(choice.detail.clone()),
        )
        .when(choice.kind != EndpointKind::MotionCapable, |row| {
            row.child(
                div()
                    .text_size(px(11.0))
                    .text_color(rgb(palette.muted))
                    .child(if english {
                        "Movement not yet observed"
                    } else {
                        "待确认运动"
                    }),
            )
        })
}

pub(super) fn popup(
    id: impl Into<gpui::SharedString>,
    palette: design::Palette,
    anchor: Anchor,
    dismiss: impl Fn(&gpui::MouseDownEvent, &mut Window, &mut gpui::App) + 'static,
) -> gpui::Stateful<gpui::Div> {
    div()
        .id(id.into())
        .absolute()
        .top(px(46.0))
        .left_0()
        .w_full()
        .max_h(px(320.0))
        .overflow_y_scroll()
        .flex()
        .flex_col()
        .gap(px(4.0))
        .p(px(8.0))
        .bg(rgb(palette.panel))
        .border_1()
        .border_color(rgb(palette.hairline))
        .occlude()
        .on_mouse_down_out(move |event, window, cx| {
            // The trigger and popup form one interaction; only clicks outside both dismiss.
            if !anchor.0.get().contains(&event.position) {
                dismiss(event, window, cx);
            }
        })
}
