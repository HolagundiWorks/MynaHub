//! Drawings: the ShilpiDB engine inside the company file.
//!
//! A drawing is a 2D vector drawing (lines, polylines, arcs, circles, text, points on named layers) kept as geometry, not pixels, and
//! linked to a project. It is stored in the `shilpi_drawings` table in ShilpiDB's own `.vdb` layout, so the same bytes can be handed
//! to a `shilpid` server unchanged. Windows over a drawing are answered by ShilpiDB's spatial grid, so a large sheet can be shown a
//! part at a time. Drawings come in from ASCII DXF files; shapes the reader does not know (blocks, splines, hatches) are counted and
//! reported, never silently dropped.
//!
//! The table name starts with `shilpi_` as the shared-file rules require of an app's own tables; it is created when first used.

use crate::schema::audit;
use rusqlite::{Connection, OptionalExtension, params};
use serde_json::{Value, json};
use shilpi_drawing::{Drawing, Entity, Geometry, Layer, Units};
use shilpidb::{Bbox, Point};
use std::collections::HashMap;
use std::f64::consts::TAU;

/// Most shapes one view call returns; a caller zooms in (a smaller window) to see the rest.
pub const VIEW_LIMIT: usize = 20_000;

fn ensure(conn: &Connection) -> Result<(), String> {
    conn.execute_batch(
        "CREATE TABLE IF NOT EXISTS shilpi_drawings (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id INTEGER NOT NULL REFERENCES projects(id),
            name TEXT NOT NULL,
            source TEXT NOT NULL DEFAULT '',
            units TEXT NOT NULL DEFAULT 'mm',
            entity_count INTEGER NOT NULL DEFAULT 0,
            layer_count INTEGER NOT NULL DEFAULT 0,
            skipped INTEGER NOT NULL DEFAULT 0,
            min_x REAL, min_y REAL, max_x REAL, max_y REAL,
            vdb BLOB NOT NULL,
            created_by INTEGER,
            created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX IF NOT EXISTS shilpi_drawings_project ON shilpi_drawings(project_id);",
    )
    .map_err(|e| e.to_string())
}

fn units_name(units: Units) -> &'static str {
    match units {
        Units::Millimeter => "mm",
        Units::Meter => "m",
        Units::Inch => "in",
        Units::Foot => "ft",
        Units::Unitless => "",
    }
}

// ---- DXF reading ----

/// What a DXF read produced.
#[derive(Debug)]
pub struct Parsed {
    pub drawing: Drawing,
    pub skipped: usize,
    pub skipped_kinds: Vec<String>,
}

fn pairs(text: &str) -> Vec<(i32, String)> {
    let mut out = Vec::new();
    let mut lines = text.lines();
    while let (Some(code), Some(value)) = (lines.next(), lines.next()) {
        if let Ok(code) = code.trim().parse::<i32>() {
            out.push((code, value.trim().to_string()));
        }
    }
    out
}

fn num(value: &str) -> f64 {
    value.parse::<f64>().unwrap_or(0.0)
}

