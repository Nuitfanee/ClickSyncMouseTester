use super::geometry::{Geometry, HISTORY_MS, MAX_SCROLL_AHEAD_MS, STROKE_WIDTH};
use super::motion::{Head, Seam, TAIL_MS};
use super::*;

const EMPTY_OPACITY: f32 = 0.48;

#[cfg(feature = "ui-restoration-probe")]
type LivePaintSample = (Instant, f64, f64, f64, [[f64; 2]; 4], f64, bool, f32, f64);

#[derive(Default)]
struct Clock {
    identity: Option<(u64, u64)>,
    sample_ms: f64,
    anchor_ms: f64,
    updated: Option<Instant>,
    running: bool,
}

impl Clock {
    fn update(&mut self, identity: (u64, u64), sample_ms: f64, running: bool, now: Instant) {
        if self.identity == Some(identity) && self.sample_ms == sample_ms && self.running == running
        {
            return;
        }
        let continuous = self.identity == Some(identity) && self.running && running;
        if continuous && sample_ms < self.sample_ms {
            return;
        }
        self.anchor_ms = if self.identity == Some(identity) && self.running && !running {
            self.logical_ms(now)
        } else if continuous {
            self.logical_ms(now)
                .max(sample_ms)
                .min(sample_ms + MAX_SCROLL_AHEAD_MS)
        } else if self.identity == Some(identity) && !self.running && running {
            self.anchor_ms.max(sample_ms)
        } else if self.identity == Some(identity) && !self.running && !running {
            self.anchor_ms
        } else {
            sample_ms
        };
        self.identity = Some(identity);
        self.sample_ms = sample_ms;
        self.updated = Some(now);
        self.running = running;
    }

    fn logical_ms(&self, now: Instant) -> f64 {
        let elapsed = if self.running {
            self.updated.map_or(0.0, |updated| {
                now.saturating_duration_since(updated).as_secs_f64() * 1000.0
            })
        } else {
            0.0
        };
        (self.anchor_ms + elapsed).min(self.sample_ms + MAX_SCROLL_AHEAD_MS)
    }

    fn animating(&self, now: Instant) -> bool {
        self.running && self.logical_ms(now) < self.sample_ms + MAX_SCROLL_AHEAD_MS
    }
}

#[derive(Default)]
pub(in crate::ui) struct PaintStats {
    pub paints: u64,
    pub geometry_builds: u64,
    pub axes_builds: u64,
    pub tail_builds: u64,
    #[cfg(feature = "ui-restoration-probe")]
    pub geometry_points: usize,
    #[cfg(feature = "ui-restoration-probe")]
    pub curve_points: usize,
    #[cfg(feature = "ui-restoration-probe")]
    pub samples: Vec<(Instant, f64, f64)>,
    #[cfg(feature = "ui-restoration-probe")]
    pub live_samples: Vec<LivePaintSample>,
    #[cfg(feature = "ui-restoration-probe")]
    pub geometry_events: Vec<([bool; 4], [f32; 2], f64, f64)>,
}

pub(in crate::ui) struct HistoryChart {
    state: Rc<RefCell<State>>,
}

fn newest_frame(
    before: Option<&Arc<crate::product::polling_stream::Frame>>,
    after: Option<Arc<crate::product::polling_stream::Frame>>,
) -> Option<Arc<crate::product::polling_stream::Frame>> {
    if let (Some(before), Some(after)) = (before, after.as_ref())
        && before.identity() == after.identity()
        && let (Some(old), Some(new)) = (before.history.last(), after.history.last())
        && new.logical_ms < old.logical_ms
    {
        return Some(before.clone());
    }
    after
}

fn same_stream(
    before: &Option<(crate::product::polling_stream::Reader, u64, u64)>,
    after: &Option<(crate::product::polling_stream::Reader, u64, u64)>,
) -> bool {
    match (before, after) {
        (None, None) => true,
        (Some((before, a, _)), Some((after, b, _))) => a == b && before.same_source(after),
        _ => false,
    }
}

