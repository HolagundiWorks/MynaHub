//! Practice tasks for a solo architect: a Kanban board (todo / doing / done), optionally tagged with a project.

use rusqlite::{params, Connection};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::schema::audit;

const STATUSES: &[&str] = &["todo", "doing", "done"];

#[derive(Deserialize)]
pub struct TaskInput {
    pub id: Option<i64>,
    pub project_id: Option<i64>,
    pub title: String,
    #[serde(default)]
    pub notes: String,
    #[serde(default = "default_status")]
    pub status: String,
    #[serde(default)]
    pub position: i64,
    #[serde(default)]
    pub due_date: String,
}

fn default_status() -> String {
    "todo".into()
}

fn title(raw: &str) -> Result<String, String> {
    let t = raw.trim();
    if t.is_empty() {
        return Err("Enter a task title.".into());
    }
    if t.chars().count() > 200 {
        return Err("Keep the title under 200 characters.".into());
    }
    Ok(t.into())
}

fn status(raw: &str) -> Result<&str, String> {
    STATUSES.iter().copied().find(|s| *s == raw).ok_or_else(|| "Task status must be todo, doing or done.".into())
}

fn due(raw: &str) -> Result<String, String> {
    let d = raw.trim();
    if d.is_empty() {
        return Ok(String::new());
    }
    if d.len() != 10 || d.as_bytes()[4] != b'-' || d.as_bytes()[7] != b'-' {
        return Err("Due date must be YYYY-MM-DD.".into());
    }
    Ok(d.into())
}

fn project_ok(conn: &Connection, id: Option<i64>) -> Result<(), String> {
    let Some(id) = id else { return Ok(()) };
    let n: i64 = conn.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1 AND archived = 0", [id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("Choose an active project, or leave the task untagged.".into());
    }
    Ok(())
}

pub fn save(conn: &Connection, user_id: i64, i: &TaskInput) -> Result<i64, String> {
    let title = title(&i.title)?;
    let status = status(&i.status)?;
    let notes = i.notes.trim();
    if notes.chars().count() > 4000 {
        return Err("Keep the notes under 4,000 characters.".into());
    }
    let due_date = due(&i.due_date)?;
    project_ok(conn, i.project_id)?;
    match i.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE tasks SET project_id=?2, title=?3, notes=?4, status=?5, position=?6, due_date=?7, updated_at=datetime('now') WHERE id=?1",
                    params![id, i.project_id, title, notes, status, i.position, due_date],
                )
                .map_err(|e| e.to_string())?;
            if n == 0 {
                return Err("That task does not exist.".into());
            }
            audit(conn, Some(user_id), "task_updated", &id.to_string());
            Ok(id)
        }
        None => {
            let position: i64 = conn
                .query_row("SELECT COALESCE(MAX(position), 0) + 1 FROM tasks WHERE status = ?1", [status], |r| r.get(0))
                .unwrap_or(1);
            conn.execute(
                "INSERT INTO tasks (project_id, title, notes, status, position, due_date, created_by) VALUES (?1,?2,?3,?4,?5,?6,?7)",
                params![i.project_id, title, notes, status, position, due_date, user_id],
            )
            .map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            audit(conn, Some(user_id), "task_created", &id.to_string());
            Ok(id)
        }
    }
}

pub fn set_status(conn: &Connection, user_id: i64, id: i64, raw_status: &str, position: Option<i64>) -> Result<(), String> {
    let status = status(raw_status)?;
    let position = position.unwrap_or_else(|| {
        conn.query_row("SELECT COALESCE(MAX(position), 0) + 1 FROM tasks WHERE status = ?1", [status], |r| r.get(0)).unwrap_or(1)
    });
    let n = conn
        .execute("UPDATE tasks SET status=?2, position=?3, updated_at=datetime('now') WHERE id=?1", params![id, status, position])
        .map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("That task does not exist.".into());
    }
    audit(conn, Some(user_id), "task_status", &format!("{id} {status}"));
    Ok(())
}

pub fn delete(conn: &Connection, user_id: i64, id: i64) -> Result<(), String> {
    let n = conn.execute("DELETE FROM tasks WHERE id = ?1", [id]).map_err(|e| e.to_string())?;
    if n == 0 {
        return Err("That task does not exist.".into());
    }
    audit(conn, Some(user_id), "task_deleted", &id.to_string());
    Ok(())
}

pub fn list(conn: &Connection, project_id: Option<i64>) -> Result<Vec<Value>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT t.id, t.project_id, COALESCE(p.name, ''), COALESCE(p.code, ''), t.title, t.notes, t.status, t.position, t.due_date
             FROM tasks t LEFT JOIN projects p ON p.id = t.project_id
             WHERE (?1 IS NULL OR t.project_id = ?1)
             ORDER BY CASE t.status WHEN 'todo' THEN 0 WHEN 'doing' THEN 1 ELSE 2 END, t.position, t.id",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([project_id], |r| {
            Ok(json!({
                "id": r.get::<_, i64>(0)?,
                "project_id": r.get::<_, Option<i64>>(1)?,
                "project": r.get::<_, String>(2)?,
                "project_code": r.get::<_, String>(3)?,
                "title": r.get::<_, String>(4)?,
                "notes": r.get::<_, String>(5)?,
                "status": r.get::<_, String>(6)?,
                "position": r.get::<_, i64>(7)?,
                "due_date": r.get::<_, String>(8)?,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO users (username, password_hash, role) VALUES ('ada','x','owner')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Client')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, code, name) VALUES (1, 'P1', 'House')", []).unwrap();
        c
    }

    #[test]
    fn board_moves_and_tags_a_project() {
        let c = conn();
        let id = save(&c, 1, &TaskInput { id: None, project_id: Some(1), title: "Sketch plan".into(), notes: String::new(), status: "todo".into(), position: 0, due_date: String::new() }).unwrap();
        set_status(&c, 1, id, "doing", None).unwrap();
        let rows = list(&c, Some(1)).unwrap();
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0]["status"], "doing");
        assert_eq!(rows[0]["project"], "House");
    }
}
