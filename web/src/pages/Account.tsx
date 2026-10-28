import { useState } from "react";
import { api, fmtDate, type AzureCredential, type Me, type Project } from "../api";
import { ErrorBox, Field, Notice, useAction, useGet } from "../components/ui";

export function Account({ me }: { me: Me }) {
  return (
    <>
      <div className="page-head"><div><h1>Hesabım</h1><div className="subtitle">{me.user.displayName} · {me.user.email}</div></div></div>
      <div className="stack">
        <AzureConnections me={me} />
        <Password hasPassword={me.user.hasPassword} />
      </div>
    </>
  );
}

function AzureConnections({ me }: { me: Me }) {
  const credentials = useGet<AzureCredential[]>(["azure-credentials"], "/api/me/azure-credentials");
  const projects = useGet<Project[]>(["projects", me.organizations[0]?.organizationId], "/api/projects");
  const organizations = [...new Set((projects.data ?? []).map((p) => p.azureOrganizationUrl))];
  const [draft, setDraft] = useState({ azureOrganizationUrl: "", personalAccessToken: "", expiresAt: "" });
  const keys = [["azure-credentials"]];
  const save = useAction(() => api<AzureCredential>("/api/me/azure-credentials/pat", {
    method: "PUT",
    body: { azureOrganizationUrl: draft.azureOrganizationUrl || organizations[0], personalAccessToken: draft.personalAccessToken, expiresAt: draft.expiresAt ? new Date(draft.expiresAt).toISOString() : null },
  }), keys);
  const remove = useAction((id: string) => api(`/api/me/azure-credentials/${id}`, { method: "DELETE" }), keys);
  return (
    <section className="card stack">
      <h2>Azure DevOps bağlantım</h2>
      <p className="muted small">
        Bağlı hesabınızla yaptığınız atama ve yeni madde gibi işlemler Azure'da <strong>sizin adınıza</strong> görünür ve Azure'daki kendi yetkileriniz geçerli olur.
        Bağlantı yoksa projenin servis kimliği kullanılır (proje zorunlu kılmadıysa). Microsoft ile giriş yaptıysanız bağlantı otomatik kurulur.
      </p>
      <ErrorBox error={credentials.error ?? save.error ?? remove.error} /><Notice text={save.message} />
      {credentials.data?.length ? (
        <table>
          <thead><tr><th>Tür</th><th>Azure organizasyonu</th><th>Kimlik</th><th>Doğrulandı</th><th /></tr></thead>
          <tbody>{credentials.data.map((c) => (
            <tr key={c.id}>
              <td><span className="badge info">{c.kind === "OAuth" ? "Microsoft hesabı" : "PAT"}</span></td>
              <td>{c.azureOrganizationUrl ?? "Tüm organizasyonlar"}</td>
              <td>{c.azureDisplayName ?? c.azureUniqueName}<div className="small muted">{c.azureUniqueName}</div></td>
              <td>{fmtDate(c.verifiedAt)}{c.expiresAt && c.kind === "PersonalAccessToken" ? <div className="small muted">Son geçerlilik {fmtDate(c.expiresAt)}</div> : null}
                {c.lastError ? <div className="small" style={{ color: "var(--bad)" }}>{c.lastError}</div> : null}</td>
              <td className="right"><button className="danger" onClick={() => remove.run(c.id)}>Kaldır</button></td>
            </tr>
          ))}</tbody>
        </table>
      ) : credentials.data ? <div className="muted small">Henüz bağlantı yok.</div> : null}
      <h3>Kişisel erişim token'ı (PAT) ekle</h3>
      <div className="form-2">
        <Field label="Azure organizasyonu">
          <select value={draft.azureOrganizationUrl || organizations[0] || ""} onChange={(e) => setDraft({ ...draft, azureOrganizationUrl: e.target.value })}>
            {organizations.map((o) => <option key={o} value={o}>{o}</option>)}
          </select>
        </Field>
        <Field label="Son geçerlilik tarihi" hint="İsteğe bağlı; PAT'i oluştururken seçtiğiniz tarih">
          <input type="date" value={draft.expiresAt} onChange={(e) => setDraft({ ...draft, expiresAt: e.target.value })} />
        </Field>
      </div>
      <Field label="PAT" hint="Azure DevOps → User settings → Personal access tokens. Kapsam: Work Items (Read & write), Code (Read). Token'ın size ait olduğu Azure'a sorularak doğrulanır.">
        <input type="password" autoComplete="off" value={draft.personalAccessToken} onChange={(e) => setDraft({ ...draft, personalAccessToken: e.target.value.trim() })} />
      </Field>
      <div className="row"><span className="spacer" />
        <button className="primary" disabled={save.busy || !draft.personalAccessToken || organizations.length === 0}
          onClick={async () => { const r = await save.run(undefined, (c) => `Bağlandı: ${c.azureUniqueName}`); if (r) setDraft({ ...draft, personalAccessToken: "" }); }}>
          {save.busy ? "Doğrulanıyor…" : "Doğrula ve kaydet"}
        </button>
      </div>
    </section>
  );
}

function Password({ hasPassword }: { hasPassword: boolean }) {
  const [form, setForm] = useState({ currentPassword: "", newPassword: "" });
  const change = useAction(() => api("/api/auth/change-password", { body: { currentPassword: form.currentPassword || null, newPassword: form.newPassword } }), [["me"]]);
  return (
    <section className="card form">
      <h2>{hasPassword ? "Şifre değiştir" : "Şifre belirle"}</h2>
      {!hasPassword ? <p className="muted small">Hesabınız Microsoft girişiyle açıldı. İsterseniz e-posta ile giriş için bir şifre de belirleyebilirsiniz.</p> : null}
      <ErrorBox error={change.error} /><Notice text={change.message} />
      <div className="form-2">
        {hasPassword ? <Field label="Mevcut şifre"><input type="password" autoComplete="current-password" value={form.currentPassword} onChange={(e) => setForm({ ...form, currentPassword: e.target.value })} /></Field> : null}
        <Field label="Yeni şifre" hint="En az 10 karakter; büyük harf, küçük harf ve rakam"><input type="password" autoComplete="new-password" value={form.newPassword} onChange={(e) => setForm({ ...form, newPassword: e.target.value })} /></Field>
      </div>
      <div className="row"><span className="spacer" /><button className="primary" disabled={change.busy || !form.newPassword} onClick={async () => {
        await change.run(undefined, "Şifre kaydedildi. Güvenlik için açık oturumlarınız sonlandırıldı; bir sonraki yenilemede yeniden giriş istenir.");
        setForm({ currentPassword: "", newPassword: "" });
      }}>Kaydet</button></div>
    </section>
  );
}
