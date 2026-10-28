import { useState } from "react";
import { api, fmtDate, p, ROLE_LABEL, type Project, type ProjectRole, type Secret, type Team, type WorkItemType } from "../api";
import { ErrorBox, Field, FieldGroup, Notice, useAction, useGet } from "../components/ui";
import type { ProjectPageProps } from "./ProjectShell";

const KNOWN_SECRETS = [
  { name: "AZURE_DEVOPS_PAT", label: "Azure DevOps personal access token (servis kimliği)" },
  { name: "AZURE_DEVOPS_SP_TENANT_ID", label: "Service principal: tenant id (üçü birlikte PAT'in yerine geçer)" },
  { name: "AZURE_DEVOPS_SP_CLIENT_ID", label: "Service principal: client id" },
  { name: "AZURE_DEVOPS_SP_CLIENT_SECRET", label: "Service principal: client secret" },
  { name: "AZURE_SERVICE_HOOK_PASSWORD", label: "Service Hook parolası" },
  { name: "ANTHROPIC_API_KEY", label: "Anthropic (Claude; Claude Code CLI de kullanır)" },
  { name: "CLAUDE_CODE_OAUTH_TOKEN", label: "Claude Code abonelik token'ı (claude setup-token; varsa API anahtarının yerine)" },
  { name: "CURSOR_API_KEY", label: "Cursor CLI API anahtarı" },
  { name: "OPENAI_API_KEY", label: "OpenAI" },
  { name: "AZURE_OPENAI_API_KEY", label: "Azure OpenAI" },
  { name: "GEMINI_API_KEY", label: "Google Gemini" },
];

export function ProjectSettingsPage({ project, can }: ProjectPageProps) {
  return (
    <div className="stack">
      <AzureConnection project={project} />
      <GeneralSettings project={project} canBudget={can("budgets.manage")} />
      {can("project.secrets.manage") ? <Secrets project={project} /> : null}
    </div>
  );
}

function AzureConnection({ project }: { project: Project }) {
  const [checks, setChecks] = useState<Array<{ name: string; ok: boolean; detail: string }> | null>(null);
  const test = useAction(() => api<Array<{ name: string; ok: boolean; detail: string }>>(p(project.id, "/azure/test"), { body: {} }));
  const sync = useAction(() => api<Record<string, number>>(p(project.id, "/azure/sync"), { body: {} }), [["project", project.id], ["work-items", project.id], ["repos", project.id], ["teams", project.id], ["people", project.id], ["members", project.id]]);
  const hookUrl = `${location.origin}/api/hooks/${project.id}`;
  return (
    <section className="card stack">
      <h2>Azure DevOps bağlantısı</h2>
      <ErrorBox error={test.error ?? sync.error} /><Notice text={sync.message} />
      <div className="row">
        <button disabled={test.busy} onClick={async () => setChecks((await test.run(undefined)) ?? null)}>Bağlantıyı test et</button>
        <button className="primary" disabled={sync.busy} onClick={() => sync.run(undefined, (r) => `Senkron tamamlandı: ${r.teams} takım, ${r.repositories} repo, ${r.workItems} work item, ${r.commits} commit, ${r.pullRequests} PR, ${r.queuedRuns} yeni iş; üyelik +${r.membersAdded}/−${r.membersRemoved}, ${r.invited} davet.`)}>
          {sync.busy ? "Senkronlanıyor…" : "Şimdi senkronla"}
        </button>
        <span className="small muted">Son senkron: {fmtDate(project.lastSyncedAt)}</span>
      </div>
      {project.lastSyncError ? <div className="error">Son senkron hatası: {project.lastSyncError}</div> : null}
      {checks ? <table><tbody>{checks.map((c) => (
        <tr key={c.name}><td><span className={`badge ${c.ok ? "ok" : "bad"}`}>{c.ok ? "OK" : "Hata"}</span></td><td><strong>{c.name}</strong></td><td className="muted">{c.detail}</td></tr>
      ))}</tbody></table> : null}
      <div className="small muted">
        Anlık güncelleme için Azure'da Project settings → Service hooks → Web Hooks: <code>{hookUrl}</code> — Basic auth, kullanıcı adı herhangi,
        parola <code>AZURE_SERVICE_HOOK_PASSWORD</code> secret'ı. Olaylar: Work item created/updated, Code pushed, Pull request created/updated.
      </div>
    </section>
  );
}

