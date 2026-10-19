//! The shared-record operations by name. Every operation checks the signed-in session and its permission first.
//! Reading needs View; adding or changing needs Edit. The audit entries are the ones the office app writes.

use office_core::auth::Permission;
use office_core::host::{arg, json, CallError};
use office_core::workspace::{self, require, AppState, INTERNAL};
use office_core::{audit, clients, office, refs, share, track, users};
use serde_json::Value;

pub fn dispatch(state: &AppState, op: &str, args: &Value) -> Result<Vec<u8>, CallError> {
    // Polling for changes must not count as activity (that would stop an idle session from ever locking), so it is answered before the
    // permission check. It needs a signed-in session to exist, and returns only a counter and a revision number.
    if op == "watch" {
        if state.session.lock().map_err(|_| CallError::Domain(INTERNAL.into()))?.is_none() {
            return Err(CallError::Domain("Not signed in.".into()));
        }
        let (project_id, kind): (Option<i64>, Option<String>) = (arg(args, "project_id")?, arg(args, "kind")?);
        let guard = state.db.lock().map_err(|_| CallError::Domain(INTERNAL.into()))?;
        return json(&share::watch(&guard, project_id, kind.as_deref().unwrap_or("aqc.project"))?);
    }
    let permission = match op {
        "client_list" | "project_list" | "project_brief_get" | "company_profile_get" | "document_context" | "data_version" | "doc_get" | "doc_list" | "doc_revision" | "project_ref" | "project_resolve" | "artifact_ref_check" | "staff_list" | "leave_list" | "library_list" | "track.version" | "track.apply" => Permission::View,
        "doc_put" | "client_save" | "client_set_archived" | "project_save" | "project_set_archived" | "project_brief_save" | "staff_save" | "leave_save" | "library_save" => Permission::Edit,
        "company_profile_save" => Permission::ManageSettings,
        "user_list" | "user_create" | "user_update" | "user_set_password" => Permission::ManageUsers,
        "" => return Err(CallError::BadRequest("The operation name was missing.".into())),
        _ => return Err(CallError::BadRequest("That operation is not part of the office interface.".into())),
    };
    let user = require(state, permission)?;
    // Changing people needs the administrator's own password again. It is checked before the database is locked for the call.
    if matches!(op, "user_create" | "user_update" | "user_set_password") {
        workspace::reauth(state, &arg::<String>(args, "admin_password")?)?;
    }
    let mut guard = state.db.lock().map_err(|_| CallError::Domain(INTERNAL.into()))?;
    if op == "track.apply" {
        let name: String = arg(args, "name")?;
        let migrations: Vec<String> = arg(args, "migrations")?;
        let slices: Vec<&str> = migrations.iter().map(String::as_str).collect();
        track::migrate_slice(&mut guard, &name, &slices)?;
        return json(&track::version(&guard, &name)?);
    }
    let conn = &*guard;
    match op {
        "track.version" => json(&track::version(conn, &arg::<String>(args, "name")?)?),
        "client_list" => json(&clients::list_clients(conn, arg(args, "include_archived")?)?),
        "client_save" => {
            let client: clients::Client = arg(args, "client")?;
            let id = clients::save_client(conn, &client)?;
            audit(conn, Some(user), if client.id.is_some() { "client_updated" } else { "client_created" }, &format!("client {id}"));
            json(&id)
        }
        "client_set_archived" => {
            let (id, archived): (i64, bool) = (arg(args, "id")?, arg(args, "archived")?);
            clients::set_client_archived(conn, id, archived)?;
            audit(conn, Some(user), if archived { "client_archived" } else { "client_restored" }, &format!("client {id}"));
            json(&Value::Null)
        }
        "project_list" => json(&clients::list_projects(conn, arg(args, "client_id")?, arg(args, "include_archived")?)?),
        "project_save" => {
            let project: clients::Project = arg(args, "project")?;
            let id = clients::save_project(conn, &project)?;
            audit(conn, Some(user), if project.id.is_some() { "project_updated" } else { "project_created" }, &format!("project {id}"));
            json(&id)
        }
        "project_set_archived" => {
            let (id, archived): (i64, bool) = (arg(args, "id")?, arg(args, "archived")?);
            clients::set_project_archived(conn, id, archived)?;
            audit(conn, Some(user), if archived { "project_archived" } else { "project_restored" }, &format!("project {id}"));
            json(&Value::Null)
        }
        "user_list" => json(&users::list(conn)?),
        "user_create" => json(&users::create(conn, user, &arg::<String>(args, "username")?, &arg::<String>(args, "password")?, &arg::<String>(args, "role")?)?),
        "user_update" => {
            users::update(conn, user, arg(args, "id")?, &arg::<String>(args, "role")?, arg(args, "disabled")?)?;
            json(&Value::Null)
        }
        "user_set_password" => {
            users::set_password(conn, user, arg(args, "id")?, &arg::<String>(args, "password")?)?;
            json(&Value::Null)
        }
        "company_profile_get" => json(&office_core::profile::read(conn)?),
        "document_context" => json(&office_core::context::read(conn, args.get("project_id").and_then(|v| v.as_i64()))?),
        "company_profile_save" => {
            office_core::profile::save(conn, user, &arg(args, "profile")?)?;
            json(&Value::Null)
        }
        "data_version" => json(&share::data_version(conn)?),
        "doc_get" => json(&share::doc_get(conn, arg(args, "project_id")?, &arg::<String>(args, "kind")?)?),
        "doc_list" => json(&share::doc_list(conn, arg(args, "project_id")?)?),
        "doc_revision" => json(&share::doc_revision(conn, arg(args, "project_id")?, &arg::<String>(args, "kind")?, arg(args, "revision")?)?),
        "doc_put" => {
            let (project_id, kind, expected, body): (i64, String, i64, String) = (arg(args, "project_id")?, arg(args, "kind")?, arg(args, "expected_revision")?, arg(args, "body")?);
            let by: String = conn.query_row("SELECT username FROM users WHERE id = ?1", [user], |r| r.get(0)).unwrap_or_default();
            let (revision, checksum) = share::doc_put(conn, project_id, &kind, expected, &body, &by)?;
            audit(conn, Some(user), "document_saved", &format!("project {project_id} {kind} revision {revision}"));
            json(&serde_json::json!({ "revision": revision, "checksum_sha256": checksum }))
        }
        "project_ref" => json(&refs::project_ref(conn, arg(args, "project_id")?)?),
        "project_resolve" => json(&refs::resolve_project(conn, &arg(args, "reference")?)?),
        "artifact_ref_check" => {
            refs::check_artifact(&arg(args, "reference")?)?;
            json(&Value::Null)
        }
        "project_brief_get" => json(&clients::get_brief(conn, arg(args, "project_id")?)?),
        "project_brief_save" => {
            let (id, brief): (i64, Value) = (arg(args, "project_id")?, arg(args, "brief")?);
            clients::save_brief(conn, id, &brief)?;
            audit(conn, Some(user), "project_brief_saved", &format!("project {id}"));
            json(&Value::Null)
        }
        "staff_list" => json(&office::staff_list(conn)?),
        "staff_save" => json(&office::staff_save(conn, user, &arg(args, "input")?)?),
        "leave_list" => json(&office::leave_list(conn)?),
        "leave_save" => json(&office::leave_save(conn, user, &arg(args, "input")?)?),
        "library_list" => {
            let (kind, search): (Option<String>, Option<String>) = (arg(args, "kind")?, arg(args, "search")?);
            json(&office::library_list(conn, kind.as_deref(), search.as_deref().unwrap_or(""))?)
        }
        "library_save" => json(&office::library_save(conn, user, &arg(args, "input")?)?),
        _ => Err(CallError::BadRequest("That operation is not part of the office interface.".into())),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use office_core::{company, workspace};
    use serde_json::json;

    struct Temp(std::path::PathBuf);
    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn call(state: &AppState, op: &str, args: Value) -> Result<Value, String> {
        match dispatch(state, op, &args) {
            Ok(bytes) => Ok(serde_json::from_slice(&bytes).unwrap()),
            Err(CallError::Domain(m)) | Err(CallError::BadRequest(m)) => Err(m),
        }
    }

    fn open(tag: &str) -> (Temp, AppState) {
        let dir = std::env::temp_dir().join(format!("office-ffi-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let state = AppState::new(company::placeholder(), dir.clone(), office_core::schema::migrate);
        company::create_company(&state, &dir.join("Studio").to_string_lossy(), "Studio", "owner", "correct horse battery", "correct horse battery").unwrap();
        (Temp(dir), state)
    }

    #[test]
    fn shared_records_are_shared_between_apps_and_need_a_sign_in() {
        let (_dir, state) = open("records");
        assert_eq!(call(&state, "client_list", json!({ "include_archived": false })).unwrap_err(), "Not signed in.");
        workspace::login(&state, "owner", "correct horse battery").unwrap();
        let client = call(&state, "client_save", json!({ "client": { "id": null, "name": "Acme", "contact_person": "", "email": "", "phone": "", "address": "", "pan": "", "gstin": "" } })).unwrap();
        let project = call(&state, "project_save", json!({ "project": { "id": null, "client_id": client, "code": "", "name": "Villa", "site_address": "", "status": "active" } })).unwrap();
        assert_eq!(call(&state, "project_list", json!({ "client_id": null, "include_archived": false })).unwrap()[0]["client_name"], "Acme");
        call(&state, "project_brief_save", json!({ "project_id": project, "brief": { "style": "Modern" } })).unwrap();
        assert_eq!(call(&state, "project_brief_get", json!({ "project_id": project })).unwrap()["brief"]["style"], "Modern");
        call(&state, "staff_save", json!({ "input": { "id": null, "name": "Meera", "role": "Architect", "specialisation": "", "phone": "", "email": "", "joined_date": "2024-01-01", "left_date": "" } })).unwrap();
        assert_eq!(call(&state, "staff_list", json!({})).unwrap().as_array().unwrap().len(), 1);
        call(&state, "library_save", json!({ "input": { "id": null, "kind": "lesson", "title": "Waterproofing", "category": "", "reference": "", "body": "Test tanks first" } })).unwrap();
        assert_eq!(call(&state, "library_list", json!({ "kind": null, "search": "tanks" })).unwrap().as_array().unwrap().len(), 1);
        call(&state, "client_set_archived", json!({ "id": client, "archived": true })).unwrap();
        assert!(call(&state, "client_list", json!({ "include_archived": false })).unwrap().as_array().unwrap().is_empty());
        assert!(call(&state, "client_save", json!({ "client": "not a client" })).is_err(), "a malformed request is refused");
        assert!(call(&state, "no_such_op", json!({})).unwrap_err().contains("not part of"));
    }

    #[test]
    fn an_app_adds_its_own_tables_under_the_track_rules_and_a_reader_cannot_change_records() {
        let (_dir, state) = open("tracks");
        workspace::login(&state, "owner", "correct horse battery").unwrap();
        let migrations = json!(["CREATE TABLE aqc_schedules (id INTEGER PRIMARY KEY, project_id INTEGER REFERENCES projects(id), title TEXT NOT NULL);"]);
        assert_eq!(call(&state, "track.apply", json!({ "name": "aqc", "migrations": migrations })).unwrap(), 1);
        assert_eq!(call(&state, "track.apply", json!({ "name": "aqc", "migrations": migrations })).unwrap(), 1, "applying again changes nothing");
        assert_eq!(call(&state, "track.version", json!({ "name": "aqc" })).unwrap(), 1);
        assert!(call(&state, "track.apply", json!({ "name": "aqc", "migrations": [] })).unwrap_err().contains("newer version"), "an older list is refused");
        assert!(call(&state, "track.apply", json!({ "name": "aqd", "migrations": ["ALTER TABLE clients ADD COLUMN note TEXT;"] })).unwrap_err().contains("may not change"));
        {
            let conn = state.db.lock().unwrap();
            conn.execute("INSERT INTO users (username, password_hash, role) VALUES ('viewer', ?1, 'auditor')", [office_core::auth::hash_password("another long password").unwrap()]).unwrap();
        }
        workspace::logout(&state).unwrap();
        workspace::login(&state, "viewer", "another long password").unwrap();
        assert!(call(&state, "client_list", json!({ "include_archived": false })).is_ok(), "a read-only user can read");
        assert!(call(&state, "staff_save", json!({ "input": {} })).unwrap_err().contains("permission"));
    }

    #[test]
    fn a_project_is_named_by_a_reference_that_finds_it_again_and_a_malformed_artifact_reference_is_refused() {
        let (_dir, state) = open("refs");
        assert_eq!(call(&state, "project_ref", json!({ "project_id": 1 })).unwrap_err(), "Not signed in.");
        workspace::login(&state, "owner", "correct horse battery").unwrap();
        let client = call(&state, "client_save", json!({ "client": { "id": null, "name": "Acme", "contact_person": "", "email": "", "phone": "", "address": "", "pan": "", "gstin": "" } })).unwrap();
        let project = call(&state, "project_save", json!({ "project": { "id": null, "client_id": client, "code": "", "name": "Villa", "site_address": "", "status": "active" } })).unwrap();
        let reference = call(&state, "project_ref", json!({ "project_id": project })).unwrap();
        assert_eq!(reference["v"], 1);
        assert_eq!(reference["project_id"], project);
        assert!(reference.get("aorms_project_office_id").is_none());
        assert_eq!(call(&state, "project_resolve", json!({ "reference": reference })).unwrap(), project);
        assert!(call(&state, "project_ref", json!({ "project_id": 999 })).is_err());
        let artifact = json!({ "v": 1, "project": reference, "kind": "boq", "id": "BOQ-1", "revision": 1, "checksum_sha256": "a".repeat(64), "made_from": [] });
        call(&state, "artifact_ref_check", json!({ "reference": artifact })).unwrap();
        let mut bad = artifact.clone();
        bad["checksum_sha256"] = json!("nope");
        assert!(call(&state, "artifact_ref_check", json!({ "reference": bad })).unwrap_err().contains("checksum"));
    }

    #[test]
    fn two_apps_share_a_project_document_and_neither_overwrites_the_other() {
        let (dir, a) = open("sharedocs");
        workspace::login(&a, "owner", "correct horse battery").unwrap();
        let client = call(&a, "client_save", json!({ "client": { "id": null, "name": "Acme", "contact_person": "", "email": "", "phone": "", "address": "", "pan": "", "gstin": "" } })).unwrap();
        let project = call(&a, "project_save", json!({ "project": { "id": null, "client_id": client, "code": "", "name": "Villa", "site_address": "", "status": "active" } })).unwrap();
        // the second app opens the same company file while the first still has it
        let path = dir.0.join("Studio.osas");
        let b = AppState::new(company::placeholder(), dir.0.clone(), office_core::schema::migrate);
        company::open_company(&b, &path.to_string_lossy(), "", "").unwrap();
        workspace::login(&b, "owner", "correct horse battery").unwrap();
        let before = call(&b, "data_version", json!({})).unwrap();
        assert_eq!(before["schema_ok"], true);
        assert!(call(&a, "doc_get", json!({ "project_id": project, "kind": "aqc.project" })).unwrap().is_null());
        let first = call(&a, "doc_put", json!({ "project_id": project, "kind": "aqc.project", "expected_revision": 0, "body": "{\"levels\":2}" })).unwrap();
        assert_eq!(first["revision"], 1);
        assert_ne!(call(&b, "data_version", json!({})).unwrap()["version"], before["version"], "B sees that A wrote");
        let seen = call(&b, "doc_get", json!({ "project_id": project, "kind": "aqc.project" })).unwrap();
        assert_eq!((seen["revision"].as_i64(), seen["body"].as_str()), (Some(1), Some("{\"levels\":2}")));
        assert_eq!(seen["checksum_sha256"], first["checksum_sha256"]);
        call(&b, "doc_put", json!({ "project_id": project, "kind": "aqc.project", "expected_revision": 1, "body": "{\"levels\":3}" })).unwrap();
        let stale = call(&a, "doc_put", json!({ "project_id": project, "kind": "aqc.project", "expected_revision": 1, "body": "{\"levels\":9}" })).unwrap_err();
        assert!(stale.contains("changed in another app"), "{stale}");
        assert_eq!(call(&a, "doc_list", json!({ "project_id": project })).unwrap()[0]["revision"], 2);
        assert_eq!(call(&a, "doc_revision", json!({ "project_id": project, "kind": "aqc.project", "revision": 1 })).unwrap()["body"], "{\"levels\":2}");
        assert!(call(&b, "project_list", json!({ "client_id": null, "include_archived": false })).unwrap().as_array().unwrap().iter().any(|p| p["name"] == "Villa"), "the project A made is B's too");
    }

    #[test]
    fn watching_for_changes_does_not_count_as_activity_so_an_idle_session_still_locks() {
        let (_dir, a) = open("watch");
        assert_eq!(call(&a, "watch", json!({})).unwrap_err(), "Not signed in.");
        workspace::login(&a, "owner", "correct horse battery").unwrap();
        let before = a.session.lock().unwrap().as_ref().unwrap().last_activity;
        std::thread::sleep(std::time::Duration::from_millis(30));
        let watched = call(&a, "watch", json!({ "project_id": null })).unwrap();
        assert_eq!(watched["schema_ok"], true);
        assert_eq!(a.session.lock().unwrap().as_ref().unwrap().last_activity, before, "watching left the idle timer alone");
        call(&a, "client_list", json!({ "include_archived": false })).unwrap();
        assert!(a.session.lock().unwrap().as_ref().unwrap().last_activity > before, "an ordinary call does count");
    }

    #[test]
    fn the_company_details_are_shared_and_only_someone_who_manages_settings_can_change_them() {
        let (_dir, a) = open("profile");
        workspace::login(&a, "owner", "correct horse battery").unwrap();
        let mut profile = call(&a, "company_profile_get", json!({})).unwrap();
        assert_eq!(profile["name"], "Studio");
        profile["name"] = json!("Studio One");
        profile["gstin"] = json!("29ABCDE1234F1Z5");
        call(&a, "company_profile_save", json!({ "profile": profile })).unwrap();
        assert_eq!(call(&a, "company_profile_get", json!({})).unwrap()["gstin"], "29ABCDE1234F1Z5");
        {
            let conn = a.db.lock().unwrap();
            conn.execute("INSERT INTO users (username, password_hash, role) VALUES ('viewer', ?1, 'auditor')", [office_core::auth::hash_password("another long password").unwrap()]).unwrap();
        }
        workspace::logout(&a).unwrap();
        workspace::login(&a, "viewer", "another long password").unwrap();
        assert_eq!(call(&a, "company_profile_get", json!({})).unwrap()["name"], "Studio One", "anyone signed in can read the details");
        let mut again = call(&a, "company_profile_get", json!({})).unwrap();
        again["name"] = json!("Changed");
        assert!(call(&a, "company_profile_save", json!({ "profile": again })).unwrap_err().contains("permission"));
    }

    #[test]
    fn people_are_managed_through_the_office_interface_and_each_change_needs_the_administrators_password() {
        let (_dir, a) = open("users");
        assert_eq!(call(&a, "user_list", json!({})).unwrap_err(), "Not signed in.");
        workspace::login(&a, "owner", "correct horse battery").unwrap();
        assert_eq!(call(&a, "user_list", json!({})).unwrap().as_array().unwrap().len(), 1);
        let refused = call(&a, "user_create", json!({ "username": "ravi", "password": "a long enough password", "role": "accounts", "admin_password": "not my password" })).unwrap_err();
        assert_eq!(refused, "Your password was not accepted.");
        let id = call(&a, "user_create", json!({ "username": "ravi", "password": "a long enough password", "role": "accounts", "admin_password": "correct horse battery" })).unwrap();
        call(&a, "user_update", json!({ "id": id, "role": "principal", "disabled": false, "admin_password": "correct horse battery" })).unwrap();
        call(&a, "user_set_password", json!({ "id": id, "password": "another long password", "admin_password": "correct horse battery" })).unwrap();
        let listed = call(&a, "user_list", json!({})).unwrap();
        assert!(listed.as_array().unwrap().iter().any(|u| u["username"] == "ravi" && u["role"] == "principal"));
        // the new password works for that person, and a person without the permission cannot manage people
        workspace::logout(&a).unwrap();
        workspace::login(&a, "ravi", "another long password").unwrap();
        assert!(call(&a, "user_list", json!({})).unwrap_err().contains("permission"));
    }
}