struct State {
    frame: Option<Arc<crate::product::polling_stream::Frame>>,
    stream: Option<(crate::product::polling_stream::Reader, u64, u64)>,
    sampled: Option<Arc<crate::product::polling_stream::Frame>>,
    running: bool,
    palette: design::Palette,
    scale: Scale,
    maximum: f64,
    clock: Clock,
    head: Head,
    seam: Seam,
    opacity: super::super::Feedback,
    geometry: Option<Geometry>,
    stroke_builder: gpui::PolylineStrokeBuilder,
    tail: Option<super::geometry::Tail>,
    tail_key: Option<(f64, f64, f64)>,
    axes: Option<Axes>,
    head_segment: Option<u64>,
    scale_motion: super::super::Feedback,
    stats: PaintStats,
}

impl HistoryChart {
    pub fn new() -> Self {
        Self {
            state: Rc::new(RefCell::new(State {
                frame: None,
                stream: None,
                sampled: None,
                running: false,
                palette: design::Palette::for_theme(true),
                scale: Scale::default(),
                maximum: INITIAL_MAXIMUM,
                clock: Clock::default(),
                head: Head::default(),
                seam: Seam::default(),
                opacity: super::super::Feedback::new(EMPTY_OPACITY),
                geometry: None,
                stroke_builder: gpui::PolylineStrokeBuilder::default(),
                tail: None,
                tail_key: None,
                axes: None,
                head_segment: None,
                scale_motion: super::super::Feedback::new(INITIAL_MAXIMUM as f32),
                stats: PaintStats::default(),
            })),
        }
    }

    pub fn update(
        &mut self,
        frame: Option<Arc<crate::product::polling_stream::Frame>>,
        running: bool,
        palette: design::Palette,
        stream: Option<(crate::product::polling_stream::Reader, u64, u64)>,
        cx: &mut Context<Self>,
    ) {
        let mut state = self.state.borrow_mut();
        let frame = newest_frame(state.frame.as_ref(), frame);
        let data_changed = match (&state.frame, &frame) {
            (Some(before), Some(after)) => !Arc::ptr_eq(before, after),
            (None, None) => false,
            _ => true,
        };
        let palette_changed = state.palette.text != palette.text
            || state.palette.hairline != palette.hairline
            || state.palette.muted != palette.muted
            || state.palette.accent != palette.accent;
        let running = running
            && frame
                .as_ref()
                .is_some_and(|frame| !frame.history.is_empty());
        let phase_changed = state.running != running;
        let stream_changed = !same_stream(&state.stream, &stream);
        if !data_changed && !palette_changed && !phase_changed && !stream_changed {
            return;
        }
        let now = Instant::now();
        if state.frame.as_ref().map(|f| f.identity()) != frame.as_ref().map(|f| f.identity())
            || stream_changed
        {
            state.geometry = None;
            state.tail = None;
            state.tail_key = None;
            state.sampled = None;
            state.head_segment = None;
            state.scale_motion = super::super::Feedback::new(INITIAL_MAXIMUM as f32);
        }
        let present = frame
            .as_ref()
            .is_some_and(|frame| !frame.history.is_empty());
        let was_present = state
            .frame
            .as_ref()
            .is_some_and(|frame| !frame.history.is_empty());
        if present != was_present {
            state.opacity.animate(
                if present { 1.0 } else { EMPTY_OPACITY },
                if present { 0.18 } else { 0.2 },
                now,
            );
        }
        if frame.is_none() {
            state.scale = Scale::default();
            state.maximum = INITIAL_MAXIMUM;
            state.clock = Clock::default();
            state.head = Head::default();
            state.seam = Seam::default();
            state.geometry = None;
            state.tail = None;
            state.tail_key = None;
            state.sampled = None;
        }
        state.palette = palette;
        state.frame = frame;
        state.stream = stream;
        state.running = running;
        if !running {
            state.scale_motion = super::super::Feedback::new(state.maximum as f32);
        }
        state.refresh_stream(now);
        state.update_motion(now);
        cx.notify();
    }

