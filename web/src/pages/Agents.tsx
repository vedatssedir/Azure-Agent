import { useState } from "react";
import { api, fmtUsd, p, type Agent, type AgentKind, type McpServer, type ModelProvider, type Skill, type Team } from "../api";
import { Drawer, ErrorBox, Field, Notice, useAction, useGet } from "../components/ui";
import type { ProjectPageProps } from "./ProjectShell";

export const PROVIDERS: Record<ModelProvider, { label: string; key: string; example: string }> = {
  Anthropic: { label: "Anthropic (Claude)", key: "ANTHROPIC_API_KEY", example: "claude-sonnet-4-5" },
  OpenAI: { label: "OpenAI", key: "OPENAI_API_KEY", example: "gpt-5" },
  AzureOpenAI: { label: "Azure OpenAI", key: "AZURE_OPENAI_API_KEY", example: "gpt-5 (deployment adı)" },
  Gemini: { label: "Google Gemini", key: "GEMINI_API_KEY", example: "gemini-2.5-pro" },
};

type Draft = Omit<Agent, "id" | "skills" | "mcpServers" | "activeRuns" | "spentThisMonthUsd"> & { id?: string };

const EMPTY: Draft = {
  name: "", kind: "Developer", provider: "Anthropic", model: "claude-sonnet-4-5", instructions: "", pairedUniqueName: null, teamId: null,
  enabled: true, maxConcurrentRuns: 1, monthlyBudgetUsd: null,
  settings: { temperature: null, maxIterations: 40, maxTokensPerRun: 400_000, maxCostPerRunUsd: 5, timeoutMinutes: 45, endpoint: null },
};

