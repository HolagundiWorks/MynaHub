import { useCallback, useEffect, useState } from "react";
import { Button, Checkbox, Form, InlineNotification, Stack, TextArea, TextInput } from "@carbon/react";
import { api, errorText, type Client } from "../../api";
import PageHeader from "../../components/PageHeader";
import StatusBadge from "../../components/StatusBadge";
import DataTable, { type Column } from "../../components/data-table/DataTable";
import RowMenu from "../../components/data-table/RowMenu";
import { Add, Edit } from "@carbon/icons-react";

const BLANK: Client = {
  id: null, name: "", contact_person: "", email: "", phone: "", address: "", pan: "", gstin: "", archived: false,
};

export default function ClientsPage() {
  const [clients, setClients] = useState<Client[]>([]);
  const [showArchived, setShowArchived] = useState(false);
  const [editing, setEditing] = useState<Client | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [error, setError] = useState("");
  const [saving, setSaving] = useState(false);

  const load = useCallback(() => {
    setLoading(true);
    api.clientList(showArchived).then((l) => { setClients(l); setLoadError(""); }).catch((e) => setLoadError(errorText(e))).finally(() => setLoading(false));
  }, [showArchived]);
  useEffect(load, [load]);

  const columns: Column<Client>[] = [
    { id: "name", header: "Client", locked: true, minWidth: 220, sortValue: (c) => c.name.toLowerCase(), render: (c) => <strong>{c.name}</strong>, csv: (c) => c.name },
    { id: "contact", header: "Contact", sortValue: (c) => c.contact_person.toLowerCase(), render: (c) => c.contact_person, csv: (c) => c.contact_person },
    { id: "phone", header: "Phone", render: (c) => c.phone, csv: (c) => c.phone },
    { id: "email", header: "Email", defaultVisible: false, render: (c) => c.email, csv: (c) => c.email },
    { id: "pan", header: "PAN", defaultVisible: false, render: (c) => c.pan, csv: (c) => c.pan },
    { id: "gstin", header: "GSTIN", defaultVisible: false, render: (c) => c.gstin, csv: (c) => c.gstin },
    { id: "status", header: "Status", sortValue: (c) => (c.archived ? 1 : 0), render: (c) => <StatusBadge status={c.archived ? "archived" : "active"} />, csv: (c) => (c.archived ? "Archived" : "Active") },
  ];

  if (editing) {
    const set = (k: keyof Client, v: string) => setEditing({ ...editing, [k]: v });
    const text = (k: keyof Client, label: string) => (
      <TextInput size="sm" id={`c-${k}`} labelText={label} value={String(editing[k] ?? "")} onChange={(e) => set(k, e.target.value)} />
    );
    return (
      <>
        <PageHeader title={editing.id ? "Edit client" : "New client"} description="Clients are archived, never deleted, so issued documents keep their reference." />
        <Form className="ao-form"
          onSubmit={async (e) => {
            e.preventDefault();
            if (saving) return;
            setSaving(true);
            try { await api.clientSave(editing); setEditing(null); setError(""); load(); } catch (err) { setError(errorText(err)); }
            setSaving(false);
          }}
        >
          <Stack gap={5}>
            {text("name", "Client name")}
            {text("contact_person", "Contact person")}
            {text("email", "Email")}
            {text("phone", "Phone")}
            <TextArea id="c-address" labelText="Address" rows={3} value={editing.address} onChange={(e) => set("address", e.target.value)} />
            {text("pan", "PAN")}
            {text("gstin", "GSTIN")}
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
      <PageHeader title="Clients" description="Everyone you bill. Projects, invoices and documents hang off a client." actions={<Button renderIcon={Add} size="md" onClick={() => setEditing(BLANK)}>New client</Button>} />
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      <DataTable
        id="clients" rows={clients} columns={columns} rowKey={(c) => c.id!} loading={loading} error={loadError} onRetry={load}
        searchLabel="Search clients by name, contact, phone or GSTIN" exportName="clients"
        searchText={(c) => `${c.name} ${c.contact_person} ${c.phone} ${c.email} ${c.pan} ${c.gstin}`}
        defaultSort={{ id: "name", dir: "asc" }}
        filters={<Checkbox id="clients-archived" labelText="Include archived" checked={showArchived} onChange={(_, { checked }) => setShowArchived(checked)} />}
        activeFilters={showArchived ? [{ label: "Including archived", clear: () => setShowArchived(false) }] : []}
        emptyTitle="No clients yet" emptyHint="Add your first client to start projects and invoices."
        rowActions={(c) => (
          <>
            <Button renderIcon={Edit} kind="ghost" size="sm" onClick={() => setEditing(c)}>Edit</Button>{" "}
            <RowMenu items={[{ label: c.archived ? "Restore" : "Archive", onClick: async () => { try { await api.clientSetArchived(c.id!, !c.archived); load(); } catch (e) { setError(errorText(e)); } } }]} />
          </>
        )}
      />
    </>
  );
}
