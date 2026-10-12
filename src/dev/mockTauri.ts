/**
 * Development only: lets the interface be opened in an ordinary browser (npm run dev) with sample data,
 * so layouts can be reviewed without the native app. It is never used inside the real application
 * (the native bridge exists there) and is removed from production builds.
 */
type Args = Record<string, unknown> | undefined;

const inr = (p: number) => (p / 100).toLocaleString("en-IN", { style: "currency", currency: "INR" });

const clients = [
  { id: 1, name: "Acme Developers", contact_person: "R. Iyer", email: "ri@acme.example", phone: "98200 11111", address: "Pune", pan: "AAACA1234A", gstin: "27AAACA1234A1Z5", archived: false },
  { id: 2, name: "Kulkarni Family Trust", contact_person: "S. Kulkarni", email: "", phone: "98200 22222", address: "Mumbai", pan: "", gstin: "", archived: false },
  { id: 3, name: "Old Client Pvt Ltd", contact_person: "", email: "", phone: "", address: "", pan: "", gstin: "", archived: true },
];

const projects = [
  { id: 1, client_id: 1, code: "ACM-01", name: "Baner Villa", site_address: "Plot 5, Baner", status: "active", archived: false, client_name: "Acme Developers" },
  { id: 2, client_id: 1, code: "ACM-02", name: "Office Fit-out", site_address: "", status: "on_hold", archived: false, client_name: "Acme Developers" },
  { id: 3, client_id: 2, code: "KFT-01", name: "Heritage Restoration", site_address: "Fort, Mumbai", status: "completed", archived: false, client_name: "Kulkarni Family Trust" },
];

const mkInvoice = (id: number, number: string | null, status: string, kind: string, total: number, paid: number, tds: number, date: string, due: string) => ({
  id, number, kind, status, client_name: id % 2 ? "Acme Developers" : "Kulkarni Family Trust", project_name: id % 2 ? "Baner Villa" : "Heritage Restoration", issue_date: date,
  total_display: inr(total), total_paise: total, paid_display: inr(paid), tds_display: inr(tds), balance_paise: total - paid - tds, balance_display: inr(total - paid - tds),
  credited_display: inr(0), paid_paise: paid, tds_paise: tds, credited_paise: 0, due_date: due, converted_to: null, from_proforma: null,
});

const invoices = [
  mkInvoice(1, "INV/2026-27/0001", "issued", "tax_invoice", 11800000, 10800000, 1000000, "2026-10-01", "2026-10-31"),
  mkInvoice(2, "INV/2026-27/0002", "issued", "tax_invoice", 5900000, 2000000, 0, "2026-09-10", "2026-09-25"),
  mkInvoice(3, "INV/2026-27/0003", "issued", "tax_invoice", 3540000, 0, 0, "2026-10-05", "2026-11-20"),
  mkInvoice(4, "PI/2026-27/0001", "issued", "proforma", 2360000, 0, 0, "2026-10-08", ""),
  mkInvoice(5, null, "draft", "tax_invoice", 1180000, 0, 0, "2026-10-12", ""),
];

const txns = Array.from({ length: 14 }, (_, i) => ({
  id: i + 1, txn_date: `2026-10-${String(1 + i).padStart(2, "0")}`, narration: i % 3 === 0 ? "NEFT ACME DEVELOPERS INV/2026-27/0001" : i % 3 === 1 ? "Bank charges" : "UPI/KULKARNI TRUST", reference: `UTR${1000 + i}`,
  debit_display: i % 3 === 1 ? inr(4500) : "", credit_display: i % 3 === 1 ? "" : inr(1080000 + i * 5000), credit_paise: i % 3 === 1 ? 0 : 1080000 + i * 5000, debit_paise: i % 3 === 1 ? 4500 : 0,
  allocated_display: inr(0), unallocated_paise: 1080000, status: i % 4 === 0 ? "matched" : i % 4 === 1 ? "unmatched" : i % 4 === 2 ? "partial" : "ignored", note: "", account_id: 1, account_name: "HDFC current",
}));

