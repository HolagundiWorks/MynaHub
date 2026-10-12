import { invoke } from "@tauri-apps/api/core";

export interface SessionInfo {
  username: string;
  role: string;
}

export interface AuthStatus {
  setup_required: boolean;
  locked: boolean;
  user: SessionInfo | null;
}

export interface CompanyProfile {
  name: string;
  address: string;
  email: string;
  phone: string;
  pan: string;
  gstin: string;
  coa_registration: string;
  bank_details: string;
  gst_scheme: "none" | "composition" | "regular";
  upi_id: string;
  architect_name: string;
}

export interface MilestoneInput {
  name: string;
  percent: string;
}

export interface DocInput {
  id: number | null;
  kind: "letter_out" | "letter_in" | "agreement";
  project_id: number | null;
  party_name: string;
  party_address: string;
  subject: string;
  reference: string;
  doc_date: string;
  body: string;
  template_version_id: number | null;
  fee: string;
  milestones: MilestoneInput[];
}

export interface MilestoneRow {
  id: number;
  name: string;
  percent: string;
  amount_display: string;
  invoice_id: number | null;
  invoice_number: string | null;
}

export interface DocDetail {
  status: string;
  number: string | null;
  revision_of: number | null;
  input: DocInput;
  milestone_rows: MilestoneRow[];
}

export interface DocRow {
  id: number;
  kind: string;
  number: string | null;
  status: string;
  party_name: string;
  subject: string;
  reference: string;
  doc_date: string;
  project_name: string;
  client_name: string;
  signed: boolean;
}

export interface InwardInput {
  project_id: number | null;
  party_name: string;
  subject: string;
  reference: string;
  doc_date: string;
  notes: string;
}

export interface AttachmentRow {
  id: number;
  kind: string;
  filename: string;
  bytes: number;
  uploaded_at: string;
}

export interface TemplateRow {
  id: number;
  kind: string;
  name: string;
  latest_version: number;
  latest_version_id: number;
  body: string;
}

export interface VersionRow {
  version: number;
  published_at: string;
  body: string;
}

export interface CreditNoteRow {
  id: number;
  number: string;
  issue_date: string;
  reason: string;
  total_display: string;
}

export interface Client {
  id: number | null;
  name: string;
  contact_person: string;
  email: string;
  phone: string;
  address: string;
  pan: string;
  gstin: string;
  archived: boolean;
}

export interface Project {
  id: number | null;
  client_id: number;
  code: string;
  name: string;
  site_address: string;
  status: "active" | "on_hold" | "completed";
  archived: boolean;
  client_name: string;
}

export interface LineInput {
  description: string;
  qty: string;
  rate: string;
}

export interface InvoiceInput {
  id: number | null;
  project_id: number;
  kind: "tax_invoice" | "proforma";
  issue_date: string;
  due_date: string;
  gst_mode: "none" | "intra" | "inter";
  gst_rate: string;
  notes: string;
  lines: LineInput[];
}

export interface InvoiceRow {
  id: number;
  number: string | null;
  kind: string;
  status: string;
  client_name: string;
  project_name: string;
  issue_date: string;
  total_display: string;
  paid_display: string;
  total_paise: number;
  tds_display: string;
  balance_paise: number;
  balance_display: string;
  credited_display: string;
  paid_paise: number;
  tds_paise: number;
  credited_paise: number;
  due_date: string;
  converted_to: string | null;
  from_proforma: string | null;
}

export interface InvoiceDetail {
  status: string;
  number: string | null;
  input: InvoiceInput;
}

export interface Totals {
  subtotal_paise: number;
  cgst_paise: number;
  sgst_paise: number;
  igst_paise: number;
  tax_paise: number;
  total_paise: number;
  composition_paise: number;
  gst_mode: string;
}

export interface ReceiptInput {
  invoice_id: number;
  received_date: string;
  amount: string;
  tds: string;
  tds_section: string;
  mode: "bank" | "upi" | "cheque" | "cash";
  reference: string;
  notes: string;
}

