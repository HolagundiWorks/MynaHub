import { useCallback, useEffect, useMemo, useState } from "react";
import { Button, Checkbox, FileUploaderButton, Form, InlineNotification, Select, SelectItem, Stack, TextInput } from "@carbon/react";
import {
  api, errorText, type Allocation, type AccountRow, type BankAccount, type BankSummary, type InvoiceRow, type ManualEntry, type Mapping,
  type ParsedRow, type RejectedRow, type Suggestion, type TxnRow, type UnlinkedReceipt,
} from "../../api";
import PageHeader from "../../components/PageHeader";
import { askReason } from "../../components/dialogs";
import FilterSelect from "../../components/FilterSelect";
import StatusBadge from "../../components/StatusBadge";
import DataTable, { type Column } from "../../components/data-table/DataTable";
import RowMenu, { type MenuItem } from "../../components/data-table/RowMenu";
import { useServerTable } from "../../components/data-table/useServerTable";
import { Add, ArrowLeft, Edit, Link, Upload } from "@carbon/icons-react";

const inr = (paise: number) => (paise / 100).toLocaleString("en-IN", { style: "currency", currency: "INR" });
const TDS_SECTIONS = ["194J", "194C-1", "194C-2", "194M", "Other"];

const Err = ({ text }: { text: string }) => (text ? <InlineNotification kind="error" title={text} hideCloseButton lowContrast /> : null);
const Okay = ({ text }: { text: string }) => (text ? <InlineNotification kind="success" title={text} hideCloseButton lowContrast /> : null);

function AccountSelect({ id, label, value, accounts, onChange }: { id: string; label: string; value: number; accounts: BankAccount[]; onChange: (v: number) => void }) {
  return (
    <Select size="sm" id={id} labelText={label} value={value || ""} onChange={(e) => onChange(Number(e.target.value))}>
      <SelectItem value="" text="Select an account" />
      {accounts.filter((a) => a.active).map((a) => <SelectItem key={a.id} value={a.id!} text={a.name} />)}
    </Select>
  );
}

// ============================================================ import

const splitLine = (line: string, delimiter: string) => line.split(delimiter).map((c) => c.replace(/^"|"$/g, "").trim());

function guessMapping(headers: string[], delimiter: string): Mapping {
  const find = (...keys: string[]) => {
    const i = headers.findIndex((h) => keys.some((k) => h.toLowerCase().includes(k)));
    return i >= 0 ? i : null;
  };
  return {
    delimiter, has_header: true, date_order: "dmy",
    date: find("date") ?? 0,
    narration: find("narration", "description", "particulars", "remarks"),
    reference: find("ref", "utr", "chq", "cheque"),
    debit: find("debit", "withdraw", "dr"),
    credit: find("credit", "deposit", "cr"),
    amount: null,
    balance: find("balance"),
  };
}

function ColumnPick({ id, label, value, headers, required, onChange }: { id: string; label: string; value: number | null; headers: string[]; required?: boolean; onChange: (v: number | null) => void }) {
  return (
    <Select id={id} labelText={label} size="md" value={value ?? ""} onChange={(e) => onChange(e.target.value === "" ? null : Number(e.target.value))}>
      {!required && <SelectItem value="" text="— none —" />}
      {headers.map((h, i) => <SelectItem key={i} value={i} text={`${i + 1}. ${h || "(blank)"}`} />)}
    </Select>
  );
}

