//! One Harper linter for the process. Checks are serialized. Nothing here writes to the company file.
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::Mutex;

use harper_core::linting::{LintGroup, Linter, Suggestion};
use harper_core::parsers::PlainEnglish;
use harper_core::spell::FstDictionary;
use harper_core::{Dialect, Document};

use super::config::{self, API_VERSION, MAX_CHARS};
use super::dto::{GrammarIssue, GrammarResponse};
use super::offsets::{char_index_to_utf16, slice_utf16};
use super::protected::editable_segments;
use super::CheckInput;

static LINTER: Mutex<Option<LintGroup>> = Mutex::new(None);

pub fn check(input: CheckInput) -> Result<GrammarResponse, String> {
    if input.api_version != API_VERSION {
        return Err("This language check is version 1.".into());
    }
    config::dialect(&input.dialect)?;
    if input.text.chars().count() > MAX_CHARS {
        return Err("The text is too long to check in one pass.".into());
    }
    if input.text.trim().is_empty() {
        return Ok(GrammarResponse { api_version: API_VERSION, document_revision: input.document_revision, issues: Vec::new() });
    }
    let mut issues = Vec::new();
    for (base, segment) in editable_segments(&input.text, &input.protected_ranges) {
        issues.extend(lint_segment(&segment, base)?);
    }
    issues.sort_by_key(|issue| issue.start_utf16);
    issues.truncate(40);
    Ok(GrammarResponse { api_version: API_VERSION, document_revision: input.document_revision, issues })
}

fn lint_segment(segment: &str, base_utf16: u32) -> Result<Vec<GrammarIssue>, String> {
    let document = Document::new_curated(segment, &PlainEnglish);
    let mut guard = LINTER.lock().map_err(|_| "The language check is unavailable.".to_string())?;
    if guard.is_none() {
        *guard = Some(LintGroup::new_curated(FstDictionary::curated(), Dialect::Indian));
    }
    let linter = guard.as_mut().unwrap();
    let lints = catch_unwind(AssertUnwindSafe(|| linter.lint(&document))).map_err(|_| "The language check could not read this text.".to_string())?;
    let mut issues = Vec::new();
    for lint in lints {
        let Some(start) = char_index_to_utf16(segment, lint.span.start) else { continue };
        let Some(end) = char_index_to_utf16(segment, lint.span.end) else { continue };
        if end < start {
            continue;
        }
        let length = end - start;
        let Some(original) = slice_utf16(segment, start, length) else { continue };
        if original.trim().is_empty() {
            continue;
        }
        let mut suggestions = Vec::new();
        for suggestion in &lint.suggestions {
            let text = replacement(suggestion, original);
            if text != original && !suggestions.contains(&text) {
                suggestions.push(text);
            }
            if suggestions.len() == 5 {
                break;
            }
        }
        issues.push(GrammarIssue {
            start_utf16: base_utf16.saturating_add(start),
            length_utf16: length,
            original: original.to_string(),
            message: lint.message,
            rule_id: lint.lint_kind.to_string_key(),
            suggestions,
        });
    }
    Ok(issues)
}

fn replacement(suggestion: &Suggestion, original: &str) -> String {
    match suggestion {
        Suggestion::ReplaceWith(chars) => chars.iter().collect(),
        Suggestion::Remove => String::new(),
        Suggestion::InsertAfter(chars) => {
            let mut out = original.to_string();
            out.extend(chars.iter().copied());
            out
        }
    }
}
