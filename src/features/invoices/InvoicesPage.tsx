import { useCallback, useEffect, useRef, useState } from "react";
import {
  Button, Form, InlineNotification, Select, SelectItem, Stack, Table, TableBody, TableCell,
  TableHead, TableHeader, TableRow, TextArea, TextInput,
} from "@carbon/react";
import {
  api, errorText, type InvoiceInput, type InvoiceRegisterRow, type Project, type Totals,
} from "../../api";
import PaymentPanel from "./PaymentPanel";
import CreditNotePanel from "./CreditNotePanel";
import PdfViewer from "../../components/PdfViewer";
import PageHeader from "../../components/PageHeader";
import FilterSelect from "../../components/FilterSelect";
import { askReason, confirmDialog } from "../../components/dialogs";
import StatusBadge from "../../components/StatusBadge";
import DataTable, { type Column } from "../../components/data-table/DataTable";
import { useServerTable } from "../../components/data-table/useServerTable";
import RowMenu, { type MenuItem } from "../../components/data-table/RowMenu";
import { Add, Money, Save } from "@carbon/icons-react";
import { DraftGuard, DraftSave } from "../../app/unsaved";
import { useCreateIntent } from "../../app/intent";

const today = () => new Date().toISOString().slice(0, 10);

const blank = (projectId = 0, kind: InvoiceInput["kind"] = "tax_invoice"): InvoiceInput => ({
  id: null, project_id: projectId, kind, issue_date: today(), due_date: "",
  gst_mode: "none", gst_rate: "18", notes: "", lines: [{ description: "", qty: "1", rate: "" }],
});

const rupees = (paise: number) => (paise / 100).toLocaleString("en-IN", { style: "currency", currency: "INR" });

