//! Fee proposals, client meetings and decisions, and staff attendance. Plain records beside the books; none posts.

use crate::audit;
use crate::check::{date, one_of, text, today};
use rusqlite::{params, Connection};
use serde::Deserialize;
use serde_json::{json, Value};

const BASES: [&str; 3] = ["percent", "per_area", "lump_sum"];
const PROPOSAL_STATUS: [&str; 4] = ["draft", "sent", "accepted", "declined"];
const ATTENDANCE: [&str; 5] = ["present", "half", "absent", "leave", "holiday"];

// ---------------------------------------------------------------- fee proposals

#[derive(Deserialize)]
pub struct ProposalInput {
    pub id: Option<i64>,
    pub client_id: i64,
    pub project_id: Option<i64>,
    pub title: String,
    pub basis: String,
    /// The estimated cost of the work, for a fee that is a percentage of it.
    pub cost_paise: i64,
    pub rate_bp: i64,
    /// The built-up area in thousandths of a unit, for a fee per unit of area.
    pub area_milli: i64,
    pub area_rate_paise: i64,
    /// The fee itself, for a lump sum.
    pub lump_paise: i64,
    pub gst_bp: i64,
    pub issued_date: String,
    pub valid_until: String,
    pub notes: String,
    /// Suite ArtifactRef JSON for the Falcon estimate this fee was based on (empty when not linked).
    #[serde(default)]
    pub estimate_artifact_json: String,
    /// Grand total of that estimate in paise at the time it was linked (0 when not linked).
    #[serde(default)]
    pub estimate_total_paise: i64,
}

/// The fee in paise, rounded half up. Each basis uses only its own figures.
pub fn fee_for(i: &ProposalInput) -> Result<i64, String> {
    let fee: i128 = match i.basis.as_str() {
        "percent" => (i.cost_paise as i128 * i.rate_bp as i128 + 5000) / 10_000,
        "per_area" => (i.area_milli as i128 * i.area_rate_paise as i128 + 500) / 1000,
        "lump_sum" => i.lump_paise as i128,
        _ => return Err("Choose how the fee is worked out.".into()),
    };
    if fee <= 0 {
        return Err(match i.basis.as_str() {
            "percent" => "Enter the cost of the work and the percentage.".into(),
            "per_area" => "Enter the area and the rate per unit of area.".into(),
            _ => "Enter the fee.".into(),
        });
    }
    if fee > 1_000_000_000_000_00 {
        return Err("That fee is too large.".into());
    }
    Ok(fee as i64)
}

