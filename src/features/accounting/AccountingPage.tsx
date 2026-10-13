import { useEffect, useState, type ReactNode } from "react";
import { Button, Form, PasswordInput, Select, SelectItem, Stack, TableCell, TableRow, TextInput } from "@carbon/react";
import {
  api, errorText, type AccountRow, type AssetRow, type ExpenseRow, type IncomeRow, type JournalLineRow, type JournalRow, type PayrollRow, type PeriodRow, type Vendor, type Property,
} from "../../api";
import PageHeader from "../../components/PageHeader";
import FilterSelect from "../../components/FilterSelect";
import { askReason } from "../../components/dialogs";
import StatusBadge from "../../components/StatusBadge";
import DataTable, { type Column } from "../../components/data-table/DataTable";
import RowMenu from "../../components/data-table/RowMenu";
import { useServerTable } from "../../components/data-table/useServerTable";
import { Grid, Notice, PaySelect, currentFy, inr, today, useLoad } from "./shared";
import { Add, ArrowLeft } from "@carbon/icons-react";
import { DraftGuard } from "../../app/unsaved";

const canPostRole = (role: string) => role === "Owner" || role === "Accounts";


// ============================================================ reports

type ReportKind = "tb" | "pnl" | "bs" | "ledger" | "tax" | "ar" | "estimate";

export function ReportsSection() {
  const fy = currentFy();
  const [kind, setKind] = useState<ReportKind>("tb");
  const [from, setFrom] = useState(fy.from);
  const [to, setTo] = useState(fy.to);
  const [rate, setRate] = useState("25");
  const [useSlabs, setUseSlabs] = useState(false);
  const [cess, setCess] = useState("4");
  const [slabs, setSlabs] = useState<{ upto: string; rate_percent: string }[]>([{ upto: "", rate_percent: "" }]);
  const [accounts, setAccounts] = useState<AccountRow[]>([]);
  const [accountId, setAccountId] = useState(0);
  const [out, setOut] = useState<ReactNode>(null);
  const [error, setError] = useState("");
  const [running, setRunning] = useState(false);

  useEffect(() => { api.accountList(today()).then(setAccounts).catch((e) => setError(errorText(e))); }, []);

  const needsFrom = ["pnl", "ledger", "tax", "estimate"].includes(kind);
  const asOf = kind === "tb" || kind === "bs" || kind === "ar";

  const run = async () => {
    setError(""); setRunning(true);
    try {
      if (kind === "tb") {
        const r = await api.reportTrialBalance(to);
        setOut(<>
          <Grid head={["Code", "Account", "Debit", "Credit"]}>
            {r.rows.map((x) => <TableRow key={x.code}><TableCell>{x.code}</TableCell><TableCell>{x.name}</TableCell><TableCell>{x.debit_display}</TableCell><TableCell>{x.credit_display}</TableCell></TableRow>)}
            <TableRow><TableCell /><TableCell><strong>Total</strong></TableCell><TableCell><strong>{r.total_debit}</strong></TableCell><TableCell><strong>{r.total_credit}</strong></TableCell></TableRow>
          </Grid>
          <StatusBadge status={r.balanced ? "posted" : "overdue"} label={r.balanced ? "Debits equal credits" : "Debits and credits differ"} />
        </>);
      } else if (kind === "pnl") {
        const r = await api.reportPnl(from, to);
        setOut(<>
          <h4>Income</h4>
          <Grid head={["Code", "Account", "Amount"]}>{r.income.map((x) => <TableRow key={x.code}><TableCell>{x.code}</TableCell><TableCell>{x.name}</TableCell><TableCell>{x.amount_display}</TableCell></TableRow>)}</Grid>
          <p><strong>Total income {r.total_income}</strong></p>
          <h4>Expenses</h4>
          <Grid head={["Code", "Account", "Amount"]}>{r.expenses.map((x) => <TableRow key={x.code}><TableCell>{x.code}</TableCell><TableCell>{x.name}</TableCell><TableCell>{x.amount_display}</TableCell></TableRow>)}</Grid>
          <p><strong>Total expenses {r.total_expenses}</strong></p>
          <h3>Profit for the period: {r.profit_display}</h3>
        </>);
      } else if (kind === "bs") {
        const r = await api.reportBalanceSheet(to);
        const sect = (title: string, rows: { code: string; name: string; amount_display: string }[]) => (
          <><h4>{title}</h4><Grid head={["Code", "Account", "Amount"]}>{rows.map((x, i) => <TableRow key={i}><TableCell>{x.code}</TableCell><TableCell>{x.name}</TableCell><TableCell>{x.amount_display}</TableCell></TableRow>)}</Grid></>
        );
        setOut(<>
          {sect("Assets", r.assets)}<p><strong>Total assets {r.total_assets}</strong></p>
          {sect("Liabilities", r.liabilities)}{sect("Equity", r.equity)}
          <p><strong>Total liabilities and equity {r.total_liabilities_equity}</strong> · <StatusBadge status={r.balanced ? "posted" : "overdue"} label={r.balanced ? "Balanced" : "Not balanced"} /></p>
        </>);
      } else if (kind === "ledger") {
        if (!accountId) { setError("Choose an account."); setRunning(false); return; }
        const r = await api.reportLedger(accountId, from, to);
        setOut(<>
          <h4>{r.code} {r.name} · opening balance {r.opening_display}</h4>
          <Grid head={["Date", "Journal", "Narration", "Debit", "Credit", "Running balance"]}>
            {r.rows.map((x, i) => <TableRow key={i}><TableCell>{x.date}</TableCell><TableCell>{x.number}</TableCell><TableCell>{x.narration}</TableCell><TableCell>{x.debit_display}</TableCell><TableCell>{x.credit_display}</TableCell><TableCell>{x.balance_display}</TableCell></TableRow>)}
          </Grid>
          <p><strong>Closing balance {r.closing_display}</strong></p>
        </>);
      } else if (kind === "tax") {
        const r = await api.reportTaxSummary(from, to);
        const row = (k: string, v: string) => <TableRow key={k}><TableCell>{k}</TableCell><TableCell>{v}</TableCell></TableRow>;
        setOut(<Grid head={["Item", "Amount"]}>
          {row("GST output - CGST", r.output_cgst)}{row("GST output - SGST", r.output_sgst)}{row("GST output - IGST", r.output_igst)}
          {row("GST input credit", r.input_credit)}{row("Net GST payable", r.net_gst_payable)}{row("Composition tax (own cost)", r.composition_tax)}
          {row("TDS deducted by clients (receivable)", r.tds_receivable)}{row("TDS payable to government", r.tds_payable)}
        </Grid>);
      } else if (kind === "ar") {
        const r = await api.reportArCheck(to);
        setOut(<p>Receivable in the ledger <strong>{r.ledger_display}</strong> · owed per open invoices <strong>{r.invoices_display}</strong> · <StatusBadge status={r.agrees ? "posted" : "overdue"} label={r.agrees ? "Agree" : "Differ"} />{!r.agrees && " Use the Post earlier documents action in Chart and financial years if invoices were issued before the ledger existed."}</p>);
      } else {
        const r = useSlabs ? await api.reportTaxSlabs(from, to, slabs, cess) : await api.reportTaxEstimate(from, to, rate);
        setOut(<>
          <p><strong>Estimate only.</strong> {useSlabs ? "Progressive tax on ledger profit using the slabs you entered" : "Flat rate on ledger profit"} plus cess; it ignores deductions, rebates, surcharge, exemptions and disallowances. Have the accountant compute the actual liability.</p>
          <Grid head={["Item", "Amount"]}>
            <TableRow><TableCell>Profit before tax</TableCell><TableCell>{r.profit_before_tax}</TableCell></TableRow>
            <TableRow><TableCell>{useSlabs ? "Tax on the slabs" : `Tax at ${rate}%`}</TableCell><TableCell>{r.tax_display}</TableCell></TableRow>
            <TableRow><TableCell>Cess ({useSlabs ? cess : "4"}%)</TableCell><TableCell>{r.cess_display}</TableCell></TableRow>
            <TableRow><TableCell><strong>Total</strong></TableCell><TableCell><strong>{r.total_display}</strong></TableCell></TableRow>
          </Grid>
        </>);
      }
    } catch (e) { setError(errorText(e)); setOut(null); }
    setRunning(false);
  };

  return (
    <>
      <PageHeader title="Reports" description={`Computed from posted journals. Financial year to date: ${fy.from} to ${fy.to}.`} actions={<Button size="sm" onClick={run} disabled={running}>{running ? "Running…" : "Run report"}</Button>} />
      <div className="toolbar ao-justify-start ao-wrap">
        <Select size="sm" id="rep-kind" labelText="Report" value={kind} onChange={(e) => { setKind(e.target.value as ReportKind); setOut(null); }}>
          <SelectItem value="tb" text="Trial balance" /><SelectItem value="pnl" text="Profit and loss" /><SelectItem value="bs" text="Balance sheet" />
          <SelectItem value="ledger" text="Account ledger" /><SelectItem value="tax" text="GST and TDS summary" />
          <SelectItem value="ar" text="Receivable vs invoices check" /><SelectItem value="estimate" text="Income-tax estimate" />
        </Select>
        {needsFrom && <TextInput size="sm" id="rep-from" type="date" labelText="From" value={from} onChange={(e) => setFrom(e.target.value)} />}
        <TextInput size="sm" id="rep-to" type="date" labelText={asOf ? "As of" : "To"} value={to} onChange={(e) => setTo(e.target.value)} />
        {kind === "ledger" && (
          <Select size="sm" id="rep-acct" labelText="Account" value={accountId || ""} onChange={(e) => setAccountId(Number(e.target.value))}>
            <SelectItem value="" text="Select an account" />
            {accounts.map((a) => <SelectItem key={a.id} value={a.id} text={`${a.code} ${a.name}`} />)}
          </Select>
        )}
        {kind === "estimate" && (
          <Select size="sm" id="rep-mode" labelText="Method" value={useSlabs ? "slabs" : "flat"} onChange={(e) => setUseSlabs(e.target.value === "slabs")}>
            <SelectItem value="flat" text="Flat rate (firms, LLPs, companies)" /><SelectItem value="slabs" text="Slab table (individuals)" />
          </Select>
        )}
        {kind === "estimate" && !useSlabs && <TextInput size="sm" id="rep-rate" labelText="Tax rate (%)" value={rate} onChange={(e) => setRate(e.target.value)} />}
      </div>
      {kind === "estimate" && useSlabs && (
        <Stack gap={3}>
          <p>Enter the slabs that apply to you for the year, lowest first; leave the last limit empty. The app does not supply rates.</p>
          {slabs.map((s, i) => (
            <div key={i} className="toolbar ao-justify-start">
              <TextInput size="sm" id={`sl-u-${i}`} labelText="Up to (₹)" value={s.upto} onChange={(e) => setSlabs(slabs.map((x, j) => (j === i ? { ...x, upto: e.target.value } : x)))} />
              <TextInput size="sm" id={`sl-r-${i}`} labelText="Rate (%)" value={s.rate_percent} onChange={(e) => setSlabs(slabs.map((x, j) => (j === i ? { ...x, rate_percent: e.target.value } : x)))} />
              {slabs.length > 1 && <Button kind="ghost" size="sm" onClick={() => setSlabs(slabs.filter((_, j) => j !== i))}>Remove</Button>}
            </div>
          ))}
          <div><Button kind="tertiary" size="sm" onClick={() => setSlabs([...slabs, { upto: "", rate_percent: "" }])}>Add slab</Button> <TextInput size="sm" id="sl-cess" labelText="Cess (%)" value={cess} onChange={(e) => setCess(e.target.value)} /></div>
        </Stack>
      )}
      <Notice error={error} />
      <div className="ao-block-start">{out}</div>
    </>
  );
}