export interface ReceiptRow {
  id: number;
  reversed: boolean;
  number: string;
  received_date: string;
  amount_display: string;
  tds_display: string;
  tds_section: string;
  mode: string;
  reference: string;
}

export interface Balance {
  credited_display: string;
  balance_paise: number;
  total_display: string;
  received_display: string;
  tds_display: string;
  balance_display: string;
}

export interface Mapping {
  delimiter: string;
  has_header: boolean;
  date_order: "dmy" | "mdy" | "ymd";
  date: number;
  narration: number | null;
  reference: number | null;
  debit: number | null;
  credit: number | null;
  amount: number | null;
  balance: number | null;
}

export interface ParsedRow {
  row_no: number;
  txn_date: string;
  narration: string;
  reference: string;
  debit_paise: number;
  credit_paise: number;
  error: string | null;
}

export interface ImportResult {
  batch_id: number;
  imported: number;
  duplicates: number;
  rejected: number;
}

export interface TxnRow {
  id: number;
  txn_date: string;
  narration: string;
  reference: string;
  debit_display: string;
  credit_display: string;
  credit_paise: number;
  debit_paise: number;
  allocated_display: string;
  unallocated_paise: number;
  status: string;
  note: string;
  account_id: number;
  account_name: string;
}

export interface BankSummary {
  unmatched: number;
  partial: number;
  matched: number;
  ignored: number;
  duplicate: number;
  rejected_rows: number;
  unallocated_credits_display: string;
}

export interface BankAccount {
  id: number | null;
  name: string;
  bank_name: string;
  account_holder: string;
  account_number: string;
  ifsc: string;
  is_primary: boolean;
  active: boolean;
}

export interface UnlinkedReceipt {
  id: number;
  number: string;
  invoice_number: string;
  client_name: string;
  received_date: string;
  amount_paise: number;
  amount_display: string;
  reference: string;
  mode: string;
}

export interface ManualEntry {
  account_id: number;
  txn_date: string;
  narration: string;
  reference: string;
  direction: "credit" | "debit";
  amount: string;
}

export interface RejectedRow {
  batch_id: number;
  row_no: number;
  raw_row: string;
  reason: string;
}

export interface Suggestion {
  invoice_id: number;
  number: string;
  client_name: string;
  balance_paise: number;
  balance_display: string;
  score: number;
  reasons: string[];
  tds_section: string | null;
  tds_amount: string | null;
}

export interface Allocation {
  invoice_id: number;
  amount: string;
  tds: string;
  tds_section: string;
}

export interface UserRow {
  id: number;
  username: string;
  role: string;
  disabled: boolean;
}

export interface AuditRow {
  at: string;
  user: string;
  event: string;
  detail: string;
}

export interface BackupInfo {
  filename: string;
  sha256: string;
  bytes: number;
  folder: string;
}

