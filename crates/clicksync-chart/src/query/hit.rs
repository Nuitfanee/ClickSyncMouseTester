//! Exact nearest-sample lookup. Trees are prepared on the analysis worker and
//! reuse immutable geometry blocks; pointer movement only traverses bounds.
use crate::{GeometryBlock, Point, Sample};
use std::{collections::HashMap, sync::Arc};

const LEAF_SAMPLES: usize = 32;

#[derive(Clone)]
struct Node {
    bounds: [f64; 4],
    range: std::ops::Range<usize>,
    children: Option<(usize, usize)>,
    first_source: usize,
}

struct BlockIndex {
    geometry: Arc<GeometryBlock>,
    order: Vec<usize>,
    nodes: Vec<Node>,
}

impl BlockIndex {
    fn new(geometry: Arc<GeometryBlock>) -> Self {
        let nodes = geometry
            .samples
            .len()
            .div_ceil(LEAF_SAMPLES)
            .next_power_of_two()
            * 2
            - 1;
        let mut index = Self {
            order: (0..geometry.samples.len()).collect(),
            geometry,
            nodes: Vec::with_capacity(nodes),
        };
        // Ordered X partitions need no permutation. The spatial Y partitions
        // must remain available for tall data and anisotropic hover queries.
        let ordered = index
            .geometry
            .relative_points
            .windows(2)
            .all(|p| p[0][0].total_cmp(&p[1][0]).is_le());
        index.build(0..index.order.len(), ordered);
        index
    }

    fn build(&mut self, range: std::ops::Range<usize>, ordered: bool) -> usize {
        let spatial_split = range.len() > LEAF_SAMPLES;
        if ordered
            && spatial_split
            && self.geometry.relative_points[range.end - 1][0]
                - self.geometry.relative_points[range.start][0]
                >= self.geometry.bounds[3] - self.geometry.bounds[2]
        {
            let node = self.nodes.len();
            self.nodes.push(Node {
                bounds: [0.0; 4],
                range: range.clone(),
                children: None,
                first_source: usize::MAX,
            });
            let middle = range.start + range.len() / 2;
            let left = self.build(range.start..middle, true);
            let right = self.build(middle..range.end, true);
            let a = &self.nodes[left];
            let b = &self.nodes[right];
            self.nodes[node] = Node {
                bounds: [
                    a.bounds[0].min(b.bounds[0]),
                    a.bounds[1].max(b.bounds[1]),
                    a.bounds[2].min(b.bounds[2]),
                    a.bounds[3].max(b.bounds[3]),
                ],
                first_source: a.first_source.min(b.first_source),
                range,
                children: Some((left, right)),
            };
            return node;
        }
        let mut bounds = [
            f64::INFINITY,
            f64::NEG_INFINITY,
            f64::INFINITY,
            f64::NEG_INFINITY,
        ];
        let mut first_source = usize::MAX;
        for &i in &self.order[range.clone()] {
            let p = self.geometry.relative_points[i];
            bounds[0] = bounds[0].min(p[0]);
            bounds[1] = bounds[1].max(p[0]);
            bounds[2] = bounds[2].min(p[1]);
            bounds[3] = bounds[3].max(p[1]);
            first_source = first_source.min(self.geometry.samples[i].source_index);
        }
        let node = self.nodes.len();
        self.nodes.push(Node {
            bounds,
            range: range.clone(),
            children: None,
            first_source,
        });
        if spatial_split {
            let axis = usize::from(bounds[3] - bounds[2] > bounds[1] - bounds[0]);
            let middle = range.start + range.len() / 2;
            let points = &self.geometry.relative_points;
            let ordered = ordered && axis == 0;
            if !ordered {
                self.order[range.clone()].select_nth_unstable_by(range.len() / 2, |a, b| {
                    points[*a][axis].total_cmp(&points[*b][axis]).then(a.cmp(b))
                });
            }
            let left = self.build(range.start..middle, ordered);
            let right = self.build(middle..range.end, ordered);
            self.nodes[node].children = Some((left, right));
        }
        node
    }