// ============================================================ journals

export function JournalsSection({ role }: { role: string }) {
  const canPost = canPostRole(role);
  const fy = currentFy();
  const [from, setFrom] = useState(fy.from);
  const [to, setTo] = useState(fy.to);
  const [source, setSource] = useState("");
  const table = useServerTable<JournalRow>({ id: "journals", fetch: api.journalPage, filters: { from, to, source }, defaultSort: { id: "date", dir: "desc" } });
  const reload = table.reload;
  const [view, setView] = useState<"list" | "new" | { reverse: JournalRow } | { lines: JournalRow }>("list");
  const [accounts, setAccounts] = useState<AccountRow[]>([]);
  const [lines, setLines] = useState<JournalLineRow[]>([]);
  const [date, setDate] = useState(today());
  const [narration, setNarration] = useState("");
  const [jl, setJl] = useState([{ account_id: 0, debit: "", credit: "", memo: "" }, { account_id: 0, debit: "", credit: "", memo: "" }]);
  const [reason, setReason] = useState("");
  const [pw, setPw] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => { api.accountList(today()).then((l) => setAccounts(l.filter((a) => a.active))).catch(() => undefined); }, []);

  const columns: Column<JournalRow>[] = [
    { id: "date", header: "Date", width: 110, sortValue: (r) => r.date, render: (r) => r.date, csv: (r) => r.date },
    { id: "number", header: "Voucher no.", locked: true, sortValue: (r) => r.number, render: (r) => r.number, csv: (r) => r.number },
    { id: "narration", header: "Narration", minWidth: 280, sortValue: (r) => r.narration.toLowerCase(), render: (r) => r.narration, csv: (r) => r.narration },
    { id: "source", header: "Source", sortValue: (r) => r.source_type, render: (r) => r.source_type.replace(/_/g, " "), csv: (r) => r.source_type },
    { id: "amount", header: "Amount", align: "end", sortValue: (r) => r.total_paise, render: (r) => r.total_display, csv: (r) => r.total_paise / 100 },
    { id: "status", header: "Status", sortValue: (r) => (r.reversed ? 1 : 0), render: (r) => <StatusBadge status={r.reversed ? "reversed" : "posted"} />, csv: (r) => (r.reversed ? "Reversed" : "Posted") },
  ];

  const sources = ["invoice", "receipt", "credit_note", "manual", "expense_pending", "expense_payment", "income_pending", "payroll_pending", "asset_pending", "tax_payment", "bank_txn", "reversal"];

  if (typeof view === "object" && "lines" in view) {
    return (
      <>
        <PageHeader title={view.lines.number} description={`${view.lines.date} · ${view.lines.narration}`} actions={<Button renderIcon={ArrowLeft} size="sm" kind="secondary" onClick={() => setView("list")}>Back to journals</Button>} />
        <Grid head={["Account", "Memo", "Debit", "Credit"]}>
          {lines.map((l, i) => <TableRow key={i}><TableCell>{l.account}</TableCell><TableCell>{l.memo}</TableCell><TableCell>{l.debit_display}</TableCell><TableCell>{l.credit_display}</TableCell></TableRow>)}
        </Grid>
      </>
    );
  }

  if (typeof view === "object" && "reverse" in view) {
    const j = view.reverse;
    return (
      <>
        <PageHeader title={`Reverse ${j.number}`} description="Posts the mirror image of this journal. The original stays on record." />
        <Form className="ao-form" onSubmit={async (e) => {
          e.preventDefault();
          if (busy) return;
          setBusy(true);
          try { await api.journalReverse(j.id, reason, pw); setView("list"); setReason(""); setPw(""); setError(""); reload(); } catch (err) { setError(errorText(err)); }
          setBusy(false);
        }}>
          <DraftGuard value={{ reason, pw }} />
          <Stack gap={5}>
            <TextInput size="sm" id="rv-reason" labelText="Reason" value={reason} onChange={(e) => setReason(e.target.value)} />
            <PasswordInput size="sm" id="rv-pw" labelText="Your password" value={pw} onChange={(e) => setPw(e.target.value)} />
            <Notice error={error} />
            <div className="toolbar"><Button size="sm" kind="secondary" onClick={() => { setView("list"); setError(""); }}>Cancel</Button><Button size="sm" type="submit" disabled={busy}>{busy ? "Posting…" : "Reverse journal"}</Button></div>
          </Stack>
        </Form>
      </>
    );
  }

  if (view === "new") {
    const dsum = jl.reduce((n, l) => n + (parseFloat(l.debit) || 0), 0);
    const csum = jl.reduce((n, l) => n + (parseFloat(l.credit) || 0), 0);
    return (
      <>
        <PageHeader title="New journal" description="Debits must equal credits. Posted journals cannot be edited; correct them by reversal." />
        <Form className="ao-form" onSubmit={async (e) => {
          e.preventDefault();
          if (busy) return;
          setBusy(true);
          try {
            await api.journalPost(date, narration, jl.filter((l) => l.account_id));
            setView("list"); setNarration(""); setJl(jl.map((l) => ({ ...l, debit: "", credit: "", memo: "" }))); setError(""); reload();
          } catch (err) { setError(errorText(err)); }
          setBusy(false);
        }}>
          <DraftGuard value={{ date, narration, jl }} />
          <Stack gap={4}>
            <TextInput size="sm" id="mj-date" type="date" labelText="Date" value={date} onChange={(e) => setDate(e.target.value)} />
            <TextInput size="sm" id="mj-narr" labelText="Narration" value={narration} onChange={(e) => setNarration(e.target.value)} />
            {jl.map((l, i) => (
              <div key={i} className="toolbar ao-span">
                <Select size="sm" id={`mj-a-${i}`} labelText={i === 0 ? "Account" : ""} hideLabel={i !== 0} value={l.account_id || ""} onChange={(e) => setJl(jl.map((x, j) => (j === i ? { ...x, account_id: Number(e.target.value) } : x)))}>
                  <SelectItem value="" text="Account…" />
                  {accounts.map((a) => <SelectItem key={a.id} value={a.id} text={`${a.code} ${a.name}`} />)}
                </Select>
                <TextInput size="sm" id={`mj-d-${i}`} labelText={i === 0 ? "Debit" : ""} hideLabel={i !== 0} value={l.debit} onChange={(e) => setJl(jl.map((x, j) => (j === i ? { ...x, debit: e.target.value } : x)))} />
                <TextInput size="sm" id={`mj-c-${i}`} labelText={i === 0 ? "Credit" : ""} hideLabel={i !== 0} value={l.credit} onChange={(e) => setJl(jl.map((x, j) => (j === i ? { ...x, credit: e.target.value } : x)))} />
              </div>
            ))}
            <div>
              <Button kind="ghost" size="sm" onClick={() => setJl([...jl, { account_id: 0, debit: "", credit: "", memo: "" }])}>Add line</Button>
              <span className="ao-inline-start">Debits {dsum.toFixed(2)} · Credits {csum.toFixed(2)} · <StatusBadge status={Math.abs(dsum - csum) < 0.005 && dsum > 0 ? "posted" : "overdue"} label={Math.abs(dsum - csum) < 0.005 && dsum > 0 ? "Balanced" : "Out of balance"} /></span>
            </div>
            <Notice error={error} />
            <div className="toolbar"><Button size="sm" kind="secondary" onClick={() => { setView("list"); setError(""); }}>Cancel</Button><Button size="sm" type="submit" disabled={busy}>{busy ? "Posting…" : "Post journal"}</Button></div>
          </Stack>
        </Form>
      </>
    );
  }

  return (
    <>
      <PageHeader title="Journals" description="Every posting in the ledger, from documents and from manual entries." actions={canPost && <Button renderIcon={Add} size="sm" onClick={() => setView("new")}>New journal</Button>} />
      <DataTable
        id="journals" rows={table.rows} columns={columns} rowKey={(r) => r.id} loading={table.loading} error={table.error} onRetry={reload}
        server={table.server} searchLabel="Search voucher number or narration" exportName="journals"
        filters={
          <>
            <TextInput id="jr-from" type="date" labelText="From" hideLabel size="md" value={from} onChange={(e) => setFrom(e.target.value)} />
            <TextInput id="jr-to" type="date" labelText="To" hideLabel size="md" value={to} onChange={(e) => setTo(e.target.value)} />
            <FilterSelect label="Filter by source" value={source} onChange={setSource} options={[["", "All sources"], ...sources.map((s) => [s, s.replace(/_/g, " ")] as [string, string])]} />
          </>
        }
        activeFilters={source ? [{ label: `Source: ${source.replace(/_/g, " ")}`, clear: () => setSource("") }] : []}
        totalsNode={<>Journal value <strong>{inr(table.sums.amount ?? 0)}</strong> (debit side)</>}
        emptyTitle="No journals in this period" emptyHint="Change the date range, or post invoices and receipts."
        rowActions={(r) => (
          <>
            <Button kind="ghost" size="sm" onClick={async () => { try { setLines(await api.journalLines(r.id)); setView({ lines: r }); } catch (e) { setError(errorText(e)); } }}>Lines</Button>{" "}
            {canPost && !r.reversed && <RowMenu items={[{ label: "Reverse…", danger: true, onClick: () => setView({ reverse: r }) }]} />}
          </>
        )}
      />
    </>
  );
}

