//! Clients, their representatives, projects and the project brief: the records every app in the suite shares.

use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};

#[derive(Serialize, Deserialize)]
pub struct Client {
    pub id: Option<i64>,
    pub name: String,
    pub contact_person: String,
    pub email: String,
    pub phone: String,
    pub address: String,
    pub pan: String,
    pub gstin: String,
    /// Billing address, when it differs from the correspondence address.
    #[serde(default)]
    pub billing_address: String,
    /// EMAIL, WHATSAPP, PHONE, PORTAL, or blank.
    #[serde(default)]
    pub communication_preference: String,
    /// individual, company, or architect_firm.
    #[serde(default = "default_kind")]
    pub kind: String,
    #[serde(default)]
    pub city: String,
    #[serde(default)]
    pub state: String,
    /// Name printed on the tax invoice when it differs from the working name.
    #[serde(default)]
    pub legal_name: String,
    /// Tax deduction account number of a client who deducts TDS.
    #[serde(default)]
    pub tan: String,
    /// regular, composition, unregistered, sez, or blank.
    #[serde(default)]
    pub gst_registration: String,
    #[serde(default)]
    pub representatives: Vec<Representative>,
    #[serde(default)]
    pub archived: bool,
}

fn default_kind() -> String {
    "individual".into()
}

#[derive(Serialize, Deserialize, Clone, Default)]
pub struct Representative {
    pub name: String,
    #[serde(default)]
    pub designation: String,
    #[serde(default)]
    pub phone: String,
}

#[derive(Serialize, Deserialize)]
pub struct Project {
    pub id: Option<i64>,
    pub client_id: i64,
    pub code: String,
    pub name: String,
    pub site_address: String,
    pub status: String,
    /// Council of Architecture service, from the Conditions of Engagement and Scale of Charges.
    #[serde(default = "default_consultancy")]
    pub consultancy_type: String,
    /// Storeys the standard drawing stages are split across. Landscape, urban, site and signage keep one set.
    #[serde(default = "default_floors")]
    pub floors: i64,
    /// The first stage that has not been marked, or Complete. Derived, not stored.
    #[serde(default)]
    pub phase: String,
    /// Short description every suite app can print (title plate, estimate cover, Finch sheet).
    #[serde(default)]
    pub summary: String,
    #[serde(default)]
    pub archived: bool,
    #[serde(default)]
    pub client_name: String,
}

fn default_consultancy() -> String {
    "comprehensive".into()
}

fn default_floors() -> i64 {
    1
}

/// Services named in the Council of Architecture Conditions of Engagement and Scale of Charges.
const CONSULTANCY: &[(&str, &str)] = &[
    ("comprehensive", "Comprehensive architectural services"),
    ("interior", "Interior architecture"),
    ("landscape", "Landscape architecture"),
    ("urban", "Urban design and urban renewal"),
    ("site", "Site development"),
    ("conservation", "Architectural conservation"),
    ("retrofitting", "Retrofitting of buildings"),
    ("signage", "Graphic design and signage"),
];

pub fn consultancy_label(key: &str) -> String {
    CONSULTANCY.iter().find(|(id, _)| *id == key).map(|(_, name)| (*name).to_string()).unwrap_or_else(|| {
        if key.is_empty() { "Comprehensive architectural services".to_string() } else { key.to_string() }
    })
}

fn consultancy(value: &str) -> Result<String, String> {
    let key = value.trim();
    let key = if key.is_empty() { "comprehensive" } else { key };
    if CONSULTANCY.iter().any(|(id, _)| *id == key) {
        Ok(key.to_string())
    } else {
        Err("Choose a consultancy type from the Council of Architecture schedule.".into())
    }
}

fn nonempty(s: &str, label: &str) -> Result<String, String> {
    let t = s.trim();
    if t.is_empty() {
        return Err(format!("{label} is required."));
    }
    if t.chars().count() > 200 {
        return Err(format!("{label} is too long."));
    }
    Ok(t.to_string())
}