/// Reads the ENTITIES of an ASCII DXF file: LINE, LWPOLYLINE, POLYLINE with VERTEX, CIRCLE, ARC, TEXT, MTEXT and POINT.
pub fn parse_dxf(text: &str) -> Result<Parsed, String> {
    let pairs = pairs(text);
    if !pairs.iter().any(|(c, v)| *c == 0 && v == "SECTION") {
        return Err("This is not an ASCII DXF file. Save the drawing as DXF (text) and try again.".into());
    }
    let mut units = Units::Millimeter;
    for (i, (code, value)) in pairs.iter().enumerate() {
        if *code == 9 && value == "$INSUNITS" {
            units = match pairs.get(i + 1).map(|p| p.1.as_str()) {
                Some("1") => Units::Inch,
                Some("2") => Units::Foot,
                Some("6") => Units::Meter,
                Some("4") | None => Units::Millimeter,
                Some("0") => Units::Unitless,
                Some(_) => Units::Millimeter,
            };
            break;
        }
    }

    // Find the ENTITIES section.
    let mut start = None;
    for i in 0..pairs.len().saturating_sub(1) {
        if pairs[i] == (0, "SECTION".to_string()) && pairs[i + 1] == (2, "ENTITIES".to_string()) {
            start = Some(i + 2);
            break;
        }
    }
    let start = start.ok_or("The DXF file has no ENTITIES section.")?;

    let mut drawing = Drawing::new(units);
    let mut layer_index: HashMap<String, u32> = HashMap::new();
    layer_index.insert("0".into(), 0);
    let mut next_id = 1u64;
    let mut skipped = 0usize;
    let mut kinds: HashMap<String, usize> = HashMap::new();

    // Group the section into records: an entity starts at each code 0.
    let mut records: Vec<(String, Vec<(i32, String)>)> = Vec::new();
    for (code, value) in &pairs[start..] {
        if *code == 0 {
            if value == "ENDSEC" {
                break;
            }
            records.push((value.clone(), Vec::new()));
        } else if let Some(last) = records.last_mut() {
            last.1.push((*code, value.clone()));
        }
    }

    let mut poly: Option<(u32, Vec<Point>, bool)> = None;
    for (kind, data) in records {
        let layer_name = data.iter().find(|(c, _)| *c == 8).map(|(_, v)| v.clone()).unwrap_or_else(|| "0".into());
        let mut layer_of = |drawing: &mut Drawing| -> u32 {
            if let Some(i) = layer_index.get(&layer_name) {
                return *i;
            }
            let i = drawing.add_layer(Layer::new(layer_name.clone()));
            layer_index.insert(layer_name.clone(), i);
            i
        };
        let first = |code: i32| data.iter().find(|(c, _)| *c == code).map(|(_, v)| num(v));
        let mut add = |drawing: &mut Drawing, layer: u32, geometry: Geometry| {
            drawing.push(next_id, layer, geometry);
            next_id += 1;
        };
        match kind.as_str() {
            "LINE" => {
                let layer = layer_of(&mut drawing);
                let (x0, y0, x1, y1) = (first(10).unwrap_or(0.0), first(20).unwrap_or(0.0), first(11).unwrap_or(0.0), first(21).unwrap_or(0.0));
                add(&mut drawing, layer, Geometry::Polyline { points: vec![Point { x: x0, y: y0 }, Point { x: x1, y: y1 }], closed: false });
            }
            "LWPOLYLINE" => {
                let layer = layer_of(&mut drawing);
                let mut points = Vec::new();
                let mut x = None;
                for (c, v) in &data {
                    match c {
                        10 => x = Some(num(v)),
                        20 => {
                            if let Some(px) = x.take() {
                                points.push(Point { x: px, y: num(v) });
                            }
                        }
                        _ => {}
                    }
                }
                let closed = first(70).is_some_and(|f| (f as i64) & 1 == 1);
                if points.len() >= 2 {
                    add(&mut drawing, layer, Geometry::Polyline { points, closed });
                }
            }
            "POLYLINE" => {
                let layer = layer_of(&mut drawing);
                poly = Some((layer, Vec::new(), first(70).is_some_and(|f| (f as i64) & 1 == 1)));
            }
            "VERTEX" => {
                if let Some((_, points, _)) = poly.as_mut() {
                    points.push(Point { x: first(10).unwrap_or(0.0), y: first(20).unwrap_or(0.0) });
                }
            }
            "SEQEND" => {
                if let Some((layer, points, closed)) = poly.take()
                    && points.len() >= 2
                {
                    add(&mut drawing, layer, Geometry::Polyline { points, closed });
                }
            }
            "CIRCLE" => {
                let layer = layer_of(&mut drawing);
                add(&mut drawing, layer, Geometry::Arc { center: Point { x: first(10).unwrap_or(0.0), y: first(20).unwrap_or(0.0) }, radius: first(40).unwrap_or(0.0), start_angle: 0.0, end_angle: TAU });
            }
            "ARC" => {
                let layer = layer_of(&mut drawing);
                let (a0, a1) = (first(50).unwrap_or(0.0).to_radians(), first(51).unwrap_or(0.0).to_radians());
                let a1 = if a1 <= a0 { a1 + TAU } else { a1 };
                add(&mut drawing, layer, Geometry::Arc { center: Point { x: first(10).unwrap_or(0.0), y: first(20).unwrap_or(0.0) }, radius: first(40).unwrap_or(0.0), start_angle: a0, end_angle: a1 });
            }
            "TEXT" | "MTEXT" => {
                let layer = layer_of(&mut drawing);
                let content: String = data.iter().filter(|(c, _)| *c == 1 || *c == 3).map(|(_, v)| v.as_str()).collect::<Vec<_>>().concat();
                add(&mut drawing, layer, Geometry::Text { at: Point { x: first(10).unwrap_or(0.0), y: first(20).unwrap_or(0.0) }, height: first(40).unwrap_or(2.5), content });
            }
            "POINT" => {
                let layer = layer_of(&mut drawing);
                add(&mut drawing, layer, Geometry::Point(Point { x: first(10).unwrap_or(0.0), y: first(20).unwrap_or(0.0) }));
            }
            "" => {}
            other => {
                skipped += 1;
                *kinds.entry(other.to_string()).or_default() += 1;
            }
        }
    }
    let mut skipped_kinds: Vec<String> = kinds.into_iter().map(|(k, n)| format!("{k} x{n}")).collect();
    skipped_kinds.sort();
    Ok(Parsed { drawing, skipped, skipped_kinds })
}

