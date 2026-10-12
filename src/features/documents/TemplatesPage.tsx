import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Button, Form, InlineNotification, Select, SelectItem, Stack, Table, TableBody, TableCell, TableHead, TableHeader, TableRow, Tag, TextArea,
  TextInput, Toggle,
} from "@carbon/react";
import { Add, ArrowLeft, Copy, Task, View } from "@carbon/icons-react";
import { api, errorText, type TemplatePreview, type TemplateRow, type VersionRow } from "../../api";
import PageHeader from "../../components/PageHeader";
import PdfViewer from "../../components/PdfViewer";
import TemplateCanvas from "./TemplateCanvas";
import StudioPage from "./StudioPage";
import FilterSelect from "../../components/FilterSelect";
import RowMenu from "../../components/data-table/RowMenu";
import DataTable, { type Column } from "../../components/data-table/DataTable";
import { askReason } from "../../components/dialogs";

const FAMILY: Record<string, string> = { consultancy: "Consultancy", billing: "Billing and accounts", administration: "Project administration" };
const LAYOUT: Record<string, string> = { letter: "Letter with addressee", record: "Titled record" };

/** Practice templates are not official forms; this is shown wherever templates are chosen or edited. */
function PracticeNotice() {
  return (
    <InlineNotification
      kind="info" lowContrast hideCloseButton title="Practice templates."
      subtitle="These are editable starting points oriented to Indian architectural practice. They are not official Council of Architecture forms, and the wording should be reviewed by the practice, and by counsel where appropriate, before use."
    />
  );
}

function PreviewPanel({ preview, onClose }: { preview: TemplatePreview; onClose: () => void }) {
  return (
    <Stack gap={4}>
      {preview.warnings.map((w) => <InlineNotification key={w} kind="warning" lowContrast hideCloseButton title={w} />)}
      {preview.warnings.length === 0 && <InlineNotification kind="success" lowContrast hideCloseButton title="Nothing is missing for this template." />}
      <p className="ao-muted">Sample project and client data are used. Your company profile is real. Nothing is saved or numbered.</p>
      <PdfViewer base64={preview.pdf_base64} filename="template-preview.pdf" onClose={onClose} />
    </Stack>
  );
}

