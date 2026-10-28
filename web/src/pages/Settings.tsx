import { useEffect, useState } from "react";
import { NavLink, useParams } from "react-router-dom";
import { api, fmtAgo, fmtDate, INVITATION_LABEL, ORG_ROLE_LABEL, PROVIDER_LABEL, ROLE_LABEL, RUNTIME_LABEL, SOURCE_LABEL, type AccessRule, type AiProvider,
  type AuditEntry, type InvitationLink, type OrganizationRole, type OrganizationSettings, type ProjectRole, type Repository, type RepositoryPolicy, type Settings,
  type Users } from "../api";
import { useApp } from "../context";
import { Avatar, Check, Empty, ErrorBox, Field, Loading, Notice, PageHead, useAction, useGet } from "../components/ui";
import { Icon } from "../components/icons";

const TABS: Array<[string, string]> = [["azure", "Azure DevOps"], ["ai", "AI sağlayıcıları"], ["automation", "Otomasyon ve bütçe"], ["users", "Kullanıcılar ve erişim"],
  ["repos", "Repolar ve izinler"], ["audit", "Denetim kaydı"]];

/** The single place for every technical setting of the organization. */
export function SettingsPage() {
  const { tab = "azure" } = useParams();
  const settings = useGet<Settings>(["settings"], "/api/settings", { refetchInterval: tab === "azure" ? 15_000 : undefined });
  return (
    <>
      <PageHead title="Ayarlar" subtitle="Organizasyonun tüm teknik ayarları burada: Azure bağlantısı, AI anahtarları ve CLI'lar, otomasyon, kullanıcılar ve repo izinleri." />
      <div className="settings">
        <nav className="settings-nav" aria-label="Ayar bölümleri">
          {TABS.map(([key, label]) => <NavLink key={key} to={`/settings/${key}`} className={() => (tab === key ? "active" : "")}>{label}</NavLink>)}
        </nav>
        <div className="stack" style={{ minWidth: 0 }}>
          <ErrorBox error={settings.error} />
          {settings.isLoading ? <Loading /> : settings.data ? (
            tab === "ai" ? <AiProviders settings={settings.data} />
              : tab === "automation" ? <Automation settings={settings.data} />
              : tab === "users" ? <UsersAndAccess settings={settings.data} />
              : tab === "repos" ? <RepositoryPolicies />
              : tab === "audit" ? <Audit />
              : <AzureConnection settings={settings.data} />
          ) : null}
        </div>
      </div>
    </>
  );
}

// Azure DevOps ----------------------------------------------------------------------------------------

