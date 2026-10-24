import { useState } from "react";
import { api, fmtDate, p, type Project, type Secret } from "../api";
import { ErrorBox, Field, Notice, useAction, useGet } from "../components/ui";
import type { ProjectPageProps } from "./ProjectShell";

const KNOWN_SECRETS = [
  { name: "AZURE_DEVOPS_PAT", label: "Azure DevOps personal access token" },
  { name: "AZURE_SERVICE_HOOK_PASSWORD", label: "Service Hook parolası" },
  { name: "ANTHROPIC_API_KEY", label: "Anthropic (Claude)" },
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
  const sync = useAction(() => api<Record<string, number>>(p(project.id, "/azure/sync"), { body: {} }), [["project", project.id], ["work-items", project.id], ["repos", project.id], ["teams", project.id]]);
  const hookUrl = `${location.origin}/api/hooks/${project.id}`;
  return (
    <section className="card stack">
      <h2>Azure DevOps bağlantısı</h2>
      <ErrorBox error={test.error ?? sync.error} /><Notice text={sync.message} />
      <div className="row">
        <button disabled={test.busy} onClick={async () => setChecks((await test.run(undefined)) ?? null)}>Bağlantıyı test et</button>
        <button className="primary" disabled={sync.busy} onClick={() => sync.run(undefined, (r) => `Senkron tamamlandı: ${r.teams} takım, ${r.repositories} repo, ${r.workItems} work item, ${r.commits} commit, ${r.pullRequests} PR, ${r.queuedRuns} yeni iş.`)}>
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
        <Field label="PR açılınca work item durumu" hint="Boş = değiştirme"><input value={s.reviewState ?? ""} onChange={(e) => setS({ reviewState: e.target.value || null })} /></Field>
        <Field label="Senkron aralığı (dakika)"><input type="number" min={1} max={1440} value={s.syncIntervalMinutes} onChange={(e) => setS({ syncIntervalMinutes: Number(e.target.value) })} /></Field>
      </div>
      <Field label="Kapalı durumlar" hint="Bu durumlardaki maddeler agent'a verilmez (virgülle)">
        <input value={s.closedStates.join(", ")} onChange={(e) => setS({ closedStates: e.target.value.split(",").map((x) => x.trim()).filter(Boolean) })} />
      </Field>
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
