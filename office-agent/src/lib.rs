//! A governed set of tools over the office records, for an AI assistant.
//!
//! The assistant never touches the database. It names a tool and supplies arguments; this crate checks the arguments against the
//! tool's schema, checks that the signed-in user may use the tool, runs a fixed operation, and records the call in the audit log.
//! Every tool here only reads. Results are marked untrusted, because records can contain text typed by anyone, and are cut to a safe size.
//!
//! The tools are the same operations the office app uses, so the assistant sees exactly what the signed-in user can see.

pub mod mcp;
pub mod schema;

use office_core::auth::Permission;
use office_core::workspace::{require, AppState, INTERNAL};
use office_core::{audit, clients, office, practice, tds26as};
use rusqlite::Connection;
use serde_json::{json, Value};

/// The most array entries one result carries; longer lists are cut and say so.
pub const MAX_ITEMS: usize = 200;

pub struct Tool {
    pub name: &'static str,
    pub description: &'static str,
    pub input_schema: fn() -> Value,
    pub permission: Permission,
    run: fn(&Connection, &Value) -> Result<Value, String>,
}

fn s(max: u64) -> Value {
    json!({ "type": "string", "maxLength": max })
}

fn object(properties: Value, required: &[&str]) -> Value {
    json!({ "type": "object", "properties": properties, "required": required, "additionalProperties": false })
}

fn text<'a>(args: &'a Value, key: &str) -> &'a str {
    args.get(key).and_then(Value::as_str).unwrap_or("")
}

fn int(args: &Value, key: &str) -> Option<i64> {
    args.get(key).and_then(Value::as_i64)
}

fn contains(haystack: &[&str], needle: &str) -> bool {
    let needle = needle.to_lowercase();
    needle.is_empty() || haystack.iter().any(|h| h.to_lowercase().contains(&needle))
}

fn to_value<T: serde::Serialize>(value: &T) -> Result<Value, String> {
    serde_json::to_value(value).map_err(|_| INTERNAL.to_string())
}

pub const TOOLS: &[Tool] = &[
    Tool {
        name: "clients.search",
        description: "Find clients by a word in their name, contact person, e-mail or city. Leave the query empty to list them all.",
        input_schema: || object(json!({ "query": s(100), "include_archived": { "type": "boolean" } }), &[]),
        permission: Permission::View,
        run: |conn, args| {
            let query = text(args, "query");
            let all = clients::list_clients(conn, args.get("include_archived").and_then(Value::as_bool).unwrap_or(false))?;
            let found: Vec<Value> = all
                .iter()
                .filter(|c| contains(&[&c.name, &c.contact_person, &c.email, &c.city], query))
                .map(|c| json!({ "id": c.id, "name": c.name, "contact_person": c.contact_person, "email": c.email, "phone": c.phone, "city": c.city, "archived": c.archived }))
                .collect();
            Ok(Value::Array(found))
        },
    },
    Tool {
        name: "company.details",
        description: "The company's own details (name, address, contacts, PAN, GSTIN, Council of Architecture registration) and, when a project is given, that project's name, site, shared description (summary), client, service, stage and brief. Bank and UPI details are not included.",
        input_schema: || object(json!({ "project_id": { "type": "integer", "minimum": 1 } }), &[]),
        permission: Permission::View,
        run: |conn, args| {
            let context = office_core::context::read(conn, int(args, "project_id"))?;
            let mut value = to_value(&context)?;
            if let Some(company) = value.get_mut("company").and_then(Value::as_object_mut) {
                company.remove("bank_details");
                company.remove("upi_id");
            }
            Ok(value)
        },
    },
    Tool {
        name: "projects.list",
        description: "List projects, optionally only those of one client. Each has its client's name, site address, shared description (summary), status and the stage it has reached.",
        input_schema: || object(json!({ "client_id": { "type": "integer", "minimum": 1 } }), &[]),
        permission: Permission::View,
        run: |conn, args| to_value(&clients::list_projects(conn, int(args, "client_id"), false)?),
    },
    Tool {
        name: "projects.brief",
        description: "The client's brief for one project: site, budget, spaces, preferences and assumptions as recorded.",
        input_schema: || object(json!({ "project_id": { "type": "integer", "minimum": 1 } }), &["project_id"]),
        permission: Permission::View,
        run: |conn, args| clients::get_brief(conn, int(args, "project_id").unwrap_or(0)),
    },
    Tool {
        name: "library.search",
        description: "Search the office library of specifications, standards, compliance notes and lessons learned by a word in the title, category, reference or text.",
        input_schema: || object(json!({ "query": s(100), "kind": { "type": "string", "enum": ["specification", "standard", "compliance", "lesson"] } }), &[]),
        permission: Permission::View,
        run: |conn, args| {
            let kind = args.get("kind").and_then(Value::as_str);
            Ok(Value::Array(office::library_list(conn, kind, text(args, "query"))?))
        },
    },
    Tool {
        name: "staff.list",
        description: "The staff roster with role, specialisation and leave days taken this financial year.",
        input_schema: || object(json!({}), &[]),
        permission: Permission::View,
        run: |conn, _| Ok(Value::Array(office::staff_list(conn)?)),
    },
    Tool {
        name: "proposals.list",
        description: "Fee proposals with how each fee was worked out, GST, the total and where the proposal stands. Optionally only one client's.",
        input_schema: || object(json!({ "client_id": { "type": "integer", "minimum": 1 } }), &[]),
        permission: Permission::View,
        run: |conn, args| Ok(Value::Array(practice::proposal_list(conn, int(args, "client_id"))?)),
    },
    Tool {
        name: "meetings.list",
        description: "Meetings with their decisions and follow-ups, newest first. Optionally only one project's. A follow-up marked due has passed its date and is not done.",
        input_schema: || object(json!({ "project_id": { "type": "integer", "minimum": 1 } }), &[]),
        permission: Permission::View,
        run: |conn, args| Ok(Value::Array(practice::meeting_list(conn, int(args, "project_id"))?)),
    },
    Tool {
        name: "finance.tds26as_check",
        description: "Compare the TDS booked on receipts with what clients' deductors reported in Form 26AS, for a period. Needs finance access.",
        input_schema: || object(json!({ "from": s(10), "to": s(10) }), &["from", "to"]),
        permission: Permission::Finance,
        run: |conn, args| tds26as::reconcile(conn, text(args, "from"), text(args, "to")),
    },
];

