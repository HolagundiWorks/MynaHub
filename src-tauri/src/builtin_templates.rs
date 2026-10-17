//! The built-in template library. These are practice templates (editable wording with {{fields}}), not official
//! Council of Architecture forms: nothing here claims COA approval or legal sufficiency, and the wording must be
//! reviewed by the practice (and its counsel where appropriate) before use.
//!
//! Text in [[double square brackets]] is a prompt the author must replace; a document with prompts left in it cannot
//! be issued. A firm customises a copy. The built-in original in an existing company is published again when this
//! library changes. Issued documents keep the version they were issued from.
use rusqlite::{params, Connection};

pub struct Builtin {
    pub key: &'static str,
    pub name: &'static str,
    pub family: &'static str,
    /// Document type the template starts: `letter` or `agreement`.
    pub kind: &'static str,
    /// `letter` has an addressee block; `record` is a titled record (minutes, notes, registers).
    pub layout: &'static str,
    pub description: &'static str,
    pub body: &'static str,
}

pub const LIBRARY: &[Builtin] = &[
    Builtin {
        key: "consultancy-proposal", name: "Consultancy proposal", family: "consultancy", kind: "letter", layout: "letter",
        description: "Fee proposal for a prospective client: understanding of the brief, scope, fee basis and assumptions.",
        body: "Dear Sir / Madam,

Sub: {{document.subject}}
{{document.reference}}

Thank you for considering {{company.name}} for {{project.name}} at {{project.site}}. This letter sets out our understanding of the brief and our proposal for architectural consultancy services.

# 1. Understanding of the brief
[[Summarise the client's requirements, the site and the intended use of the building.]]

# 2. Consultancy category and project type
Consultancy category: [[Architectural / Landscaping / Part consultancy / Miscellaneous]]
Project type: [[Residential / Commercial / Healthcare / Hospitality / Education / Other]]

# 3. Services proposed
Design phase: [[list the design-stage services included]]
Execution phase: [[state exactly what is included, for example drawings issued for construction, periodic site visits, or none. Do not imply supervision or certification unless it is listed here.]]
Handover: [[list handover documents and drawings included]]

# 4. Services not included
[[List services that are excluded or provided by others, such as structural, MEP, interior, landscape, statutory liaison, project management or site supervision.]]

# 5. Professional fee
Fee basis: [[per square foot / percentage of project cost / lump sum / other]]
Proposed fee: {{agreement.fee}}, exclusive of applicable taxes and reimbursable expenses.

# 6. Assumptions and client decisions
[[State programme assumptions, the information and approvals needed from the client, and how long the proposal remains open.]]

We would be glad to discuss this proposal and to prepare a formal agreement on acceptance.

Yours sincerely,",
    },
    Builtin {
        key: "appointment-letter", name: "Appointment letter", family: "consultancy", kind: "letter", layout: "letter",
        description: "Confirms the appointment of the practice for a project, pending or alongside a signed agreement.",
        body: "Dear Sir / Madam,

Sub: Appointment as architects for {{project.name}}
{{document.reference}}

We refer to our discussions and the proposal dated [[date of proposal]]. We are pleased to confirm that {{client.name}} has appointed {{company.name}} to provide architectural consultancy services for {{project.name}} at {{project.site}}.

# Terms of appointment
The scope of services is as set out in [[proposal or annexure reference]]. The professional fee is {{agreement.fee}}, payable as set out in [[fee annexure or agreement reference]]. Applicable taxes are charged in addition.

# Authority
This appointment is confirmed by [[name and designation of the person authorised to sign for the client]].

A formal agreement will follow. Please sign and return a copy of this letter to confirm.

Yours sincerely,",
    },
    Builtin {
        key: "consultancy-agreement-detailed", name: "Consultancy agreement (detailed)", family: "consultancy", kind: "agreement", layout: "letter",
        description: "A fuller practice agreement with configurable scope, exclusions, responsibilities and terms. Have it reviewed before use.",
        body: "This agreement is made on {{document.date}} at [[place]] between {{company.name}}, {{company.address}} (the \"Architect\"), and {{client.name}}, {{client.address}} (the \"Client\"). The Client is represented by [[name and authority to sign]].

# 1. Project
The Client appoints the Architect for {{project.name}} at {{project.site}}. Project description: [[brief description, type and approximate area]].

# 2. Scope of services
Design phase: [[services included]]
Execution phase: [[services included, stated explicitly]]
Handover: [[services included]]
Site supervision, certification, project management, statutory liaison and specialist consultancy are included only where listed above.

# 3. Deliverables
[[List the drawings and documents and the stage at which each is issued.]]

# 4. Exclusions and services by others
[[List exclusions and the consultants or agencies responsible for them.]]

# 5. Client responsibilities
The Client shall provide information, decisions, approvals and site access in good time, and shall pay fees as agreed. Information required: [[list]].

# 6. Coordination
The Architect coordinates with the consultants named here: [[consultants and who appoints and pays them]].

# 7. Professional fee
The fee is {{agreement.fee}}, on the basis of [[fee basis]], payable at the project stages. Those stages follow the consultancy and the number of floors, and each stage is a payment. Applicable taxes are charged in addition.

# 8. Reimbursable expenses
[[State which expenses are reimbursable and the approval process.]]

# 9. Additional services and changes
Services outside this agreement and changes to the scope are recorded in writing and the fee adjusted by agreement before the work proceeds.

# 10. Programme and decisions
[[State programme assumptions and the client decisions the programme depends on.]]

# 11. Copyright and use of documents
[[State who holds copyright in the drawings and documents and the permitted use of them.]]

# 12. Suspension and termination
[[State notice periods and how fees for work done are settled on suspension or termination.]]

# 13. Disputes and governing law
[[State the dispute-resolution wording chosen by the practice and the governing law.]]

# 14. Annexures
[[List annexures and the document version.]]",
    },
    Builtin {
        key: "scope-annexure", name: "Scope of services annexure", family: "consultancy", kind: "letter", layout: "record",
        description: "Annexure listing the services, deliverables and exclusions by phase.",
        body: "Project: {{project.name}}, {{project.site}}
Client: {{client.name}}
Reference: {{document.reference}}

# Design phase
Included services: [[list]]
Deliverables and drawing stages: [[list]]

# Execution phase
Service level: [[state exactly what is included, for example drawings for construction and periodic site visits. If supervision or certification is not included, say so.]]

# Handover
Included services and documents: [[list]]

# Exclusions and services by others
[[list]]

# Client responsibilities and information required
[[list]]",
    },
    Builtin {
        key: "fee-milestone-annexure", name: "Fee and milestone annexure", family: "consultancy", kind: "letter", layout: "record",
        description: "Annexure setting out the fee basis, payment milestones and reimbursables.",
        body: "Project: {{project.name}}
Client: {{client.name}}

# Fee
Fee basis: [[per square foot / percentage / lump sum / other]]
Professional fee: {{agreement.fee}}, exclusive of applicable taxes.

# Payment milestones
[[List each milestone, its share of the fee and when it falls due. Use the schedule in the agreement if there is one.]]

# Reimbursable expenses
[[State what is reimbursable and the approval process.]]

# Taxes
Taxes are charged as applicable to the practice's registration on the invoice date.",
    },
    Builtin {
        key: "variation-proposal", name: "Additional services / variation proposal", family: "consultancy", kind: "letter", layout: "letter",
        description: "Proposes a fee for work outside the agreed scope.",
        body: "Dear Sir / Madam,

Sub: Additional services for {{project.name}}
{{document.reference}}

During the course of {{project.name}} the following work has arisen that is outside the agreed scope.

# Description of the change
[[describe the request or change and who asked for it]]

# Effect
Additional services: [[list]]
Additional fee: [[amount and basis]]
Effect on programme: [[describe]]

We will proceed on receiving your written confirmation.

Yours sincerely,",
    },
    Builtin {
        key: "client-instruction", name: "Client instruction / change confirmation", family: "consultancy", kind: "letter", layout: "letter",
        description: "Confirms an instruction or change given verbally or informally by the client.",
        body: "Dear Sir / Madam,

Sub: Confirmation of instruction, {{project.name}}
{{document.reference}}

We confirm the following instruction received from {{client.name}} on [[date]] by [[meeting / call / email]]:

[[state the instruction]]

Effect on drawings, programme or fee: [[describe, or state none]]

Please let us know within [[number]] days if this does not reflect your instruction.

Yours sincerely,",
    },
    Builtin {
        key: "design-approval", name: "Design approval record", family: "consultancy", kind: "letter", layout: "record",
        description: "Records the client's approval of a design stage.",
        body: "Project: {{project.name}}, {{project.site}}
Client: {{client.name}}
Stage approved: [[concept / schematic / design development / other]]

# Drawings and documents approved
[[list drawing numbers, revisions and dates]]

# Conditions and comments
[[record any conditions or comments]]

Approved on behalf of the client by: [[name and designation]]
Date: [[date]]",
    },
    Builtin {
        key: "drawing-transmittal", name: "Drawing and document transmittal", family: "consultancy", kind: "letter", layout: "record",
        description: "Lists drawings and documents sent, with revision, purpose and copies.",
        body: "Project: {{project.name}}, {{project.site}}
Sent to: {{party.name}}
{{party.address}}
Reference: {{document.reference}}

# Items sent
[[list each drawing or document with number, title, revision, size and number of copies]]

Purpose of issue: [[for information / approval / tender / construction]]
Sent by: [[courier / email / hand]]

Please acknowledge receipt.",
    },
    Builtin {
        key: "meeting-minutes", name: "Meeting minutes", family: "consultancy", kind: "letter", layout: "record",
        description: "Minutes with attendees, decisions and actions.",
        body: "Project: {{project.name}}, {{project.site}}
Meeting: [[title]] on [[date]] at [[place]]
Reference: {{document.reference}}

# Present
[[names and organisations]]

# Matters discussed and decisions
[[item by item]]

# Actions
[[action, responsible person, due date]]

Next meeting: [[date and place]]

These minutes will be taken as correct unless corrections are sent within [[number]] days.",
    },
    Builtin {
        key: "site-visit-note", name: "Site visit note", family: "consultancy", kind: "letter", layout: "record",
        description: "A record of observations on a site visit. It is not a certificate or supervision report.",
        body: "Project: {{project.name}}, {{project.site}}
Visit on: [[date]] by [[name]]
Reference: {{document.reference}}

This note records what was observed during a visit. It is not a certificate of quality, quantity or compliance, and it does not make the Architect responsible for supervising the contractor's work unless that service is included in the appointment.

# Observations
[[describe what was seen, with locations]]

# Points raised with the contractor
[[list]]

# Follow-up
[[actions and who is to do them]]",
    },
    Builtin {
        key: "status-report", name: "Project status report", family: "consultancy", kind: "letter", layout: "record",
        description: "Progress report on design, approvals, programme and open client decisions.",
        body: "Project: {{project.name}}, {{project.site}}
Client: {{client.name}}
Report date: {{document.date}}   Reference: {{document.reference}}

# Progress since the last report
[[describe]]

# Programme
[[state progress against the programme and any delay and its cause]]

# Approvals and statutory submissions
[[status]]

# Decisions needed from the client
[[list, with dates needed by]]

# Fees and invoices
[[summarise invoices raised and amounts outstanding]]",
    },
    Builtin {
        key: "handover-register", name: "Handover document register", family: "consultancy", kind: "letter", layout: "record",
        description: "Index of drawings and documents handed over at completion, with a checklist.",
        body: "Project: {{project.name}}, {{project.site}}
Handed to: {{client.name}}
Date: {{document.date}}   Reference: {{document.reference}}

# Documents handed over
[[list each with number, title, revision and format]]

# Checklist
[[tick off items such as as-built drawings, approvals, certificates and manuals that are part of the appointment]]

Received on behalf of the client by: [[name and designation]]   Date: [[date]]",
    },
    Builtin {
        key: "closure-final-account", name: "Consultancy closure and final account letter", family: "consultancy", kind: "letter", layout: "letter",
        description: "Closes the consultancy and states the final account.",
        body: "Dear Sir / Madam,

Sub: Closure of consultancy and final account, {{project.name}}
{{document.reference}}

The services under our appointment for {{project.name}} are complete as of [[date]]. The final account is below.

Total professional fee: {{agreement.fee}}
Additional services agreed: [[amount or none]]
Invoiced to date: [[amount]]
Received to date: [[amount]]
Balance: [[amount]]

[[state any documents still to be handed over]]

Yours sincerely,",
    },
    Builtin {
        key: "no-dues-letter", name: "No-dues confirmation letter", family: "consultancy", kind: "letter", layout: "letter",
        description: "Confirms that the fee for the stated services has been received in full.",
        body: "Dear Sir / Madam,

Sub: Confirmation of payment, {{project.name}}
{{document.reference}}

We confirm that we have received {{client.name}}'s payments of [[amount]] against our invoices [[invoice numbers]] for the services [[describe services]] on {{project.name}}. Nothing is outstanding against those invoices as of {{document.date}}.

This letter does not cover services outside those invoices.

Yours sincerely,",
    },
    Builtin {
        key: "fee-reminder", name: "Outstanding fee reminder", family: "billing", kind: "letter", layout: "letter",
        description: "A courteous reminder for fees past their due date.",
        body: "Dear Sir / Madam,

Sub: Reminder of outstanding fees, {{project.name}}
{{document.reference}}

Our records show the following fees are outstanding as of {{document.date}}:

[[invoice numbers, dates, amounts and amount outstanding]]

If payment has already been made, please send us the details so that we can reconcile it. Otherwise we would be grateful for payment by [[date]].

Payment details: {{company.bank}}

Yours sincerely,",
    },
    Builtin {
        key: "expense-claim", name: "Reimbursement / expense claim", family: "billing", kind: "letter", layout: "letter",
        description: "Claims reimbursable expenses with a list and the approval reference.",
        body: "Dear Sir / Madam,

Sub: Reimbursable expenses, {{project.name}}
{{document.reference}}

We claim the following reimbursable expenses incurred on {{project.name}}, as approved in [[approval reference]]:

[[date, description and amount of each expense. Attach the bills.]]

Total claimed: [[amount]]

Yours sincerely,",
    },
    Builtin {
        key: "payment-instructions", name: "Bank and payment instructions", family: "billing", kind: "letter", layout: "record",
        description: "A sheet giving the practice's payment details to a client. Shown only with details entered in the company profile.",
        body: "Payments to {{company.name}}

{{company.bank}}

UPI: {{company.upi}}

Please quote the invoice number in the payment reference and send the payment advice to {{company.email}}.",
    },
    Builtin {
        key: "approval-submission", name: "Approval submission cover letter", family: "administration", kind: "letter", layout: "letter",
        description: "Covers drawings submitted to an authority for approval.",
        body: "To,
{{party.name}}
{{party.address}}

Sub: Submission for approval, {{project.name}}, {{project.site}}
{{document.reference}}

We submit herewith the following documents for [[approval or permission sought]] on behalf of {{client.name}}.

[[list drawings and documents with numbers and revisions]]

Please acknowledge receipt and inform us of any further requirements.

Yours faithfully,",
    },
    Builtin {
        key: "consultant-coordination", name: "Consultant coordination request", family: "administration", kind: "letter", layout: "letter",
        description: "Asks a specialist consultant for input or information needed for coordination.",
        body: "Dear Sir / Madam,

Sub: Coordination required, {{project.name}}
{{document.reference}}

For the next design stage of {{project.name}} we need the following from you:

[[list the information, drawings or decisions needed]]

Please send these by [[date]]. The drawings we are working from are [[list with revisions]].

Yours sincerely,",
    },
    Builtin {
        key: "vendor-correspondence", name: "Contractor / vendor correspondence", family: "administration", kind: "letter", layout: "letter",
        description: "General correspondence to a contractor or vendor.",
        body: "Dear Sir / Madam,

Sub: {{document.subject}}
{{document.reference}}

With reference to {{project.name}} at {{project.site}}:

[[state the matter]]

Please respond by [[date]].

Yours sincerely,",
    },
    Builtin {
        key: "rfi", name: "RFI / clarification request", family: "administration", kind: "letter", layout: "record",
        description: "A request for information, with space to record the response.",
        body: "Project: {{project.name}}, {{project.site}}
RFI reference: {{document.reference}}
To: {{party.name}}   Date: {{document.date}}

# Question
[[describe the clarification needed, with drawing references]]

Response needed by: [[date]]

# Response
[[record the response, who gave it and when]]",
    },
    Builtin {
        key: "onboarding-sheet", name: "Client onboarding / project information sheet", family: "administration", kind: "letter", layout: "record",
        description: "Pre-design questionnaire: client, site, occupants, preferences, spaces, materials and room notes.",
        body: "PRE-DESIGN QUESTIONNAIRE
Client: {{client.name}}
Correspondence: {{client.address}}
Project: {{project.name}} ({{project.code}})
Site: {{project.site}}
Date: {{document.date}}

Complete this before the briefing meeting. Incomplete sections delay concept design.

# 1. Basic information
Current address: [[current address]]
Site address: [[site address, if different]]
Mobile: [[mobile]]    Phone: [[phone]]    Email: [[email]]
Occupation: [[occupation]]
Plot size or dimensions: [[acres or dimensions]]
Terrain: [[sloped / flat / mountainous]]
Vegetation: [[open field / wooded]]
Natural features, soil, other characteristics: [[notes]]
Orientation measurements (W N-S, E N-S, N E-W, S E-W, NE-SW, SE-NW): [[measurements]]

# 2. Project information
Intended use: [[residential, commercial, new build or renovation]]
Built-up area intended: [[area]]
Construction in phases: [[yes / no]]
Tentative construction start: [[date]]
Budget excluding legal fees: [[amount]]
Finance: [[source]]

# 3. Occupants and staff
Household (name, relation, age, occupation): [[table]]
Staff (maid, butler, driver, gardener, other, and counts): [[staff]]

# 4. Design preferences
Orientation: [[N-S / E-W]]
Main door: [[north / south / east / west]]
Secondary door: [[direction]]
Views: [[direction]]
Basement: [[yes / no, and purpose]]
Vastu: [[yes / no]]    Consultant: [[name, or none]]

# 5. Style, places and activities
Style: [[the architectural character the client wants]]
Places loved, and why: [[references]]
Important activities: [[functions and events]]
Indoor preference: [[large open spaces / separate rooms]]
Outdoor living: [[yes / no, and whether indoor and outdoor should flow together]]
Outdoor spaces: [[garden, informal spaces, swimming pool, and any others]]

# 6. Space requirements
For each space, record area, floor and a short description.
Kitchen, store, utility, dining, pooja, general bath, powder room, living, lobby, bedrooms, gym, study, family living, home theatre, meditation, parking, maid room, and any space not listed.
[[space schedule]]

# 7. Material preferences
Construction: [[notes]]
Flooring: [[notes]]
Wall finish: [[notes]]
Cabinetry: [[notes]]
Seating: [[notes]]
Beds: [[notes]]

# 8. Room notes
One note per room: name, ambience, lighting, type of space, public connectivity, outdoor connectivity, ceiling, flooring, furniture, and any further instruction.
Kitchen also records type (parallel, U, L, island or single counter), open or closed, and the appliances required.
[[room notes]]

# 9. Billing identity
Billing address: [[billing address]]
GSTIN: [[GSTIN]]    PAN: [[PAN]]
Preferred contact: [[email / WhatsApp / phone / client portal]]
Authorised representative: [[name, designation, phone]]",
    },
    Builtin {
        key: "project-brief", name: "Project brief confirmation", family: "administration", kind: "letter", layout: "letter",
        description: "The project design brief compiled from the questionnaire, for the client to confirm before concept design.",
        body: "Dear Sir / Madam,

Sub: Confirmation of project brief, {{project.name}}
{{document.reference}}
Site: {{project.site}}

We record below our understanding of your brief for {{project.name}}. Please confirm it, or tell us what to change, before concept design begins.

# 1. Project summary
Use and typology: [[use]]
Built-up area: [[area, or TBD with a decision date]]
Phasing: [[phases]]
Budget excluding legal fees: [[amount]]
Start target: [[date]]

# 2. Client and occupants
{{client.name}}
{{client.address}}
Household: [[name, relation, age, occupation]]
Staff: [[requirements]]

# 3. Site context
Plot dimensions: [[dimensions]]
Terrain and vegetation: [[notes]]
Constraints: [[survey, soil, access, services]]
Orientation and views: [[notes]]

# 4. Design drivers
Style: [[character]]
Places loved: [[references]]
Activities: [[functions]]
Indoor and outdoor: [[preference]]
Vastu: [[yes, no, or consultant engaged]]
Doors and basement: [[notes]]

# 5. Accommodation schedule
[[room, area, floor, description]]

# 6. Room character
[[one paragraph each for entrance, living, kitchen, dining and every bedroom]]
Kitchen type and open or closed: [[decision]]

# 7. Materials
Construction, flooring, walls, cabinetry, seating and beds: [[palette, or to develop in concept]]

# 8. Assumptions and exclusions
[[survey pending, authority constraints, items deferred to concept]]

# 9. Approval
Approved by: [[client name]]
Date: [[date]]

Yours sincerely,",
    },
    Builtin {
        key: "brief-guidelines", name: "Onboarding and brief guidelines", family: "administration", kind: "letter", layout: "record",
        description: "How the office takes a client from enquiry to a confirmed project design brief.",
        body: "CLIENT ONBOARDING AND PROJECT BRIEF
Project: {{project.name}} ({{project.code}})
Client: {{client.name}}
Site: {{project.site}}

Onboarding ends when the client brief is confirmed and the project can move to concept design. It is not statutory approval, tendering, or construction administration.

# When to use
Enquiry: create the client and log the first communication.
Fee discussion: draft the fee proposal.
Appointment: site visit, letter of appointment, and the advance if one applies.
Brief: send the pre-design questionnaire and compile the design brief.
Design start: only after the client has approved the brief.

Send the questionnaire after the client accepts the proposal in principle, and before concept design starts.

# Steps
1. Create the client: name, correspondence address, mobile, email, billing address, GSTIN, PAN, and how they prefer to be contacted. Add an authorised representative when someone else may instruct the office.
2. Open the project: type, site, and the built-up area if the client has stated it. Record the site visit and the scope summary.
3. Issue the pre-design questionnaire. A printed copy suits a first residential briefing. An edited copy can come back by email for the office to transcribe. A meeting can walk through the sections, with room notes in a second sitting.
4. Visit the site and check the questionnaire against the plot: terrain, vegetation, orientation, dimensions, soil, access, and services. Record any difference before the brief is compiled.
5. Hold the briefing with the principal or the senior lead. Confirm the budget excluding legal fees, the phasing, and the tentative start. Agree which rooms need a room note.
6. Compile the project design brief from the questionnaire and obtain the client's approval before concept design.

# Ready for concept design
Budget and built-up area are stated, or marked to be decided on a named date.
Every bedroom and wet area is in the space list, or marked not required.
Kitchen type, and open or closed, is decided.
Vastu is yes, no, or a consultant is named.
Entrance, living, kitchen and the principal bedroom each have a room note.
The principal has reviewed the brief and the client has approved it.

# Roles
The owner or partner appoints the practice, accepts the fee, and signs off the brief.
The senior architect reviews the questionnaire, holds the briefing, and writes the brief.
An associate transcribes the answers and the site notes.
Admin keeps the client record and sends the questionnaire.

# Records
Questionnaires hold personal and financial information. Keep them with the project team. Do not share one client's budget or preferences with another client or with a contractor.",
    },
];

/// Adds any built-in that is missing, and publishes the current wording over an older built-in original.
/// A firm's own copy is left alone. Returns how many were added.
pub fn seed(conn: &Connection) -> Result<usize, String> {
    let mut added = 0;
    for b in LIBRARY {
        let exists: i64 = conn.query_row("SELECT COUNT(*) FROM templates WHERE template_key = ?1", params![b.key], |r| r.get(0)).map_err(|e| e.to_string())?;
        if exists > 0 {
            refresh_wording(conn, b)?;
            continue;
        }
        // If the firm already has its own template with this name, keep theirs and label the built-in.
        let taken: i64 = conn.query_row("SELECT COUNT(*) FROM templates WHERE name = ?1", params![b.name], |r| r.get(0)).map_err(|e| e.to_string())?;
        let name = if taken > 0 { format!("{} (ADMS built-in)", b.name) } else { b.name.to_string() };
        conn.execute(
            "INSERT INTO templates (kind, name, template_key, family, layout, description, built_in) VALUES (?1,?2,?3,?4,?5,?6,1)",
            params![b.kind, name, b.key, b.family, b.layout, b.description],
        )
        .map_err(|e| e.to_string())?;
        let id = conn.last_insert_rowid();
        conn.execute("INSERT INTO template_versions (template_id, version, body) VALUES (?1, 1, ?2)", params![id, b.body]).map_err(|e| e.to_string())?;
        added += 1;
    }
    Ok(added)
}

fn refresh_wording(conn: &Connection, b: &Builtin) -> Result<(), String> {
    let found = conn.query_row(
        "SELECT t.id, v.version, v.body FROM templates t
         JOIN template_versions v ON v.template_id = t.id AND v.version = (SELECT MAX(version) FROM template_versions WHERE template_id = t.id)
         WHERE t.template_key = ?1 AND t.built_in = 1",
        params![b.key],
        |r| Ok((r.get::<_, i64>(0)?, r.get::<_, i64>(1)?, r.get::<_, String>(2)?)),
    );
    let Ok((id, version, body)) = found else { return Ok(()) };
    conn.execute("UPDATE templates SET description = ?1 WHERE id = ?2 AND description != ?1", params![b.description, id]).map_err(|e| e.to_string())?;
    if body == b.body {
        return Ok(());
    }
    conn.execute(
        "INSERT INTO template_versions (template_id, version, body, change_summary) VALUES (?1, ?2, ?3, 'Updated built-in wording')",
        params![id, version + 1, b.body],
    )
    .map_err(|e| e.to_string())?;
    let published = conn.last_insert_rowid();
    conn.execute(
        "UPDATE documents SET template_version_id = ?1 WHERE status = 'draft' AND template_version_id IN (SELECT id FROM template_versions WHERE template_id = ?2 AND id != ?1)",
        params![published, id],
    )
    .map_err(|e| e.to_string())?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::documents::validate_fields;

    #[test]
    fn every_built_in_uses_only_known_fields_and_has_unique_keys() {
        let mut keys = std::collections::HashSet::new();
        let mut names = std::collections::HashSet::new();
        for b in LIBRARY {
            assert!(keys.insert(b.key), "duplicate key {}", b.key);
            assert!(names.insert(b.name), "duplicate name {}", b.name);
            validate_fields(b.body).unwrap_or_else(|e| panic!("{}: {e}", b.key));
            assert!(["consultancy", "billing", "administration"].contains(&b.family));
            assert!(["letter", "agreement"].contains(&b.kind) && ["letter", "record"].contains(&b.layout));
            assert!(!b.body.contains("COA-approved") && !b.body.contains("legally sufficient"), "{} must not claim approval", b.key);
        }
        assert!(LIBRARY.len() >= 24);
    }

    #[test]
    fn seeding_adds_missing_built_ins_once_and_never_touches_firm_templates() {
        let mut c = Connection::open_in_memory().unwrap();
        c.pragma_update(None, "foreign_keys", "ON").unwrap();
        crate::db::migrate(&mut c).unwrap();
        let n: i64 = c.query_row("SELECT COUNT(*) FROM templates WHERE built_in = 1", [], |r| r.get(0)).unwrap();
        assert_eq!(n as usize, LIBRARY.len() + crate::builtin_html::LIBRARY.len() + 2, "wording library, HTML library and the two starter templates");
        assert_eq!(seed(&c).unwrap(), 0, "seeding again adds nothing");
        // a firm's own template with a built-in's name survives, and a customised built-in copy is not reset
        c.execute("DELETE FROM template_versions WHERE template_id = (SELECT id FROM templates WHERE template_key = 'meeting-minutes')", []).unwrap_err();
        c.execute("UPDATE templates SET name = 'My minutes', built_in = 0, template_key = NULL WHERE template_key = 'meeting-minutes'", []).unwrap();
        assert_eq!(seed(&c).unwrap(), 1, "the missing built-in is added back");
        let mine: i64 = c.query_row("SELECT COUNT(*) FROM templates WHERE name = 'My minutes'", [], |r| r.get(0)).unwrap();
        assert_eq!(mine, 1);
    }
}