pub fn proposal_save(conn: &Connection, user_id: i64, i: &ProposalInput) -> Result<i64, String> {
    let basis = one_of(&i.basis, &BASES, "basis for the fee")?;
    let title = text(&i.title, 160, "title", true)?;
    let issued = date(&i.issued_date, true)?;
    let valid = date(&i.valid_until, false)?;
    let notes = text(&i.notes, 1000, "notes", false)?;
    if !valid.is_empty() && valid < issued {
        return Err("The proposal is valid until a date before it was issued.".into());
    }
    if !(0..=2800).contains(&i.gst_bp) {
        return Err("Enter GST between 0 and 28 percent.".into());
    }
    if !(0..=10_000).contains(&i.rate_bp) {
        return Err("Enter a percentage between 0 and 100.".into());
    }
    for amount in [i.cost_paise, i.area_milli, i.area_rate_paise, i.lump_paise, i.estimate_total_paise] {
        if !(0..=1_000_000_000_000_00).contains(&amount) {
            return Err("Amounts cannot be negative or absurdly large.".into());
        }
    }
    let estimate_json = text(&i.estimate_artifact_json, 4000, "estimate link", false)?;
    if !estimate_json.is_empty() {
        let parsed: Value = serde_json::from_str(&estimate_json).map_err(|_| "The estimate link is not valid JSON.".to_string())?;
        if parsed.get("kind").and_then(|v| v.as_str()) != Some("estimate") {
            return Err("The estimate link must name an estimate ArtifactRef.".into());
        }
    }
    let fee = fee_for(&ProposalInput { basis: basis.clone(), ..clone_figures(i) })?;
    let known: i64 = conn.query_row("SELECT COUNT(*) FROM clients WHERE id = ?1", [i.client_id], |r| r.get(0)).map_err(|e| e.to_string())?;
    if known == 0 {
        return Err("Choose a client.".into());
    }
    if let Some(project) = i.project_id {
        let n: i64 = conn.query_row("SELECT COUNT(*) FROM projects WHERE id = ?1 AND client_id = ?2", params![project, i.client_id], |r| r.get(0)).map_err(|e| e.to_string())?;
        if n == 0 {
            return Err("That project does not belong to this client.".into());
        }
    }
    match i.id {
        Some(id) => {
            let status: String = conn.query_row("SELECT status FROM fee_proposals WHERE id = ?1", [id], |r| r.get(0)).map_err(|_| "That proposal does not exist.".to_string())?;
            if status == "accepted" || status == "declined" {
                return Err("A proposal that has been accepted or declined cannot change. Make a new one.".into());
            }
            conn.execute(
                "UPDATE fee_proposals SET client_id=?2, project_id=?3, title=?4, basis=?5, cost_paise=?6, rate_bp=?7, area_milli=?8, area_rate_paise=?9,
                 fee_paise=?10, gst_bp=?11, issued_date=?12, valid_until=?13, notes=?14, estimate_artifact_json=?15, estimate_total_paise=?16 WHERE id = ?1",
                params![id, i.client_id, i.project_id, title, basis, i.cost_paise, i.rate_bp, i.area_milli, i.area_rate_paise, fee, i.gst_bp, issued, valid, notes, estimate_json, i.estimate_total_paise],
            )
            .map_err(|e| e.to_string())?;
            audit(conn, Some(user_id), "proposal_updated", &id.to_string());
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO fee_proposals (client_id, project_id, title, basis, cost_paise, rate_bp, area_milli, area_rate_paise, fee_paise, gst_bp, issued_date, valid_until, notes, created_by, estimate_artifact_json, estimate_total_paise)
                 VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15,?16)",
                params![i.client_id, i.project_id, title, basis, i.cost_paise, i.rate_bp, i.area_milli, i.area_rate_paise, fee, i.gst_bp, issued, valid, notes, user_id, estimate_json, i.estimate_total_paise],
            )
            .map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            conn.execute("UPDATE fee_proposals SET number = ?2 WHERE id = ?1", params![id, format!("FP-{:04}", id)]).map_err(|e| e.to_string())?;
            audit(conn, Some(user_id), "proposal_created", &id.to_string());
            Ok(id)
        }
    }
}

fn clone_figures(i: &ProposalInput) -> ProposalInput {
    ProposalInput {
        id: i.id, client_id: i.client_id, project_id: i.project_id, title: String::new(), basis: i.basis.clone(), cost_paise: i.cost_paise, rate_bp: i.rate_bp,
        area_milli: i.area_milli, area_rate_paise: i.area_rate_paise, lump_paise: i.lump_paise, gst_bp: i.gst_bp, issued_date: String::new(), valid_until: String::new(), notes: String::new(),
        estimate_artifact_json: String::new(), estimate_total_paise: 0,
    }
}

/// Moves a proposal along: draft to sent, sent to accepted or declined. Accepted and declined are final.
pub fn proposal_set_status(conn: &Connection, user_id: i64, id: i64, status: &str) -> Result<(), String> {
    let status = one_of(status, &PROPOSAL_STATUS, "status")?;
    let current: String = conn.query_row("SELECT status FROM fee_proposals WHERE id = ?1", [id], |r| r.get(0)).map_err(|_| "That proposal does not exist.".to_string())?;
    let allowed = matches!((current.as_str(), status.as_str()), ("draft", "sent") | ("sent", "accepted") | ("sent", "declined") | ("sent", "draft"));
    if !allowed {
        return Err(format!("A {current} proposal cannot become {status}."));
    }
    conn.execute("UPDATE fee_proposals SET status = ?2 WHERE id = ?1", params![id, status]).map_err(|e| e.to_string())?;
    audit(conn, Some(user_id), "proposal_status", &format!("{id} {status}"));
    Ok(())
}

