//! Checker settings. The office writes in Indian English.
use harper_core::Dialect;

pub const API_VERSION: u32 = 1;
pub const MAX_CHARS: usize = 20_000;

pub fn dialect(name: &str) -> Result<Dialect, String> {
    let name = name.trim();
    if name.is_empty() || name.eq_ignore_ascii_case("Indian") || name.eq_ignore_ascii_case("IN") {
        Ok(Dialect::Indian)
    } else {
        Err("The language check uses Indian English.".into())
    }
}
