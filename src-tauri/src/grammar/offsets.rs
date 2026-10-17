//! Harper spans count Unicode scalar values. WinUI text positions count UTF-16 code units.
use serde::Deserialize;

#[derive(Deserialize, Clone, Copy)]
pub struct Utf16Range {
    pub start_utf16: u32,
    pub length_utf16: u32,
}

pub fn utf16_len(text: &str) -> u32 {
    text.chars().map(|ch| ch.len_utf16() as u32).sum()
}

pub fn char_index_to_utf16(text: &str, char_index: usize) -> Option<u32> {
    let mut seen = 0usize;
    let mut utf16 = 0u32;
    for ch in text.chars() {
        if seen == char_index {
            return Some(utf16);
        }
        seen += 1;
        utf16 = utf16.checked_add(ch.len_utf16() as u32)?;
    }
    if seen == char_index { Some(utf16) } else { None }
}

pub fn slice_utf16(text: &str, start: u32, length: u32) -> Option<&str> {
    let end = start.checked_add(length)?;
    let mut utf16 = 0u32;
    let mut byte_start = None;
    let mut byte_end = None;
    for (byte, ch) in text.char_indices() {
        if utf16 == start {
            byte_start = Some(byte);
        }
        if utf16 == end {
            byte_end = Some(byte);
            break;
        }
        utf16 = utf16.checked_add(ch.len_utf16() as u32)?;
    }
    if byte_end.is_none() && utf16 == end {
        byte_end = Some(text.len());
    }
    if byte_start.is_none() && start == 0 && end == 0 {
        return Some("");
    }
    let (Some(from), Some(to)) = (byte_start, byte_end) else { return None };
    text.get(from..to)
}