// ---- storage ----

/// Reads a DXF file from disk and keeps it as a drawing of the project.
pub fn import_dxf(conn: &Connection, user: i64, project_id: i64, name: &str, path: &str) -> Result<Value, String> {
    ensure(conn)?;
    let known: Option<i64> = conn.query_row("SELECT id FROM projects WHERE id = ?1", [project_id], |r| r.get(0)).optional().map_err(|e| e.to_string())?;
    if known.is_none() {
        return Err("The project was not found.".into());
    }
    let bytes = std::fs::read(path).map_err(|e| format!("The file could not be read: {e}"))?;
    let text = String::from_utf8_lossy(&bytes);
    let parsed = parse_dxf(&text)?;
    if parsed.drawing.entities.is_empty() {
        return Err("The DXF file has no lines, polylines, circles, arcs, text or points that can be read.".into());
    }
    let name = if name.trim().is_empty() {
        std::path::Path::new(path).file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_else(|| "Drawing".into())
    } else {
        name.trim().to_string()
    };
    let bbox = parsed.drawing.bbox();
    let id = {
        conn.execute(
            "INSERT INTO shilpi_drawings (project_id, name, source, units, entity_count, layer_count, skipped, min_x, min_y, max_x, max_y, vdb, created_by)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)",
            params![
                project_id,
                name,
                std::path::Path::new(path).file_name().map(|s| s.to_string_lossy().to_string()).unwrap_or_default(),
                units_name(parsed.drawing.units),
                parsed.drawing.entities.len() as i64,
                parsed.drawing.layers.len() as i64,
                parsed.skipped as i64,
                bbox.map(|b| b.min.x),
                bbox.map(|b| b.min.y),
                bbox.map(|b| b.max.x),
                bbox.map(|b| b.max.y),
                parsed.drawing.to_bytes(),
                user
            ],
        )
        .map_err(|e| e.to_string())?;
        conn.last_insert_rowid()
    };
    audit(conn, Some(user), "drawing_imported", &format!("drawing {id} ({} shapes)", parsed.drawing.entities.len()));
    Ok(json!({ "id": id, "entities": parsed.drawing.entities.len(), "layers": parsed.drawing.layers.len(), "skipped": parsed.skipped, "skipped_kinds": parsed.skipped_kinds }))
}