pub fn proposal_list(conn: &Connection, client_id: Option<i64>) -> Result<Vec<Value>, String> {
    let today = today(conn);
    let mut stmt = conn
        .prepare(
            "SELECT f.id, f.number, f.client_id, c.name, f.project_id, COALESCE(p.name, ''), f.title, f.basis, f.cost_paise, f.rate_bp, f.area_milli, f.area_rate_paise,
                    f.fee_paise, f.gst_bp, f.status, f.issued_date, f.valid_until, f.notes, f.estimate_artifact_json, f.estimate_total_paise
             FROM fee_proposals f JOIN clients c ON c.id = f.client_id LEFT JOIN projects p ON p.id = f.project_id
             WHERE (?1 IS NULL OR f.client_id = ?1) ORDER BY f.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([client_id], |r| {
            let fee: i64 = r.get(12)?;
            let gst_bp: i64 = r.get(13)?;
            let status: String = r.get(14)?;
            let valid: String = r.get(16)?;
            let estimate_json: String = r.get(18)?;
            let estimate_total: i64 = r.get(19)?;
            let gst = (fee as i128 * gst_bp as i128 + 5000) / 10_000;
            Ok(json!({
                "id": r.get::<_, i64>(0)?, "number": r.get::<_, String>(1)?, "client_id": r.get::<_, i64>(2)?, "client": r.get::<_, String>(3)?,
                "project_id": r.get::<_, Option<i64>>(4)?, "project": r.get::<_, String>(5)?, "title": r.get::<_, String>(6)?, "basis": r.get::<_, String>(7)?,
                "cost_paise": r.get::<_, i64>(8)?, "rate_bp": r.get::<_, i64>(9)?, "area_milli": r.get::<_, i64>(10)?, "area_rate_paise": r.get::<_, i64>(11)?,
                "fee_paise": fee, "gst_bp": gst_bp, "gst_paise": gst as i64, "total_paise": fee + gst as i64, "status": status,
                "issued_date": r.get::<_, String>(15)?, "valid_until": valid, "notes": r.get::<_, String>(17)?,
                "estimate_artifact_json": estimate_json, "estimate_total_paise": estimate_total,
                "estimate_linked": estimate_total > 0 || !estimate_json.is_empty(),
                "lapsed": status == "sent" && !valid.is_empty() && valid < today,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

// ---------------------------------------------------------------- meetings

#[derive(Deserialize)]
pub struct MeetingInput {
    pub id: Option<i64>,
    pub meeting_date: String,
    pub title: String,
    pub client_id: Option<i64>,
    pub project_id: Option<i64>,
    pub attendees: String,
    pub notes: String,
    pub decisions: String,
    pub follow_up_date: String,
    pub follow_up_done: bool,
}

pub fn meeting_save(conn: &Connection, user_id: i64, i: &MeetingInput) -> Result<i64, String> {
    let day = date(&i.meeting_date, true)?;
    let title = text(&i.title, 160, "title", true)?;
    let attendees = text(&i.attendees, 500, "attendees", false)?;
    let notes = text(&i.notes, 4000, "notes", false)?;
    let decisions = text(&i.decisions, 4000, "decisions", false)?;
    let follow = date(&i.follow_up_date, false)?;
    if !follow.is_empty() && follow < day {
        return Err("The follow-up date is before the meeting.".into());
    }
    for (table, id) in [("clients", i.client_id), ("projects", i.project_id)] {
        if let Some(id) = id {
            let n: i64 = conn.query_row(&format!("SELECT COUNT(*) FROM {table} WHERE id = ?1"), [id], |r| r.get(0)).map_err(|e| e.to_string())?;
            if n == 0 {
                return Err(format!("That {} does not exist.", &table[..table.len() - 1]));
            }
        }
    }
    match i.id {
        Some(id) => {
            let n = conn
                .execute(
                    "UPDATE meetings SET meeting_date=?2, title=?3, client_id=?4, project_id=?5, attendees=?6, notes=?7, decisions=?8, follow_up_date=?9, follow_up_done=?10 WHERE id = ?1",
                    params![id, day, title, i.client_id, i.project_id, attendees, notes, decisions, follow, i.follow_up_done],
                )
                .map_err(|e| e.to_string())?;
            if n == 0 {
                return Err("That meeting is not in the log.".into());
            }
            audit(conn, Some(user_id), "meeting_updated", &id.to_string());
            Ok(id)
        }
        None => {
            conn.execute(
                "INSERT INTO meetings (meeting_date, title, client_id, project_id, attendees, notes, decisions, follow_up_date, follow_up_done, created_by) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10)",
                params![day, title, i.client_id, i.project_id, attendees, notes, decisions, follow, i.follow_up_done, user_id],
            )
            .map_err(|e| e.to_string())?;
            let id = conn.last_insert_rowid();
            audit(conn, Some(user_id), "meeting_added", &id.to_string());
            Ok(id)
        }
    }
}

pub fn meeting_list(conn: &Connection, project_id: Option<i64>) -> Result<Vec<Value>, String> {
    let today = today(conn);
    let mut stmt = conn
        .prepare(
            "SELECT m.id, m.meeting_date, m.title, m.client_id, COALESCE(c.name, ''), m.project_id, COALESCE(p.name, ''), m.attendees, m.notes, m.decisions, m.follow_up_date, m.follow_up_done
             FROM meetings m LEFT JOIN clients c ON c.id = m.client_id LEFT JOIN projects p ON p.id = m.project_id
             WHERE (?1 IS NULL OR m.project_id = ?1) ORDER BY m.meeting_date DESC, m.id DESC",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([project_id], |r| {
            let follow: String = r.get(10)?;
            let done = r.get::<_, i64>(11)? == 1;
            Ok(json!({
                "id": r.get::<_, i64>(0)?, "meeting_date": r.get::<_, String>(1)?, "title": r.get::<_, String>(2)?, "client_id": r.get::<_, Option<i64>>(3)?, "client": r.get::<_, String>(4)?,
                "project_id": r.get::<_, Option<i64>>(5)?, "project": r.get::<_, String>(6)?, "attendees": r.get::<_, String>(7)?, "notes": r.get::<_, String>(8)?,
                "decisions": r.get::<_, String>(9)?, "follow_up_date": follow, "follow_up_done": done,
                "follow_up_due": !done && !follow.is_empty() && follow <= today,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

// ---------------------------------------------------------------- attendance

/// Marks one person for one day, replacing an earlier mark. A day before they joined or after they left is refused.
pub fn attendance_mark(conn: &Connection, user_id: i64, staff_id: i64, day: &str, status: &str, note: &str) -> Result<(), String> {
    let day = date(day, true)?;
    let status = one_of(status, &ATTENDANCE, "attendance")?;
    let note = text(note, 200, "note", false)?;
    let (joined, left): (String, String) = conn
        .query_row("SELECT joined_date, left_date FROM staff WHERE id = ?1", [staff_id], |r| Ok((r.get(0)?, r.get(1)?)))
        .map_err(|_| "Choose a person from the roster.".to_string())?;
    if !joined.is_empty() && day < joined {
        return Err("That day is before the person joined.".into());
    }
    if !left.is_empty() && day > left {
        return Err("That day is after the person left.".into());
    }
    if day > today(conn) {
        return Err("Attendance cannot be marked for a day that has not come.".into());
    }
    conn.execute(
        "INSERT INTO attendance (staff_id, day, status, note, marked_by) VALUES (?1,?2,?3,?4,?5)
         ON CONFLICT(staff_id, day) DO UPDATE SET status = excluded.status, note = excluded.note, marked_by = excluded.marked_by",
        params![staff_id, day, status, note, user_id],
    )
    .map_err(|e| e.to_string())?;
    audit(conn, Some(user_id), "attendance_marked", &format!("{staff_id} {day} {status}"));
    Ok(())
}

/// One month of attendance for everyone on the roster: each person's days by status, and the days present counted with halves.
pub fn attendance_month(conn: &Connection, month: &str) -> Result<Vec<Value>, String> {
    let bad = || "Enter the month as YYYY-MM.".to_string();
    let (y, m) = month.split_once('-').ok_or_else(bad)?;
    if y.len() != 4 || m.len() != 2 || !y.chars().chain(m.chars()).all(|c| c.is_ascii_digit()) || !(1..=12).contains(&m.parse::<u32>().map_err(|_| bad())?) {
        return Err(bad());
    }
    let from = format!("{month}-01");
    let to = format!("{month}-31");
    let mut stmt = conn
        .prepare(
            "SELECT s.id, s.name, s.left_date,
                    COALESCE(SUM(a.status = 'present'), 0), COALESCE(SUM(a.status = 'half'), 0), COALESCE(SUM(a.status = 'absent'), 0),
                    COALESCE(SUM(a.status = 'leave'), 0), COALESCE(SUM(a.status = 'holiday'), 0)
             FROM staff s LEFT JOIN attendance a ON a.staff_id = s.id AND a.day >= ?1 AND a.day <= ?2
             WHERE (s.joined_date = '' OR s.joined_date <= ?2) AND (s.left_date = '' OR s.left_date >= ?1)
             GROUP BY s.id ORDER BY s.name COLLATE NOCASE",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map(params![from, to], |r| {
            let (present, half): (i64, i64) = (r.get(3)?, r.get(4)?);
            Ok(json!({
                "staff_id": r.get::<_, i64>(0)?, "name": r.get::<_, String>(1)?, "present": present, "half": half, "absent": r.get::<_, i64>(5)?,
                "leave": r.get::<_, i64>(6)?, "holiday": r.get::<_, i64>(7)?,
                // days worked in half-days, so a half day is not lost to rounding: 2 per full day, 1 per half
                "worked_half_days": present * 2 + half,
            }))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(rows)
}

/// The marks for one day, for the register screen.
pub fn attendance_day(conn: &Connection, day: &str) -> Result<Vec<Value>, String> {
    let day = date(day, true)?;
    let mut stmt = conn
        .prepare(
            "SELECT s.id, s.name, COALESCE(a.status, ''), COALESCE(a.note, '') FROM staff s LEFT JOIN attendance a ON a.staff_id = s.id AND a.day = ?1
             WHERE (s.joined_date = '' OR s.joined_date <= ?1) AND (s.left_date = '' OR s.left_date >= ?1) ORDER BY s.name COLLATE NOCASE",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([&day], |r| Ok(json!({ "staff_id": r.get::<_, i64>(0)?, "name": r.get::<_, String>(1)?, "status": r.get::<_, String>(2)?, "note": r.get::<_, String>(3)? })))
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
        c.execute("INSERT INTO clients (name) VALUES ('Acme')", []).unwrap();
        c.execute("INSERT INTO clients (name) VALUES ('Other')", []).unwrap();
        c.execute("INSERT INTO projects (client_id, name) VALUES (1,'Villa')", []).unwrap();
        c
    }

    fn proposal(basis: &str) -> ProposalInput {
        ProposalInput {
            id: None, client_id: 1, project_id: Some(1), title: "Villa design".into(), basis: basis.into(), cost_paise: 500_000_000_00, rate_bp: 600, area_milli: 2_500_000,
            area_rate_paise: 150_00, lump_paise: 7_500_000_00, gst_bp: 1800, issued_date: "2026-10-01".into(), valid_until: "2026-10-31".into(), notes: String::new(),
            estimate_artifact_json: String::new(), estimate_total_paise: 0,
        }
    }

    #[test]
    fn a_proposal_can_record_the_coreop_estimate_it_was_based_on() {
        let c = conn();
        let artifact = r#"{"v":1,"kind":"estimate","id":"last_estimate","revision":2,"checksum_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","project":{"v":1,"company_uuid":"u","project_id":1},"made_from":[]}"#;
        let id = proposal_save(&c, 1, &ProposalInput {
            estimate_artifact_json: artifact.into(),
            estimate_total_paise: 123_456_00,
            cost_paise: 123_456_00,
            ..proposal("percent")
        }).unwrap();
        let row = &proposal_list(&c, None).unwrap()[0];
        assert_eq!(row["id"], id);
        assert_eq!(row["estimate_linked"], true);
        assert_eq!(row["estimate_total_paise"], 123_456_00);
        assert!(row["estimate_artifact_json"].as_str().unwrap().contains("last_estimate"));
        assert!(proposal_save(&c, 1, &ProposalInput { estimate_artifact_json: "{".into(), ..proposal("percent") }).is_err());
        assert!(proposal_save(&c, 1, &ProposalInput { estimate_artifact_json: r#"{"kind":"drawing","id":"x"}"#.into(), ..proposal("percent") }).is_err());
    }

    #[test]
    fn each_basis_works_out_the_fee_from_its_own_figures() {
        // 6 percent of 5,00,00,000.00
        assert_eq!(fee_for(&proposal("percent")).unwrap(), 30_000_000_00);
        // 2,500.000 sq ft at 150.00 a sq ft
        assert_eq!(fee_for(&proposal("per_area")).unwrap(), 375_000_00);
        assert_eq!(fee_for(&proposal("lump_sum")).unwrap(), 7_500_000_00);
        assert!(fee_for(&ProposalInput { cost_paise: 0, ..proposal("percent") }).is_err());
        assert!(fee_for(&proposal("hourly")).is_err());
        assert_eq!(fee_for(&ProposalInput { cost_paise: 1, rate_bp: 5000, ..proposal("percent") }).unwrap(), 1, "half a paisa rounds up");
    }

    #[test]
    fn a_proposal_is_numbered_totalled_and_locked_once_the_client_decides() {
        let c = conn();
        let id = proposal_save(&c, 1, &proposal("percent")).unwrap();
        let row = &proposal_list(&c, Some(1)).unwrap()[0];
        assert_eq!((row["number"].as_str(), row["fee_paise"].as_i64(), row["gst_paise"].as_i64(), row["total_paise"].as_i64()), (Some("FP-0001"), Some(30_000_000_00), Some(5_400_000_00), Some(35_400_000_00)));
        proposal_save(&c, 1, &ProposalInput { id: Some(id), title: "Villa design and interiors".into(), ..proposal("percent") }).unwrap();
        assert!(proposal_set_status(&c, 1, id, "accepted").is_err(), "it must be sent first");
        proposal_set_status(&c, 1, id, "sent").unwrap();
        proposal_set_status(&c, 1, id, "accepted").unwrap();
        assert!(proposal_set_status(&c, 1, id, "draft").is_err());
        assert!(proposal_save(&c, 1, &ProposalInput { id: Some(id), ..proposal("percent") }).unwrap_err().contains("cannot change"));
        assert!(proposal_save(&c, 1, &ProposalInput { client_id: 2, ..proposal("percent") }).unwrap_err().contains("does not belong"), "the project must be the client's");
        assert!(proposal_save(&c, 1, &ProposalInput { valid_until: "2026-09-01".into(), ..proposal("percent") }).is_err());
        assert!(proposal_save(&c, 1, &ProposalInput { gst_bp: 5000, ..proposal("percent") }).is_err());
    }

    #[test]
    fn a_sent_proposal_lapses_after_its_date() {
        let c = conn();
        let id = proposal_save(&c, 1, &ProposalInput { valid_until: "2026-10-05".into(), ..proposal("lump_sum") }).unwrap();
        proposal_set_status(&c, 1, id, "sent").unwrap();
        assert_eq!(proposal_list(&c, None).unwrap()[0]["lapsed"], true);
    }

    #[test]
    fn meetings_record_decisions_and_flag_follow_ups_that_are_due() {
        let c = conn();
        let input = |follow: &str, done: bool| MeetingInput {
            id: None, meeting_date: "2026-01-10".into(), title: "Site visit".into(), client_id: Some(1), project_id: Some(1), attendees: "Rao, Meera".into(), notes: "Walked the plot".into(),
            decisions: "Shift the stair to the north".into(), follow_up_date: follow.into(), follow_up_done: done,
        };
        let id = meeting_save(&c, 1, &input("2026-01-20", false)).unwrap();
        assert_eq!(meeting_list(&c, Some(1)).unwrap()[0]["follow_up_due"], true);
        meeting_save(&c, 1, &MeetingInput { id: Some(id), ..input("2026-01-20", true) }).unwrap();
        assert_eq!(meeting_list(&c, None).unwrap()[0]["follow_up_due"], false);
        assert!(meeting_save(&c, 1, &input("2026-01-01", false)).is_err());
        assert!(meeting_save(&c, 1, &MeetingInput { project_id: Some(99), ..input("", false) }).is_err());
        assert!(meeting_save(&c, 1, &MeetingInput { title: " ".into(), ..input("", false) }).is_err());
    }

    #[test]
    fn attendance_is_one_mark_a_day_within_the_persons_time_here() {
        let c = conn();
        c.execute("INSERT INTO staff (name, joined_date, left_date) VALUES ('Meera', '2026-01-05', '')", []).unwrap();
        c.execute("INSERT INTO staff (name, joined_date, left_date) VALUES ('Ravi', '2025-01-01', '2026-01-20')", []).unwrap();
        attendance_mark(&c, 1, 1, "2026-01-06", "present", "").unwrap();
        attendance_mark(&c, 1, 1, "2026-01-07", "half", "left at noon").unwrap();
        attendance_mark(&c, 1, 1, "2026-01-08", "absent", "").unwrap();
        attendance_mark(&c, 1, 1, "2026-01-08", "leave", "approved leave").unwrap();
        assert!(attendance_mark(&c, 1, 1, "2026-01-02", "present", "").unwrap_err().contains("before the person joined"));
        assert!(attendance_mark(&c, 1, 2, "2026-01-21", "present", "").unwrap_err().contains("after the person left"));
        assert!(attendance_mark(&c, 1, 1, "2999-01-01", "present", "").is_err());
        assert!(attendance_mark(&c, 1, 1, "2026-01-09", "sleeping", "").is_err());
        assert!(attendance_mark(&c, 1, 9, "2026-01-09", "present", "").is_err());
        let month = attendance_month(&c, "2026-01").unwrap();
        assert_eq!(month.len(), 2);
        let meera = month.iter().find(|r| r["name"] == "Meera").unwrap();
        assert_eq!((meera["present"].as_i64(), meera["half"].as_i64(), meera["absent"].as_i64(), meera["leave"].as_i64(), meera["worked_half_days"].as_i64()), (Some(1), Some(1), Some(0), Some(1), Some(3)));
        assert!(attendance_month(&c, "2026-13").is_err() && attendance_month(&c, "Jan 2026").is_err());
        assert_eq!(attendance_month(&c, "2026-02").unwrap().len(), 1, "someone who left in January is not on February's register");
        let day = attendance_day(&c, "2026-01-06").unwrap();
        assert_eq!(day.iter().find(|r| r["name"] == "Meera").unwrap()["status"], "present");
        assert_eq!(day.iter().find(|r| r["name"] == "Ravi").unwrap()["status"], "");
    }
}