    fn search(
        &self,
        node: usize,
        target: [f64; 2],
        scale: [f64; 2],
        best: &mut Option<(f64, Sample)>,
    ) {
        let entry = &self.nodes[node];
        let lower = distance_to_bounds(entry.bounds, target, scale);
        if best.is_some_and(|(distance, sample)| {
            lower > distance || (lower == distance && entry.first_source >= sample.source_index)
        }) {
            return;
        }
        if let Some((left, right)) = entry.children {
            let a = distance_to_bounds(self.nodes[left].bounds, target, scale);
            let b = distance_to_bounds(self.nodes[right].bounds, target, scale);
            let (first, second) = if a <= b { (left, right) } else { (right, left) };
            self.search(first, target, scale, best);
            self.search(second, target, scale, best);
        } else {
            for &i in &self.order[entry.range.clone()] {
                let p = self.geometry.relative_points[i];
                let distance = ((p[0] - target[0]) * scale[0]).powi(2)
                    + ((p[1] - target[1]) * scale[1]).powi(2);
                let sample = self.geometry.samples[i];
                if best.is_none_or(|(d, s)| {
                    distance < d || (distance == d && sample.source_index < s.source_index)
                }) {
                    *best = Some((distance, sample));
                }
            }
        }
    }
}

fn distance_to_bounds(bounds: [f64; 4], point: [f64; 2], scale: [f64; 2]) -> f64 {
    ((point[0] - point[0].clamp(bounds[0], bounds[1])) * scale[0]).powi(2)
        + ((point[1] - point[1].clamp(bounds[2], bounds[3])) * scale[1]).powi(2)
}

#[derive(Default)]
pub struct HitIndex {
    blocks: Vec<Arc<BlockIndex>>,
}

impl HitIndex {
    /// Index payload only; referenced geometry blocks are shared with the scene.
    pub fn cached_index_bytes(&self) -> usize {
        self.blocks.capacity() * std::mem::size_of::<Arc<BlockIndex>>()
            + self
                .blocks
                .iter()
                .map(|block| {
                    std::mem::size_of::<BlockIndex>()
                        + block.order.capacity() * std::mem::size_of::<usize>()
                        + block.nodes.capacity() * std::mem::size_of::<Node>()
                })
                .sum::<usize>()
    }

    pub fn prepare(blocks: &[Arc<GeometryBlock>], previous: Option<&Self>) -> Self {
        // Geometry ids are local to each metric. Pointer identity also survives
        // insertion of new primary blocks before the secondary metric's blocks.
        let previous: HashMap<_, _> = previous
            .into_iter()
            .flat_map(|p| p.blocks.iter())
            .map(|block| (Arc::as_ptr(&block.geometry), block))
            .collect();
        Self {
            blocks: blocks
                .iter()
                .map(|block| {
                    previous
                        .get(&Arc::as_ptr(block))
                        .map(|old| Arc::clone(old))
                        .unwrap_or_else(|| Arc::new(BlockIndex::new(block.clone())))
                })
                .collect(),
        }
    }

    /// The target is an offset from `origin` in original data units. Integer
    /// and QPC origins are subtracted before conversion, preserving precision.
    pub fn nearest(
        &self,
        origin: Point,
        offset: [f64; 2],
        pixels_per_unit: [f64; 2],
    ) -> Option<Sample> {
        if offset
            .iter()
            .chain(&pixels_per_unit)
            .any(|v| !v.is_finite())
            || pixels_per_unit.iter().any(|v| *v <= 0.0)
        {
            return None;
        }
        let mut best = None;
        for block in &self.blocks {
            let target = [
                offset[0] - block.geometry.origin.x.relative_to(origin.x),
                offset[1] - block.geometry.origin.y.relative_to(origin.y),
            ];
            block.search(0, target, pixels_per_unit, &mut best);
        }
        best.map(|(_, sample)| sample)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Coordinate, GeometryStore};