function ImportView({ accounts, defaultAccount, onDone }: { accounts: BankAccount[]; defaultAccount: number; onDone: () => void }) {
  const [accountId, setAccountId] = useState(defaultAccount);
  const [filename, setFilename] = useState("");
  const [text, setText] = useState("");
  const [mapping, setMapping] = useState<Mapping | null>(null);
  const [preview, setPreview] = useState<ParsedRow[] | null>(null);
  const [error, setError] = useState("");
  const [result, setResult] = useState("");
  const [busy, setBusy] = useState(false);

  const headers = useMemo(() => {
    if (!mapping) return [];
    const first = text.split(/\r?\n/).find((l) => l.trim());
    return first ? splitLine(first, mapping.delimiter) : [];
  }, [mapping, text]);

  const load = async (file: File | undefined) => {
    if (!file) return;
    const t = await file.text();
    setFilename(file.name); setText(t); setPreview(null); setResult(""); setError("");
    const first = t.split(/\r?\n/).find((l) => l.trim()) ?? "";
    const delimiter = [",", ";", "\t", "|"].sort((a, b) => first.split(b).length - first.split(a).length)[0];
    setMapping(guessMapping(splitLine(first, delimiter), delimiter));
  };

  const set = <K extends keyof Mapping>(k: K, v: Mapping[K]) => { if (mapping) { setMapping({ ...mapping, [k]: v }); setPreview(null); } };
  const bad = preview?.filter((r) => r.error).length ?? 0;

  const previewColumns: Column<ParsedRow>[] = [
    { id: "row", header: "Row", align: "end", sortValue: (r) => r.row_no, render: (r) => r.row_no },
    { id: "date", header: "Date", sortValue: (r) => r.txn_date, render: (r) => r.txn_date },
    { id: "narration", header: "Description", locked: true, minWidth: 260, render: (r) => r.narration },
    { id: "debit", header: "Debit", align: "end", sortValue: (r) => r.debit_paise, render: (r) => (r.debit_paise ? inr(r.debit_paise) : "") },
    { id: "credit", header: "Credit", align: "end", sortValue: (r) => r.credit_paise, render: (r) => (r.credit_paise ? inr(r.credit_paise) : "") },
    { id: "result", header: "Result", sortValue: (r) => (r.error ? 0 : 1), render: (r) => (r.error ? <span><StatusBadge status="attention" label="Problem" /> {r.error}</span> : <StatusBadge status="posted" label="Will import" />) },
  ];

  return (
    <Stack gap={6}>
      <Form className="ao-form" onSubmit={(e) => e.preventDefault()}>
        <Stack gap={5}>
          <AccountSelect id="imp-account" label="Bank account this statement belongs to" value={accountId} accounts={accounts} onChange={setAccountId} />
          <div>
            <p className="cds--label">Statement file (CSV)</p>
            <FileUploaderButton labelText={filename || "Choose a CSV file"} buttonKind="tertiary" size="md" accept={[".csv", ".txt"]} disableLabelChanges
              onChange={(e: { target: { files: FileList | null } }) => load(e.target.files?.[0])} />
          </div>
        </Stack>
      </Form>

      {mapping && (
        <Form className="ao-form" onSubmit={(e) => e.preventDefault()}>
          <Stack gap={5}>
            <h4>Map the columns</h4>
            <div className="ao-cols-4 ao-span">
              <Select size="sm" id="m-delim" labelText="Delimiter" value={mapping.delimiter} onChange={(e) => set("delimiter", e.target.value)}>
                <SelectItem value="," text="Comma" /><SelectItem value=";" text="Semicolon" /><SelectItem value={"\t"} text="Tab" /><SelectItem value="|" text="Pipe" />
              </Select>
              <Select size="sm" id="m-order" labelText="Date format" value={mapping.date_order} onChange={(e) => set("date_order", e.target.value as Mapping["date_order"])}>
                <SelectItem value="dmy" text="Day / Month / Year" /><SelectItem value="mdy" text="Month / Day / Year" /><SelectItem value="ymd" text="Year / Month / Day" />
              </Select>
              <Select size="sm" id="m-first" labelText="First row" value={mapping.has_header ? "h" : "d"} onChange={(e) => set("has_header", e.target.value === "h")}>
                <SelectItem value="h" text="Is a header" /><SelectItem value="d" text="Is data" />
              </Select>
              <ColumnPick id="m-date" label="Date column" required value={mapping.date} headers={headers} onChange={(v) => set("date", v ?? 0)} />
              <ColumnPick id="m-narr" label="Description" value={mapping.narration} headers={headers} onChange={(v) => set("narration", v)} />
              <ColumnPick id="m-ref" label="Reference / UTR" value={mapping.reference} headers={headers} onChange={(v) => set("reference", v)} />
              <ColumnPick id="m-debit" label="Debit (withdrawal)" value={mapping.debit} headers={headers} onChange={(v) => set("debit", v)} />
              <ColumnPick id="m-credit" label="Credit (deposit)" value={mapping.credit} headers={headers} onChange={(v) => set("credit", v)} />
              <ColumnPick id="m-amount" label="Or one signed amount" value={mapping.amount} headers={headers} onChange={(v) => set("amount", v)} />
              <ColumnPick id="m-bal" label="Balance" value={mapping.balance} headers={headers} onChange={(v) => set("balance", v)} />
            </div>
            <div className="ao-actions ao-span">
              <Button size="sm" kind="tertiary" disabled={busy} onClick={async () => {
                setBusy(true);
                try { setPreview(await api.bankPreview(text, mapping)); setError(""); } catch (e) { setError(errorText(e)); setPreview(null); }
                setBusy(false);
              }}>Preview</Button>
              <Button size="sm" disabled={busy || !preview || preview.length === bad || !accountId} onClick={async () => {
                setBusy(true);
                try {
                  const r = await api.bankImport(accountId, filename, text, mapping);
                  setResult(`Imported ${r.imported} transactions. ${r.duplicates} were already in an earlier statement, ${r.rejected} rejected rows are kept for review.`);
                  setError(""); setPreview(null); setMapping(null); setText("");
                  onDone();
                } catch (e) { setError(errorText(e)); }
                setBusy(false);
              }}>{busy ? "Working…" : "Import"}</Button>
            </div>
          </Stack>
        </Form>
      )}
      <Err text={error} /><Okay text={result} />
      {preview && (
        <div>
          <p className="ao-block">{preview.length} rows read{bad ? ` · ${bad} with problems (kept in the rejected list, not imported)` : " · all can be imported"}. Importing never marks anything as paid.</p>
          <DataTable id="bank-preview" rows={preview} columns={previewColumns} rowKey={(r) => r.row_no} pageSizes={[50, 100]} defaultSort={{ id: "result", dir: "asc" }} emptyTitle="No rows" />
        </div>
      )}
    </Stack>
  );
}

