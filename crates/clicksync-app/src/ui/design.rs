//! Shared theme, typography and embedded application resources.
use gpui::{App, AssetSource, Div, FontWeight, SharedString, Stateful, div, prelude::*, px, rgb};
use std::borrow::Cow;

pub const DISPLAY_FONT: &str = "Space Grotesk";
pub const CHINESE_FONT: &str = "Microsoft YaHei UI";

const DISPLAY_FONTS: [&[u8]; 3] = [
    include_bytes!("../../../../assets/fonts/SpaceGrotesk/spacegrotesk-regular.ttf"),
    include_bytes!("../../../../assets/fonts/SpaceGrotesk/spacegrotesk-medium.ttf"),
    include_bytes!("../../../../assets/fonts/SpaceGrotesk/spacegrotesk-bold.ttf"),
];

pub(crate) fn ui_font() -> gpui::Font {
    static FONT: std::sync::OnceLock<gpui::Font> = std::sync::OnceLock::new();
    FONT.get_or_init(|| gpui::Font {
        fallbacks: Some(gpui::FontFallbacks::from_fonts(vec![
            CHINESE_FONT.to_owned(),
        ])),
        ..gpui::font(DISPLAY_FONT)
    })
    .clone()
}

pub(super) fn tabular_display_font() -> gpui::Font {
    static FONT: std::sync::OnceLock<gpui::Font> = std::sync::OnceLock::new();
    FONT.get_or_init(|| gpui::Font {
        features: gpui::FontFeatures(std::sync::Arc::new(vec![("tnum".into(), 1)])),
        ..ui_font()
    })
    .clone()
}

const WINDOW_ICONS: [(&str, &[u8]); 4] = [
    (
        "icons/window-minimize.svg",
        include_bytes!("../../../../assets/icons/window-minimize.svg"),
    ),
    (
        "icons/window-maximize.svg",
        include_bytes!("../../../../assets/icons/window-maximize.svg"),
    ),
    (
        "icons/window-restore.svg",
        include_bytes!("../../../../assets/icons/window-restore.svg"),
    ),
    (
        "icons/window-close.svg",
        include_bytes!("../../../../assets/icons/window-close.svg"),
    ),
];

pub(super) fn window_icon(label: &str) -> gpui::Svg {
    gpui::svg().size(px(16.0)).path(match label {
        "−" => "icons/window-minimize.svg",
        "□" => "icons/window-maximize.svg",
        "▢" => "icons/window-restore.svg",
        _ => "icons/window-close.svg",
    })
}

#[derive(Clone, Copy, Debug, serde::Serialize)]
pub struct Palette {
    pub background: u32,
    pub panel: u32,
    pub text: u32,
    pub muted: u32,
    pub hairline: u32,
    pub accent: u32,
    pub accent_hover: u32,
    pub accent_text: u32,
    pub hover: u32,
    pub selected: u32,
    pub curtain: u32,
    pub brand: u32,
}

impl Palette {
    pub fn for_theme(dark: bool) -> Self {
        if dark {
            Self {
                background: 0x050505,
                panel: 0x0b0b0c,
                text: 0xffffff,
                muted: 0x96969e,
                hairline: 0x1e1e20,
                accent: 0xb62fff,
                accent_hover: 0xc34bff,
                accent_text: 0x050505,
                hover: 0x101010,
                selected: 0x211a4d,
                curtain: 0x6a21a7,
                brand: 0x2a0047,
            }
        } else {
            Self {
                background: 0xf7f8fa,
                panel: 0xffffff,
                text: 0x13151a,
                muted: 0x6e7580,
                hairline: 0xe3e6ea,
                accent: 0x5c35ff,
                accent_hover: 0x704dff,
                accent_text: 0xffffff,
                hover: 0xedf1f5,
                selected: 0xeee9ff,
                curtain: 0x1e3a89,
                brand: 0x050b2e,
            }
        }
    }
}

#[derive(Clone, Copy, Debug, serde::Serialize)]
pub struct Typography {
    pub body: f32,
    pub micro: f32,
    pub primary: f32,
    pub secondary: f32,
    pub combo: f32,
    pub mode: f32,
}

impl Typography {
    pub fn for_language(english: bool) -> Self {
        if english {
            Self {
                body: 13.0,
                micro: 11.0,
                primary: 13.5,
                secondary: 13.0,
                combo: 14.0,
                mode: 11.0,
            }
        } else {
            Self {
                body: 14.0,
                micro: 12.0,
                primary: 14.0,
                secondary: 14.0,
                combo: 15.0,
                mode: 11.5,
            }
        }
    }
}

pub fn install_fonts(cx: &App) -> gpui::Result<()> {
    cx.text_system().add_fonts(
        DISPLAY_FONTS
            .iter()
            .map(|&bytes| Cow::Borrowed(bytes))
            .collect(),
    )?;
    Ok(())
}

