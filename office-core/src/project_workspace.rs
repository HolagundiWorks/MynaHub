//! Suite project folder: one directory per Kivo project holding Tren, Finch and the `.osp` project file.
//!
//! Layout (relative to the company file's parent):
//! ```text
//! projects/<slug>/
//!   osas.project.json   # suite envelope (ProjectRef + drawing_id)
//!   project.osp         # drawing, take-off, CoreOp project record (Tren / Falcon)
//!   draft/model.dxf     # Tren starter drawing (legacy scaffold)
//!   form/model.blend    # Finch model (created empty; first save fills it)
//! ```

use crate::refs::{self, ProjectRef, REF_VERSION};
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::fs;
use std::path::{Path, PathBuf};

pub const MANIFEST: &str = "osas.project.json";
pub const FORMAT: &str = "osp";
pub const MANIFEST_VERSION: u32 = 1;

const MINIMAL_DXF: &str = "0\nSECTION\n2\nHEADER\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF\n";

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorkspaceInfo {
    pub root: String,
    pub manifest: String,
    pub draft: String,
    pub form: String,
    pub osp: String,
    pub drawing_id: String,
    pub project_ref: ProjectRef,
}

#[derive(Debug, Clone)]
pub struct ScaffoldInput<'a> {
    pub company_path: &'a Path,
    pub project_id: i64,
    pub code: &'a str,
    pub name: &'a str,
    pub floors: i64,
}

/// Safe folder segment from code or name.
pub fn slug(code: &str, name: &str, project_id: i64) -> String {
    let raw = if !code.trim().is_empty() { code } else { name };
    let mut out = String::new();
    for ch in raw.chars() {
        if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
            out.push(ch.to_ascii_lowercase());
        } else if ch.is_whitespace() || ch == '/' || ch == '\\' {
            if !out.ends_with('-') {
                out.push('-');
            }
        }
        if out.len() >= 48 {
            break;
        }
    }
    let out = out.trim_matches('-').to_string();
    if out.is_empty() {
        format!("p{project_id}")
    } else {
        format!("{out}-{project_id}")
    }
}

pub fn projects_root(company_path: &Path) -> PathBuf {
    company_path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join("projects")
}

pub fn workspace_root(company_path: &Path, code: &str, name: &str, project_id: i64) -> PathBuf {
    projects_root(company_path).join(slug(code, name, project_id))
}

fn write_atomic(path: &Path, bytes: &[u8]) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("Could not create {}: {e}", parent.display()))?;
    }
    let tmp = path.with_extension("tmp");
    fs::write(&tmp, bytes).map_err(|e| format!("Could not write {}: {e}", tmp.display()))?;
    fs::rename(&tmp, path).map_err(|e| format!("Could not save {}: {e}", path.display()))?;
    Ok(())
}

fn new_drawing_id() -> String {
    // UUID v4-shaped from random hasher state (no extra crate).
    use std::hash::{BuildHasher, Hasher};
    let mut h = std::collections::hash_map::RandomState::new().build_hasher();
    h.write_u64(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos() as u64)
            .unwrap_or(0),
    );
    let a = h.finish();
    h.write_u64(a);
    let b = h.finish();
    format!(
        "{:08x}-{:04x}-4{:03x}-a{:03x}-{:012x}",
        (a >> 32) as u32,
        ((a >> 16) & 0xffff) as u16,
        (a & 0xfff) as u16,
        (b >> 48) as u16 & 0xfff,
        b & 0xffff_ffff_ffff
    )
}

