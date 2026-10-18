//! Checks shared by every record: trimmed text with a length limit, business dates, and a fixed set of choices.

use rusqlite::Connection;

pub fn text(value: &str, max: usize, label: &str, needed: bool) -> Result<String, String> {
    let t = value.trim();
    if needed && t.is_empty() {
        return Err(format!("Enter the {label}."));
    }
    if t.chars().count() > max {
        return Err(format!("Keep the {label} to {max} characters."));
    }
    Ok(t.to_string())
}

/// A business date as YYYY-MM-DD between 1990 and 2100, or empty when `needed` is false.
pub fn date(value: &str, needed: bool) -> Result<String, String> {
    let t = value.trim();
    if t.is_empty() && !needed {
        return Ok(String::new());
    }
    let bad = || format!("'{t}' is not a valid date (use YYYY-MM-DD).");
    let parts: Vec<&str> = t.split('-').collect();
    if parts.len() != 3 || parts[0].len() != 4 || parts[1].len() != 2 || parts[2].len() != 2 || !parts.iter().all(|p| p.bytes().all(|b| b.is_ascii_digit())) {
        return Err(bad());
    }
    let (y, m, d): (i32, u32, u32) = (parts[0].parse().map_err(|_| bad())?, parts[1].parse().map_err(|_| bad())?, parts[2].parse().map_err(|_| bad())?);
    if !(1990..=2100).contains(&y) {
        return Err(bad());
    }
    let leap = (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;
    let dim = match m { 1 | 3 | 5 | 7 | 8 | 10 | 12 => 31, 4 | 6 | 9 | 11 => 30, 2 => if leap { 29 } else { 28 }, _ => return Err(bad()) };
    if d == 0 || d > dim {
        return Err(bad());
    }
    Ok(t.to_string())
}

pub fn one_of(value: &str, allowed: &[&str], label: &str) -> Result<String, String> {
    if allowed.contains(&value) {
        Ok(value.to_string())
    } else {
        Err(format!("Choose a valid {label}."))
    }
}

/// Today's date in the machine's local time, from SQLite.
pub fn today(conn: &Connection) -> String {
    conn.query_row("SELECT date('now','localtime')", [], |r| r.get(0)).unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dates_text_and_choices_are_checked() {
        assert_eq!(date("2026-02-28", true).unwrap(), "2026-02-28");
        assert!(date("2026-02-30", true).is_err());
        assert!(date("26-02-01", true).is_err());
        assert_eq!(date("", false).unwrap(), "");
        assert!(date("", true).is_err());
        assert!(text("  ", 5, "name", true).is_err());
        assert!(text("abcdef", 5, "name", false).is_err());
        assert!(one_of("a", &["a", "b"], "kind").is_ok());
        assert!(one_of("c", &["a", "b"], "kind").is_err());
    }
}
