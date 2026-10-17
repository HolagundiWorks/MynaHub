//! Application-owned results. These types are not Harper's.
use serde::Serialize;

#[derive(Serialize)]
pub struct GrammarIssue {
    pub start_utf16: u32,
    pub length_utf16: u32,
    pub original: String,
    pub message: String,
    pub rule_id: String,
    pub suggestions: Vec<String>,
}

#[derive(Serialize)]
pub struct GrammarResponse {
    pub api_version: u32,
    pub document_revision: u64,
    pub issues: Vec<GrammarIssue>,
}
