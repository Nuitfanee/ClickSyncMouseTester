use super::Instance;
use std::{
    ops::{Deref, Range},
    sync::Arc,
};

const VISIBILITY_PAGE: usize = 256;

/// Immutable draw payload with validation computed by its producing CPU worker.
/// Replacing a payload recomputes validity; cloning or presenting it never scans it again.
#[derive(Clone, Debug)]
pub struct PreparedInstances {
    values: Arc<Vec<Instance>>,
    valid: bool,
    bounds: [f32; 4],
    pages: Arc<[[f32; 4]]>,
}

impl From<Vec<Instance>> for PreparedInstances {
    fn from(values: Vec<Instance>) -> Self {
        let mut bounds = [
            f32::INFINITY,
            f32::NEG_INFINITY,
            f32::INFINITY,
            f32::NEG_INFINITY,
        ];
        let mut valid = true;
        let pages: Vec<_> = values
            .chunks(VISIBILITY_PAGE)
            .map(|values| {
                let mut page = [
                    f32::INFINITY,
                    f32::NEG_INFINITY,
                    f32::INFINITY,
                    f32::NEG_INFINITY,
                ];
                for i in values {
                    valid &= i.kind <= 4 && i.a.iter().chain(&i.b).all(|v| v.is_finite());
                    for p in [i.a, i.b] {
                        page[0] = page[0].min(p[0]);
                        page[1] = page[1].max(p[0]);
                        page[2] = page[2].min(p[1]);
                        page[3] = page[3].max(p[1]);
                    }
                }
                bounds[0] = bounds[0].min(page[0]);
                bounds[1] = bounds[1].max(page[1]);
                bounds[2] = bounds[2].min(page[2]);
                bounds[3] = bounds[3].max(page[3]);
                page
            })
            .collect();
        Self {
            // Keep ownership of the produced Vec; Vec -> Arc<[T]> copies its entire payload.
            values: Arc::new(values),
            valid,
            bounds,
            pages: pages.into(),
        }
    }
}

impl Deref for PreparedInstances {
    type Target = [Instance];

    fn deref(&self) -> &Self::Target {
        &self.values
    }
}

impl PreparedInstances {
    pub(super) fn is_valid(&self) -> bool {
        self.valid
    }

    pub(super) fn intersects(&self, origin: super::super::Point, spec: &super::RenderSpec) -> bool {
        Self::bounds_intersect(self.bounds, origin, spec)
    }

    fn bounds_intersect(
        bounds: [f32; 4],
        origin: super::super::Point,
        spec: &super::RenderSpec,
    ) -> bool {
        let x = origin.x.relative_to(spec.viewport.origin.x);
        let y = origin.y.relative_to(spec.viewport.origin.y);
        let margin = f64::from(spec.radius.max(spec.line_width) + 1.0);
        let mx = margin * spec.viewport.width / f64::from(spec.width);
        let my = margin * spec.viewport.height / f64::from(spec.height);
        x + f64::from(bounds[1]) >= -mx
            && x + f64::from(bounds[0]) <= spec.viewport.width + mx
            && y + f64::from(bounds[3]) >= -my
            && y + f64::from(bounds[2]) <= spec.viewport.height + my
    }

    /// Conservative page bounds preserve primitive order, connectors and alpha blending.
    /// Built once with the immutable payload, never by scanning instances on the UI thread.
    pub(super) fn visible_range(
        &self,
        from: usize,
        origin: super::super::Point,
        spec: &super::RenderSpec,
    ) -> Option<Range<usize>> {
        if from >= self.len() {
            return None;
        }
        let mut first = from / VISIBILITY_PAGE;
        while first < self.pages.len() && !Self::bounds_intersect(self.pages[first], origin, spec) {
            first += 1;
        }
        if first == self.pages.len() {
            return None;
        }
        let mut end = first + 1;
        while end < self.pages.len() && Self::bounds_intersect(self.pages[end], origin, spec) {
            end += 1;
        }
        Some(from.max(first * VISIBILITY_PAGE)..(end * VISIBILITY_PAGE).min(self.len()))
    }