// ============================================================ expenses

export function ExpensesSection({ role }: { role: string }) {
  const canPost = canPostRole(role);
  const { data: rows, loading, error: loadError, reload } = useLoad(() => api.expenseList(), [] as ExpenseRow[]);
  const [view, setView] = useState<"list" | "new" | { pay: ExpenseRow }>("list");
  const [accounts, setAccounts] = useState<AccountRow[]>([]);
  const [vendors, setVendors] = useState<Vendor[]>([]);
  const [vname, setVname] = useState("");
  const blank = { date: today(), vendor_id: 0, bill_ref: "", description: "", expense_account_id: 0, amount: "", gst: "", tds: "", paid_via: "bank" };
  const [f, setF] = useState(blank);
  const [payDate, setPayDate] = useState(today());
  const [payVia, setPayVia] = useState("bank");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<typeof f>) => setF({ ...f, ...patch });

  const loadSupport = () => {
    api.vendorList().then(setVendors).catch(() => undefined);
    api.accountList(today()).then((l) => setAccounts(l.filter((a) => a.kind === "expense" && a.active))).catch(() => undefined);
  };
  useEffect(loadSupport, []);

  const state = (r: ExpenseRow) => (r.voided ? "void" : r.paid_via === "payable" ? (r.settled ? "paid" : "payable") : "paid");
  const columns: Column<ExpenseRow>[] = [
    { id: "date", header: "Date", width: 110, sortValue: (r) => r.date, render: (r) => r.date, csv: (r) => r.date },
    { id: "vendor", header: "Vendor", sortValue: (r) => r.vendor.toLowerCase(), render: (r) => r.vendor, csv: (r) => r.vendor },
    { id: "desc", header: "Description", locked: true, minWidth: 240, sortValue: (r) => r.description.toLowerCase(), render: (r) => r.description, csv: (r) => r.description },
    { id: "account", header: "Account", sortValue: (r) => r.account, render: (r) => r.account, csv: (r) => r.account },
    { id: "total", header: "Amount incl. GST", align: "end", sortValue: (r) => r.total_paise, render: (r) => r.total_display, csv: (r) => r.total_paise / 100 },
    { id: "status", header: "Status", sortValue: (r) => state(r), render: (r) => <StatusBadge status={state(r)} />, csv: (r) => state(r) },
  ];

  if (typeof view === "object") {
    const e = view.pay;
    return (
      <>
        <PageHeader title="Pay vendor bill" description={`${e.vendor || "No vendor"} · ${e.description} · ${e.total_display}`} />
        <Form className="ao-form" onSubmit={async (ev) => {
          ev.preventDefault();
          if (busy) return;
          setBusy(true);
          try { await api.expenseSettle(e.id, payDate, payVia); setView("list"); setError(""); reload(); } catch (err) { setError(errorText(err)); }
          setBusy(false);
        }}>
          <DraftGuard value={{ payDate, payVia }} />
          <Stack gap={5}>
            <TextInput size="sm" id="pay-date" type="date" labelText="Payment date" value={payDate} onChange={(x) => setPayDate(x.target.value)} />
            <PaySelect id="pay-via" label="Paid from" value={payVia} onChange={setPayVia} allowPayable={false} />
            <Notice error={error} />
            <div className="toolbar"><Button size="sm" kind="secondary" onClick={() => { setView("list"); setError(""); }}>Cancel</Button><Button size="sm" type="submit" disabled={busy}>{busy ? "Saving…" : "Record payment"}</Button></div>
          </Stack>
        </Form>
      </>
    );
  }

  if (view === "new") {
    return (
      <>
        <PageHeader title="Record an expense" description="GST is claimed as input credit only with a regular GST registration; otherwise it is part of the cost." />
        <Form className="ao-form" onSubmit={async (ev) => {
          ev.preventDefault();
          if (busy) return;
          setBusy(true);
          try { await api.expenseRecord({ ...f, vendor_id: f.vendor_id || null }); setF(blank); setView("list"); setError(""); reload(); } catch (err) { setError(errorText(err)); }
          setBusy(false);
        }}>
          <DraftGuard value={f} />
          <Stack gap={4}>
            <TextInput size="sm" id="ex-date" type="date" labelText="Date" value={f.date} onChange={(e) => set({ date: e.target.value })} />
            <Select size="sm" id="ex-vendor" labelText="Vendor (optional)" value={f.vendor_id || ""} onChange={(e) => set({ vendor_id: Number(e.target.value) })}>
              <SelectItem value="" text="No vendor" />{vendors.filter((v) => v.active).map((v) => <SelectItem key={v.id} value={v.id!} text={v.name} />)}
            </Select>
            <TextInput size="sm" id="ex-ref" labelText="Bill / reference" value={f.bill_ref} onChange={(e) => set({ bill_ref: e.target.value })} />
            <TextInput size="sm" id="ex-desc" labelText="Description" value={f.description} onChange={(e) => set({ description: e.target.value })} />
            <Select size="sm" id="ex-acct" labelText="Expense account" value={f.expense_account_id || ""} onChange={(e) => set({ expense_account_id: Number(e.target.value) })}>
              <SelectItem value="" text="Select an account" />{accounts.map((a) => <SelectItem key={a.id} value={a.id} text={`${a.code} ${a.name}`} />)}
            </Select>
            <TextInput size="sm" id="ex-amt" labelText="Amount before GST (₹)" value={f.amount} onChange={(e) => set({ amount: e.target.value })} />
            <TextInput size="sm" id="ex-gst" labelText="GST charged (₹)" value={f.gst} onChange={(e) => set({ gst: e.target.value })} />
            <TextInput size="sm" id="ex-tds" labelText="TDS you deducted from the vendor (₹)" value={f.tds} onChange={(e) => set({ tds: e.target.value })} />
            <PaySelect id="ex-via" label="Paid from" value={f.paid_via} onChange={(v) => set({ paid_via: v })} allowPayable />
            <div className="toolbar ao-justify-start ao-span">
              <TextInput size="sm" id="vn" labelText="Add a vendor" value={vname} onChange={(e) => setVname(e.target.value)} />
              <Button kind="tertiary" size="sm" onClick={async () => { try { await api.vendorSave({ id: null, name: vname, pan: "", active: true }); setVname(""); loadSupport(); setError(""); } catch (err) { setError(errorText(err)); } }}>Add vendor</Button>
            </div>
            <Notice error={error} />
            <div className="toolbar"><Button size="sm" kind="secondary" onClick={() => { setView("list"); setError(""); }}>Cancel</Button><Button size="sm" type="submit" disabled={busy}>{busy ? "Saving…" : "Record expense"}</Button></div>
          </Stack>
        </Form>
      </>
    );
  }

  return (
    <>
      <PageHeader title="Expenses" description="Bills and costs, with GST input credit and TDS deducted from vendors." actions={canPost && <Button renderIcon={Add} size="sm" onClick={() => setView("new")}>Record expense</Button>} />
      <Notice error={error} />
      <DataTable
        id="expenses" rows={rows} columns={columns} rowKey={(r) => r.id} loading={loading} error={loadError} onRetry={reload}
        searchLabel="Search description, vendor or account" exportName="expenses"
        searchText={(r) => `${r.description} ${r.vendor} ${r.account}`}
        defaultSort={{ id: "date", dir: "desc" }}
        totals={(l) => <>Total <strong>{inr(l.filter((r) => !r.voided).reduce((n, r) => n + r.total_paise, 0))}</strong> (voided entries excluded)</>}
        emptyTitle="No expenses recorded" emptyHint="Record bills as they arrive so payables and GST credit stay current."
        rowActions={(r) => (
          canPost && !r.voided ? (
            <>
              {r.paid_via === "payable" && !r.settled && <Button kind="ghost" size="sm" onClick={() => setView({ pay: r })}>Pay</Button>}{" "}
              {!r.settled && <RowMenu items={[{ label: "Void…", danger: true, onClick: async () => { const reason = await askReason("Void this expense? Enter the reason."); if (!reason) return; try { await api.expenseVoid(r.id, reason); reload(); } catch (e) { setError(errorText(e)); } } }]} />}
            </>
          ) : null
        )}
      />
    </>
  );
}