export default function InvoicesPage() {
  const [projects, setProjects] = useState<Project[]>([]);
  const [draft, setDraft] = useState<InvoiceInput | null>(null);
  const [scheme, setScheme] = useState<"none" | "composition" | "regular">("none");
  const [paying, setPaying] = useState<InvoiceRegisterRow | null>(null);
  const [crediting, setCrediting] = useState<InvoiceRegisterRow | null>(null);
  const [status, setStatus] = useState<{ status: string; number: string | null } | null>(null);
  const [totals, setTotals] = useState<Totals | null>(null);
  const [calcError, setCalcError] = useState("");
  const [error, setError] = useState("");
  const [pdf, setPdf] = useState<string | null>(null);
  const [cleanToken, setCleanToken] = useState(0);
  const calcSeq = useRef(0);

  const [kindFilter, setKindFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState("");

  const table = useServerTable<InvoiceRegisterRow>({
    id: "invoices", fetch: api.invoicePage, filters: { kind: kindFilter, status: statusFilter }, defaultSort: { id: "date", dir: "desc" },
  });
  const loadSupport = useCallback(() => {
    Promise.all([api.projectList(null, false), api.companyGet()])
      .then(([pr, co]) => { setProjects(pr); setScheme(co.gst_scheme); })
      .catch((e) => setError(errorText(e)));
  }, []);
  const reloadTable = table.reload;
  const load = useCallback(() => { loadSupport(); reloadTable(); }, [loadSupport, reloadTable]);
  useEffect(loadSupport, [loadSupport]);
  const openNew = useCallback(() => {
    api.projectList(null, false).then((list) => { setProjects(list); setDraft(blank(list[0]?.id ?? 0)); setStatus(null); }).catch(() => setDraft(blank()));
  }, []);
  useCreateIntent("invoices", openNew);

  // Totals always come from the native layer; the UI never does money arithmetic.
  useEffect(() => {
    if (!draft) return;
    const seq = ++calcSeq.current;
    api.invoiceCalculate(draft)
      .then((t) => { if (seq === calcSeq.current) { setTotals(t); setCalcError(""); } })
      .catch((e) => { if (seq === calcSeq.current) { setTotals(null); setCalcError(errorText(e)); } });
  }, [draft]);

  const open = async (id: number) => {
    try {
      const d = await api.invoiceGet(id);
      setDraft(d.input);
      setStatus({ status: d.status, number: d.number });
      setError("");
    } catch (e) { setError(errorText(e)); }
  };

  const close = () => { setDraft(null); setStatus(null); setPdf(null); setTotals(null); setError(""); load(); };

  const save = async (): Promise<number | null> => {
    if (!draft) return null;
    try {
      const id = await api.invoiceSaveDraft(draft);
      setDraft({ ...draft, id });
      setCleanToken((n) => n + 1);
      setError("");
      return id;
    } catch (e) { setError(errorText(e)); return null; }
  };

  if (crediting) return <CreditNotePanel invoiceId={crediting.id} number={crediting.number ?? ""} onClose={() => { setCrediting(null); load(); }} />;
  if (paying) return <PaymentPanel invoiceId={paying.id} number={paying.number ?? ""} onClose={() => { setPaying(null); load(); }} />;

  if (pdf) return <PdfViewer base64={pdf} onClose={() => setPdf(null)} />;

  if (draft) {
    const locked = status !== null && status.status !== "draft";
    const set = <K extends keyof InvoiceInput>(k: K, v: InvoiceInput[K]) => setDraft({ ...draft, [k]: v });
    const setLine = (i: number, k: "description" | "qty" | "rate", v: string) =>
      set("lines", draft.lines.map((l, j) => (j === i ? { ...l, [k]: v } : l)));
    return (
      <Form className="ao-form" onSubmit={(e) => e.preventDefault()}>
        <DraftGuard value={draft} cleanToken={cleanToken} enabled={!locked} />
        <DraftSave enabled={!locked} save={() => { void save(); }} />
        <Stack gap={5}>
          <h3>{locked ? `${draft.kind === "proforma" ? "Pro forma" : "Invoice"} ${status?.number}` : `${draft.id ? "Edit" : "New"} ${draft.kind === "proforma" ? "pro forma invoice" : "invoice"}`}</h3>
          <Select size="sm" id="i-project" labelText="Project" value={draft.project_id || ""} disabled={locked}
            onChange={(e) => set("project_id", Number(e.target.value))}>
            <SelectItem value="" text="Select a project" />
            {projects.map((p) => <SelectItem key={p.id} value={p.id!} text={`${p.client_name} — ${p.name}`} />)}
          </Select>
          <Select size="sm" id="i-kind" labelText="Document type" value={draft.kind} disabled={locked}
            onChange={(e) => set("kind", e.target.value as InvoiceInput["kind"])}>
            <SelectItem value="tax_invoice" text="Invoice" />
            <SelectItem value="proforma" text="Pro forma invoice" />
          </Select>
          <TextInput size="sm" id="i-date" type="date" labelText="Invoice date" value={draft.issue_date} readOnly={locked}
            onChange={(e) => set("issue_date", e.target.value)} />
          <TextInput size="sm" id="i-due" type="date" labelText="Due date" value={draft.due_date} readOnly={locked}
            onChange={(e) => set("due_date", e.target.value)} />
          {scheme === "regular" && (
            <Select size="sm" id="i-gst" labelText="GST (18%)" value={draft.gst_mode === "inter" ? "inter" : "intra"} disabled={locked}
              onChange={(e) => set("gst_mode", e.target.value as InvoiceInput["gst_mode"])}>
              <SelectItem value="intra" text="9% CGST + 9% SGST (same state)" />
              <SelectItem value="inter" text="18% IGST (other state)" />
            </Select>
          )}
          {scheme === "composition" && <p>Composition scheme: issued as a Bill of Supply. No GST is charged to the client.</p>}
          {scheme === "none" && <p>Not registered for GST: no tax is charged.</p>}

          <Table size="md">
            <TableHead>
              <TableRow>
                <TableHeader>Description</TableHeader>
                <TableHeader>Qty</TableHeader>
                <TableHeader>Rate (₹)</TableHeader>
                <TableHeader />
              </TableRow>
            </TableHead>
            <TableBody>
              {draft.lines.map((l, i) => (
                <TableRow key={i}>
                  <TableCell><TextInput size="sm" id={`l-d-${i}`} labelText="" hideLabel value={l.description} readOnly={locked} onChange={(e) => setLine(i, "description", e.target.value)} /></TableCell>
                  <TableCell><TextInput size="sm" id={`l-q-${i}`} labelText="" hideLabel value={l.qty} readOnly={locked} onChange={(e) => setLine(i, "qty", e.target.value)} /></TableCell>
                  <TableCell><TextInput size="sm" id={`l-r-${i}`} labelText="" hideLabel value={l.rate} readOnly={locked} onChange={(e) => setLine(i, "rate", e.target.value)} /></TableCell>
                  <TableCell>
                    {!locked && draft.lines.length > 1 && (
                      <Button kind="ghost" size="sm" onClick={() => set("lines", draft.lines.filter((_, j) => j !== i))}>Remove</Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          {!locked && (
            <div><Button kind="tertiary" size="sm" onClick={() => set("lines", [...draft.lines, { description: "", qty: "1", rate: "" }])}>Add line</Button></div>
          )}

          <TextArea id="i-notes" labelText="Notes" value={draft.notes} readOnly={locked} onChange={(e) => set("notes", e.target.value)} />

          {totals ? (
            <div>
              <div>Subtotal: {rupees(totals.subtotal_paise)}</div>
              {draft.gst_mode === "intra" && <><div>CGST: {rupees(totals.cgst_paise)}</div><div>SGST: {rupees(totals.sgst_paise)}</div></>}
              {draft.gst_mode === "inter" && <div>IGST: {rupees(totals.igst_paise)}</div>}
              <strong>Total: {rupees(totals.total_paise)}</strong>
              {scheme === "composition" && <div>Composition tax payable by the firm (6%): {rupees(totals.composition_paise)}</div>}
            </div>
          ) : (
            calcError && <p>{calcError}</p>
          )}

          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
          <div className="toolbar">
            <Button size="sm" kind="secondary" onClick={close}>{locked ? "Back" : "Cancel"}</Button>
            <div>
              {!locked && <Button renderIcon={Save} size="sm" kind="tertiary" onClick={save}>Save draft</Button>}
              <Button size="sm"
                kind="tertiary"
                onClick={async () => {
                  const id = locked ? draft.id : await save();
                  if (id == null) return;
                  try { setPdf(await api.invoicePdf(id)); } catch (e) { setError(errorText(e)); }
                }}
              >
                {locked ? "View PDF" : "Preview PDF"}
              </Button>
              {!locked && (
                <Button size="sm"
                  onClick={async () => {
                    const id = await save();
                    if (id == null) return;
                    if (!(await confirmDialog("Issue this document? It gets a number and can no longer be edited."))) return;
                    try {
                      const number = await api.invoiceIssue(id);
                      setStatus({ status: "issued", number });
                      setError("");
                    } catch (e) { setError(errorText(e)); }
                  }}
                >
                  Issue
                </Button>
              )}
            </div>
          </div>
        </Stack>
      </Form>
    );
  }

  const statusText: Record<string, string> = { completed: "Converted" };
  const rupeesPlain = (paise: number) => (paise / 100).toLocaleString("en-IN", { style: "currency", currency: "INR" });
  const isTax = (r: InvoiceRegisterRow) => r.status === "issued" && r.kind === "tax_invoice";

  const columns: Column<InvoiceRegisterRow>[] = [
    { id: "number", header: "Number", locked: true, minWidth: 150, sortValue: (r) => r.number ?? "~draft",
      render: (r) => <div>{r.number ?? "Draft"}{r.converted_to && <div className="ao-muted">Converted to {r.converted_to}</div>}{r.from_proforma && <div className="ao-muted">From {r.from_proforma}</div>}</div>,
      csv: (r) => r.number ?? "Draft" },
    { id: "type", header: "Type", sortValue: (r) => r.kind, render: (r) => (r.kind === "proforma" ? "Pro forma" : "Invoice"), csv: (r) => r.kind },
    { id: "party", header: "Client / project", minWidth: 220, sortValue: (r) => r.client_name.toLowerCase(), render: (r) => <span>{r.client_name} <span className="ao-muted">· {r.project_name}</span></span>, csv: (r) => `${r.client_name} - ${r.project_name}` },
    { id: "date", header: "Date", sortValue: (r) => r.issue_date, render: (r) => r.issue_date, csv: (r) => r.issue_date },
    { id: "due", header: "Due", defaultVisible: false, sortValue: (r) => r.due_date || "9999", render: (r) => r.due_date, csv: (r) => r.due_date },
    { id: "total", header: "Gross amount", align: "end", sortValue: (r) => r.total_paise, render: (r) => r.total_display, csv: (r) => r.total_paise / 100 },
    { id: "received", header: "Received", align: "end", sortValue: (r) => r.paid_paise, render: (r) => (isTax(r) ? r.paid_display : ""), csv: (r) => r.paid_paise / 100 },
    { id: "tds", header: "TDS", align: "end", defaultVisible: false, sortValue: (r) => r.tds_paise, render: (r) => (isTax(r) ? r.tds_display : ""), csv: (r) => r.tds_paise / 100 },
    { id: "credited", header: "Credited", align: "end", defaultVisible: false, sortValue: (r) => r.credited_paise, render: (r) => (isTax(r) ? r.credited_display : ""), csv: (r) => r.credited_paise / 100 },
    { id: "balance", header: "Balance", align: "end", sortValue: (r) => (isTax(r) ? r.balance_paise : -1), render: (r) => (isTax(r) ? <strong>{r.balance_display}</strong> : ""), csv: (r) => (isTax(r) ? r.balance_paise / 100 : "") },
    { id: "status", header: "Status", sortValue: (r) => r.effective_status, render: (r) => <StatusBadge status={r.effective_status} label={statusText[r.effective_status]} />, csv: (r) => r.effective_status },
  ];

  const menuFor = (r: InvoiceRegisterRow): MenuItem[] => {
    const items: MenuItem[] = [];
    if (r.status === "issued" && r.kind === "proforma" && !r.converted_to) {
      items.push({ label: "Convert to invoice", onClick: async () => { try { const id = await api.invoiceConvertProforma(r.id, today()); await open(id); } catch (e) { setError(errorText(e)); } } });
    }
    if (isTax(r)) items.push({ label: "Credit notes…", onClick: () => setCrediting(r) });
    if (r.status === "issued") {
      items.push({
        label: "Cancel document…", danger: true,
        onClick: async () => {
          const reason = await askReason(`Cancel ${r.number}? Enter the reason.`);
          if (!reason) return;
          try { await api.invoiceCancel(r.id, reason); load(); } catch (e) { setError(errorText(e)); }
        },
      });
    }
    return items;
  };

  return (
    <>
      <PageHeader
        title="Invoices and receipts"
        description="Pro forma and tax invoices, credit notes and payments. Issued documents are never edited; corrections are credit notes or cancellations."
        actions={
          <>
            <Button renderIcon={Add} size="sm" kind="tertiary" disabled={projects.length === 0} onClick={() => { setDraft(blank(projects[0]?.id ?? 0, "proforma")); setStatus(null); }}>New pro forma</Button>
            <Button renderIcon={Add} size="sm" disabled={projects.length === 0} onClick={() => { setDraft(blank(projects[0]?.id ?? 0)); setStatus(null); }}>New invoice</Button>
          </>
        }
      />
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      <DataTable
        id="invoices" rows={table.rows} columns={columns} rowKey={(r) => r.id} loading={table.loading} error={table.error} onRetry={table.reload}
        server={table.server}
        searchLabel="Search by number, client, project or exact amount" exportName="invoices"
        filters={
          <>
            <FilterSelect label="Filter by document type" value={kindFilter} onChange={setKindFilter} options={[["", "All documents"], ["tax_invoice", "Invoices"], ["proforma", "Pro forma"]]} />
            <FilterSelect label="Filter by status" value={statusFilter} onChange={setStatusFilter}
              options={[["", "All statuses"], ["draft", "Draft"], ["issued", "Issued"], ["partial", "Partly paid"], ["overdue", "Overdue"], ["paid", "Paid"], ["cancelled", "Cancelled"]]} />
          </>
        }
        activeFilters={[
          ...(kindFilter ? [{ label: kindFilter === "proforma" ? "Pro forma only" : "Invoices only", clear: () => setKindFilter("") }] : []),
          ...(statusFilter ? [{ label: `Status: ${statusFilter}`, clear: () => setStatusFilter("") }] : []),
        ]}
        totalsNode={<><strong>{rupeesPlain(table.sums.billed ?? 0)}</strong> billed · <strong>{rupeesPlain(table.sums.outstanding ?? 0)}</strong> outstanding (issued invoices only)</>}
        emptyTitle={projects.length === 0 ? "Create a client and a project first" : "No invoices yet"}
        emptyHint={projects.length === 0 ? "Invoices are raised against a project." : "Use New invoice to raise the first one."}
        rowActions={(r) => (
          <>
            {isTax(r) && r.balance_paise > 0 && <Button renderIcon={Money} kind="ghost" size="sm" onClick={() => setPaying(r)}>Record payment</Button>}{" "}
            <RowMenu items={[{ label: r.status === "draft" ? "Edit draft" : "Open", onClick: () => open(r.id) }, ...menuFor(r)]} />
          </>
        )}
      />
    </>
  );
}
