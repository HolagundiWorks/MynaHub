//! The references the suite's apps use to name the same thing: a project, and a drawing, estimate or document made for it.
//!
//! The contract is written out in `docs/contracts/refs.md`. In short:
//!
//! * A `ProjectRef` names a project by the company file it lives in (`company_uuid` and `project_id`), and also by the portal's
//!   `projectOfficeId` once the project is linked to AORMS. Any one of the two names is enough to find the project again.
//! * An `ArtifactRef` names one revision of something an app made for a project, with a checksum, and the revisions it was
//!   made from. Two apps that hold the same revision agree on its checksum.
//!
//! References carry no project data and no authority. Holding one lets an app ask for a project; it does not let it read one.

use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};

/// The version of the reference format. A reader refuses a version it does not know rather than guessing.
pub const REF_VERSION: u32 = 1;

/// What an `ArtifactRef` can name. A new kind is a new version of the contract.
pub const ARTIFACT_KINDS: [&str; 5] = ["drawing", "boq", "estimate", "rate_schedule", "document"];

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProjectRef {
    pub v: u32,
    pub company_uuid: String,
    pub project_id: i64,
    /// The portal's `projectOfficeId`, when the project has been pulled from or linked to AORMS.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub aorms_project_office_id: Option<String>,
}

/// A revision of something another artifact was made from.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ArtifactInput {
    pub kind: String,
    pub id: String,
    pub revision: u32,
    pub checksum_sha256: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ArtifactRef {
    pub v: u32,
    pub project: ProjectRef,
    pub kind: String,
    /// The maker's own id for the artifact (a drawing file id, an estimate number). Not interpreted by anyone else.
    pub id: String,
    /// Starts at 1 and goes up by one each time the maker saves a new revision.
    pub revision: u32,
    /// Lower-case hex SHA-256 of the artifact's content at this revision.
    pub checksum_sha256: String,
    /// The revisions this one was calculated or drafted from. Empty for a source such as a drawing.
    #[serde(default)]
    pub made_from: Vec<ArtifactInput>,
}

/// The reference for a project in this company file, with its portal id when it has one.
pub fn project_ref(conn: &Connection, project_id: i64) -> Result<ProjectRef, String> {
    let company_uuid: String = conn
        .query_row("SELECT company_uuid FROM company_file WHERE id = 1", [], |r| r.get(0))
        .map_err(|_| "This is not an open company file.".to_string())?;
    let exists: bool = conn
        .query_row("SELECT 1 FROM projects WHERE id = ?1", params![project_id], |_| Ok(true))
        .unwrap_or(false);
    if !exists {
        return Err("Project not found.".into());
    }
    let aorms_project_office_id = conn
        .query_row("SELECT project_office_id FROM aorms_links WHERE project_id = ?1", params![project_id], |r| r.get::<_, String>(0))
        .ok();
    Ok(ProjectRef { v: REF_VERSION, company_uuid, project_id, aorms_project_office_id })
}

/// Finds the project a reference names in this company file. It matches by company and id first, and by the portal id second,
/// so a project that was pulled from the portal into another company file is still found by the id the portal gave it.
pub fn resolve_project(conn: &Connection, reference: &ProjectRef) -> Result<i64, String> {
    check_version(reference.v)?;
    let company_uuid: String = conn
        .query_row("SELECT company_uuid FROM company_file WHERE id = 1", [], |r| r.get(0))
        .map_err(|_| "This is not an open company file.".to_string())?;
    if reference.company_uuid == company_uuid {
        let found: Option<i64> = conn.query_row("SELECT id FROM projects WHERE id = ?1", params![reference.project_id], |r| r.get(0)).ok();
        if let Some(id) = found {
            return Ok(id);
        }
    }
    if let Some(office_id) = reference.aorms_project_office_id.as_deref().filter(|s| !s.is_empty()) {
        if let Ok(id) = conn.query_row("SELECT project_id FROM aorms_links WHERE project_office_id = ?1", params![office_id], |r| r.get::<_, i64>(0)) {
            return Ok(id);
        }
    }
    Err("That project is not in this company file.".into())
}

fn check_version(v: u32) -> Result<(), String> {
    if v == REF_VERSION {
        Ok(())
    } else {
        Err(format!("Reference version {v} is not supported; this is version {REF_VERSION}."))
    }
}