    #[cfg(feature = "ui-restoration-probe")]
    pub fn stats(&self) -> std::cell::Ref<'_, PaintStats> {
        std::cell::Ref::map(self.state.borrow(), |state| &state.stats)
    }

    #[cfg(feature = "ui-restoration-probe")]
    pub fn sampling_stats(&self) -> serde_json::Value {
        let state = self.state.borrow();
        state.sampled.as_ref().map_or(serde_json::Value::Null, |frame| {
            let intervals: Vec<_> = frame.history.windows(2).map(|pair| pair[1].observed_ms - pair[0].observed_ms).collect();
            serde_json::json!({
                "target_hz": frame.sample_hz,
                "high_resolution_timer": frame.high_resolution_timer,
                "observations": frame.observation,
                "processed_reports": frame.processed,
                "retained_points": frame.history.len(),
                "running": frame.running,
                "observation_intervals_ms": intervals,
                "execution_intervals_ms": frame.execution_intervals_ms,
                "logical_times_ms": frame.history.iter().map(|p| p.logical_ms).collect::<Vec<_>>(),
            })
        })
    }
}

impl Render for HistoryChart {
    fn render(&mut self, window: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        let now = Instant::now();
        let mut state = self.state.borrow_mut();
        state.refresh_stream(now);
        if state.clock.running
            && state
                .sampled
                .as_ref()
                .or(state.frame.as_ref())
                .is_some_and(|frame| !frame.fresh(now))
        {
            state.update_motion(now);
        }
        let (opacity, fading) = state.opacity.sample(now);
        let (maximum, scaling) = state.scale_motion.sample(now);
        state.maximum = f64::from(maximum);
        if fading || scaling || state.clock.animating(now) || state.head.animating() {
            window.request_animation_frame();
        }
        let state = self.state.clone();
        div().size_full().opacity(opacity).child(
            canvas(
                |_, _, _| {},
                move |bounds, _, window, cx| state.borrow_mut().paint(bounds, window, cx),
            )
            .size_full(),
        )
    }
}

impl State {
    fn points(&self) -> Option<Arc<[HistoryPoint]>> {
        self.sampled
            .as_ref()
            .map(|s| s.history.clone())
            .or_else(|| self.frame.as_ref().map(|f| f.history.clone()))
    }

    fn refresh_stream(&mut self, now: Instant) {
        if !self.running {
            return;
        }
        let Some(frame) = &self.frame else {
            return;
        };
        if self.sampled.as_ref().is_some_and(|s| {
            self.stream.as_ref().is_none_or(|(_, receiver, revision)| {
                s.revision != *revision
                    || !s.belongs_to(*receiver, frame.identity().0, frame.identity().1)
            })
        }) {
            self.sampled = None;
        }
        let next = self
            .stream
            .as_ref()
            .and_then(|(reader, receiver, revision)| {
                reader.latest().filter(|s| {
                    s.revision == *revision
                        && s.belongs_to(*receiver, frame.identity().0, frame.identity().1)
                })
            });
        if let Some(next) = next
            && self
                .sampled
                .as_ref()
                .is_none_or(|old| next.observation >= old.observation)
            && self
                .sampled
                .as_ref()
                .is_none_or(|old| !Arc::ptr_eq(old, &next))
        {
            self.sampled = Some(next);
            self.update_motion(now);
        }
    }

    fn update_motion(&mut self, now: Instant) {
        if let Some(frame) = &self.frame
            && let Some(points) = self.points()
        {
            let identity = frame.identity();
            let maximum = self
                .scale
                .update(identity, &points, frame.metrics.peak_rate);
            if self.head_segment.is_none() {
                self.scale_motion = super::super::Feedback::new(maximum as f32);
                self.maximum = maximum;
            } else if self.running && self.scale_motion.target != maximum as f32 {
                self.scale_motion.animate(maximum as f32, 0.16, now);
            }
            if let Some(last) = points.last() {
                let source = self.sampled.as_ref().unwrap_or(frame);
                let running = self.running && source.running && source.fresh(now);
                self.clock.update(identity, source.logical_ms, running, now);
                if self.head_segment != Some(last.segment) {
                    self.head = Head::default();
                    self.seam = Seam::default();
                    self.head_segment = Some(last.segment);
                }
                self.head.update(
                    identity,
                    last.rate,
                    running && source.metrics.status == RateStatus::Valid,
                    now,
                );
            }
        }
    }