function AzureConnection({ settings }: { settings: Settings }) {
  const a = settings.azure;
  const { organization } = useApp();
  const [form, setForm] = useState({ organizationUrl: a.organizationUrl ?? "", personalAccessToken: "", servicePrincipalTenantId: "", servicePrincipalClientId: "", servicePrincipalClientSecret: "" });
  const [mode, setMode] = useState<"pat" | "sp">(a.credentialType === "service-principal" ? "sp" : "pat");
  const [hookPassword, setHookPassword] = useState<string | null>(null);
  const keys = [["settings"], ["projects"]];
  const save = useAction(() => api("/api/settings/azure", {
    method: "PUT",
    body: mode === "pat"
      ? { organizationUrl: form.organizationUrl, personalAccessToken: form.personalAccessToken || null }
      : { organizationUrl: form.organizationUrl, servicePrincipalTenantId: form.servicePrincipalTenantId, servicePrincipalClientId: form.servicePrincipalClientId, servicePrincipalClientSecret: form.servicePrincipalClientSecret },
  }), keys);
  const sync = useAction(() => api("/api/settings/azure/sync", { body: {} }), keys);
  const rotate = useAction(() => api<{ password: string }>("/api/settings/azure/service-hook-password", { body: {} }), keys);
  const hookUrl = `${window.location.origin}/api/hooks/${organization.organizationId}`;
  return <>
    <section className="card stack">
      <div className="section-title"><h2>Bağlantı durumu</h2><span className="spacer" />
        <button disabled={sync.busy} onClick={() => sync.run(undefined, "Senkron başlatıldı; projeler, board'lar ve repolar arka planda güncelleniyor.")}><Icon name="refresh" /> Şimdi senkronize et</button></div>
      <ErrorBox error={sync.error} /><Notice text={sync.message} />
      {a.lastSyncError ? <div className="error">Son senkron hatası: {a.lastSyncError}</div> : null}
      <div className="stats">
        <div className="stat"><div className="label">Organizasyon</div><div className="value" style={{ fontSize: 16 }}>{a.organizationName}</div></div>
        <div className="stat"><div className="label">Proje</div><div className="value">{a.projects}</div></div>
        <div className="stat"><div className="label">Repo (kopyalanan)</div><div className="value">{a.repositories} <span className="small muted">({a.mirroredRepositories})</span></div></div>
        <div className="stat"><div className="label">Takım</div><div className="value">{a.teams}</div></div>
        <div className="stat"><div className="label">Work item</div><div className="value">{a.workItems}</div></div>
        <div className="stat"><div className="label">Son senkron</div><div className="value" style={{ fontSize: 16 }}>{fmtAgo(a.lastSyncedAt)}</div></div>
      </div>
      <p className="small muted" style={{ margin: 0 }}>
        Organizasyondaki tüm projeler, takımlar, board kolonları, iteration'lar, work item'lar ve repolar (tüm geçmişiyle) otomatik içeri alınır; Azure'da yeni açılan projeler de bir sonraki senkronda gelir.
        Senkron aralığı "Otomasyon" bölümünden ayarlanır.
      </p>
    </section>
    <section className="card stack">
      <h2>Azure DevOps organizasyonu</h2>
      <ErrorBox error={save.error} /><Notice text={save.message} />
      <Field label="Organizasyon URL'i"><input type="url" value={form.organizationUrl} onChange={(e) => setForm({ ...form, organizationUrl: e.target.value })} placeholder="https://dev.azure.com/sirketim" /></Field>
      <div className="row" role="radiogroup">
        <label className="check"><input type="radio" checked={mode === "pat"} onChange={() => setMode("pat")} /> Kişisel erişim token'ı (PAT) {a.patConfigured ? <span className="badge ok">tanımlı</span> : null}</label>
        <label className="check"><input type="radio" checked={mode === "sp"} onChange={() => setMode("sp")} /> Service principal (Entra ID) {a.servicePrincipalConfigured ? <span className="badge ok">tanımlı</span> : null}</label>
      </div>
      {mode === "pat" ? (
        <Field label="PAT" hint={a.patConfigured ? "Değiştirmek için yeni token girin; boş bırakırsanız mevcut token korunur." : "Work Items (Read & write), Code (Read & write), Project and Team (Read) kapsamları"}>
          <input type="password" autoComplete="off" value={form.personalAccessToken} onChange={(e) => setForm({ ...form, personalAccessToken: e.target.value.trim() })} placeholder={a.patConfigured ? "••••••••" : ""} />
        </Field>
      ) : (
        <div className="form-3">
          <Field label="Tenant ID"><input value={form.servicePrincipalTenantId} onChange={(e) => setForm({ ...form, servicePrincipalTenantId: e.target.value.trim() })} /></Field>
          <Field label="Client ID"><input value={form.servicePrincipalClientId} onChange={(e) => setForm({ ...form, servicePrincipalClientId: e.target.value.trim() })} /></Field>
          <Field label="Client secret"><input type="password" autoComplete="off" value={form.servicePrincipalClientSecret} onChange={(e) => setForm({ ...form, servicePrincipalClientSecret: e.target.value.trim() })} /></Field>
        </div>
      )}
      <div className="row"><span className="spacer" /><button className="primary" disabled={save.busy || !form.organizationUrl}
        onClick={async () => { if (await save.run(undefined, "Bağlantı doğrulandı ve kaydedildi; senkron başladı.")) setForm({ ...form, personalAccessToken: "", servicePrincipalClientSecret: "" }); }}>
        {save.busy ? "Doğrulanıyor…" : "Doğrula ve kaydet"}</button></div>
    </section>
    <section className="card stack">
      <h2>Anlık güncellemeler (Service hooks)</h2>
      <p className="small muted" style={{ margin: 0 }}>
        İsteğe bağlı: Azure DevOps → Project settings → Service hooks → Web Hooks ile "Work item created/updated", "Code pushed" ve "Pull request created/updated" olaylarını aşağıdaki adrese gönderin.
        Böylece değişiklikler senkron aralığını beklemeden gelir. Kimlik doğrulama: Basic, kullanıcı adı herhangi, şifre aşağıdaki şifre.
      </p>
      <Field label="URL"><input readOnly value={hookUrl} onFocus={(e) => e.target.select()} className="mono" /></Field>
      <ErrorBox error={rotate.error} />
      {hookPassword ? <div className="callout">Şifre (yalnızca bir kez gösterilir): <span className="mono">{hookPassword}</span></div> : null}
      <div className="row">{a.serviceHookPasswordConfigured ? <span className="badge ok">şifre tanımlı</span> : <span className="badge warn">şifre yok</span>}<span className="spacer" />
        <button disabled={rotate.busy} onClick={async () => { const r = await rotate.run(undefined); if (r) setHookPassword(r.password); }}>{a.serviceHookPasswordConfigured ? "Şifreyi yenile" : "Şifre oluştur"}</button></div>
    </section>
  </>;
}

// AI providers -----------------------------------------------------------------------------------------

const LOGO: Record<string, [string, string]> = {
  "claude-code": ["#d97757", "C"], cursor: ["#111111", "▲"], anthropic: ["#c96442", "A"], openai: ["#10a37f", "O"], "azure-openai": ["#0078d4", "Az"], gemini: ["#4285f4", "G"],
};

