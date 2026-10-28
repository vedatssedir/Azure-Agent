import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { api, fmtAgo, fmtDate, fmtTokens, fmtUsd, qs, RUN_STATUS, type Agent, type RunDetail, type RunStatus, type RunSummary } from "../api";
import { useApp } from "../context";
import { Avatar, Empty, ErrorBox, Loading, PageHead, RunBadge, Stat, useAction, useGet } from "../components/ui";
import { Icon } from "../components/icons";

const duration = (r: RunSummary) => {
  if (!r.startedAt) return "—";
  const ms = new Date(r.finishedAt ?? Date.now()).getTime() - new Date(r.startedAt).getTime();
  const m = Math.floor(ms / 60_000);
  return m >= 1 ? `${m} dk ${Math.round((ms % 60_000) / 1000)} sn` : `${Math.round(ms / 1000)} sn`;
};

export function Runs() {
  const { isAdmin, projects } = useApp();
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const filter = { status: params.get("status") ?? "", agent: params.get("agent") ?? "", project: params.get("project") ?? "", mine: params.get("all") !== "1" };
  const set = (key: string, value: string) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); setParams(next); };
  const agents = useGet<Agent[]>(["agents", filter.mine ? "mine" : "all"], `/api/agents${filter.mine ? "?mine=true" : ""}`);
  const runs = useGet<RunSummary[]>(["runs", filter], `/api/runs${qs({ status: filter.status, agent: filter.agent, project: filter.project, mine: filter.mine })}`, { refetchInterval: 10_000 });
  return (
    <>
      <PageHead title="Agent çalıştırmaları" subtitle="Agent'ların geliştirme ve kod inceleme işleri; adım adım kayıtları ve maliyetleriyle." />
      <div className="toolbar">
        {isAdmin ? (
          <select aria-label="Kapsam" value={filter.mine ? "" : "1"} onChange={(e) => set("all", e.target.value)}>
            <option value="">Benim agent'larım</option><option value="1">Tüm agent'lar</option>
          </select>
        ) : null}
        <select aria-label="Durum" value={filter.status} onChange={(e) => set("status", e.target.value)}>
          <option value="">Tüm durumlar</option>{(Object.keys(RUN_STATUS) as RunStatus[]).map((s) => <option key={s} value={s}>{RUN_STATUS[s].label}</option>)}
        </select>
        <select aria-label="Agent" value={filter.agent} onChange={(e) => set("agent", e.target.value)}>
          <option value="">Tüm agent'lar</option>{(agents.data ?? []).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
        </select>
        <select aria-label="Proje" value={filter.project} onChange={(e) => set("project", e.target.value)}>
          <option value="">Tüm projeler</option>{projects.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
        </select>
      </div>
      <ErrorBox error={runs.error} />
      {runs.isLoading ? <Loading /> : runs.data?.length === 0 ? <Empty>Çalıştırma yok.</Empty> : (
        <div className="table-card">
          <table>
            <thead><tr><th>Durum</th><th>Agent</th><th>İş</th><th>Proje</th><th>Pull request</th><th className="right">Token</th><th className="right">Maliyet</th><th>Süre</th><th>Başladı</th></tr></thead>
            <tbody>{(runs.data ?? []).map((r) => (
              <tr key={r.id} className="clickable" onClick={() => navigate(`/runs/${r.id}`)}>
                <td><RunBadge status={r.status} /></td>
                <td><span className="row nowrap"><Avatar name={r.agentName} />{r.agentName}</span></td>
                <td>{r.kind === "Review" ? "Kod incelemesi" : r.workItemAzureId ? `#${r.workItemAzureId}` : "—"}</td>
                <td className="muted">{projects.find((x) => x.id === r.projectId)?.name ?? ""}</td>
                <td>{r.pullRequestUrls.length ? <span className="badge ok">{r.pullRequestUrls.length} PR</span> : null}</td>
                <td className="right small">{fmtTokens(r.inputTokens + r.outputTokens)}</td>
                <td className="right">{fmtUsd(r.costUsd)}</td>
                <td className="small muted">{duration(r)}</td>
                <td className="small muted">{fmtAgo(r.createdAt)}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
    </>
  );
}

const STEP_ICON: Record<string, string> = { ModelCall: "agents", ToolCall: "settings", Git: "branch", Log: "workitems", Error: "close" };

export function RunPage() {
  const { runId = "" } = useParams();
  const key = ["run", runId];
  const run = useGet<RunDetail>(key, `/api/runs/${runId}`, { refetchInterval: 5_000 });
  const refresh = [key, ["runs"]];
  const approve = useAction(() => api(`/api/runs/${runId}/approve`, { body: {} }), refresh);
  const cancel = useAction(() => api(`/api/runs/${runId}/cancel`, { body: {} }), refresh);
  const retry = useAction(() => api<string>(`/api/runs/${runId}/retry`, { body: {} }), refresh);
  const navigate = useNavigate();
  if (run.isLoading) return <Loading />;
  if (run.error) return <ErrorBox error={run.error} />;
  const d = run.data!;
  const r = d.run;
  const active = ["Queued", "Running", "PendingApproval"].includes(r.status);
  return (
    <>
      <div className="small"><Link to="/runs">← Çalıştırmalar</Link></div>
      <PageHead title={<span className="row"><RunBadge status={r.status} /> {r.kind === "Review" ? "Kod incelemesi" : d.workItemTitle ?? "Geliştirme"}</span>}
        subtitle={<span>{r.agentName} · {d.projectName}{r.workItemAzureId ? <> · <Link to={`/p/${r.projectId}/board?wi=${r.projectId}:${r.workItemAzureId}`}>#{r.workItemAzureId}</Link></> : null}
          {d.requestedBy ? ` · başlatan ${d.requestedBy}` : ""}</span>}>
        {r.status === "PendingApproval" ? <button className="primary" disabled={approve.busy} onClick={() => approve.run(undefined)}>Onayla ve başlat</button> : null}
        {active ? <button className="danger" disabled={cancel.busy} onClick={() => cancel.run(undefined)}>İptal et</button> : null}
        {r.status === "Failed" || r.status === "Cancelled" ? <button disabled={retry.busy} onClick={async () => { const id = await retry.run(undefined); if (id) navigate(`/runs/${id}`); }}>Yeniden dene</button> : null}
      </PageHead>
      <ErrorBox error={approve.error ?? cancel.error ?? retry.error} />
      {r.error ? <div className="error" style={{ marginBottom: 12 }}>{r.error}</div> : null}
      <div className="stats" style={{ marginBottom: 16 }}>
        <Stat label="Maliyet" value={fmtUsd(r.costUsd)} />
        <Stat label="Token (giriş / çıkış)" value={<span style={{ fontSize: 16 }}>{fmtTokens(r.inputTokens)} / {fmtTokens(r.outputTokens)}</span>} />
        <Stat label="Model çağrısı / araç" value={`${d.modelCalls} / ${d.toolCalls}`} />
        <Stat label="Süre" value={<span style={{ fontSize: 16 }}>{duration(r)}</span>} />
        <Stat label="Model" value={<span className="mono" style={{ fontSize: 13 }}>{d.model ?? "—"}</span>} />
      </div>
      {r.branch || r.pullRequestUrls.length ? (
        <div className="card row" style={{ marginBottom: 16 }}>
          {r.branch ? <span className="row nowrap"><Icon name="branch" /><span className="mono">{r.branch}</span></span> : null}
          {r.pullRequestUrls.map((u) => <a key={u} href={u} target="_blank" rel="noreferrer" className="row nowrap"><Icon name="pr" /> Pull request</a>)}
        </div>
      ) : null}
      {d.summary ? <div className="card prewrap" style={{ marginBottom: 16 }}><h3 className="panel-title">Özet</h3>{d.summary}</div> : null}
      <h2>Adımlar</h2>
      {d.steps.length === 0 ? <Empty>{active ? "Agent başlıyor…" : "Kayıtlı adım yok."}</Empty> : (
        <div className="table-card">
          <table>
            <tbody>{d.steps.map((s) => (
              <tr key={s.sequence}>
                <td style={{ width: 30, color: s.type === "Error" ? "var(--bad)" : "var(--accent)" }}><Icon name={STEP_ICON[s.type] ?? "workitems"} /></td>
                <td>
                  <div><strong style={{ fontWeight: 600 }}>{s.title}</strong></div>
                  {s.detail ? <details><summary className="small muted" style={{ cursor: "pointer" }}>ayrıntı</summary><pre className="prewrap small" style={{ maxHeight: 320, overflow: "auto" }}>{s.detail}</pre></details> : null}
                </td>
                <td className="right small muted">{s.inputTokens + s.outputTokens ? `${fmtTokens(s.inputTokens + s.outputTokens)} tok` : ""}</td>
                <td className="right small muted">{s.costUsd ? fmtUsd(s.costUsd, 4) : ""}</td>
                <td className="right small muted">{s.durationMs ? `${(s.durationMs / 1000).toFixed(1)} sn` : ""}</td>
                <td className="small subtle">{fmtDate(s.createdAt)}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
    </>
  );
}