fn map_unique(e: rusqlite::Error, what: &str) -> String {
    match e {
        rusqlite::Error::SqliteFailure(f, _) if f.code == rusqlite::ErrorCode::ConstraintViolation => {
            format!("{what} already exists.")
        }
        other => other.to_string(),
    }
}

pub fn list_clients(conn: &Connection, include_archived: bool) -> Result<Vec<Client>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT id, name, contact_person, email, phone, address, pan, gstin, billing_address, communication_preference, archived,
                    kind, city, state, legal_name, tan, gst_registration FROM clients
             WHERE archived = 0 OR ?1 ORDER BY name",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![include_archived], |r| {
            let id: i64 = r.get(0)?;
            Ok(Client {
                id: Some(id),
                name: r.get(1)?,
                contact_person: r.get(2)?,
                email: r.get(3)?,
                phone: r.get(4)?,
                address: r.get(5)?,
                pan: r.get(6)?,
                gstin: r.get(7)?,
                billing_address: r.get(8)?,
                communication_preference: r.get(9)?,
                kind: r.get(11)?,
                city: r.get(12)?,
                state: r.get(13)?,
                legal_name: r.get(14)?,
                tan: r.get(15)?,
                gst_registration: r.get(16)?,
                representatives: Vec::new(),
                archived: r.get::<_, i64>(10)? != 0,
            })
        })
        .map_err(|e| e.to_string())?;
    let mut clients = rows.collect::<Result<Vec<_>, _>>().map_err(|e| e.to_string())?;
    for client in &mut clients {
        if let Some(id) = client.id {
            client.representatives = list_reps(conn, id)?;
        }
    }
    Ok(clients)
}

fn list_reps(conn: &Connection, client_id: i64) -> Result<Vec<Representative>, String> {
    let mut stmt = conn
        .prepare("SELECT name, designation, phone FROM client_representatives WHERE client_id = ?1 ORDER BY position, id")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![client_id], |r| Ok(Representative { name: r.get(0)?, designation: r.get(1)?, phone: r.get(2)? }))
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

fn preference(value: &str) -> Result<String, String> {
    let key = value.trim().to_ascii_uppercase();
    if key.is_empty() || matches!(key.as_str(), "EMAIL" | "WHATSAPP" | "PHONE" | "PORTAL") {
        Ok(key)
    } else {
        Err("Choose email, WhatsApp, phone, or the client portal.".into())
    }
}

fn save_reps(conn: &Connection, client_id: i64, reps: &[Representative]) -> Result<(), String> {
    conn.execute("DELETE FROM client_representatives WHERE client_id = ?1", params![client_id]).map_err(|e| e.to_string())?;
    let mut position = 0;
    for rep in reps {
        let name = rep.name.trim();
        if name.is_empty() {
            continue;
        }
        if name.chars().count() > 200 || rep.designation.chars().count() > 120 || rep.phone.chars().count() > 40 {
            return Err("An authorised representative's name, designation or phone is too long.".into());
        }
        if position >= 10 {
            return Err("A client can have at most ten authorised representatives.".into());
        }
        conn.execute(
            "INSERT INTO client_representatives (client_id, name, designation, phone, position) VALUES (?1,?2,?3,?4,?5)",
            params![client_id, name, rep.designation.trim(), rep.phone.trim(), position],
        )
        .map_err(|e| e.to_string())?;
        position += 1;
    }
    Ok(())
}

fn client_kind(value: &str) -> Result<String, String> {
    match value.trim().to_ascii_lowercase().as_str() {
        "" | "individual" => Ok("individual".into()),
        "company" => Ok("company".into()),
        "architect_firm" | "architect firm" => Ok("architect_firm".into()),
        _ => Err("The client kind is individual, company, or architect firm.".into()),
    }
}

fn gst_registration(value: &str) -> Result<String, String> {
    match value.trim().to_ascii_lowercase().as_str() {
        "" => Ok(String::new()),
        "regular" => Ok("regular".into()),
        "composition" => Ok("composition".into()),
        "unregistered" => Ok("unregistered".into()),
        "sez" => Ok("sez".into()),
        _ => Err("GST registration is regular, composition, unregistered, or SEZ.".into()),
    }
}