    fn check(points: &[[i64; 2]]) {
        let origin = Point {
            x: Coordinate::Integer(1_i64 << 54),
            y: Coordinate::Integer(-(1_i64 << 54)),
        };
        let samples: Vec<_> = points
            .iter()
            .enumerate()
            .map(|(i, p)| Sample {
                source_index: i * 2,
                segment_id: 1,
                point: Point {
                    x: Coordinate::Integer((1_i64 << 54) + p[0]),
                    y: Coordinate::Integer(-(1_i64 << 54) + p[1]),
                },
            })
            .collect();
        let mut geometry = GeometryStore::new(257);
        geometry.replace_from(0, samples.iter().copied(), 1);
        let hit = HitIndex::prepare(geometry.blocks(), None);
        for block in &hit.blocks {
            let mut spatial = BlockIndex {
                geometry: block.geometry.clone(),
                order: (0..block.geometry.samples.len()).collect(),
                nodes: Vec::new(),
            };
            spatial.build(0..spatial.order.len(), false);
            assert_eq!(block.nodes.len(), spatial.nodes.len());
            for (actual, reference) in block.nodes.iter().zip(&spatial.nodes) {
                assert_eq!(actual.bounds, reference.bounds);
                assert_eq!(actual.children, reference.children);
                assert_eq!(actual.first_source, reference.first_source);
            }
        }
        for scale in [[1.0, 1.0], [0.001, 1000.0], [1000.0, 0.001]] {
            for i in 0..256 {
                let offset = [f64::from(i * 5 - 50), f64::from(i % 23 - 11)];
                let distance = |sample: &Sample| {
                    ((sample.point.x.relative_to(origin.x) - offset[0]) * scale[0]).powi(2)
                        + ((sample.point.y.relative_to(origin.y) - offset[1]) * scale[1]).powi(2)
                };
                let expected = samples.iter().min_by(|a, b| {
                    distance(a)
                        .total_cmp(&distance(b))
                        .then(a.source_index.cmp(&b.source_index))
                });
                assert_eq!(hit.nearest(origin, offset, scale).as_ref(), expected);
            }
        }
        let reused = HitIndex::prepare(geometry.blocks(), Some(&hit));
        assert!(
            reused
                .blocks
                .iter()
                .zip(&hit.blocks)
                .all(|(a, b)| Arc::ptr_eq(a, b))
        );
        if let Some(sample) = samples.last() {
            let from = sample.source_index;
            let mut replacement = *sample;
            replacement.point.y = Coordinate::Integer(-(1_i64 << 54) + 12345);
            geometry.replace_from(from, [replacement].into_iter(), 2);
            let changed = HitIndex::prepare(geometry.blocks(), Some(&hit));
            assert!(!Arc::ptr_eq(
                changed.blocks.last().unwrap(),
                hit.blocks.last().unwrap()
            ));
            assert_eq!(
                changed.nearest(
                    origin,
                    [replacement.point.x.relative_to(origin.x), 12345.0],
                    [1.0, 1.0],
                ),
                Some(replacement)
            );
        }
    }

    #[test]
    fn ordered_and_spatial_trees_match_exact_scan_with_ties_and_large_origins() {
        check(&[]);
        check(&[[0, 0]]);
        check(&vec![[5, 5]; 1025]);
        check(&(0..1025).map(|i| [i * 30, i % 7]).collect::<Vec<_>>());
        check(
            &(0..1025)
                .map(|i| [i / 3, (i * 73 % 113) - 56])
                .collect::<Vec<_>>(),
        );
        check(
            &(0..1025)
                .map(|i| [(i * 37 % 109) - 50, (i * 73 % 113) - 56])
                .collect::<Vec<_>>(),
        );
    }
}
