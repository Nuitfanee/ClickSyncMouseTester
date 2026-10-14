use std::{ops::Range, sync::Arc};

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Coordinate {
    Integer(i64),
    Time { ticks: u64, frequency: u64 },
    Scalar(f64),
}

impl Coordinate {
    pub fn value(self) -> f64 {
        match self {
            Self::Integer(value) => value as f64,
            Self::Time { ticks, frequency } => ticks as f64 * 1000.0 / frequency as f64,
            Self::Scalar(value) => value,
        }
    }

    /// Subtract integers/ticks before conversion, including above 2^53.
    pub fn relative_to(self, origin: Self) -> f64 {
        match (self, origin) {
            (Self::Integer(value), Self::Integer(base)) => (value as i128 - base as i128) as f64,
            (
                Self::Time { ticks, frequency },
                Self::Time {
                    ticks: base,
                    frequency: base_frequency,
                },
            ) if frequency == base_frequency => {
                (ticks as i128 - base as i128) as f64 * 1000.0 / frequency as f64
            }
            _ => self.value() - origin.value(),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Point {
    pub x: Coordinate,
    pub y: Coordinate,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Sample {
    pub source_index: usize,
    pub segment_id: u32,
    pub point: Point,
}

#[derive(Clone, Debug, PartialEq)]
pub struct GeometryBlock {
    pub id: usize,
    pub content_version: u64,
    pub source_range: Range<usize>,
    pub origin: Point,
    /// A connector belongs to this block, not to the preceding block.
    pub connector: Option<Sample>,
    pub samples: Vec<Sample>,
    pub relative_points: Vec<[f64; 2]>,
    /// Bounds in local coordinates include the connector.
    pub bounds: [f64; 4],
}

impl GeometryBlock {
    fn new(id: usize, version: u64, samples: Vec<Sample>, previous: Option<Sample>) -> Self {
        let first = samples[0];
        let connector = previous.filter(|p| p.segment_id == first.segment_id);
        let origin = first.point;
        let relative_points: Vec<_> = samples
            .iter()
            .map(|s| {
                [
                    s.point.x.relative_to(origin.x),
                    s.point.y.relative_to(origin.y),
                ]
            })
            .collect();
        let mut bounds = [0.0_f64; 4];
        for p in relative_points.iter().copied().chain(connector.map(|s| {
            [
                s.point.x.relative_to(origin.x),
                s.point.y.relative_to(origin.y),
            ]
        })) {
            bounds[0] = bounds[0].min(p[0]);
            bounds[1] = bounds[1].max(p[0]);
            bounds[2] = bounds[2].min(p[1]);
            bounds[3] = bounds[3].max(p[1]);
        }
        Self {
            id,
            content_version: version,
            source_range: first.source_index..samples.last().unwrap().source_index + 1,
            origin,
            connector,
            samples,
            relative_points,
            bounds,
        }
    }

    /// Breaks do not become line segments. Repeated/zero-length points remain.
    pub fn segments(&self) -> impl Iterator<Item = (Sample, Sample)> + '_ {
        self.connector
            .map(|p| (p, self.samples[0]))
            .into_iter()
            .chain(self.samples.windows(2).map(|w| (w[0], w[1])))
            .filter(|(a, b)| a.segment_id == b.segment_id)
    }
}

#[derive(Clone, Debug)]
pub struct GeometryStore {
    capacity: usize,
    blocks: Vec<Arc<GeometryBlock>>,
}

impl GeometryStore {
    pub fn new(capacity: usize) -> Self {
        assert!(capacity > 0);
        Self {
            capacity,
            blocks: Vec::new(),
        }
    }

    pub fn blocks(&self) -> &[Arc<GeometryBlock>] {
        &self.blocks
    }

    pub fn samples(&self) -> impl Iterator<Item = Sample> + '_ {
        self.blocks.iter().flat_map(|b| b.samples.iter().copied())
    }

    pub(crate) fn replace_from(
        &mut self,
        from: usize,
        replacement: impl Iterator<Item = Sample>,
        version: u64,
    ) -> Range<usize> {
        let mut replacement = replacement.peekable();
        let mut first = self.blocks.partition_point(|b| b.source_range.end <= from);
        if first == self.blocks.len()
            && first > 0
            && self.blocks[first - 1].samples.len() < self.capacity
            && replacement.peek().is_some_and(|sample| {
                self.blocks[first - 1].samples.last().unwrap().segment_id == sample.segment_id
            })
        {
            first -= 1;
        }
        let mut head = self
            .blocks
            .get(first)
            .map(|b| {
                b.samples
                    .iter()
                    .copied()
                    .take_while(|s| s.source_index < from)
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default();
        self.blocks.truncate(first);
        let mut previous = self.blocks.last().and_then(|b| b.samples.last()).copied();
        for sample in replacement {
            if head.len() == self.capacity
                || head
                    .last()
                    .is_some_and(|p| p.segment_id != sample.segment_id)
            {
                let id = self.blocks.len();
                let last = head.last().copied();
                self.blocks.push(Arc::new(GeometryBlock::new(
                    id,
                    version,
                    std::mem::take(&mut head),
                    previous,
                )));
                previous = last;
            }
            head.push(sample);
        }
        if !head.is_empty() {
            let id = self.blocks.len();
            self.blocks
                .push(Arc::new(GeometryBlock::new(id, version, head, previous)));
        }
        first..self.blocks.len()
    }
}