// ============================================================ manual entry

function ManualView({ accounts, defaultAccount, onDone }: { accounts: BankAccount[]; defaultAccount: number; onDone: () => void }) {
  const [e, setE] = useState<ManualEntry>({ account_id: defaultAccount, txn_date: new Date().toISOString().slice(0, 10), narration: "", reference: "", direction: "credit", amount: "" });
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const set = <K extends keyof ManualEntry>(k: K, v: ManualEntry[K]) => setE({ ...e, [k]: v });
  return (
    <Form className="ao-form" onSubmit={async (ev) => {
      ev.preventDefault();
      if (busy) return;
      setBusy(true);
      try { await api.bankManualEntry(e); onDone(); } catch (err) { setError(errorText(err)); }
      setBusy(false);
    }}>
      <Stack gap={5}>
        <AccountSelect id="me-account" label="Bank account" value={e.account_id} accounts={accounts} onChange={(v) => set("account_id", v)} />
        <TextInput size="sm" id="me-date" type="date" labelText="Date" value={e.txn_date} onChange={(x) => set("txn_date", x.target.value)} />
        <Select size="sm" id="me-dir" labelText="Type" value={e.direction} onChange={(x) => set("direction", x.target.value as ManualEntry["direction"])}>
          <SelectItem value="credit" text="Credit (money received)" /><SelectItem value="debit" text="Debit (money paid out)" />
        </Select>
        <TextInput size="sm" id="me-amount" labelText="Amount (₹)" inputMode="decimal" value={e.amount} onChange={(x) => set("amount", x.target.value)} />
        <TextInput size="sm" id="me-narr" labelText="Description / who paid" value={e.narration} onChange={(x) => set("narration", x.target.value)} />
        <TextInput size="sm" id="me-ref" labelText="Bank reference / UTR" value={e.reference} onChange={(x) => set("reference", x.target.value)} />
        <div className="ao-span"><Err text={error} /></div>
        <div className="ao-actions ao-span"><Button size="sm" type="submit" disabled={busy}>{busy ? "Saving…" : "Add transaction"}</Button></div>
      </Stack>
    </Form>
  );
}

// ============================================================ accounts