function GeneralSettings({ project, canBudget }: { project: Project; canBudget: boolean }) {
  const [form, setForm] = useState({
    name: project.name, description: project.description ?? "", azureOrganizationUrl: project.azureOrganizationUrl, azureProjectName: project.azureProjectName,
    monthlyBudgetUsd: project.monthlyBudgetUsd?.toString() ?? "", settings: { ...project.settings, closedStates: [...project.settings.closedStates] },
  });
  const save = useAction(() => api(p(project.id), { method: "PUT", body: { ...form, monthlyBudgetUsd: form.monthlyBudgetUsd === "" ? null : Number(form.monthlyBudgetUsd) } }),
    [["project", project.id], ["projects"]]);
  const s = form.settings;
  const setS = (patch: Partial<typeof s>) => setForm({ ...form, settings: { ...s, ...patch } });
  const teams = useGet<Team[]>(["teams", project.id], p(project.id, "/teams"));
  const types = useGet<WorkItemType[]>(["work-item-types", project.id], p(project.id, "/work-item-types"));
  const states = [...new Set((types.data ?? []).flatMap((t) => t.states))];
  const roles = Object.keys(ROLE_LABEL) as ProjectRole[];
  const [group, setGroup] = useState<{ groupId: string; role: ProjectRole }>({ groupId: "", role: "Developer" });
  const teamRole = (team: string) => s.teamRoles.find((m) => m.team.toLowerCase() === team.toLowerCase())?.role ?? "";
  const setTeamRole = (team: string, role: ProjectRole | "") =>
    setS({ teamRoles: [...s.teamRoles.filter((m) => m.team.toLowerCase() !== team.toLowerCase()), ...(role ? [{ team, role }] : [])] });
  return (
    <section className="card form">
      <h2>Proje ayarları</h2>
      <ErrorBox error={save.error} /><Notice text={save.message} />
      <div className="form-2">
        <Field label="Ad"><input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} /></Field>
        <Field label="Aylık bütçe (USD)" hint={canBudget ? "Boş = limitsiz" : "Yetkiniz yok"}><input type="number" min={0} disabled={!canBudget} value={form.monthlyBudgetUsd} onChange={(e) => setForm({ ...form, monthlyBudgetUsd: e.target.value })} /></Field>
        <Field label="Azure organizasyon URL'i"><input value={form.azureOrganizationUrl} onChange={(e) => setForm({ ...form, azureOrganizationUrl: e.target.value })} /></Field>
        <Field label="Azure proje adı"><input value={form.azureProjectName} onChange={(e) => setForm({ ...form, azureProjectName: e.target.value })} /></Field>
      </div>
      <Field label="Açıklama"><input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} /></Field>
      <h3>Otomasyon</h3>
      <label className="check"><input type="checkbox" checked={s.autoDispatch} onChange={(e) => setS({ autoDispatch: e.target.checked })} /> Atanan maddeleri developer'ın agent'ına otomatik ver</label>
      <label className="check"><input type="checkbox" checked={s.requireRunApproval} onChange={(e) => setS({ requireRunApproval: e.target.checked })} /> Her iş başlamadan önce insan onayı iste</label>
      <label className="check"><input type="checkbox" checked={s.autoReviewPullRequests} onChange={(e) => setS({ autoReviewPullRequests: e.target.checked })} /> Yeni PR'ları reviewer agent ile otomatik incele</label>
      <div className="form-2">
        <Field label="PR açılınca work item durumu" hint={states.length ? `Bu projedeki durumlar: ${states.join(", ")} · boş = değiştirme` : "Boş = değiştirme"}>
          <input list="wi-states" value={s.reviewState ?? ""} onChange={(e) => setS({ reviewState: e.target.value || null })} />
          <datalist id="wi-states">{states.map((x) => <option key={x} value={x} />)}</datalist>
        </Field>
        <Field label="Senkron aralığı (dakika)"><input type="number" min={1} max={1440} value={s.syncIntervalMinutes} onChange={(e) => setS({ syncIntervalMinutes: Number(e.target.value) })} /></Field>
      </div>
      <Field label="Kapalı durumlar" hint="Bu durumlardaki maddeler agent'a verilmez (virgülle)">
        <input value={s.closedStates.join(", ")} onChange={(e) => setS({ closedStates: e.target.value.split(",").map((x) => x.trim()).filter(Boolean) })} />
      </Field>
      <h3>Kişiler ve kimlik</h3>
      <label className="check"><input type="checkbox" checked={s.autoProvisionMembers} onChange={(e) => setS({ autoProvisionMembers: e.target.checked })} /> Azure takım üyelerini her senkronda projeye ekle/çıkar (hesabı olanlar)</label>
      <label className="check"><input type="checkbox" checked={s.autoInviteTeamMembers} onChange={(e) => setS({ autoInviteTeamMembers: e.target.checked })} /> Hesabı olmayan takım üyelerine otomatik davet oluştur (SMTP varsa e-posta gönderilir)</label>
      <label className="check"><input type="checkbox" checked={s.requireUserCredentialForWrites} onChange={(e) => setS({ requireUserCredentialForWrites: e.target.checked })} /> Atama ve yeni madde için kişinin kendi Azure bağlantısını zorunlu kıl (servis kimliğine düşme)</label>
      <FieldGroup label="Takım → proje rolü" hint="Hiç eşleme yoksa tüm takımlar Developer olur. Eşleme varsa yalnızca eşlenen takımlar eklenir; birden fazla takımdaki kişi en yüksek rolü alır.">
        <table><tbody>{teams.data?.map((t) => (
          <tr key={t.id}><td>{t.name}<div className="small muted">{t.members.length} kişi · {t.areaPaths.join(", ") || "alan yok"}</div></td>
            <td><select aria-label={`${t.name} rolü`} value={teamRole(t.name)} onChange={(e) => setTeamRole(t.name, e.target.value as ProjectRole | "")} style={{ maxWidth: 220 }}>
              <option value="">{s.teamRoles.length ? "Eklenmez" : "Developer (varsayılan)"}</option>
              {roles.map((r) => <option key={r} value={r}>{ROLE_LABEL[r]}</option>)}
            </select></td></tr>
        ))}</tbody></table>
      </FieldGroup>
      <FieldGroup label="Entra ID grubu → proje rolü" hint="Microsoft ile girişte gelen grup üyeliğine göre (grup object id).">
        <table><tbody>{s.groupRoles.map((g) => (
          <tr key={g.groupId}><td><code>{g.groupId}</code></td><td>{ROLE_LABEL[g.role]}</td>
            <td className="right"><button className="danger" onClick={() => setS({ groupRoles: s.groupRoles.filter((x) => x.groupId !== g.groupId) })}>Sil</button></td></tr>
        ))}</tbody></table>
        <div className="row">
          <input placeholder="Grup object id" value={group.groupId} onChange={(e) => setGroup({ ...group, groupId: e.target.value.trim() })} style={{ maxWidth: 340 }} />
          <select value={group.role} onChange={(e) => setGroup({ ...group, role: e.target.value as ProjectRole })} style={{ maxWidth: 200 }}>{roles.map((r) => <option key={r} value={r}>{ROLE_LABEL[r]}</option>)}</select>
          <button disabled={!group.groupId} onClick={() => { setS({ groupRoles: [...s.groupRoles.filter((x) => x.groupId !== group.groupId), group] }); setGroup({ groupId: "", role: "Developer" }); }}>Ekle</button>
        </div>
      </FieldGroup>
      <div className="row"><span className="spacer" /><button className="primary" disabled={save.busy} onClick={() => save.run(undefined, "Ayarlar kaydedildi.")}>Kaydet</button></div>
    </section>
  );
}

