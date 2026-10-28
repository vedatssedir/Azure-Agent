import { useEffect, useRef, useState } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import { api, ApiError, session, type AuthProviders, type AuthResult, type InvitationInfo, type SetupStatus } from "../api";
import { Field } from "../components/ui";
import { Icon } from "../components/icons";

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

const Brand = () => <div className="auth-brand"><Icon name="logo" size={26} /> Azure Agent</div>;
const PASSWORD_HINT = "En az 10 karakter; büyük harf, küçük harf ve rakam";
const PAT_HINT = "Azure DevOps → User settings → Personal access tokens. Kapsam: Work Items (Read & write), Code (Read & write), Project and Team (Read).";

/** Signed-out entry: first-time setup when the system is empty, otherwise sign-in and (if allowed) developer sign-up. */
export function AuthPages({ onSignedIn }: { onSignedIn: () => void }) {
  const [status, setStatus] = useState<SetupStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [mode, setMode] = useState<"login" | "signup">("login");
  useEffect(() => { api<SetupStatus>("/api/auth/status").then(setStatus).catch((e: Error) => setError(e.message)); }, []);
  if (error) return <div className="auth"><div className="auth-card"><Brand /><div className="error">{error}</div></div></div>;
  if (!status) return <div className="auth"><div className="auth-card"><Brand /><p className="muted">Yükleniyor…</p></div></div>;
  if (status.setupRequired) return <Setup onSignedIn={onSignedIn} />;
  return mode === "signup" && status.selfSignUp
    ? <SignUp onSignedIn={onSignedIn} onLogin={() => setMode("login")} />
    : <Login onSignedIn={onSignedIn} onSignUp={status.selfSignUp ? () => setMode("signup") : undefined} />;
}

function useSubmit(onSignedIn: () => void, send: () => Promise<AuthResult>) {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      session.store(await send());
      onSignedIn();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };
  return { error, setError, busy, submit };
}

export function Login({ onSignedIn, onSignUp }: { onSignedIn: () => void; onSignUp?: () => void }) {
  const [form, setForm] = useState({ email: "", password: "" });
  const providers = useProviders();
  const { error, setError, busy, submit } = useSubmit(onSignedIn, () => api<AuthResult>("/api/auth/login", { body: form }));
  return (
    <div className="auth">
      <form className="auth-card" onSubmit={submit}>
        <Brand />
        <div><h2 style={{ margin: 0 }}>Giriş yap</h2><p className="muted small" style={{ margin: "4px 0 0" }}>Azure Boards işlerinizi agent'larınızla geliştirin.</p></div>
        <MicrosoftButton providers={providers} onError={setError} />
        {providers?.entra ? <div className="divider">veya e-posta ile</div> : null}
        {error ? <div className="error">{error}</div> : null}
        <Field label="E-posta"><input type="email" required autoComplete="username" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} /></Field>
        <Field label="Şifre"><input type="password" required autoComplete="current-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} /></Field>
        <button className="primary" disabled={busy}>{busy ? "Giriş yapılıyor…" : "Giriş yap"}</button>
        {onSignUp ? <p className="small muted" style={{ margin: 0 }}>Hesabınız yok mu? <a href="#" onClick={(e) => { e.preventDefault(); onSignUp(); }}>Azure hesabınızla kaydolun</a></p> : null}
      </form>
    </div>
  );
}

/** Developer self sign-up: the PAT proves the Azure identity; the account sees the projects whose teams include that person. */
export function SignUp({ onSignedIn, onLogin }: { onSignedIn: () => void; onLogin: () => void }) {
  const [form, setForm] = useState({ email: "", displayName: "", password: "", personalAccessToken: "" });
  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: key === "personalAccessToken" ? e.target.value.trim() : e.target.value });
  const { error, busy, submit } = useSubmit(onSignedIn, () => api<AuthResult>("/api/auth/signup", { body: form }));
  return (
    <div className="auth">
      <form className="auth-card" onSubmit={submit}>
        <Brand />
        <div><h2 style={{ margin: 0 }}>Developer hesabı oluştur</h2>
          <p className="muted small" style={{ margin: "4px 0 0" }}>Azure DevOps'taki e-postanızla kaydolun. Takımında olduğunuz projeler, board'unuz ve kodlar hemen görünür.</p></div>
        {error ? <div className="error">{error}</div> : null}
        <div className="form-2">
          <Field label="Adınız"><input required value={form.displayName} onChange={set("displayName")} /></Field>
          <Field label="Azure DevOps e-postanız"><input type="email" required autoComplete="username" value={form.email} onChange={set("email")} /></Field>
        </div>
        <Field label="Şifre" hint={PASSWORD_HINT}><input type="password" required autoComplete="new-password" value={form.password} onChange={set("password")} /></Field>
        <Field label="Kişisel erişim token'ınız (PAT)" hint={`${PAT_HINT} Token'ın bu e-postaya ait olduğu Azure'a sorularak doğrulanır; agent'larınız Azure'da sizin adınıza çalışır.`}>
          <input type="password" required autoComplete="off" value={form.personalAccessToken} onChange={set("personalAccessToken")} />
        </Field>
        <button className="primary" disabled={busy}>{busy ? "Azure ile doğrulanıyor…" : "Hesap oluştur"}</button>
        <p className="small muted" style={{ margin: 0 }}>Hesabınız var mı? <a href="#" onClick={(e) => { e.preventDefault(); onLogin(); }}>Giriş yapın</a></p>
      </form>
    </div>
  );
}