// ============================================================ income

export function IncomeSection({ role }: { role: string }) {
  const canPost = canPostRole(role);
  const { data: rows, loading, error: loadError, reload } = useLoad(() => api.incomeList(), [] as IncomeRow[]);
  const [view, setView] = useState<"list" | "new">("list");
  const [props, setProps] = useState<Property[]>([]);
  const [pname, setPname] = useState("");
  const [pkind, setPkind] = useState<"commercial" | "residential">("commercial");
  const blank = { date: today(), kind: "rent" as "rent" | "interest" | "other", property_id: 0, payer: "", description: "", amount: "", gst: "", gst_mode: "intra" as "intra" | "inter", tds: "", received_via: "bank" };
  const [f, setF] = useState(blank);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<typeof f>) => setF({ ...f, ...patch });
  const loadProps = () => api.propertyList().then(setProps).catch(() => undefined);
  useEffect(() => { loadProps(); }, []);

  const columns: Column<IncomeRow>[] = [
    { id: "date", header: "Date", width: 110, sortValue: (r) => r.date, render: (r) => r.date, csv: (r) => r.date },
    { id: "kind", header: "Type", locked: true, sortValue: (r) => r.kind, render: (r) => r.kind, csv: (r) => r.kind },
    { id: "property", header: "Property", sortValue: (r) => r.property.toLowerCase(), render: (r) => r.property, csv: (r) => r.property },
    { id: "payer", header: "Received from", minWidth: 200, sortValue: (r) => r.payer.toLowerCase(), render: (r) => r.payer, csv: (r) => r.payer },
    { id: "amount", header: "Amount", align: "end", sortValue: (r) => r.amount_paise, render: (r) => r.amount_display, csv: (r) => r.amount_paise / 100 },
    { id: "tds", header: "TDS deducted", align: "end", sortValue: (r) => r.tds_paise, render: (r) => r.tds_display, csv: (r) => r.tds_paise / 100 },
    { id: "status", header: "Status", sortValue: (r) => (r.voided ? 1 : 0), render: (r) => <StatusBadge status={r.voided ? "void" : "posted"} />, csv: (r) => (r.voided ? "Void" : "Posted") },
  ];

  if (view === "new") {
    return (
      <>
        <PageHeader title="Record income" description="Rent, bank interest and other receipts. TDS deducted by the payer is tracked as receivable." />
        <Form className="ao-form" onSubmit={async (ev) => {
          ev.preventDefault();
          if (busy) return;
          setBusy(true);
          try { await api.incomeRecord({ ...f, property_id: f.property_id || null }); setF(blank); setView("list"); setError(""); reload(); } catch (err) { setError(errorText(err)); }
          setBusy(false);
        }}>
          <DraftGuard value={{ f, pname, pkind }} />
          <Stack gap={4}>
            <TextInput size="sm" id="in-date" type="date" labelText="Date received" value={f.date} onChange={(e) => set({ date: e.target.value })} />
            <Select size="sm" id="in-kind" labelText="Type" value={f.kind} onChange={(e) => set({ kind: e.target.value as typeof f.kind })}>
              <SelectItem value="rent" text="Rent" /><SelectItem value="interest" text="Bank interest" /><SelectItem value="other" text="Other income" />
            </Select>
            {f.kind === "rent" && (
              <Select size="sm" id="in-prop" labelText="Property" value={f.property_id || ""} onChange={(e) => set({ property_id: Number(e.target.value) })}>
                <SelectItem value="" text="Select a property" />{props.filter((p) => p.active).map((p) => <SelectItem key={p.id} value={p.id!} text={`${p.name} (${p.kind})`} />)}
              </Select>
            )}
            <TextInput size="sm" id="in-payer" labelText="Received from" value={f.payer} onChange={(e) => set({ payer: e.target.value })} />
            <TextInput size="sm" id="in-amt" labelText="Amount before GST (₹)" value={f.amount} onChange={(e) => set({ amount: e.target.value })} />
            <TextInput size="sm" id="in-gst" labelText="GST charged (₹, regular registration only)" value={f.gst} onChange={(e) => set({ gst: e.target.value })} />
            {f.gst && (
              <Select size="sm" id="in-gstmode" labelText="GST type" value={f.gst_mode} onChange={(e) => set({ gst_mode: e.target.value as "intra" | "inter" })}>
                <SelectItem value="intra" text="CGST + SGST" /><SelectItem value="inter" text="IGST" />
              </Select>
            )}
            <TextInput size="sm" id="in-tds" labelText="TDS deducted by the payer (₹)" value={f.tds} onChange={(e) => set({ tds: e.target.value })} />
            <PaySelect id="in-via" label="Received into" value={f.received_via} onChange={(v) => set({ received_via: v })} allowPayable={false} />
            <div className="toolbar ao-justify-start ao-span">
              <TextInput size="sm" id="pn" labelText="Add a rental property" value={pname} onChange={(e) => setPname(e.target.value)} />
              <Select size="sm" id="pk" labelText="Kind" value={pkind} onChange={(e) => setPkind(e.target.value as "commercial" | "residential")}>
                <SelectItem value="commercial" text="Commercial" /><SelectItem value="residential" text="Residential" />
              </Select>
              <Button kind="tertiary" size="sm" onClick={async () => { try { await api.propertySave({ id: null, name: pname, kind: pkind, address: "", active: true }); setPname(""); loadProps(); setError(""); } catch (err) { setError(errorText(err)); } }}>Add property</Button>
            </div>
            <Notice error={error} />
            <div className="toolbar"><Button size="sm" kind="secondary" onClick={() => { setView("list"); setError(""); }}>Cancel</Button><Button size="sm" type="submit" disabled={busy}>{busy ? "Saving…" : "Record income"}</Button></div>
          </Stack>
        </Form>
      </>
    );
  }

  return (
    <>
      <PageHeader title="Rent and interest" description="Income other than professional fees." actions={canPost && <Button renderIcon={Add} size="sm" onClick={() => setView("new")}>Record income</Button>} />
      <Notice error={error} />
      <DataTable
        id="income" rows={rows} columns={columns} rowKey={(r) => r.id} loading={loading} error={loadError} onRetry={reload}
        searchLabel="Search payer, property or type" exportName="income" searchText={(r) => `${r.payer} ${r.property} ${r.kind}`}
        defaultSort={{ id: "date", dir: "desc" }}
        totals={(l) => <>Income <strong>{inr(l.filter((r) => !r.voided).reduce((n, r) => n + r.amount_paise, 0))}</strong> · TDS <strong>{inr(l.filter((r) => !r.voided).reduce((n, r) => n + r.tds_paise, 0))}</strong></>}
        emptyTitle="No income recorded" emptyHint="Record rent and interest as received."
        rowActions={(r) => canPost && !r.voided ? <RowMenu items={[{ label: "Void…", danger: true, onClick: async () => { const reason = await askReason("Void this entry? Enter the reason."); if (!reason) return; try { await api.incomeVoid(r.id, reason); reload(); } catch (e) { setError(errorText(e)); } } }]} /> : null}
      />
    </>
  );
}