#[cfg(feature = "ui-restoration-probe")]
pub(super) fn verify_font_shaping(window: &gpui::Window) -> gpui::Result<()> {
    let text_system = window.text_system();
    let weights = [FontWeight::NORMAL, FontWeight::MEDIUM, FontWeight::BOLD];
    // System font weights can resolve to the same physical face.
    for weight in weights {
        let mut chinese = gpui::font(CHINESE_FONT);
        chinese.weight = weight;
        text_system.resolve_font(&chinese);
    }
    for weight in weights {
        let mut latin = ui_font();
        latin.weight = weight;
        let latin_id = text_system.resolve_font(&latin);
        let mut tabular = tabular_display_font();
        tabular.weight = weight;
        let tabular_id = text_system.resolve_font(&tabular);
        let mut chinese = gpui::font(CHINESE_FONT);
        chinese.weight = weight;
        let chinese_id = text_system.resolve_font(&chinese);
        let text = "中文鼠标报告率，。 Space Grotesk DPI 0123456789 12.5ms";
        for (font, expected_latin_id) in [(latin.clone(), latin_id), (tabular, tabular_id)] {
            let run = gpui::TextRun {
                len: text.len(),
                font,
                color: rgb(0).into(),
                background_color: None,
                underline: None,
                strikethrough: None,
            };
            let line = text_system.shape_line(text.into(), px(14.0), &[run], None);
            for run in &line.runs {
                for glyph in &run.glyphs {
                    let ch = text[glyph.index..].chars().next().expect("glyph character");
                    let expected = if ch.is_ascii() {
                        expected_latin_id
                    } else {
                        chinese_id
                    };
                    if glyph.id.is_missing() || run.font_id != expected {
                        return Err(std::io::Error::other(format!(
                            "Mixed-script font routing failed for {ch:?} at weight {}: {:?} ({:?}), expected {:?} ({:?})",
                            weight.0,
                            run.font_id,
                            text_system.get_font_for_id(run.font_id),
                            expected,
                            text_system.get_font_for_id(expected),
                        ))
                        .into());
                    }
                }
            }
        }
        // A dynamic name can contain characters outside the embedded UI subset.
        let dynamic = char::from_u32(0x9f98)
            .expect("valid CJK codepoint")
            .to_string();
        let run = gpui::TextRun {
            len: dynamic.len(),
            font: latin,
            color: rgb(0).into(),
            background_color: None,
            underline: None,
            strikethrough: None,
        };
        let line = text_system.shape_line(dynamic.into(), px(14.0), &[run], None);
        if line.runs.is_empty()
            || line
                .runs
                .iter()
                .any(|run| run.glyphs.iter().any(|glyph| glyph.id.is_missing()))
        {
            return Err(
                std::io::Error::other("System fallback for dynamic CJK text failed").into(),
            );
        }
    }
    println!(
        "Font routing verified: Space Grotesk Latin/digits, Microsoft YaHei UI CJK, system fallback."
    );
    Ok(())
}

/// Stable hit targets and immediate press feedback across product pages.
pub fn button(
    id: impl Into<SharedString>,
    label: impl Into<SharedString>,
    palette: Palette,
    english: bool,
    primary: bool,
    enabled: bool,
) -> Stateful<Div> {
    let typography = Typography::for_language(english);
    div()
        .id(id.into())
        .flex()
        .items_center()
        .justify_center()
        .flex_shrink_0()
        .min_h(px(36.0))
        .rounded(px(8.0))
        .px(px(16.0))
        .py(px(8.0))
        .text_size(px(if primary {
            typography.primary
        } else {
            typography.secondary
        }))
        .font_weight(if primary {
            if english {
                FontWeight::BOLD
            } else {
                FontWeight::SEMIBOLD
            }
        } else if english {
            FontWeight::MEDIUM
        } else {
            FontWeight::NORMAL
        })
        .border_1()
        .border_color(rgb(if primary {
            palette.accent
        } else {
            palette.hairline
        }))
        .bg(rgb(if primary {
            palette.accent
        } else {
            palette.background
        }))
        .text_color(rgb(if primary {
            palette.accent_text
        } else {
            palette.text
        }))
        .when(enabled, |button| {
            button
                .cursor_pointer()
                .hover(move |style| {
                    style.bg(rgb(if primary {
                        palette.accent_hover
                    } else {
                        palette.hover
                    }))
                })
                .active(move |style| {
                    style
                        .bg(rgb(if primary {
                            palette.accent
                        } else {
                            palette.selected
                        }))
                        .opacity(0.86)
                })
        })
        .when(!enabled, |button| button.opacity(0.4))
        .child(label.into())
}

pub struct Assets;
impl AssetSource for Assets {
    fn load(&self, path: &str) -> gpui::Result<Option<Cow<'static, [u8]>>> {
        Ok(
            if let Some((_, bytes)) = WINDOW_ICONS.iter().find(|(name, _)| *name == path) {
                Some(Cow::Borrowed(*bytes))
            } else if path == "brand/mouse-guide.svg" {
                Some(Cow::Borrowed(include_bytes!(
                    "../../../../assets/brand/mouse-guide.svg"
                )))
            } else if path == "brand/rog.svg" {
                Some(Cow::Borrowed(include_bytes!(
                    "../../../../assets/brand/rog.svg"
                )))
            } else {
                super::brand_assets::SVG_ASSETS
                    .iter()
                    .find(|(name, _)| *name == path)
                    .map(|(_, bytes)| Cow::Borrowed(*bytes))
            },
        )
    }
    fn list(&self, path: &str) -> gpui::Result<Vec<SharedString>> {
        Ok(if path == "brand" {
            ["brand/rog.svg", "brand/mouse-guide.svg"]
                .into_iter()
                .map(SharedString::from)
                .chain(
                    super::brand_assets::SVG_ASSETS
                        .iter()
                        .map(|(name, _)| (*name).into()),
                )
                .collect()
        } else if path == "icons" {
            WINDOW_ICONS
                .iter()
                .map(|(name, _)| (*name).into())
                .collect()
        } else {
            Vec::new()
        })
    }
}

#[cfg(test)]
#[path = "design/font_tests.rs"]
mod font_tests;