function AiProviders({ settings }: { settings: Settings }) {
  const [defaults, setDefaults] = useState({ defaultRuntime: settings.automation.defaultRuntime, defaultProvider: settings.automation.defaultProvider,
    defaultModel: settings.automation.defaultModel, azureOpenAiEndpoint: settings.automation.azureOpenAiEndpoint ?? "" });
  const saveDefaults = useAction(() => api("/api/settings/automation", {
    method: "PUT", body: { settings: { ...settings.automation, ...defaults, azureOpenAiEndpoint: defaults.azureOpenAiEndpoint || null }, monthlyBudgetUsd: settings.monthlyBudgetUsd },
  }), [["settings"], ["providers"]]);
  const usable = settings.providers.filter((x) => x.configured && x.installed !== false);
  return <>
    <p className="muted" style={{ margin: 0 }}>Bir sağlayıcının anahtarını girdiğiniz anda agent'lar onu kullanabilir. CLI'lar (Claude Code, Cursor) sunucuda kurulu olmalı; durumları aşağıda görünür.</p>
    {settings.providers.map((x) => <ProviderCard key={x.key} provider={x} isDefault={x.runtime === defaults.defaultRuntime && (x.kind === "cli" || x.provider === defaults.defaultProvider)}
      endpoint={x.key === "azure-openai" ? defaults.azureOpenAiEndpoint : undefined} onEndpoint={(v) => setDefaults({ ...defaults, azureOpenAiEndpoint: v })}
      onDefault={() => setDefaults({ ...defaults, defaultRuntime: x.runtime, defaultProvider: x.provider, defaultModel: x.defaultModel })} />)}
    <section className="card stack">
      <h2>Yeni agent'ların varsayılanı</h2>
      <ErrorBox error={saveDefaults.error} /><Notice text={saveDefaults.message} />
      <div className="form-3">
        <Field label="Çalışma ortamı">
          <select value={`${defaults.defaultRuntime}|${defaults.defaultProvider}`} onChange={(e) => {
            const [runtime, provider] = e.target.value.split("|");
            const match = settings.providers.find((x) => x.runtime === runtime && x.provider === provider);
            setDefaults({ ...defaults, defaultRuntime: runtime as typeof defaults.defaultRuntime, defaultProvider: provider as typeof defaults.defaultProvider, defaultModel: match?.defaultModel ?? defaults.defaultModel });
          }}>
            {settings.providers.map((x) => <option key={x.key} value={`${x.runtime}|${x.provider}`}>{x.name}{usable.includes(x) ? "" : " (hazır değil)"}</option>)}
          </select>
        </Field>
        <Field label="Model"><input className="mono" value={defaults.defaultModel} onChange={(e) => setDefaults({ ...defaults, defaultModel: e.target.value })} /></Field>
        <div style={{ alignSelf: "end" }}><button className="primary" disabled={saveDefaults.busy} onClick={() => saveDefaults.run(undefined, "Kaydedildi")}>Kaydet</button></div>
      </div>
      <p className="small muted" style={{ margin: 0 }}>Developer'lar kaydolduğunda otomatik oluşturulan agent da bu ayarla gelir: {RUNTIME_LABEL[defaults.defaultRuntime]} · {PROVIDER_LABEL[defaults.defaultProvider]} · <span className="mono">{defaults.defaultModel}</span></p>
    </section>
  </>;
}

