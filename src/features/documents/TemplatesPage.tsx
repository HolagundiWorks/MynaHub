import { useCallback, useEffect, useState } from "react";
import {
  Button, Form, InlineNotification, Select, SelectItem, Stack, Table, TableBody, TableCell, TableHead, TableHeader,
  TableRow, Tag, TextArea, TextInput,
} from "@carbon/react";
import { api, errorText, type TemplateRow, type VersionRow } from "../../api";
import PageHeader from "../../components/PageHeader";
import DataTable, { type Column } from "../../components/data-table/DataTable";

export default function TemplatesPage({ role }: { role: string }) {
  const canEdit = role === "Owner";
  const [templates, setTemplates] = useState<TemplateRow[]>([]);
  const [fields, setFields] = useState<string[]>([]);
  const [selected, setSelected] = useState<TemplateRow | null>(null);
  const [versions, setVersions] = useState<VersionRow[]>([]);
  const [body, setBody] = useState("");
  const [creating, setCreating] = useState<{ kind: string; name: string } | null>(null);
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");

  const load = useCallback(() => {
    api.templateList(null).then(setTemplates).catch((e) => setError(errorText(e)));
    api.docFields().then(setFields).catch(() => undefined);
  }, []);
  useEffect(load, [load]);

  const open = async (t: TemplateRow) => {
    setSelected(t);
    setBody(t.body);
    setOk(""); setError("");
    try { setVersions(await api.templateVersions(t.id)); } catch (e) { setError(errorText(e)); }
  };

  const insertField = (f: string) => setBody(`${body}{{${f}}}`);

  if (creating) {
    return (
      <Form className="ao-form" onSubmit={async (e) => {
        e.preventDefault();
        try {
          await api.templateCreate(creating.kind, creating.name, body);
          setCreating(null); setBody(""); setError(""); load();
        } catch (err) { setError(errorText(err)); }
      }}>
        <Stack gap={5}>
          <h3>New template</h3>
          <Select id="nt-kind" labelText="Type" value={creating.kind} onChange={(e) => setCreating({ ...creating, kind: e.target.value })}>
            <SelectItem value="letter" text="Letter" /><SelectItem value="agreement" text="Agreement" />
          </Select>
          <TextInput id="nt-name" labelText="Name" value={creating.name} onChange={(e) => setCreating({ ...creating, name: e.target.value })} />
          <TextArea id="nt-body" labelText="Wording" rows={12} value={body} onChange={(e) => setBody(e.target.value)} />
          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
          <div className="toolbar">
            <Button kind="secondary" onClick={() => { setCreating(null); setError(""); }}>Cancel</Button>
            <Button type="submit">Create</Button>
          </div>
        </Stack>
      </Form>
    );
  }

  if (selected) {
    return (
      <Stack gap={5}>
        <div className="toolbar">
          <h3>{selected.name} <Tag type="gray">{selected.kind}</Tag> <Tag type="blue">v{selected.latest_version}</Tag></h3>
          <Button kind="secondary" size="sm" onClick={() => { setSelected(null); load(); }}>Back</Button>
        </div>
        <p>
          Wording only: the letterhead, numbering and layout are applied by the app. Published versions are never changed;
          documents already issued keep the version and text they were issued with. Use blank lines between paragraphs,
          “# ” for a heading and “- ” for bullets.
        </p>
        <div>
          <strong>Insert a field:</strong>{" "}
          {fields.map((f) => <Button key={f} kind="ghost" size="sm" disabled={!canEdit} onClick={() => insertField(f)}>{`{{${f}}}`}</Button>)}
        </div>
        <TextArea id="t-body" labelText="Wording" rows={16} value={body} readOnly={!canEdit} onChange={(e) => setBody(e.target.value)} />
        {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
        {ok && <InlineNotification kind="success" title={ok} hideCloseButton lowContrast />}
        {canEdit && (
          <div>
            <Button onClick={async () => {
              try {
                const v = await api.templatePublish(selected.id, body);
                setOk(`Published as version ${v}.`); setError("");
                const fresh = (await api.templateList(null)).find((t) => t.id === selected.id);
                if (fresh) { setSelected(fresh); setVersions(await api.templateVersions(fresh.id)); }
              } catch (e) { setError(errorText(e)); setOk(""); }
            }}>Publish as new version</Button>
          </div>
        )}
        <h4>Version history</h4>
        <Table size="sm">
          <TableHead><TableRow><TableHeader>Version</TableHeader><TableHeader>Published (UTC)</TableHeader><TableHeader /></TableRow></TableHead>
          <TableBody>
            {versions.map((v) => (
              <TableRow key={v.version}>
                <TableCell>v{v.version}</TableCell><TableCell>{v.published_at}</TableCell>
                <TableCell><Button kind="ghost" size="sm" onClick={() => setBody(v.body)}>Load into editor</Button></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Stack>
    );
  }

  const columns: Column<TemplateRow>[] = [
    { id: "name", header: "Template", locked: true, minWidth: 260, sortValue: (t) => t.name.toLowerCase(), render: (t) => t.name, csv: (t) => t.name },
    { id: "kind", header: "Type", sortValue: (t) => t.kind, render: (t) => (t.kind === "agreement" ? "Agreement" : "Letter"), csv: (t) => t.kind },
    { id: "version", header: "Latest version", align: "end", sortValue: (t) => t.latest_version, render: (t) => `v${t.latest_version}`, csv: (t) => t.latest_version },
  ];
  return (
    <>
      <PageHeader
        title="Templates"
        description="Wording for letters and agreements. Each publish is a new version; issued documents keep the version they used."
        actions={canEdit && <Button onClick={() => { setCreating({ kind: "letter", name: "" }); setBody(""); }}>New template</Button>}
      />
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      <DataTable
        id="templates" rows={templates} columns={columns} rowKey={(t) => t.id} pageSizes={[50]}
        searchLabel="Search templates" searchText={(t) => `${t.name} ${t.kind}`} defaultSort={{ id: "name", dir: "asc" }}
        emptyTitle="No templates"
        rowActions={(t) => <Button kind="ghost" size="sm" onClick={() => open(t)}>{canEdit ? "Edit" : "View"}</Button>}
      />
    </>
  );
}
