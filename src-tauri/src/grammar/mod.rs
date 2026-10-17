//! Local language check. Harper reads the text it is given and returns suggestions.
//! It does not open the company file, and it does not change an issued document.
mod config;
mod dto;
mod engine;
mod offsets;
mod protected;

use crate::auth::Permission;
use crate::commands::{HostState, require};
use serde::Deserialize;

pub use dto::GrammarResponse;
pub use offsets::Utf16Range;

#[derive(Deserialize)]
pub struct CheckInput {
    #[serde(default = "api_one")]
    pub api_version: u32,
    #[serde(default)]
    pub document_revision: u64,
    #[serde(default)]
    pub dialect: String,
    #[serde(default)]
    pub text: String,
    #[serde(default)]
    pub protected_ranges: Vec<Utf16Range>,
}

fn api_one() -> u32 {
    config::API_VERSION
}

pub fn grammar_check(state: HostState<'_>, input: CheckInput) -> Result<GrammarResponse, String> {
    require(&state, Permission::View)?;
    engine::check(input)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_known_agreement_error_is_a_suggestion() {
        let response = engine::check(CheckInput {
            api_version: 1,
            document_revision: 7,
            dialect: "Indian".into(),
            text: "This is an test.".into(),
            protected_ranges: Vec::new(),
        })
        .unwrap();
        assert_eq!(response.api_version, 1);
        assert_eq!(response.document_revision, 7);
        assert!(response.issues.iter().any(|issue| issue.suggestions.iter().any(|item| item == "a")));
    }

    #[test]
    fn indian_spelling_is_left_alone() {
        let response = engine::check(CheckInput {
            api_version: 1,
            document_revision: 1,
            dialect: "Indian".into(),
            text: "The colour of the wall is white.".into(),
            protected_ranges: Vec::new(),
        })
        .unwrap();
        assert!(response.issues.iter().all(|issue| issue.suggestions.iter().all(|item| item != "color")));
    }

    #[test]
    fn a_protected_span_is_not_rewritten() {
        let text = "We has received your payment of ₹1180.";
        let start = offsets::utf16_len("We has received your payment of ");
        let response = engine::check(CheckInput {
            api_version: 1,
            document_revision: 3,
            dialect: String::new(),
            text: text.into(),
            protected_ranges: vec![Utf16Range { start_utf16: start, length_utf16: offsets::utf16_len("₹1180.") }],
        })
        .unwrap();
        assert!(response.issues.iter().all(|issue| issue.start_utf16 + issue.length_utf16 <= start || issue.start_utf16 >= start + offsets::utf16_len("₹1180.")));
        assert!(response.issues.iter().any(|issue| issue.suggestions.iter().any(|item| item.eq_ignore_ascii_case("have"))));
    }

    #[test]
    fn template_tokens_and_an_unknown_dialect_stay_out_of_the_suggestion() {
        let kept = engine::check(CheckInput {
            api_version: 1,
            document_revision: 1,
            dialect: "Indian".into(),
            text: "Please see [[the fee]] and {{project.name}}.".into(),
            protected_ranges: Vec::new(),
        })
        .unwrap();
        assert!(kept.issues.iter().all(|issue| !issue.original.contains("[[") && !issue.original.contains("{{")));
        let rejected = engine::check(CheckInput {
            api_version: 1,
            document_revision: 1,
            dialect: "American".into(),
            text: "Hello.".into(),
            protected_ranges: Vec::new(),
        });
        let message = match rejected { Ok(_) => String::new(), Err(message) => message };
        assert!(message.contains("Indian English"));
    }

    #[test]
    fn an_emoji_span_uses_utf16_positions() {
        let text = "A 😀 B";
        let start = offsets::char_index_to_utf16(text, 2).unwrap();
        let end = offsets::char_index_to_utf16(text, 3).unwrap();
        assert_eq!(start, 2);
        assert_eq!(end - start, 2);
    }
}