export interface AccountRow {
  id: number;
  code: string;
  name: string;
  kind: string;
  is_system: boolean;
  active: boolean;
  balance_paise: number;
  balance_display: string;
}
export interface PeriodRow { fy: string; start_date: string; end_date: string; status: string; journals: number }
export interface JournalRow { id: number; number: string; date: string; narration: string; source_type: string; total_display: string; total_paise: number; reversed: boolean }
export interface JournalLineRow { account: string; memo: string; debit_display: string; credit_display: string }
export interface JournalLineInput { account_id: number; debit: string; credit: string; memo: string }
export interface TbRow { code: string; name: string; kind: string; debit_display: string; credit_display: string }
export interface TrialBalance { rows: TbRow[]; total_debit: string; total_credit: string; balanced: boolean }
export interface LedgerRow { date: string; number: string; narration: string; debit_display: string; credit_display: string; balance_display: string }
export interface AccountLedger { code: string; name: string; opening_display: string; closing_display: string; rows: LedgerRow[] }
export interface AmountRow { code: string; name: string; amount_display: string }
export interface ProfitAndLoss { income: AmountRow[]; expenses: AmountRow[]; total_income: string; total_expenses: string; profit_display: string }
export interface BalanceSheet { assets: AmountRow[]; liabilities: AmountRow[]; equity: AmountRow[]; total_assets: string; total_liabilities_equity: string; balanced: boolean }
export interface TaxSummary { output_cgst: string; output_sgst: string; output_igst: string; input_credit: string; net_gst_payable: string; composition_tax: string; tds_receivable: string; tds_payable: string }
export interface ArCheck { ledger_display: string; invoices_display: string; agrees: boolean }
export interface TaxEstimate { profit_before_tax: string; tax_display: string; cess_display: string; total_display: string }
export interface Vendor { id: number | null; name: string; pan: string; active: boolean }
export interface Property { id: number | null; name: string; kind: "commercial" | "residential"; address: string; active: boolean }
export interface ExpenseInput { date: string; vendor_id: number | null; bill_ref: string; description: string; expense_account_id: number; amount: string; gst: string; tds: string; paid_via: string }
export interface ExpenseRow { id: number; date: string; vendor: string; description: string; account: string; total_display: string; total_paise: number; paid_via: string; settled: boolean; voided: boolean }
export interface IncomeInput { date: string; kind: "rent" | "interest" | "other"; property_id: number | null; payer: string; description: string; amount: string; gst: string; gst_mode: "intra" | "inter"; tds: string; received_via: string }
export interface IncomeRow { id: number; date: string; kind: string; property: string; payer: string; amount_display: string; amount_paise: number; tds_display: string; tds_paise: number; voided: boolean }
export interface PayrollInput { date: string; employee: string; period_label: string; gross: string; tds: string; paid_via: string; pf_employee: string; pf_employer: string; esi_employee: string; esi_employer: string; professional_tax: string }
export interface PayrollSuggestion { pf_employee: string; pf_employer: string; esi_employee: string; esi_employer: string }
export interface Slab { upto: string; rate_percent: string }
export interface PayrollRow { id: number; date: string; employee: string; period_label: string; gross_display: string; gross_paise: number; tds_display: string; net_display: string; net_paise: number; voided: boolean }
export interface AssetInput { name: string; purchase_date: string; cost: string; rate_percent: string; paid_via: string }
export interface AssetRow { id: number; name: string; purchase_date: string; cost_display: string; cost_paise: number; rate: string; accumulated_display: string; book_value_display: string; book_value_paise: number }

export interface PageQuery {
  search: string;
  sort: string;
  dir: string;
  page: number;
  page_size: number;
  filters: Record<string, string>;
}

export interface Page<T> {
  rows: T[];
  /** Rows matching the search and filters, across all pages. */
  total: number;
  /** Totals over all matching rows. */
  sums: Record<string, number>;
}

export type InvoiceRegisterRow = InvoiceRow & { effective_status: string };
export interface AuditPageRow { id: number; at: string; user: string; event: string; detail: string }