export function Agents({ project, can }: ProjectPageProps) {
  const agents = useGet<Agent[]>(["agents", project.id], p(project.id, "/agents"), { refetchInterval: 15_000 });
  const teams = useGet<Team[]>(["teams", project.id], p(project.id, "/teams"));
  const [editing, setEditing] = useState<Draft | null>(null);
  const [capabilities, setCapabilities] = useState<Agent | null>(null);
  const [provision, setProvision] = useState<Team | null>(null);
  const manage = can("agents.manage");
  const toggle = useAction((agent: Agent) => api(p(project.id, `/agents/${agent.id}`), { method: "PUT", body: { ...agent, enabled: !agent.enabled } }), [["agents", project.id]]);

  return (
    <div className="stack">
      <section className="card">
        <div className="row"><h2 style={{ margin: 0 }}>Takımlar</h2><span className="spacer" />{manage ? <button className="primary" onClick={() => setEditing({ ...EMPTY })}>Yeni agent</button> : null}</div>
        <p className="muted small">Her developer'ın bir agent'ı olur. Developer Azure'da bir maddeyi kendine atadığında o maddeyi kendi agent'ı çalışır.</p>
        <ErrorBox error={teams.error ?? toggle.error} />
        {teams.data?.length === 0 ? <div className="muted">Takım yok. Önce Azure senkronunu çalıştırın.</div> : null}
        {teams.data?.map((team) => (
          <div key={team.id} className="stack" style={{ marginBottom: 12 }}>
            <div className="row">
              <strong>{team.name}</strong>
              <span className="muted small">{team.members.length} üye · {team.members.filter((m) => m.agentId).length} agent</span>
              <span className="spacer" />
              {manage && team.members.some((m) => !m.agentId) ? <button onClick={() => setProvision(team)}>Eksik agent'ları oluştur</button> : null}
            </div>
            <div className="row">{team.members.map((m) => (
              <span key={m.uniqueName} className={`badge ${m.agentId ? "ok" : ""}`} title={m.uniqueName}>{m.displayName}{m.agentName ? ` → ${m.agentName}` : " · agent yok"}</span>
            ))}</div>
          </div>
        ))}
      </section>

      <section className="card">
        <h2>Agent'lar</h2>
        <ErrorBox error={agents.error} />
        <table>
          <thead><tr><th>Agent</th><th>Model</th><th>Developer</th><th>Skill / MCP</th><th className="right">Bu ay</th><th>Durum</th><th /></tr></thead>
          <tbody>{agents.data?.map((a) => (
            <tr key={a.id}>
              <td><strong>{a.name}</strong><div className="small muted">{a.kind === "Reviewer" ? "Code reviewer" : "Developer agent"}</div></td>
              <td>{PROVIDERS[a.provider].label}<div className="small muted">{a.model}</div></td>
              <td>{a.pairedUniqueName ?? <span className="muted">—</span>}</td>
              <td><div className="row">{a.skills.map((s) => <span key={s} className="badge">{s}</span>)}{a.mcpServers.map((m) => <span key={m} className="badge info">{m}</span>)}</div></td>
              <td className="right">{fmtUsd(a.spentThisMonthUsd)}<div className="small muted">limit {fmtUsd(a.monthlyBudgetUsd)}</div></td>
              <td><span className={`badge ${a.enabled ? "ok" : ""}`}>{a.enabled ? "Aktif" : "Durdu"}</span>{a.activeRuns ? <div className="small muted">{a.activeRuns} aktif iş</div> : null}</td>
              <td>{manage ? <div className="row">
                <button onClick={() => setEditing({ ...a })}>Düzenle</button>
                <button onClick={() => setCapabilities(a)}>Skill/MCP</button>
                <button onClick={() => toggle.run(a)}>{a.enabled ? "Durdur" : "Başlat"}</button>
              </div> : null}</td>
            </tr>
          ))}</tbody>
        </table>
      </section>

      {editing ? <AgentEditor projectId={project.id} draft={editing} teams={teams.data ?? []} onClose={() => setEditing(null)} /> : null}
      {capabilities ? <CapabilitiesEditor projectId={project.id} agent={capabilities} onClose={() => setCapabilities(null)} /> : null}
      {provision ? <ProvisionTeam projectId={project.id} team={provision} onClose={() => setProvision(null)} /> : null}
    </div>
  );
}

function AgentEditor({ projectId, draft: initial, teams, onClose }: { projectId: string; draft: Draft; teams: Team[]; onClose: () => void }) {
  const [draft, setDraft] = useState<Draft>(initial);
  const keys = [["agents", projectId], ["teams", projectId]];
  const save = useAction(() => {
    const { id, ...body } = draft;
    return api(p(projectId, id ? `/agents/${id}` : "/agents"), { method: id ? "PUT" : "POST", body: { ...body, pairedUniqueName: body.pairedUniqueName || null } });
  }, keys);
  const remove = useAction(() => api(p(projectId, `/agents/${draft.id}`), { method: "DELETE" }), keys);
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft({ ...draft, [key]: value });
  const setting = <K extends keyof Draft["settings"]>(key: K, value: Draft["settings"][K]) => setDraft({ ...draft, settings: { ...draft.settings, [key]: value } });
  return (
    <Drawer title={draft.id ? "Agent'ı düzenle" : "Yeni agent"} onClose={onClose}>
      <ErrorBox error={save.error ?? remove.error} />
      <div className="form">
        <div className="form-2">
          <Field label="Ad"><input value={draft.name} onChange={(e) => set("name", e.target.value)} /></Field>
          <Field label="Tür">
            <select value={draft.kind} onChange={(e) => set("kind", e.target.value as AgentKind)}>
              <option value="Developer">Developer (iş yapar, PR açar)</option><option value="Reviewer">Reviewer (PR inceler)</option>
            </select>
          </Field>
          <Field label="Sağlayıcı" hint={`Anahtar: ${PROVIDERS[draft.provider].key} (Ayarlar → Secret'lar)`}>
            <select value={draft.provider} onChange={(e) => set("provider", e.target.value as ModelProvider)}>
              {Object.entries(PROVIDERS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
            </select>
          </Field>
          <Field label="Model" hint={`Örn. ${PROVIDERS[draft.provider].example}`}><input value={draft.model} onChange={(e) => set("model", e.target.value)} /></Field>
        </div>
        {draft.provider === "AzureOpenAI" ? <Field label="Azure OpenAI endpoint" hint="https://<kaynak>.openai.azure.com"><input value={draft.settings.endpoint ?? ""} onChange={(e) => setting("endpoint", e.target.value || null)} /></Field> : null}
        {draft.kind === "Developer" ? (
          <div className="form-2">
            <Field label="Developer (Azure kullanıcı adı)" hint="Bu kişiye atanan maddeleri bu agent çalışır">
              <input type="email" value={draft.pairedUniqueName ?? ""} onChange={(e) => set("pairedUniqueName", e.target.value)} />
            </Field>
            <Field label="Takım">
              <select value={draft.teamId ?? ""} onChange={(e) => set("teamId", e.target.value || null)}>
                <option value="">—</option>{teams.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
              </select>
            </Field>
          </div>
        ) : null}
        <Field label="Talimatlar" hint="Her işte agent'a sistem talimatı olarak verilir">
          <textarea value={draft.instructions} onChange={(e) => set("instructions", e.target.value)} />
        </Field>
        <h3>Limitler ve maliyet</h3>
        <div className="form-2">
          <Field label="Aylık bütçe (USD)" hint="Boş = limitsiz"><input type="number" min={0} step="0.01" value={draft.monthlyBudgetUsd ?? ""} onChange={(e) => set("monthlyBudgetUsd", e.target.value === "" ? null : Number(e.target.value))} /></Field>
          <Field label="İş başına en fazla maliyet (USD)"><input type="number" min={0.01} step="0.01" value={draft.settings.maxCostPerRunUsd} onChange={(e) => setting("maxCostPerRunUsd", Number(e.target.value))} /></Field>
          <Field label="İş başına en fazla token"><input type="number" min={1000} value={draft.settings.maxTokensPerRun} onChange={(e) => setting("maxTokensPerRun", Number(e.target.value))} /></Field>
          <Field label="En fazla model/araç turu"><input type="number" min={1} max={200} value={draft.settings.maxIterations} onChange={(e) => setting("maxIterations", Number(e.target.value))} /></Field>
          <Field label="Zaman aşımı (dakika)"><input type="number" min={1} max={240} value={draft.settings.timeoutMinutes} onChange={(e) => setting("timeoutMinutes", Number(e.target.value))} /></Field>
          <Field label="Eşzamanlı iş limiti"><input type="number" min={1} max={10} value={draft.maxConcurrentRuns} onChange={(e) => set("maxConcurrentRuns", Number(e.target.value))} /></Field>
          <Field label="Temperature" hint="Boş = model varsayılanı"><input type="number" min={0} max={2} step="0.1" value={draft.settings.temperature ?? ""} onChange={(e) => setting("temperature", e.target.value === "" ? null : Number(e.target.value))} /></Field>
        </div>
        <label className="check"><input type="checkbox" checked={draft.enabled} onChange={(e) => set("enabled", e.target.checked)} /> Aktif</label>
        <div className="row">
          <button onClick={onClose}>Vazgeç</button><span className="spacer" />
          {draft.id ? <button className="danger" onClick={async () => { if (await remove.run(undefined) !== undefined) onClose(); }}>Sil</button> : null}
          <button className="primary" disabled={save.busy || !draft.name || !draft.model} onClick={async () => { if (await save.run(undefined) !== undefined) onClose(); }}>Kaydet</button>
        </div>
      </div>
    </Drawer>
  );
}

function CapabilitiesEditor({ projectId, agent, onClose }: { projectId: string; agent: Agent; onClose: () => void }) {
  const skills = useGet<Skill[]>(["skills"], "/api/skills");
  const mcp = useGet<McpServer[]>(["mcp"], "/api/mcp-servers");
  const [selectedSkills, setSkills] = useState(agent.skills);
  const [selectedMcp, setMcp] = useState(agent.mcpServers);
  const toggle = (list: string[], value: string) => (list.includes(value) ? list.filter((v) => v !== value) : [...list, value]);
  const save = useAction(() => api(p(projectId, `/agents/${agent.id}/capabilities`), { method: "PUT", body: { skillKeys: selectedSkills, mcpServerNames: selectedMcp } }), [["agents", projectId]]);
  return (
    <Drawer title={`${agent.name}: skill ve MCP`} onClose={onClose}>
      <ErrorBox error={save.error} />
      <p className="muted small">Skill ve MCP sunucuları organizasyon düzeyinde tanımlanır (Organizasyon → Skill & MCP).</p>
      <div className="grid">
        <section className="card stack"><h3>Skill'ler</h3>{skills.data?.map((s) => (
          <label key={s.key} className="check"><input type="checkbox" checked={selectedSkills.includes(s.key)} onChange={() => setSkills(toggle(selectedSkills, s.key))} />{s.name} <span className="muted small">{s.key}</span></label>
        ))}{skills.data?.length === 0 ? <span className="muted">Tanımlı skill yok.</span> : null}</section>
        <section className="card stack"><h3>MCP sunucuları</h3>{mcp.data?.map((m) => (
          <label key={m.name} className="check"><input type="checkbox" checked={selectedMcp.includes(m.name)} onChange={() => setMcp(toggle(selectedMcp, m.name))} />{m.name}</label>
        ))}{mcp.data?.length === 0 ? <span className="muted">Tanımlı MCP sunucusu yok.</span> : null}</section>
      </div>
      <div className="row"><span className="spacer" /><button className="primary" disabled={save.busy} onClick={async () => { if (await save.run(undefined) !== undefined) onClose(); }}>Kaydet</button></div>
      <SkillSuggestions projectId={projectId} />
    </Drawer>
  );
}

function SkillSuggestions({ projectId }: { projectId: string }) {
  const suggestions = useGet<Array<{ key: string; name: string; description: string; reason: string }>>(["skill-suggestions", projectId], p(projectId, "/skill-suggestions"));
  const catalog = useGet<{ skills: Array<{ key: string; name: string; description: string; content: string }> }>(["catalog"], "/api/catalog");
  const install = useAction((key: string) => {
    const skill = catalog.data!.skills.find((s) => s.key === key)!;
    return api(`/api/skills/${key}`, { method: "PUT", body: { name: skill.name, description: skill.description, content: skill.content } });
  }, [["skills"], ["skill-suggestions", projectId]]);
  if (!suggestions.data?.length) return null;
  return (
    <section className="card">
      <h3>Önerilen skill'ler</h3>
      <p className="muted small">Repolardaki dosyalara göre önerildi.</p>
      <ErrorBox error={install.error} /><Notice text={install.message} />
      <table><tbody>{suggestions.data.map((s) => (
        <tr key={s.key}><td><strong>{s.name}</strong><div className="small muted">{s.description}</div></td><td className="small muted">{s.reason}</td>
          <td className="right"><button onClick={() => install.run(s.key, `${s.name} eklendi.`)}>Ekle</button></td></tr>
      ))}</tbody></table>
    </section>
  );
}

function ProvisionTeam({ projectId, team, onClose }: { projectId: string; team: Team; onClose: () => void }) {
  const skills = useGet<Skill[]>(["skills"], "/api/skills");
  const [form, setForm] = useState<{ provider: ModelProvider; model: string; skillKeys: string[] }>({ provider: "Anthropic", model: "claude-sonnet-4-5", skillKeys: [] });
  const run = useAction(() => api<{ created: number }>(p(projectId, `/teams/${team.id}/provision-agents`), { body: form }), [["agents", projectId], ["teams", projectId]]);
  return (
    <Drawer title={`${team.name}: agent oluştur`} onClose={onClose}>
      <ErrorBox error={run.error} /><Notice text={run.message} />
      <p className="muted">Agent'ı olmayan {team.members.filter((m) => !m.agentId).length} üye için birer developer agent oluşturulur.</p>
      <div className="form">
        <Field label="Sağlayıcı">
          <select value={form.provider} onChange={(e) => setForm({ ...form, provider: e.target.value as ModelProvider, model: PROVIDERS[e.target.value as ModelProvider].example })}>
            {Object.entries(PROVIDERS).filter(([k]) => k !== "AzureOpenAI").map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
          </select>
        </Field>
        <Field label="Model"><input value={form.model} onChange={(e) => setForm({ ...form, model: e.target.value })} /></Field>
        <div className="stack"><strong className="small">Skill'ler</strong>{skills.data?.map((s) => (
          <label key={s.key} className="check"><input type="checkbox" checked={form.skillKeys.includes(s.key)}
            onChange={() => setForm({ ...form, skillKeys: form.skillKeys.includes(s.key) ? form.skillKeys.filter((k) => k !== s.key) : [...form.skillKeys, s.key] })} />{s.name}</label>
        ))}</div>
        <div className="row"><span className="spacer" /><button className="primary" disabled={run.busy} onClick={() => run.run(undefined, (r) => `${r.created} agent oluşturuldu.`)}>Oluştur</button></div>
      </div>
    </Drawer>
  );
}
