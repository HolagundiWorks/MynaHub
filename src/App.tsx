import { useCallback, useEffect, useState } from "react";
import { Button, Form, GlobalTheme, InlineNotification, PasswordInput, Stack, TextInput, Tile } from "@carbon/react";
import { api, errorText, type AuthStatus } from "./api";
import Shell from "./app/Shell";

function Setup({ onDone }: { onDone: () => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState("");
  const [recovery, setRecovery] = useState<string | null>(null);

  if (recovery) {
    return (
      <Tile className="panel">
        <h3>Save your recovery code</h3>
        <p>
          This code is shown once. Store it somewhere safe; it is the only way to recover the owner
          account if the password is forgotten.
        </p>
        <code style={{ fontSize: "1.25rem" }}>{recovery}</code>
        <Button onClick={onDone}>I have stored it</Button>
      </Tile>
    );
  }

  return (
    <Tile className="panel">
      <Form
        onSubmit={async (e) => {
          e.preventDefault();
          try {
            setRecovery(await api.authSetup(username, password, confirm));
            setError("");
          } catch (err) {
            setError(errorText(err));
          }
        }}
      >
        <Stack gap={5}>
          <h3>Set up owner account</h3>
          <TextInput id="u" labelText="Username" value={username} onChange={(e) => setUsername(e.target.value)} />
          <PasswordInput id="p" labelText="Password (12+ characters)" value={password} onChange={(e) => setPassword(e.target.value)} />
          <PasswordInput id="c" labelText="Confirm password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
          <Button type="submit">Create account</Button>
        </Stack>
      </Form>
    </Tile>
  );
}

function Recover({ onBack }: { onBack: () => void }) {
  const [user, setUser] = useState("");
  const [code, setCode] = useState("");
  const [pw, setPw] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState("");
  const [fresh, setFresh] = useState<string | null>(null);
  if (fresh) {
    return (
      <Tile className="panel">
        <h3>Password reset</h3>
        <p>Your old recovery code no longer works. Store this new one safely; it is shown once.</p>
        <code style={{ fontSize: "1.25rem" }}>{fresh}</code>
        <Button onClick={onBack}>Back to sign in</Button>
      </Tile>
    );
  }
  return (
    <Tile className="panel">
      <Form onSubmit={async (e) => {
        e.preventDefault();
        try { setFresh(await api.recover(user, code, pw, confirm)); setError(""); } catch (err) { setError(errorText(err)); }
      }}>
        <Stack gap={5}>
          <h3>Reset password</h3>
          <TextInput id="ru" labelText="Username" value={user} onChange={(e) => setUser(e.target.value)} />
          <TextInput id="rc" labelText="Recovery code" value={code} onChange={(e) => setCode(e.target.value)} />
          <PasswordInput id="rp" labelText="New password (12+ characters)" value={pw} onChange={(e) => setPw(e.target.value)} />
          <PasswordInput id="rpc" labelText="Confirm new password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
          <Button type="submit">Reset password</Button>
          <Button kind="ghost" onClick={onBack}>Cancel</Button>
        </Stack>
      </Form>
    </Tile>
  );
}

function Login({ locked, username, onDone }: { locked: boolean; username?: string; onDone: () => void }) {
  const [recovering, setRecovering] = useState(false);
  const [user, setUser] = useState(username ?? "");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");

  if (recovering) return <Recover onBack={() => setRecovering(false)} />;

  return (
    <Tile className="panel">
      <Form
        onSubmit={async (e) => {
          e.preventDefault();
          try {
            if (locked) await api.authUnlock(password);
            else await api.authLogin(user, password);
            setPassword("");
            onDone();
          } catch (err) {
            setError(errorText(err));
          }
        }}
      >
        <Stack gap={5}>
          <h3>{locked ? "Session locked" : "Sign in"}</h3>
          {!locked && <TextInput id="lu" labelText="Username" value={user} onChange={(e) => setUser(e.target.value)} />}
          <PasswordInput id="lp" labelText="Password" value={password} onChange={(e) => setPassword(e.target.value)} />
          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
          <Button type="submit">{locked ? "Unlock" : "Sign in"}</Button>
          {!locked && <Button kind="ghost" onClick={() => setRecovering(true)}>Forgot password? Use recovery code</Button>}
        </Stack>
      </Form>
    </Tile>
  );
}

export default function App() {
  const [status, setStatus] = useState<AuthStatus | null>(null);
  const [fatal, setFatal] = useState("");
  const refresh = useCallback(() => {
    api.authStatus().then(setStatus).catch((e) => setFatal(errorText(e)));
  }, []);
  useEffect(refresh, [refresh]);

  if (fatal) return <GlobalTheme theme="white"><div className="centered"><InlineNotification kind="error" title="Could not start" subtitle={fatal} hideCloseButton /></div></GlobalTheme>;
  if (!status) return null;

  let view;
  if (status.setup_required) view = <Setup onDone={refresh} />;
  else if (!status.user) view = <Login locked={false} onDone={refresh} />;
  else if (status.locked) view = <Login locked username={status.user.username} onDone={refresh} />;
  else return <Shell status={status} refresh={refresh} />;

  return <GlobalTheme theme="white"><div className="centered">{view}</div></GlobalTheme>;
}
