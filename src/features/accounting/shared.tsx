import { useCallback, useEffect, useId, useState, type ReactNode } from "react";
import { InlineNotification, Select, SelectItem, Table, TableBody, TableHead, TableHeader, TableRow } from "@carbon/react";
import { api, errorText, type BankAccount } from "../../api";

export const today = () => new Date().toISOString().slice(0, 10);

export function currentFy(): { from: string; to: string } {
  const d = new Date();
  const y = d.getMonth() >= 3 ? d.getFullYear() : d.getFullYear() - 1;
  return { from: `${y}-04-01`, to: `${y + 1}-03-31` };
}

export function Notice({ error, ok }: { error: string; ok?: string }) {
  return (
    <>
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      {ok && <InlineNotification kind="success" title={ok} hideCloseButton lowContrast />}
    </>
  );
}

/** Loads a list with distinct loading / failed / loaded states, so a failure is never shown as "no records". */
export function useLoad<T>(fn: () => Promise<T>, initial: T, deps: unknown[] = []) {
  const [data, setData] = useState<T>(initial);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const reload = useCallback(() => {
    setLoading(true);
    fn().then((d) => { setData(d); setError(""); }).catch((e) => setError(errorText(e))).finally(() => setLoading(false));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  useEffect(reload, [reload]);
  return { data, loading, error, reload };
}

const NUMERIC = /amount|debit|credit|balance|total|received|tds|gross|net|cost|rate|share|value|depreciation|tax|payable|input|output|profit/i;

/** Read-only report output on Carbon's Table. Amount columns are end-aligned (a documented exception: Carbon has no numeric option). */
export function Grid({ head, children }: { head: string[]; children: ReactNode }) {
  const uid = useId().replace(/:/g, "");
  const numeric = head.map((h, i) => (NUMERIC.test(h) ? i + 1 : 0)).filter(Boolean);
  const css = numeric.map((n) => `.g${uid} th:nth-child(${n}), .g${uid} td:nth-child(${n}) { text-align: end; font-variant-numeric: tabular-nums; }`).join(" ");
  return (
    <>
      <style>{css}</style>
      <div className={`g${uid} ao-block`}>
        <Table size="sm" aria-label="Report">
          <TableHead><TableRow>{head.map((h) => <TableHeader key={h}>{h}</TableHeader>)}</TableRow></TableHead>
          <TableBody>{children}</TableBody>
        </Table>
      </div>
    </>
  );
}

export function usePayOptions(allowPayable: boolean) {
  const [accounts, setAccounts] = useState<BankAccount[]>([]);
  useEffect(() => { api.bankAccounts().then(setAccounts).catch(() => undefined); }, []);
  return [
    { v: "bank", t: "Bank (primary account)" },
    ...accounts.filter((a) => a.active).map((a) => ({ v: `bank:${a.id}`, t: a.name })),
    { v: "cash", t: "Cash" },
    ...(allowPayable ? [{ v: "payable", t: "Not paid yet (payable)" }] : []),
  ];
}

export function PaySelect({ id, label, value, onChange, allowPayable }: { id: string; label: string; value: string; onChange: (v: string) => void; allowPayable: boolean }) {
  const options = usePayOptions(allowPayable);
  return (
    <Select id={id} labelText={label} value={value} onChange={(e) => onChange(e.target.value)}>
      {options.map((o) => <SelectItem key={o.v} value={o.v} text={o.t} />)}
    </Select>
  );
}

export const inr = (paise: number) => (paise / 100).toLocaleString("en-IN", { style: "currency", currency: "INR" });
