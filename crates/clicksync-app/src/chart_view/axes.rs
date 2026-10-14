//! One current axis layout per view; data-only publications reuse ticks and shaped labels.
use super::*;
use crate::chart::labels::{Tick, Unit};
use clicksync_chart::{Coordinate, analysis::histogram::HistogramBin, gpu::PlotKind};

#[derive(Clone, Copy, PartialEq)]
pub(super) struct Spec {
    pub viewport: Viewport,
    pub plot: Bounds<Pixels>,
    pub outer_right: Pixels,
    pub units: [Unit; 2],
    pub kind: PlotKind,
    pub interval_ms: Option<f64>,
    pub y_down: bool,
    pub trajectory: bool,
    pub workspace: bool,
    pub dark: bool,
    pub scale_factor: f32,
}

impl Spec {
    fn histogram_ticks<'a>(&self, bins: &'a [HistogramBin]) -> &'a [HistogramBin] {
        if self.kind == PlotKind::Histogram
            && self.units[0] == Unit::Count
            && !bins.is_empty()
            && bins.len() <= 32
            && bins.iter().all(|bin| bin.maximum - bin.minimum <= 1.0)
            && (bins[0].maximum - bins[0].minimum) / self.viewport.width
                * f64::from(f32::from(self.plot.size.width))
                >= 36.0
        {
            bins
        } else {
            &[]
        }
    }
}

#[derive(Default)]
pub(super) struct Cache {
    gutter: Option<(GutterKey, f32)>,
    layout: Option<Layout>,
}

#[derive(Clone, Copy, PartialEq, Debug)]
struct GutterKey {
    unit: Unit,
    origin: Coordinate,
    span: f64,
    height: f32,
}

struct Label {
    line: gpui::ShapedLine,
    position: Point<Pixels>,
}

struct Layout {
    spec: Spec,
    bin_edges: Vec<[f64; 2]>,
    x_ticks: Vec<Tick>,
    y_ticks: Vec<Tick>,
    labels: Vec<Label>,
}

impl Cache {
    pub fn gutter(&mut self, unit: Unit, viewport: Viewport, height: f32) -> f32 {
        let key = GutterKey {
            unit,
            origin: viewport.origin.y,
            span: viewport.height,
            height,
        };
        if let Some((previous, width)) = self.gutter
            && previous == key
        {
            return width;
        }
        let width = unit
            .vertical_ticks(viewport.origin.y, viewport.height, height)
            .iter()
            .filter(|tick| tick.major)
            .map(|tick| unit.axis_label(tick.value, viewport.height).len())
            .max()
            .unwrap_or(4) as f32
            * 7.0
            + 10.0;
        self.gutter = Some((key, width));
        width
    }

    pub fn paint(&mut self, spec: Spec, bins: &[HistogramBin], window: &mut Window, cx: &mut App) {
        let bins = spec.histogram_ticks(bins);
        if self
            .layout
            .as_ref()
            .is_none_or(|layout| !layout.matches(spec, bins))
        {
            self.layout = Some(Layout::new(spec, bins, window));
        }
        self.layout.as_ref().unwrap().paint(window, cx);
    }
}

impl Layout {
    fn matches(&self, spec: Spec, bins: &[HistogramBin]) -> bool {
        self.spec == spec
            && self.bin_edges.len() == bins.len()
            && self
                .bin_edges
                .iter()
                .zip(bins)
                .all(|(edge, bin)| *edge == [bin.minimum, bin.maximum])
    }

