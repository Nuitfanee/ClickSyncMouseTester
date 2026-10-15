use clicksync_chart::{
    Coordinate, Point,
    gpu::{PreparedBlock, PreparedScene},
};
use std::{collections::HashMap, sync::Arc};

#[derive(Clone, Copy, Debug, Default, serde::Serialize)]
pub struct BoundsWork {
    pub scanned_instances: usize,
    pub reused_blocks: usize,
}

struct CachedBlock {
    // Keeping the Arc prevents pointer reuse while this cache entry is alive.
    block: Arc<PreparedBlock>,
    bounds: [f64; 4],
}

#[derive(Default)]
pub(super) struct BoundsCache {
    origin: Option<Point>,
    blocks: HashMap<usize, CachedBlock>,
}

impl BoundsCache {
    pub(super) fn measure(
        &mut self,
        scene: &PreparedScene,
        valid: &impl Fn() -> bool,
    ) -> Result<(Point, [f64; 4], BoundsWork), String> {
        let origin = scene.blocks.first().map(|b| b.origin).unwrap_or(Point {
            x: Coordinate::Scalar(0.0),
            y: Coordinate::Scalar(0.0),
        });
        if self.origin != Some(origin) {
            self.blocks.clear();
            self.origin = Some(origin);
        }
        let mut previous = std::mem::take(&mut self.blocks);
        let mut current = HashMap::with_capacity(scene.blocks.len());
        let mut result = [0.0_f64; 4];
        let mut work = BoundsWork::default();
        for block in &scene.blocks {
            if !valid() {
                return Err("chart target superseded".into());
            }
            let pointer = Arc::as_ptr(block) as usize;
            let cached = if let Some(cached) = previous.remove(&pointer) {
                debug_assert!(Arc::ptr_eq(&cached.block, block));
                work.reused_blocks += 1;
                cached
            } else {
                // Preserve the original subtract/convert/add order. Caching local
                // extrema and shifting them afterwards can change rounded bounds.
                let x = block.origin.x.relative_to(origin.x);
                let y = block.origin.y.relative_to(origin.y);
                let mut bounds = [0.0_f64; 4];
                for instance in &*block.instances {
                    for p in [instance.a, instance.b] {
                        bounds[0] = bounds[0].min(x + p[0] as f64);
                        bounds[1] = bounds[1].max(x + p[0] as f64);
                        bounds[2] = bounds[2].min(y + p[1] as f64);
                        bounds[3] = bounds[3].max(y + p[1] as f64);
                    }
                }
                work.scanned_instances += block.instances.len();
                CachedBlock {
                    block: block.clone(),
                    bounds,
                }
            };
            result[0] = result[0].min(cached.bounds[0]);
            result[1] = result[1].max(cached.bounds[1]);
            result[2] = result[2].min(cached.bounds[2]);
            result[3] = result[3].max(cached.bounds[3]);
            current.insert(pointer, cached);
        }
        self.blocks = current; // retain only blocks in the current scene
        Ok((origin, result, work))
    }
}

#[derive(Clone, Copy, Debug)]
pub struct ValueBand {
    pub origin: Coordinate,
    /// Relative values preserve small count differences above 2^53.
    pub quantiles: [f64; 3],
}

/// Bounded display-only estimate; every real point remains in the render/query geometry.
pub(super) fn value_band(
    blocks: &[Arc<clicksync_chart::GeometryBlock>],
    window: Option<(Coordinate, f64)>,
    ordered_x: impl Fn(usize) -> bool,
) -> Option<ValueBand> {
    let selected = |index: usize| {
        let block = &blocks[index];
        let mut range = 0..block.samples.len();
        if let Some((start, width)) = window {
            let offset = block.origin.x.relative_to(start);
            if offset + block.bounds[1] < 0.0 || offset + block.bounds[0] > width {
                range = 0..0;
            } else if ordered_x(index) {
                range.start = block
                    .samples
                    .partition_point(|s| s.point.x.relative_to(start) < 0.0);
                range.end = block
                    .samples
                    .partition_point(|s| s.point.x.relative_to(start) <= width);
            }
        }
        range
    };
    let total: usize = (0..blocks.len()).map(|index| selected(index).len()).sum();
    if total < 24 {
        return None;
    }
    let mut origin = None;
    let count = total.min(2048);
    let mut values = Vec::with_capacity(count);
    // One deterministic, jittered position per stratum avoids locking onto
    // periodic mouse motion (or periodic outliers) like a fixed stride does.
    let position = |slot: usize| {
        let start = slot * total / count;
        let end = (slot + 1) * total / count;
        let mut hash = (slot as u64).wrapping_add(0x9e3779b97f4a7c15);
        hash = (hash ^ (hash >> 30)).wrapping_mul(0xbf58476d1ce4e5b9);
        hash = (hash ^ (hash >> 27)).wrapping_mul(0x94d049bb133111eb);
        start + ((hash ^ (hash >> 31)) % (end - start) as u64) as usize
    };
    let mut slot = 0;
    let mut next = position(slot);
    let mut offset = 0;
    for (index, block) in blocks.iter().enumerate() {
        let range = selected(index);
        while slot < count && next < offset + range.len() {
            let point = block.samples[range.start + next - offset].point;
            if point.y.value().is_finite()
                && window.is_none_or(|(start, width)| {
                    (0.0..=width).contains(&point.x.relative_to(start))
                })
            {
                let value = point.y.relative_to(*origin.get_or_insert(point.y));
                if value.is_finite() {
                    values.push(value);
                }
            }
            slot += 1;
            if slot < count {
                next = position(slot);
            }
        }
        offset += range.len();
    }
    if values.len() < 24 {
        return None;
    }
    values.sort_unstable_by(f64::total_cmp);
    let last = values.len() - 1;
    Some(ValueBand {
        origin: origin?,
        quantiles: [
            values[last / 40],
            values[last / 2],
            values[last - last / 40],
        ],
    })
}