/// Create the project folder tree and `osas.project.json`. Idempotent if the folder already matches.
pub fn scaffold(conn: &Connection, input: ScaffoldInput<'_>) -> Result<WorkspaceInfo, String> {
    let pref = refs::project_ref(conn, input.project_id)?;
    let root = workspace_root(input.company_path, input.code, input.name, input.project_id);
    fs::create_dir_all(root.join("draft")).map_err(|e| e.to_string())?;
    fs::create_dir_all(root.join("form")).map_err(|e| e.to_string())?;

    let draft = root.join("draft").join("model.dxf");
    let form = root.join("form").join("model.blend");
    let osp = root.join("project.osp");

    if !draft.exists() {
        write_atomic(&draft, MINIMAL_DXF.as_bytes())?;
    }
    // Finch fills the blend on first save; leave a zero-byte stub so the path exists.
    if !form.exists() {
        write_atomic(&form, b"")?;
    }
    // `.osp` is created when Tren or Falcon first saves the project file.

    let drawing_id = {
        let existing = read_manifest(&root).ok().and_then(|m| {
            m.get("drawing_id")
                .and_then(|v| v.as_str())
                .map(|s| s.to_string())
        });
        existing.unwrap_or_else(new_drawing_id)
    };

    let office = pref
        .aorms_project_office_id
        .clone()
        .unwrap_or_else(|| format!("{}:{}", pref.company_uuid, pref.project_id));

    let floors = if (1..=40).contains(&input.floors) {
        input.floors
    } else {
        1
    };

    let manifest = json!({
        "v": MANIFEST_VERSION,
        "format": FORMAT,
        "name": input.name,
        "code": input.code,
        "project_ref": pref,
        "drawing_id": drawing_id,
        "project_office_id": office,
        "files": {
            "draft": "draft/model.dxf",
            "form": "form/model.blend",
            "project": "project.osp",
            "odraft_project": "draft/project.json"
        },
        "apps": {
            "ostudio": { "role": "owner" },
            "odraft": { "file": "project.osp", "drawing": "draft/model.dxf" },
            "ocoreop": { "file": "project.osp" },
            "oform": { "file": "form/model.blend" }
        }
    });

    let manifest_path = root.join(MANIFEST);
    write_atomic(
        &manifest_path,
        serde_json::to_vec_pretty(&manifest).map_err(|e| e.to_string())?.as_slice(),
    )?;

    // Minimal Tren project.json so Draft can adopt the folder later.
    let draft_project = json!({
        "schema_version": 1,
        "project": {
            "id": format!("prj-{}", input.project_id),
            "code": if input.code.is_empty() { format!("P{}", input.project_id) } else { input.code.to_string() },
            "name": input.name,
            "type": "building",
            "description": "Scaffolded by Kivo",
            "units": { "length": "mm", "area": "m2" }
        },
        "floors": [{
            "id": "fl-ground",
            "name": "Ground",
            "code": "GF",
            "kind": "floor",
            "sequence": 0,
            "elevation": 0.0,
            "floor_to_floor": 3000.0
        }],
        "suite": {
            "osas_project": format!("../{MANIFEST}"),
            "drawing_id": drawing_id,
            "project_ref": pref,
            "floors_hint": floors
        }
    });
    write_atomic(
        &root.join("draft").join("project.json"),
        serde_json::to_vec_pretty(&draft_project).map_err(|e| e.to_string())?.as_slice(),
    )?;

    let root_str = root.to_string_lossy().to_string();
    conn.execute(
        "UPDATE projects SET workspace_path=?1, updated_at=datetime('now') WHERE id=?2",
        params![root_str, input.project_id],
    )
    .map_err(|e| e.to_string())?;

    Ok(WorkspaceInfo {
        root: root_str,
        manifest: manifest_path.to_string_lossy().to_string(),
        draft: draft.to_string_lossy().to_string(),
        form: form.to_string_lossy().to_string(),
        osp: osp.to_string_lossy().to_string(),
        drawing_id,
        project_ref: ProjectRef {
            v: REF_VERSION,
            company_uuid: pref.company_uuid,
            project_id: pref.project_id,
            aorms_project_office_id: pref.aorms_project_office_id,
        },
    })
}

fn read_manifest(root: &Path) -> Result<serde_json::Value, String> {
    let text = fs::read_to_string(root.join(MANIFEST)).map_err(|e| e.to_string())?;
    serde_json::from_str(&text).map_err(|e| e.to_string())
}

pub fn get(conn: &Connection, project_id: i64) -> Result<Option<WorkspaceInfo>, String> {
    let path: String = conn
        .query_row(
            "SELECT workspace_path FROM projects WHERE id=?1",
            params![project_id],
            |r| r.get(0),
        )
        .map_err(|_| "Project not found.".to_string())?;
    if path.trim().is_empty() {
        return Ok(None);
    }
    let root = PathBuf::from(&path);
    let manifest = read_manifest(&root).unwrap_or_else(|_| json!({}));
    let drawing_id = manifest
        .get("drawing_id")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    let pref = refs::project_ref(conn, project_id)?;
    Ok(Some(WorkspaceInfo {
        root: path,
        manifest: root.join(MANIFEST).to_string_lossy().to_string(),
        draft: root.join("draft").join("model.dxf").to_string_lossy().to_string(),
        form: root.join("form").join("model.blend").to_string_lossy().to_string(),
        osp: root.join("project.osp").to_string_lossy().to_string(),
        drawing_id,
        project_ref: pref,
    }))
}

/// What the launcher needs to list a project and what an app checks before it opens one.
#[derive(Debug, Clone, Serialize)]
pub struct LaunchInfo {
    pub id: i64,
    pub code: String,
    pub name: String,
    pub client: String,
    pub status: String,
    pub archived: bool,
    pub workspace: Option<WorkspaceInfo>,
    /// The `.osp` is made by the app that first opens the project, so it may not exist yet.
    pub osp_exists: bool,
    /// Not archived and has a workspace folder: an app may open it.
    pub launchable: bool,
}