    fn new(spec: Spec, bins: &[HistogramBin], window: &Window) -> Self {
        let Spec {
            viewport,
            plot,
            units,
            kind,
            ..
        } = spec;
        let mut x_ticks = units[0].ticks(
            viewport.origin.x,
            viewport.width,
            f32::from(plot.size.width),
        );
        if !bins.is_empty() {
            x_ticks = bins
                .iter()
                .filter_map(|bin| {
                    let value = Coordinate::Scalar((bin.minimum + bin.maximum) * 0.5);
                    let fraction = value.relative_to(viewport.origin.x) / viewport.width;
                    (0.0..=1.0).contains(&fraction).then_some(Tick {
                        fraction,
                        value,
                        major: true,
                    })
                })
                .collect();
        }
        let mut labels = Vec::new();
        let mut label = |value: String, mut position: Point<Pixels>, align: f32| {
            let line = shape_label(value, spec.workspace, spec.dark, window);
            position.x -= line.width * align;
            labels.push(Label { line, position });
        };
        let mut last_label_right = plot.left() - px(6.0);
        for tick in x_ticks.iter().filter(|tick| tick.major) {
            let x = plot.origin.x + plot.size.width * tick.fraction as f32;
            let value = units[0].axis_label(tick.value, viewport.width);
            let half = px(value.len() as f32 * 3.5);
            let label_x = x.clamp(
                plot.left() + half,
                (plot.right() - half).max(plot.left() + half),
            );
            if label_x - half >= last_label_right
                && (!spec.workspace
                    || kind == PlotKind::Histogram
                    || label_x + half < plot.right() - px(48.0))
            {
                last_label_right = label_x + half + px(8.0);
                label(value, point(label_x, plot.bottom() + px(6.0)), 0.5);
            }
        }
        let mut y_ticks = units[1].vertical_ticks(
            viewport.origin.y,
            viewport.height,
            f32::from(plot.size.height),
        );
        let mut interval_reference = None;
        if let Some(interval) = spec.interval_ms {
            let scale = 10.0_f64.powf((2.0 - interval.log10().floor()).clamp(0.0, 9.0));
            let value = Coordinate::Scalar((interval * scale).round() / scale);
            let fraction = value.relative_to(viewport.origin.y) / viewport.height;
            if (0.0..=1.0).contains(&fraction) {
                interval_reference = Some(value);
                y_ticks.retain(|tick| {
                    !tick.major
                        || (tick.fraction - fraction).abs() * f64::from(f32::from(plot.size.height))
                            > 20.0
                });
                y_ticks.push(Tick {
                    fraction,
                    value,
                    major: true,
                });
            }
        }
        for tick in y_ticks.iter().filter(|tick| tick.major) {
            let fraction = if spec.y_down {
                tick.fraction
            } else {
                1.0 - tick.fraction
            };
            let y = plot.origin.y + plot.size.height * fraction as f32;
            let value = if interval_reference == Some(tick.value) {
                units[1].readout(tick.value)
            } else {
                let value = units[1].axis_label(tick.value, viewport.height);
                if units[1] == Unit::Percent {
                    format!("{value}%")
                } else {
                    value
                }
            };
            label(
                value,
                point(
                    plot.left() - px(8.0),
                    (y - px(8.0)).clamp(
                        plot.top() - px(4.0),
                        (plot.bottom() - px(12.0)).max(plot.top() - px(4.0)),
                    ),
                ),
                1.0,
            );
        }
        if spec.workspace {
            label(
                format!(
                    "{} {}",
                    if spec.trajectory {
                        "X"
                    } else if units[0] == Unit::Milliseconds && kind != PlotKind::Histogram {
                        "t"
                    } else {
                        ""
                    },
                    units[0].symbol()
                ),
                point(
                    if kind == PlotKind::Histogram {
                        spec.outer_right - px(2.0)
                    } else {
                        plot.right()
                    },
                    plot.bottom() + px(6.0),
                ),
                1.0,
            );
        }
        Self {
            spec,
            bin_edges: bins.iter().map(|bin| [bin.minimum, bin.maximum]).collect(),
            x_ticks,
            y_ticks,
            labels,
        }
    }

    fn paint(&self, window: &mut Window, cx: &mut App) {
        let spec = self.spec;
        let plot = spec.plot;
        let palette = crate::ui::design::Palette::for_theme(spec.dark);
        let background = if spec.workspace {
            palette.background
        } else {
            0xffffff
        };
        let grid = if spec.workspace {
            gpui::rgba((palette.hairline << 8) | 0x90)
        } else {
            rgb(0xe9edf3)
        };
        window.paint_quad(fill(plot, rgb(background)));
        for (vertical, ticks) in [(true, &self.x_ticks), (false, &self.y_ticks)] {
            for tick in ticks {
                if spec.kind == clicksync_chart::gpu::PlotKind::Histogram && !tick.major {
                    continue;
                }
                let fraction = if vertical || spec.y_down {
                    tick.fraction
                } else {
                    1.0 - tick.fraction
                };
                let bounds = if vertical {
                    Bounds::new(
                        point(
                            plot.origin.x + plot.size.width * fraction as f32,
                            plot.origin.y,
                        ),
                        size(px(1.0), plot.size.height),
                    )
                } else {
                    Bounds::new(
                        point(
                            plot.origin.x,
                            plot.origin.y + plot.size.height * fraction as f32,
                        ),
                        size(plot.size.width, px(1.0)),
                    )
                };
                window.paint_quad(fill(
                    bounds,
                    if tick.major {
                        grid
                    } else {
                        gpui::rgba((palette.hairline << 8) | 0x38)
                    },
                ));
            }
        }
        for label in &self.labels {
            if let Err(error) = label.line.paint(label.position, px(16.0), window, cx) {
                eprintln!("chart labels: {error}");
            }
        }
    }
}

