//! The company's own details: the name, address and registrations printed on every document, shared by every app.

use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
pub struct CompanyProfile {
    pub name: String,
    pub address: String,
    pub email: String,
    pub phone: String,
    pub pan: String,
    pub gstin: String,
    pub coa_registration: String,
    #[serde(default)]
    pub bank_details: String,
    #[serde(default)]
    pub gst_scheme: String,
    #[serde(default)]
    pub upi_id: String,
    #[serde(default)]
    pub architect_name: String,
    /// `classic` or `minimal`: how the firm's details are set at the top of issued documents.
    #[serde(default)]
    pub letterhead_style: String,
    /// `#RRGGBB`, or blank for near-black ink on new documents.
    #[serde(default)]
    pub accent_hex: String,
    /// Prefix for automatic project codes (e.g. PROJ).
    #[serde(default = "default_project_code_prefix")]
    pub project_code_prefix: String,
    /// One of the `project_codes::FORMATS` keys, or `custom`.
    #[serde(default = "default_project_code_format")]
    pub project_code_format: String,
    /// When format is `custom`: `{prefix}`, `{year}`, `{yy}`, `{no}`.
    #[serde(default)]
    pub project_code_custom: String,
    /// `automatic` or `choose` (prompt for `.osp` when creating a project).
    #[serde(default = "default_project_osp_on_create")]
    pub project_osp_on_create: String,
    /// When `on`, project codes are assigned from the format and cannot be edited in Projects.
    #[serde(default = "default_project_code_locked")]
    pub project_code_locked: String,
}

fn default_project_code_locked() -> String {
    "on".into()
}

fn default_project_code_prefix() -> String {
    "PROJ".into()
}

fn default_project_code_format() -> String {
    "prefix_year_number".into()
}

fn default_project_osp_on_create() -> String {
    "automatic".into()
}

pub fn normalize_project_code_locked(raw: &str) -> Result<&'static str, String> {
    match raw.trim() {
        "" | "on" => Ok("on"),
        "off" => Ok("off"),
        _ => Err("Lock project codes must be on or off.".into()),
    }
}

/// `#RRGGBB` in upper case, or an empty string for "none". Anything else is refused.
pub fn normalize_accent(raw: &str) -> Result<String, String> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return Ok(String::new());
    }
    let body = trimmed.strip_prefix('#').unwrap_or(trimmed);
    if body.len() == 6 && body.bytes().all(|b| b.is_ascii_hexdigit()) {
        Ok(format!("#{}", body.to_ascii_uppercase()))
    } else {
        Err("Enter the document accent as six hex digits, such as #3E5240, or leave it blank for black.".into())
    }
}

pub fn read(conn: &Connection) -> Result<CompanyProfile, String> {
    conn.query_row(
        "SELECT name, address, email, phone, pan, gstin, coa_registration, bank_details, gst_scheme, upi_id, architect_name, letterhead_style, accent_hex, project_code_prefix, project_code_format, project_code_custom, project_osp_on_create, project_code_locked FROM company_profile WHERE id = 1",
        [],
        |r| {
            Ok(CompanyProfile {
                name: r.get(0)?,
                address: r.get(1)?,
                email: r.get(2)?,
                phone: r.get(3)?,
                pan: r.get(4)?,
                gstin: r.get(5)?,
                coa_registration: r.get(6)?,
                bank_details: r.get(7)?,
                gst_scheme: r.get(8)?,
                upi_id: r.get(9)?,
                architect_name: r.get(10)?,
                letterhead_style: r.get(11)?,
                accent_hex: r.get(12)?,
                project_code_prefix: r.get(13)?,
                project_code_format: r.get(14)?,
                project_code_custom: r.get(15)?,
                project_osp_on_create: r.get(16)?,
                project_code_locked: r.get(17)?,
            })
        },
    )
    .map_err(|e| e.to_string())
}