function AccountsView({ accounts, onChange }: { accounts: BankAccount[]; onChange: () => void }) {
  const [editing, setEditing] = useState<BankAccount | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const columns: Column<BankAccount>[] = [
    { id: "name", header: "Account", locked: true, minWidth: 200, sortValue: (a) => a.name.toLowerCase(), render: (a) => <span>{a.name} {a.is_primary && <StatusBadge status="open" label="Primary" />}</span> },
    { id: "bank", header: "Bank", sortValue: (a) => a.bank_name.toLowerCase(), render: (a) => a.bank_name },
    { id: "no", header: "Account number", render: (a) => a.account_number },
    { id: "ifsc", header: "IFSC", render: (a) => a.ifsc },
    { id: "status", header: "Status", sortValue: (a) => (a.active ? 0 : 1), render: (a) => <StatusBadge status={a.active ? "active" : "inactive"} /> },
  ];

  if (editing) {
    const set = <K extends keyof BankAccount>(k: K, v: BankAccount[K]) => setEditing({ ...editing, [k]: v });
    return (
      <Form className="ao-form" onSubmit={async (ev) => {
        ev.preventDefault();
        if (busy) return;
        setBusy(true);
        try { await api.bankAccountSave(editing); setEditing(null); setError(""); onChange(); } catch (err) { setError(errorText(err)); }
        setBusy(false);
      }}>
        <Stack gap={5}>
          <h4>{editing.id ? "Edit bank account" : "New bank account"}</h4>
          <TextInput size="sm" id="ba-name" labelText="Name shown in lists (e.g. HDFC current)" value={editing.name} onChange={(e) => set("name", e.target.value)} />
          <TextInput size="sm" id="ba-bank" labelText="Bank" value={editing.bank_name} onChange={(e) => set("bank_name", e.target.value)} />
          <TextInput size="sm" id="ba-holder" labelText="Account holder" value={editing.account_holder} onChange={(e) => set("account_holder", e.target.value)} />
          <TextInput size="sm" id="ba-no" labelText="Account number" value={editing.account_number} onChange={(e) => set("account_number", e.target.value)} />
          <TextInput size="sm" id="ba-ifsc" labelText="IFSC" value={editing.ifsc} onChange={(e) => set("ifsc", e.target.value)} />
          <div className="ao-span">
            <Checkbox id="ba-primary" labelText="Primary account (its details print on invoices)" checked={editing.is_primary} onChange={(_, { checked }) => set("is_primary", checked)} />
            <Checkbox id="ba-active" labelText="Active" checked={editing.active} onChange={(_, { checked }) => set("active", checked)} />
          </div>
          <div className="ao-span"><Err text={error} /></div>
          <div className="ao-actions ao-span">
            <Button size="sm" kind="secondary" onClick={() => { setEditing(null); setError(""); }}>Cancel</Button>
            <Button size="sm" type="submit" disabled={busy}>{busy ? "Saving…" : "Save"}</Button>
          </div>
        </Stack>
      </Form>
    );
  }

  return (
    <>
      <div className="ao-actions ao-block"><Button renderIcon={Add} size="sm" onClick={() => setEditing({ id: null, name: "", bank_name: "", account_holder: "", account_number: "", ifsc: "", is_primary: false, active: true })}>Add account</Button></div>
      <DataTable
        id="bank-accounts" rows={accounts} columns={columns} rowKey={(a) => a.id!} pageSizes={[50]} defaultSort={{ id: "name", dir: "asc" }}
        emptyTitle="No bank accounts" emptyHint="Add the account before importing its statement."
        rowActions={(a) => <Button renderIcon={Edit} kind="ghost" size="sm" onClick={() => setEditing(a)}>Edit</Button>}
      />
    </>
  );
}

// ============================================================ match

type AllocRow = Allocation & { key: number };

