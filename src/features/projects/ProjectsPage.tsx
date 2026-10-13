import { useCallback, useEffect, useState } from "react";
import { Button, Checkbox, Form, InlineNotification, Select, SelectItem, Stack, TextArea, TextInput } from "@carbon/react";
import { api, errorText, type Client, type Project } from "../../api";
import PageHeader from "../../components/PageHeader";
import FilterSelect from "../../components/FilterSelect";
import StatusBadge from "../../components/StatusBadge";
import DataTable, { type Column } from "../../components/data-table/DataTable";
import RowMenu from "../../components/data-table/RowMenu";
import { Edit } from "@carbon/icons-react";
import { DraftGuard } from "../../app/unsaved";

const STATUS_LABEL = { active: "Active", on_hold: "On hold", completed: "Completed" } as const;

export default function ProjectsPage() {
  const [projects, setProjects] = useState<Project[]>([]);
  const [clients, setClients] = useState<Client[]>([]);
  const [showArchived, setShowArchived] = useState(false);
  const [status, setStatus] = useState("");
  const [editing, setEditing] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [error, setError] = useState("");
  const [saving, setSaving] = useState(false);

  const load = useCallback(() => {
    setLoading(true);
    Promise.all([api.projectList(null, showArchived), api.clientList(false)])
      .then(([p, c]) => { setProjects(p); setClients(c); setLoadError(""); })
      .catch((e) => setLoadError(errorText(e)))
      .finally(() => setLoading(false));
  }, [showArchived]);
  useEffect(load, [load]);

  const shown = status ? projects.filter((p) => p.status === status) : projects;

  const columns: Column<Project>[] = [
    { id: "code", header: "Code", width: 110, sortValue: (p) => p.code.toLowerCase(), render: (p) => p.code, csv: (p) => p.code },
    { id: "name", header: "Project", locked: true, minWidth: 200, sortValue: (p) => p.name.toLowerCase(), render: (p) => <strong>{p.name}</strong>, csv: (p) => p.name },
    { id: "client", header: "Client", sortValue: (p) => p.client_name.toLowerCase(), render: (p) => p.client_name, csv: (p) => p.client_name },
    { id: "site", header: "Site", defaultVisible: false, render: (p) => p.site_address, csv: (p) => p.site_address },
    { id: "status", header: "Status", sortValue: (p) => p.status, render: (p) => <StatusBadge status={p.archived ? "archived" : p.status} />, csv: (p) => (p.archived ? "Archived" : STATUS_LABEL[p.status]) },
  ];

  if (editing) {
    return (
      <>
        <PageHeader title={editing.id ? "Edit project" : "New project"} description="A client can have many projects; each project holds its own agreements, letters and invoices." />
        <Form className="ao-form"
          onSubmit={async (e) => {
            e.preventDefault();
            if (saving) return;
            setSaving(true);
            try { await api.projectSave(editing); setEditing(null); setError(""); load(); } catch (err) { setError(errorText(err)); }
            setSaving(false);
          }}
        >
          <DraftGuard value={editing} />
          <Stack gap={5}>
            <Select size="sm" id="p-client" labelText="Client" value={editing.client_id || ""} disabled={editing.id !== null} onChange={(e) => setEditing({ ...editing, client_id: Number(e.target.value) })}>
              <SelectItem value="" text="Select a client" />
              {clients.map((c) => <SelectItem key={c.id} value={c.id!} text={c.name} />)}
            </Select>
            <TextInput size="sm" id="p-name" labelText="Project name" value={editing.name} onChange={(e) => setEditing({ ...editing, name: e.target.value })} />
            <TextInput size="sm" id="p-code" labelText="Project code" value={editing.code} onChange={(e) => setEditing({ ...editing, code: e.target.value })} />
            <Select size="sm" id="p-status" labelText="Status" value={editing.status} onChange={(e) => setEditing({ ...editing, status: e.target.value as Project["status"] })}>
              {Object.entries(STATUS_LABEL).map(([v, t]) => <SelectItem key={v} value={v} text={t} />)}
            </Select>
            <TextArea id="p-site" labelText="Site address" rows={3} value={editing.site_address} onChange={(e) => setEditing({ ...editing, site_address: e.target.value })} />
            {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
            <div className="toolbar">
              <Button size="sm" kind="secondary" onClick={() => { setEditing(null); setError(""); }}>Cancel</Button>
              <Button size="sm" type="submit" disabled={saving}>{saving ? "Saving…" : "Save"}</Button>
            </div>
          </Stack>
        </Form>
      </>
    );
  }

  return (
    <>
      <PageHeader
        title="Projects" description="Every engagement, with its client and status."
        actions={<Button size="sm" disabled={clients.length === 0} onClick={() => setEditing({ id: null, client_id: 0, code: "", name: "", site_address: "", status: "active", archived: false, client_name: "" })}>New project</Button>}
      />
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      <DataTable
        id="projects" rows={shown} columns={columns} rowKey={(p) => p.id!} loading={loading} error={loadError} onRetry={load}
        searchLabel="Search projects by code, name, client or site" exportName="projects"
        searchText={(p) => `${p.code} ${p.name} ${p.client_name} ${p.site_address}`}
        defaultSort={{ id: "client", dir: "asc" }}
        filters={
          <>
            <FilterSelect label="Filter by status" value={status} onChange={setStatus} options={[["", "All statuses"], ...Object.entries(STATUS_LABEL)]} />
            <Checkbox id="projects-archived" labelText="Include archived" checked={showArchived} onChange={(_, { checked }) => setShowArchived(checked)} />
          </>
        }
        activeFilters={[
          ...(status ? [{ label: `Status: ${STATUS_LABEL[status as keyof typeof STATUS_LABEL]}`, clear: () => setStatus("") }] : []),
          ...(showArchived ? [{ label: "Including archived", clear: () => setShowArchived(false) }] : []),
        ]}
        emptyTitle={clients.length === 0 ? "Create a client first" : "No projects yet"} emptyHint={clients.length === 0 ? "A project belongs to a client." : "Add the first project for a client."}
        rowActions={(p) => (
          <>
            <Button renderIcon={Edit} kind="ghost" size="sm" onClick={() => setEditing(p)}>Edit</Button>{" "}
            <RowMenu items={[{ label: p.archived ? "Restore" : "Archive", onClick: async () => { try { await api.projectSetArchived(p.id!, !p.archived); load(); } catch (e) { setError(errorText(e)); } } }]} />
          </>
        )}
      />
    </>
  );
}