pub fn launch_info(conn: &Connection, project_id: i64) -> Result<LaunchInfo, String> {
    let (code, name, status, archived, client): (String, String, String, bool, String) = conn
        .query_row(
            "SELECT p.code, p.name, p.status, p.archived, c.name FROM projects p JOIN clients c ON c.id = p.client_id WHERE p.id = ?1",
            params![project_id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)),
        )
        .map_err(|_| "Project not found.".to_string())?;
    let workspace = get(conn, project_id)?;
    let osp_exists = workspace.as_ref().is_some_and(|w| Path::new(&w.osp).is_file());
    let launchable = !archived && workspace.as_ref().is_some_and(|w| Path::new(&w.root).is_dir());
    Ok(LaunchInfo { id: project_id, code, name, client, status, archived, workspace, osp_exists, launchable })
}

/// Copy an existing `.osp` into the project's workspace (`project.osp`).
pub fn attach_osp(conn: &Connection, company_path: &Path, project_id: i64, source: &Path) -> Result<WorkspaceInfo, String> {
    if !source.is_file() {
        return Err("The project file was not found.".into());
    }
    if source.extension().and_then(|e| e.to_str()) != Some("osp") {
        return Err("Choose an ODP project file (.osp).".into());
    }
    let (code, name, floors): (String, String, i64) = conn
        .query_row(
            "SELECT code, name, floors FROM projects WHERE id=?1",
            params![project_id],
            |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
        )
        .map_err(|_| "Project not found.".to_string())?;
    let info = match get(conn, project_id)? {
        Some(existing) => existing,
        None => scaffold(
            conn,
            ScaffoldInput {
                company_path,
                project_id,
                code: &code,
                name: &name,
                floors,
            },
        )?,
    };
    let dest = PathBuf::from(&info.osp);
    if let Some(parent) = dest.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    fs::copy(source, &dest).map_err(|e| format!("Could not copy the project file: {e}"))?;
    Ok(info)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema;
    use rusqlite::Connection;

    fn setup() -> (PathBuf, Connection, PathBuf) {
        let d = std::env::temp_dir().join(format!("osas-ws-{}-{}", std::process::id(), new_drawing_id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        let company = d.join("Acme.osas");
        let mut conn = Connection::open(&company).unwrap();
        schema::migrate(&mut conn).unwrap();
        conn.execute(
            "INSERT INTO company_file (id, format, company_uuid, display_name) VALUES (1, 'AORMS-1', 'uuid-co', 'Acme')",
            [],
        )
        .unwrap();
        conn.execute("INSERT INTO clients (id, name) VALUES (1, 'Client')", [])
            .unwrap();
        conn.execute(
            "INSERT INTO projects (id, client_id, code, name, floors) VALUES (7, 1, 'KS', 'Kesar', 2)",
            [],
        )
        .unwrap();
        (d, conn, company)
    }

    #[test]
    fn launch_info_follows_the_workspace_and_the_archive_flag() {
        let (d, conn, company) = setup();
        let before = launch_info(&conn, 7).unwrap();
        assert!(!before.launchable && before.workspace.is_none() && !before.osp_exists, "no folder yet");
        assert_eq!((before.code.as_str(), before.client.as_str()), ("KS", "Client"));
        scaffold(&conn, ScaffoldInput { company_path: &company, project_id: 7, code: "KS", name: "Kesar", floors: 2 }).unwrap();
        let after = launch_info(&conn, 7).unwrap();
        assert!(after.launchable && after.workspace.is_some());
        conn.execute("UPDATE projects SET archived = 1 WHERE id = 7", []).unwrap();
        assert!(!launch_info(&conn, 7).unwrap().launchable, "an archived project is not opened");
        assert!(launch_info(&conn, 99).is_err());
        let _ = fs::remove_dir_all(d);
    }

    #[test]
    fn scaffold_writes_folder_and_manifest() {
        let (d, conn, company) = setup();
        let info = scaffold(
            &conn,
            ScaffoldInput {
                company_path: &company,
                project_id: 7,
                code: "KS",
                name: "Kesar",
                floors: 2,
            },
        )
        .unwrap();
        assert!(Path::new(&info.root).join(MANIFEST).is_file());
        assert!(Path::new(&info.draft).is_file());
        assert!(Path::new(&info.form).is_file());
        assert!(!Path::new(&info.osp).exists());
        assert!(!info.drawing_id.is_empty());
        let again = scaffold(
            &conn,
            ScaffoldInput {
                company_path: &company,
                project_id: 7,
                code: "KS",
                name: "Kesar",
                floors: 2,
            },
        )
        .unwrap();
        assert_eq!(again.drawing_id, info.drawing_id, "drawing_id must stay stable");
        let got = get(&conn, 7).unwrap().unwrap();
        assert_eq!(got.root, info.root);
        let _ = fs::remove_dir_all(d);
    }
}