pub fn tool(name: &str) -> Option<&'static Tool> {
    TOOLS.iter().find(|t| t.name == name)
}

/// Cuts any array longer than `MAX_ITEMS` and says so, so a huge result cannot flood the assistant.
fn limit(value: Value) -> (Value, bool) {
    match value {
        Value::Array(mut items) if items.len() > MAX_ITEMS => {
            items.truncate(MAX_ITEMS);
            (Value::Array(items), true)
        }
        other => (other, false),
    }
}

/// Runs a tool for the signed-in user. A call that is refused, whether for a bad argument or a missing permission, never reaches the data.
pub fn call(state: &AppState, name: &str, args: &Value) -> Result<Value, String> {
    let tool = tool(name).ok_or_else(|| format!("There is no tool called {name}."))?;
    let args = if args.is_null() { json!({}) } else { args.clone() };
    let outcome = (|| {
        schema::validate(&(tool.input_schema)(), &args).map_err(|why| format!("The arguments for {name} are not valid: {why}"))?;
        let user = require(state, tool.permission)?;
        let conn = state.db.lock().map_err(|_| INTERNAL.to_string())?;
        let data = (tool.run)(&conn, &args)?;
        audit(&conn, Some(user), "agent_tool_call", name);
        Ok::<Value, String>(data)
    })();
    match outcome {
        Ok(data) => {
            let (data, truncated) = limit(data);
            Ok(json!({ "tool": name, "status": "ok", "data_is_untrusted": true, "truncated": truncated, "data": data }))
        }
        Err(why) => {
            if let Ok(conn) = state.db.lock() {
                audit(&conn, None, "agent_tool_refused", name);
            }
            Err(why)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use office_core::{company, workspace};

    pub struct Temp(pub std::path::PathBuf);
    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    /// A signed-in owner session on a new company file with a client and a project.
    pub fn signed_in(tag: &str) -> (Temp, AppState) {
        let dir = std::env::temp_dir().join(format!("office-agent-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let state = AppState::new(company::placeholder(), dir.clone(), office_core::schema::migrate);
        company::create_company(&state, &dir.join("Studio").to_string_lossy(), "Studio", "owner", "correct horse battery", "correct horse battery").unwrap();
        workspace::login(&state, "owner", "correct horse battery").unwrap();
        {
            let conn = state.db.lock().unwrap();
            conn.execute("INSERT INTO clients (name, city, email) VALUES ('Rao family', 'Pune', 'rao@example.com')", []).unwrap();
            conn.execute("INSERT INTO clients (name, city) VALUES ('Acme Builders', 'Mumbai')", []).unwrap();
            conn.execute("INSERT INTO projects (client_id, name) VALUES (1, 'Farmhouse')", []).unwrap();
        }
        (Temp(dir), state)
    }

    #[test]
    fn every_tool_has_a_schema_that_forbids_extra_arguments_and_a_description() {
        let mut names = std::collections::HashSet::new();
        for t in TOOLS {
            assert!(names.insert(t.name), "duplicate tool {}", t.name);
            assert!(!t.description.is_empty() && t.description.len() > 20);
            let schema = (t.input_schema)();
            assert_eq!(schema["type"], "object");
            assert_eq!(schema["additionalProperties"], false, "{} must refuse unknown arguments", t.name);
        }
    }

    #[test]
    fn a_tool_answers_from_the_records_and_marks_the_result_untrusted() {
        let (_dir, state) = signed_in("answers");
        let found = call(&state, "clients.search", &json!({ "query": "pune" })).unwrap();
        assert_eq!((found["status"].clone(), found["data_is_untrusted"].clone()), (json!("ok"), json!(true)));
        assert_eq!(found["data"].as_array().unwrap().len(), 1);
        assert_eq!(found["data"][0]["name"], "Rao family");
        assert_eq!(call(&state, "clients.search", &json!({})).unwrap()["data"].as_array().unwrap().len(), 2);
        assert_eq!(call(&state, "projects.list", &json!({ "client_id": 1 })).unwrap()["data"][0]["name"], "Farmhouse");
        assert_eq!(call(&state, "projects.brief", &json!({ "project_id": 1 })).unwrap()["data"]["project_id"], 1);
        assert_eq!(call(&state, "staff.list", &Value::Null).unwrap()["data"], json!([]));
    }

    #[test]
    fn bad_arguments_unknown_tools_and_missing_permission_are_refused_and_logged() {
        let (_dir, state) = signed_in("refused");
        assert!(call(&state, "clients.delete", &json!({})).unwrap_err().contains("no tool"));
        assert!(call(&state, "clients.search", &json!({ "query": 5 })).unwrap_err().contains("not valid"));
        assert!(call(&state, "clients.search", &json!({ "query": "x", "sql": "DROP TABLE clients" })).unwrap_err().contains("not valid"), "an unknown argument is refused");
        assert!(call(&state, "projects.brief", &json!({})).unwrap_err().contains("project_id"));
        assert!(call(&state, "projects.brief", &json!({ "project_id": 0 })).is_err());
        assert!(call(&state, "library.search", &json!({ "kind": "secret" })).is_err());
        {
            let conn = state.db.lock().unwrap();
            conn.execute("INSERT INTO users (username, password_hash, role) VALUES ('drafter', ?1, 'document_editor')", [office_core::auth::hash_password("another long password").unwrap()]).unwrap();
        }
        workspace::logout(&state).unwrap();
        assert!(call(&state, "clients.search", &json!({})).unwrap_err().contains("Not signed in"), "nothing runs without a sign-in");
        workspace::login(&state, "drafter", "another long password").unwrap();
        assert!(call(&state, "clients.search", &json!({})).is_ok(), "a drafter may read the records");
        assert!(call(&state, "finance.tds26as_check", &json!({ "from": "2026-04-01", "to": "2027-03-31" })).unwrap_err().contains("permission"), "finance tools need finance access");
        let conn = state.db.lock().unwrap();
        let ok: i64 = conn.query_row("SELECT COUNT(*) FROM audit_log WHERE event = 'agent_tool_call'", [], |r| r.get(0)).unwrap();
        let refused: i64 = conn.query_row("SELECT COUNT(*) FROM audit_log WHERE event = 'agent_tool_refused'", [], |r| r.get(0)).unwrap();
        assert!(ok >= 1 && refused >= 7, "calls and refusals are both in the audit log ({ok} ok, {refused} refused)");
    }

    #[test]
    fn a_long_result_is_cut_and_says_so() {
        let (_dir, state) = signed_in("long");
        {
            let conn = state.db.lock().unwrap();
            for n in 0..(MAX_ITEMS + 20) {
                conn.execute("INSERT INTO clients (name) VALUES (?1)", [format!("Client {n}")]).unwrap();
            }
        }
        let result = call(&state, "clients.search", &json!({})).unwrap();
        assert_eq!((result["data"].as_array().unwrap().len(), result["truncated"].clone()), (MAX_ITEMS, json!(true)));
    }
}
