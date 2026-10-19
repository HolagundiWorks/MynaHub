import { useCallback, useEffect, useState } from "react";
import { open, save } from "@tauri-apps/plugin-dialog";
import {
  ActionableNotification, Button, ContainedList, ContainedListItem, Form, InlineNotification, Modal, PasswordInput, Stack, TextInput, Tile,
} from "@carbon/react";
import { api, errorText, type FileCheck, type RecentCompany } from "../../api";
import { PRODUCT, ProductCredit } from "../../brand";
import { Add, Folder, FolderOpen } from "@carbon/icons-react";

// New files use .osas; files saved by earlier builds (.adms, .aorms) are the same format and still open.
const FILTER = [{ name: "Company file", extensions: ["osas", "adms", "aorms"] }];

/** "Studio One" becomes a safe default file name; the user can change it in the save dialog. */
const fileNameFor = (name: string) => `${name.trim().replace(/[\\/:*?"<>|]+/g, " ").replace(/\s+/g, " ").trim() || "Company"}.osas`;

function lastOpened(secs: number): string {
  return secs ? new Date(secs * 1000).toLocaleDateString("en-IN", { day: "2-digit", month: "short", year: "numeric" }) : "";
}

function CreateCompany({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const [name, setName] = useState("");
  const [path, setPath] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [recovery, setRecovery] = useState<string | null>(null);

  const choose = async () => {
    try {
      const picked = await save({ title: "Save the new company file", defaultPath: fileNameFor(name), filters: FILTER });
      if (picked) setPath(picked);
    } catch (e) {
      setError(errorText(e));
    }
  };

  const create = async () => {
    setBusy(true);
    try {
      const [, code] = await api.companyCreate(path, name, username, password, confirm);
      setPassword(""); setConfirm("");
      setRecovery(code);
      setError("");
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  if (recovery) {
    return (
      <Modal open size="sm" modalHeading="Save your recovery code" primaryButtonText="I have stored it" onRequestSubmit={onDone} onRequestClose={onDone}>
        <Stack gap={4}>
          <p>This code is shown once. It is the only way to recover the owner account if the password is forgotten.</p>
          <code>{recovery}</code>
        </Stack>
      </Modal>
    );
  }

  return (
    <Modal
      open size="sm" modalHeading="Create a new company" primaryButtonText={busy ? "Creating…" : "Create company"} secondaryButtonText="Cancel"
      primaryButtonDisabled={busy || !name.trim() || !path || !username.trim() || !password}
      onRequestSubmit={create} onRequestClose={onClose}
    >
      <Form onSubmit={(e) => { e.preventDefault(); }}>
        <Stack gap={4}>
          <TextInput size="sm" id="cc-name" labelText="Company name" value={name} onChange={(e) => setName(e.target.value)} data-modal-primary-focus />
          <TextInput size="sm" id="cc-path" labelText="Company file" readOnly value={path} placeholder="No location chosen" helperText="One file holds everything for this company, including issued documents. Existing files are never replaced." />
          <div><Button renderIcon={Folder} size="sm" kind="tertiary" onClick={choose}>Choose location…</Button></div>
          <TextInput size="sm" id="cc-user" labelText="Owner username" value={username} onChange={(e) => setUsername(e.target.value)} />
          <PasswordInput size="sm" id="cc-pw" labelText="Owner password (12+ characters)" value={password} onChange={(e) => setPassword(e.target.value)} />
          <PasswordInput size="sm" id="cc-pw2" labelText="Confirm password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
        </Stack>
      </Form>
    </Modal>
  );
}

function ConfirmOpen({ file, busy, onOpen, onClose }: { file: FileCheck; busy: boolean; onOpen: () => void; onClose: () => void }) {
  return (
    <Modal open size="sm" modalHeading="Open this company?" primaryButtonText={busy ? "Opening…" : "Open"} secondaryButtonText="Cancel" primaryButtonDisabled={busy} onRequestSubmit={onOpen} onRequestClose={onClose}>
      <Stack gap={3}>
        <p><strong>{file.name}</strong></p>
        <p className="ao-muted">{file.path}</p>
        {file.needs_upgrade && <InlineNotification kind="info" hideCloseButton lowContrast title="This file was saved by an older version." subtitle="A safety copy is made next to it before it is upgraded." />}
      </Stack>
    </Modal>
  );
}

/** Shown whenever no company is open. Nothing about any company is readable from here. */
export default function StartScreen({ onOpened }: { onOpened: () => void }) {
  const [recent, setRecent] = useState<RecentCompany[]>([]);
  const [legacy, setLegacy] = useState(false);
  const [creating, setCreating] = useState(false);
  const [pending, setPending] = useState<FileCheck | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const load = useCallback(() => {
    api.companyRecent().then(setRecent).catch(() => setRecent([]));
    api.companyLegacy().then((l) => setLegacy(!!l)).catch(() => setLegacy(false));
  }, []);
  useEffect(load, [load]);

  const check = async (path: string) => {
    try {
      setPending(await api.companyValidate(path));
      setError("");
    } catch (e) {
      setError(errorText(e));
    }
  };

  const pick = async () => {
    try {
      const picked = await open({ title: "Open a company file", multiple: false, directory: false, filters: [...FILTER, { name: "All files", extensions: ["*"] }] });
      if (typeof picked === "string") await check(picked);
    } catch (e) {
      setError(errorText(e));
    }
  };

  const openPending = async () => {
    if (!pending) return;
    setBusy(true);
    try {
      await api.companyOpen(pending.path);
      onOpened();
    } catch (e) {
      setError(errorText(e));
      setPending(null);
    } finally {
      setBusy(false);
    }
  };

  const convert = async () => {
    try {
      const dest = await save({ title: "Save your existing data as a company file", defaultPath: "My company.osas", filters: FILTER });
      if (!dest) return;
      await api.companyConvertLegacy(dest);
      onOpened();
    } catch (e) {
      setError(errorText(e));
    }
  };

  return (
    <div className="centered">
      <Tile className="panel ao-start">
        <Stack gap={5}>
          <div>
            <h1>{PRODUCT}</h1>
            <p className="ao-muted">Create a company file or open an existing one.</p>
            <ProductCredit />
          </div>
          <div className="ao-actions">
            <Button renderIcon={Add} size="md" onClick={() => setCreating(true)}>Create a new company</Button>
            <Button renderIcon={FolderOpen} size="md" kind="tertiary" onClick={pick}>Open an existing company</Button>
          </div>
          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
          {legacy && (
            <ActionableNotification
              kind="info" lowContrast hideCloseButton inline title="Data from the earlier version was found on this computer."
              subtitle="Convert it into a company file. The original is left untouched." actionButtonLabel="Convert" onActionButtonClick={convert}
            />
          )}
          {recent.length > 0 && (
            <ContainedList label="Recent companies" kind="on-page" size="md">
              {recent.map((r) => (
                <ContainedListItem
                  key={r.uuid}
                  action={
                    <div className="ao-actions">
                      <Button renderIcon={FolderOpen} size="sm" kind="ghost" disabled={!r.exists} onClick={() => check(r.path)}>Open</Button>
                      <Button size="sm" kind="ghost" onClick={async () => { await api.companyRecentRemove(r.uuid); load(); }}>Remove from list</Button>
                    </div>
                  }
                >
                  <div>
                    <div>{r.name}</div>
                    <div className="ao-muted">{r.exists ? r.path : `${r.path} (file not found)`} · {lastOpened(r.last_opened)}</div>
                  </div>
                </ContainedListItem>
              ))}
            </ContainedList>
          )}
        </Stack>
      </Tile>
      {creating && <CreateCompany onClose={() => setCreating(false)} onDone={() => { setCreating(false); onOpened(); }} />}
      {pending && <ConfirmOpen file={pending} busy={busy} onOpen={openPending} onClose={() => setPending(null)} />}
    </div>
  );
}