function MatchView({ txn, outstanding, onClose }: { txn: TxnRow; outstanding: InvoiceRow[]; onClose: () => void }) {
  const [suggestions, setSuggestions] = useState<Suggestion[]>([]);
  const [rows, setRows] = useState<AllocRow[]>([{ key: 1, invoice_id: 0, amount: "", tds: "", tds_section: "" }]);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => { api.bankSuggest(txn.id).then(setSuggestions).catch((e) => setError(errorText(e))); }, [txn.id]);

  const update = (key: number, patch: Partial<Allocation>) => setRows(rows.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const applied = rows.reduce((n, r) => n + Math.round((parseFloat(r.amount) || 0) * 100), 0);
  const left = txn.unallocated_paise - applied;

  const sugColumns: Column<Suggestion>[] = [
    { id: "number", header: "Invoice", locked: true, sortValue: (s) => s.number, render: (s) => s.number },
    { id: "client", header: "Client", sortValue: (s) => s.client_name.toLowerCase(), render: (s) => s.client_name },
    { id: "balance", header: "Balance", align: "end", sortValue: (s) => s.balance_paise, render: (s) => s.balance_display },
    { id: "why", header: "Why it may match", minWidth: 320, render: (s) => s.reasons.join("; ") },
    { id: "score", header: "Score", align: "end", sortValue: (s) => s.score, render: (s) => s.score },
  ];

  return (
    <>
      <PageHeader title="Match bank credit" description={`${txn.txn_date} · ${txn.narration}${txn.reference ? ` · ${txn.reference}` : ""}`} actions={<Button renderIcon={ArrowLeft} size="sm" kind="secondary" onClick={onClose}>Back to transactions</Button>} />
      <p className="ao-block">Credit <strong>{txn.credit_display}</strong> · already applied {txn.allocated_display} · to allocate <strong>{inr(txn.unallocated_paise)}</strong></p>
      <h4 className="ao-block">Suggested invoices <span className="ao-muted">(nothing is applied until you confirm)</span></h4>
      <DataTable
        id="bank-suggest" rows={suggestions} columns={sugColumns} rowKey={(s) => s.invoice_id} pageSizes={[10, 20]} defaultSort={{ id: "score", dir: "desc" }}
        emptyTitle="No suggestions" emptyHint="Choose the invoice yourself below."
        rowActions={(s) => (
          <Button kind="ghost" size="sm" onClick={() => setRows([{ key: Date.now(), invoice_id: s.invoice_id, amount: (txn.unallocated_paise / 100).toFixed(2), tds: s.tds_amount ?? "", tds_section: s.tds_section ?? "" }])}>Use</Button>
        )}
      />
      <h4 className="ao-block ao-section">Allocation</h4>
      {rows.map((r) => (
        <div className="ao-alloc-row" key={r.key}>
          <Select size="sm" id={`al-inv-${r.key}`} labelText="Invoice" value={r.invoice_id || ""} onChange={(e) => update(r.key, { invoice_id: Number(e.target.value) })}>
            <SelectItem value="" text="Select an invoice" />
            {outstanding.map((o) => <SelectItem key={o.id} value={o.id} text={`${o.number} · ${o.client_name} · balance ${o.balance_display}`} />)}
          </Select>
          <TextInput size="sm" id={`al-amt-${r.key}`} labelText="Cash applied (₹)" inputMode="decimal" value={r.amount} onChange={(e) => update(r.key, { amount: e.target.value })} />
          <Select size="sm" id={`al-sec-${r.key}`} labelText="TDS deducted" value={r.tds_section} onChange={(e) => update(r.key, { tds_section: e.target.value })}>
            <SelectItem value="" text="No TDS" />{TDS_SECTIONS.map((c) => <SelectItem key={c} value={c} text={c} />)}
          </Select>
          <TextInput size="sm" id={`al-tds-${r.key}`} labelText="TDS (₹)" inputMode="decimal" disabled={!r.tds_section} value={r.tds} onChange={(e) => update(r.key, { tds: e.target.value })} />
          {rows.length > 1 ? <Button kind="ghost" size="md" onClick={() => setRows(rows.filter((x) => x.key !== r.key))}>Remove</Button> : <span />}
        </div>
      ))}
      <div className="ao-actions ao-block">
        <Button size="sm" kind="tertiary" onClick={() => setRows([...rows, { key: Date.now(), invoice_id: 0, amount: "", tds: "", tds_section: "" }])}>Split to another invoice</Button>
        <span>
          Applying <strong>{inr(applied)}</strong> · {left === 0 ? <StatusBadge status="matched" label="Fully allocated" /> : left > 0 ? <span>{inr(left)} stays open</span> : <StatusBadge status="unmatched" label={`Over by ${inr(-left)}`} />}
        </span>
      </div>
      <Err text={error} />
      <div className="ao-actions">
        <Button size="sm" kind="secondary" onClick={onClose}>Cancel</Button>
        <Button size="sm" disabled={busy || left < 0 || applied === 0} onClick={async () => {
          setBusy(true);
          try { await api.bankReconcile(txn.id, rows.filter((r) => r.invoice_id).map(({ invoice_id, amount, tds, tds_section }) => ({ invoice_id, amount, tds, tds_section }))); onClose(); }
          catch (e) { setError(errorText(e)); }
          setBusy(false);
        }}>{busy ? "Confirming…" : "Confirm match"}</Button>
      </div>
    </>
  );
}

// ============================================================ link existing receipts