// ============================================================ salaries

export function SalariesSection({ role }: { role: string }) {
  const canPost = canPostRole(role);
  const { data: rows, loading, error: loadError, reload } = useLoad(() => api.payrollList(), [] as PayrollRow[]);
  const [view, setView] = useState<"list" | "new">("list");
  const blank = { date: today(), employee: "", period_label: "", gross: "", tds: "", paid_via: "bank", pf_employee: "", pf_employer: "", esi_employee: "", esi_employer: "", professional_tax: "" };
  const [f, setF] = useState(blank);
  const [basic, setBasic] = useState("");
  // Editable assumptions for the suggestion; statutory rates and limits change, so check these before relying on them.
  const [rates, setRates] = useState({ pf: "12", ceiling: "15000", esiLimit: "21000", esiEe: "0.75", esiEr: "3.25" });
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<typeof f>) => setF({ ...f, ...patch });

  const columns: Column<PayrollRow>[] = [
    { id: "date", header: "Date", width: 110, sortValue: (r) => r.date, render: (r) => r.date, csv: (r) => r.date },
    { id: "employee", header: "Employee", locked: true, minWidth: 200, sortValue: (r) => r.employee.toLowerCase(), render: (r) => r.employee, csv: (r) => r.employee },
    { id: "period", header: "Period", sortValue: (r) => r.period_label, render: (r) => r.period_label, csv: (r) => r.period_label },
    { id: "gross", header: "Gross", align: "end", sortValue: (r) => r.gross_paise, render: (r) => r.gross_display, csv: (r) => r.gross_paise / 100 },
    { id: "ded", header: "Deductions", align: "end", defaultVisible: false, sortValue: (r) => r.gross_paise - r.net_paise, render: (r) => inr(r.gross_paise - r.net_paise), csv: (r) => (r.gross_paise - r.net_paise) / 100 },
    { id: "net", header: "Net paid", align: "end", sortValue: (r) => r.net_paise, render: (r) => r.net_display, csv: (r) => r.net_paise / 100 },
    { id: "status", header: "Status", sortValue: (r) => (r.voided ? 1 : 0), render: (r) => <StatusBadge status={r.voided ? "void" : "posted"} />, csv: (r) => (r.voided ? "Void" : "Posted") },
  ];

  if (view === "new") {
    return (
      <>
        <PageHeader title="Record a salary" description="Statutory amounts post to their own liability accounts until deposited." />
        <Form className="ao-form" onSubmit={async (ev) => {
          ev.preventDefault();
          if (busy) return;
          setBusy(true);
          try { await api.payrollRecord(f); setF({ ...blank, date: f.date, paid_via: f.paid_via }); setView("list"); setError(""); reload(); } catch (err) { setError(errorText(err)); }
          setBusy(false);
        }}>
          <DraftGuard value={{ f, basic, rates }} />
          <Stack gap={4}>
            <TextInput size="sm" id="pr-date" type="date" labelText="Date paid" value={f.date} onChange={(e) => set({ date: e.target.value })} />
            <TextInput size="sm" id="pr-emp" labelText="Employee" value={f.employee} onChange={(e) => set({ employee: e.target.value })} />
            <TextInput size="sm" id="pr-per" labelText="Period (e.g. Oct 2026)" value={f.period_label} onChange={(e) => set({ period_label: e.target.value })} />
            <TextInput size="sm" id="pr-gross" labelText="Gross salary (₹)" value={f.gross} onChange={(e) => set({ gross: e.target.value })} />
            <TextInput size="sm" id="pr-tds" labelText="TDS on salary (₹)" value={f.tds} onChange={(e) => set({ tds: e.target.value })} />
            <PaySelect id="pr-via" label="Paid from" value={f.paid_via} onChange={(v) => set({ paid_via: v })} allowPayable />
            <h4>Provident fund, ESI and professional tax</h4>
            <p>Enter the amounts, or fill them in from the rates below. The rates are assumptions you control; confirm them against current rules and your state.</p>
            <TextInput size="sm" id="pr-basic" labelText="Basic pay (₹), for the PF suggestion" value={basic} onChange={(e) => setBasic(e.target.value)} />
            <div className="toolbar ao-span">
              <TextInput size="sm" id="pr-pfr" labelText="PF rate %" value={rates.pf} onChange={(e) => setRates({ ...rates, pf: e.target.value })} />
              <TextInput size="sm" id="pr-pfc" labelText="PF wage ceiling (₹)" value={rates.ceiling} onChange={(e) => setRates({ ...rates, ceiling: e.target.value })} />
              <TextInput size="sm" id="pr-esil" labelText="ESI gross limit (₹)" value={rates.esiLimit} onChange={(e) => setRates({ ...rates, esiLimit: e.target.value })} />
              <TextInput size="sm" id="pr-esie" labelText="ESI employee %" value={rates.esiEe} onChange={(e) => setRates({ ...rates, esiEe: e.target.value })} />
              <TextInput size="sm" id="pr-esir" labelText="ESI employer %" value={rates.esiEr} onChange={(e) => setRates({ ...rates, esiEr: e.target.value })} />
              <Button kind="tertiary" size="sm" onClick={async () => {
                try {
                  const s = await api.payrollSuggest(basic || f.gross, f.gross, rates.pf, rates.ceiling, rates.esiLimit, rates.esiEe, rates.esiEr);
                  set({ pf_employee: s.pf_employee, pf_employer: s.pf_employer, esi_employee: s.esi_employee, esi_employer: s.esi_employer });
                  setError("");
                } catch (e) { setError(errorText(e)); }
              }}>Suggest PF and ESI</Button>
            </div>
            <TextInput size="sm" id="pr-pfe" labelText="Employee PF (₹)" value={f.pf_employee} onChange={(e) => set({ pf_employee: e.target.value })} />
            <TextInput size="sm" id="pr-pfr2" labelText="Employer PF (₹)" value={f.pf_employer} onChange={(e) => set({ pf_employer: e.target.value })} />
            <TextInput size="sm" id="pr-esie2" labelText="Employee ESI (₹)" value={f.esi_employee} onChange={(e) => set({ esi_employee: e.target.value })} />
            <TextInput size="sm" id="pr-esir2" labelText="Employer ESI (₹)" value={f.esi_employer} onChange={(e) => set({ esi_employer: e.target.value })} />
            <TextInput size="sm" id="pr-pt" labelText="Professional tax (₹)" value={f.professional_tax} onChange={(e) => set({ professional_tax: e.target.value })} />
            <Notice error={error} />
            <div className="toolbar"><Button size="sm" kind="secondary" onClick={() => { setView("list"); setError(""); }}>Cancel</Button><Button size="sm" type="submit" disabled={busy}>{busy ? "Saving…" : "Record salary"}</Button></div>
          </Stack>
        </Form>
      </>
    );
  }

  return (
    <>
      <PageHeader title="Salaries" description="Salary payments with TDS, PF, ESI and professional tax." actions={canPost && <Button renderIcon={Add} size="sm" onClick={() => setView("new")}>Record salary</Button>} />
      <Notice error={error} />
      <DataTable
        id="salaries" rows={rows} columns={columns} rowKey={(r) => r.id} loading={loading} error={loadError} onRetry={reload}
        searchLabel="Search employee or period" exportName="salaries" searchText={(r) => `${r.employee} ${r.period_label}`}
        defaultSort={{ id: "date", dir: "desc" }}
        totals={(l) => <>Gross <strong>{inr(l.filter((r) => !r.voided).reduce((n, r) => n + r.gross_paise, 0))}</strong> · Net <strong>{inr(l.filter((r) => !r.voided).reduce((n, r) => n + r.net_paise, 0))}</strong></>}
        emptyTitle="No salaries recorded" emptyHint="Record each month's salary to keep payroll liabilities current."
        rowActions={(r) => canPost && !r.voided ? <RowMenu items={[{ label: "Void…", danger: true, onClick: async () => { const reason = await askReason("Void this salary entry? Enter the reason."); if (!reason) return; try { await api.payrollVoid(r.id, reason); reload(); } catch (e) { setError(errorText(e)); } } }]} /> : null}
      />
    </>
  );
}