function ProviderCard({ provider: x, isDefault, onDefault, endpoint, onEndpoint }: {
  provider: AiProvider; isDefault: boolean; onDefault: () => void; endpoint?: string; onEndpoint: (v: string) => void;
}) {
  const [values, setValues] = useState<Record<string, string>>({});
  const keys = [["settings"], ["providers"]];
  const setCredential = useAction(({ name, value }: { name: string; value: string }) => api(`/api/settings/ai/credentials/${name}`, { method: "PUT", body: { value } }).then(() => true), keys);
  const remove = useAction((name: string) => api(`/api/settings/ai/credentials/${name}`, { method: "DELETE" }), keys);
  const test = useAction(() => api<{ ok: boolean; detail: string }>(`/api/settings/ai/${x.key}/test`, { body: {} }));
  const [result, setResult] = useState<{ ok: boolean; detail: string } | null>(null);
  const [color, letter] = LOGO[x.key] ?? ["#605e5c", x.name[0]];
  const ready = x.configured && x.installed !== false;
  return (
    <section className="card provider">
      <div className="provider-head">
        <span className="logo" style={{ background: color }}>{letter}</span>
        <div style={{ flex: 1 }}>
          <div className="row"><strong>{x.name}</strong><span className="tag">{x.kind === "cli" ? "CLI" : "API"}</span>
            <span className={`badge ${ready ? "ok" : "warn"}`}>{ready ? "Kullanıma hazır" : x.installed === false ? "CLI kurulu değil" : "Anahtar gerekli"}</span>
            {isDefault ? <span className="badge info">varsayılan</span> : null}</div>
          <div className="small muted">{x.description}</div>
        </div>
        <button className="small" disabled={!ready || test.busy} onClick={async () => { setResult(null); const r = await test.run(undefined); if (r) setResult(r); }}>{test.busy ? "Test ediliyor…" : "Test et"}</button>
        {!isDefault ? <button className="small" onClick={onDefault}>Varsayılan yap</button> : null}
      </div>
      {x.kind === "cli" ? (
        <div className="small">{x.installed ? <span className="badge ok">kurulu</span> : <span className="badge bad">kurulu değil</span>} {x.version ? <span className="mono muted">{x.version}</span> : null}
          {!x.installed && x.installHint ? <div className="muted" style={{ marginTop: 4 }}>Kurulum: <span className="mono">{x.installHint}</span></div> : null}</div>
      ) : null}
      <ErrorBox error={setCredential.error ?? remove.error ?? test.error} />
      {result ? <div className={result.ok ? "notice" : "error"}>{result.detail}</div> : null}
      {x.credentials.map((c) => (
        <div key={c.name} className="row nowrap" style={{ alignItems: "flex-end" }}>
          <Field label={c.label} hint={c.configured ? `Tanımlı · ${fmtDate(c.updatedAt)}` : undefined}>
            <input type="password" autoComplete="off" placeholder={c.configured ? "••••••••••••" : "Anahtarı yapıştırın"} value={values[c.name] ?? ""}
              onChange={(e) => setValues({ ...values, [c.name]: e.target.value.trim() })} />
          </Field>
          <button className="primary" disabled={!values[c.name] || setCredential.busy}
            onClick={async () => { if (await setCredential.run({ name: c.name, value: values[c.name]! })) setValues({ ...values, [c.name]: "" }); }}>Kaydet</button>
          {c.configured ? <button className="danger" disabled={remove.busy} onClick={() => remove.run(c.name)}>Kaldır</button> : null}
        </div>
      ))}
      {endpoint !== undefined ? (
        <Field label="Endpoint" hint="https://<kaynak>.openai.azure.com — kaydetmek için alttaki 'Varsayılan' bölümünde Kaydet'e basın">
          <input value={endpoint} onChange={(e) => onEndpoint(e.target.value)} placeholder="https://<kaynak>.openai.azure.com" />
        </Field>
      ) : null}
    </section>
  );
}

// Automation & budgets ---------------------------------------------------------------------------------

function Automation({ settings }: { settings: Settings }) {
  const { projects } = useApp();
  const [form, setForm] = useState<OrganizationSettings>(settings.automation);
  const [budget, setBudget] = useState<number | null>(settings.monthlyBudgetUsd);
  const save = useAction(() => api("/api/settings/automation", { method: "PUT", body: { settings: form, monthlyBudgetUsd: budget } }), [["settings"]]);
  const set = <K extends keyof OrganizationSettings>(key: K, value: OrganizationSettings[K]) => setForm({ ...form, [key]: value });
  return <>
    <section className="card stack">
      <h2>Agent otomasyonu</h2>
      <Check label="Atanan işleri otomatik başlat" hint="Bir work item birine atandığında (veya agent:<slug> etiketi aldığında) ilgili agent işe başlar. Her madde bir kez otomatik başlatılır."
        checked={form.autoDispatch} onChange={(v) => set("autoDispatch", v)} />
      <Check label="Çalıştırmadan önce onay iste" hint="Otomatik başlatılan işler 'Onay bekliyor' durumunda bekler." checked={form.requireRunApproval} onChange={(v) => set("requireRunApproval", v)} />
      <Check label="Work item durumunu güncelle" hint="Agent başladığında maddeyi 'devam ediyor', PR açtığında 'çözüldü' kategorisindeki duruma taşır." checked={form.updateWorkItemState} onChange={(v) => set("updateWorkItemState", v)} />
      <Check label="Pull request'leri otomatik incele" hint="Yeni PR'lar reviewer agent'a verilir (repo politikasında da açık olmalı)." checked={form.autoReviewPullRequests} onChange={(v) => set("autoReviewPullRequests", v)} />
      <Field label="Senkron aralığı (dakika)"><input type="number" min={1} max={1440} value={form.syncIntervalMinutes} onChange={(e) => set("syncIntervalMinutes", Number(e.target.value))} style={{ width: 160 }} /></Field>
    </section>
    <section className="card stack">
      <h2>Kullanıcılar</h2>
      <Check label="Developer'lar kendi e-postalarıyla kaydolabilir" hint="Azure'daki bir takımda olan herkes PAT'iyle hesap açabilir." checked={form.allowSelfSignUp} onChange={(v) => set("allowSelfSignUp", v)} />
      <Check label="Takım üyelerini projelere otomatik ekle" hint="Azure takım üyeliği proje rolüne dönüşür (rol eşlemesi 'Kullanıcılar' bölümünde)." checked={form.autoProvisionMembers} onChange={(v) => set("autoProvisionMembers", v)} />
      <Check label="Hesabı olmayan takım üyelerini davet et" hint="E-posta gönderimi yapılandırılmışsa davet e-postası gider." checked={form.autoInviteTeamMembers} onChange={(v) => set("autoInviteTeamMembers", v)} />
      <Check label="Her developer'a bir varsayılan agent oluştur" checked={form.createDefaultAgentForDevelopers} onChange={(v) => set("createDefaultAgentForDevelopers", v)} />
      <Check label="Azure'a yazarken kullanıcının kendi kimliğini zorunlu kıl" hint="Atama, yorum ve agent işlemleri yalnızca kişisel Azure bağlantısı olanlar için yapılır." checked={form.requireUserCredentialForWrites} onChange={(v) => set("requireUserCredentialForWrites", v)} />
    </section>
    <section className="card stack">
      <h2>Bütçe</h2>
      <Field label="Organizasyonun aylık bütçesi (USD)" hint="Aşılınca tüm agent'lar durur. Boş: limit yok.">
        <input type="number" min={0} value={budget ?? ""} onChange={(e) => setBudget(e.target.value ? Number(e.target.value) : null)} style={{ width: 200 }} />
      </Field>
      <ErrorBox error={save.error} /><Notice text={save.message} />
      <div className="row"><span className="spacer" /><button className="primary" disabled={save.busy} onClick={() => save.run(undefined, "Kaydedildi")}>Kaydet</button></div>
      <h3>Proje bütçeleri</h3>
      <table><tbody>{projects.map((x) => <ProjectBudget key={x.id} id={x.id} name={x.name} value={x.monthlyBudgetUsd} />)}</tbody></table>
    </section>
  </>;
}

