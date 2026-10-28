import { useEffect, useRef, useState } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import { api, ApiError, session, type AuthProviders, type AuthResult, type InvitationInfo } from "../api";
import { Field } from "../components/ui";

/** Starts the Entra ID (OIDC) sign-in: the server keeps state, nonce and PKCE verifier. */
async function startMicrosoftSignIn() {
  const { authorizeUrl } = await api<{ authorizeUrl: string }>("/api/auth/entra/start", { body: {} });
  window.location.assign(authorizeUrl);
}

function MicrosoftButton({ providers, onError }: { providers: AuthProviders | null; onError: (message: string) => void }) {
  if (!providers?.entra) return null;
  return (
    <button type="button" className="microsoft" onClick={() => startMicrosoftSignIn().catch((e: Error) => onError(e.message))}>
      <span aria-hidden className="ms-logo"><i /><i /><i /><i /></span> {providers.entraName ?? "Microsoft"} ile giriş yap
    </button>
  );
}

function useProviders() {
  const [providers, setProviders] = useState<AuthProviders | null>(null);
  useEffect(() => { api<AuthProviders>("/api/auth/providers").then(setProviders).catch(() => setProviders(null)); }, []);
  return providers;
}

export function Login({ onSignedIn }: { onSignedIn: () => void }) {
  const [mode, setMode] = useState<"login" | "setup">("login");
  const [form, setForm] = useState({ email: "", password: "", displayName: "", organizationName: "" });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const providers = useProviders();
  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: e.target.value });

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const result = mode === "login"
        ? await api<AuthResult>("/api/auth/login", { body: { email: form.email, password: form.password } })
        : await api<AuthResult>("/api/auth/setup", { body: form });
      session.store(result);
      onSignedIn();
    } catch (err) {
      setError(err instanceof ApiError && err.status === 409 ? "Kurulum zaten yapılmış. Giriş yapın." : (err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="login card stack" onSubmit={submit}>
      <h1>Azure Agent</h1>
      <p className="muted">Azure Boards'taki işleri developer'larınızın agent'ları yapar.</p>
      <MicrosoftButton providers={providers} onError={setError} />
      {providers?.entra ? <div className="divider"><span>veya e-posta ile</span></div> : null}
      <div className="row">
        <button type="button" className={mode === "login" ? "primary" : ""} onClick={() => setMode("login")}>Giriş</button>
        <button type="button" className={mode === "setup" ? "primary" : ""} onClick={() => setMode("setup")}>İlk kurulum</button>
      </div>
      {error ? <div className="error">{error}</div> : null}
      {mode === "setup" ? <>
        <Field label="Organizasyon adı"><input required value={form.organizationName} onChange={set("organizationName")} /></Field>
        <Field label="Adınız"><input required value={form.displayName} onChange={set("displayName")} /></Field>
      </> : null}
      <Field label="E-posta"><input type="email" required autoComplete="username" value={form.email} onChange={set("email")} /></Field>
      <Field label="Şifre" hint={mode === "setup" ? "En az 10 karakter; büyük harf, küçük harf ve rakam" : undefined}>
        <input type="password" required autoComplete={mode === "login" ? "current-password" : "new-password"} value={form.password} onChange={set("password")} />
      </Field>
      <button className="primary" disabled={busy}>{mode === "login" ? "Giriş yap" : "Kur ve giriş yap"}</button>
    </form>
  );
}

/** Return page of the Entra ID sign-in (registered as the redirect URI). */
export function AuthCallback({ onSignedIn }: { onSignedIn: () => void }) {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const [error, setError] = useState<string | null>(params.get("error_description") ?? params.get("error"));
  const started = useRef(false);
  useEffect(() => {
    const code = params.get("code");
    const state = params.get("state");
    if (started.current || !code || !state) return;
    started.current = true; // the code is single-use; StrictMode must not redeem it twice
    api<AuthResult>("/api/auth/entra/callback", { body: { code, state } })
      .then((result) => { session.store(result); onSignedIn(); navigate("/", { replace: true }); })
      .catch((e: Error) => setError(e.message));
  }, [params, navigate, onSignedIn]);
  return (
    <div className="login card stack">
      <h1>Azure Agent</h1>
      {error ? <>
        <div className="error">{error}</div>
        <button className="primary" onClick={() => navigate("/", { replace: true })}>Giriş sayfasına dön</button>
      </> : <p className="muted">Microsoft hesabınızla giriş yapılıyor…</p>}
    </div>
  );
}

/** Invitation link: new people choose a password (or use Microsoft); existing accounts confirm theirs. */
export function AcceptInvitation({ onSignedIn }: { onSignedIn: () => void }) {
  const { token = "" } = useParams();
  const navigate = useNavigate();
  const [info, setInfo] = useState<InvitationInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [form, setForm] = useState({ displayName: "", password: "" });
  const [busy, setBusy] = useState(false);
  const providers = useProviders();
  useEffect(() => {
    api<InvitationInfo>(`/api/auth/invitations/${encodeURIComponent(token)}`)
      .then((i) => { setInfo(i); setForm((f) => ({ ...f, displayName: i.displayName })); })
      .catch((e: ApiError) => setError(e.status === 404 ? "Davet bulunamadı, süresi dolmuş ya da kullanılmış." : e.message));
  }, [token]);

  const accept = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      session.store(await api<AuthResult>("/api/auth/invitations/accept", { body: { token, displayName: form.displayName, password: form.password } }));
      onSignedIn();
      navigate("/", { replace: true });
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="login card stack" onSubmit={accept}>
      <h1>Davet</h1>
      {error ? <div className="error">{error}</div> : null}
      {info ? <>
        <p><strong>{info.organizationName}</strong> organizasyonuna davet edildiniz ({info.email}).</p>
        {info.ssoEnabled ? <>
          <MicrosoftButton providers={providers} onError={setError} />
          <div className="divider"><span>veya şifre ile</span></div>
        </> : null}
        {info.hasAccount ? (
          <Field label="Mevcut şifreniz" hint="Bu e-postayla bir hesabınız var; daveti kabul etmek için şifrenizi girin.">
            <input type="password" required autoComplete="current-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
          </Field>
        ) : <>
          <Field label="Adınız"><input required value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} /></Field>
          <Field label="Şifre belirleyin" hint="En az 10 karakter; büyük harf, küçük harf ve rakam">
            <input type="password" required autoComplete="new-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
          </Field>
        </>}
        <button className="primary" disabled={busy}>Daveti kabul et</button>
      </> : null}
      <a href="/" className="small">Giriş sayfası</a>
    </form>
  );
}
