//! Ranges the checker must not rewrite: caller-supplied fields, and template tokens in a draft.
use super::offsets::{Utf16Range, slice_utf16, utf16_len};

pub fn editable_segments(text: &str, extra: &[Utf16Range]) -> Vec<(u32, String)> {
    let total = utf16_len(text);
    let mut blocked = token_ranges(text);
    for range in extra {
        let end = range.start_utf16.saturating_add(range.length_utf16).min(total);
        if range.length_utf16 > 0 && range.start_utf16 < end {
            blocked.push((range.start_utf16, end));
        }
    }
    blocked.sort_by_key(|range| range.0);
    let mut merged = Vec::<(u32, u32)>::new();
    for (start, end) in blocked {
        if let Some(last) = merged.last_mut() {
            if start <= last.1 {
                last.1 = last.1.max(end);
                continue;
            }
        }
        merged.push((start, end));
    }
    let mut segments = Vec::new();
    let mut cursor = 0u32;
    for (start, end) in merged {
        if cursor < start {
            if let Some(slice) = slice_utf16(text, cursor, start - cursor) {
                if !slice.trim().is_empty() {
                    segments.push((cursor, slice.to_string()));
                }
            }
        }
        cursor = cursor.max(end);
    }
    if cursor < total {
        if let Some(slice) = slice_utf16(text, cursor, total - cursor) {
            if !slice.trim().is_empty() {
                segments.push((cursor, slice.to_string()));
            }
        }
    }
    segments
}

fn token_ranges(text: &str) -> Vec<(u32, u32)> {
    let mut ranges = Vec::new();
    for marker in ["[[", "{{"] {
        let close = if marker == "[[" { "]]" } else { "}}" };
        let mut rest = text;
        let mut base = 0usize;
        while let Some(start) = rest.find(marker) {
            let after = start + marker.len();
            if let Some(end) = rest[after..].find(close) {
                let byte_end = base + after + end + close.len();
                let from = utf16_len(&text[..base + start]);
                let to = utf16_len(&text[..byte_end]);
                ranges.push((from, to));
                base = byte_end;
                rest = &text[base..];
            } else {
                break;
            }
        }
    }
    ranges
}
