import { useCallback, useEffect, useState } from "react";
import { Button, FileUploaderButton, Form, InlineNotification, Select, SelectItem, Stack, Table, TableBody, TableCell, TableHead, TableHeader, TableRow, Tag, TextArea, TextInput } from "@carbon/react";
import {
  api, errorText, type AttachmentRow, type DocDetail, type DocInput, type DocRow, type InwardInput, type Project, type TemplateRow,
} from "../../api";
import PdfViewer from "../../components/PdfViewer";
import PageHeader from "../../components/PageHeader";
import FilterSelect from "../../components/FilterSelect";
import { askReason, confirmDialog } from "../../components/dialogs";
import StatusBadge from "../../components/StatusBadge";
import DataTable, { type Column } from "../../components/data-table/DataTable";
import { useServerTable } from "../../components/data-table/useServerTable";
import { downloadBase64, readFileAsBase64 } from "../../components/files";
import { Add, ArrowLeft, Save } from "@carbon/icons-react";
import { DraftGuard, DraftSave } from "../../app/unsaved";
import { useCreateIntent } from "../../app/intent";

const FAMILY_SHORT: Record<string, string> = { consultancy: "Consultancy", billing: "Billing", administration: "Administration" };

const today = () => new Date().toISOString().slice(0, 10);
const KIND_LABEL: Record<string, string> = { letter_out: "Letter (out)", letter_in: "Letter (in)", agreement: "Agreement" };
const STATUS_TAG: Record<string, "gray" | "blue" | "green" | "red"> = { draft: "gray", issued: "blue", signed: "green", cancelled: "red" };

const blankDoc = (kind: DocInput["kind"]): DocInput => ({
  id: null, kind, project_id: null, party_name: "", party_address: "", subject: "", reference: "", doc_date: today(), body: "",
  template_version_id: null, fee: "", milestones: kind === "agreement" ? [{ name: "", percent: "" }] : [],
});