/// The drawings of one project, or of every project, without their geometry.
pub fn list(conn: &Connection, project_id: Option<i64>) -> Result<Vec<Value>, String> {
    ensure(conn)?;
    let mut stmt = conn
        .prepare(
            "SELECT d.id, d.project_id, p.name, d.name, d.source, d.units, d.entity_count, d.layer_count, d.skipped, d.min_x, d.min_y, d.max_x, d.max_y, d.created_at
             FROM shilpi_drawings d JOIN projects p ON p.id = d.project_id
             WHERE (?1 IS NULL OR d.project_id = ?1) ORDER BY d.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([project_id], |r| {
            let (min_x, min_y, max_x, max_y): (Option<f64>, Option<f64>, Option<f64>, Option<f64>) = (r.get(9)?, r.get(10)?, r.get(11)?, r.get(12)?);
            Ok(json!({
                "id": r.get::<_, i64>(0)?, "project_id": r.get::<_, i64>(1)?, "project": r.get::<_, String>(2)?, "name": r.get::<_, String>(3)?,
                "source": r.get::<_, String>(4)?, "units": r.get::<_, String>(5)?, "entities": r.get::<_, i64>(6)?, "layers": r.get::<_, i64>(7)?,
                "skipped": r.get::<_, i64>(8)?,
                "width": max_x.zip(min_x).map(|(a, b)| a - b), "height": max_y.zip(min_y).map(|(a, b)| a - b),
                "created_at": r.get::<_, String>(13)?,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

fn load(conn: &Connection, id: i64) -> Result<(String, Drawing), String> {
    ensure(conn)?;
    let (name, blob): (String, Vec<u8>) = conn
        .query_row("SELECT name, vdb FROM shilpi_drawings WHERE id = ?1", [id], |r| Ok((r.get(0)?, r.get(1)?)))
        .optional()
        .map_err(|e| e.to_string())?
        .ok_or("The drawing was not found.")?;
    let drawing = Drawing::read(&blob).map_err(|e| format!("The stored drawing could not be read: {e}"))?;
    Ok((name, drawing))
}

fn shape(e: &Entity) -> Value {
    match &e.geometry {
        Geometry::Point(p) => json!({ "id": e.id, "layer": e.layer, "t": "point", "pts": [[p.x, p.y]] }),
        Geometry::Polyline { points, closed } => json!({ "id": e.id, "layer": e.layer, "t": "poly", "closed": closed, "pts": points.iter().map(|p| [p.x, p.y]).collect::<Vec<_>>() }),
        Geometry::Arc { center, radius, start_angle, end_angle } => json!({ "id": e.id, "layer": e.layer, "t": "arc", "c": [center.x, center.y], "r": radius, "a0": start_angle, "a1": end_angle }),
        Geometry::Text { at, height, content } => json!({ "id": e.id, "layer": e.layer, "t": "text", "pts": [[at.x, at.y]], "h": height, "text": content }),
    }
}

/// The shapes of a drawing, all of them or only those whose box meets `window` = [min_x, min_y, max_x, max_y], found by the spatial grid.
pub fn view(conn: &Connection, id: i64, window: Option<[f64; 4]>) -> Result<Value, String> {
    let (name, drawing) = load(conn, id)?;
    let mut shapes = Vec::new();
    let mut truncated = false;
    match window {
        Some([x0, y0, x1, y1]) => {
            let index = drawing.spatial_index();
            let mut hits = index.query_bbox(Bbox::from_corners(x0, y0, x1, y1));
            hits.sort_unstable();
            let by_id: HashMap<u64, &Entity> = drawing.entities.iter().map(|e| (e.id, e)).collect();
            for hit in hits {
                if shapes.len() >= VIEW_LIMIT {
                    truncated = true;
                    break;
                }
                if let Some(e) = by_id.get(&hit) {
                    shapes.push(shape(e));
                }
            }
        }
        None => {
            for e in &drawing.entities {
                if shapes.len() >= VIEW_LIMIT {
                    truncated = true;
                    break;
                }
                shapes.push(shape(e));
            }
        }
    }
    let bbox = drawing.bbox().map(|b| json!([b.min.x, b.min.y, b.max.x, b.max.y]));
    Ok(json!({
        "id": id, "name": name, "units": units_name(drawing.units), "bbox": bbox,
        "layers": drawing.layers.iter().map(|l| json!({ "name": l.name, "visible": l.visible })).collect::<Vec<_>>(),
        "total": drawing.entities.len(), "shapes": shapes, "truncated": truncated,
    }))
}

/// Shapes whose box is within `tolerance` of a point: what is under the cursor.
pub fn at_point(conn: &Connection, id: i64, x: f64, y: f64, tolerance: f64) -> Result<Vec<Value>, String> {
    let (_, drawing) = load(conn, id)?;
    let index = drawing.spatial_index();
    let hits = index.query_point(Point { x, y }, tolerance);
    let by_id: HashMap<u64, &Entity> = drawing.entities.iter().map(|e| (e.id, e)).collect();
    Ok(hits.into_iter().filter_map(|h| by_id.get(&h).map(|e| shape(e))).take(50).collect())
}

pub fn delete(conn: &Connection, user: i64, id: i64) -> Result<(), String> {
    ensure(conn)?;
    let n = conn.execute("DELETE FROM shilpi_drawings WHERE id = ?1", [id]).map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("The drawing was not found.".into());
    }
    audit(conn, Some(user), "drawing_deleted", &format!("drawing {id}"));
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    const DXF: &str = "0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n4\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n\
        0\nLINE\n8\nWALLS\n10\n0.0\n20\n0.0\n11\n5000.0\n21\n0.0\n\
        0\nLWPOLYLINE\n8\nWALLS\n70\n1\n10\n0\n20\n0\n10\n5000\n20\n0\n10\n5000\n20\n4000\n10\n0\n20\n4000\n\
        0\nCIRCLE\n8\nCOLUMNS\n10\n2500\n20\n2000\n40\n150\n\
        0\nARC\n8\nDOORS\n10\n1000\n20\n0\n40\n900\n50\n0\n51\n90\n\
        0\nTEXT\n8\nNOTES\n10\n100\n20\n100\n40\n200\n1\nLiving\n\
        0\nSPLINE\n8\nWALLS\n0\nENDSEC\n0\nEOF\n";

    fn conn() -> Connection {
        let c = Connection::open_in_memory().unwrap();
        c.execute_batch("CREATE TABLE projects (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO projects VALUES (1, 'Villa');
            CREATE TABLE audit_log (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT, user_id INTEGER, event TEXT, detail TEXT);").unwrap();
        c
    }

    #[test]
    fn dxf_shapes_layers_and_unknowns_are_read_and_reported() {
        let p = parse_dxf(DXF).unwrap();
        assert_eq!(p.drawing.entities.len(), 5);
        assert_eq!(p.drawing.layers.len(), 5); // 0, WALLS, COLUMNS, DOORS, NOTES
        assert_eq!(p.skipped, 1);
        assert_eq!(p.skipped_kinds, vec!["SPLINE x1".to_string()]);
        assert_eq!(p.drawing.units, Units::Millimeter);
        let b = p.drawing.bbox().unwrap();
        assert_eq!((b.max.x as i64, b.max.y as i64), (5000, 4000));
    }

    #[test]
    fn a_file_that_is_not_dxf_is_refused_with_a_clear_message() {
        assert!(parse_dxf("hello").unwrap_err().contains("ASCII DXF"));
    }

    #[test]
    fn windows_find_only_the_shapes_in_them() {
        let c = conn();
        let parsed = parse_dxf(DXF).unwrap();
        c.execute_batch("").unwrap();
        ensure(&c).unwrap();
        c.execute(
            "INSERT INTO shilpi_drawings (project_id, name, vdb) VALUES (1, 'Plan', ?1)",
            [parsed.drawing.to_bytes()],
        )
        .unwrap();
        let all = view(&c, 1, None).unwrap();
        assert_eq!(all["total"], 5);
        assert_eq!(all["shapes"].as_array().unwrap().len(), 5);
        // A small window around the circle at (2500, 2000): the circle only (the wall outline's box covers it too, so two shapes).
        let part = view(&c, 1, Some([2400.0, 1900.0, 2600.0, 2100.0])).unwrap();
        let kinds: Vec<&str> = part["shapes"].as_array().unwrap().iter().map(|s| s["t"].as_str().unwrap()).collect();
        assert!(kinds.contains(&"arc") && !kinds.contains(&"text"));
        assert_eq!(list(&c, Some(1)).unwrap().len(), 0 + 1);
        assert!(delete(&c, 1, 1).is_ok());
        assert!(list(&c, None).unwrap().is_empty());
    }
}
