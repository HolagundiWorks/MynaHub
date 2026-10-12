import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Button, ContentSwitcher, InlineNotification, OverflowMenu, OverflowMenuItem, Search, Select, SelectItem, Stack, Switch, Table, TableBody, TableCell, TableHead,
  TableHeader, TableRow, Tag,
} from "@carbon/react";
import { ArrowLeft, CheckmarkOutline, Copy, Save, Task } from "@carbon/icons-react";
import { api, errorText, type FieldDef, type StudioDiagnostic, type StudioPreview, type StudioState } from "../../api";
import CodeEditor, { type CodeEditorHandle } from "../../components/CodeEditor";
import PdfViewer from "../../components/PdfViewer";
import { askReason, confirmDialog } from "../../components/dialogs";

const FAMILY: Record<string, string> = { consultancy: "Consultancy", billing: "Billing and accounts", administration: "Project administration" };
const MODES = ["code", "preview", "fields"] as const;
type Mode = (typeof MODES)[number];
const PROFILES: [string, string][] = [["short", "Sample: short details"], ["long", "Sample: long names and many rows"], ["company", "My company details, sample project"]];

function DiagnosticsTable({ items }: { items: StudioDiagnostic[] }) {
  if (items.length === 0) return null;
  return (
    <Table size="sm" aria-label="Validation results">
      <TableHead><TableRow><TableHeader>Result</TableHeader><TableHeader>Where</TableHeader><TableHeader>Message</TableHeader></TableRow></TableHead>
      <TableBody>
        {items.map((d, i) => (
          <TableRow key={`${d.source}-${d.line}-${d.col}-${i}`}>
            <TableCell><Tag size="sm" type={d.severity === "error" ? "red" : "warm-gray"}>{d.severity === "error" ? "Error" : "Warning"}</Tag></TableCell>
            <TableCell>{d.source.toUpperCase()} line {d.line}, column {d.col}</TableCell>
            <TableCell>{d.message}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function FieldsPanel({ fields, canInsert, onInsert }: { fields: FieldDef[]; canInsert: boolean; onInsert: (text: string) => void }) {
  const [q, setQ] = useState("");
  const shown = useMemo(() => fields.filter((f) => `${f.path} ${f.label} ${f.group}`.toLowerCase().includes(q.toLowerCase())), [fields, q]);
  const groups = Array.from(new Set(shown.map((f) => f.group)));
  return (
    <Stack gap={4}>
      <Search id="studio-field-search" size="sm" labelText="Search fields" placeholder="Search fields" value={q} onChange={(e: { target: { value: string } }) => setQ(e.target.value)} />
      <p className="ao-muted">
        Placeholders look like {"{{ company.name }}"}. Add a helper after a bar: {"{{ document.date | date }}"}. Repeat a list with {"{{#each fee_items}} … {{/each}}"} and show
        something only when it has a value with {"{{#if company.gstin}} … {{/if}}"}. Values are escaped automatically. Totals and amounts come from ADMS; templates never calculate.
      </p>
      {!canInsert && <InlineNotification kind="info" lowContrast hideCloseButton title="This template is read-only." subtitle="Make a copy to insert fields." />}
      {groups.map((g) => (
        <div key={g}>
          <h4>{g}</h4>
          <Table size="sm" aria-label={`${g} fields`}>
            <TableHead><TableRow><TableHeader>Field</TableHeader><TableHeader>Placeholder</TableHeader><TableHeader>Type</TableHeader><TableHeader>Availability</TableHeader><TableHeader>Example</TableHeader><TableHeader>Insert</TableHeader></TableRow></TableHead>
            <TableBody>
              {shown.filter((f) => f.group === g).map((f) => {
                const text = f.kind === "list" ? `{{#each ${f.path}}}\n  {{ description }} {{ formatted_amount }}\n{{/each}}` : `{{ ${f.path} }}`;
                return (
                  <TableRow key={f.path}>
                    <TableCell>{f.label} {f.required ? <Tag size="sm" type="blue">Required</Tag> : <Tag size="sm" type="gray">Optional</Tag>}</TableCell>
                    <TableCell><code>{f.kind === "list" ? `{{#each ${f.path}}}` : `{{ ${f.path} }}`}</code></TableCell>
                    <TableCell>{f.kind === "block" ? "Written text" : f.kind[0].toUpperCase() + f.kind.slice(1)}</TableCell>
                    <TableCell>{f.available}</TableCell>
                    <TableCell>{f.example}</TableCell>
                    <TableCell><Button size="sm" kind="ghost" disabled={!canInsert} onClick={() => onInsert(text)}>{`Insert ${f.label}`}</Button></TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </div>
      ))}
      {groups.length === 0 && <p className="ao-muted">No field matches your search.</p>}
    </Stack>
  );
}

export default function StudioPage({ templateId, role, onBack, onOpenOther }: { templateId: number; role: string; onBack: () => void; onOpenOther: (id: number) => void }) {
  const [studio, setStudio] = useState<StudioState | null>(null);
  const [fields, setFields] = useState<FieldDef[]>([]);
  const [html, setHtml] = useState("");
  const [css, setCss] = useState("");
  const [baseline, setBaseline] = useState({ html: "", css: "" });
  const [mode, setMode] = useState<Mode>("code");
  const [section, setSection] = useState<"html" | "css">("html");
  const [profile, setProfile] = useState("short");
  const [diagnostics, setDiagnostics] = useState<StudioDiagnostic[]>([]);
  const [preview, setPreview] = useState<StudioPreview | null>(null);
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");
  const [busy, setBusy] = useState(false);
  const htmlRef = useRef<CodeEditorHandle>(null);
  const latest = useRef(0);

  const reload = useCallback(async (keepEdits = false) => {
    const s = await api.studioOpen(templateId);
    setStudio(s);
    setBaseline({ html: s.html, css: s.css });
    if (!keepEdits) { setHtml(s.html); setCss(s.css); }
  }, [templateId]);

  useEffect(() => {
    reload().catch((e) => setError(errorText(e)));
    api.studioFields().then(setFields).catch(() => undefined);
  }, [reload]);

  const canEdit = role === "Owner" && !!studio && !studio.built_in && !studio.archived;
  const dirty = !!studio && (html !== baseline.html || css !== baseline.css);

  // Validation as you type (debounced), shown as markers in the editors and in the results list.
  useEffect(() => {
    if (!studio) return;
    const ticket = ++latest.current;
    const t = setTimeout(() => {
      api.studioValidate(html, css).then((d) => { if (ticket === latest.current) setDiagnostics(d); }).catch(() => undefined);
    }, 400);
    return () => clearTimeout(t);
  }, [html, css, studio]);

  // Live preview only while the Preview mode is open; stale renders are discarded.
  useEffect(() => {
    if (mode !== "preview" || !studio) return;
    const ticket = ++latest.current;
    const t = setTimeout(async () => {
      try {
        const p = await api.studioPreview(html, css, profile);
        if (ticket === latest.current) { setPreview(p); setError(""); }
      } catch (e) {
        if (ticket === latest.current) setError(errorText(e));
      }
    }, 700);
    return () => clearTimeout(t);
  }, [mode, html, css, profile, studio]);

  const run = async (fn: () => Promise<void>) => {
    setBusy(true);
    try { await fn(); setError(""); } catch (e) { setError(errorText(e)); setOk(""); } finally { setBusy(false); }
  };

  const save = () => run(async () => { await api.studioSaveDraft(templateId, html, css); await reload(true); setOk("Draft saved."); });
  const publish = () => run(async () => {
    const summary = await askReason("What changed in this version?");
    if (summary === null) return;
    if (dirty) await api.studioSaveDraft(templateId, html, css);
    const v = await api.studioPublish(templateId, summary);
    await reload();
    setOk(`Published as version ${v}. Documents already issued are not changed.`);
  });
  const validate = () => run(async () => {
    const d = await api.studioValidate(html, css);
    setDiagnostics(d);
    setOk(d.some((x) => x.severity === "error") ? "" : d.length ? "No errors. There are warnings." : "No errors or warnings.");
  });
  const copy = () => run(async () => {
    for (let n = 1; n <= 9; n++) {
      const name = n === 1 ? `${studio?.name} (our copy)` : `${studio?.name} (our copy ${n})`;
      try {
        onOpenOther(await api.templateDuplicate(templateId, name));
        return;
      } catch (e) {
        if (!errorText(e).includes("already exists")) throw e;
      }
    }
    throw new Error("Could not find a free name for the copy.");
  });
  const restore = (version: number) => run(async () => {
    if (dirty && !(await confirmDialog("Replace your unsaved changes with that version?", { danger: true }))) return;
    await api.studioRestore(templateId, version);
    await reload();
    setOk(`Version ${version} is now in your draft. Publish it to make a new version.`);
  });
  const discard = () => run(async () => {
    if (!(await confirmDialog("Discard the draft and go back to the published version?", { danger: true }))) return;
    await api.studioDiscardDraft(templateId);
    await reload();
    setOk("Draft discarded.");
  });
  const back = async () => {
    if (dirty && !(await confirmDialog("You have unsaved changes. Leave without saving?", { danger: true }))) return;
    onBack();
  };

  const insert = (text: string) => {
    setSection("html");
    setMode("code");
    // the editor is shown again on the next frame; insert once it is visible
    setTimeout(() => htmlRef.current?.insert(text), 0);
  };

  if (!studio) return error ? <InlineNotification kind="error" title={error} hideCloseButton lowContrast /> : null;

  const errors = diagnostics.filter((d) => d.severity === "error");
  const status = studio.has_draft || dirty ? "Draft" : "Published";

  return (
    <Stack gap={4}>
      <div className="toolbar">
        <h3>
          {studio.name}{" "}
          <Tag size="sm" type="gray">{FAMILY[studio.family]}</Tag>{" "}
          <Tag size="sm" type={status === "Draft" ? "purple" : "green"}>{status}</Tag>{" "}
          <Tag size="sm" type="blue">v{studio.published_version}</Tag>{" "}
          {studio.built_in && <Tag size="sm" type="cool-gray">Built-in original</Tag>}
          {dirty && <Tag size="sm" type="red">Unsaved changes</Tag>}
        </h3>
        <Button renderIcon={ArrowLeft} kind="secondary" size="sm" onClick={back}>Back</Button>
      </div>
      <div className="ao-actions">
        {canEdit && <Button size="sm" renderIcon={Save} disabled={busy || !dirty} onClick={save}>Save draft</Button>}
        <Button size="sm" kind="tertiary" renderIcon={CheckmarkOutline} disabled={busy} onClick={validate}>Validate</Button>
        {canEdit && <Button size="sm" renderIcon={Task} disabled={busy || errors.length > 0} onClick={publish}>Publish as new version</Button>}
        {role === "Owner" && studio.built_in && <Button size="sm" kind="primary" renderIcon={Copy} disabled={busy} onClick={copy}>Customise: make an editable copy</Button>}
        {canEdit && (
          <OverflowMenu size="sm" flipped iconDescription="More template actions" aria-label="More template actions">
            {studio.versions.map((v) => (
              <OverflowMenuItem key={v.version} itemText={`Restore version ${v.version} as a draft`} onClick={() => restore(v.version)} />
            ))}
            {studio.has_draft && <OverflowMenuItem itemText="Discard the draft" isDelete hasDivider onClick={discard} />}
          </OverflowMenu>
        )}
      </div>
      <p className="ao-muted">
        Practice template, not an official Council of Architecture form. HTML and CSS only: no scripts, links or remote files, and only the approved placeholders and fonts.
        Set the paper and margins with an @page rule, for example @page {"{ size: A4; margin: 18mm 16mm; }"}.
      </p>
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      {ok && <InlineNotification kind="success" title={ok} hideCloseButton lowContrast />}
      {studio.built_in && role === "Owner" && (
        <InlineNotification kind="info" lowContrast hideCloseButton title="You are viewing a built-in original, so the editors are read-only." subtitle="Choose “Customise: make an editable copy” above to edit your own copy." />
      )}

      <ContentSwitcher size="sm" selectedIndex={MODES.indexOf(mode)} onChange={({ index }: { index?: number }) => setMode(MODES[index ?? 0])}>
        <Switch name="code" text="Code" />
        <Switch name="preview" text="Preview" />
        <Switch name="fields" text="Fields" />
      </ContentSwitcher>

      <div hidden={mode !== "code"}>
        <Stack gap={3}>
          <ContentSwitcher size="sm" selectedIndex={section === "html" ? 0 : 1} onChange={({ index }: { index?: number }) => setSection(index === 1 ? "css" : "html")}>
            <Switch name="html" text="HTML" />
            <Switch name="css" text="CSS" />
          </ContentSwitcher>
          <div hidden={section !== "html"}>
            <CodeEditor ref={htmlRef} language="html" label="Template HTML" value={html} onChange={setHtml} readOnly={!canEdit} fields={fields} diagnostics={diagnostics.filter((d) => d.source === "html")} />
          </div>
          <div hidden={section !== "css"}>
            <CodeEditor language="css" label="Template CSS" value={css} onChange={setCss} readOnly={!canEdit} diagnostics={diagnostics.filter((d) => d.source === "css")} />
          </div>
          <DiagnosticsTable items={diagnostics} />
        </Stack>
      </div>

      {mode === "preview" && (
        <Stack gap={3}>
          <div className="toolbar">
            <Select size="sm" id="studio-profile" labelText="Sample data" hideLabel value={profile} onChange={(e) => setProfile(e.target.value)}>
              {PROFILES.map(([v, t]) => <SelectItem key={v} value={v} text={t} />)}
            </Select>
            {preview && preview.pages > 0 && <span><Tag size="sm" type="gray">{preview.pages} page{preview.pages === 1 ? "" : "s"}</Tag> <Tag size="sm" type="gray">A4 {preview.landscape ? "landscape" : "portrait"} or as set by @page</Tag></span>}
          </div>
          <DiagnosticsTable items={preview?.diagnostics ?? diagnostics} />
          {preview?.pdf_base64
            ? <PdfViewer base64={preview.pdf_base64} filename={`${studio.name}-test.pdf`} />
            : <p className="ao-muted">{preview && preview.diagnostics.some((d) => d.severity === "error") ? "Fix the errors above to see the preview." : "Rendering the preview…"}</p>}
          <p className="ao-muted">
            This is the same renderer that produces issued PDFs. Use the viewer controls for page navigation and zoom, and Download PDF to save a test copy. Nothing is stored or numbered.
          </p>
        </Stack>
      )}

      {mode === "fields" && <FieldsPanel fields={fields} canInsert={canEdit} onInsert={insert} />}

      <h4>Versions</h4>
      <Table size="sm" aria-label="Published versions">
        <TableHead><TableRow><TableHeader>Version</TableHeader><TableHeader>Published (UTC)</TableHeader><TableHeader>What changed</TableHeader></TableRow></TableHead>
        <TableBody>
          {studio.versions.map((v) => (
            <TableRow key={v.version}><TableCell>v{v.version}</TableCell><TableCell>{v.published_at}</TableCell><TableCell>{v.change_summary}</TableCell></TableRow>
          ))}
        </TableBody>
      </Table>
    </Stack>
  );
}