function ProjectBudget({ id, name, value }: { id: string; name: string; value: number | null }) {
  const [budget, setBudget] = useState(value === null ? "" : String(value));
  const save = useAction(() => api(`/api/settings/projects/${id}/budget`, { method: "PUT", body: { monthlyBudgetUsd: budget ? Number(budget) : null } }).then(() => true), [["projects"]]);
  return (
    <tr><td>{name}</td><td style={{ width: 200 }}><input aria-label={`${name} bütçesi`} type="number" min={0} placeholder="limit yok" value={budget} onChange={(e) => setBudget(e.target.value)} /></td>
      <td style={{ width: 160 }}><button className="small" disabled={save.busy} onClick={() => save.run(undefined, "Kaydedildi")}>Kaydet</button> {save.message ? <span className="small" style={{ color: "var(--ok)" }}>✓</span> : null}
        {save.error ? <span className="small" style={{ color: "var(--bad)" }}>{save.error}</span> : null}</td></tr>
  );
}

// Users & access ---------------------------------------------------------------------------------------

const PROJECT_ROLES: ProjectRole[] = ["Viewer", "Developer", "Reviewer", "Maintainer", "Admin"];

function UsersAndAccess({ settings }: { settings: Settings }) {
  const { projects, me } = useApp();
  const users = useGet<Users>(["users"], "/api/settings/users");
  const keys = [["users"]];
  const setRole = useAction(({ userId, role }: { userId: string; role: OrganizationRole }) => api(`/api/settings/users/${userId}/role`, { method: "PUT", body: { role } }), keys);
  const setActive = useAction(({ userId, isActive }: { userId: string; isActive: boolean }) => api(`/api/settings/users/${userId}/active`, { method: "PUT", body: { isActive } }), keys);
  const setProject = useAction(({ userId, projectId, role }: { userId: string; projectId: string; role: ProjectRole | "" }) => role
    ? api(`/api/settings/users/${userId}/projects/${projectId}`, { method: "PUT", body: { role, azureUniqueName: null } })
    : api(`/api/settings/users/${userId}/projects/${projectId}`, { method: "DELETE" }), keys);
  const invite = useAction((people: Array<{ email: string; displayName: string }>) => api<InvitationLink[]>("/api/settings/invitations", { body: { people, role: "Member", projectId: null } }), keys);
  const revoke = useAction((id: string) => api(`/api/settings/invitations/${id}`, { method: "DELETE" }), keys);
  const [links, setLinks] = useState<InvitationLink[]>([]);
  const [roles, setRoles] = useState(settings.automation.teamRoles);
  const saveRoles = useAction(() => api("/api/settings/automation", { method: "PUT", body: { settings: { ...settings.automation, teamRoles: roles }, monthlyBudgetUsd: settings.monthlyBudgetUsd } }), [["settings"]]);
  const [newRole, setNewRole] = useState({ team: "", role: "Developer" as ProjectRole });
  useEffect(() => setRoles(settings.automation.teamRoles), [settings.automation.teamRoles]);
  const error = users.error ?? setRole.error ?? setActive.error ?? setProject.error ?? invite.error ?? revoke.error;
  return <>
    <ErrorBox error={error} />
    <section className="card flush">
      <div style={{ padding: "16px 16px 0" }}><h2>Kullanıcılar</h2>
        <p className="small muted" style={{ marginTop: -6 }}>Developer'lar Azure takım üyeliklerine göre projelere otomatik eklenir. Buradan organizasyon ve proje rollerini değiştirebilirsiniz.</p></div>
      {users.isLoading ? <Loading /> : (
        <table>
          <thead><tr><th>Kullanıcı</th><th>Organizasyon rolü</th>{projects.map((x) => <th key={x.id}>{x.name}</th>)}<th>Azure</th><th>Agent</th><th>Son giriş</th><th /></tr></thead>
          <tbody>{(users.data?.users ?? []).map((u) => (
            <tr key={u.userId} style={u.isActive ? undefined : { opacity: 0.55 }}>
              <td><span className="row nowrap"><Avatar name={u.displayName} /><span><strong>{u.displayName}</strong><div className="small muted">{u.email}</div></span></span></td>
              <td><select aria-label={`${u.displayName} rolü`} value={u.role} disabled={u.userId === me.user.id} onChange={(e) => setRole.run({ userId: u.userId, role: e.target.value as OrganizationRole })}>
                {(["Member", "Admin", "Owner"] as OrganizationRole[]).map((r) => <option key={r} value={r}>{ORG_ROLE_LABEL[r]}</option>)}</select></td>
              {projects.map((x) => {
                const membership = u.projects.find((m) => m.projectId === x.id);
                return (
                  <td key={x.id}>
                    <select aria-label={`${u.displayName} ${x.name} rolü`} value={membership?.role ?? ""} onChange={(e) => setProject.run({ userId: u.userId, projectId: x.id, role: e.target.value as ProjectRole | "" })}>
                      <option value="">Erişim yok</option>{PROJECT_ROLES.map((r) => <option key={r} value={r}>{ROLE_LABEL[r]}</option>)}
                    </select>
                    {membership ? <div className="small subtle">{SOURCE_LABEL[membership.source]}</div> : null}
                  </td>
                );
              })}
              <td>{u.azureConnected ? <span className="badge ok">bağlı</span> : <span className="badge warn">bağlı değil</span>}</td>
              <td>{u.agents}</td>
              <td className="small muted">{fmtAgo(u.lastLoginAt)}</td>
              <td>{u.userId !== me.user.id ? <button className="small" onClick={() => setActive.run({ userId: u.userId, isActive: !u.isActive })}>{u.isActive ? "Devre dışı bırak" : "Etkinleştir"}</button> : null}</td>
            </tr>
          ))}</tbody>
        </table>
      )}
    </section>
    <section className="card stack">
      <div className="section-title"><h2>Henüz hesabı olmayan Azure takım üyeleri</h2><span className="spacer" />
        {users.data?.withoutAccount.some((x) => !x.invitationStatus) ? (
          <button disabled={invite.busy} onClick={async () => { const r = await invite.run(users.data!.withoutAccount.filter((x) => !x.invitationStatus).map((x) => ({ email: x.uniqueName, displayName: x.displayName }))); if (r) setLinks(r); }}>Hepsini davet et</button>
        ) : null}</div>
      <p className="small muted" style={{ margin: 0 }}>{settings.automation.allowSelfSignUp ? "Bu kişiler kendi e-postaları ve PAT'leriyle doğrudan kaydolabilir; isterseniz davet bağlantısı da gönderebilirsiniz." : "Kendi kendine kayıt kapalı; bu kişileri davet edin."}</p>
      {links.length ? <div className="callout small stack tight">{links.map((l) => <div key={l.id}><strong>{l.email}</strong>: {l.emailSent ? "e-posta gönderildi" : <span className="mono">{l.url}</span>}</div>)}</div> : null}
      {users.data?.withoutAccount.length ? (
        <table>
          <thead><tr><th>Kişi</th><th>Projeler</th><th>Durum</th><th /></tr></thead>
          <tbody>{users.data.withoutAccount.map((x) => (
            <tr key={x.uniqueName}>
              <td><span className="row nowrap"><Avatar name={x.displayName} /><span>{x.displayName}<div className="small muted">{x.uniqueName}</div></span></span></td>
              <td className="small">{x.projects.join(", ")}</td>
              <td>{x.invitationStatus ? <span className={`badge ${INVITATION_LABEL[x.invitationStatus].cls}`}>{INVITATION_LABEL[x.invitationStatus].label}</span> : <span className="subtle small">—</span>}</td>
              <td className="right">{!x.invitationStatus || x.invitationStatus === "Expired" ? <button className="small" disabled={invite.busy}
                onClick={async () => { const r = await invite.run([{ email: x.uniqueName, displayName: x.displayName }]); if (r) setLinks(r); }}>Davet et</button> : null}</td>
            </tr>
          ))}</tbody>
        </table>
      ) : <Empty>Tüm takım üyelerinin hesabı var.</Empty>}
      {users.data?.invitations.filter((i) => i.status === "Pending").length ? <>
        <h3>Bekleyen davetler</h3>
        <table><tbody>{users.data.invitations.filter((i) => i.status === "Pending").map((i) => (
          <tr key={i.id}><td>{i.displayName} <span className="small muted">{i.email}</span></td><td className="small muted">{i.invitedBy} · son geçerlilik {fmtDate(i.expiresAt)}</td>
            <td className="right"><button className="small danger" onClick={() => revoke.run(i.id)}>İptal</button></td></tr>
        ))}</tbody></table>
      </> : null}
    </section>
    <section className="card stack">
      <h2>Takım → proje rolü eşlemesi</h2>
      <p className="small muted" style={{ margin: 0 }}>Azure takımındaki kişiler projede bu rolü alır. Eşleme yoksa takım üyeleri Developer olur.</p>
      <ErrorBox error={saveRoles.error} /><Notice text={saveRoles.message} />
      <table><tbody>
        {roles.map((r, i) => (
          <tr key={i}><td>{r.team}</td><td>{ROLE_LABEL[r.role]}</td><td className="right"><button className="small ghost danger" onClick={() => setRoles(roles.filter((_, j) => j !== i))}>Kaldır</button></td></tr>
        ))}
        <tr>
          <td><input aria-label="Takım adı" placeholder="Takım adı (örn. Test_Project Team)" value={newRole.team} onChange={(e) => setNewRole({ ...newRole, team: e.target.value })} /></td>
          <td><select aria-label="Rol" value={newRole.role} onChange={(e) => setNewRole({ ...newRole, role: e.target.value as ProjectRole })}>{PROJECT_ROLES.map((r) => <option key={r} value={r}>{ROLE_LABEL[r]}</option>)}</select></td>
          <td className="right"><button className="small" disabled={!newRole.team.trim()} onClick={() => { setRoles([...roles, { team: newRole.team.trim(), role: newRole.role }]); setNewRole({ team: "", role: "Developer" }); }}>Ekle</button></td>
        </tr>
      </tbody></table>
      <div className="row"><span className="spacer" /><button className="primary" disabled={saveRoles.busy} onClick={() => saveRoles.run(undefined, "Kaydedildi; bir sonraki senkronda uygulanır.")}>Kaydet</button></div>
    </section>
  </>;
}