fn tan(value: &str) -> Result<String, String> {
    let text = value.trim().to_ascii_uppercase();
    if text.is_empty() {
        return Ok(text);
    }
    let bytes = text.as_bytes();
    let ok = bytes.len() == 10
        && bytes[..4].iter().all(|c| c.is_ascii_alphabetic())
        && bytes[4..9].iter().all(|c| c.is_ascii_digit())
        && bytes[9].is_ascii_alphabetic();
    if !ok {
        return Err("TAN is four letters, five digits, and a letter.".into());
    }
    Ok(text)
}

pub fn save_client(conn: &Connection, c: &Client) -> Result<i64, String> {
    let name = nonempty(&c.name, "Client name")?;
    let preference = preference(&c.communication_preference)?;
    let kind = client_kind(&c.kind)?;
    let registration = gst_registration(&c.gst_registration)?;
    let tan = tan(&c.tan)?;
    if c.billing_address.chars().count() > 1000 {
        return Err("The billing address is too long.".into());
    }
    if c.legal_name.chars().count() > 200 || c.city.chars().count() > 80 || c.state.chars().count() > 80 {
        return Err("The legal name, city or state is too long.".into());
    }
    let id = match c.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE clients SET name=?1, contact_person=?2, email=?3, phone=?4, address=?5, pan=?6, gstin=?7,
                     billing_address=?8, communication_preference=?9, kind=?10, city=?11, state=?12, legal_name=?13, tan=?14,
                     gst_registration=?15, updated_at=datetime('now') WHERE id=?16",
                    params![name, c.contact_person.trim(), c.email.trim(), c.phone.trim(), c.address.trim(), c.pan.trim(), c.gstin.trim(), c.billing_address.trim(), preference, kind, c.city.trim(), c.state.trim(), c.legal_name.trim(), tan, registration, id],
                )
                .map_err(|e| map_unique(e, "A client with this name"))?;
            if n == 0 {
                return Err("Client not found.".into());
            }
            id
        }
        None => {
            conn.execute(
                "INSERT INTO clients (name, contact_person, email, phone, address, pan, gstin, billing_address, communication_preference, kind, city, state, legal_name, tan, gst_registration) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15)",
                params![name, c.contact_person.trim(), c.email.trim(), c.phone.trim(), c.address.trim(), c.pan.trim(), c.gstin.trim(), c.billing_address.trim(), preference, kind, c.city.trim(), c.state.trim(), c.legal_name.trim(), tan, registration],
            )
            .map_err(|e| map_unique(e, "A client with this name"))?;
            conn.last_insert_rowid()
        }
    };
    save_reps(conn, id, &c.representatives)?;
    Ok(id)
}

pub fn set_client_archived(conn: &Connection, id: i64, archived: bool) -> Result<(), String> {
    let n = conn
        .execute("UPDATE clients SET archived=?1, updated_at=datetime('now') WHERE id=?2", params![archived, id])
        .map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("Client not found.".into());
    }
    Ok(())
}

pub fn list_projects(conn: &Connection, client_id: Option<i64>, include_archived: bool) -> Result<Vec<Project>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT p.id, p.client_id, p.code, p.name, p.site_address, p.status, p.consultancy_type, p.floors, p.archived, c.name,
                    COALESCE((SELECT s.name FROM project_stages s WHERE s.project_id = p.id AND s.marked_on = '' ORDER BY s.position LIMIT 1),
                             CASE WHEN EXISTS (SELECT 1 FROM project_stages s WHERE s.project_id = p.id) THEN 'Complete' ELSE '' END),
                    COALESCE(p.summary, '')
             FROM projects p JOIN clients c ON c.id = p.client_id
             WHERE (p.archived = 0 OR ?1) AND (?2 IS NULL OR p.client_id = ?2)
             ORDER BY c.name, p.name",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![include_archived, client_id], |r| {
            Ok(Project {
                id: Some(r.get(0)?),
                client_id: r.get(1)?,
                code: r.get(2)?,
                name: r.get(3)?,
                site_address: r.get(4)?,
                status: r.get(5)?,
                consultancy_type: r.get(6)?,
                floors: r.get(7)?,
                archived: r.get::<_, i64>(8)? != 0,
                client_name: r.get(9)?,
                phase: r.get(10)?,
                summary: r.get(11)?,
            })
        })
        .map_err(|e| e.to_string())?;
    rows.collect::<Result<_, _>>().map_err(|e| e.to_string())
}