function Secrets({ project }: { project: Project }) {
  const secrets = useGet<Secret[]>(["secrets", project.id], p(project.id, "/secrets"));
  const [draft, setDraft] = useState({ name: "", value: "" });
  const keys = [["secrets", project.id]];
  const set = useAction(() => api(p(project.id, `/secrets/${draft.name}`), { method: "PUT", body: { value: draft.value } }), keys);
  const remove = useAction((name: string) => api(p(project.id, `/secrets/${name}`), { method: "DELETE" }), keys);
  const has = (name: string) => secrets.data?.find((s) => s.name === name);
  const custom = secrets.data?.filter((s) => !KNOWN_SECRETS.some((k) => k.name === s.name)) ?? [];
  return (
    <section className="card stack">
      <h2>Secret'lar</h2>
      <p className="muted small">AES-256-GCM ile şifreli saklanır ve hiçbir zaman geri gösterilmez. MCP sunucuları da buradaki secret'ları kullanır.</p>
      <ErrorBox error={secrets.error ?? set.error ?? remove.error} /><Notice text={set.message} />
      <table><tbody>
        {[...KNOWN_SECRETS, ...custom.map((c) => ({ name: c.name, label: c.name }))].map((k) => (
          <tr key={k.name}>
            <td>{k.label}<div className="small muted"><code>{k.name}</code></div></td>
            <td>{has(k.name) ? <span className="badge ok">Kayıtlı · {fmtDate(has(k.name)!.updatedAt)}</span> : <span className="badge">Yok</span>}{has(k.name)?.projectId === null ? <span className="small muted"> (organizasyon)</span> : null}</td>
            <td className="right"><div className="row" style={{ justifyContent: "flex-end" }}>
              <button onClick={() => setDraft({ name: k.name, value: "" })}>Ayarla</button>
              {has(k.name)?.projectId ? <button className="danger" onClick={() => remove.run(k.name)}>Sil</button> : null}
            </div></td>
          </tr>
        ))}
      </tbody></table>
      <div className="row">
        <input placeholder="AD (ör. SONAR_TOKEN)" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value.toUpperCase() })} style={{ maxWidth: 260 }} />
        <input type="password" placeholder="Değer" value={draft.value} onChange={(e) => setDraft({ ...draft, value: e.target.value })} style={{ maxWidth: 320 }} />
        <button className="primary" disabled={!draft.name || !draft.value || set.busy} onClick={async () => { await set.run(undefined, `${draft.name} kaydedildi.`); setDraft({ name: "", value: "" }); }}>Kaydet</button>
      </div>
    </section>
  );
}
