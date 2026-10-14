//! Button, wheel and custom-key timing. No platform or UI dependencies.
use serde::Serialize;

#[derive(Clone, Debug, Default, Serialize)]
pub struct TimingStatistics {
    pub down_count: u64,
    pub up_count: u64,
    pub double_click_count: u64,
    pub current_down_down_ms: Option<f64>,
    pub minimum_down_down_ms: Option<f64>,
    pub current_down_up_ms: Option<f64>,
    pub minimum_down_up_ms: Option<f64>,
    pub is_pressed: bool,
    #[serde(skip)]
    last_down: Option<f64>,
    #[serde(skip)]
    down_start: Option<f64>,
    #[serde(skip)]
    down_down_sum: f64,
    #[serde(skip)]
    down_down_samples: u64,
    #[serde(skip)]
    down_up_sum: f64,
    #[serde(skip)]
    down_up_samples: u64,
}
impl TimingStatistics {
    pub fn reset(&mut self) {
        *self = Self::default();
    }
    pub fn reset_pressed(&mut self) {
        self.is_pressed = false;
        self.down_start = None;
    }
    pub fn down(&mut self, timestamp: f64, threshold: f64) -> bool {
        if self.is_pressed {
            return false;
        }
        self.is_pressed = true;
        self.down_start = Some(timestamp);
        let mut double = false;
        if let Some(previous) = self.last_down {
            let interval = timestamp - previous;
            self.current_down_down_ms = Some(interval);
            if interval > 0.2 && interval < 3000.0 {
                self.minimum_down_down_ms = Some(
                    self.minimum_down_down_ms
                        .map_or(interval, |v| v.min(interval)),
                );
                self.down_down_sum += interval;
                self.down_down_samples += 1;
            }
            if interval > 0.0 && interval < threshold {
                self.double_click_count += 1;
                double = true;
            }
        } else {
            self.current_down_down_ms = None;
        }
        self.down_count += 1;
        self.last_down = Some(timestamp);
        double
    }
    pub fn up(&mut self, timestamp: f64) {
        if let Some(start) = self.down_start.filter(|v| self.is_pressed && !v.is_nan()) {
            let interval = timestamp - start;
            self.current_down_up_ms = Some(interval);
            if interval > 0.2 && interval < 5000.0 {
                self.minimum_down_up_ms = Some(
                    self.minimum_down_up_ms
                        .map_or(interval, |v| v.min(interval)),
                );
                self.down_up_sum += interval;
                self.down_up_samples += 1;
            }
        }
        self.reset_pressed();
        self.up_count += 1;
    }
    pub fn average_down_down_ms(&self) -> Option<f64> {
        (self.down_down_samples > 0).then(|| self.down_down_sum / self.down_down_samples as f64)
    }
    pub fn average_down_up_ms(&self) -> Option<f64> {
        (self.down_up_samples > 0).then(|| self.down_up_sum / self.down_up_samples as f64)
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
pub struct KeyIdentity {
    pub virtual_key: u16,
    pub scan_code: u16,
    pub extended: bool,
}
#[derive(Serialize)]
pub struct KeyEngine {
    pub buttons: [TimingStatistics; 5],
    pub custom: TimingStatistics,
    pub selected: Option<KeyIdentity>,
    pub picking: bool,
    pub wheel_up: u64,
    pub wheel_down: u64,
    pub wheel_up_until: f64,
    pub wheel_down_until: f64,
    pub mouse_threshold_ms: f64,
    pub key_threshold_ms: f64,
    #[serde(skip)]
    ignored_release: Option<KeyIdentity>,
}
impl Default for KeyEngine {
    fn default() -> Self {
        Self {
            buttons: std::array::from_fn(|_| TimingStatistics::default()),
            custom: TimingStatistics::default(),
            selected: None,
            picking: false,
            wheel_up: 0,
            wheel_down: 0,
            wheel_up_until: 0.0,
            wheel_down_until: 0.0,
            mouse_threshold_ms: 80.0,
            key_threshold_ms: 80.0,
            ignored_release: None,
        }
    }
}
impl KeyEngine {
    pub fn reset_statistics(&mut self) {
        for button in &mut self.buttons {
            button.reset();
        }
        self.custom.reset();
        self.wheel_up = 0;
        self.wheel_down = 0;
        self.wheel_up_until = 0.0;
        self.wheel_down_until = 0.0;
    }
    pub fn reset_transient(&mut self) {
        for button in &mut self.buttons {
            button.reset_pressed();
        }
        self.custom.reset_pressed();
        self.picking = false;
    }
    pub fn reset_custom(&mut self) {
        self.selected = None;
        self.ignored_release = None;
        self.picking = false;
        self.custom.reset();
    }
    /// Button order: left, middle, right, forward, back.
    pub fn mouse(&mut self, flags: u16, data: u16, timestamp: f64) {
        for (index, down) in [(0, 0x1), (1, 0x10), (2, 0x4), (3, 0x100), (4, 0x40)] {
            if flags & down != 0 {
                self.buttons[index].down(timestamp, self.mouse_threshold_ms);
            }
            if flags & (down << 1) != 0 {
                self.buttons[index].up(timestamp);
            }
        }
        if flags & 0x400 != 0 {
            let delta = data as i16;
            if delta > 0 {
                self.wheel_up += 1;
                self.wheel_up_until = timestamp + 150.0;
            } else if delta < 0 {
                self.wheel_down += 1;
                self.wheel_down_until = timestamp + 150.0;
            }
        }
    }
    pub fn keyboard(
        &mut self,
        key: KeyIdentity,
        down: bool,
        timestamp: f64,
        active: bool,
        text_entry: bool,
    ) {
        if self.picking {
            if !down {
                if self.ignored_release == Some(key) {
                    self.ignored_release = None;
                }
                return;
            }
            self.ignored_release = Some(key);
            self.picking = false;
            if key.virtual_key == 27 {
                return;
            }
            if self.selected != Some(key) {
                self.custom.reset();
            }
            self.selected = Some(key);
            return;
        }
        if !active || text_entry {
            return;
        }
        if self.ignored_release == Some(key) {
            if !down {
                self.ignored_release = None;
            }
        } else if self.selected == Some(key) {
            if down {
                self.custom.down(timestamp, self.key_threshold_ms);
            } else {
                self.custom.up(timestamp);
            }
        }
    }
    pub fn normalize_threshold(value: f64) -> f64 {
        if value > 1.0 && value < 1000.0 {
            value
        } else {
            80.0
        }
    }
}
