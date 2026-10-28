import { useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, fmtAgo, fmtUsd, PROVIDER_LABEL, RUNTIME_LABEL, type Agent, type AgentKind, type AgentSettings, type AgentSkill, type ModelProvider,
  type ProviderStatusList, type AgentRuntime, type Users } from "../api";
import { useApp } from "../context";
import { Avatar, Check, Empty, ErrorBox, Field, Loading, Modal, PageHead, useAction, useGet } from "../components/ui";
import { Icon } from "../components/icons";

/** Agent definitions in the style of Paperclip: each agent has its own runtime, model, instructions and skills. */
export function Agents() {
  const { isAdmin } = useApp();
  const [params, setParams] = useSearchParams();
  const all = isAdmin && params.get("all") === "1";
  const agents = useGet<Agent[]>(["agents", all ? "all" : "mine"], `/api/agents${all ? "" : "?mine=true"}`, { refetchInterval: 30_000 });
  const [editing, setEditing] = useState<Agent | "new" | null>(null);
  return (
    <>
      <PageHead title="Agent'lar" subtitle="Her developer birden fazla agent çalıştırabilir. Agent'lar Azure'da sahiplerinin yetkileriyle çalışır.">
        <button className="primary" onClick={() => setEditing("new")}><Icon name="plus" /> Yeni agent</button>
      </PageHead>
      {isAdmin ? (
        <div className="tabs">
          <button className={`tab${!all ? " active" : ""}`} onClick={() => setParams({})}>Agent'larım</button>
          <button className={`tab${all ? " active" : ""}`} onClick={() => setParams({ all: "1" })}>Organizasyondaki tüm agent'lar</button>
        </div>
      ) : null}
      <ErrorBox error={agents.error} />
      {agents.isLoading ? <Loading /> : agents.data?.length === 0 ? (
        <Empty>Henüz agent yok. Bir agent oluşturun; size atanan işleri geliştirip pull request açsın.</Empty>
      ) : (
        <div className="grid cards">
          {(agents.data ?? []).map((a) => (
            <div key={a.id} className="card hover agent-card" onClick={() => setEditing(a)}>
              <div className="top">
                <Avatar name={a.name} size="xl" />
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div className="row nowrap"><span className="name truncate">{a.name}</span>{a.isDefault ? <span className="badge info">varsayılan</span> : null}
                    {!a.enabled ? <span className="badge">kapalı</span> : null}</div>
                  <div className="small muted truncate">{a.title || (a.kind === "Developer" ? "Developer agent" : "Kod inceleme agent'ı")}</div>
                  <div className="small muted truncate">{a.ownerName ? `Sahibi: ${a.ownerName}` : "Ortak agent"}{a.projectName ? ` · ${a.projectName}` : ""}</div>
                </div>
              </div>
              <div className="row small">
                <span className="tag">{RUNTIME_LABEL[a.runtime]}</span><span className="tag mono">{a.model}</span>
                <span className="tag">{a.kind === "Developer" ? "Geliştirir" : "İnceler"}</span>
                {a.skills.filter((s) => s.enabled).length ? <span className="tag agent">{a.skills.filter((s) => s.enabled).length} skill</span> : null}
              </div>
              <div className="row small muted">
                {a.activeRuns ? <span className="badge info">{a.activeRuns} çalışıyor</span> : null}
                <span>Bu ay {a.runsThisMonth} çalıştırma · {fmtUsd(a.spentThisMonthUsd)}{a.monthlyBudgetUsd ? ` / ${fmtUsd(a.monthlyBudgetUsd, 0)}` : ""}</span>
                <span className="spacer" />
                <Link to={`/runs?agent=${a.id}`} onClick={(e) => e.stopPropagation()}>{a.lastRunAt ? `son ${fmtAgo(a.lastRunAt)}` : "çalıştırmalar"}</Link>
              </div>
            </div>
          ))}
        </div>
      )}
      {editing ? <AgentEditor agent={editing === "new" ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

const DEFAULT_SETTINGS: AgentSettings = { temperature: null, maxIterations: 40, maxTokensPerRun: 400_000, maxCostPerRunUsd: 5, timeoutMinutes: 45, endpoint: null };
const SKILL_TEMPLATE = "# Skill adı\n\nNe zaman kullanılır:\n- ...\n\nNasıl yapılır:\n1. ...\n";

type Tab = "general" | "runtime" | "instructions" | "skills" | "limits";

function AgentEditor({ agent, onClose }: { agent: Agent | null; onClose: () => void }) {
  const { isAdmin, me, projects } = useApp();
  const providers = useGet<ProviderStatusList>(["providers"], "/api/settings/providers");
  const users = useGet<Users>(["users"], isAdmin ? "/api/settings/users" : null);
  const readOnly = agent !== null && !agent.canEdit;
  const [tab, setTab] = useState<Tab>("general");
  const [form, setForm] = useState(() => ({
    name: agent?.name ?? "", title: agent?.title ?? "", kind: agent?.kind ?? ("Developer" as AgentKind),
    runtime: agent?.runtime ?? (null as AgentRuntime | null), provider: agent?.provider ?? (null as ModelProvider | null), model: agent?.model ?? "",
    instructions: agent?.instructions ?? "", skills: agent?.skills ?? ([] as AgentSkill[]), ownerUserId: agent ? agent.ownerUserId : me.user.id,
    projectId: agent?.projectId ?? null, isDefault: agent?.isDefault ?? false, enabled: agent?.enabled ?? true, maxConcurrentRuns: agent?.maxConcurrentRuns ?? 1,
    monthlyBudgetUsd: agent?.monthlyBudgetUsd ?? null, settings: agent?.settings ?? DEFAULT_SETTINGS,
  }));
  const runtime = form.runtime ?? providers.data?.defaultRuntime ?? "ClaudeCode";
  const provider = form.provider ?? providers.data?.defaultProvider ?? "Anthropic";
  const selected = providers.data?.providers.find((x) => x.runtime === runtime && (x.kind === "cli" || x.provider === provider));
  const model = form.model || selected?.defaultModel || providers.data?.defaultModel || "";
  const keys = [["agents"]];
  const save = useAction(() => api<Agent>(agent ? `/api/agents/${agent.id}` : "/api/agents", {
    method: agent ? "PUT" : "POST",
    body: { ...form, runtime, provider, model, title: form.title || null },
  }), keys);
  const remove = useAction(() => api(`/api/agents/${agent!.id}`, { method: "DELETE" }).then(() => true), keys);
  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm({ ...form, [key]: value });
  const setSettings = <K extends keyof AgentSettings>(key: K, value: AgentSettings[K]) => setForm({ ...form, settings: { ...form.settings, [key]: value } });
  const setSkill = (index: number, skill: AgentSkill) => set("skills", form.skills.map((s, i) => (i === index ? skill : s)));
  const tabs: Array<[Tab, string]> = [["general", "Genel"], ["runtime", "Çalışma ortamı"], ["instructions", "Talimatlar"], ["skills", `Skill'ler (${form.skills.length})`], ["limits", "Limitler"]];

  return (
    <Modal title={<div className="row nowrap"><Avatar name={form.name || "Yeni agent"} size="lg" /><div><h2 style={{ margin: 0 }}>{form.name || "Yeni agent"}</h2>
      <div className="small muted">{agent ? `@${agent.slug}` : "Agent tanımı"}{readOnly ? " · salt okunur" : ""}</div></div></div>} onClose={onClose}>
      <div className="tabs">{tabs.map(([key, label]) => <button key={key} className={`tab${tab === key ? " active" : ""}`} onClick={() => setTab(key)}>{label}</button>)}</div>
      <ErrorBox error={save.error ?? remove.error ?? providers.error} />
      <fieldset disabled={readOnly} style={{ border: 0, padding: 0, margin: 0 }} className="stack">
        {tab === "general" ? <>
          <div className="form-2">
            <Field label="Ad"><input value={form.name} onChange={(e) => set("name", e.target.value)} placeholder="Örn. Backend geliştirici" /></Field>
            <Field label="Unvan" hint="Kartta görünen kısa açıklama"><input value={form.title} onChange={(e) => set("title", e.target.value)} placeholder="Örn. .NET API uzmanı" /></Field>
          </div>
          <Field label="Görev">
            <select value={form.kind} onChange={(e) => set("kind", e.target.value as AgentKind)}>
              <option value="Developer">Developer: work item'ları geliştirir, branch ve pull request açar</option>
              <option value="Reviewer">Reviewer: pull request'leri inceler, yorum ve öneri bırakır</option>
            </select>
          </Field>
          <div className="form-2">
            {isAdmin ? (
              <Field label="Sahibi" hint="Agent Azure'da sahibinin yetkileriyle çalışır">
                <select value={form.ownerUserId ?? ""} onChange={(e) => set("ownerUserId", e.target.value || null)}>
                  <option value="">Ortak (servis kimliği)</option>
                  {(users.data?.users ?? [{ userId: me.user.id, displayName: me.user.displayName }]).map((u) => <option key={u.userId} value={u.userId}>{u.displayName}</option>)}
                </select>
              </Field>
            ) : null}
            <Field label="Proje" hint="Boş bırakırsanız tüm projelerde çalışabilir">
              <select value={form.projectId ?? ""} onChange={(e) => set("projectId", e.target.value || null)}>
                <option value="">Tüm projeler</option>{projects.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
              </select>
            </Field>
          </div>
          <Check label="Varsayılan agent'ım" hint="Size atanan işler (agent:<slug> etiketi yoksa) otomatik olarak bu agent'a verilir" checked={form.isDefault} onChange={(v) => set("isDefault", v)} />
          <Check label="Etkin" checked={form.enabled} onChange={(v) => set("enabled", v)} />
          {agent ? <div className="callout small">Bir maddeyi bu agent'a vermek için Azure Boards'ta <span className="mono">agent:{agent.slug}</span> etiketini ekleyin.</div> : null}
        </> : null}

        {tab === "runtime" ? <>
          {providers.isLoading ? <Loading /> : null}
          <div className="runtime-cards">
            {(providers.data?.providers ?? []).map((x) => {
              const isSelected = x.runtime === runtime && (x.kind === "cli" || x.provider === provider);
              const usable = x.configured && x.installed !== false;
              return (
                <button type="button" key={x.key} className={`runtime-card${isSelected ? " selected" : ""}`}
                  onClick={() => setForm({ ...form, runtime: x.runtime, provider: x.provider, model: x.defaultModel })}>
                  <div className="row nowrap"><span className="title">{x.name}</span><span className="spacer" />
                    <span className={`badge ${usable ? "ok" : "warn"}`}>{usable ? "hazır" : x.installed === false ? "kurulu değil" : "anahtar yok"}</span></div>
                  <div className="small muted" style={{ marginTop: 4 }}>{x.description}</div>
                  {x.version ? <div className="small subtle mono">{x.version}</div> : null}
                </button>
              );
            })}
          </div>
          {selected && !(selected.configured && selected.installed !== false) ? (
            <div className="callout small">{selected.name} henüz kullanılamıyor. {isAdmin ? <Link to="/settings/ai">Ayarlar → AI sağlayıcıları</Link> : "Yöneticinizden"} anahtarı tanımlamasını isteyin.</div>
          ) : null}
          <div className="form-2">
            <Field label="Model" hint={runtime === "CursorCli" ? "Cursor model adı (örn. sonnet-4.5, gpt-5, auto)" : `Örn. ${selected?.defaultModel ?? "claude-sonnet-4-5"}`}>
              <input className="mono" value={model} onChange={(e) => set("model", e.target.value)} />
            </Field>
            <Field label="Sağlayıcı"><input value={`${RUNTIME_LABEL[runtime]} · ${PROVIDER_LABEL[provider]}`} disabled /></Field>
          </div>
          {provider === "AzureOpenAI" ? (
            <Field label="Azure OpenAI endpoint" hint="Boşsa Ayarlar'daki organizasyon endpoint'i kullanılır">
              <input value={form.settings.endpoint ?? ""} onChange={(e) => setSettings("endpoint", e.target.value || null)} placeholder="https://<kaynak>.openai.azure.com" />
            </Field>
          ) : null}
        </> : null}

        {tab === "instructions" ? (
          <Field label="Talimatlar (system prompt)" hint="Agent'ın kim olduğu, kodlama kuralları, test beklentileri. Her çalıştırmada work item ve repo bilgisiyle birlikte verilir.">
            <textarea className="code" style={{ minHeight: 360 }} value={form.instructions} onChange={(e) => set("instructions", e.target.value)}
              placeholder={"Sen deneyimli bir .NET geliştiricisisin.\n- Clean Architecture katmanlarına uy.\n- Her değişiklik için birim testi yaz.\n- PR açıklamasını Türkçe yaz."} />
          </Field>
        ) : null}

        {tab === "skills" ? <>
          <p className="small muted" style={{ margin: 0 }}>Skill'ler bu agent'a özel Markdown talimatlarıdır (örn. "EF Core migration oluşturma", "API endpoint ekleme"). Etkin skill'ler her çalıştırmada agent'a verilir.</p>
          {form.skills.map((s, i) => (
            <div className="skill" key={i}>
              <div className="skill-head">
                <input aria-label="Skill adı" value={s.name} onChange={(e) => setSkill(i, { ...s, name: e.target.value })} placeholder="skill-adi" />
                <label className="check small"><input type="checkbox" checked={s.enabled} onChange={(e) => setSkill(i, { ...s, enabled: e.target.checked })} /> etkin</label>
                <button type="button" className="small ghost danger" onClick={() => set("skills", form.skills.filter((_, j) => j !== i))}>Sil</button>
              </div>
              <textarea aria-label={`${s.name} içeriği`} value={s.content} onChange={(e) => setSkill(i, { ...s, content: e.target.value })} />
            </div>
          ))}
          <div><button type="button" onClick={() => set("skills", [...form.skills, { name: `skill-${form.skills.length + 1}`, content: SKILL_TEMPLATE, enabled: true }])}>
            <Icon name="plus" /> Skill ekle</button></div>
        </> : null}

        {tab === "limits" ? (
          <div className="form-3">
            <Field label="Aynı anda en fazla çalıştırma"><input type="number" min={1} max={10} value={form.maxConcurrentRuns} onChange={(e) => set("maxConcurrentRuns", Number(e.target.value))} /></Field>
            <Field label="Aylık bütçe (USD)" hint="Boş: limit yok"><input type="number" min={0} step="1" value={form.monthlyBudgetUsd ?? ""} onChange={(e) => set("monthlyBudgetUsd", e.target.value ? Number(e.target.value) : null)} /></Field>
            <Field label="Çalıştırma başına en fazla maliyet (USD)"><input type="number" min={0.1} step="0.5" value={form.settings.maxCostPerRunUsd} onChange={(e) => setSettings("maxCostPerRunUsd", Number(e.target.value))} /></Field>
            <Field label="En fazla adım"><input type="number" min={1} max={200} value={form.settings.maxIterations} onChange={(e) => setSettings("maxIterations", Number(e.target.value))} /></Field>
            <Field label="En fazla token"><input type="number" min={10000} step={10000} value={form.settings.maxTokensPerRun} onChange={(e) => setSettings("maxTokensPerRun", Number(e.target.value))} /></Field>
            <Field label="Zaman aşımı (dakika)"><input type="number" min={1} max={240} value={form.settings.timeoutMinutes} onChange={(e) => setSettings("timeoutMinutes", Number(e.target.value))} /></Field>
            <Field label="Temperature" hint="Boş: model varsayılanı"><input type="number" min={0} max={2} step={0.1} value={form.settings.temperature ?? ""} onChange={(e) => setSettings("temperature", e.target.value ? Number(e.target.value) : null)} /></Field>
          </div>
        ) : null}
      </fieldset>
      <div className="row" style={{ marginTop: 20, paddingTop: 12, borderTop: "1px solid var(--border)" }}>
        {agent?.canEdit ? <button className="danger" disabled={remove.busy} onClick={async () => { if (confirm(`${agent.name} silinsin mi?`) && await remove.run(undefined)) onClose(); }}>Sil</button> : null}
        <span className="spacer" />
        <button onClick={onClose}>Kapat</button>
        {!readOnly ? <button className="primary" disabled={save.busy || !form.name.trim() || !model} onClick={async () => { if (await save.run(undefined)) onClose(); }}>{agent ? "Kaydet" : "Oluştur"}</button> : null}
      </div>
    </Modal>
  );
}