// ============================================================ fixed assets

export function AssetsSection({ role }: { role: string }) {
  const canPost = canPostRole(role);
  const fy = currentFy();
  const { data: rows, loading, error: loadError, reload } = useLoad(() => api.assetList(), [] as AssetRow[]);
  const [view, setView] = useState<"list" | "new">("list");
  const blank = { name: "", purchase_date: today(), cost: "", rate_percent: "15", paid_via: "bank" };
  const [f, setF] = useState(blank);
  const [dep, setDep] = useState(`${fy.from.slice(0, 4)}-${String((Number(fy.from.slice(0, 4)) + 1) % 100).padStart(2, "0")}`);
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<typeof f>) => setF({ ...f, ...patch });

  const columns: Column<AssetRow>[] = [
    { id: "name", header: "Asset", locked: true, minWidth: 200, sortValue: (r) => r.name.toLowerCase(), render: (r) => r.name, csv: (r) => r.name },
    { id: "date", header: "Purchased", sortValue: (r) => r.purchase_date, render: (r) => r.purchase_date, csv: (r) => r.purchase_date },
    { id: "cost", header: "Cost", align: "end", sortValue: (r) => r.cost_paise, render: (r) => r.cost_display, csv: (r) => r.cost_paise / 100 },
    { id: "rate", header: "Rate", align: "end", render: (r) => r.rate, csv: (r) => r.rate },
    { id: "acc", header: "Depreciation to date", align: "end", render: (r) => r.accumulated_display, csv: (r) => r.accumulated_display },
    { id: "book", header: "Book value", align: "end", sortValue: (r) => r.book_value_paise, render: (r) => r.book_value_display, csv: (r) => r.book_value_paise / 100 },
  ];

  if (view === "new") {
    return (
      <>
        <PageHeader title="Add an asset" description="Depreciation is posted separately, once per financial year." />
        <Form className="ao-form" onSubmit={async (ev) => {
          ev.preventDefault();
          if (busy) return;
          setBusy(true);
          try { await api.assetAdd(f); setF(blank); setView("list"); setError(""); reload(); } catch (err) { setError(errorText(err)); }
          setBusy(false);
        }}>
          <DraftGuard value={f} />
          <Stack gap={4}>
            <TextInput size="sm" id="as-name" labelText="Asset" value={f.name} onChange={(e) => set({ name: e.target.value })} />
            <TextInput size="sm" id="as-date" type="date" labelText="Purchase date" value={f.purchase_date} onChange={(e) => set({ purchase_date: e.target.value })} />
            <TextInput size="sm" id="as-cost" labelText="Cost (₹)" value={f.cost} onChange={(e) => set({ cost: e.target.value })} />
            <TextInput size="sm" id="as-rate" labelText="Depreciation rate (% per year, written-down value)" value={f.rate_percent} onChange={(e) => set({ rate_percent: e.target.value })} />
            <PaySelect id="as-via" label="Paid from" value={f.paid_via} onChange={(v) => set({ paid_via: v })} allowPayable />
            <Notice error={error} />
            <div className="toolbar"><Button size="sm" kind="secondary" onClick={() => { setView("list"); setError(""); }}>Cancel</Button><Button size="sm" type="submit" disabled={busy}>{busy ? "Saving…" : "Add asset"}</Button></div>
          </Stack>
        </Form>
      </>
    );
  }

  return (
    <>
      <PageHeader title="Fixed assets" description="Cost and written-down value. Depreciation uses the full rate in the purchase year; check it against your tax schedule." actions={canPost && <Button renderIcon={Add} size="sm" onClick={() => setView("new")}>Add asset</Button>} />
      <Notice error={error} ok={ok} />
      <DataTable
        id="assets" rows={rows} columns={columns} rowKey={(r) => r.id} loading={loading} error={loadError} onRetry={reload}
        searchLabel="Search assets" exportName="fixed-assets" searchText={(r) => r.name}
        defaultSort={{ id: "date", dir: "desc" }}
        toolbarEnd={canPost && (
          <>
            <TextInput id="dep-fy" labelText="Financial year to depreciate" hideLabel size="md" placeholder="2026-27" value={dep} onChange={(e) => setDep(e.target.value)} />
            <Button kind="tertiary" size="md" onClick={async () => { try { const n = await api.assetDepreciate(dep); setOk(`Depreciation posted for ${n} asset(s).`); setError(""); reload(); } catch (e) { setError(errorText(e)); setOk(""); } }}>Post depreciation</Button>
          </>
        )}
        totals={(l) => <>Cost <strong>{inr(l.reduce((n, r) => n + r.cost_paise, 0))}</strong> · Book value <strong>{inr(l.reduce((n, r) => n + r.book_value_paise, 0))}</strong></>}
        emptyTitle="No fixed assets" emptyHint="Add equipment and furniture to track depreciation."
      />
    </>
  );
}