// Repository policies -----------------------------------------------------------------------------------

function RepositoryPolicies() {
  const repos = useGet<Repository[]>(["repos", "all"], "/api/repos");
  const [selected, setSelected] = useState<string | null>(null);
  const repo = repos.data?.find((r) => r.id === selected) ?? repos.data?.[0];
  if (repos.isLoading) return <Loading />;
  if (!repos.data?.length) return <Empty>Henüz repo yok.</Empty>;
  return (
    <div className="repo-layout">
      <nav className="tree">
        {repos.data.map((r) => (
          <div key={r.id} className={`tree-item${r.id === repo?.id ? " active" : ""}`} onClick={() => setSelected(r.id)}>
            <Icon name="repos" /><span className="truncate" style={{ flex: 1 }}>{r.name}</span>{!r.policy.agentsEnabled ? <span className="badge">kapalı</span> : null}
          </div>
        ))}
      </nav>
      {repo ? <RepositoryPolicyEditor key={repo.id} repo={repo} /> : null}
    </div>
  );
}

function RepositoryPolicyEditor({ repo }: { repo: Repository }) {
  const [policy, setPolicy] = useState<RepositoryPolicy>(repo.policy);
  const [branches, setBranches] = useState(repo.policy.protectedBranches.join(", "));
  const [commands, setCommands] = useState(repo.policy.allowedCommands.join(", "));
  const save = useAction(() => api(`/api/settings/repositories/${repo.id}/policy`, {
    method: "PUT", body: { ...policy, protectedBranches: branches.split(",").map((s) => s.trim()).filter(Boolean), allowedCommands: commands.split(",").map((s) => s.trim()).filter(Boolean) },
  }).then(() => true), [["repos"]]);
  const users = useGet<Users>(["users"], "/api/settings/users");
  const rules = useGet<AccessRule[]>(["access-rules", repo.id], `/api/settings/repositories/${repo.id}/access-rules`);
  const permissions = useGet<string[]>(["permissions"], "/api/permissions");
  const [rule, setRule] = useState({ subject: "role:Developer", permission: "repo.agent.write", effect: "Deny" as "Allow" | "Deny" });
  const addRule = useAction(() => {
    const [kind, value] = rule.subject.split(":");
    return api(`/api/settings/repositories/${repo.id}/access-rules`, { body: { userId: kind === "user" ? value : null, role: kind === "role" ? value : null, permission: rule.permission, effect: rule.effect } });
  }, [["access-rules", repo.id]]);
  const removeRule = useAction((id: string) => api(`/api/settings/repositories/${repo.id}/access-rules/${id}`, { method: "DELETE" }), [["access-rules", repo.id]]);
  const repoPermissions = (permissions.data ?? []).filter((x) => x.startsWith("repo.") || x.startsWith("pullrequests."));
  return (
    <div className="stack">
      <section className="card stack">
        <div className="section-title"><h2>{repo.name}</h2><span className="muted small">{repo.projectName}</span></div>
        <ErrorBox error={save.error} /><Notice text={save.message} />
        <Check label="Agent'lar bu repoda çalışabilir" checked={policy.agentsEnabled} onChange={(v) => setPolicy({ ...policy, agentsEnabled: v })} />
        <Check label="Yeni pull request'leri otomatik incele" checked={policy.autoReview} onChange={(v) => setPolicy({ ...policy, autoReview: v })} />
        <Field label="Korumalı branch'ler" hint="Agent'lar bunlara doğrudan push edemez (virgülle; * joker)"><input value={branches} onChange={(e) => setBranches(e.target.value)} /></Field>
        <Field label="İzin verilen komutlar" hint="Agent'ın çalıştırabileceği komutlar: build, test, lint (virgülle)"><input className="mono" value={commands} onChange={(e) => setCommands(e.target.value)} /></Field>
        <Field label="Çalıştırma başına en fazla değişen dosya"><input type="number" min={1} max={1000} value={policy.maxFilesChangedPerRun} style={{ width: 160 }} onChange={(e) => setPolicy({ ...policy, maxFilesChangedPerRun: Number(e.target.value) })} /></Field>
        <div className="row"><span className="spacer" /><button className="primary" disabled={save.busy} onClick={() => save.run(undefined, "Kaydedildi")}>Kaydet</button></div>
      </section>
      <section className="card stack">
        <h2>Erişim kuralları</h2>
        <p className="small muted" style={{ margin: 0 }}>Proje rolünün üzerine repo bazında izin verin ya da engelleyin. Örn. Developer rolündekilerin agent'larının bu repoya yazmasını engelleyin.</p>
        <ErrorBox error={rules.error ?? addRule.error ?? removeRule.error} />
        <table><tbody>
          {(rules.data ?? []).map((r) => (
            <tr key={r.id}><td>{r.userName ?? (r.role ? `${ROLE_LABEL[r.role]} rolü` : "—")}</td><td className="mono small">{r.permission}</td>
              <td><span className={`badge ${r.effect === "Allow" ? "ok" : "bad"}`}>{r.effect === "Allow" ? "izin ver" : "engelle"}</span></td>
              <td className="right"><button className="small ghost danger" onClick={() => removeRule.run(r.id)}>Kaldır</button></td></tr>
          ))}
          <tr>
            <td><select aria-label="Kim" value={rule.subject} onChange={(e) => setRule({ ...rule, subject: e.target.value })}>
              <optgroup label="Rol">{PROJECT_ROLES.map((r) => <option key={r} value={`role:${r}`}>{ROLE_LABEL[r]}</option>)}</optgroup>
              <optgroup label="Kullanıcı">{(users.data?.users ?? []).map((u) => <option key={u.userId} value={`user:${u.userId}`}>{u.displayName}</option>)}</optgroup>
            </select></td>
            <td><select aria-label="İzin" value={rule.permission} onChange={(e) => setRule({ ...rule, permission: e.target.value })}>{repoPermissions.map((x) => <option key={x}>{x}</option>)}</select></td>
            <td><select aria-label="Etki" value={rule.effect} onChange={(e) => setRule({ ...rule, effect: e.target.value as "Allow" | "Deny" })}><option value="Allow">izin ver</option><option value="Deny">engelle</option></select></td>
            <td className="right"><button className="small" disabled={addRule.busy} onClick={() => addRule.run(undefined)}>Ekle</button></td>
          </tr>
        </tbody></table>
      </section>
    </div>
  );
}

