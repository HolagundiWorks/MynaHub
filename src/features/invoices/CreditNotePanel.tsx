import { useEffect, useState } from "react";
import {
  Button, Form, InlineNotification, Stack, Table, TableBody, TableCell, TableHead, TableHeader, TableRow, TextInput,
} from "@carbon/react";
import { api, errorText, type CreditNoteRow, type LineInput, type Totals } from "../../api";
import { confirmDialog } from "../../components/dialogs";
import { DraftGuard } from "../../app/unsaved";
import PdfViewer from "../../components/PdfViewer";
import { ArrowLeft, Document } from "@carbon/icons-react";

const today = () => new Date().toISOString().slice(0, 10);
const rupees = (paise: number) => (paise / 100).toLocaleString("en-IN", { style: "currency", currency: "INR" });

export default function CreditNotePanel({ invoiceId, number, onClose }: { invoiceId: number; number: string; onClose: () => void }) {
  const [notes, setNotes] = useState<CreditNoteRow[]>([]);
  const [date, setDate] = useState(today());
  const [reason, setReason] = useState("");
  const [lines, setLines] = useState<LineInput[]>([{ description: "", qty: "1", rate: "" }]);
  const [totals, setTotals] = useState<Totals | null>(null);
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");
  const [pdf, setPdf] = useState<string | null>(null);
  const [cleanToken, setCleanToken] = useState(0);

  const load = () => api.creditNoteList(invoiceId).then(setNotes).catch((e) => setError(errorText(e)));
  useEffect(() => { load(); }, [invoiceId]);

  // Preview of the amounts, computed natively: the credit note uses the original invoice's GST treatment.
  useEffect(() => {
    api.invoiceGet(invoiceId).then((d) =>
      api.invoiceCalculate({ ...d.input, lines }).then(setTotals).catch(() => setTotals(null)),
    ).catch(() => setTotals(null));
  }, [invoiceId, lines]);

  if (pdf) return <PdfViewer base64={pdf} filename="credit-note.pdf" onClose={() => setPdf(null)} />;

  const setLine = (i: number, k: keyof LineInput, v: string) => setLines(lines.map((l, j) => (j === i ? { ...l, [k]: v } : l)));

  return (
    <Form className="ao-form" onSubmit={async (e) => {
      e.preventDefault();
      if (!(await confirmDialog("Issue this credit note? It cannot be edited afterwards."))) return;
      try {
        const n = await api.creditNoteIssue(invoiceId, date, reason, lines);
        setOk(`Issued ${n}.`); setError(""); setReason(""); setLines([{ description: "", qty: "1", rate: "" }]); setCleanToken((t) => t + 1); load();
      } catch (err) { setError(errorText(err)); setOk(""); }
    }}>
      <DraftGuard value={{ date, reason, lines }} cleanToken={cleanToken} />
      <Stack gap={5}>
        <h3>Credit notes — {number}</h3>
        {notes.length > 0 && (
          <Table size="sm">
            <TableHead><TableRow><TableHeader>Number</TableHeader><TableHeader>Date</TableHeader><TableHeader>Reason</TableHeader><TableHeader>Total</TableHeader><TableHeader /></TableRow></TableHead>
            <TableBody>
              {notes.map((n) => (
                <TableRow key={n.id}>
                  <TableCell>{n.number}</TableCell><TableCell>{n.issue_date}</TableCell><TableCell>{n.reason}</TableCell><TableCell>{n.total_display}</TableCell>
                  <TableCell><Button kind="ghost" size="sm" onClick={async () => { try { setPdf(await api.creditNotePdf(n.id)); } catch (e) { setError(errorText(e)); } }}>PDF</Button></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        <h4>New credit note</h4>
        <p>Reduces what the client owes on this invoice. GST follows the original invoice. It cannot exceed the amount still owed.</p>
        <TextInput size="sm" id="cn-date" type="date" labelText="Date" value={date} onChange={(e) => setDate(e.target.value)} />
        <TextInput size="sm" id="cn-reason" labelText="Reason" value={reason} onChange={(e) => setReason(e.target.value)} />
        {lines.map((l, i) => (
          <Stack key={i} gap={3}>
            <TextInput size="sm" id={`cn-d-${i}`} labelText="Description" value={l.description} onChange={(e) => setLine(i, "description", e.target.value)} />
            <TextInput size="sm" id={`cn-q-${i}`} labelText="Qty" value={l.qty} onChange={(e) => setLine(i, "qty", e.target.value)} />
            <TextInput size="sm" id={`cn-r-${i}`} labelText="Rate (₹)" value={l.rate} onChange={(e) => setLine(i, "rate", e.target.value)} />
            {lines.length > 1 && <div><Button kind="ghost" size="sm" onClick={() => setLines(lines.filter((_, j) => j !== i))}>Remove line</Button></div>}
          </Stack>
        ))}
        <div><Button kind="tertiary" size="sm" onClick={() => setLines([...lines, { description: "", qty: "1", rate: "" }])}>Add line</Button></div>
        {totals && <p><strong>Credit total: {rupees(totals.total_paise)}</strong> (tax {rupees(totals.tax_paise)})</p>}
        {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
        {ok && <InlineNotification kind="success" title={ok} hideCloseButton lowContrast />}
        <div className="toolbar">
          <Button renderIcon={ArrowLeft} size="sm" kind="secondary" onClick={onClose}>Back</Button>
          <Button renderIcon={Document} size="sm" type="submit">Issue credit note</Button>
        </div>
      </Stack>
    </Form>
  );
}