function Editor({ doc, projects, onClose }: { doc: DocInput; projects: Project[]; onClose: () => void }) {
  const [d, setD] = useState<DocInput>(doc);
  const [templates, setTemplates] = useState<TemplateRow[]>([]);
  const [fields, setFields] = useState<string[]>([]);
  const [error, setError] = useState("");
  const [pdf, setPdf] = useState<string | null>(null);
  const [cleanToken, setCleanToken] = useState(0);

  useEffect(() => {
    api.templateList(d.kind === "agreement" ? "agreement" : "letter").then(setTemplates).catch((e) => setError(errorText(e)));
    api.docFields().then(setFields).catch(() => undefined);
  }, [d.kind]);

  const set = <K extends keyof DocInput>(k: K, v: DocInput[K]) => setD({ ...d, [k]: v });
  const totalPct = d.milestones.reduce((n, m) => n + (parseFloat(m.percent) || 0), 0);
  const prompts = (d.body.match(/\[\[[^\]]*\]\]/g) ?? []).length;
  const isHtml = templates.find((t) => t.latest_version_id === d.template_version_id)?.format === "html";
  const isRecord = templates.find((t) => t.latest_version_id === d.template_version_id)?.layout === "record";

  const save = async (): Promise<number | null> => {
    try {
      const id = await api.docSaveDraft(d);
      setD({ ...d, id });
      setCleanToken((n) => n + 1);
      setError("");
      return id;
    } catch (e) { setError(errorText(e)); return null; }
  };

  if (pdf) return <PdfViewer base64={pdf} filename="document.pdf" onClose={() => setPdf(null)} />;

  return (
    <Form className="ao-form" onSubmit={(e) => e.preventDefault()}>
      <DraftGuard value={d} cleanToken={cleanToken} />
      <DraftSave save={() => { void save(); }} />
      <Stack gap={5}>
        <h3>{d.id ? "Edit draft" : "New"} {KIND_LABEL[d.kind].toLowerCase()}</h3>
        <Select size="sm" id="d-project" labelText={d.kind === "agreement" ? "Project" : "Project (optional)"} value={d.project_id ?? ""}
          onChange={(e) => set("project_id", e.target.value ? Number(e.target.value) : null)}>
          <SelectItem value="" text={d.kind === "agreement" ? "Select a project" : "No project"} />
          {projects.map((p) => <SelectItem key={p.id} value={p.id!} text={`${p.client_name} — ${p.name}`} />)}
        </Select>
        {d.kind === "letter_out" && (
          <>
            <TextInput size="sm" id="d-party" labelText="To (defaults to the project's client)" value={d.party_name} onChange={(e) => set("party_name", e.target.value)} />
            <TextArea id="d-addr" labelText="Address" rows={2} value={d.party_address} onChange={(e) => set("party_address", e.target.value)} />
          </>
        )}
        <TextInput size="sm" id="d-subject" labelText={d.kind === "agreement" ? "Title" : isRecord ? "Title (optional; defaults to the template name)" : "Subject"} value={d.subject} onChange={(e) => set("subject", e.target.value)} />
        <TextInput size="sm" id="d-ref" labelText="Reference" value={d.reference} onChange={(e) => set("reference", e.target.value)} />
        <TextInput size="sm" id="d-date" type="date" labelText="Date" value={d.doc_date} onChange={(e) => set("doc_date", e.target.value)} />
        <Select size="sm" id="d-template" labelText="Start from template (replaces the text below)" value="" onChange={(e) => {
          const t = templates.find((x) => x.id === Number(e.target.value));
          if (t) setD({ ...d, body: t.format === "html" ? "" : t.body, template_version_id: t.latest_version_id });
        }}>
          <SelectItem value="" text="Choose a template…" />
          {templates.map((t) => <SelectItem key={t.id} value={t.id} text={`${FAMILY_SHORT[t.family]}: ${t.name} (v${t.latest_version})`} />)}
        </Select>
        {isHtml ? (
          <InlineNotification
            kind="info" lowContrast hideCloseButton title="This template controls the layout."
            subtitle="Write only the text for this document below. The template places it, together with the firm, client, project and fee details."
          />
        ) : (
          <div>
            <strong>Insert a field:</strong>{" "}
            {fields.map((f) => <Button key={f} kind="ghost" size="sm" onClick={() => set("body", `${d.body}{{${f}}}`)}>{`{{${f}}}`}</Button>)}
          </div>
        )}
        <TextArea id="d-body" labelText={isHtml ? "Document text (plain paragraphs; separate paragraphs with a blank line)" : "Text"} rows={14} value={d.body} onChange={(e) => set("body", e.target.value)} />
        {prompts > 0 && (
          <InlineNotification
            kind="warning" lowContrast hideCloseButton title={`${prompts} prompt${prompts === 1 ? "" : "s"} in [[double brackets]] still to replace or delete.`}
            subtitle="A document cannot be issued while any remain."
          />
        )}

        {d.kind === "agreement" && (
          <>
            <TextInput size="sm" id="d-fee" labelText="Agreed professional fee (₹, excluding GST)" value={d.fee} onChange={(e) => set("fee", e.target.value)} />
            <h4>Payment milestones</h4>
            {d.milestones.map((m, i) => (
              <div key={i} className="toolbar">
                <TextInput size="sm" id={`m-n-${i}`} labelText="Milestone" value={m.name} onChange={(e) => set("milestones", d.milestones.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
                <TextInput size="sm" id={`m-p-${i}`} labelText="Share (%)" value={m.percent} onChange={(e) => set("milestones", d.milestones.map((x, j) => (j === i ? { ...x, percent: e.target.value } : x)))} />
                {d.milestones.length > 1 && <Button kind="ghost" size="sm" onClick={() => set("milestones", d.milestones.filter((_, j) => j !== i))}>Remove</Button>}
              </div>
            ))}
            <div>
              <Button kind="tertiary" size="sm" onClick={() => set("milestones", [...d.milestones, { name: "", percent: "" }])}>Add milestone</Button>
              <span> Total: {totalPct}% {totalPct !== 100 && "(must be 100% to issue)"}</span>
            </div>
          </>
        )}

        {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
        <div className="toolbar">
          <Button size="sm" kind="secondary" onClick={onClose}>Close</Button>
          <div>
            <Button renderIcon={Save} size="sm" kind="tertiary" onClick={save}>Save draft</Button>
            <Button size="sm" kind="tertiary" onClick={async () => { const id = await save(); if (id == null) return; try { setPdf(await api.docPdf(id)); } catch (e) { setError(errorText(e)); } }}>Preview PDF</Button>
            <Button size="sm" onClick={async () => {
              const id = await save();
              if (id == null) return;
              if (!(await confirmDialog("Issue this document? It gets a number and its content can no longer be edited."))) return;
              try { await api.docIssue(id); onClose(); } catch (e) { setError(errorText(e)); }
            }}>Issue</Button>
          </div>
        </div>
      </Stack>
    </Form>
  );
}

function InwardForm({ projects, onClose }: { projects: Project[]; onClose: () => void }) {
  const [i, setI] = useState<InwardInput>({ project_id: null, party_name: "", subject: "", reference: "", doc_date: today(), notes: "" });
  const [error, setError] = useState("");
  const set = <K extends keyof InwardInput>(k: K, v: InwardInput[K]) => setI({ ...i, [k]: v });
  return (
    <Form className="ao-form" onSubmit={async (e) => {
      e.preventDefault();
      try { await api.docRegisterInward(i); onClose(); } catch (err) { setError(errorText(err)); }
    }}>
      <DraftGuard value={i} />
      <Stack gap={5}>
        <h3>Register an inward letter</h3>
        <TextInput size="sm" id="in-party" labelText="Received from" value={i.party_name} onChange={(e) => set("party_name", e.target.value)} />
        <TextInput size="sm" id="in-subject" labelText="Subject" value={i.subject} onChange={(e) => set("subject", e.target.value)} />
        <TextInput size="sm" id="in-ref" labelText="Sender's reference" value={i.reference} onChange={(e) => set("reference", e.target.value)} />
        <TextInput size="sm" id="in-date" type="date" labelText="Date received" value={i.doc_date} onChange={(e) => set("doc_date", e.target.value)} />
        <Select size="sm" id="in-project" labelText="Project (optional)" value={i.project_id ?? ""} onChange={(e) => set("project_id", e.target.value ? Number(e.target.value) : null)}>
          <SelectItem value="" text="No project" />
          {projects.map((p) => <SelectItem key={p.id} value={p.id!} text={`${p.client_name} — ${p.name}`} />)}
        </Select>
        <TextArea id="in-notes" labelText="Remarks" rows={3} value={i.notes} onChange={(e) => set("notes", e.target.value)} />
        <p>After registering, open the entry to attach the scanned letter.</p>
        {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
        <div className="toolbar"><Button size="sm" kind="secondary" onClick={onClose}>Cancel</Button><Button renderIcon={Add} size="sm" type="submit">Register</Button></div>
      </Stack>
    </Form>
  );
}

function Detail({ id, onClose, onOpen }: { id: number; onClose: () => void; onOpen: (id: number) => void }) {
  const [d, setD] = useState<DocDetail | null>(null);
  const [atts, setAtts] = useState<AttachmentRow[]>([]);
  const [pdf, setPdf] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");

  const load = useCallback(() => {
    api.docGet(id).then(setD).catch((e) => setError(errorText(e)));
    api.docAttachments(id).then(setAtts).catch((e) => setError(errorText(e)));
  }, [id]);
  useEffect(load, [load]);

  if (pdf) return <PdfViewer base64={pdf} filename={`${d?.number ?? "document"}.pdf`.replace(/\//g, "_")} onClose={() => setPdf(null)} />;
  if (!d) return error ? <InlineNotification kind="error" title={error} hideCloseButton lowContrast /> : null;

  const kind = d.input.kind;
  const upload = async (file: File | undefined, as: "attachment" | "signed_copy") => {
    if (!file) return;
    try { await api.docAttach(id, as, file.name, await readFileAsBase64(file)); setOk("File attached."); setError(""); load(); } catch (e) { setError(errorText(e)); setOk(""); }
  };

  return (
    <Stack gap={5}>
      <div className="toolbar">
        <h3>{d.number} <Tag type={STATUS_TAG[d.status] ?? "gray"}>{d.status}</Tag></h3>
        <Button renderIcon={ArrowLeft} kind="secondary" size="sm" onClick={onClose}>Back</Button>
      </div>
      <p>{KIND_LABEL[kind]} · {d.input.doc_date} · {d.input.party_name} · {d.input.subject}{d.input.reference && ` · ${d.input.reference}`}</p>
      {d.input.body && kind === "letter_in" && <p>{d.input.body}</p>}
      <div>
        {kind !== "letter_in" && <Button kind="tertiary" size="sm" onClick={async () => { try { setPdf(await api.docPdf(id)); } catch (e) { setError(errorText(e)); } }}>View PDF</Button>}
        {kind !== "letter_in" && (d.status === "issued" || d.status === "signed") && (
          <Button kind="tertiary" size="sm" onClick={async () => { try { onOpen(await api.docRevise(id)); } catch (e) { setError(errorText(e)); } }}>Create revision</Button>
        )}
        {kind === "agreement" && d.status === "issued" && <Button kind="tertiary" size="sm" onClick={async () => { try { await api.docMarkSigned(id); load(); } catch (e) { setError(errorText(e)); } }}>Mark as signed</Button>}
        {kind !== "letter_in" && d.status === "issued" && (
          <Button kind="ghost" size="sm" onClick={async () => {
            const reason = await askReason("Cancel this document? Enter the reason.");
            if (!reason) return;
            try { await api.docCancel(id, reason); load(); } catch (e) { setError(errorText(e)); }
          }}>Cancel</Button>
        )}
      </div>

      {kind === "agreement" && d.milestone_rows.length > 0 && (
        <>
          <h4>Fee schedule — {d.input.fee}</h4>
          <Table size="sm">
            <TableHead><TableRow><TableHeader>Milestone</TableHeader><TableHeader>Share</TableHeader><TableHeader>Amount</TableHeader><TableHeader>Billing</TableHeader></TableRow></TableHead>
            <TableBody>
              {d.milestone_rows.map((m) => (
                <TableRow key={m.id}>
                  <TableCell>{m.name}</TableCell><TableCell>{m.percent}%</TableCell><TableCell>{m.amount_display}</TableCell>
                  <TableCell>
                    {m.invoice_id ? <Tag type="green">{m.invoice_number ?? "Draft invoice"}</Tag> : (d.status === "issued" || d.status === "signed") && (
                      <Button kind="ghost" size="sm" onClick={async () => {
                        try { await api.docBillMilestone(m.id, today()); setOk("A draft invoice was created. Find it under Invoices."); setError(""); load(); } catch (e) { setError(errorText(e)); setOk(""); }
                      }}>Create invoice</Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </>
      )}

      <h4>Files</h4>
      {atts.length === 0 && <p>No files attached.</p>}
      {atts.map((a) => (
        <div key={a.id} className="toolbar">
          <span>{a.filename} {a.kind === "signed_copy" && <Tag type="green">signed copy</Tag>} · {(a.bytes / 1024).toFixed(0)} KB</span>
          <Button kind="ghost" size="sm" onClick={async () => { try { const f = await api.docAttachmentData(a.id); downloadBase64(f.filename, f.base64_data); } catch (e) { setError(errorText(e)); } }}>Download</Button>
        </div>
      ))}
      {d.status !== "cancelled" && (
        <div className="ao-actions">
          <FileUploaderButton labelText="Attach a file" buttonKind="tertiary" size="md" disableLabelChanges accept={[".pdf", ".png", ".jpg", ".jpeg", ".docx", ".xlsx", ".txt"]}
            onChange={(e: { target: { files: FileList | null; value: string } }) => { upload(e.target.files?.[0], "attachment"); e.target.value = ""; }} />
          {kind === "agreement" && d.status === "issued" && (
            <FileUploaderButton labelText="Attach the signed copy" buttonKind="tertiary" size="md" disableLabelChanges accept={[".pdf", ".png", ".jpg", ".jpeg"]}
              onChange={(e: { target: { files: FileList | null; value: string } }) => { upload(e.target.files?.[0], "signed_copy"); e.target.value = ""; }} />
          )}
          <p className="ao-muted">Allowed: pdf, png, jpg, docx, xlsx, txt, up to 15 MB.</p>
        </div>
      )}
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      {ok && <InlineNotification kind="success" title={ok} hideCloseButton lowContrast />}
    </Stack>
  );
}

type View = { type: "list" } | { type: "edit"; doc: DocInput } | { type: "inward" } | { type: "detail"; id: number };

export default function DocumentsPage() {
  const [view, setView] = useState<View>({ type: "list" });
  const [projects, setProjects] = useState<Project[]>([]);
  const [kind, setKind] = useState("");
  const [status, setStatus] = useState("");
  const [error, setError] = useState("");
  const openNew = useCallback(() => setView({ type: "edit", doc: blankDoc("letter_out") }), []);
  useCreateIntent("documents", openNew);

  const table = useServerTable<DocRow>({ id: "documents", fetch: api.docPage, filters: { kind, status }, defaultSort: { id: "date", dir: "desc" } });
  const reloadTable = table.reload;
  const load = useCallback(() => {
    api.projectList(null, false).then(setProjects).catch((e) => setError(errorText(e)));
    reloadTable();
  }, [reloadTable]);
  useEffect(() => { api.projectList(null, false).then(setProjects).catch((e) => setError(errorText(e))); }, []);

  const back = () => { setView({ type: "list" }); load(); };
  const openRow = async (r: DocRow) => {
    if (r.status === "draft") {
      try { setView({ type: "edit", doc: (await api.docGet(r.id)).input }); } catch (e) { setError(errorText(e)); }
    } else setView({ type: "detail", id: r.id });
  };
  const openId = async (id: number) => {
    try { const d = await api.docGet(id); setView(d.status === "draft" ? { type: "edit", doc: d.input } : { type: "detail", id }); } catch (e) { setError(errorText(e)); }
  };

  if (view.type === "edit") return <Editor doc={view.doc} projects={projects} onClose={back} />;
  if (view.type === "inward") return <InwardForm projects={projects} onClose={back} />;
  if (view.type === "detail") return <Detail id={view.id} onClose={back} onOpen={openId} />;

  const columns: Column<DocRow>[] = [
    { id: "number", header: "Number", locked: true, minWidth: 170, sortValue: (r) => r.number ?? "~draft", render: (r) => r.number ?? "Draft", csv: (r) => r.number ?? "Draft" },
    { id: "type", header: "Type", sortValue: (r) => r.kind, render: (r) => KIND_LABEL[r.kind], csv: (r) => KIND_LABEL[r.kind] },
    { id: "date", header: "Date", width: 110, sortValue: (r) => r.doc_date, render: (r) => r.doc_date, csv: (r) => r.doc_date },
    { id: "party", header: "Party / client", minWidth: 200, sortValue: (r) => (r.party_name || r.client_name).toLowerCase(), render: (r) => r.party_name || r.client_name, csv: (r) => r.party_name || r.client_name },
    { id: "subject", header: "Subject", minWidth: 260, sortValue: (r) => r.subject.toLowerCase(), render: (r) => r.subject, csv: (r) => r.subject },
    { id: "project", header: "Project", defaultVisible: false, sortValue: (r) => r.project_name.toLowerCase(), render: (r) => r.project_name, csv: (r) => r.project_name },
    { id: "reference", header: "Reference", defaultVisible: false, render: (r) => r.reference, csv: (r) => r.reference },
    { id: "status", header: "Status", sortValue: (r) => r.status, render: (r) => <span><StatusBadge status={r.status} />{r.signed && <span className="ao-muted ao-inline-start">signed copy on file</span>}</span>, csv: (r) => r.status },
  ];

  return (
    <>
      <PageHeader
        title="Letters and agreements"
        description="Outward and inward letter registers and consultancy agreements. Issued documents are frozen; changes are revisions."
        actions={
          <>
            <Button size="sm" kind="tertiary" onClick={() => setView({ type: "inward" })}>Register inward letter</Button>
            <Button size="sm" kind="tertiary" onClick={() => setView({ type: "edit", doc: blankDoc("agreement") })}>New agreement</Button>
            <Button size="sm" onClick={() => setView({ type: "edit", doc: blankDoc("letter_out") })}>New letter</Button>
          </>
        }
      />
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      <DataTable
        id="documents" rows={table.rows} columns={columns} rowKey={(r) => r.id} loading={table.loading} error={table.error} onRetry={table.reload}
        server={table.server} searchLabel="Search number, subject, party, reference, client or project" exportName="document-register"
        filters={
          <>
            <FilterSelect label="Filter by document type" value={kind} onChange={setKind} options={[["", "All documents"], ["letter_out", "Outward letters"], ["letter_in", "Inward letters"], ["agreement", "Agreements"]]} />
            <FilterSelect label="Filter by status" value={status} onChange={setStatus} options={[["", "All statuses"], ["draft", "Draft"], ["issued", "Issued"], ["signed", "Signed"], ["cancelled", "Cancelled"]]} />
          </>
        }
        activeFilters={[
          ...(kind ? [{ label: KIND_LABEL[kind], clear: () => setKind("") }] : []),
          ...(status ? [{ label: `Status: ${status}`, clear: () => setStatus("") }] : []),
        ]}
        emptyTitle="No documents yet" emptyHint="Create a letter or agreement, or register a letter you received."
        rowActions={(r) => <Button kind="ghost" size="sm" onClick={() => openRow(r)}>{r.status === "draft" ? "Edit" : "Open"}</Button>}
      />
    </>
  );
}