function LinkView({ txn, onClose }: { txn: TxnRow; onClose: () => void }) {
  const [receipts, setReceipts] = useState<UnlinkedReceipt[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [picked, setPicked] = useState<UnlinkedReceipt[]>([]);
  useEffect(() => { api.bankUnlinked().then((r) => { setReceipts(r); setLoadError(""); }).catch((e) => setLoadError(errorText(e))).finally(() => setLoading(false)); }, []);

  const total = picked.reduce((n, r) => n + r.amount_paise, 0);
  const columns: Column<UnlinkedReceipt>[] = [
    { id: "number", header: "Receipt", locked: true, sortValue: (r) => r.number, render: (r) => r.number },
    { id: "invoice", header: "Invoice", sortValue: (r) => r.invoice_number, render: (r) => r.invoice_number },
    { id: "client", header: "Client", sortValue: (r) => r.client_name.toLowerCase(), render: (r) => r.client_name },
    { id: "date", header: "Date", sortValue: (r) => r.received_date, render: (r) => r.received_date },
    { id: "amount", header: "Amount", align: "end", sortValue: (r) => r.amount_paise, render: (r) => <span>{r.amount_display}{r.amount_paise === txn.unallocated_paise && <> <StatusBadge status="matched" label="Equals credit" /></>}</span> },
    { id: "ref", header: "Reference", render: (r) => r.reference },
  ];

  return (
    <>
      <PageHeader title="Link existing receipts" description={`${txn.txn_date} · ${txn.narration} · credit ${txn.credit_display} · to allocate ${inr(txn.unallocated_paise)}`} actions={<Button renderIcon={ArrowLeft} size="sm" kind="secondary" onClick={onClose}>Back to transactions</Button>} />
      <p className="ao-block">Use this when the payment was already recorded on the invoice. Linking confirms the bank credit; it does not change any invoice balance.</p>
      <DataTable
        id="bank-link" rows={receipts} columns={columns} rowKey={(r) => r.id} loading={loading} error={loadError} selectable onSelectionChange={setPicked}
        searchText={(r) => `${r.number} ${r.invoice_number} ${r.client_name} ${r.reference} ${r.amount_display}`} searchLabel="Search receipts"
        defaultSort={{ id: "date", dir: "desc" }} pageSizes={[10, 20, 50]} emptyTitle="No unlinked receipts" emptyHint="Every recorded payment is already tied to a bank credit."
      />
      <p className="ao-footnote">
        Selected <strong>{inr(total)}</strong> of {inr(txn.unallocated_paise)} · {total > txn.unallocated_paise ? <StatusBadge status="unmatched" label="More than the credit" /> : total === txn.unallocated_paise && total > 0 ? <StatusBadge status="matched" label="Equals the credit" /> : <span>{inr(txn.unallocated_paise - total)} would stay open</span>}
      </p>
      <Err text={error} />
      <div className="ao-actions">
        <Button size="sm" kind="secondary" onClick={onClose}>Cancel</Button>
        <Button size="sm" disabled={busy || picked.length === 0 || total > txn.unallocated_paise} onClick={async () => {
          setBusy(true);
          try { await api.bankLinkReceipts(txn.id, picked.map((r) => r.id)); onClose(); } catch (e) { setError(errorText(e)); }
          setBusy(false);
        }}>{busy ? "Linking…" : "Link selected receipts"}</Button>
      </div>
    </>
  );
}

// ============================================================ post to ledger

function PostView({ txn, onClose }: { txn: TxnRow; onClose: () => void }) {
  const [accounts, setAccounts] = useState<AccountRow[]>([]);
  const [accountId, setAccountId] = useState(0);
  const [tds, setTds] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const isCredit = txn.credit_paise > 0;
  useEffect(() => {
    api.accountList(new Date().toISOString().slice(0, 10)).then((l) => setAccounts(l.filter((a) => a.active && !a.code.startsWith("BANK-") && !["1000", "1010", "1200"].includes(a.code)))).catch((e) => setError(errorText(e)));
  }, []);
  return (
    <>
      <PageHeader title="Post to the ledger" description={`${txn.txn_date} · ${txn.narration} · ${isCredit ? `${txn.credit_display} credit` : `${txn.debit_display} debit`}`} actions={<Button renderIcon={ArrowLeft} size="sm" kind="secondary" onClick={onClose}>Back to transactions</Button>} />
      <p className="ao-block">For bank charges, interest, refunds and other lines that are not invoice payments. It records the entry and takes the line out of matching.</p>
      <Form className="ao-form" onSubmit={(e) => e.preventDefault()}>
        <Stack gap={5}>
          <Select size="sm" id="pl-acct" labelText={isCredit ? "Credit to (where the money came from)" : "Debit to (what the money was spent on)"} value={accountId || ""} onChange={(e) => setAccountId(Number(e.target.value))}>
            <SelectItem value="" text="Select an account" />{accounts.map((a) => <SelectItem key={a.id} value={a.id} text={`${a.code} ${a.name}`} />)}
          </Select>
          {isCredit && <TextInput size="sm" id="pl-tds" labelText="TDS deducted by the bank (₹, optional)" inputMode="decimal" value={tds} onChange={(e) => setTds(e.target.value)} />}
          <div className="ao-span"><Err text={error} /></div>
          <div className="ao-actions ao-span">
            <Button size="sm" kind="secondary" onClick={onClose}>Cancel</Button>
            <Button size="sm" disabled={!accountId || busy} onClick={async () => { setBusy(true); try { await api.bankClassify(txn.id, accountId, tds); onClose(); } catch (e) { setError(errorText(e)); } setBusy(false); }}>{busy ? "Posting…" : "Post"}</Button>
          </div>
        </Stack>
      </Form>
    </>
  );
}

// ============================================================ the register

type Mode = "list" | "import" | "manual" | "accounts" | "rejected";

export default function BankPage() {
  const [summary, setSummary] = useState<BankSummary | null>(null);
  const [outstanding, setOutstanding] = useState<InvoiceRow[]>([]);
  const [rejected, setRejected] = useState<RejectedRow[]>([]);
  const [accounts, setAccounts] = useState<BankAccount[]>([]);
  const [accountFilter, setAccountFilter] = useState(0);
  const [status, setStatus] = useState("");
  const [mode, setMode] = useState<Mode>("list");
  const [matching, setMatching] = useState<TxnRow | null>(null);
  const [linking, setLinking] = useState<TxnRow | null>(null);
  const [posting, setPosting] = useState<TxnRow | null>(null);
  const [error, setError] = useState("");

  const table = useServerTable<TxnRow>({
    id: "bank-txns", fetch: api.bankTxnPage, filters: { status, account: accountFilter ? String(accountFilter) : "" }, defaultSort: { id: "date", dir: "desc" },
  });
  const reloadTable = table.reload;

  const loadSupport = useCallback(() => {
    const acct = accountFilter || null;
    Promise.all([api.bankAccounts(), api.bankSummary(acct), api.bankRejected(), api.invoiceList()])
      .then(([a, s, r, inv]) => {
        setAccounts(a); setSummary(s); setRejected(r);
        setOutstanding(inv.filter((i) => i.status === "issued" && i.kind === "tax_invoice" && i.balance_paise > 0));
      })
      .catch((e) => setError(errorText(e)));
  }, [accountFilter]);
  useEffect(loadSupport, [loadSupport]);
  const load = useCallback(() => { loadSupport(); reloadTable(); }, [loadSupport, reloadTable]);

  const defaultAccount = accountFilter || accounts.find((a) => a.is_primary)?.id || accounts.find((a) => a.active)?.id || 0;
  const done = () => { setMode("list"); load(); };

  if (posting) return <PostView txn={posting} onClose={() => { setPosting(null); load(); }} />;
  if (linking) return <LinkView txn={linking} onClose={() => { setLinking(null); load(); }} />;
  if (matching) return <MatchView txn={matching} outstanding={outstanding} onClose={() => { setMatching(null); load(); }} />;

  if (mode !== "list") {
    const titles = { import: "Import a bank statement", manual: "Add a bank transaction", accounts: "Bank accounts", rejected: "Rejected statement rows" } as const;
    return (
      <>
        <PageHeader title={titles[mode]} actions={<Button renderIcon={ArrowLeft} size="sm" kind="secondary" onClick={() => { setMode("list"); load(); }}>Back to transactions</Button>} />
        {mode === "accounts" && <AccountsView accounts={accounts} onChange={loadSupport} />}
        {mode === "manual" && <ManualView accounts={accounts} defaultAccount={defaultAccount} onDone={done} />}
        {mode === "import" && <ImportView accounts={accounts} defaultAccount={defaultAccount} onDone={done} />}
        {mode === "rejected" && (
          <DataTable
            id="bank-rejected" rows={rejected} rowKey={(r) => `${r.batch_id}-${r.row_no}`} exportName="rejected-rows" pageSizes={[10, 20, 50]}
            columns={[
              { id: "batch", header: "Batch", sortValue: (r) => r.batch_id, render: (r) => r.batch_id, csv: (r) => r.batch_id },
              { id: "row", header: "Row", align: "end", sortValue: (r) => r.row_no, render: (r) => r.row_no, csv: (r) => r.row_no },
              { id: "content", header: "Content", minWidth: 300, render: (r) => r.raw_row, csv: (r) => r.raw_row },
              { id: "reason", header: "Reason", minWidth: 240, render: (r) => r.reason, csv: (r) => r.reason },
            ] as Column<RejectedRow>[]}
            emptyTitle="No rejected rows" emptyHint="Rows that could not be read are kept here instead of being dropped."
          />
        )}
      </>
    );
  }

  const columns: Column<TxnRow>[] = [
    { id: "date", header: "Date", sortValue: (t) => t.txn_date, render: (t) => t.txn_date, csv: (t) => t.txn_date },
    { id: "account", header: "Account", defaultVisible: accounts.length > 1, sortValue: (t) => t.account_name.toLowerCase(), render: (t) => t.account_name, csv: (t) => t.account_name },
    { id: "ref", header: "Bank reference", defaultVisible: false, sortValue: (t) => t.reference.toLowerCase(), render: (t) => t.reference, csv: (t) => t.reference },
    { id: "narration", header: "Description", locked: true, minWidth: 300, sortValue: (t) => t.narration.toLowerCase(), render: (t) => <span>{t.narration}{t.reference && <span className="ao-muted"> · {t.reference}</span>}{t.note && <span className="ao-muted"> — {t.note}</span>}</span>, csv: (t) => t.narration },
    { id: "debit", header: "Debit", align: "end", sortValue: (t) => t.debit_paise, render: (t) => t.debit_display, csv: (t) => t.debit_paise / 100 },
    { id: "credit", header: "Credit", align: "end", sortValue: (t) => t.credit_paise, render: (t) => t.credit_display, csv: (t) => t.credit_paise / 100 },
    { id: "applied", header: "Applied to invoices", align: "end", defaultVisible: false, render: (t) => (t.credit_paise ? t.allocated_display : ""), csv: (t) => (t.credit_paise ? t.allocated_display : "") },
    { id: "status", header: "Status", sortValue: (t) => t.status, render: (t) => <StatusBadge status={t.status} />, csv: (t) => t.status },
  ];

  const open = (t: TxnRow) => t.credit_paise > 0 && (t.status === "unmatched" || t.status === "partial");
  const menu = (t: TxnRow): MenuItem[] => {
    const items: MenuItem[] = [];
    if (open(t)) items.push({ label: "Link existing receipt…", onClick: () => setLinking(t) });
    if (t.status === "unmatched") {
      items.push({ label: "Post to ledger…", onClick: () => setPosting(t) });
      items.push({
        label: "Ignore (not an invoice payment)…",
        onClick: async () => {
          const note = await askReason("Why is this not an invoice payment? (for example a bank charge, a refund or an own transfer)", { danger: false });
          if (!note) return;
          try { await api.bankIgnore(t.id, note, false); load(); } catch (e) { setError(errorText(e)); }
        },
      });
    }
    if (t.status === "matched" || t.status === "partial") {
      items.push({
        label: "Undo match…", danger: true,
        onClick: async () => {
          const reason = await askReason("Undo this match? Enter the reason. Payments created by the match are reversed.");
          if (!reason) return;
          try { await api.bankUnmatch(t.id, reason); load(); } catch (e) { setError(errorText(e)); }
        },
      });
    }
    if (t.status === "ignored") items.push({ label: "Restore to unreconciled", onClick: async () => { try { await api.bankIgnore(t.id, "", true); load(); } catch (e) { setError(errorText(e)); } } });
    return items;
  };

  return (
    <>
      <PageHeader
        title="Bank reconciliation"
        description="Imported statement lines are never treated as paid until you confirm a match."
        actions={
          <>
            <Button size="sm" kind="ghost" onClick={() => setMode("accounts")}>Accounts</Button>
            <Button renderIcon={Add} size="sm" kind="tertiary" onClick={() => setMode("manual")}>Add manually</Button>
            <Button renderIcon={Upload} size="sm" onClick={() => setMode("import")}>Import statement</Button>
          </>
        }
      />
      {summary && (
        <p className="ao-block">
          <StatusBadge status="unmatched" label={`${summary.unmatched} unreconciled`} /> {summary.partial} partly matched · {summary.matched} reconciled · {summary.ignored} ignored · {summary.duplicate} duplicates ·{" "}
          {rejected.length > 0 ? <Button kind="ghost" size="sm" onClick={() => setMode("rejected")}>{summary.rejected_rows} rejected rows</Button> : "0 rejected rows"} · credits still to allocate <strong>{summary.unallocated_credits_display}</strong>
        </p>
      )}
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      <DataTable
        id="bank-txns" rows={table.rows} columns={columns} rowKey={(t) => t.id} loading={table.loading} error={table.error} onRetry={reloadTable}
        server={table.server} searchLabel="Search description, reference or exact amount" exportName="bank-transactions"
        filters={
          <>
            <FilterSelect label="Filter by bank account" value={accountFilter ? String(accountFilter) : ""} onChange={(v) => setAccountFilter(Number(v) || 0)} options={[["", "All accounts"], ...accounts.map((a) => [String(a.id), a.name] as [string, string])]} />
            <FilterSelect label="Filter by reconciliation status" value={status} onChange={setStatus}
              options={[["", "All statuses"], ["unmatched", "Unreconciled"], ["partial", "Partly matched"], ["matched", "Reconciled"], ["ignored", "Ignored"], ["duplicate", "Duplicates"]]} />
          </>
        }
        activeFilters={[
          ...(accountFilter ? [{ label: `Account: ${accounts.find((a) => a.id === accountFilter)?.name ?? accountFilter}`, clear: () => setAccountFilter(0) }] : []),
          ...(status ? [{ label: `Status: ${status}`, clear: () => setStatus("") }] : []),
        ]}
        totalsNode={<>Debits <strong>{inr(table.sums.debit ?? 0)}</strong> · Credits <strong>{inr(table.sums.credit ?? 0)}</strong></>}
        emptyTitle={accounts.length === 0 ? "Add a bank account first" : "No bank transactions yet"}
        emptyHint={accounts.length === 0 ? "Use Accounts to add the account, then import its statement." : "Import a CSV statement, or add a transaction manually."}
        rowActions={(t) => (
          <>
            {open(t) && <Button renderIcon={Link} kind="ghost" size="sm" onClick={() => setMatching(t)}>Match</Button>}
            <RowMenu items={menu(t)} />
          </>
        )}
      />
    </>
  );
}
