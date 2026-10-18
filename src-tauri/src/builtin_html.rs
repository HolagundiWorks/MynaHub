//! Built-in Word templates. Like the wording library these are practice templates, not official
//! Council of Architecture forms. A company file receives each one by key. When this library changes, the built-in
//! original in an existing company is published again. A firm's own copy, and every document already issued, stay as they are.
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
const BASE: &str = "@page { size: A4; margin: 16mm 16mm 22mm 16mm; }
.firm-name { font-family: 'IBM Plex Sans SemiBold'; font-size: 16pt; font-weight: 400; color: #1a1a1a; }
.registration, .meta { font-size: 8.5pt; color: #5c5c5c; }
.rule { border-top: 0.6pt solid #1a1a1a; margin-top: 6pt; margin-bottom: 10pt; }
.doc-ref { font-family: 'IBM Plex Mono'; font-size: 9pt; color: #1a1a1a; }
h1 { font-family: 'IBM Plex Sans SemiBold'; font-size: 22pt; font-weight: 400; margin-top: 10pt; margin-bottom: 4pt; color: #1a1a1a; }
h2 { font-family: 'IBM Plex Sans SemiBold'; font-size: 12pt; font-weight: 400; margin-top: 10pt; margin-bottom: 2pt; color: #1a1a1a; }
.keep { break-inside: avoid; }
.amount { text-align: right; }
table.schedule { width: 178mm; table-layout: fixed; border-collapse: collapse; margin-top: 6pt; }
table.schedule thead { display: table-header-group; }
table.schedule tr { break-inside: avoid; }
table.schedule th, table.schedule td { padding: 5pt 4pt; font-size: 9pt; color: #1a1a1a; }
table.schedule th { font-family: 'IBM Plex Sans Medium'; font-weight: 400; text-align: left; background-color: #e8e8e8; }
table.schedule th.amount, table.schedule td.amount { text-align: right; }
table.schedule th.c-no { width: 10mm; }
table.schedule td.lab { width: 32mm; color: #5c5c5c; }
table.schedule th.c-share { width: 18mm; }
table.schedule th.c-amt { width: 32mm; }
table.schedule td { word-wrap: break-word; overflow-wrap: break-word; }
table.sign { width: 178mm; table-layout: fixed; margin-top: 28pt; break-inside: avoid; }
table.sign td { width: 89mm; vertical-align: top; padding-top: 30pt; font-size: 9pt; word-wrap: break-word; overflow-wrap: break-word; }
footer { margin-top: 18pt; border-top: 0.6pt solid #1a1a1a; padding-top: 6pt; }";

/// A practice proposal for one service in the Council of Architecture Conditions of Engagement.
/// These are not official Council forms.
macro_rules! service_letter {
    ($key:literal, $name:literal, $service:literal) => {
        HtmlTemplate {
            key: $key,
            name: $name,
            family: "consultancy",
            kind: "letter",
            description: "Practice proposal for a Council of Architecture service. Not an official Council form.",
            html: concat!(
                "<header class=\"document-header\"><div class=\"firm-name\">{{ company.name }}</div>",
                "<div class=\"meta\">{{ company.address }}</div>",
                "<div class=\"registration\">{{#if company.coa_registration_number}}COA Reg. No.: {{ company.coa_registration_number }}{{/if}}</div>",
                "<div class=\"rule\"></div></header>",
                "<p><span class=\"doc-ref\">{{ document.number }}</span> &nbsp; {{ document.date | date }}</p>",
                "{{#if addressee.name}}<p>To,<br/>{{ addressee.name }}<br/>{{ addressee.address }}</p>{{/if}}",
                "<h1>Proposal for ", $service, "</h1>",
                "<p>This proposal is for ", $service, ", following the Council of Architecture Conditions of Engagement and Scale of Charges for that service.</p>",
                "<p>{{ project.name }}<br/>{{ project.site_address }}</p>",
                "<p>Client: {{ client.name }}</p>",
                "<h2>Services</h2><main>{{ document.body }}</main>",
                "<table class=\"sign\"><tr><td>Yours sincerely,<br/>For {{ signatory.firm }}<br/>{{ signatory.name }}</td><td></td></tr></table>",
                "<footer><p class=\"meta\">{{ company.name }}{{#if company.phone}} · {{ company.phone }}{{/if}}{{#if company.email}} · {{ company.email }}{{/if}}</p></footer>"
            ),
            css: BASE,
        }
    };
}

pub const LIBRARY: &[HtmlTemplate] = &[
    HtmlTemplate {
        key: "html-letterhead-classic", name: "Letterhead (classic)", family: "administration", kind: "letter",
        description: "A letter with the mark on the left, the addressee beside it, and the date on the right.",
        html: "<table class=\"head\"><tr>
  <td class=\"brand\"><div class=\"firm-name\">{{ company.name }}</div><div class=\"meta\">{{ company.address }}</div></td>
  <td class=\"who\">{{#if addressee.name}}<div class=\"meta\">To</div>{{ addressee.name }}<br/>{{ addressee.address }}{{/if}}</td>
  <td class=\"when\"><div class=\"bar\"></div>{{ document.date | date }}<br/><span class=\"doc-ref\">{{ document.number }}</span></td>
</tr></table>
{{#if document.subject}}<h2>{{ document.subject }}</h2>{{/if}}
{{#if document.reference}}<p class=\"meta\">Ref: {{ document.reference }}</p>{{/if}}
<main>{{ document.body }}</main>
<p class=\"sign-name\">{{#if signatory.name}}{{ signatory.name }}{{else}}{{ signatory.firm }}{{/if}}</p>
{{#if signatory.name}}<p class=\"meta\">{{ signatory.firm }}</p>{{/if}}
<footer><p class=\"meta\">{{ company.name }}{{#if company.address}} · {{ company.address }}{{/if}}{{#if company.phone}} · {{ company.phone }}{{/if}}{{#if company.email}} · {{ company.email }}{{/if}}</p></footer>",
        css: "@page { size: A4; margin: 16mm 16mm 22mm 16mm; }
footer { margin-top: 18pt; border-top: 0.6pt solid #1a1a1a; padding-top: 6pt; }
.firm-name { font-family: 'IBM Plex Sans SemiBold'; font-size: 12pt; font-weight: 400; }
.meta { font-family: 'IBM Plex Sans'; font-size: 8.5pt; color: #5c5c5c; }
.doc-ref { font-family: 'IBM Plex Mono'; font-size: 9pt; }
h2 { font-family: 'IBM Plex Sans SemiBold'; font-size: 12pt; font-weight: 400; margin-top: 12pt; margin-bottom: 2pt; }
table.head { width: 178mm; table-layout: fixed; border-collapse: collapse; }
table.head td { word-wrap: break-word; overflow-wrap: break-word; }
td.brand { width: 56mm; vertical-align: top; }
td.who { width: 74mm; vertical-align: top; }
td.when { width: 48mm; vertical-align: top; text-align: right; }
.bar { width: 28mm; height: 3.5mm; background-color: #1a1a1a; margin-left: 20mm; margin-bottom: 8pt; }
.sign-name { font-family: 'IBM Plex Sans SemiBold'; font-size: 16pt; font-weight: 400; margin-top: 28pt; }",
    },
    HtmlTemplate {
        key: "html-letterhead-minimal", name: "Letterhead (minimal)", family: "administration", kind: "letter",
        description: "A letter with the mark and the date on the left, and the addressee under them.",
        html: "<div class=\"firm-name\">{{ company.name }}</div>
<div class=\"meta\">{{ company.address }}</div>
<p>{{ document.date | date }}<br/><span class=\"doc-ref\">{{ document.number }}</span></p>
{{#if addressee.name}}<p><span class=\"meta\">To</span><br/>{{ addressee.name }}<br/>{{ addressee.address }}</p>{{/if}}
{{#if document.subject}}<h2>{{ document.subject }}</h2>{{/if}}
<main>{{ document.body }}</main>
<p class=\"sign-name\">{{#if signatory.name}}{{ signatory.name }}{{else}}{{ signatory.firm }}{{/if}}</p>
{{#if signatory.name}}<p class=\"meta\">{{ signatory.firm }}</p>{{/if}}
<footer><p class=\"meta\">{{ company.name }}{{#if company.address}} · {{ company.address }}{{/if}}{{#if company.phone}} · {{ company.phone }}{{/if}}{{#if company.email}} · {{ company.email }}{{/if}}</p></footer>",
        css: "@page { size: A4; margin: 22mm 20mm 22mm 20mm; }
.firm-name { font-family: 'IBM Plex Sans SemiBold'; font-size: 12pt; font-weight: 400; }
.sign-name { font-family: 'IBM Plex Sans SemiBold'; font-size: 16pt; font-weight: 400; margin-top: 28pt; }
.meta { font-size: 8.5pt; color: #5c5c5c; }
footer { margin-top: 18pt; border-top: 0.6pt solid #1a1a1a; padding-top: 6pt; }
.doc-ref { font-family: 'IBM Plex Mono'; font-size: 9pt; }
h2 { font-family: 'IBM Plex Sans SemiBold'; font-size: 12pt; font-weight: 400; margin-top: 10pt; margin-bottom: 2pt; }
table.sign { width: 100%; margin-top: 28pt; break-inside: avoid; }
table.sign td { width: 50%; vertical-align: top; font-size: 9pt; }",
    },
    HtmlTemplate {
        key: "html-consultancy-proposal", name: "Consultancy proposal", family: "consultancy", kind: "letter",
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
  <thead><tr><th class=\"c-no\">#</th><th>Milestone</th><th class=\"amount c-share\">Share</th><th class=\"amount c-amt\">Amount</th></tr></thead>
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
        key: "html-appointment-letter", name: "Appointment letter", family: "consultancy", kind: "letter",
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
        key: "html-meeting-minutes", name: "Meeting minutes", family: "consultancy", kind: "letter",
        description: "Minutes record: project block, the minutes text and a prepared-by line.",
        html: "<header class=\"document-header\">
  <div class=\"firm-name\">{{ company.name }}</div>
  <div class=\"rule\"></div>
</header>
<h1>{{ document.title | default \"Meeting minutes\" }}</h1>
<p class=\"meta\"><span class=\"doc-ref\">{{ document.number }}</span> &nbsp; {{ document.date | date }} {{#if document.reference}}| Ref: {{ document.reference }}{{/if}}</p>
<table class=\"schedule\">
  <tr><td class=\"lab\">Project</td><td>{{ project.name }}</td></tr>
  <tr><td class=\"lab\">Client</td><td>{{ client.name }}</td></tr>
  <tr><td class=\"lab\">Site</td><td>{{ project.site_address }}</td></tr>
</table>
<main>{{ document.body }}</main>
<p class=\"keep\">Prepared by {{ signatory.name | default \"the architect\" }}, {{ signatory.firm }}</p>",
        css: BASE,
    },
    HtmlTemplate {
        key: "html-drawing-transmittal", name: "Drawing transmittal", family: "consultancy", kind: "letter",
        description: "Transmittal of drawings and documents: who it is sent to, what is sent (written in the text) and the purpose.",
        html: "<header class=\"document-header\">
  <div class=\"firm-name\">{{ company.name }}</div>
  <div class=\"meta\">{{ company.address }}</div>
  <div class=\"rule\"></div>
</header>
<h1>Drawing and document transmittal</h1>
<p class=\"meta\"><span class=\"doc-ref\">{{ document.number }}</span> &nbsp; {{ document.date | date }}</p>
<table class=\"schedule\">
  <tr><td class=\"lab\">Project</td><td>{{ project.name }}</td></tr>
  <tr><td class=\"lab\">Sent to</td><td>{{ addressee.name }}<br/>{{ addressee.address }}</td></tr>
  <tr><td class=\"lab\">Reference</td><td>{{ document.reference }}</td></tr>
</table>
<h2>Items sent</h2>
<main>{{ document.body }}</main>
<p class=\"meta\">Please acknowledge receipt.</p>",
        css: BASE,
    },
    HtmlTemplate {
        key: "html-fee-schedule", name: "Fee schedule annexure", family: "consultancy", kind: "agreement",
        description: "Annexure that lists the fee milestones. Use with an agreement so the schedule matches the agreement exactly.",
        html: "<header class=\"document-header\">
  <div class=\"firm-name\">{{ company.name }}</div>
  <div class=\"rule\"></div>
</header>
<h1>Fee and payment schedule</h1>
<p>Project: {{ project.name }}, {{ project.site_address }}<br/>Client: {{ client.name }}</p>
<h2>Total professional fee: {{ fee.total }}</h2>
<table class=\"schedule\">
  <thead><tr><th class=\"c-no\">#</th><th>Milestone</th><th class=\"amount c-share\">Share</th><th class=\"amount c-amt\">Amount</th></tr></thead>
  <tbody>
  {{#each fee_items}}
  <tr><td>{{ no }}</td><td>{{ description }}</td><td class=\"amount\">{{ share }}</td><td class=\"amount\">{{ formatted_amount }}</td></tr>
  {{/each}}
  </tbody>
</table>
<p class=\"meta\">Taxes are charged in addition as applicable ({{ tax.scheme }}). {{ document.body }}</p>",
        css: BASE,
    },
    service_letter!("html-coa-comprehensive", "Proposal — Comprehensive architectural services", "comprehensive architectural services"),
    service_letter!("html-coa-interior", "Proposal — Interior architecture", "interior architecture"),
    service_letter!("html-coa-landscape", "Proposal — Landscape architecture", "landscape architecture"),
    service_letter!("html-coa-urban", "Proposal — Urban design and urban renewal", "urban design and urban renewal"),
    service_letter!("html-coa-site", "Proposal — Site development", "site development"),
    service_letter!("html-coa-conservation", "Proposal — Architectural conservation", "architectural conservation"),
    service_letter!("html-coa-retrofitting", "Proposal — Retrofitting of buildings", "retrofitting of buildings"),
    service_letter!("html-coa-signage", "Proposal — Graphic design and signage", "graphic design and signage"),
];


/// Adds any built-in Word template that is missing, and publishes the current sheet over an older built-in original.
/// An older HTML original is published again as a Word document. A copy the firm made is left alone.
pub fn seed(conn: &Connection) -> Result<usize, String> {
    let mut added = 0;
    for t in LIBRARY {
        let exists: i64 = conn.query_row("SELECT COUNT(*) FROM templates WHERE template_key = ?1", params![t.key], |r| r.get(0)).map_err(|e| e.to_string())?;
        if exists > 0 {
            continue;
        }
        let taken: i64 = conn.query_row("SELECT COUNT(*) FROM templates WHERE name = ?1", params![t.name], |r| r.get(0)).map_err(|e| e.to_string())?;
        let name = if taken > 0 { format!("{} (O-Studio built-in)", t.name) } else { t.name.to_string() };
        let (html, css) = crate::docx::store(t.html, t.css)?;
        conn.execute(
            "INSERT INTO templates (kind, name, template_key, family, layout, description, built_in, format) VALUES (?1,?2,?3,?4,'letter',?5,1,'html')",
            params![t.kind, name, t.key, t.family, t.description],
        )
        .map_err(|e| e.to_string())?;
        let id = conn.last_insert_rowid();
        conn.execute(
            "INSERT INTO template_versions (template_id, version, body, html_source, css_source, change_summary) VALUES (?1, 1, '', ?2, ?3, 'Built-in original')",
            params![id, html, css],
        )
        .map_err(|e| e.to_string())?;
        added += 1;
    }
    refresh_shipped(conn)?;
    publish_word_versions(conn)?;
    Ok(added)
}

/// Publishes the current library sheet as the next version of each built-in whose latest text differs.
/// Drafts that still point at the built-in move to that version. Issued documents keep the version they were issued from.
fn refresh_shipped(conn: &Connection) -> Result<(), String> {
    conn.execute(
        "DELETE FROM template_drafts WHERE template_id IN (SELECT id FROM templates WHERE built_in = 1 AND template_key IS NOT NULL)",
        [],
    )
    .map_err(|e| e.to_string())?;
    for t in LIBRARY {
        let found = conn.query_row(
            "SELECT t.id, v.version, COALESCE(v.html_source,''), COALESCE(v.css_source,'')
             FROM templates t
             JOIN template_versions v ON v.template_id = t.id AND v.version = (SELECT MAX(version) FROM template_versions WHERE template_id = t.id)
             WHERE t.template_key = ?1 AND t.built_in = 1",
            params![t.key],
            |r| Ok((r.get::<_, i64>(0)?, r.get::<_, i64>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?)),
        );
        let Ok((id, version, html, css)) = found else { continue };
        conn.execute("UPDATE templates SET description = ?1 WHERE id = ?2 AND description != ?1", params![t.description, id]).map_err(|e| e.to_string())?;
        let (current_html, current_css) = crate::docx::prepare(&html, &css).unwrap_or((html, css));
        if current_html == t.html && current_css == t.css {
            continue;
        }
        let (document, marker) = crate::docx::store(t.html, t.css)?;
        conn.execute(
            "INSERT INTO template_versions (template_id, version, body, html_source, css_source, change_summary) VALUES (?1, ?2, '', ?3, ?4, 'Updated built-in sheet')",
            params![id, version + 1, document, marker],
        )
        .map_err(|e| e.to_string())?;
        let published = conn.last_insert_rowid();
        conn.execute(
            "UPDATE documents SET template_version_id = ?1 WHERE status = 'draft' AND template_version_id IN (SELECT id FROM template_versions WHERE template_id = ?2 AND id != ?1)",
            params![published, id],
        )
        .map_err(|e| e.to_string())?;
    }
    Ok(())
}

/// Company files that already stored an HTML template get a new published version that is a Word document.
fn publish_word_versions(conn: &Connection) -> Result<(), String> {
    let mut stmt = conn
        .prepare(
            "SELECT t.id, t.name, COALESCE(v.html_source,''), COALESCE(v.css_source,''), v.version FROM templates t
             JOIN template_versions v ON v.template_id = t.id AND v.version = (SELECT MAX(version) FROM template_versions WHERE template_id = t.id)
             WHERE t.format = 'html' AND COALESCE(v.css_source,'') != 'docx'",
        )
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?, r.get::<_, String>(2)?, r.get::<_, String>(3)?, r.get::<_, i64>(4)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    for (id, name, html, css, version) in rows {
        let (document, marker) = crate::docx::store(&html, &css)?;
        conn.execute(
            "INSERT INTO template_versions (template_id, version, body, html_source, css_source, change_summary) VALUES (?1, ?2, '', ?3, ?4, 'Saved as a Word document')",
            params![id, version + 1, document, marker],
        )
        .map_err(|e| e.to_string())?;
        let cleaned = name.replace(" (HTML)", "").replace(", HTML)", ")");
        if cleaned != name {
            let taken: i64 = conn
                .query_row("SELECT COUNT(*) FROM templates WHERE name = ?1 COLLATE NOCASE AND id != ?2", params![cleaned, id], |r| r.get(0))
                .map_err(|e| e.to_string())?;
            if taken == 0 {
                conn.execute("UPDATE templates SET name = ?1 WHERE id = ?2", params![cleaned, id]).map_err(|e| e.to_string())?;
            }
        }
    }
    Ok(())
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
    fn an_existing_company_receives_the_current_built_in_and_keeps_its_own_copy() {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::db::migrate(&mut c).unwrap();
        let classic = LIBRARY.iter().find(|t| t.key == "html-letterhead-classic").unwrap();
        let (id, version): (i64, i64) = c
            .query_row(
                "SELECT t.id, MAX(v.version) FROM templates t JOIN template_versions v ON v.template_id = t.id WHERE t.template_key = 'html-letterhead-classic'",
                [],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .unwrap();
        let (old, marker) = crate::docx::store("<p>Old letterhead</p>", "@page { size: A4; margin: 18mm; }").unwrap();
        c.execute(
            "INSERT INTO template_versions (template_id, version, body, html_source, css_source, change_summary) VALUES (?1, ?2, '', ?3, ?4, 'Older sheet')",
            params![id, version + 1, old, marker],
        )
        .unwrap();
        let old_version: i64 = c.query_row("SELECT id FROM template_versions WHERE template_id = ?1 AND version = ?2", params![id, version + 1], |r| r.get(0)).unwrap();
        c.execute("INSERT INTO template_drafts (template_id, html_source, css_source, based_on_version) VALUES (?1, ?2, 'docx', ?3)", params![id, old, version]).unwrap();
        c.execute("INSERT INTO documents (kind, doc_date, template_version_id, status) VALUES ('letter_out', '2026-10-01', ?1, 'draft')", params![old_version]).unwrap();
        c.execute("INSERT INTO documents (kind, doc_date, template_version_id, status, number) VALUES ('letter_out', '2026-10-01', ?1, 'issued', 'OUT/1')", params![old_version]).unwrap();
        c.execute("INSERT INTO templates (kind, name, family, layout, built_in, format) VALUES ('letter', 'Our letterhead', 'administration', 'letter', 0, 'html')", []).unwrap();
        let copy = c.last_insert_rowid();
        c.execute(
            "INSERT INTO template_versions (template_id, version, body, html_source, css_source) VALUES (?1, 1, '', ?2, ?3)",
            params![copy, old, marker],
        )
        .unwrap();
        assert_eq!(seed(&c).unwrap(), 0);
        let (latest, html, css): (i64, String, String) = c
            .query_row(
                "SELECT v.version, v.html_source, v.css_source FROM template_versions v WHERE v.template_id = ?1 ORDER BY v.version DESC LIMIT 1",
                params![id],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
            )
            .unwrap();
        assert!(latest > version + 1);
        let (html, css) = crate::docx::prepare(&html, &css).unwrap();
        assert_eq!(html, classic.html);
        assert_eq!(css, classic.css);
        let draft: i64 = c.query_row("SELECT template_version_id FROM documents WHERE status = 'draft'", [], |r| r.get(0)).unwrap();
        let issued: i64 = c.query_row("SELECT template_version_id FROM documents WHERE status = 'issued'", [], |r| r.get(0)).unwrap();
        assert_ne!(draft, old_version);
        assert_eq!(issued, old_version);
        let copy_html: String = c.query_row("SELECT html_source FROM template_versions WHERE template_id = ?1", params![copy], |r| r.get(0)).unwrap();
        assert_eq!(copy_html, old);
        assert_eq!(seed(&c).unwrap(), 0);
        let again: i64 = c.query_row("SELECT MAX(version) FROM template_versions WHERE template_id = ?1", params![id], |r| r.get(0)).unwrap();
        assert_eq!(again, latest);
        let drafts: i64 = c.query_row("SELECT COUNT(*) FROM template_drafts WHERE template_id = ?1", params![id], |r| r.get(0)).unwrap();
        assert_eq!(drafts, 0);
    }

    /// The renderer sizes an auto-layout table to its longest unbroken text, which pushed amount columns past the right margin
    /// when long descriptions or names were used. Tables that carry data must use a fixed layout so cells wrap instead.
    #[test]
    fn data_tables_use_a_fixed_layout_so_long_text_wraps_inside_the_page() {
        for t in LIBRARY {
            for class in ["schedule", "head", "sign"] {
                if t.html.contains(&format!("<table class=\"{class}\"")) {
                    assert!(t.css.contains(&format!("table.{class} {{")) && t.css.split(&format!("table.{class} {{")).nth(1).unwrap().split('}').next().unwrap().contains("table-layout: fixed"),
                        "{}: table.{class} must set table-layout: fixed", t.key);
                }
            }
        }
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