const journals = Array.from({ length: 12 }, (_, i) => ({
  id: i + 1, number: `JV/2026-27/${String(i + 1).padStart(4, "0")}`, date: `2026-10-${String(1 + i).padStart(2, "0")}`, narration: i % 2 ? `Receipt RCT/2026-27/${String(i).padStart(4, "0")}` : `Invoice INV/2026-27/${String(i).padStart(4, "0")}`,
  source_type: i % 2 ? "receipt" : "invoice", total_display: inr(11800000 - i * 100000), total_paise: 11800000 - i * 100000, reversed: i === 5,
}));

const accounts = [
  ["1000", "Cash in hand", "asset"], ["1010", "Bank (unassigned)", "asset"], ["1200", "Accounts receivable", "asset"], ["4000", "Professional fees", "income"], ["5000", "Salaries and wages", "expense"],
].map(([code, name, kind], i) => ({ id: i + 1, code, name, kind, is_system: true, active: true, balance_paise: 1234500 * (i + 1), balance_display: inr(1234500 * (i + 1)) }));

type Row = Record<string, unknown>;
type Q = { search?: string; page?: number; page_size?: number; sort?: string; dir?: string; filters?: Record<string, string> };

function pageOf(all: Row[], q: Q, text: (r: Row) => string, sums: (rows: Row[]) => Record<string, number> = () => ({})) {
  const needle = (q.search ?? "").toLowerCase();
  let rows = all.filter((r) => !needle || text(r).toLowerCase().includes(needle));
  rows = rows.filter((r) => Object.entries(q.filters ?? {}).every(([k, v]) => !v || k === "from" || k === "to" || String(r[k === "source" ? "source_type" : k] ?? r["effective_" + k] ?? "") === v));
  const key = q.sort && q.sort !== "date" ? q.sort : "";
  if (key) rows = [...rows].sort((a, b) => String(a[key] ?? "").localeCompare(String(b[key] ?? ""), undefined, { numeric: true }) * (q.dir === "desc" ? -1 : 1));
  const size = q.page_size || 50;
  const start = (q.page ?? 0) * size;
  return { rows: rows.slice(start, start + size), total: rows.length, sums: sums(rows) };
}

const manyTxns = Array.from({ length: 230 }, (_, i) => ({
  ...txns[i % txns.length], id: i + 1, txn_date: `2026-${String(1 + (i % 9)).padStart(2, "0")}-${String(1 + (i % 27)).padStart(2, "0")}`,
  narration: `${txns[i % txns.length].narration} #${i}`,
}));

// Preview-only company state: add ?start to the address to see the company chooser first.
let company: { name: string; path: string; uuid: string } | null = location.search.includes("start") ? null : { name: "Studio X Architects", path: "D:\Companies\Studio X.adms", uuid: "0a1b2c3d-0000-4000-8000-000000000001" };
let signedIn = !location.search.includes("start");

const templates = [
  { id: 1, kind: "letter", name: "Standard letter", family: "administration", layout: "letter", description: "A plain outward letter.", built_in: true, archived: false, format: "wording", latest_version: 1, latest_version_id: 3, body: "Dear Sir / Madam,\n\nSub: {{document.subject}}" },
  { id: 2, kind: "letter", name: "Meeting minutes", family: "consultancy", layout: "record", description: "Minutes with attendees, decisions and actions.", built_in: true, archived: false, format: "wording", latest_version: 1, latest_version_id: 4, body: "Project: {{project.name}}\n\n# Present\n\n[[names]]" },
  { id: 3, kind: "letter", name: "Outstanding fee reminder", family: "billing", layout: "letter", description: "A courteous reminder for fees past their due date.", built_in: true, archived: false, format: "wording", latest_version: 1, latest_version_id: 5, body: "Dear Sir / Madam," },
  { id: 5, kind: "letter", name: "Consultancy proposal (HTML)", family: "consultancy", layout: "letter", description: "A proposal with a fee schedule.", built_in: true, archived: false, format: "html", latest_version: 1, latest_version_id: 7, body: "" },
  { id: 6, kind: "letter", name: "Our proposal", family: "consultancy", layout: "letter", description: "", built_in: false, archived: false, format: "html", latest_version: 2, latest_version_id: 8, body: "" },
  { id: 4, kind: "letter", name: "Our site note", family: "consultancy", layout: "record", description: "", built_in: false, archived: false, format: "wording", latest_version: 2, latest_version_id: 6, body: "Visit on [[date]]" },
];