// ============================================================ tax payments

export function TaxPaymentsSection({ role }: { role: string }) {
  const canPost = canPostRole(role);
  const [f, setF] = useState({ kind: "tds", date: today(), amount: "", paid_via: "bank", narration: "" });
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");
  const [busy, setBusy] = useState(false);
  const [cleanToken, setCleanToken] = useState(0);
  const set = (patch: Partial<typeof f>) => setF({ ...f, ...patch });
  return (
    <>
      <PageHeader title="Tax payments" description="Deposits to the government: they reduce the matching liability in the ledger." />
      {!canPost ? <p>Only accountants and the owner can record tax payments.</p> : (
        <Form className="ao-form" onSubmit={async (e) => {
          e.preventDefault();
          if (busy) return;
          setBusy(true);
          try { await api.taxPayment(f.kind, f.date, f.amount, f.paid_via, f.narration); setOk("Payment recorded."); setError(""); setF({ ...f, amount: "", narration: "" }); setCleanToken((n) => n + 1); } catch (err) { setError(errorText(err)); setOk(""); }
          setBusy(false);
        }}>
          <DraftGuard value={f} cleanToken={cleanToken} />
          <Stack gap={4}>
            <Select size="sm" id="tp-kind" labelText="Tax" value={f.kind} onChange={(e) => set({ kind: e.target.value })}>
              <SelectItem value="tds" text="TDS deposited" /><SelectItem value="cgst" text="GST - CGST" /><SelectItem value="sgst" text="GST - SGST" />
              <SelectItem value="igst" text="GST - IGST" /><SelectItem value="composition" text="GST composition tax" /><SelectItem value="income_tax" text="Income tax / advance tax" />
              <SelectItem value="pf" text="Provident fund" /><SelectItem value="esi" text="ESI" /><SelectItem value="professional_tax" text="Professional tax" />
            </Select>
            <TextInput size="sm" id="tp-date" type="date" labelText="Date paid" value={f.date} onChange={(e) => set({ date: e.target.value })} />
            <TextInput size="sm" id="tp-amt" labelText="Amount (₹)" value={f.amount} onChange={(e) => set({ amount: e.target.value })} />
            <PaySelect id="tp-via" label="Paid from" value={f.paid_via} onChange={(v) => set({ paid_via: v })} allowPayable={false} />
            <TextInput size="sm" id="tp-narr" labelText="Challan / reference" value={f.narration} onChange={(e) => set({ narration: e.target.value })} />
            <Notice error={error} ok={ok} />
            <div className="toolbar"><span /><Button size="sm" type="submit" disabled={busy}>{busy ? "Saving…" : "Record payment"}</Button></div>
          </Stack>
        </Form>
      )}
    </>
  );
}

