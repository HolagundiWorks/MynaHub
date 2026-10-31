//! Automatic project codes from company settings (prefix, calendar-year sequence, custom pattern).

use crate::profile::CompanyProfile;
use rusqlite::{params, Connection};

const FORMATS: &[&str] = &[
    "prefix_year_number",
    "prefix_year_dash",
    "year_number",
    "prefix_number",
    "number_only",
    "custom",
];

pub fn normalize_format(raw: &str) -> Result<&'static str, String> {
    let key = raw.trim();
    if key.is_empty() {
        return Ok("prefix_year_number");
    }
    if FORMATS.contains(&key) {
        Ok(FORMATS.iter().copied().find(|f| *f == key).unwrap())
    } else {
        Err("Choose a supported project code format.".into())
    }
}

pub fn normalize_osp_on_create(raw: &str) -> Result<&'static str, String> {
    match raw.trim() {
        "" | "automatic" => Ok("automatic"),
        "choose" => Ok("choose"),
        _ => Err("Project file on create must be automatic or choose.".into()),
    }
}

fn next_sequence(conn: &Connection, year: &str) -> Result<i64, String> {
    conn.query_row(
        "SELECT COUNT(*)+1 FROM projects WHERE strftime('%Y', created_at)=?1",
        params![year],
        |r| r.get(0),
    )
    .map_err(|e| e.to_string())
}

fn calendar_year(conn: &Connection) -> Result<String, String> {
    conn.query_row("SELECT strftime('%Y','now')", [], |r| r.get(0))
        .map_err(|e| e.to_string())
}

fn render(profile: &CompanyProfile, year: &str, seq: i64) -> Result<String, String> {
    let prefix = profile.project_code_prefix.trim();
    let prefix = if prefix.is_empty() { "PROJ" } else { prefix };
    let no = format!("{seq:03}");
    let yy = &year[year.len().saturating_sub(2)..];
    let format = normalize_format(&profile.project_code_format)?;
    let code = match format {
        "prefix_year_number" => format!("{prefix}/{year}/{no}"),
        "prefix_year_dash" => format!("{prefix}-{year}-{no}"),
        "year_number" => format!("{year}/{no}"),
        "prefix_number" => format!("{prefix}-{no}"),
        "number_only" => no,
        "custom" => render_custom(&profile.project_code_custom, prefix, year, yy, &no)?,
        _ => unreachable!(),
    };
    Ok(code.trim().to_string())
}

fn render_custom(template: &str, prefix: &str, year: &str, yy: &str, no: &str) -> Result<String, String> {
    let t = template.trim();
    if t.is_empty() {
        return Err("Enter a custom project code pattern, or pick a standard format.".into());
    }
    let mut out = t.to_string();
    for (from, to) in [
        ("{prefix}", prefix),
        ("{PREFIX}", prefix),
        ("{year}", year),
        ("{YEAR}", year),
        ("{yy}", yy),
        ("{YY}", yy),
        ("{no}", no),
        ("{NO}", no),
        ("{number}", no),
        ("{NUMBER}", no),
    ] {
        out = out.replace(from, to);
    }
    if out.contains('{') {
        return Err("Custom pattern can use {prefix}, {year}, {yy}, and {no} only.".into());
    }
    Ok(out)
}

fn code_taken(conn: &Connection, code: &str) -> Result<bool, String> {
    let n: i64 = conn
        .query_row("SELECT COUNT(*) FROM projects WHERE code=?1 COLLATE NOCASE", params![code], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    Ok(n > 0)
}

/// Next unused project code from firm settings.
pub fn next_project_code(conn: &Connection, profile: &CompanyProfile) -> Result<String, String> {
    let year = calendar_year(conn)?;
    let mut seq = next_sequence(conn, &year)?;
    for _ in 0..500 {
        let code = render(profile, &year, seq)?;
        if code.is_empty() {
            return Err("The project code format produced an empty code.".into());
        }
        if !code_taken(conn, &code)? {
            return Ok(code);
        }
        seq += 1;
    }
    Err("Could not allocate a unique project code; adjust the format or prefix.".into())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::profile::CompanyProfile;
    use crate::schema;
    use rusqlite::Connection;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO company_file (id, format, company_uuid, display_name) VALUES (1, 'ADMS', 'u', 'T')", [])
            .unwrap();
        c
    }

    fn profile() -> CompanyProfile {
        CompanyProfile {
            name: "T".into(),
            address: String::new(),
            email: String::new(),
            phone: String::new(),
            pan: String::new(),
            gstin: String::new(),
            coa_registration: String::new(),
            bank_details: String::new(),
            gst_scheme: "none".into(),
            upi_id: String::new(),
            architect_name: String::new(),
            letterhead_style: "classic".into(),
            accent_hex: String::new(),
            project_code_prefix: "PROJ".into(),
            project_code_format: "prefix_year_number".into(),
            project_code_custom: String::new(),
            project_osp_on_create: "automatic".into(),
            project_code_locked: "on".into(),
        }
    }

    #[test]
    fn prefix_year_number_uses_slashes() {
        let c = conn();
        let p = profile();
        let year = calendar_year(&c).unwrap();
        let code = render(&p, &year, 1).unwrap();
        assert!(code.starts_with("PROJ/"), "{code}");
        assert!(code.contains(&format!("/{year}/")), "{code}");
        assert!(code.ends_with("/001"), "{code}");
    }

    #[test]
    fn custom_pattern_replaces_tokens() {
        let mut p = profile();
        p.project_code_format = "custom".into();
        p.project_code_custom = "{PREFIX}-{yy}-{no}".into();
        let code = render(&p, "2026", 7).unwrap();
        assert_eq!(code, "PROJ-26-007");
    }
}