export default function TemplatesPage({ role }: { role: string }) {
  const canEdit = role === "Owner";
  const [templates, setTemplates] = useState<TemplateRow[]>([]);
  const [fields, setFields] = useState<string[]>([]);
  const [selected, setSelected] = useState<TemplateRow | null>(null);
  const [versions, setVersions] = useState<VersionRow[]>([]);
  const [body, setBody] = useState("");
  const [creating, setCreating] = useState<{ kind: string; family: string; layout: string; name: string; format: "wording" | "html" } | null>(null);
  const [studioId, setStudioId] = useState<number | null>(null);
  const [preview, setPreview] = useState<TemplatePreview | null>(null);
  const [family, setFamily] = useState("");
  const [format, setFormat] = useState("");
  const [showArchived, setShowArchived] = useState(false);
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");
  const [canvasKey, setCanvasKey] = useState(0);

  const load = useCallback(() => {
    api.templateList(null, true).then(setTemplates).catch((e) => setError(errorText(e)));
    api.docFields().then(setFields).catch(() => undefined);
  }, []);
  useEffect(load, [load]);

  const open = async (t: TemplateRow) => {
    if (t.format === "html") { setStudioId(t.id); return; }
    setSelected(t);
    setBody(t.body);
    setOk(""); setError(""); setPreview(null); setCanvasKey((k) => k + 1);
    try { setVersions(await api.templateVersions(t.id)); } catch (e) { setError(errorText(e)); }
  };

  const showPreview = async (b: string, t: { layout: string; kind: string; name: string }) => {
    try { setPreview(await api.templatePreview(b, t.layout, t.kind, t.name)); setError(""); } catch (e) { setError(errorText(e)); }
  };

  const copy = async (t: TemplateRow) => {
    const name = await askReason(`Name for your copy of “${t.name}”`);
    if (!name) return;
    try {
      const id = await api.templateDuplicate(t.id, name);
      const fresh = await api.templateList(null, true);
      setTemplates(fresh);
      const mine = fresh.find((x) => x.id === id);
      if (mine) await open(mine);
      setOk(`Created your copy “${name}”.`);
    } catch (e) { setError(errorText(e)); }
  };

  /** One click: makes your own editable copy of a built-in (named "<name> (our copy)") and opens it. */
  const customise = async (t: TemplateRow) => {
    for (let n = 1; n <= 9; n++) {
      const name = n === 1 ? `${t.name} (our copy)` : `${t.name} (our copy ${n})`;
      try {
        const id = await api.templateDuplicate(t.id, name);
        const fresh = await api.templateList(null, true);
        setTemplates(fresh);
        const mine = fresh.find((x) => x.id === id);
        if (mine) await open(mine);
        setOk(`Created your editable copy “${name}”. The original is unchanged.`);
        return;
      } catch (e) {
        if (!errorText(e).includes("already exists")) { setError(errorText(e)); return; }
      }
    }
    setError("Could not find a free name for the copy. Make a copy from the row menu and choose a name.");
  };

  const setArchived = async (t: TemplateRow, archived: boolean) => {
    try { await api.templateSetArchived(t.id, archived); setError(""); load(); } catch (e) { setError(errorText(e)); }
  };

  const shown = useMemo(() => templates.filter((t) => (showArchived || !t.archived) && (!family || t.family === family) && (!format || t.format === format)), [templates, family, format, showArchived]);

  if (studioId !== null) {
    return <StudioPage key={studioId} templateId={studioId} role={role} onBack={() => { setStudioId(null); load(); }} onOpenOther={(id) => { setStudioId(id); load(); }} />;
  }

  if (preview) {
    return <PreviewPanel preview={preview} onClose={() => setPreview(null)} />;
  }

  if (creating) {
    return (
      <Form className="ao-form" onSubmit={async (e) => {
        e.preventDefault();
        try {
          if (creating.format === "html") {
            const id = await api.studioCreate(creating.name, creating.family, creating.kind);
            setCreating(null); setError(""); load(); setStudioId(id);
            return;
          }
          await api.templateCreate(creating.kind, creating.family, creating.layout, creating.name, body);
          setCreating(null); setBody(""); setError(""); load();
        } catch (err) { setError(errorText(err)); }
      }}>
        <Stack gap={4}>
          <h3>New template</h3>
          <Select size="sm" id="nt-format" labelText="Format" value={creating.format} helperText="HTML templates are designed in the Template Studio with a code editor and live preview." onChange={(e) => setCreating({ ...creating, format: e.target.value as "wording" | "html" })}>
            <SelectItem value="wording" text="Wording with the design canvas" /><SelectItem value="html" text="HTML and CSS (Template Studio)" />
          </Select>
          <Select size="sm" id="nt-kind" labelText="Type" value={creating.kind} onChange={(e) => setCreating({ ...creating, kind: e.target.value })}>
            <SelectItem value="letter" text="Letter or record" /><SelectItem value="agreement" text="Agreement with fee milestones" />
          </Select>
          <Select size="sm" id="nt-family" labelText="Family" value={creating.family} onChange={(e) => setCreating({ ...creating, family: e.target.value })}>
            {Object.entries(FAMILY).map(([v, t]) => <SelectItem key={v} value={v} text={t} />)}
          </Select>
          {creating.format === "wording" && (
            <Select size="sm" id="nt-layout" labelText="Layout" value={creating.layout} onChange={(e) => setCreating({ ...creating, layout: e.target.value })}>
              {Object.entries(LAYOUT).map(([v, t]) => <SelectItem key={v} value={v} text={t} />)}
            </Select>
          )}
          <TextInput size="sm" id="nt-name" labelText="Name" value={creating.name} onChange={(e) => setCreating({ ...creating, name: e.target.value })} />
          {creating.format === "wording" && <TemplateCanvas key={canvasKey} value={body} onChange={setBody} fields={fields} layout={creating.layout} kind={creating.kind} name={creating.name} />}
          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
          <div className="toolbar">
            <Button size="sm" kind="secondary" onClick={() => { setCreating(null); setError(""); }}>Cancel</Button>
            <Button renderIcon={Add} size="sm" type="submit">Create</Button>
          </div>
        </Stack>
      </Form>
    );
  }

  if (selected) {
    const editable = canEdit && !selected.built_in;
    return (
      <Stack gap={4}>
        <div className="toolbar">
          <h3>
            {selected.name} <Tag type="gray" size="sm">{FAMILY[selected.family]}</Tag> <Tag type="blue" size="sm">v{selected.latest_version}</Tag>{" "}
            {selected.built_in && <Tag type="cool-gray" size="sm">Built-in original</Tag>}{selected.archived && <Tag type="warm-gray" size="sm">Archived</Tag>}
          </h3>
          <Button renderIcon={ArrowLeft} kind="secondary" size="sm" onClick={() => { setSelected(null); load(); }}>Back</Button>
        </div>
        <PracticeNotice />
        {selected.description && <p className="ao-muted">{selected.description}</p>}
        <p className="ao-muted">
          {LAYOUT[selected.layout]}. Wording only: the letterhead, numbering and page layout are applied by the app. Use blank lines between
          paragraphs, “# ” for a heading and “- ” for bullets. Text in [[double brackets]] is a prompt to replace; a document cannot be issued while
          any remain.
        </p>
        {selected.built_in && canEdit && (
          <InlineNotification
            kind="info" lowContrast hideCloseButton title="This is a built-in original and is not edited."
            subtitle="Make a copy to customise the wording. Your copy is versioned; the original and any documents already issued are never changed."
          />
        )}
        {editable
          ? <TemplateCanvas key={canvasKey} value={body} onChange={setBody} fields={fields} layout={selected.layout} kind={selected.kind} name={selected.name} />
          : <TextArea id="t-body" labelText="Wording" rows={16} value={body} readOnly onChange={() => undefined} />}
        {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
        {ok && <InlineNotification kind="success" title={ok} hideCloseButton lowContrast />}
        <div className="ao-actions">
          <Button size="sm" kind="tertiary" renderIcon={View} onClick={() => showPreview(body, selected)}>Preview with sample data</Button>
          {canEdit && selected.built_in && <Button size="sm" renderIcon={Copy} onClick={() => copy(selected)}>Make a copy to edit</Button>}
          {editable && (
            <Button size="sm" renderIcon={Task} onClick={async () => {
              try {
                const v = await api.templatePublish(selected.id, body);
                setOk(`Published as version ${v}.`); setError("");
                const fresh = (await api.templateList(null, true)).find((t) => t.id === selected.id);
                if (fresh) { setSelected(fresh); setVersions(await api.templateVersions(fresh.id)); }
              } catch (e) { setError(errorText(e)); setOk(""); }
            }}>Publish as new version</Button>
          )}
        </div>
        <h4>Version history</h4>
        <Table size="sm" aria-label="Version history">
          <TableHead><TableRow><TableHeader>Version</TableHeader><TableHeader>Published (UTC)</TableHeader><TableHeader>Action</TableHeader></TableRow></TableHead>
          <TableBody>
            {versions.map((v) => (
              <TableRow key={v.version}>
                <TableCell>v{v.version}</TableCell><TableCell>{v.published_at}</TableCell>
                <TableCell><Button kind="ghost" size="sm" onClick={() => { setBody(v.body); setCanvasKey((k) => k + 1); }}>Load into editor</Button></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Stack>
    );
  }

  const columns: Column<TemplateRow>[] = [
    { id: "name", header: "Template", locked: true, minWidth: 260, sortValue: (t) => t.name.toLowerCase(), render: (t) => t.name, csv: (t) => t.name },
    { id: "family", header: "Family", sortValue: (t) => t.family, render: (t) => FAMILY[t.family], csv: (t) => FAMILY[t.family] },
    { id: "type", header: "Type", sortValue: (t) => t.kind, render: (t) => (t.format === "html" ? "HTML template" : t.kind === "agreement" ? "Agreement" : LAYOUT[t.layout]), csv: (t) => (t.format === "html" ? "html" : t.kind) },
    { id: "version", header: "Version", align: "end", sortValue: (t) => t.latest_version, render: (t) => `v${t.latest_version}`, csv: (t) => t.latest_version },
    {
      id: "status", header: "Status", sortValue: (t) => (t.archived ? 2 : t.built_in ? 0 : 1),
      render: (t) => (t.archived ? <Tag type="warm-gray" size="sm">Archived</Tag> : t.built_in ? <Tag type="cool-gray" size="sm">Built-in original</Tag> : <Tag type="green" size="sm">Your template</Tag>),
      csv: (t) => (t.archived ? "Archived" : t.built_in ? "Built-in" : "Your template"),
    },
  ];
  return (
    <>
      <PageHeader
        title="Templates"
        description="Wording for letters, agreements and records. Built-in originals are copied before editing; each publish is a new version."
        actions={canEdit && (
          <>
            <Button renderIcon={Add} size="sm" onClick={() => { setCreating({ kind: "letter", family: "administration", layout: "letter", name: "", format: "html" }); setBody(""); setCanvasKey((k) => k + 1); }}>New HTML template</Button>
            <Button renderIcon={Add} size="sm" kind="tertiary" onClick={() => { setCreating({ kind: "letter", family: "administration", layout: "letter", name: "", format: "wording" }); setBody(""); setCanvasKey((k) => k + 1); }}>New wording template</Button>
          </>
        )}
      />
      <PracticeNotice />
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      {ok && <InlineNotification kind="success" title={ok} hideCloseButton lowContrast />}
      <DataTable
        id="templates" rows={shown} columns={columns} rowKey={(t) => t.id} pageSizes={[50]}
        searchLabel="Search templates" searchText={(t) => `${t.name} ${FAMILY[t.family]} ${t.description}`} defaultSort={{ id: "name", dir: "asc" }}
        emptyTitle="No templates"
        toolbarEnd={
          <>
            <FilterSelect label="Format" value={format} onChange={setFormat} options={[["", "All formats"], ["html", "HTML templates (Template Studio)"], ["wording", "Wording templates"]]} />
            <FilterSelect label="Family" value={family} onChange={setFamily} options={[["", "All families"], ...Object.entries(FAMILY)] as [string, string][]} />
            <Toggle id="tpl-archived" size="sm" labelText="Show archived" hideLabel labelA="Archived hidden" labelB="Archived shown" toggled={showArchived} onToggle={setShowArchived} />
          </>
        }
        rowActions={(t) => (
          <div className="ao-actions">
            {canEdit && t.built_in && t.format === "html"
              ? <Button kind="primary" size="sm" renderIcon={Copy} onClick={() => customise(t)}>Customise</Button>
              : <Button kind="ghost" size="sm" onClick={() => open(t)}>{canEdit && !t.built_in ? "Edit" : "View"}</Button>}
            <RowMenu items={[
              { label: t.format === "html" ? "View the original" : "Preview with sample data", onClick: () => (t.format === "html" ? setStudioId(t.id) : showPreview(t.body, t)) },
              ...(canEdit ? [{ label: "Make a copy", onClick: () => copy(t) }, { label: t.archived ? "Restore" : "Archive", onClick: () => setArchived(t, !t.archived) }] : []),
            ]} />
          </div>
        )}
      />
    </>
  );
}