// ============================================================ chart and years

export function AccountSetup({ role }: { role: string }) {
  const canPost = canPostRole(role);
  const canClose = role === "Owner";
  const accounts = useLoad(() => api.accountList(today()), [] as AccountRow[]);
  const periods = useLoad(() => api.periodList(), [] as PeriodRow[]);
  const [view, setView] = useState<"list" | "new" | { period: PeriodRow }>("list");
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [kind, setKind] = useState("expense");
  const [pw, setPw] = useState("");
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");
  const [busy, setBusy] = useState(false);

  const accountColumns: Column<AccountRow>[] = [
    { id: "code", header: "Code", locked: true, width: 100, sortValue: (a) => a.code, render: (a) => a.code, csv: (a) => a.code },
    { id: "name", header: "Account", minWidth: 260, sortValue: (a) => a.name.toLowerCase(), render: (a) => a.name, csv: (a) => a.name },
    { id: "kind", header: "Type", sortValue: (a) => a.kind, render: (a) => a.kind, csv: (a) => a.kind },
    { id: "balance", header: "Balance", align: "end", sortValue: (a) => a.balance_paise, render: (a) => a.balance_display, csv: (a) => a.balance_paise / 100 },
    { id: "status", header: "Status", sortValue: (a) => (a.active ? 0 : 1), render: (a) => <StatusBadge status={a.active ? "active" : "inactive"} />, csv: (a) => (a.active ? "Active" : "Inactive") },
  ];
  const periodColumns: Column<PeriodRow>[] = [
    { id: "fy", header: "Financial year", locked: true, sortValue: (p) => p.start_date, render: (p) => p.fy, csv: (p) => p.fy },
    { id: "from", header: "From", render: (p) => p.start_date, csv: (p) => p.start_date },
    { id: "to", header: "To", render: (p) => p.end_date, csv: (p) => p.end_date },
    { id: "journals", header: "Journals", align: "end", sortValue: (p) => p.journals, render: (p) => p.journals, csv: (p) => p.journals },
    { id: "status", header: "Status", sortValue: (p) => p.status, render: (p) => <StatusBadge status={p.status} />, csv: (p) => p.status },
  ];

  if (view === "new") {
    return (
      <>
        <PageHeader title="Add a ledger account" description="System accounts cannot be removed; new accounts can be deactivated but never deleted." />
        <Form className="ao-form" onSubmit={async (e) => {
          e.preventDefault();
          if (busy) return;
          setBusy(true);
          try { await api.accountCreate(code, name, kind); setCode(""); setName(""); setView("list"); setError(""); accounts.reload(); } catch (err) { setError(errorText(err)); }
          setBusy(false);
        }}>
          <DraftGuard value={{ code, name, kind }} />
          <Stack gap={4}>
            <TextInput size="sm" id="na-code" labelText="Code" value={code} onChange={(e) => setCode(e.target.value)} />
            <TextInput size="sm" id="na-name" labelText="Name" value={name} onChange={(e) => setName(e.target.value)} />
            <Select size="sm" id="na-kind" labelText="Type" value={kind} onChange={(e) => setKind(e.target.value)}>
              {["asset", "liability", "equity", "income", "expense"].map((k) => <SelectItem key={k} value={k} text={k} />)}
            </Select>
            <Notice error={error} />
            <div className="toolbar"><Button size="sm" kind="secondary" onClick={() => { setView("list"); setError(""); }}>Cancel</Button><Button size="sm" type="submit" disabled={busy}>{busy ? "Saving…" : "Add account"}</Button></div>
          </Stack>
        </Form>
      </>
    );
  }

  if (typeof view === "object") {
    const p = view.period;
    const closing = p.status === "open";
    return (
      <>
        <PageHeader title={`${closing ? "Close" : "Reopen"} ${p.fy}`} description={closing ? "A closed year rejects new postings." : "Reopening allows postings dated in this year again."} />
        <Form className="ao-form" onSubmit={async (e) => {
          e.preventDefault();
          if (busy) return;
          setBusy(true);
          try { await api.periodSet(p.fy, closing, pw); setPw(""); setView("list"); setError(""); setOk(`${p.fy} ${closing ? "closed" : "reopened"}.`); periods.reload(); } catch (err) { setError(errorText(err)); setOk(""); }
          setBusy(false);
        }}>
          <DraftGuard value={{ pw }} />
          <Stack gap={4}>
            <PasswordInput size="sm" id="per-pw" labelText="Your password" value={pw} onChange={(e) => setPw(e.target.value)} />
            <Notice error={error} />
            <div className="toolbar"><Button size="sm" kind="secondary" onClick={() => { setView("list"); setError(""); }}>Cancel</Button><Button size="sm" type="submit" disabled={busy}>{busy ? "Saving…" : closing ? "Close year" : "Reopen year"}</Button></div>
          </Stack>
        </Form>
      </>
    );
  }

  return (
    <>
      <PageHeader
        title="Chart and financial years" description="Ledger accounts, and the years that can be closed against further postings."
        actions={canPost && (
          <>
            <Button size="sm" kind="tertiary" onClick={async () => { try { const [i, c, r] = await api.ledgerBackfill(); setOk(`Posted ${i} invoices, ${c} credit notes and ${r} receipts that were not in the ledger yet.`); setError(""); accounts.reload(); periods.reload(); } catch (e) { setError(errorText(e)); setOk(""); } }}>Post earlier documents</Button>
            <Button renderIcon={Add} size="sm" onClick={() => setView("new")}>Add account</Button>
          </>
        )}
      />
      <Notice error={error} ok={ok} />
      <h4>Financial years</h4>
      <DataTable
        id="periods" rows={periods.data} columns={periodColumns} rowKey={(p) => p.fy} loading={periods.loading} error={periods.error} onRetry={periods.reload}
        pageSizes={[20]} defaultSort={{ id: "fy", dir: "desc" }} emptyTitle="No financial years yet" emptyHint="They appear when the first journal is posted."
        rowActions={(p) => canClose ? <Button kind="ghost" size="sm" onClick={() => setView({ period: p })}>{p.status === "open" ? "Close year…" : "Reopen…"}</Button> : null}
      />
      <h4 className="ao-section">Chart of accounts</h4>
      <DataTable
        id="accounts" rows={accounts.data} columns={accountColumns} rowKey={(a) => a.id} loading={accounts.loading} error={accounts.error} onRetry={accounts.reload}
        searchLabel="Search accounts by code or name" exportName="chart-of-accounts" searchText={(a) => `${a.code} ${a.name} ${a.kind}`}
        defaultSort={{ id: "code", dir: "asc" }} pageSizes={[100, 200]}
        emptyTitle="No accounts"
        rowActions={(a) => canPost && !a.is_system ? <Button kind="ghost" size="sm" onClick={async () => { try { await api.accountSetActive(a.id, !a.active); accounts.reload(); } catch (e) { setError(errorText(e)); } }}>{a.active ? "Deactivate" : "Activate"}</Button> : null}
      />
    </>
  );
}
