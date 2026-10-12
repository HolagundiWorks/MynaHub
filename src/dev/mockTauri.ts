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

function handle(cmd: string, args: Args): unknown {
  switch (cmd) {
    case "auth_status": return { setup_required: false, locked: false, user: { username: "admin", role: "Owner" } };
    case "company_get_profile": return { name: "Studio X Architects", address: "Mumbai", email: "", phone: "", pan: "", gstin: "", coa_registration: "CA/2001/12345", bank_details: "", gst_scheme: "regular", upi_id: "studio@bank", architect_name: "A. Architect" };
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
    case "template_list": return [{ id: 1, kind: "letter", name: "Standard letter", latest_version: 2, latest_version_id: 3, body: "" }];
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
