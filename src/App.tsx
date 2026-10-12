import { useCallback, useEffect, useState } from "react";
import { Button, Form, GlobalTheme, InlineNotification, PasswordInput, Stack, TextInput, Tile } from "@carbon/react";
import { api, errorText, type AuthStatus } from "./api";
import Shell from "./app/Shell";
import StartScreen from "./features/company-file/StartScreen";
import { ArrowLeft, Password, Switcher, UserFollow } from "@carbon/icons-react";

function Setup({ company, onDone, onSwitch }: { company: string; onDone: () => void; onSwitch: () => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState("");
  const [recovery, setRecovery] = useState<string | null>(null);

  if (recovery) {
    return (
      <Tile className="panel">
        <h2>Save your recovery code</h2>
        <p>
          This code is shown once. Store it somewhere safe; it is the only way to recover the owner
          account if the password is forgotten.
        </p>
        <code>{recovery}</code>
        <Button size="sm" onClick={onDone}>I have stored it</Button>
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
        <Stack gap={4}>
          <CompanyBar name={company} onSwitch={onSwitch} />
          <h2>Set up owner account</h2>
          <TextInput size="sm" id="u" labelText="Username" value={username} onChange={(e) => setUsername(e.target.value)} />
          <PasswordInput size="sm" id="p" labelText="Password (12+ characters)" value={password} onChange={(e) => setPassword(e.target.value)} />
          <PasswordInput size="sm" id="c" labelText="Confirm password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
          <Button renderIcon={UserFollow} size="sm" type="submit">Create account</Button>
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
        <h2>Password reset</h2>
        <p>Your old recovery code no longer works. Store this new one safely; it is shown once.</p>
        <code>{fresh}</code>
        <Button renderIcon={ArrowLeft} size="sm" onClick={onBack}>Back to sign in</Button>
      </Tile>
    );
  }
  return (
    <Tile className="panel">
      <Form onSubmit={async (e) => {
        e.preventDefault();
        try { setFresh(await api.recover(user, code, pw, confirm)); setError(""); } catch (err) { setError(errorText(err)); }
      }}>
        <Stack gap={4}>
          <h2>Reset password</h2>
          <TextInput size="sm" id="ru" labelText="Username" value={user} onChange={(e) => setUser(e.target.value)} />
          <TextInput size="sm" id="rc" labelText="Recovery code" value={code} onChange={(e) => setCode(e.target.value)} />
          <PasswordInput size="sm" id="rp" labelText="New password (12+ characters)" value={pw} onChange={(e) => setPw(e.target.value)} />
          <PasswordInput size="sm" id="rpc" labelText="Confirm new password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
          <Button renderIcon={Password} size="sm" type="submit">Reset password</Button>
          <Button size="sm" kind="ghost" onClick={onBack}>Cancel</Button>
        </Stack>
      </Form>
    </Tile>
  );
}

/** Which company the sign-in is for, with a way back to the company chooser. */
function CompanyBar({ name, onSwitch }: { name: string; onSwitch: () => void }) {
  return (
    <div className="toolbar">
      <span className="ao-muted">{name}</span>
      <Button renderIcon={Switcher} size="sm" kind="ghost" onClick={onSwitch}>Switch company</Button>
    </div>
  );
}

function Login({ locked, username, company, onDone, onSwitch }: { locked: boolean; username?: string; company: string; onDone: () => void; onSwitch: () => void }) {
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
        <Stack gap={4}>
          <CompanyBar name={company} onSwitch={onSwitch} />
          <h2>{locked ? "Session locked" : "Sign in"}</h2>
          {!locked && <TextInput size="sm" id="lu" labelText="Username" value={user} onChange={(e) => setUser(e.target.value)} />}
          <PasswordInput size="sm" id="lp" labelText="Password" value={password} onChange={(e) => setPassword(e.target.value)} />
          {error && <InlineNotification kind="error" title={error} hideCloseButton lowContrast />}
          <Button size="sm" type="submit">{locked ? "Unlock" : "Sign in"}</Button>
          {!locked && <Button size="sm" kind="ghost" onClick={() => setRecovering(true)}>Forgot password? Use recovery code</Button>}
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
  if (!status.company) return <GlobalTheme theme="white"><StartScreen onOpened={refresh} /></GlobalTheme>;

  const switchCompany = async () => { await api.companyClose(); refresh(); };
  let view;
  if (status.setup_required) view = <Setup company={status.company.name} onDone={refresh} onSwitch={switchCompany} />;
  else if (!status.user) view = <Login locked={false} company={status.company.name} onDone={refresh} onSwitch={switchCompany} />;
  else if (status.locked) view = <Login locked username={status.user.username} company={status.company.name} onDone={refresh} onSwitch={switchCompany} />;
  else return <Shell status={status} refresh={refresh} />;

  return <GlobalTheme theme="white"><div className="centered">{view}</div></GlobalTheme>;
}