// The only bridge to the native layer: a small, typed set of commands.
export const api = {
  authStatus: () => invoke<AuthStatus>("auth_status"),
  authSetup: (username: string, password: string, confirm: string) =>
    invoke<string>("auth_setup", { username, password, confirm }),
  authLogin: (username: string, password: string) =>
    invoke<SessionInfo>("auth_login", { username, password }),
  authUnlock: (password: string) => invoke<void>("auth_unlock", { password }),
  authLock: () => invoke<void>("auth_lock"),
  authLogout: () => invoke<void>("auth_logout"),
  companyGet: () => invoke<CompanyProfile>("company_get_profile"),
  companySave: (profile: CompanyProfile) =>
    invoke<void>("company_save_profile", { profile }),
  clientList: (includeArchived: boolean) => invoke<Client[]>("client_list", { includeArchived }),
  clientSave: (client: Client) => invoke<number>("client_save", { client }),
  clientSetArchived: (id: number, archived: boolean) =>
    invoke<void>("client_set_archived", { id, archived }),
  projectList: (clientId: number | null, includeArchived: boolean) =>
    invoke<Project[]>("project_list", { clientId, includeArchived }),
  projectSave: (project: Project) => invoke<number>("project_save", { project }),
  projectSetArchived: (id: number, archived: boolean) =>
    invoke<void>("project_set_archived", { id, archived }),
  invoiceList: () => invoke<InvoiceRow[]>("invoice_list"),
  invoiceGet: (id: number) => invoke<InvoiceDetail>("invoice_get", { id }),
  invoiceCalculate: (input: InvoiceInput) => invoke<Totals>("invoice_calculate", { input }),
  invoiceSaveDraft: (input: InvoiceInput) => invoke<number>("invoice_save_draft", { input }),
  invoiceIssue: (id: number) => invoke<string>("invoice_issue", { id }),
  docFields: () => invoke<string[]>("doc_fields"),
  docList: (kind: string | null, q: string | null) => invoke<DocRow[]>("doc_list", { kind, q }),
  docGet: (id: number) => invoke<DocDetail>("doc_get", { id }),
  docSaveDraft: (input: DocInput) => invoke<number>("doc_save_draft", { input }),
  docIssue: (id: number) => invoke<string>("doc_issue", { id }),
  docRegisterInward: (input: InwardInput) => invoke<string>("doc_register_inward", { input }),
  docPdf: (id: number) => invoke<string>("doc_pdf", { id }),
  docRevise: (id: number) => invoke<number>("doc_revise", { id }),
  docMarkSigned: (id: number) => invoke<void>("doc_mark_signed", { id }),
  docCancel: (id: number, reason: string) => invoke<void>("doc_cancel", { id, reason }),
  docBillMilestone: (milestoneId: number, invoiceDate: string) => invoke<number>("doc_bill_milestone", { milestoneId, invoiceDate }),
  docAttach: (docId: number, kind: "attachment" | "signed_copy", filename: string, base64Data: string) =>
    invoke<number>("doc_attach", { docId, kind, filename, base64Data }),
  docAttachments: (docId: number) => invoke<AttachmentRow[]>("doc_attachments", { docId }),
  docAttachmentData: (id: number) => invoke<{ filename: string; base64_data: string }>("doc_attachment_data", { id }),
  templateList: (kind: string | null) => invoke<TemplateRow[]>("template_list", { kind }),
  templateVersions: (templateId: number) => invoke<VersionRow[]>("template_versions_list", { templateId }),
  templateCreate: (kind: string, name: string, body: string) => invoke<number>("template_create", { kind, name, body }),
  templatePublish: (templateId: number, body: string) => invoke<number>("template_publish", { templateId, body }),
  accountList: (asOf: string) => invoke<AccountRow[]>("account_list", { asOf }),
  accountCreate: (code: string, name: string, kind: string) => invoke<number>("account_create", { code, name, kind }),
  accountSetActive: (id: number, active: boolean) => invoke<void>("account_set_active", { id, active }),
  periodList: () => invoke<PeriodRow[]>("period_list"),
  periodSet: (fy: string, closed: boolean, password: string) => invoke<void>("period_set", { fy, closed, password }),
  ledgerBackfill: () => invoke<[number, number, number]>("ledger_backfill"),
  journalPost: (date: string, narration: string, lines: JournalLineInput[]) => invoke<number>("journal_post", { date, narration, lines }),
  journalReverse: (id: number, reason: string, password: string) => invoke<number>("journal_reverse", { id, reason, password }),
  journalList: (from: string, to: string, limit: number) => invoke<JournalRow[]>("journal_list", { from, to, limit }),
  journalLines: (id: number) => invoke<JournalLineRow[]>("journal_lines", { id }),
  reportTrialBalance: (asOf: string) => invoke<TrialBalance>("report_trial_balance", { asOf }),
  reportLedger: (accountId: number, from: string, to: string) => invoke<AccountLedger>("report_ledger", { accountId, from, to }),
  reportPnl: (from: string, to: string) => invoke<ProfitAndLoss>("report_pnl", { from, to }),
  reportBalanceSheet: (asOf: string) => invoke<BalanceSheet>("report_balance_sheet", { asOf }),
  reportTaxSummary: (from: string, to: string) => invoke<TaxSummary>("report_tax_summary", { from, to }),
  reportArCheck: (asOf: string) => invoke<ArCheck>("report_ar_check", { asOf }),
  reportTaxEstimate: (from: string, to: string, ratePercent: string) => invoke<TaxEstimate>("report_tax_estimate", { from, to, ratePercent }),
  reportTaxSlabs: (from: string, to: string, slabs: Slab[], cessPercent: string) => invoke<TaxEstimate>("report_tax_slabs", { from, to, slabs, cessPercent }),
  payrollSuggest: (basic: string, gross: string, pfRate: string, pfCeiling: string, esiThreshold: string, esiEeRate: string, esiErRate: string) =>
    invoke<PayrollSuggestion>("payroll_suggest", { basic, gross, pfRate, pfCeiling, esiThreshold, esiEeRate, esiErRate }),
  vendorList: () => invoke<Vendor[]>("vendor_list"),
  vendorSave: (vendor: Vendor) => invoke<number>("vendor_save", { vendor }),
  propertyList: () => invoke<Property[]>("property_list"),
  propertySave: (property: Property) => invoke<number>("property_save", { property }),
  expenseList: () => invoke<ExpenseRow[]>("expense_list"),
  expenseRecord: (expense: ExpenseInput) => invoke<number>("expense_record", { expense }),
  expenseSettle: (id: number, date: string, paidVia: string) => invoke<void>("expense_settle", { id, date, paidVia }),
  expenseVoid: (id: number, reason: string) => invoke<void>("expense_void", { id, reason }),
  incomeList: () => invoke<IncomeRow[]>("income_list"),
  incomeRecord: (income: IncomeInput) => invoke<number>("income_record", { income }),
  incomeVoid: (id: number, reason: string) => invoke<void>("income_void", { id, reason }),
  payrollList: () => invoke<PayrollRow[]>("payroll_list"),
  payrollRecord: (payroll: PayrollInput) => invoke<number>("payroll_record", { payroll }),
  payrollVoid: (id: number, reason: string) => invoke<void>("payroll_void", { id, reason }),
  assetList: () => invoke<AssetRow[]>("asset_list"),
  assetAdd: (asset: AssetInput) => invoke<number>("asset_add", { asset }),
  assetDepreciate: (fy: string) => invoke<number>("asset_depreciate", { fy }),
  taxPayment: (kind: string, date: string, amount: string, paidVia: string, narration: string) =>
    invoke<number>("tax_payment", { kind, date, amount, paidVia, narration }),
  bankClassify: (txnId: number, accountId: number, tds: string) => invoke<number>("bank_classify", { txnId, accountId, tds }),
  bankTxnPage: (query: PageQuery) => invoke<Page<TxnRow>>("bank_txn_page", { query }),
  journalPage: (query: PageQuery) => invoke<Page<JournalRow>>("journal_page", { query }),
  invoicePage: (query: PageQuery) => invoke<Page<InvoiceRegisterRow>>("invoice_page", { query }),
  docPage: (query: PageQuery) => invoke<Page<DocRow>>("doc_page", { query }),
  auditPage: (query: PageQuery) => invoke<Page<AuditPageRow>>("audit_page", { query }),
  invoicePdf: (id: number) => invoke<string>("invoice_pdf", { id }),
  invoiceCancel: (id: number, reason: string) => invoke<void>("invoice_cancel", { id, reason }),
  creditNoteIssue: (invoiceId: number, issueDate: string, reason: string, lines: LineInput[]) =>
    invoke<string>("credit_note_issue", { invoiceId, issueDate, reason, lines }),
  creditNoteList: (invoiceId: number) => invoke<CreditNoteRow[]>("credit_note_list", { invoiceId }),
  creditNotePdf: (id: number) => invoke<string>("credit_note_pdf_data", { id }),
  receiptReverse: (receiptId: number, reason: string) => invoke<void>("receipt_reverse", { receiptId, reason }),
  receiptPdf: (receiptId: number) => invoke<string>("receipt_pdf", { receiptId }),
  bankUnmatch: (txnId: number, reason: string) => invoke<void>("bank_unmatch", { txnId, reason }),
  invoiceConvertProforma: (id: number, today: string) => invoke<number>("invoice_convert_proforma", { id, today }),
  receiptBalance: (invoiceId: number) => invoke<Balance>("receipt_balance", { invoiceId }),
  receiptList: (invoiceId: number) => invoke<ReceiptRow[]>("receipt_list", { invoiceId }),
  receiptSuggestTds: (invoiceId: number, rate: string) => invoke<string>("receipt_suggest_tds", { invoiceId, rate }),
  receiptRecord: (input: ReceiptInput) => invoke<string>("receipt_record", { input }),
  changePassword: (current: string, newPw: string, confirm: string) => invoke<void>("auth_change_password", { current, new: newPw, confirm }),
  recover: (username: string, code: string, newPw: string, confirm: string) => invoke<string>("auth_recover", { username, code, new: newPw, confirm }),
  securityGet: () => invoke<number>("security_get"),
  securitySetIdle: (minutes: number, password: string) => invoke<void>("security_set_idle_minutes", { minutes, password }),
  userList: () => invoke<UserRow[]>("user_list"),
  userCreate: (username: string, password: string, role: string, adminPassword: string) => invoke<number>("user_create", { username, password, role, adminPassword }),
  userUpdate: (id: number, role: string, disabled: boolean, adminPassword: string) => invoke<void>("user_update", { id, role, disabled, adminPassword }),
  auditList: (limit: number) => invoke<AuditRow[]>("audit_list", { limit }),
  backupCreate: (password: string) => invoke<BackupInfo>("backup_create", { password }),
  backupList: () => invoke<{ filename: string; bytes: number }[]>("backup_list"),
  backupRestore: (filename: string, password: string) => invoke<string>("backup_restore", { filename, password }),
  bankPreview: (text: string, mapping: Mapping) => invoke<ParsedRow[]>("bank_csv_preview", { text, mapping }),
  bankImport: (accountId: number, filename: string, text: string, mapping: Mapping) => invoke<ImportResult>("bank_import_commit", { accountId, filename, text, mapping }),
  bankTxns: (status: string | null, accountId: number | null) => invoke<TxnRow[]>("bank_txn_list", { status, accountId }),
  bankSummary: (accountId: number | null) => invoke<BankSummary>("bank_summary", { accountId }),
  bankAccounts: () => invoke<BankAccount[]>("bank_account_list"),
  bankAccountSave: (account: BankAccount) => invoke<number>("bank_account_save", { account }),
  bankManualEntry: (entry: ManualEntry) => invoke<number>("bank_manual_entry", { entry }),
  bankUnlinked: () => invoke<UnlinkedReceipt[]>("bank_unlinked_receipts"),
  bankLinkReceipts: (txnId: number, receiptIds: number[]) => invoke<void>("bank_link_receipts", { txnId, receiptIds }),
  bankRejected: () => invoke<RejectedRow[]>("bank_rejected_list"),
  bankSuggest: (txnId: number) => invoke<Suggestion[]>("bank_txn_suggest", { txnId }),
  bankReconcile: (txnId: number, allocations: Allocation[]) => invoke<string[]>("bank_reconcile", { txnId, allocations }),
  bankIgnore: (txnId: number, note: string, undo: boolean) => invoke<void>("bank_txn_ignore", { txnId, note, undo }),
};

export const errorText = (e: unknown) => (typeof e === "string" ? e : "Something went wrong.");