pub fn save(conn: &Connection, user_id: i64, profile: &CompanyProfile) -> Result<(), String> {
    let accent = normalize_accent(&profile.accent_hex)?;
    let code_format = crate::project_codes::normalize_format(&profile.project_code_format)?;
    let osp_on_create = crate::project_codes::normalize_osp_on_create(&profile.project_osp_on_create)?;
    let code_locked = normalize_project_code_locked(&profile.project_code_locked)?;
    let prefix = profile.project_code_prefix.trim();
    if prefix.len() > 24 {
        return Err("Keep the project code prefix under 24 characters.".into());
    }
    if code_format == "custom" && profile.project_code_custom.trim().is_empty() {
        return Err("Enter a custom project code pattern, or pick a standard format.".into());
    }
    if profile.name.trim().is_empty() {
        return Err("Enter the company name.".into());
    }
    if !["none", "composition", "regular"].contains(&profile.gst_scheme.as_str()) {
        return Err("Choose the GST scheme: none, composition or regular.".into());
    }
    conn.execute(
        "UPDATE company_profile SET name=?1, address=?2, email=?3, phone=?4, pan=?5, gstin=?6, coa_registration=?7, bank_details=?8, gst_scheme=?9, upi_id=?10, architect_name=?11, letterhead_style=?12, accent_hex=?13, project_code_prefix=?14, project_code_format=?15, project_code_custom=?16, project_osp_on_create=?17, project_code_locked=?18, updated_at=datetime('now') WHERE id=1",
        params![
            profile.name.trim(),
            profile.address,
            profile.email,
            profile.phone,
            profile.pan,
            profile.gstin,
            profile.coa_registration,
            profile.bank_details,
            profile.gst_scheme,
            profile.upi_id.trim(),
            profile.architect_name.trim(),
            if profile.letterhead_style == "minimal" { "minimal" } else { "classic" },
            accent,
            if prefix.is_empty() { "PROJ" } else { prefix },
            code_format,
            profile.project_code_custom.trim(),
            osp_on_create,
            code_locked,
        ],
    )
    .map_err(|e| e.to_string())?;
    // The company file's own display name follows the company name, so every app and the file picker show the same one.
    conn.execute("UPDATE company_file SET display_name = ?1 WHERE id = 1", params![profile.name.trim()]).map_err(|e| e.to_string())?;
    crate::schema::audit(conn, Some(user_id), "company_profile_saved", "");
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn conn() -> Connection {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::schema::migrate(&mut c).unwrap();
        c.execute("INSERT INTO company_file (id, format, company_uuid, display_name) VALUES (1, 'ADMS', 'u', 'Old')", []).unwrap();
        c
    }

    fn sample() -> CompanyProfile {
        CompanyProfile {
            name: "Studio One".into(),
            address: "Hospet".into(),
            email: "a@b.in".into(),
            phone: "1".into(),
            pan: "ABCDE1234F".into(),
            gstin: "".into(),
            coa_registration: "CA/1".into(),
            bank_details: "".into(),
            gst_scheme: "regular".into(),
            upi_id: " me@upi ".into(),
            architect_name: "Meera ".into(),
            letterhead_style: "weird".into(),
            accent_hex: "3e5240".into(),
            project_code_prefix: "PROJ".into(),
            project_code_format: "prefix_year_number".into(),
            project_code_custom: String::new(),
            project_osp_on_create: "automatic".into(),
            project_code_locked: "on".into(),
        }
    }

    #[test]
    fn the_company_details_save_normalised_and_read_back() {
        let c = conn();
        save(&c, 1, &sample()).unwrap();
        let got = read(&c).unwrap();
        assert_eq!((got.name.as_str(), got.accent_hex.as_str(), got.letterhead_style.as_str(), got.upi_id.as_str(), got.architect_name.as_str()), ("Studio One", "#3E5240", "classic", "me@upi", "Meera"));
        let display: String = c.query_row("SELECT display_name FROM company_file WHERE id = 1", [], |r| r.get(0)).unwrap();
        assert_eq!(display, "Studio One");
    }

    #[test]
    fn a_blank_name_or_a_bad_accent_is_refused_and_nothing_changes() {
        let c = conn();
        let before = read(&c).unwrap();
        let mut p = sample();
        p.name = "  ".into();
        assert!(save(&c, 1, &p).is_err());
        let mut p = sample();
        p.accent_hex = "red".into();
        assert!(save(&c, 1, &p).is_err());
        assert_eq!(read(&c).unwrap(), before);
    }

}
