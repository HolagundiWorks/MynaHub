import { useEffect, useState } from "react";
import {
  Button, Form, InlineNotification, Select, SelectItem, Stack, Table, TableBody, TableCell,
  TableHead, TableHeader, TableRow, TextInput,
} from "@carbon/react";
import { api, errorText, type Balance, type ReceiptInput, type ReceiptRow } from "../../api";
import { askReason } from "../../components/dialogs";
import PdfViewer from "../../components/PdfViewer";
import { ArrowLeft, Money } from "@carbon/icons-react";

// Common TDS sections for professional / contract receipts; the rate only seeds a suggestion.
const TDS_SECTIONS = [
  { code: "194J", label: "194J – Professional fees (10%)", rate: "10" },
  { code: "194C-1", label: "194C – Contractor, individual/HUF (1%)", rate: "1" },
  { code: "194C-2", label: "194C – Contractor, others (2%)", rate: "2" },
  { code: "194M", label: "194M – Individual/HUF payer (5%)", rate: "5" },
  { code: "Other", label: "Other (enter amount manually)", rate: "" },
];

const today = () => new Date().toISOString().slice(0, 10);

export default function PaymentPanel({ invoiceId, number, onClose }: { invoiceId: number; number: string; onClose: () => void }) {
  const [balance, setBalance] = useState<Balance | null>(null);
  const [receipts, setReceipts] = useState<ReceiptRow[]>([]);
  const [form, setForm] = useState<ReceiptInput>({
    invoice_id: invoiceId, received_date: today(), amount: "", tds: "", tds_section: "", mode: "bank", reference: "", notes: "",
  });
  const [error, setError] = useState("");
  const [done, setDone] = useState("");
  const [pdf, setPdf] = useState<string | null>(null);

  const load = () => {
    api.receiptBalance(invoiceId).then(setBalance).catch((e) => setError(errorText(e)));
    api.receiptList(invoiceId).then(setReceipts).catch((e) => setError(errorText(e)));
  };
  useEffect(load, [invoiceId]);

  const set = <K extends keyof ReceiptInput>(k: K, v: ReceiptInput[K]) => setForm({ ...form, [k]: v });

  const pickSection = async (code: string) => {
    const sec = TDS_SECTIONS.find((s) => s.code === code);
    const next = { ...form, tds_section: code };
    if (sec?.rate) {
      try { next.tds = await api.receiptSuggestTds(invoiceId, sec.rate); } catch (e) { setError(errorText(e)); }
    }
    setForm(next);
  };

  if (pdf) return <PdfViewer base64={pdf} filename="receipt.pdf" onClose={() => setPdf(null)} />;

  return (
    <Form className="ao-form"
      onSubmit={async (e) => {
        e.preventDefault();
        try {
          const n = await api.receiptRecord(form);
          setDone(`Recorded as ${n}.`);
          setError("");
          setForm({ ...form, amount: "", tds: "", tds_section: "", reference: "", notes: "" });
          load();
        } catch (err) { setError(errorText(err)); setDone(""); }
      }}
    >
      <Stack gap={5}>
        <h3>Record payment — {number}</h3>
        {balance && (
          <p>
            Invoice {balance.total_display} · received {balance.received_display} · TDS {balance.tds_display} ·{" "}
            <strong>outstanding {balance.balance_display}</strong>
            {balance.credited_display !== "₹0.00" && <> · credit notes {balance.credited_display}</>}
          </p>
        )}
        <TextInput size="sm" id="r-date" type="date" labelText="Payment date" value={form.received_date} onChange={(e) => set("received_date", e.target.value)} />
        <TextInput size="sm" id="r-amount" labelText="Amount received in bank (₹)" value={form.amount} onChange={(e) => set("amount", e.target.value)} />
        <Select size="sm" id="r-section" labelText="TDS deducted by client" value={form.tds_section} onChange={(e) => pickSection(e.target.value)}>
          <SelectItem value="" text="No TDS" />
          {TDS_SECTIONS.map((s) => <SelectItem key={s.code} value={s.code} text={s.label} />)}
        </Select>
        {form.tds_section && (
          <TextInput size="sm" id="r-tds" labelText="TDS amount (₹)" helperText="Suggested on the invoice value excluding GST; edit to match the client's deduction."
            value={form.tds} onChange={(e) => set("tds", e.target.value)} />
        )}
        <Select size="sm" id="r-mode" labelText="Mode" value={form.mode} onChange={(e) => set("mode", e.target.value as ReceiptInput["mode"])}>
          <SelectItem value="bank" text="Bank transfer" />
          <SelectItem value="upi" text="UPI" />
          <SelectItem value="cheque" text="Cheque" />
          <SelectItem value="cash" text="Cash" />
        </Select>
        <TextInput size="sm" id="r-ref" labelText="Bank reference / UTR" value={form.reference} onChange={(e) => set("reference", e.target.value)} />
        <TextInput size="sm" id="r-notes" labelText="Notes" value={form.notes} onChange={(e) => set("notes", e.target.value)} />
        {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
        {done && <InlineNotification kind="success" title={done} hideCloseButton lowContrast />}
        <div className="toolbar">
          <Button renderIcon={ArrowLeft} size="sm" kind="secondary" onClick={onClose}>Back</Button>
          <Button renderIcon={Money} size="sm" type="submit" disabled={balance?.balance_paise === 0}>Record payment</Button>
        </div>

        {receipts.length > 0 && (
          <>
            <h4>Payments so far</h4>
            <Table size="sm">
              <TableHead>
                <TableRow>
                  <TableHeader>Receipt</TableHeader><TableHeader>Date</TableHeader><TableHeader>Received</TableHeader>
                  <TableHeader>TDS</TableHeader><TableHeader>Reference</TableHeader><TableHeader />
                </TableRow>
              </TableHead>
              <TableBody>
                {receipts.map((r) => (
                  <TableRow key={r.number}>
                    <TableCell>{r.number}</TableCell><TableCell>{r.received_date}</TableCell><TableCell>{r.amount_display}</TableCell>
                    <TableCell>{r.tds_display}{r.tds_section ? ` (${r.tds_section})` : ""}</TableCell><TableCell>{r.reference}</TableCell>
                    <TableCell>
                      {r.reversed ? <em>Reversed</em> : (
                        <>
                          <Button kind="ghost" size="sm" onClick={async () => { try { setPdf(await api.receiptPdf(r.id)); } catch (e) { setError(errorText(e)); } }}>PDF</Button>
                          <Button kind="ghost" size="sm" onClick={async () => {
                            const reason = await askReason(`Reverse ${r.number}? Enter the reason.`);
                            if (!reason) return;
                            try { await api.receiptReverse(r.id, reason); load(); } catch (e) { setError(errorText(e)); }
                          }}>Reverse</Button>
                        </>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </>
        )}
      </Stack>
    </Form>
  );
}
