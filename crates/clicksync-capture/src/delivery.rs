//! Optional, cumulative counts of decoded mouse reports delivered by Windows.
//! These are observations at the Raw Input entrance, not USB packet counters.
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

#[derive(Default)]
pub struct RawDelivery {
    pub enabled: AtomicBool,
    foreground: AtomicU64,
    background: AtomicU64,
    other: AtomicU64,
    selected_foreground: AtomicU64,
    selected_background: AtomicU64,
    selected_other: AtomicU64,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct DeliverySnapshot {
    pub foreground: u64,
    pub background: u64,
    pub other: u64,
    pub selected_foreground: u64,
    pub selected_background: u64,
    pub selected_other: u64,
}

impl RawDelivery {
    pub(crate) fn record(&self, code: usize, selected: bool) {
        if !self.enabled.load(Ordering::Relaxed) {
            return;
        }
        let (all, selected_counter) = match code {
            0 => (&self.foreground, &self.selected_foreground), // RIM_INPUT
            1 => (&self.background, &self.selected_background), // RIM_INPUTSINK
            _ => (&self.other, &self.selected_other),
        };
        all.fetch_add(1, Ordering::Relaxed);
        if selected {
            selected_counter.fetch_add(1, Ordering::Relaxed);
        }
    }

    pub fn snapshot(&self) -> DeliverySnapshot {
        DeliverySnapshot {
            foreground: self.foreground.load(Ordering::Relaxed),
            background: self.background.load(Ordering::Relaxed),
            other: self.other.load(Ordering::Relaxed),
            selected_foreground: self.selected_foreground.load(Ordering::Relaxed),
            selected_background: self.selected_background.load(Ordering::Relaxed),
            selected_other: self.selected_other.load(Ordering::Relaxed),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn delivery_counters_distinguish_selection_and_background_without_allocating() {
        let counts = RawDelivery::default();
        counts.record(0, true);
        assert_eq!(counts.snapshot(), DeliverySnapshot::default());
        counts.enabled.store(true, Ordering::Relaxed);
        let allocations = allocation_counter::measure(|| {
            for _ in 0..10_000 {
                counts.record(0, true);
                counts.record(1, true);
                counts.record(1, false);
                counts.record(2, false);
            }
        });
        assert_eq!(allocations.count_total, 0);
        assert_eq!(
            counts.snapshot(),
            DeliverySnapshot {
                foreground: 10_000,
                background: 20_000,
                other: 10_000,
                selected_foreground: 10_000,
                selected_background: 10_000,
                selected_other: 0,
            }
        );
    }
}
