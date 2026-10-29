//! # shilpi-project: project business records inside a ShilpiDB file
//!
//! A project's ShilpiDB data file holds its drawing geometry and also the figures that come from it: the
//! estimate, the billing stages, the tasks, the quantities and the purchase orders. Each of these is one
//! record whose payload starts with the tag `BIZ1` and a kind byte, followed by the fields in the
//! `shilpidb` codec. Business records carry an empty bounding box, so they never show up in a spatial
//! query. Records without the tag (drawing geometry) are left alone when the file is read or rewritten.
//!
//! [`read_file`] reads a data file as it is on disk (the layout `shilpid` saves). [`write_file`] writes the
//! business records back and keeps every other record as it was.

use shilpidb::{Bbox, Point, Reader, Writer, MAGIC};
use std::fs;
use std::io;
use std::path::Path;

/// The data file layout version this crate reads and writes, as saved by `shilpid`.
pub const FILE_VERSION: u32 = 1;
const TAG: &[u8; 4] = b"BIZ1";

#[derive(Debug, Clone, PartialEq, Default)]
pub struct EstimateItem {
    pub code: String,
    pub description: String,
    pub unit: String,
    pub quantity: f64,
    pub rate: f64,
}

impl EstimateItem {
    pub fn amount(&self) -> f64 {
        self.quantity * self.rate
    }
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct BillingStage {
    pub stage: String,
    pub description: String,
    pub percent: f64,
    pub amount: f64,
    /// `pending`, `invoiced` or `paid`.
    pub status: String,
    pub date: String,
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct Task {
    pub title: String,
    pub assignee: String,
    pub due: String,
    /// `open`, `doing` or `done`.
    pub status: String,
    pub percent: f64,
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct Quantity {
    pub item: String,
    pub description: String,
    pub unit: String,
    pub quantity: f64,
    /// Where the figure was measured, such as a drawing layer or sheet.
    pub source: String,
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct PurchaseOrder {
    pub number: String,
    pub vendor: String,
    pub date: String,
    pub description: String,
    pub amount: f64,
    /// `draft`, `issued`, `received` or `closed`.
    pub status: String,
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct ProjectData {
    pub estimate: Vec<EstimateItem>,
    pub billing: Vec<BillingStage>,
    pub tasks: Vec<Task>,
    pub quantities: Vec<Quantity>,
    pub orders: Vec<PurchaseOrder>,
    /// Records in the file that are not business records, such as drawing geometry.
    pub other_records: usize,
}

type RawRecord = (u64, Bbox, Vec<u8>);

fn bad(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message.into())
}

fn codec(e: shilpidb::Error) -> io::Error {
    bad(e.to_string())
}

fn raw_records(bytes: &[u8]) -> io::Result<Vec<RawRecord>> {
    let mut r = Reader::new(bytes);
    let magic = r.raw(MAGIC.len()).map_err(codec)?;
    if magic != MAGIC {
        return Err(bad("not a ShilpiDB data file"));
    }
    let version = r.u32().map_err(codec)?;
    if version != FILE_VERSION {
        return Err(bad(format!("unsupported ShilpiDB file version {version}")));
    }
    let n = r.u32().map_err(codec)? as usize;
    let mut out = Vec::with_capacity(n.min(1 << 20));
    for _ in 0..n {
        let id = r.u64().map_err(codec)?;
        let min = Point::new(r.f64().map_err(codec)?, r.f64().map_err(codec)?);
        let max = Point::new(r.f64().map_err(codec)?, r.f64().map_err(codec)?);
        let len = r.u32().map_err(codec)? as usize;
        let payload = r.raw(len).map_err(codec)?.to_vec();
        out.push((id, Bbox::new(min, max), payload));
    }
    Ok(out)
}

fn is_business(payload: &[u8]) -> bool {
    payload.len() > TAG.len() && &payload[..TAG.len()] == TAG
}

fn decode(payload: &[u8], data: &mut ProjectData) -> io::Result<()> {
    let mut r = Reader::new(&payload[TAG.len()..]);
    let kind = r.u8().map_err(codec)?;
    match kind {
        1 => {
            let (code, description, unit) = (r.str().map_err(codec)?, r.str().map_err(codec)?, r.str().map_err(codec)?);
            let (quantity, rate) = (r.f64().map_err(codec)?, r.f64().map_err(codec)?);
            data.estimate.push(EstimateItem { code, description, unit, quantity, rate });
        }
        2 => {
            let (stage, description) = (r.str().map_err(codec)?, r.str().map_err(codec)?);
            let (percent, amount) = (r.f64().map_err(codec)?, r.f64().map_err(codec)?);
            let (status, date) = (r.str().map_err(codec)?, r.str().map_err(codec)?);
            data.billing.push(BillingStage { stage, description, percent, amount, status, date });
        }
        3 => {
            let (title, assignee) = (r.str().map_err(codec)?, r.str().map_err(codec)?);
            let (due, status) = (r.str().map_err(codec)?, r.str().map_err(codec)?);
            let percent = r.f64().map_err(codec)?;
            data.tasks.push(Task { title, assignee, due, status, percent });
        }
        4 => {
            let (item, description, unit) = (r.str().map_err(codec)?, r.str().map_err(codec)?, r.str().map_err(codec)?);
            let quantity = r.f64().map_err(codec)?;
            let source = r.str().map_err(codec)?;
            data.quantities.push(Quantity { item, description, unit, quantity, source });
        }
        5 => {
            let (number, vendor) = (r.str().map_err(codec)?, r.str().map_err(codec)?);
            let (date, description) = (r.str().map_err(codec)?, r.str().map_err(codec)?);
            let amount = r.f64().map_err(codec)?;
            let status = r.str().map_err(codec)?;
            data.orders.push(PurchaseOrder { number, vendor, date, description, amount, status });
        }
        // A kind from a newer version is skipped rather than refused, so an older reader still opens the file.
        _ => data.other_records += 1,
    }
    Ok(())
}

/// Reads the business records of a ShilpiDB data file. The file is only read, never changed.
pub fn read_file(path: &Path) -> io::Result<ProjectData> {
    read_bytes(&fs::read(path)?)
}

pub fn read_bytes(bytes: &[u8]) -> io::Result<ProjectData> {
    let mut data = ProjectData::default();
    for (_, _, payload) in raw_records(bytes)? {
        if is_business(&payload) {
            decode(&payload, &mut data)?;
        } else {
            data.other_records += 1;
        }
    }
    Ok(data)
}

fn payload(kind: u8, fill: impl FnOnce(&mut Writer)) -> Vec<u8> {
    let mut w = Writer::new();
    w.raw(TAG);
    w.u8(kind);
    fill(&mut w);
    w.into_bytes()
}

fn encode(data: &ProjectData) -> Vec<Vec<u8>> {
    let mut out = Vec::new();
    for e in &data.estimate {
        out.push(payload(1, |w| {
            w.str(&e.code);
            w.str(&e.description);
            w.str(&e.unit);
            w.f64(e.quantity);
            w.f64(e.rate);
        }));
    }
    for b in &data.billing {
        out.push(payload(2, |w| {
            w.str(&b.stage);
            w.str(&b.description);
            w.f64(b.percent);
            w.f64(b.amount);
            w.str(&b.status);
            w.str(&b.date);
        }));
    }
    for t in &data.tasks {
        out.push(payload(3, |w| {
            w.str(&t.title);
            w.str(&t.assignee);
            w.str(&t.due);
            w.str(&t.status);
            w.f64(t.percent);
        }));
    }
    for q in &data.quantities {
        out.push(payload(4, |w| {
            w.str(&q.item);
            w.str(&q.description);
            w.str(&q.unit);
            w.f64(q.quantity);
            w.str(&q.source);
        }));
    }
    for p in &data.orders {
        out.push(payload(5, |w| {
            w.str(&p.number);
            w.str(&p.vendor);
            w.str(&p.date);
            w.str(&p.description);
            w.f64(p.amount);
            w.str(&p.status);
        }));
    }
    out
}

/// Writes `data` as the business records of the file at `path`, keeping every other record (drawing geometry) and
/// creating the file if it is missing. Used to make a project file and by tests; O-Studio itself only reads.
pub fn write_file(path: &Path, data: &ProjectData) -> io::Result<()> {
    let mut records: Vec<RawRecord> = if path.exists() {
        raw_records(&fs::read(path)?)?.into_iter().filter(|(_, _, p)| !is_business(p)).collect()
    } else {
        Vec::new()
    };
    let mut next = records.iter().map(|(id, _, _)| *id).max().map_or(1, |m| m + 1);
    let empty = Bbox::new(Point::new(0.0, 0.0), Point::new(0.0, 0.0));
    for p in encode(data) {
        records.push((next, empty, p));
        next += 1;
    }
    let mut w = Writer::new();
    w.raw(MAGIC);
    w.u32(FILE_VERSION);
    w.u32(records.len() as u32);
    for (id, bbox, p) in &records {
        w.u64(*id);
        w.f64(bbox.min.x);
        w.f64(bbox.min.y);
        w.f64(bbox.max.x);
        w.f64(bbox.max.y);
        w.u32(p.len() as u32);
        w.raw(p);
    }
    let tmp = path.with_extension("vdb.tmp");
    fs::write(&tmp, w.into_bytes())?;
    fs::rename(&tmp, path)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> ProjectData {
        ProjectData {
            estimate: vec![EstimateItem { code: "E1".into(), description: "RCC".into(), unit: "cum".into(), quantity: 12.5, rate: 8000.0 }],
            billing: vec![BillingStage { stage: "Concept".into(), description: "".into(), percent: 15.0, amount: 150000.0, status: "paid".into(), date: "2026-01-10".into() }],
            tasks: vec![Task { title: "Issue plans".into(), assignee: "Meena".into(), due: "2026-02-01".into(), status: "open".into(), percent: 40.0 }],
            quantities: vec![Quantity { item: "Wall".into(), description: "230 mm".into(), unit: "sqm".into(), quantity: 310.2, source: "layer A-WALL".into() }],
            orders: vec![PurchaseOrder { number: "PO-1".into(), vendor: "Mysuru Steel".into(), date: "2026-01-12".into(), description: "TMT".into(), amount: 420000.0, status: "issued".into() }],
            other_records: 0,
        }
    }

    #[test]
    fn records_round_trip_through_a_file_without_duplicating() {
        let dir = std::env::temp_dir().join(format!("shilpi-project-{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let file = dir.join("p.vdb");
        write_file(&file, &sample()).unwrap();
        let first = read_file(&file).unwrap();
        assert_eq!(first, sample());
        assert_eq!(first.estimate[0].amount(), 100000.0);
        write_file(&file, &sample()).unwrap();
        assert_eq!(read_file(&file).unwrap(), sample());
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn a_file_that_is_not_shilpidb_is_refused() {
        assert!(read_bytes(b"hello world, not a database").is_err());
    }
}