    pub fn shares_storage_with(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.values, &other.values)
    }

    /// Owned payload capacity, excluding allocator/Arc headers.
    pub fn cached_payload_bytes(&self) -> usize {
        self.values.capacity() * std::mem::size_of::<Instance>()
            + self.pages.len() * std::mem::size_of::<[f32; 4]>()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        Coordinate, Point,
        gpu::{RenderSpec, Viewport},
    };

    fn spec() -> RenderSpec {
        RenderSpec::new(
            Viewport {
                origin: Point {
                    x: Coordinate::Integer(1_i64 << 54),
                    y: Coordinate::Integer(1_i64 << 54),
                },
                width: 64.0,
                height: 64.0,
            },
            64,
            64,
            1,
        )
    }

    #[test]
    fn preparation_keeps_vector_storage_and_accounts_for_capacity_and_pages() {
        let mut values = Vec::with_capacity(1024);
        values.resize(513, Instance::point(1.0, 2.0));
        let pointer = values.as_ptr();
        let prepared: PreparedInstances = values.into();
        assert_eq!(pointer, prepared.as_ptr());
        assert_eq!(
            prepared.cached_payload_bytes(),
            1024 * std::mem::size_of::<Instance>() + 3 * std::mem::size_of::<[f32; 4]>()
        );
        assert!(prepared.shares_storage_with(&prepared.clone()));
    }

    #[test]
    fn visibility_keeps_crossing_segments_stems_rectangles_and_pixel_margins_in_order() {
        let spec = spec();
        let mut values = vec![Instance::point(-1000.0, -1000.0); 256 * 5 + 7];
        values[256] = Instance::line([-100.0, 24.0], [100.0, 24.0]);
        values[257] = Instance::connector([-100.0, 24.0], [100.0, 24.0]);
        values[512] = Instance::stem([24.0, -100.0], [24.0, 100.0]);
        values[1024] = Instance::bar([-100.0, -100.0], [100.0, 100.0]);
        values[1280] = Instance::point(-1.0, 24.0);
        let values: PreparedInstances = values.into();
        let mut from = 0;
        let mut ranges = Vec::new();
        while let Some(range) = values.visible_range(from, spec.viewport.origin, &spec) {
            from = range.end;
            ranges.push(range);
        }
        assert_eq!(ranges, [256..768, 1024..1287]);
        assert!(values.is_valid());
        assert!(
            values
                .visible_range(values.len(), spec.viewport.origin, &spec)
                .is_none()
        );
        assert!(
            values
                .visible_range(768, spec.viewport.origin, &spec)
                .unwrap()
                .contains(&1280)
        );
        assert!(values.shares_storage_with(&values.clone()));
    }

    #[test]
    fn page_visibility_is_conservative_for_every_primitive_even_with_nonmonotonic_x() {
        let spec = spec();
        let values: PreparedInstances = (0..4097)
            .map(|i| {
                let a = [(i % 107) as f32 - 25.0, (i % 97) as f32 - 20.0];
                let b = [(i % 193) as f32 - 90.0, (i % 173) as f32 - 85.0];
                Instance { a, b, kind: i % 5 }
            })
            .collect::<Vec<_>>()
            .into();
        let mut from = 0;
        let mut drawn = vec![false; values.len()];
        while let Some(range) = values.visible_range(from, spec.viewport.origin, &spec) {
            drawn[range.clone()].fill(true);
            from = range.end;
        }
        for (i, value) in values.iter().enumerate() {
            let bounds = [
                value.a[0].min(value.b[0]),
                value.a[0].max(value.b[0]),
                value.a[1].min(value.b[1]),
                value.a[1].max(value.b[1]),
            ];
            if PreparedInstances::bounds_intersect(bounds, spec.viewport.origin, &spec) {
                assert!(drawn[i], "visible primitive {i} was culled");
            }
        }
    }
}
