//! What every document needs to print: the company's own details (owned by OSAS), and the project's details (owned by Kivo),
//! read together. Falcon reports, Tren title plates and Kivo's own letters read this one answer instead of keeping copies,
//! so a change to the address, the registration number or a project's site shows up in every app's next document.
//! It only reads. The company's details are changed in OSAS, a project's in Kivo.

use crate::clients::{self, Client, Project};
use crate::profile::{self, CompanyProfile};
use crate::refs::{self, ProjectRef};
use rusqlite::Connection;
use serde::Serialize;
use serde_json::Value;

pub const CONTEXT_VERSION: u32 = 1;

#[derive(Serialize)]
pub struct ProjectBlock {
    pub project: Project,
    /// The service in words, as the Council of Architecture schedule names it.
    pub consultancy_label: String,
    pub client: Option<Client>,
    /// The project brief form: client's requirements, site, approvals and assumptions.
    pub brief: Value,
    pub project_ref: ProjectRef,
}

#[derive(Serialize)]
pub struct DocumentContext {
    pub v: u32,
    pub company: CompanyProfile,
    /// Absent when no project was asked for, for example for a letterhead.
    pub project: Option<ProjectBlock>,
    /// Today's date in the company file's clock, `YYYY-MM-DD`, so every app dates a document the same way.
    pub prepared_on: String,
}

pub fn read(conn: &Connection, project_id: Option<i64>) -> Result<DocumentContext, String> {
    let company = profile::read(conn)?;
    let prepared_on: String = conn.query_row("SELECT date('now', 'localtime')", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    let project = match project_id {
        None => None,
        Some(id) => {
            let project = clients::list_projects(conn, None, true)?
                .into_iter()
                .find(|p| p.id == Some(id))
                .ok_or_else(|| "Project not found.".to_string())?;
            let client = clients::list_clients(conn, true)?.into_iter().find(|c| c.id == Some(project.client_id));
            Some(ProjectBlock {
                consultancy_label: clients::consultancy_label(&project.consultancy_type),
                brief: clients::get_brief(conn, id)?,
                project_ref: refs::project_ref(conn, id)?,
                client,
                project,
            })
        }
    };
    Ok(DocumentContext { v: CONTEXT_VERSION, company, project, prepared_on })
}

#[cfg(test)]
mod tests {
    use super::*;
    use rusqlite::params;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO company_file (id, format, company_uuid, display_name) VALUES (1, 'ADMS', 'uuid-1', 'Studio')", []).unwrap();
        c
    }

    fn company_and_project(c: &Connection) -> i64 {
        c.execute("INSERT INTO clients (name) VALUES ('Mehta family')", []).unwrap();
        let client_id = c.last_insert_rowid();
        let project = Project {
            id: None,
            client_id,
            code: "P-001".into(),
            name: "Mehta residence".into(),
            site_address: "Plot 4, Hospet".into(),
            status: "active".into(),
            consultancy_type: "interior".into(),
            floors: 2,
            phase: String::new(),
            summary: "Two-storey residence with courtyard.".into(),
            archived: false,
            client_name: String::new(),
        };
        clients::save_project(c, &project).unwrap()
    }

    #[test]
    fn company_alone_has_no_project_and_a_date() {
        let c = conn();
        let ctx = read(&c, None).unwrap();
        assert_eq!(ctx.v, CONTEXT_VERSION);
        assert!(ctx.project.is_none());
        assert_eq!(ctx.prepared_on.len(), 10);
        assert_eq!(ctx.prepared_on.as_bytes()[4], b'-');
    }

    #[test]
    fn a_project_comes_with_its_client_brief_and_reference() {
        let c = conn();
        let id = company_and_project(&c);
        let ctx = read(&c, Some(id)).unwrap();
        let block = ctx.project.unwrap();
        assert_eq!(block.project.name, "Mehta residence");
        assert_eq!(block.project.site_address, "Plot 4, Hospet");
        assert_eq!(block.project.summary, "Two-storey residence with courtyard.");
        assert_eq!(block.consultancy_label, "Interior architecture");
        assert_eq!(block.client.unwrap().name, "Mehta family");
        assert_eq!(block.project_ref.project_id, id);
        assert_eq!(block.project_ref.company_uuid, "uuid-1");
        assert!(block.brief.is_object());
    }

    #[test]
    fn a_change_in_either_place_shows_in_the_next_read() {
        let c = conn();
        let id = company_and_project(&c);
        let before = read(&c, Some(id)).unwrap();
        let mut profile = before.company.clone();
        profile.name = "New Name Architects".into();
        profile.gstin = "29ABCDE1234F1Z5".into();
        let user: i64 = {
            c.execute("INSERT INTO users (username, display_name, password_hash, role) VALUES ('o', 'O', 'x', 'owner')", []).ok();
            c.query_row("SELECT id FROM users LIMIT 1", [], |r| r.get(0)).unwrap_or(1)
        };
        profile::save(&c, user, &profile).unwrap();
        c.execute("UPDATE projects SET site_address = ?1 WHERE id = ?2", params!["Plot 9, Hubli", id]).unwrap();
        let after = read(&c, Some(id)).unwrap();
        assert_eq!(after.company.name, "New Name Architects");
        assert_eq!(after.company.gstin, "29ABCDE1234F1Z5");
        assert_eq!(after.project.unwrap().project.site_address, "Plot 9, Hubli");
    }

    #[test]
    fn an_unknown_project_is_refused() {
        let c = conn();
        assert!(read(&c, Some(999)).err().unwrap().contains("not found"));
    }
}