pub fn save_project(conn: &Connection, p: &Project) -> Result<i64, String> {
    let name = nonempty(&p.name, "Project name")?;
    if !["active", "on_hold", "completed"].contains(&p.status.as_str()) {
        return Err("Invalid project status.".into());
    }
    let consultancy_type = consultancy(&p.consultancy_type)?;
    let floors = if (1..=40).contains(&p.floors) { p.floors } else { return Err("Enter the number of floors from 1 to 40.".into()) };
    let summary = p.summary.trim();
    if summary.chars().count() > 2000 {
        return Err("Keep the project description under 2,000 characters.".into());
    }
    let mut code = p.code.trim().to_string();
    if p.id.is_none() && code.is_empty() {
        let profile = crate::profile::read(conn)?;
        code = crate::project_codes::next_project_code(conn, &profile)?;
    }
    if code.is_empty() {
        return Err("Project code is required.".into());
    }
    if code.chars().count() > 64 {
        return Err("Keep the project code under 64 characters.".into());
    }
    let duplicate: i64 = conn
        .query_row(
            "SELECT COUNT(*) FROM projects WHERE code=?1 COLLATE NOCASE AND (?2 IS NULL OR id<>?2)",
            params![code, p.id],
            |r| r.get(0),
        )
        .map_err(|e| e.to_string())?;
    if duplicate > 0 {
        return Err("Another project already uses this code.".into());
    }
    let client_archived: Option<i64> = conn
        .query_row("SELECT archived FROM clients WHERE id=?1", params![p.client_id], |r| r.get(0))
        .ok();
    match client_archived {
        None => return Err("Client not found.".into()),
        Some(a) if a != 0 && p.id.is_none() => return Err("Cannot add a project to an archived client.".into()),
        _ => {}
    }
    match p.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE projects SET client_id=?1, code=?2, name=?3, site_address=?4, status=?5, consultancy_type=?6, floors=?7, summary=?8, updated_at=datetime('now') WHERE id=?9",
                    params![p.client_id, code, name, p.site_address.trim(), p.status, consultancy_type, floors, summary, id],
                )
                .map_err(|e| map_unique(e, "This client already has a project with this name; it"))?;
            if n == 0 {
                return Err("Project not found.".into());
            }
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO projects (client_id, code, name, site_address, status, consultancy_type, floors, summary) VALUES (?1,?2,?3,?4,?5,?6,?7,?8)",
                params![p.client_id, code, name, p.site_address.trim(), p.status, consultancy_type, floors, summary],
            )
            .map_err(|e| map_unique(e, "This client already has a project with this name; it"))?;
            Ok(conn.last_insert_rowid())
        }
    }
}

pub fn next_code(conn: &Connection) -> Result<String, String> {
    let profile = crate::profile::read(conn)?;
    crate::project_codes::next_project_code(conn, &profile)
}

pub fn set_project_archived(conn: &Connection, id: i64, archived: bool) -> Result<(), String> {
    let n = conn
        .execute("UPDATE projects SET archived=?1, updated_at=datetime('now') WHERE id=?2", params![archived, id])
        .map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("Project not found.".into());
    }
    Ok(())
}

const BRIEF_FIELDS: &[&str] = &[
    "client_name", "current_address", "site_address", "mobile", "email", "occupation", "plot_size", "terrain", "vegetation", "orientation_notes",
    "intended_use", "built_up_area", "phased", "tentative_start", "budget_note", "finance_note", "household", "staff_requirements", "orientation",
    "door_direction", "views", "basement", "vastu", "style", "loved_places", "activities", "indoor_prefs", "outdoor_prefs", "space_schedule",
    "construction", "flooring", "walls", "cabinetry", "seating", "beds", "room_details", "assumptions", "approval_note", "approved_at",
];

