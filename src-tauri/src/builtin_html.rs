//! Built-in HTML/CSS templates for the Template Studio. Like the wording library these are practice templates, not official
//! Council of Architecture forms. They are added to each company file once (matched by key), never overwritten, and are
//! duplicated into a company-owned copy before they are customised.
use rusqlite::{params, Connection};

pub struct HtmlTemplate {
    pub key: &'static str,
    pub name: &'static str,
    pub family: &'static str,
    pub kind: &'static str,
    pub description: &'static str,
    pub html: &'static str,
    pub css: &'static str,
}

/// Shared print styles. Fonts come from the bundled IBM Plex families; sizes follow the ADMS print scale.
const BASE: &str = "@page { size: A4; margin: 18mm 16mm 20mm 16mm; }
.firm-name { font-family: 'IBM Plex Sans SemiBold'; font-size: 16pt; font-weight: 400; }
.registration, .meta { font-size: 8.5pt; color: #393939; }
.rule { border-top: 1pt solid #161616; margin-top: 6pt; margin-bottom: 10pt; }
.doc-ref { font-family: 'IBM Plex Mono'; font-size: 9pt; }
h1 { font-family: 'IBM Plex Sans SemiBold'; font-size: 22pt; font-weight: 400; margin-top: 10pt; margin-bottom: 4pt; }
h2 { font-family: 'IBM Plex Sans SemiBold'; font-size: 12pt; font-weight: 400; margin-top: 10pt; margin-bottom: 2pt; }
.keep { break-inside: avoid; }
.amount { text-align: right; }
table.schedule { width: 100%; border-collapse: collapse; margin-top: 6pt; }
table.schedule th, table.schedule td { border-bottom: 1pt solid #8d8d8d; padding: 4pt; font-size: 9pt; }
table.schedule th { font-family: 'IBM Plex Sans Medium'; font-weight: 400; text-align: left; background-color: #e0e0e0; }
table.schedule th.amount, table.schedule td.amount { text-align: right; }
table.sign { width: 100%; margin-top: 28pt; break-inside: avoid; }
table.sign td { width: 50%; vertical-align: top; padding-top: 30pt; font-size: 9pt; }";

pub const LIBRARY: &[HtmlTemplate] = &[
    HtmlTemplate {
        key: "html-letterhead-classic", name: "Letterhead (classic, HTML)", family: "administration", kind: "letter",
        description: "A letter on a classic letterhead: firm name, address and registration above a rule.",
        html: "<header class=\"document-header\">
  <div class=\"firm-name\">{{ company.name }}</div>
  <div class=\"meta\">{{ company.address }}</div>
  <div class=\"meta\">{{ company.phone }} {{#if company.email}}| {{ company.email }}{{/if}}</div>
  <div class=\"registration\">{{#if company.coa_registration_number}}COA Reg. No.: {{ company.coa_registration_number }}{{/if}} {{#if company.gstin}} | GSTIN: {{ company.gstin }}{{/if}}</div>
  <div class=\"rule\"></div>
</header>
<p><span class=\"doc-ref\">{{ document.number }}</span> &nbsp; {{ document.date | date }}</p>
{{#if addressee.name}}<p>To,<br/>{{ addressee.name }}<br/>{{ addressee.address }}</p>{{/if}}
{{#if document.subject}}<h2>Sub: {{ document.subject }}</h2>{{/if}}
{{#if document.reference}}<p class=\"meta\">Ref: {{ document.reference }}</p>{{/if}}
<main>{{ document.body }}</main>
<table class=\"sign\"><tr>
  <td>Yours sincerely,<br/>For {{ signatory.firm }}<br/>{{ signatory.name }}</td>
  <td></td>
</tr></table>",
        css: BASE,
    },
    HtmlTemplate {
        key: "html-letterhead-minimal", name: "Letterhead (minimal, HTML)", family: "administration", kind: "letter",
        description: "A letter on a minimal letterhead: a small name line and one line of details.",
        html: "<header class=\"document-header\">
  <div class=\"minimal-name\">{{ company.name | upper }}</div>
  <div class=\"meta\">{{ company.address }} {{#if company.phone}}| {{ company.phone }}{{/if}} {{#if company.email}}| {{ company.email }}{{/if}}</div>
</header>
<p><span class=\"doc-ref\">{{ document.number }}</span> &nbsp; {{ document.date | date }}</p>
{{#if addressee.name}}<p>To,<br/>{{ addressee.name }}<br/>{{ addressee.address }}</p>{{/if}}
{{#if document.subject}}<h2>Sub: {{ document.subject }}</h2>{{/if}}
<main>{{ document.body }}</main>
<table class=\"sign\"><tr>
  <td>Yours sincerely,<br/>For {{ signatory.firm }}<br/>{{ signatory.name }}</td>
  <td></td>
</tr></table>",
        css: "@page { size: A4; margin: 22mm 20mm 22mm 20mm; }
.minimal-name { font-family: 'IBM Plex Sans Medium'; font-size: 10.5pt; font-weight: 400; }
.meta { font-size: 8.5pt; color: #393939; }
.doc-ref { font-family: 'IBM Plex Mono'; font-size: 9pt; }
h2 { font-family: 'IBM Plex Sans SemiBold'; font-size: 12pt; font-weight: 400; margin-top: 10pt; margin-bottom: 2pt; }
table.sign { width: 100%; margin-top: 28pt; break-inside: avoid; }
table.sign td { width: 50%; vertical-align: top; font-size: 9pt; }",
    },
    HtmlTemplate {
        key: "html-consultancy-proposal", name: "Consultancy proposal (HTML)", family: "consultancy", kind: "letter",
        description: "A proposal with a title block, project details, the document text and a fee schedule that repeats the milestone rows.",
        html: "<header class=\"document-header\">
  <div class=\"firm-name\">{{ company.name }}</div>
  <div class=\"meta\">{{ company.address }}</div>
  <div class=\"registration\">{{#if company.coa_registration_number}}COA Reg. No.: {{ company.coa_registration_number }}{{/if}}</div>
  <div class=\"rule\"></div>
</header>
<h1>{{ document.title | default \"Consultancy proposal\" }}</h1>
<p class=\"meta\"><span class=\"doc-ref\">{{ document.number }}</span> &nbsp; {{ document.date | date }} {{#if document.reference}}| Ref: {{ document.reference }}{{/if}}</p>
<h2>Prepared for</h2>
<p>{{ client.name }}<br/>{{ client.address }}</p>
<h2>Project</h2>
<p>{{ project.name }}<br/>{{ project.site_address }}</p>
<main>{{ document.body }}</main>
{{#if fee_items}}
<h2>Professional fee: {{ fee.total }}</h2>
<table class=\"schedule\">
  <thead><tr><th>#</th><th>Milestone</th><th class=\"amount\">Share</th><th class=\"amount\">Amount</th></tr></thead>
  <tbody>
  {{#each fee_items}}
  <tr><td>{{ no }}</td><td>{{ description }}</td><td class=\"amount\">{{ share }}</td><td class=\"amount\">{{ formatted_amount }}</td></tr>
  {{/each}}
  </tbody>
</table>
<p class=\"meta\">Fees are exclusive of applicable taxes ({{ tax.scheme }}).</p>
{{/if}}
{{#if payment.bank_details}}<h2>Payment details</h2><p>{{ payment.bank_details | multiline }}</p>{{/if}}
<table class=\"sign\"><tr>
  <td>For {{ signatory.firm }}<br/>{{ signatory.name }}</td>
  <td>Accepted for {{ client.name }}<br/>Signature and date</td>
</tr></table>",
        css: BASE,
    },
    HtmlTemplate {
        key: "html-appointment-letter", name: "Appointment letter (HTML)", family: "consultancy", kind: "letter",
        description: "Confirms the appointment of the practice, with the fee and a countersignature block.",
        html: "<header class=\"document-header\">
  <div class=\"firm-name\">{{ company.name }}</div>
  <div class=\"meta\">{{ company.address }}</div>
  <div class=\"rule\"></div>
</header>
<p><span class=\"doc-ref\">{{ document.number }}</span> &nbsp; {{ document.date | date }}</p>
<p>To,<br/>{{ addressee.name }}<br/>{{ addressee.address }}</p>
<h2>Sub: Appointment as architects for {{ project.name }}</h2>
<main>{{ document.body }}</main>
{{#if fee.total}}<p>The professional fee for the services is {{ fee.total }}, exclusive of applicable taxes.</p>{{/if}}
<table class=\"sign\"><tr>
  <td>Yours sincerely,<br/>For {{ signatory.firm }}<br/>{{ signatory.name }}</td>
  <td>Accepted: {{ client.name }}<br/>Signature and date</td>
</tr></table>",
        css: BASE,
    },
    HtmlTemplate {
        key: "html-meeting-minutes", name: "Meeting minutes (HTML)", family: "consultancy", kind: "letter",
        description: "Minutes record: project block, the minutes text and a prepared-by line.",
        html: "<header class=\"document-header\">
  <div class=\"firm-name\">{{ company.name }}</div>
  <div class=\"rule\"></div>
</header>
<h1>{{ document.title | default \"Meeting minutes\" }}</h1>
<p class=\"meta\"><span class=\"doc-ref\">{{ document.number }}</span> &nbsp; {{ document.date | date }} {{#if document.reference}}| Ref: {{ document.reference }}{{/if}}</p>
<table class=\"schedule\">
  <tr><td>Project</td><td>{{ project.name }}</td></tr>
  <tr><td>Client</td><td>{{ client.name }}</td></tr>
  <tr><td>Site</td><td>{{ project.site_address }}</td></tr>
</table>
<main>{{ document.body }}</main>
<p class=\"keep\">Prepared by {{ signatory.name | default \"the architect\" }}, {{ signatory.firm }}</p>",
        css: BASE,
    },
    HtmlTemplate {
        key: "html-drawing-transmittal", name: "Drawing transmittal (HTML)", family: "consultancy", kind: "letter",
        description: "Transmittal of drawings and documents: who it is sent to, what is sent (written in the text) and the purpose.",
        html: "<header class=\"document-header\">
  <div class=\"firm-name\">{{ company.name }}</div>
  <div class=\"meta\">{{ company.address }}</div>
  <div class=\"rule\"></div>
</header>
<h1>Drawing and document transmittal</h1>
<p class=\"meta\"><span class=\"doc-ref\">{{ document.number }}</span> &nbsp; {{ document.date | date }}</p>
<table class=\"schedule\">
  <tr><td>Project</td><td>{{ project.name }}</td></tr>
  <tr><td>Sent to</td><td>{{ addressee.name }}<br/>{{ addressee.address }}</td></tr>
  <tr><td>Reference</td><td>{{ document.reference }}</td></tr>
</table>
<h2>Items sent</h2>
<main>{{ document.body }}</main>
<p class=\"meta\">Please acknowledge receipt.</p>",
        css: BASE,
    },
    HtmlTemplate {
        key: "html-fee-schedule", name: "Fee schedule annexure (HTML)", family: "consultancy", kind: "agreement",
        description: "Annexure that lists the fee milestones. Use with an agreement so the schedule matches the agreement exactly.",
        html: "<header class=\"document-header\">
  <div class=\"firm-name\">{{ company.name }}</div>
  <div class=\"rule\"></div>
</header>
<h1>Fee and payment schedule</h1>
<p>Project: {{ project.name }}, {{ project.site_address }}<br/>Client: {{ client.name }}</p>
<h2>Total professional fee: {{ fee.total }}</h2>
<table class=\"schedule\">
  <thead><tr><th>#</th><th>Milestone</th><th class=\"amount\">Share</th><th class=\"amount\">Amount</th></tr></thead>
  <tbody>
  {{#each fee_items}}
  <tr><td>{{ no }}</td><td>{{ description }}</td><td class=\"amount\">{{ share }}</td><td class=\"amount\">{{ formatted_amount }}</td></tr>
  {{/each}}
  </tbody>
</table>
<p class=\"meta\">Taxes are charged in addition as applicable ({{ tax.scheme }}). {{ document.body }}</p>",
        css: BASE,
    },
];


/// Adds any HTML built-in that is missing, matched by key. Existing rows are never changed.
pub fn seed(conn: &Connection) -> Result<usize, String> {
    let mut added = 0;
    for t in LIBRARY {
        let exists: i64 = conn.query_row("SELECT COUNT(*) FROM templates WHERE template_key = ?1", params![t.key], |r| r.get(0)).map_err(|e| e.to_string())?;
        if exists > 0 {
            continue;
        }
        let taken: i64 = conn.query_row("SELECT COUNT(*) FROM templates WHERE name = ?1", params![t.name], |r| r.get(0)).map_err(|e| e.to_string())?;
        let name = if taken > 0 { format!("{} (ADMS built-in)", t.name) } else { t.name.to_string() };
        conn.execute(
            "INSERT INTO templates (kind, name, template_key, family, layout, description, built_in, format) VALUES (?1,?2,?3,?4,'letter',?5,1,'html')",
            params![t.kind, name, t.key, t.family, t.description],
        )
        .map_err(|e| e.to_string())?;
        let id = conn.last_insert_rowid();
        conn.execute(
            "INSERT INTO template_versions (template_id, version, body, html_source, css_source, change_summary) VALUES (?1, 1, '', ?2, ?3, 'Built-in original')",
            params![id, t.html, t.css],
        )
        .map_err(|e| e.to_string())?;
        added += 1;
    }
    Ok(added)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::template_engine::{full_document, has_errors, render_fragment, sample_data, split_page_rule, validate};

    #[test]
    fn every_built_in_validates_with_no_errors_and_renders_to_pdf_with_both_sample_profiles() {
        for t in LIBRARY {
            let d = validate(t.html, t.css);
            assert!(!has_errors(&d), "{}: {:?}", t.key, d);
            for profile in ["short", "long"] {
                let data = sample_data(profile, None);
                let fragment = render_fragment(t.html, &data).unwrap();
                assert!(!fragment.contains("{{") && !fragment.contains("undefined"), "{}: {fragment}", t.key);
                let mut w = Vec::new();
                let (setup, css) = split_page_rule(t.css, &mut w);
                let html = full_document(&fragment, &css);
                let r = crate::pdf::render_html_with_page(&html, &crate::pdf::bundled_fonts(), &setup).unwrap();
                assert!(r.bytes.starts_with(b"%PDF") && r.pages >= 1, "{} {profile}", t.key);
                if let Ok(dir) = std::env::var("AO_PREVIEW_DIR") {
                    std::fs::write(std::path::Path::new(&dir).join(format!("studio-{}-{profile}.pdf", t.key.trim_start_matches("html-"))), &r.bytes).unwrap();
                }
            }
        }
    }

    #[test]
    fn long_data_paginates_and_short_data_fits_one_page() {
        let t = LIBRARY.iter().find(|t| t.key == "html-consultancy-proposal").unwrap();
        let pages = |profile: &str| {
            let mut w = Vec::new();
            let (setup, css) = split_page_rule(t.css, &mut w);
            let html = full_document(&render_fragment(t.html, &sample_data(profile, None)).unwrap(), &css);
            crate::pdf::render_html_with_page(&html, &crate::pdf::bundled_fonts(), &setup).unwrap().pages
        };
        assert_eq!(pages("short"), 1);
        assert!(pages("long") >= 3, "the long profile should run over several pages, got {}", pages("long"));
    }

    #[test]
    fn built_in_keys_and_names_are_unique() {
        let mut keys = std::collections::HashSet::new();
        let mut names = std::collections::HashSet::new();
        for t in LIBRARY {
            assert!(keys.insert(t.key) && names.insert(t.name));
        }
    }
}