function handle(cmd: string, args: Args): unknown {
  switch (cmd) {
    case "plugin:dialog|save": return "D:\Companies\New Studio.adms";
    case "plugin:dialog|open": return "D:\Companies\Studio X.adms";
    case "company_recent_list": return [
      { name: "Studio X Architects", path: "D:\Companies\Studio X.adms", uuid: "0a1b2c3d-0000-4000-8000-000000000001", last_opened: 1791000000, exists: true },
      { name: "Old Practice", path: "E:\Archive\Old Practice.adms", uuid: "0a1b2c3d-0000-4000-8000-000000000002", last_opened: 1770000000, exists: false },
    ];
    case "company_legacy_info": return null;
    case "company_validate": return { name: "Studio X Architects", path: String(args?.path), uuid: "0a1b2c3d-0000-4000-8000-000000000001", schema_version: 13, needs_upgrade: false };
    case "company_open": company = { name: "Studio X Architects", path: String(args?.path), uuid: "0a1b2c3d-0000-4000-8000-000000000001" }; signedIn = false; return company;
    case "company_create": company = { name: String(args?.name), path: String(args?.path), uuid: "0a1b2c3d-0000-4000-8000-000000000003" }; return [company, "ABCD-EFGH-JKLM-NPQR"];
    case "company_close": company = null; signedIn = false; return null;
    case "auth_login": signedIn = true; return { username: "admin", role: "Owner" };
    case "auth_status": return { company, setup_required: false, locked: false, user: signedIn ? { username: "admin", role: "Owner" } : null };
    case "company_get_profile": return { name: "Studio X Architects", address: "Mumbai", email: "", phone: "", pan: "", gstin: "", coa_registration: "CA/2001/12345", bank_details: "", gst_scheme: "regular", upi_id: "studio@bank", architect_name: "A. Architect", letterhead_style: "classic" };
    case "client_list": return (args?.includeArchived ? clients : clients.filter((c) => !c.archived));
    case "project_list": return projects;
    case "invoice_list": return invoices;
    case "bank_account_list": return [{ id: 1, name: "HDFC current", bank_name: "HDFC", account_holder: "Studio X", account_number: "5010012345", ifsc: "HDFC0001", is_primary: true, active: true }];
    case "bank_summary": return { unmatched: 4, partial: 3, matched: 4, ignored: 3, duplicate: 0, rejected_rows: 0, unallocated_credits_display: inr(3240000) };
    case "bank_txn_list": return txns;
    case "bank_txn_page": return pageOf(manyTxns as unknown as Row[], (args?.query ?? {}) as Q, (r) => String(r.narration) + String(r.reference), (rows) => ({ debit: rows.reduce((n, r) => n + Number(r.debit_paise), 0), credit: rows.reduce((n, r) => n + Number(r.credit_paise), 0) }));
    case "journal_page": return pageOf(journals as unknown as Row[], (args?.query ?? {}) as Q, (r) => String(r.narration) + String(r.number), (rows) => ({ amount: rows.reduce((n, r) => n + Number(r.total_paise), 0) }));
    case "invoice_page": return pageOf(invoices.map((i) => ({ ...i, effective_status: i.status === "draft" ? "draft" : i.kind === "proforma" ? "issued" : i.balance_paise <= 0 ? "paid" : i.due_date && i.due_date < "2026-10-12" ? "overdue" : i.paid_paise > 0 ? "partial" : "issued" })) as unknown as Row[], (args?.query ?? {}) as Q, (r) => String(r.number) + String(r.client_name), () => ({ billed: 22600000, outstanding: 7440000 }));
    case "audit_page": return pageOf(Array.from({ length: 120 }, (_, i) => ({ id: i + 1, at: "2026-10-12 09:00:00", user: "admin", event: i % 2 ? "invoice_issued" : "login_ok", detail: `detail ${i}` })) as Row[], (args?.query ?? {}) as Q, (r) => String(r.event) + String(r.detail));
    case "doc_page": return pageOf([] as Row[], (args?.query ?? {}) as Q, () => "");
    case "bank_rejected_list": return [];
    case "journal_list": return journals;
    case "account_list": return accounts;
    case "period_list": return [{ fy: "2026-27", start_date: "2026-04-01", end_date: "2027-03-31", status: "open", journals: 12 }];
    case "doc_list": return [
      { id: 1, kind: "agreement", number: "AGR/2026-27/0001", status: "signed", party_name: "", subject: "Consultancy agreement", reference: "", doc_date: "2026-09-01", project_name: "Baner Villa", client_name: "Acme Developers", signed: true },
      { id: 2, kind: "letter_out", number: "OUT/2026-27/0004", status: "issued", party_name: "Municipal Corporation", subject: "Drawings submitted for approval", reference: "SK/12", doc_date: "2026-10-09", project_name: "Baner Villa", client_name: "Acme Developers", signed: false },
      { id: 3, kind: "letter_out", number: null, status: "draft", party_name: "", subject: "Site visit follow-up", reference: "", doc_date: "2026-10-12", project_name: "", client_name: "", signed: false },
    ];
    case "template_list": return templates.filter((t) => args?.includeArchived || !t.archived);
    case "template_duplicate": templates.push({ ...templates[0], id: templates.length + 1, name: String(args?.name), built_in: false, latest_version: 1, latest_version_id: 100 + templates.length }); return templates.length;
    case "template_set_archived": { const t = templates.find((x) => x.id === Number(args?.templateId)); if (t) t.archived = Boolean(args?.archived); return null; }
    case "studio_fields": return [
      { path: "company.name", group: "Company", label: "Firm name", kind: "text", required: true, example: "Studio Meridian Architects", available: "Always" },
      { path: "client.name", group: "Client", label: "Client name", kind: "text", required: true, example: "Acme Developers", available: "When a project is selected" },
      { path: "fee_items", group: "Fee", label: "Fee milestones (repeat)", kind: "list", required: false, example: "", available: "When a milestone schedule is entered" },
      { path: "document.body", group: "Document", label: "Document text", kind: "block", required: false, example: "Paragraphs", available: "When entered" },
    ];
    case "studio_open": { const t = templates.find((x) => x.id === Number(args?.templateId))!; return { id: t.id, name: t.name, family: t.family, kind: t.kind, built_in: t.built_in, archived: t.archived, published_version: t.latest_version, has_draft: false, html: "<h1>{{ document.title | default \"Proposal\" }}</h1>\n<p>{{ client.name }}</p>\n<main>{{ document.body }}</main>", css: "@page { size: A4; margin: 18mm 16mm; }\nh1 { font-size: 22pt; }", versions: [{ version: t.latest_version, published_at: "2026-10-05 10:00:00", change_summary: "Built-in original" }] }; }
    case "studio_validate": return String(args?.html).includes("nope") ? [{ severity: "error", source: "html", line: 1, col: 1, message: "Unknown field “nope”. Pick one from the Fields panel." }] : [];
    case "studio_preview": return { pdf_base64: "", pages: 2, landscape: false, diagnostics: [], missing_required: [] };
    case "studio_save_draft": case "studio_discard_draft": case "studio_restore": return null;
    case "studio_publish": return 3;
    case "studio_create": return 6;
    case "template_preview": return { pdf_base64: "", warnings: ["The company profile has no COA registration number; it will be left out of issued documents.", "2 prompts in [[double brackets]] must be replaced or deleted in each document before it can be issued."] };
    case "audit_list": return Array.from({ length: 8 }, (_, i) => ({ at: `2026-10-12 09:${10 + i}:00`, user: "admin", event: i % 2 ? "invoice_issued" : "login_ok", detail: i % 2 ? `invoice ${i} as INV/2026-27/000${i}` : "" }));
    case "user_list": return [{ id: 1, username: "admin", role: "owner", disabled: false }, { id: 2, username: "accounts", role: "accounts", disabled: false }];
    case "security_get": return 5;
    case "expense_list": case "income_list": case "payroll_list": case "asset_list": case "vendor_list": case "property_list": return [];
    default:
      if (cmd.endsWith("_list")) return [];
      throw "Not available in the browser preview.";
  }
}

export function installMock() {
  const w = window as unknown as { __TAURI_INTERNALS__?: unknown };
  if (w.__TAURI_INTERNALS__) return;
  w.__TAURI_INTERNALS__ = {
    invoke: async (cmd: string, args: Args) => handle(cmd, args),
    transformCallback: () => 0,
  };
}