    fn paint(&mut self, bounds: Bounds<gpui::Pixels>, window: &mut Window, cx: &mut gpui::App) {
        #[cfg(feature = "ui-restoration-probe")]
        let started = Instant::now();
        let plot = Bounds {
            origin: bounds.origin + gpui::point(px(70.0), px(18.0)),
            size: size(
                (bounds.size.width - px(94.0)).max(px(1.0)),
                (bounds.size.height - px(48.0)).max(px(1.0)),
            ),
        };
        let palette = self.palette;
        let now = Instant::now();
        let head_rate = self.head.sample(now);
        let axes_key = AxesKey {
            size: bounds.size,
            maximum: self.maximum,
            text: palette.text,
            muted: palette.muted,
            hairline: palette.hairline,
            accent: palette.accent,
        };
        if self.axes.as_ref().is_none_or(|axes| axes.key != axes_key) {
            self.axes = Some(Axes::new(axes_key, window));
            self.stats.axes_builds += 1;
        }
        let points = self.points();
        let present = points.as_ref().is_some_and(|points| !points.is_empty());
        self.axes
            .as_ref()
            .unwrap()
            .paint(bounds.origin, plot, present, palette, window, cx);
        if let Some(points) = points.filter(|points| !points.is_empty()) {
            if self.geometry.as_ref().is_none_or(|geometry| {
                !geometry.matches(&points, plot.size, self.maximum, window.scale_factor())
            }) {
                #[cfg(feature = "ui-restoration-probe")]
                if self.stats.geometry_events.len() < 2048 {
                    let changes = self.geometry.as_ref().map_or([true; 4], |old| {
                        [
                            !Arc::ptr_eq(&old.points, &points),
                            old.size != plot.size,
                            old.maximum != self.maximum,
                            old.scale_factor != window.scale_factor(),
                        ]
                    });
                    self.stats.geometry_events.push((
                        changes,
                        [f32::from(plot.size.width), f32::from(plot.size.height)],
                        self.maximum,
                        points.last().unwrap().logical_ms,
                    ));
                }
                self.geometry = Some(Geometry::following_with_builder(
                    self.geometry.as_ref(),
                    points,
                    plot.size,
                    self.maximum,
                    window.scale_factor(),
                    &mut self.stroke_builder,
                ));
                self.tail_key = None;
                self.stats.geometry_builds += 1;
                #[cfg(feature = "ui-restoration-probe")]
                {
                    self.stats.geometry_points = self.geometry.as_ref().unwrap().point_count;
                    self.stats.curve_points = self.geometry.as_ref().unwrap().curve_point_count;
                }
            }
            let geometry = self.geometry.as_ref().unwrap();
            let display_ms = self.clock.logical_ms(now);
            let scroll_ms = (display_ms - geometry.origin_ms).max(0.0);
            let available = self
                .sampled
                .as_ref()
                .or(self.frame.as_ref())
                .is_some_and(|f| f.metrics.status == RateStatus::Valid);
            let tail_end_ms = if available {
                display_ms
            } else {
                geometry.points.last().unwrap().logical_ms
            };
            let seam_ms = self
                .seam
                .sample(
                    self.clock.identity.unwrap(),
                    (tail_end_ms - TAIL_MS)
                        .min(geometry.confirmed_ms)
                        .max(geometry.first_ms),
                    now,
                    self.clock.running,
                )
                .max(geometry.first_ms)
                .min(tail_end_ms);
            let key = (seam_ms, tail_end_ms, head_rate);
            if self.tail_key != Some(key) {
                geometry.update_tail(
                    self.tail.get_or_insert_with(Default::default),
                    seam_ms,
                    tail_end_ms,
                    head_rate,
                    &mut self.stroke_builder,
                );
                self.tail_key = Some(key);
                self.stats.tail_builds += 1;
            }
            let tail = self.tail.as_ref().unwrap();
            let live = self.clock.running && available;
            if live {
                window.paint_quad(gpui::fill(
                    Bounds::new(
                        gpui::point(plot.right() - px(16.0).min(plot.size.width), plot.origin.y),
                        size(px(16.0).min(plot.size.width), plot.size.height),
                    ),
                    gpui::rgba((palette.accent << 8) | 0x16),
                ));
                let label = &self.axes.as_ref().unwrap().live;
                let _ = label.paint(
                    gpui::point(plot.right() - label.width, plot.origin.y - px(17.75)),
                    px(13.75),
                    window,
                    cx,
                );
            }
            let offset = plot.origin
                - gpui::point(plot.size.width * (scroll_ms / HISTORY_MS) as f32, px(0.0));
            window.with_content_mask(
                Some(gpui::ContentMask {
                    bounds: plot.dilate(px(STROKE_WIDTH / 2.0)),
                }),
                |window| {
                    for paths in &geometry.archived {
                        window.paint_path_with_offset(
                            &paths.fill,
                            offset,
                            gpui::rgba((palette.text << 8) | 0x18),
                        );
                        if let Some(stroke) = &paths.stroke {
                            window.paint_path_with_offset(stroke, offset, rgb(palette.text));
                        }
                    }
                },
            );
            let seam_x = (offset.x + tail.start_x).clamp(plot.origin.x, plot.right());
            let history_mask =
                Bounds::new(plot.origin, size(seam_x - plot.origin.x, plot.size.height));
            window.with_content_mask(
                Some(gpui::ContentMask {
                    bounds: history_mask,
                }),
                |window| {
                    window.paint_path_with_offset(
                        &geometry.fill,
                        offset,
                        gpui::rgba((palette.text << 8) | 0x18),
                    );
                },
            );
            window.with_content_mask(
                Some(gpui::ContentMask {
                    bounds: Bounds::new(
                        plot.origin - gpui::point(px(STROKE_WIDTH / 2.0), px(STROKE_WIDTH / 2.0)),
                        size(
                            seam_x - plot.origin.x + px(STROKE_WIDTH / 2.0),
                            plot.size.height + px(STROKE_WIDTH),
                        ),
                    ),
                }),
                |window| {
                    if let Some(stroke) = &geometry.stroke {
                        window.paint_path_with_offset(stroke, offset, rgb(palette.text));
                    }
                },
            );
            window.with_content_mask(Some(gpui::ContentMask { bounds: plot }), |window| {
                window.paint_path_with_offset(
                    &tail.paths.fill,
                    offset,
                    gpui::rgba((palette.text << 8) | 0x18),
                );
            });
            window.with_content_mask(
                Some(gpui::ContentMask {
                    bounds: plot.dilate(px(STROKE_WIDTH / 2.0)),
                }),
                |window| {
                    if let Some(stroke) = &tail.paths.stroke {
                        window.paint_path_with_offset(stroke, offset, rgb(palette.text));
                    }
                },
            );
            let marker = gpui::point(
                offset.x + px(geometry.x(tail_end_ms) as f32),
                plot.origin.y + tail.marker_y,
            );
            if live && marker.y < plot.bottom() {
                window.paint_quad(gpui::fill(
                    Bounds::new(
                        gpui::point(marker.x - px(0.5), marker.y),
                        size(px(1.0), plot.bottom() - marker.y),
                    ),
                    rgb(palette.accent),
                ));
            }
            window.with_content_mask(
                Some(gpui::ContentMask {
                    bounds: plot.dilate(px(4.5)),
                }),
                |window| {
                    window.paint_quad(gpui::quad(
                        Bounds {
                            origin: marker - gpui::point(px(4.0), px(4.0)),
                            size: size(px(8.0), px(8.0)),
                        },
                        px(4.0),
                        rgb(palette.accent),
                        px(1.0),
                        rgb(palette.text),
                        gpui::BorderStyle::Solid,
                    ));
                },
            );
            #[cfg(feature = "ui-restoration-probe")]
            if self.stats.live_samples.len() < 4096 {
                self.stats.live_samples.push((
                    now,
                    display_ms,
                    head_rate,
                    seam_ms,
                    tail.cubic,
                    geometry.origin_ms,
                    live,
                    window.scale_factor(),
                    tail.incoming_slope,
                ));
            }
        }
        self.stats.paints += 1;
        #[cfg(feature = "ui-restoration-probe")]
        {
            if self.stats.samples.len() == 4096 {
                self.stats.samples.drain(..2048);
            }
            self.stats.samples.push((
                started,
                self.clock.logical_ms(started),
                started.elapsed().as_secs_f64() * 1000.0,
            ));
        }
    }
}

