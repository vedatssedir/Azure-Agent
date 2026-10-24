import { useState } from "react";
import { api, ApiError, session, type AuthResult } from "../api";
import { Field } from "../components/ui";

export function Login({ onSignedIn }: { onSignedIn: () => void }) {
  const [mode, setMode] = useState<"login" | "setup">("login");
  const [form, setForm] = useState({ email: "", password: "", displayName: "", organizationName: "" });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
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
