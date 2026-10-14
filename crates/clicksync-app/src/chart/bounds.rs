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

/// Bounded display-only estimate; every real point remains in the render/query geometry.
pub(super) fn interval_band(blocks: &[Arc<clicksync_chart::GeometryBlock>]) -> Option<[f64; 3]> {
    let total: usize = blocks.iter().map(|block| block.samples.len()).sum();
    if total < 24 {
        return None;
    }
    let stride = total.div_ceil(2048);
    let mut values = Vec::with_capacity(total.min(2048));
    let mut next = 0;
    let mut offset = 0;
    for block in blocks {
        while next < offset + block.samples.len() {
            let value = block.samples[next - offset].point.y.value();
            if value.is_finite() && value > 0.0 {
                values.push(value);
            }
            next += stride;
        }
        offset += block.samples.len();
    }
    if values.len() < 24 {
        return None;
    }
    values.sort_unstable_by(f64::total_cmp);
    let last = values.len() - 1;
    Some([values[last / 40], values[last / 2], values[last * 39 / 40]])
}