#[derive(PartialEq)]
struct AxesKey {
    size: gpui::Size<gpui::Pixels>,
    maximum: f64,
    text: u32,
    muted: u32,
    hairline: u32,
    accent: u32,
}

struct Axes {
    key: AxesKey,
    watermark: gpui::ShapedLine,
    live: gpui::ShapedLine,
    labels: Vec<(gpui::ShapedLine, gpui::Point<gpui::Pixels>, gpui::Pixels)>,
}

fn shape(window: &Window, value: String, font_size: f32, color: u32) -> gpui::ShapedLine {
    let run = gpui::TextRun {
        len: value.len(),
        font: design::ui_font(),
        color: rgb(color).into(),
        background_color: None,
        underline: None,
        strikethrough: None,
    };
    window
        .text_system()
        .shape_line(value.into(), px(font_size), &[run], None)
}

fn rate_label(rate: f64) -> String {
    let rate = rate.round();
    if rate >= 1000.0 {
        let value = format!("{:.3}", rate / 1000.0);
        format!("{}k", value.trim_end_matches('0').trim_end_matches('.'))
    } else {
        format!("{rate:.0}")
    }
}

fn horizontal_grid(maximum: f64) -> [f32; 4] {
    let mut fractions = axis_ticks(maximum).map(|(_, fraction)| fraction);
    fractions[3] = 1.0;
    fractions
}

