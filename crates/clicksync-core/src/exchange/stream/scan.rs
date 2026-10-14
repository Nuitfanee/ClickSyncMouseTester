use super::*;
use std::cell::Cell;
use std::rc::Rc;

struct CountingReader<R> {
    reader: R,
    position: Rc<Cell<u64>>,
}
impl<R: Read> Read for CountingReader<R> {
    fn read(&mut self, bytes: &mut [u8]) -> std::io::Result<usize> {
        let count = self.reader.read(bytes)?;
        self.position.set(self.position.get() + count as u64);
        Ok(count)
    }
}

pub(super) fn scan(reader: impl Read, limit: usize) -> Result<Scan, ExchangeError> {
    let position = Rc::new(Cell::new(0));
    let reader = CountingReader {
        reader,
        position: position.clone(),
    };
    let mut decoder = serde_json::Deserializer::from_reader(reader);
    let scan = ScanSeed { position, limit }.deserialize(&mut decoder)?;
    decoder.end()?;
    Ok(scan)
}

fn key(key: &str) -> String {
    key.chars()
        .filter(|c| *c != '_')
        .flat_map(char::to_lowercase)
        .collect()
}
struct ScanSeed {
    position: Rc<Cell<u64>>,
    limit: usize,
}
impl<'de> DeserializeSeed<'de> for ScanSeed {
    type Value = Scan;
    fn deserialize<D: serde::Deserializer<'de>>(self, decoder: D) -> Result<Scan, D::Error> {
        decoder.deserialize_map(self)
    }
}
impl<'de> Visitor<'de> for ScanSeed {
    type Value = Scan;
    fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
        f.write_str("exchange document")
    }
    fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Scan, M::Error> {
        let (mut version, mut mouse, mut session, mut segments) = (None, None, None, None);
        while let Some(name) = map.next_key::<String>()? {
            match key(&name).as_str() {
                "v" | "formatversion" => version = Some(map.next_value()?),
                "m" | "mouse" => mouse = Some(map.next_value_seed(MouseSeed)?),
                "s" | "session" => session = Some(map.next_value_seed(SessionSeed)?),
                "g" | "segments" => {
                    if segments.is_some() {
                        return Err(M::Error::custom("duplicate segments"));
                    }
                    segments = Some(map.next_value_seed(AnchorsSeed {
                        position: self.position.clone(),
                        limit: self.limit,
                    })?);
                }
                _ => {
                    map.next_value::<IgnoredAny>()?;
                }
            }
        }
        Ok(Scan {
            version: version.ok_or_else(|| M::Error::custom("missing version"))?,
            mouse: mouse.ok_or_else(|| M::Error::custom("missing mouse metadata"))?,
            session: session.ok_or_else(|| M::Error::custom("missing session metadata"))?,
            segments: segments.ok_or_else(|| M::Error::custom("missing segments"))?,
        })
    }
}
struct MouseSeed;
impl<'de> DeserializeSeed<'de> for MouseSeed {
    type Value = ExchangeMouseDocument;
    fn deserialize<D: serde::Deserializer<'de>>(self, decoder: D) -> Result<Self::Value, D::Error> {
        decoder.deserialize_map(self)
    }
}
impl<'de> Visitor<'de> for MouseSeed {
    type Value = ExchangeMouseDocument;
    fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
        f.write_str("mouse metadata")
    }
    fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Self::Value, M::Error> {
        let mut mouse = ExchangeMouseDocument::default();
        while let Some(name) = map.next_key::<String>()? {
            match key(&name).as_str() {
                "n" | "displayname" => mouse.display_name = map.next_value()?,
                "d" | "deviceid" => mouse.device_id = map.next_value()?,
                "vi" | "vendorid" => mouse.vendor_id = map.next_value()?,
                "pi" | "productid" => mouse.product_id = map.next_value()?,
                "b" | "buttoncount" => mouse.button_count = map.next_value()?,
                "x" | "isvirtual" => mouse.is_virtual = map.next_value()?,
                "p" | "pathsummary" => mouse.path_summary = map.next_value()?,
                _ => {
                    map.next_value::<IgnoredAny>()?;
                }
            }
        }
        Ok(mouse)
    }
}
struct SessionSeed;
impl<'de> DeserializeSeed<'de> for SessionSeed {
    type Value = ExchangeSessionDocument;
    fn deserialize<D: serde::Deserializer<'de>>(self, decoder: D) -> Result<Self::Value, D::Error> {
        decoder.deserialize_map(self)
    }
}
impl<'de> Visitor<'de> for SessionSeed {
    type Value = ExchangeSessionDocument;
    fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
        f.write_str("session metadata")
    }
    fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Self::Value, M::Error> {
        let mut session = ExchangeSessionDocument::default();
        let mut evidence_seen = false;
        while let Some(name) = map.next_key::<String>()? {
            match key(&name).as_str() {
                "c" | "effectivecpi" => session.effective_cpi = map.next_value()?,
                "f" | "hosttimestampfrequency" => {
                    session.host_timestamp_frequency = map.next_value()?
                }
                "e" | "captureevidence" => {
                    if evidence_seen {
                        return Err(M::Error::custom("duplicate capture evidence"));
                    }
                    evidence_seen = true;
                    session.capture_evidence = map.next_value()?;
                }
                _ => {
                    map.next_value::<IgnoredAny>()?;
                }
            }
        }
        Ok(session)
    }
}
struct AnchorsSeed {
    position: Rc<Cell<u64>>,
    limit: usize,
}
impl<'de> DeserializeSeed<'de> for AnchorsSeed {
    type Value = Vec<Anchor>;
    fn deserialize<D: serde::Deserializer<'de>>(self, decoder: D) -> Result<Self::Value, D::Error> {
        decoder.deserialize_seq(self)
    }
}
impl<'de> Visitor<'de> for AnchorsSeed {
    type Value = Vec<Anchor>;
    fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
        f.write_str("segment anchors")
    }
    fn visit_seq<S: SeqAccess<'de>>(self, mut seq: S) -> Result<Self::Value, S::Error> {
        let mut anchors = Vec::new();
        while let Some(anchor) = seq.next_element_seed(AnchorSeed(self.position.clone()))? {
            if anchors.len() >= self.limit {
                return Err(S::Error::custom("exchange exceeds the session event limit"));
            }
            if let Some(anchor) = anchor {
                anchors.push(anchor);
            }
        }
        anchors.sort_by_key(|anchor| anchor.id);
        Ok(anchors)
    }
}
struct AnchorSeed(Rc<Cell<u64>>);
impl<'de> DeserializeSeed<'de> for AnchorSeed {
    type Value = Option<Anchor>;
    fn deserialize<D: serde::Deserializer<'de>>(self, decoder: D) -> Result<Self::Value, D::Error> {
        decoder.deserialize_option(self)
    }
}
impl<'de> Visitor<'de> for AnchorSeed {
    type Value = Option<Anchor>;
    fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
        f.write_str("segment anchor or null")
    }
    fn visit_none<E: serde::de::Error>(self) -> Result<Self::Value, E> {
        Ok(None)
    }
    fn visit_some<D: serde::Deserializer<'de>>(self, decoder: D) -> Result<Self::Value, D::Error> {
        let offset = self.0.get().saturating_sub(1);
        decoder.deserialize_map(AnchorMap { offset }).map(Some)
    }
}
struct AnchorMap {
    offset: u64,
}
impl<'de> Visitor<'de> for AnchorMap {
    type Value = Anchor;
    fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
        f.write_str("segment anchor")
    }
    fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Anchor, M::Error> {
        let (mut id, mut ticks, mut sequence, mut rows) = (0, 0, 0, false);
        while let Some(name) = map.next_key::<String>()? {
            match key(&name).as_str() {
                "i" | "segmentid" => id = map.next_value()?,
                "t" | "startedatrawcaptureticks" => ticks = map.next_value()?,
                "q" | "basecapturesequence" => sequence = map.next_value()?,
                "r" | "packetrows" => {
                    map.next_value::<IgnoredAny>()?;
                    rows = true;
                }
                _ => {
                    map.next_value::<IgnoredAny>()?;
                }
            }
        }
        if !rows {
            return Err(M::Error::custom("missing packet rows"));
        }
        Ok(Anchor {
            id,
            ticks,
            sequence,
            offset: self.offset,
        })
    }
}