fn is_checksum(s: &str) -> bool {
    s.len() == 64 && s.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn check_part(kind: &str, id: &str, revision: u32, checksum: &str) -> Result<(), String> {
    if !ARTIFACT_KINDS.contains(&kind) {
        return Err(format!("\"{kind}\" is not a kind of artifact the suite knows."));
    }
    if id.trim().is_empty() || id.len() > 200 {
        return Err("An artifact needs an id of 1 to 200 characters.".into());
    }
    if revision == 0 {
        return Err("Revisions start at 1.".into());
    }
    if !is_checksum(checksum) {
        return Err("The checksum must be 64 lower-case hex digits (SHA-256).".into());
    }
    Ok(())
}

/// Checks that a reference is well formed. It does not check that the artifact exists; that is the maker's to say.
pub fn check_artifact(reference: &ArtifactRef) -> Result<(), String> {
    check_version(reference.v)?;
    check_version(reference.project.v)?;
    check_part(&reference.kind, &reference.id, reference.revision, &reference.checksum_sha256)?;
    if reference.made_from.len() > 100 {
        return Err("An artifact can name at most 100 inputs.".into());
    }
    for input in &reference.made_from {
        check_part(&input.kind, &input.id, input.revision, &input.checksum_sha256)?;
        if input.kind == reference.kind && input.id == reference.id && input.revision >= reference.revision {
            return Err("An artifact cannot be made from itself or from a later revision of itself.".into());
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn(uuid: &str) -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO company_file (id, format, company_uuid, display_name) VALUES (1, 'ADMS', ?1, 'Test')", params![uuid]).unwrap();
        c.execute("INSERT INTO clients (id, name) VALUES (1, 'Client')", []).unwrap();
        c.execute("INSERT INTO projects (id, client_id, name) VALUES (7, 1, 'House')", []).unwrap();
        c
    }

    fn sum(c: char) -> String {
        std::iter::repeat(c).take(64).collect()
    }

    fn artifact() -> ArtifactRef {
        ArtifactRef {
            v: 1,
            project: ProjectRef { v: 1, company_uuid: "a".into(), project_id: 7, aorms_project_office_id: None },
            kind: "estimate".into(),
            id: "EST-1".into(),
            revision: 2,
            checksum_sha256: sum('a'),
            made_from: vec![ArtifactInput { kind: "drawing".into(), id: "D-1".into(), revision: 3, checksum_sha256: sum('b') }],
        }
    }

    #[test]
    fn a_project_reference_names_the_company_and_the_project() {
        let c = conn("uuid-a");
        let r = project_ref(&c, 7).unwrap();
        assert_eq!((r.v, r.company_uuid.as_str(), r.project_id), (1, "uuid-a", 7));
        assert_eq!(r.aorms_project_office_id, None);
        assert_eq!(resolve_project(&c, &r).unwrap(), 7);
        assert!(project_ref(&c, 99).is_err());
    }

    #[test]
    fn a_linked_project_is_also_found_by_its_portal_id_in_another_company_file() {
        let c = conn("uuid-a");
        c.execute("INSERT INTO aorms_links (project_id, project_office_id) VALUES (7, 'office-123')", []).unwrap();
        let r = project_ref(&c, 7).unwrap();
        assert_eq!(r.aorms_project_office_id.as_deref(), Some("office-123"));
        let other = conn("uuid-b");
        other.execute("INSERT INTO projects (id, client_id, name) VALUES (11, 1, 'Copy')", []).unwrap();
        other.execute("INSERT INTO aorms_links (project_id, project_office_id) VALUES (11, 'office-123')", []).unwrap();
        assert_eq!(resolve_project(&other, &r).unwrap(), 11);
        let unlinked = ProjectRef { aorms_project_office_id: None, ..r };
        assert!(resolve_project(&other, &unlinked).is_err(), "company and id do not match, and there is no portal id");
    }

    #[test]
    fn an_unknown_version_or_project_is_refused() {
        let c = conn("uuid-a");
        let mut r = project_ref(&c, 7).unwrap();
        r.v = 2;
        assert!(resolve_project(&c, &r).unwrap_err().contains("not supported"));
        r.v = 1;
        r.project_id = 99;
        assert!(resolve_project(&c, &r).is_err());
    }

    #[test]
    fn references_round_trip_as_json_and_omit_an_empty_portal_id() {
        let r = artifact();
        let text = serde_json::to_string(&r).unwrap();
        assert!(!text.contains("aorms_project_office_id"));
        assert_eq!(serde_json::from_str::<ArtifactRef>(&text).unwrap(), r);
    }

    #[test]
    fn an_artifact_reference_is_checked_for_shape() {
        assert!(check_artifact(&artifact()).is_ok());
        let mut r = artifact();
        r.kind = "spreadsheet".into();
        assert!(check_artifact(&r).is_err());
        let mut r = artifact();
        r.revision = 0;
        assert!(check_artifact(&r).is_err());
        let mut r = artifact();
        r.checksum_sha256 = "ABC".into();
        assert!(check_artifact(&r).is_err());
        let mut r = artifact();
        r.checksum_sha256 = sum('A');
        assert!(check_artifact(&r).is_err(), "upper case is refused so two apps cannot disagree on spelling");
        let mut r = artifact();
        r.made_from[0].checksum_sha256 = "x".into();
        assert!(check_artifact(&r).is_err());
        let mut r = artifact();
        r.v = 2;
        assert!(check_artifact(&r).is_err());
    }

    #[test]
    fn an_artifact_cannot_be_made_from_itself_or_a_later_revision_of_itself() {
        let mut r = artifact();
        r.made_from.push(ArtifactInput { kind: "estimate".into(), id: "EST-1".into(), revision: 1, checksum_sha256: sum('c') });
        assert!(check_artifact(&r).is_ok(), "an earlier revision is fine");
        r.made_from[1].revision = 2;
        assert!(check_artifact(&r).is_err());
    }
}
