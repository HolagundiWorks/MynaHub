import { useEffect, useState } from "react";
import { Button, ContentSwitcher, Form, InlineNotification, PasswordInput, Select, SelectItem, Stack, Switch, TextInput } from "@carbon/react";
import { api, errorText, type AuditPageRow, type BackupInfo, type UserRow } from "../../api";
import { useServerTable } from "../../components/data-table/useServerTable";
import PageHeader from "../../components/PageHeader";
import { confirmDialog } from "../../components/dialogs";
import StatusBadge from "../../components/StatusBadge";
import DataTable, { type Column } from "../../components/data-table/DataTable";
import { usePersisted } from "../../design/density";

const ROLES = [
  ["owner", "Owner / Admin"], ["principal", "Principal / Architect"], ["accounts", "Accounts"],
  ["document_editor", "Document editor"], ["auditor", "Read-only / Auditor"],
] as const;

function Notice({ error, ok }: { error: string; ok?: string }) {
  return (
    <>
      {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
      {ok && <InlineNotification kind="success" title={ok} hideCloseButton lowContrast />}
    </>
  );
}

function PasswordCard() {
  const [cur, setCur] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");
  return (
    <Form className="ao-form" onSubmit={async (e) => {
      e.preventDefault();
      try { await api.changePassword(cur, next, confirm); setOk("Password changed."); setError(""); setCur(""); setNext(""); setConfirm(""); }
      catch (err) { setError(errorText(err)); setOk(""); }
    }}>
      <Stack gap={4}>
        <h4>Change password</h4>
        <PasswordInput id="pw-cur" labelText="Current password" value={cur} onChange={(e) => setCur(e.target.value)} />
        <PasswordInput id="pw-new" labelText="New password (12+ characters)" value={next} onChange={(e) => setNext(e.target.value)} />
        <PasswordInput id="pw-conf" labelText="Confirm new password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
        <Notice error={error} ok={ok} />
        <div><Button type="submit" size="sm">Change password</Button></div>
      </Stack>
    </Form>
  );
}

function IdleCard() {
  const [minutes, setMinutes] = useState(5);
  const [pw, setPw] = useState("");
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");
  useEffect(() => { api.securityGet().then(setMinutes).catch((e) => setError(errorText(e))); }, []);
  return (
    <Form className="ao-form" onSubmit={async (e) => {
      e.preventDefault();
      try { await api.securitySetIdle(minutes, pw); setOk("Saved."); setError(""); setPw(""); }
      catch (err) { setError(errorText(err)); setOk(""); }
    }}>
      <Stack gap={4}>
        <h4>Auto-lock</h4>
        <Select id="idle" labelText="Lock after inactivity" value={minutes} onChange={(e) => setMinutes(Number(e.target.value))}>
          {[1, 5, 10, 15, 30].map((m) => <SelectItem key={m} value={m} text={`${m} minute${m > 1 ? "s" : ""}`} />)}
        </Select>
        <PasswordInput id="idle-pw" labelText="Confirm with your password" value={pw} onChange={(e) => setPw(e.target.value)} />
        <Notice error={error} ok={ok} />
        <div><Button type="submit" size="sm">Save</Button></div>
      </Stack>
    </Form>
  );
}

function UsersCard() {
  const [users, setUsers] = useState<UserRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [view, setView] = useState<"list" | "new">("list");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("accounts");
  const [adminPw, setAdminPw] = useState("");
  const [error, setError] = useState("");
  const [ok, setOk] = useState("");
  const [busy, setBusy] = useState(false);
  const load = () => { setLoading(true); api.userList().then((u) => { setUsers(u); setLoadError(""); }).catch((e) => setLoadError(errorText(e))).finally(() => setLoading(false)); };
  useEffect(load, []);

  const change = async (u: UserRow, nextRole: string, disabled: boolean) => {
    try { await api.userUpdate(u.id, nextRole, disabled, adminPw); setOk("Updated."); setError(""); load(); }
    catch (e) { setError(errorText(e)); setOk(""); }
  };

  const columns: Column<UserRow>[] = [
    { id: "user", header: "User", locked: true, minWidth: 200, sortValue: (u) => u.username.toLowerCase(), render: (u) => u.username, csv: (u) => u.username },
    {
      id: "role", header: "Role", sortValue: (u) => u.role,
      render: (u) => (
        <Select id={`role-${u.id}`} labelText={`Role of ${u.username}`} hideLabel size="sm" value={u.role} onChange={(e) => change(u, e.target.value, u.disabled)}>
          {ROLES.map(([v, t]) => <SelectItem key={v} value={v} text={t} />)}
        </Select>
      ),
      csv: (u) => u.role,
    },
    { id: "status", header: "Status", sortValue: (u) => (u.disabled ? 1 : 0), render: (u) => <StatusBadge status={u.disabled ? "disabled" : "active"} />, csv: (u) => (u.disabled ? "Disabled" : "Active") },
  ];

  if (view === "new") {
    return (
      <>
        <PageHeader title="Add a user" description="They sign in with this username and the initial password, and should change it." />
        <Form className="ao-form" onSubmit={async (e) => {
          e.preventDefault();
          if (busy) return;
          setBusy(true);
          try { await api.userCreate(username, password, role, adminPw); setOk(`User ${username} created.`); setError(""); setUsername(""); setPassword(""); setView("list"); load(); }
          catch (err) { setError(errorText(err)); setOk(""); }
          setBusy(false);
        }}>
          <Stack gap={4}>
            <TextInput id="nu-name" labelText="Username" value={username} onChange={(e) => setUsername(e.target.value)} />
            <PasswordInput id="nu-pw" labelText="Initial password (12+ characters)" value={password} onChange={(e) => setPassword(e.target.value)} />
            <Select id="nu-role" labelText="Role" value={role} onChange={(e) => setRole(e.target.value)}>
              {ROLES.map(([v, t]) => <SelectItem key={v} value={v} text={t} />)}
            </Select>
            <PasswordInput id="nu-admin" labelText="Your password" value={adminPw} onChange={(e) => setAdminPw(e.target.value)} />
            <Notice error={error} />
            <div className="toolbar"><Button kind="secondary" onClick={() => { setView("list"); setError(""); }}>Cancel</Button><Button type="submit" disabled={busy}>{busy ? "Saving…" : "Add user"}</Button></div>
          </Stack>
        </Form>
      </>
    );
  }

  return (
    <>
      <PageHeader title="Users and roles" description="Changing a role or adding a user needs your password." actions={<Button onClick={() => setView("new")}>Add user</Button>} />
      <div style={{ maxWidth: 360, marginBottom: 8 }}>
        <PasswordInput id="adm-pw" labelText="Your password (for role changes)" size="sm" value={adminPw} onChange={(e) => setAdminPw(e.target.value)} />
      </div>
      <Notice error={error} ok={ok} />
      <DataTable
        id="users" rows={users} columns={columns} rowKey={(u) => u.id} loading={loading} error={loadError} onRetry={load}
        searchLabel="Search users" searchText={(u) => `${u.username} ${u.role}`} defaultSort={{ id: "user", dir: "asc" }} pageSizes={[50]}
        rowActions={(u) => <Button kind="ghost" size="sm" onClick={() => change(u, u.role, !u.disabled)}>{u.disabled ? "Enable" : "Disable"}</Button>}
        emptyTitle="No users"
      />
    </>
  );
}

function BackupCard() {
  const [pw, setPw] = useState("");
  const [info, setInfo] = useState<BackupInfo | null>(null);
  const [error, setError] = useState("");
  return (
    <Form className="ao-form" onSubmit={async (e) => {
      e.preventDefault();
      try { setInfo(await api.backupCreate(pw)); setError(""); setPw(""); } catch (err) { setError(errorText(err)); setInfo(null); }
    }}>
      <Stack gap={4}>
        <h4>Backup</h4>
        <p>Creates a verified copy of the database. Issued invoice PDFs are stored in the files folder next to it; copy that folder as well.</p>
        <PasswordInput id="bk-pw" labelText="Confirm with your password" value={pw} onChange={(e) => setPw(e.target.value)} />
        <Notice error={error} />
        {info && <InlineNotification kind="success" hideCloseButton lowContrast title={`Backup created: ${info.filename}`} subtitle={`${info.folder} · SHA-256 ${info.sha256}`} />}
        <div><Button type="submit" size="sm">Create backup</Button></div>
      </Stack>
    </Form>
  );
}

function RestoreCard({ onRestored }: { onRestored: () => void }) {
  const [files, setFiles] = useState<{ filename: string; bytes: number }[]>([]);
  const [pick, setPick] = useState("");
  const [pw, setPw] = useState("");
  const [error, setError] = useState("");
  useEffect(() => { api.backupList().then(setFiles).catch((e) => setError(errorText(e))); }, []);
  return (
    <Stack gap={4}>
      <h4>Restore a backup</h4>
      <p>
        This replaces all current data with the backup. A safety backup of the current data is made first, and everyone is
        signed out afterwards. Only the database is restored; issued PDFs and attachments are not changed.
      </p>
      <Select id="rs-file" labelText="Backup" value={pick} onChange={(e) => setPick(e.target.value)}>
        <SelectItem value="" text="Choose a backup…" />
        {files.map((f) => <SelectItem key={f.filename} value={f.filename} text={`${f.filename} (${(f.bytes / 1024).toFixed(0)} KB)`} />)}
      </Select>
      <PasswordInput id="rs-pw" labelText="Confirm with your password" value={pw} onChange={(e) => setPw(e.target.value)} />
      <Notice error={error} />
      <div>
        <Button size="sm" kind="danger" disabled={!pick} onClick={async () => {
          if (!(await confirmDialog(`Replace ALL current data with ${pick}?`))) return;
          try { await api.backupRestore(pick, pw); onRestored(); } catch (e) { setError(errorText(e)); }
        }}>Restore</Button>
      </div>
    </Stack>
  );
}

function AuditCard() {
  const table = useServerTable<AuditPageRow>({ id: "audit", fetch: api.auditPage, filters: {}, defaultSort: { id: "at", dir: "desc" } });
  const columns: Column<AuditPageRow>[] = [
    { id: "at", header: "When (UTC)", width: 170, sortValue: (r) => r.at, render: (r) => r.at, csv: (r) => r.at },
    { id: "user", header: "User", sortValue: (r) => r.user.toLowerCase(), render: (r) => r.user, csv: (r) => r.user },
    { id: "event", header: "Event", locked: true, sortValue: (r) => r.event, render: (r) => r.event.replace(/_/g, " "), csv: (r) => r.event },
    { id: "detail", header: "Detail", minWidth: 320, render: (r) => r.detail, csv: (r) => r.detail },
  ];
  return (
    <>
      <PageHeader title="Audit log" description="Security and financial actions. Entries cannot be edited or deleted." actions={<Button kind="secondary" onClick={table.reload}>Refresh</Button>} />
      <DataTable
        id="audit" rows={table.rows} columns={columns} rowKey={(r) => r.id} loading={table.loading} error={table.error} onRetry={table.reload}
        server={table.server} searchLabel="Search events, users or details" exportName="audit-log" emptyTitle="No audit entries yet"
      />
    </>
  );
}

type Section = "security" | "users" | "backup" | "audit";

export default function SettingsPage({ role, onRestored }: { role: string; onRestored: () => void }) {
  const owner = role === "Owner";
  const sections: [Section, string][] = [
    ["security", "Security"],
    ...(owner ? ([["users", "Users"], ["backup", "Backup and restore"]] as [Section, string][]) : []),
    ...(owner || role === "Auditor" ? ([["audit", "Audit log"]] as [Section, string][]) : []),
  ];
  const [section, setSection] = usePersisted<Section>("ao.settingsSection", "security");
  const current = sections.some(([s]) => s === section) ? section : "security";
  return (
    <>
      <PageHeader title="Security, users and backup" description="Sign-in security, who can do what, and protecting your data." />
      <ContentSwitcher size="md" selectedIndex={Math.max(0, sections.findIndex(([id]) => id === current))} onChange={({ index }: { index?: number }) => setSection(sections[index ?? 0][0])} className="ao-block">
        {sections.map(([id, label]) => <Switch key={id} name={id} text={label} />)}
      </ContentSwitcher>
      {current === "security" && (
        <Stack gap={8}>
          <PasswordCard />
          {owner && <IdleCard />}
        </Stack>
      )}
      {current === "users" && <UsersCard />}
      {current === "backup" && (
        <Stack gap={8}>
          <BackupCard />
          <RestoreCard onRestored={onRestored} />
        </Stack>
      )}
      {current === "audit" && <AuditCard />}
    </>
  );
}