pub(super) fn shape_label(
    value: String,
    workspace: bool,
    dark: bool,
    window: &Window,
) -> gpui::ShapedLine {
    let run = TextRun {
        len: value.len(),
        font: font(if workspace {
            crate::ui::design::DISPLAY_FONT
        } else {
            "Segoe UI"
        }),
        color: rgb(if workspace {
            crate::ui::design::Palette::for_theme(dark).muted
        } else {
            0x526174
        })
        .into(),
        background_color: None,
        underline: None,
        strikethrough: None,
    };
    window.text_system().shape_line(
        value.into(),
        px(if workspace { 12.0 } else { 11.0 }),
        &[run],
        None,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn spec() -> Spec {
        Spec {
            viewport: Viewport {
                origin: clicksync_chart::Point {
                    x: Coordinate::Scalar(0.0),
                    y: Coordinate::Scalar(0.0),
                },
                width: 1000.0,
                height: 100.0,
            },
            plot: Bounds::new(point(px(60.0), px(24.0)), size(px(800.0), px(400.0))),
            outer_right: px(900.0),
            units: [Unit::Milliseconds, Unit::Count],
            kind: PlotKind::Points,
            interval_ms: None,
            y_down: false,
            trajectory: false,
            workspace: true,
            dark: true,
            scale_factor: 1.0,
        }
    }

    #[test]
    fn layout_identity_tracks_display_coordinates_style_dpi_and_bin_edges_not_counts() {
        let mut spec = spec();
        spec.kind = PlotKind::Histogram;
        spec.units[0] = Unit::Count;
        let bins = [HistogramBin {
            minimum: -0.5,
            maximum: 0.5,
            value: 2.0,
            count: 2,
        }];
        let layout = Layout {
            spec,
            bin_edges: vec![[-0.5, 0.5]],
            x_ticks: Vec::new(),
            y_ticks: Vec::new(),
            labels: Vec::new(),
        };
        assert!(layout.matches(spec, &bins));
        assert!(layout.matches(
            spec,
            &[HistogramBin {
                count: 20,
                value: 20.0,
                ..bins[0]
            }]
        ));
        assert!(!layout.matches(
            spec,
            &[HistogramBin {
                maximum: 1.5,
                ..bins[0]
            }]
        ));
        for changed in [
            Spec {
                scale_factor: 2.0,
                ..spec
            },
            Spec {
                dark: false,
                ..spec
            },
            Spec {
                y_down: true,
                ..spec
            },
            Spec {
                units: [Unit::Count, Unit::Percent],
                ..spec
            },
            Spec {
                interval_ms: Some(0.125),
                ..spec
            },
            Spec {
                viewport: Viewport {
                    width: 2000.0,
                    ..spec.viewport
                },
                ..spec
            },
            Spec {
                plot: Bounds::new(point(px(61.0), px(24.0)), spec.plot.size),
                ..spec
            },
        ] {
            assert!(!layout.matches(changed, &bins));
        }
        assert!(!layout.matches(spec, &[]));
    }

    #[test]
    fn gutter_reuses_unchanged_y_axes_and_ignores_horizontal_motion() {
        let mut cache = Cache::default();
        let view = spec().viewport;
        let width = cache.gutter(Unit::Count, view, 400.0);
        let before = cache.gutter;
        assert_eq!(
            width,
            cache.gutter(
                Unit::Count,
                Viewport {
                    width: 3000.0,
                    ..view
                },
                400.0
            )
        );
        assert_eq!(cache.gutter, before);
        cache.gutter(Unit::Milliseconds, view, 400.0);
        assert_ne!(cache.gutter, before);
    }
}