/** First run: the administrator account plus the Azure DevOps organization; everything else is synced from it. */
export function Setup({ onSignedIn }: { onSignedIn: () => void }) {
  const [form, setForm] = useState({ displayName: "", email: "", password: "", azureOrganizationUrl: "", personalAccessToken: "" });
  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: key === "personalAccessToken" ? e.target.value.trim() : e.target.value });
  const { error, busy, submit } = useSubmit(onSignedIn, () => api<AuthResult>("/api/auth/setup", { body: form }));
  return (
    <div className="auth">
      <form className="auth-card wide" onSubmit={submit}>
        <Brand />
        <div><h2 style={{ margin: 0 }}>Kurulum</h2>
          <p className="muted small" style={{ margin: "4px 0 0" }}>
            Bir kez Azure DevOps organizasyonunuzu bağlayın. Tüm projeler, takımlar, board'lar, work item'lar ve repolar (geçmişleriyle) otomatik içeri alınır ve senkron tutulur.
          </p></div>
        {error ? <div className="error">{error}</div> : null}
        <h3 style={{ margin: 0 }}>1. Yönetici hesabı</h3>
        <div className="form-2">
          <Field label="Adınız"><input required value={form.displayName} onChange={set("displayName")} /></Field>
          <Field label="E-posta" hint="Azure DevOps e-postanız olursa board'unuz da hemen gelir"><input type="email" required autoComplete="username" value={form.email} onChange={set("email")} /></Field>
        </div>
        <Field label="Şifre" hint={PASSWORD_HINT}><input type="password" required autoComplete="new-password" value={form.password} onChange={set("password")} /></Field>
        <h3 style={{ margin: "6px 0 0" }}>2. Azure DevOps organizasyonu</h3>
        <Field label="Organizasyon URL'i" hint="Örn. https://dev.azure.com/sirketim">
          <input type="url" required placeholder="https://dev.azure.com/..." value={form.azureOrganizationUrl} onChange={set("azureOrganizationUrl")} />
        </Field>
        <Field label="Kişisel erişim token'ı (PAT)" hint={`${PAT_HINT} Organizasyonun tüm projelerini görebilen bir hesabın token'ı olmalı.`}>
          <input type="password" required autoComplete="off" value={form.personalAccessToken} onChange={set("personalAccessToken")} />
        </Field>
        <button className="primary" disabled={busy}>{busy ? "Azure'a bağlanılıyor…" : "Bağlan ve senkronize et"}</button>
      </form>
    </div>
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
    <div className="auth"><div className="auth-card">
      <Brand />
      {error ? <>
        <div className="error">{error}</div>
        <button className="primary" onClick={() => navigate("/", { replace: true })}>Giriş sayfasına dön</button>
      </> : <p className="muted">Microsoft hesabınızla giriş yapılıyor…</p>}
    </div></div>
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
    <div className="auth">
      <form className="auth-card" onSubmit={accept}>
        <Brand />
        <h2 style={{ margin: 0 }}>Davet</h2>
        {error ? <div className="error">{error}</div> : null}
        {info ? <>
          <p style={{ margin: 0 }}><strong>{info.organizationName}</strong> organizasyonuna davet edildiniz ({info.email}).</p>
          {info.ssoEnabled ? <>
            <MicrosoftButton providers={providers} onError={setError} />
            <div className="divider">veya şifre ile</div>
          </> : null}
          {info.hasAccount ? (
            <Field label="Mevcut şifreniz" hint="Bu e-postayla bir hesabınız var; daveti kabul etmek için şifrenizi girin.">
              <input type="password" required autoComplete="current-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
            </Field>
          ) : <>
            <Field label="Adınız"><input required value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} /></Field>
            <Field label="Şifre belirleyin" hint={PASSWORD_HINT}>
              <input type="password" required autoComplete="new-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
            </Field>
          </>}
          <button className="primary" disabled={busy}>Daveti kabul et</button>
        </> : null}
        <a href="/" className="small">Giriş sayfası</a>
      </form>
    </div>
  );
}