// Audit -----------------------------------------------------------------------------------------------

function Audit() {
  const { projects } = useApp();
  const [project, setProject] = useState("");
  const audit = useGet<AuditEntry[]>(["audit", project], `/api/settings/audit${project ? `?project=${project}` : ""}`);
  return (
    <section className="card flush">
      <div className="row" style={{ padding: 16 }}><h2 style={{ margin: 0 }}>Denetim kaydı</h2><span className="spacer" />
        <select aria-label="Proje" value={project} onChange={(e) => setProject(e.target.value)} style={{ width: "auto" }}>
          <option value="">Tüm organizasyon</option>{projects.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}</select></div>
      <ErrorBox error={audit.error} />
      {audit.isLoading ? <Loading /> : (
        <table>
          <thead><tr><th>Zaman</th><th>Kim</th><th>İşlem</th><th>Kayıt</th><th>Ayrıntı</th></tr></thead>
          <tbody>{(audit.data ?? []).map((e, i) => (
            <tr key={i}><td className="small muted">{fmtDate(e.at)}</td><td>{e.actor}</td><td className="mono small">{e.action}</td><td className="small">{e.entityType} <span className="subtle">{e.entityId.slice(0, 8)}</span></td>
              <td className="small muted"><span className="truncate" style={{ display: "block", maxWidth: 420 }}>{e.details}</span></td></tr>
          ))}</tbody>
        </table>
      )}
    </section>
  );
}