fn blank_brief() -> Value {
    let mut body = Map::new();
    for key in BRIEF_FIELDS {
        body.insert((*key).to_string(), Value::String(String::new()));
    }
    Value::Object(body)
}

fn clean_brief(input: &Value) -> Result<Value, String> {
    let Some(obj) = input.as_object() else {
        return Err("The project brief was not a form.".into());
    };
    let mut body = blank_brief();
    let stored = body.as_object_mut().expect("blank_brief always returns Object");
    for (key, value) in obj {
        if !BRIEF_FIELDS.contains(&key.as_str()) {
            return Err(format!("The project brief has no field {key}."));
        }
        let text = value.as_str().unwrap_or("").trim();
        let limit = if key == "assumptions" { 10_000 } else { 4_000 };
        if text.chars().count() > limit {
            return Err(format!("{key} is too long."));
        }
        if key == "approved_at" && !text.is_empty() && (text.len() != 10 || text.as_bytes().get(4) != Some(&b'-')) {
            return Err("Enter the approval date as YYYY-MM-DD.".into());
        }
        stored.insert(key.clone(), Value::String(text.to_string()));
    }
    Ok(body)
}

pub fn get_brief(conn: &Connection, project_id: i64) -> Result<Value, String> {
    let exists: i64 = conn.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1", params![project_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if exists == 0 {
        return Err("Project not found.".into());
    }
    let stored: Option<String> = conn.query_row("SELECT body FROM project_briefs WHERE project_id = ?1", params![project_id], |r| r.get(0)).ok();
    let mut body = blank_brief();
    if let Some(text) = stored {
        if let Ok(value) = serde_json::from_str::<Value>(&text) {
            if let Some(obj) = value.as_object() {
                let fields = body.as_object_mut().expect("blank_brief always returns Object");
                for key in BRIEF_FIELDS {
                    if let Some(item) = obj.get(*key).and_then(|v| v.as_str()) {
                        fields.insert((*key).to_string(), Value::String(item.to_string()));
                    }
                }
            }
        }
    }
    Ok(json!({ "project_id": project_id, "brief": body }))
}