impl Axes {
    fn new(key: AxesKey, window: &Window) -> Self {
        let width = (key.size.width - px(94.0)).max(px(1.0));
        let height = (key.size.height - px(48.0)).max(px(1.0));
        let mut labels = Vec::with_capacity(9);
        for index in 0..=4 {
            let fraction = index as f32 / 4.0;
            let value = if index == 4 {
                "NOW".into()
            } else {
                format!("-{}s", 3.0 - fraction * 3.0)
            };
            labels.push((
                shape(window, value, 11.0, key.muted),
                gpui::point(px(60.0) + width * fraction, px(26.0) + height),
                px(13.75),
            ));
        }
        for (rate, fraction) in axis_ticks(key.maximum) {
            labels.push((
                shape(window, rate_label(rate), 13.5, key.text),
                gpui::point(px(8.0), px(10.0) + height * (1.0 - fraction)),
                px(16.875),
            ));
        }
        let watermark = shape(
            window,
            rate_label(key.maximum / SCALE_HEADROOM),
            80.0,
            key.hairline,
        );
        Self {
            live: shape(window, "LIVE".into(), 11.0, key.accent),
            key,
            watermark,
            labels,
        }
    }

    fn paint(
        &self,
        origin: gpui::Point<gpui::Pixels>,
        plot: Bounds<gpui::Pixels>,
        present: bool,
        palette: design::Palette,
        window: &mut Window,
        cx: &mut gpui::App,
    ) {
        if present {
            window.with_content_mask(Some(gpui::ContentMask { bounds: plot }), |window| {
                let _ = self.watermark.paint(
                    plot.origin + gpui::point(px(12.0), px(2.0)),
                    px(100.0),
                    window,
                    cx,
                );
            });
        }
        for index in 0..=4 {
            window.paint_quad(gpui::fill(
                Bounds {
                    origin: plot.origin
                        + gpui::point(plot.size.width * (index as f32 / 4.0), px(0.0)),
                    size: size(px(1.0), plot.size.height),
                },
                rgb(palette.hairline),
            ));
        }
        // The outer border closes the grid above the highest labeled tick.
        for fraction in horizontal_grid(self.key.maximum) {
            window.paint_quad(gpui::fill(
                Bounds {
                    origin: gpui::point(plot.origin.x, plot.bottom() - plot.size.height * fraction),
                    size: size(plot.size.width, px(1.0)),
                },
                rgb(palette.hairline),
            ));
        }
        for (line, offset, line_height) in &self.labels {
            let _ = line.paint(origin + *offset, *line_height, window, cx);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn horizontal_grid_has_one_top_border_and_keeps_lower_ticks_aligned() {
        for maximum in [1020.0, 2040.0, 4080.0, 6120.0, 8160.0, 16320.0, 20400.0] {
            let grid = horizontal_grid(maximum);
            let ticks = axis_ticks(maximum).map(|(_, fraction)| fraction);
            assert_eq!(grid[..3], ticks[..3]);
            assert_eq!(grid[3], 1.0);
            assert_eq!(grid.iter().filter(|&&fraction| fraction > 0.5).count(), 1);
            assert!(grid.windows(2).all(|pair| pair[0] < pair[1]));
            assert!(ticks[3] < 1.0);
        }
    }

    #[test]
    fn axis_labels_do_not_round_fractional_kilohertz_to_a_different_tick() {
        for (rate, expected) in [
            (0.0, "0"),
            (250.0, "250"),
            (1000.0, "1k"),
            (1250.0, "1.25k"),
            (1500.0, "1.5k"),
            (2500.0, "2.5k"),
            (4000.0, "4k"),
            (4321.0, "4.321k"),
            (8000.0, "8k"),
            (20000.0, "20k"),
        ] {
            assert_eq!(rate_label(rate), expected);
        }
    }

    #[test]
    fn late_history_cannot_replace_confirmed_data_but_equal_time_and_new_identity_are_admitted() {
        let frame = |generation, reset, time, rate| {
            let frame =
                crate::product::polling_stream::Frame::for_test(generation, reset, time, rate);
            Arc::new(frame)
        };
        let latest = frame(1, 0, 60.0, 4000.0);
        let late = frame(1, 0, 40.0, 3000.0);
        assert!(Arc::ptr_eq(
            &newest_frame(Some(&latest), Some(late)).unwrap(),
            &latest
        ));
        let repeated = frame(1, 0, 60.0, 2000.0);
        assert!(Arc::ptr_eq(
            &newest_frame(Some(&latest), Some(repeated.clone())).unwrap(),
            &repeated
        ));
        for replacement in [frame(1, 1, 0.0, 0.0), frame(2, 0, 0.0, 1000.0)] {
            assert!(Arc::ptr_eq(
                &newest_frame(Some(&latest), Some(replacement.clone())).unwrap(),
                &replacement
            ));
        }
        assert!(newest_frame(Some(&latest), None).is_none());
    }

    #[test]
    fn sampling_source_and_receiver_changes_are_not_hidden_by_an_unchanged_business_frame() {
        let mut worker = crate::product::polling_stream::Worker::spawn();
        let mut replacement = crate::product::polling_stream::Worker::spawn();
        let before = Some((worker.reader(), 1, 0));
        assert!(same_stream(&before, &Some((worker.reader(), 1, 0))));
        assert!(!same_stream(&before, &Some((worker.reader(), 2, 0))));
        assert!(!same_stream(&before, &Some((replacement.reader(), 1, 0))));
        assert!(!same_stream(&before, &None));
        assert!(same_stream(&None, &None));
        worker.shutdown();
        replacement.shutdown();
    }

    #[test]
    fn display_clock_scrolls_between_results_freezes_on_pause_and_bounds_worker_stalls() {
        let start = Instant::now();
        let mut clock = Clock::default();
        clock.update((1, 0), 3000.0, true, start);
        assert_eq!(clock.logical_ms(start + Duration::from_millis(8)), 3008.0);
        assert_eq!(clock.logical_ms(start + Duration::from_millis(16)), 3016.0);
        // A late publication cannot move the live timeline backwards.
        clock.update((1, 0), 3010.0, true, start + Duration::from_millis(20));
        assert_eq!(clock.logical_ms(start + Duration::from_millis(20)), 3020.0);
        assert_eq!(clock.logical_ms(start + Duration::from_secs(1)), 3110.0);
        assert!(!clock.animating(start + Duration::from_secs(1)));
        clock.update((1, 0), 3040.0, false, start + Duration::from_secs(1));
        assert_eq!(clock.logical_ms(start + Duration::from_secs(5)), 3110.0);
        assert!(!clock.animating(start + Duration::from_secs(5)));
        clock.update((1, 0), 3060.0, true, start + Duration::from_secs(5));
        assert_eq!(
            clock.logical_ms(start + Duration::from_millis(5008)),
            3118.0
        );
        clock.update((2, 0), 0.0, true, start + Duration::from_secs(6));
        assert_eq!(clock.logical_ms(start + Duration::from_millis(6008)), 8.0);
    }
}