pub fn save_brief(conn: &Connection, project_id: i64, brief: &Value) -> Result<(), String> {
    let exists: i64 = conn.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1", params![project_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if exists == 0 {
        return Err("Project not found.".into());
    }
    let body = clean_brief(brief)?;
    let text = serde_json::to_string(&body).map_err(|e| e.to_string())?;
    conn.execute(
        "INSERT INTO project_briefs (project_id, body) VALUES (?1, ?2)
         ON CONFLICT(project_id) DO UPDATE SET body = excluded.body, updated_at = datetime('now')",
        params![project_id, text],
    )
    .map_err(|e| e.to_string())?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c
    }

    fn client(name: &str) -> Client {
        Client {
            id: None, name: name.into(), contact_person: "".into(), email: "".into(), phone: "".into(), address: "".into(), pan: "".into(), gstin: "".into(),
            billing_address: "".into(), communication_preference: "".into(), kind: "individual".into(), city: "".into(), state: "".into(),
            legal_name: "".into(), tan: "".into(), gst_registration: "".into(), representatives: Vec::new(), archived: false,
        }
    }

    fn project(client_id: i64, name: &str) -> Project {
        Project { id: None, client_id, code: "".into(), name: name.into(), site_address: "".into(), status: "active".into(), consultancy_type: "comprehensive".into(), floors: 1, phase: String::new(), summary: String::new(), archived: false, client_name: "".into() }
    }

    #[test]
    fn duplicate_client_name_rejected_case_insensitively() {
        let c = conn();
        save_client(&c, &client("Acme")).unwrap();
        assert!(save_client(&c, &client("ACME")).unwrap_err().contains("already exists"));
        assert!(save_client(&c, &client("  ")).is_err());
    }

    #[test]
    fn client_has_many_projects_without_duplicates() {
        let c = conn();
        let id = save_client(&c, &client("Acme")).unwrap();
        save_project(&c, &project(id, "Villa")).unwrap();
        save_project(&c, &project(id, "Office")).unwrap();
        assert!(save_project(&c, &project(id, "villa")).is_err());
        assert_eq!(list_projects(&c, Some(id), false).unwrap().len(), 2);
    }

    #[test]
    fn archive_hides_but_keeps_records_and_blocks_new_projects() {
        let c = conn();
        let id = save_client(&c, &client("Acme")).unwrap();
        let pid = save_project(&c, &project(id, "Villa")).unwrap();
        set_client_archived(&c, id, true).unwrap();
        assert!(list_clients(&c, false).unwrap().is_empty());
        assert_eq!(list_clients(&c, true).unwrap().len(), 1);
        assert!(save_project(&c, &project(id, "New")).is_err());
        set_project_archived(&c, pid, true).unwrap();
        assert!(list_projects(&c, None, false).unwrap().is_empty());
    }

    #[test]
    fn project_requires_valid_status_and_existing_client() {
        let c = conn();
        let mut p = project(999, "X");
        assert!(save_project(&c, &p).is_err());
        let id = save_client(&c, &client("Acme")).unwrap();
        p.client_id = id;
        p.status = "bogus".into();
        assert!(save_project(&c, &p).is_err());
        p.status = "active".into();
        p.consultancy_type = "not-a-service".into();
        assert!(save_project(&c, &p).unwrap_err().contains("Council of Architecture"));
        p.consultancy_type = "interior".into();
        let saved_id = save_project(&c, &p).unwrap();
        let saved = list_projects(&c, None, false).unwrap();
        assert_eq!(saved.iter().find(|row| row.id == Some(saved_id)).unwrap().consultancy_type, "interior");
    }

    #[test]
    fn onboarding_and_the_project_brief_round_trip() {
        let c = conn();
        let mut person = client("Acme");
        person.billing_address = "12 Billing Road".into();
        person.communication_preference = "whatsapp".into();
        person.gstin = "29ABCDE1234F1Z5".into();
        person.pan = "ABCDE1234F".into();
        person.representatives = vec![Representative { name: "A. Client".into(), designation: "Director".into(), phone: "99999".into() }];
        let id = save_client(&c, &person).unwrap();
        let saved = list_clients(&c, false).unwrap();
        assert_eq!(saved[0].communication_preference, "WHATSAPP");
        assert_eq!(saved[0].billing_address, "12 Billing Road");
        assert_eq!(saved[0].representatives[0].name, "A. Client");
        assert_eq!(saved[0].kind, "individual");
        person.id = Some(id);
        person.tan = "not-a-tan".into();
        assert!(save_client(&c, &person).unwrap_err().contains("TAN"));
        person.tan = "blrg12345c".into();
        person.kind = "company".into();
        person.legal_name = "Acme Structures Private Limited".into();
        person.city = "Bengaluru".into();
        person.state = "Karnataka".into();
        person.gst_registration = "regular".into();
        save_client(&c, &person).unwrap();
        let again = list_clients(&c, false).unwrap();
        assert_eq!(again[0].tan, "BLRG12345C");
        assert_eq!(again[0].kind, "company");
        assert_eq!(again[0].legal_name, "Acme Structures Private Limited");
        let project_id = save_project(&c, &project(id, "Villa")).unwrap();
        save_brief(&c, project_id, &json!({ "vastu": "Yes, consultant engaged", "space_schedule": "Kitchen | 12 sq m | Ground" })).unwrap();
        let brief = get_brief(&c, project_id).unwrap();
        assert_eq!(brief["brief"]["vastu"], "Yes, consultant engaged");
        assert_eq!(brief["brief"]["style"], "");
        assert!(save_brief(&c, project_id, &json!({ "not_a_field": "x" })).is_err());
    }
}
