import { useState } from "react";
import { api, fmtDate, fmtTokens, fmtUsd, p, type RunDetail, type RunStatus, type RunSummary } from "../api";
import { Drawer, Empty, ErrorBox, RunBadge, useAction, useGet } from "../components/ui";
import type { ProjectPageProps } from "./ProjectShell";

export function Runs({ project, can }: ProjectPageProps) {
  const [status, setStatus] = useState<RunStatus | "">("");
  const runs = useGet<RunSummary[]>(["runs", project.id, status], p(project.id, `/runs${status ? `?status=${status}` : ""}`), { refetchInterval: 5_000 });
  const [open, setOpen] = useState<string | null>(null);
  const keys = [["runs", project.id]];
  const act = useAction(({ id, action }: { id: string; action: "approve" | "cancel" | "retry" }) => api(p(project.id, `/runs/${id}/${action}`), { body: {} }), keys);
  const canRun = can("agents.run");
  return (
    <div className="stack">
      <div className="row">
        {(["", "PendingApproval", "Queued", "Running", "Succeeded", "Failed"] as const).map((s) => (
          <button key={s || "all"} className={status === s ? "primary" : ""} onClick={() => setStatus(s)}>
            {s === "" ? "Tümü" : s === "PendingApproval" ? "Onay bekleyen" : s === "Queued" ? "Sırada" : s === "Running" ? "Çalışan" : s === "Succeeded" ? "Tamamlanan" : "Başarısız"}
          </button>
        ))}
      </div>
      <ErrorBox error={runs.error ?? act.error} />
      <div className="card">
        {runs.data?.length === 0 ? <Empty>Agent çalışması yok.</Empty> : (
          <table>
            <thead><tr><th>Durum</th><th>İş</th><th>Agent</th><th className="right">Token</th><th className="right">Maliyet</th><th>Sonuç</th><th /></tr></thead>
            <tbody>{runs.data?.map((r) => (
              <tr key={r.id} className="clickable" onClick={() => setOpen(r.id)}>
                <td><RunBadge status={r.status} /><div className="small muted">{fmtDate(r.createdAt)}</div></td>
                <td>{r.kind === "Review" ? "Code review" : `#${r.workItemAzureId}`}<div className="small muted">{r.branch}</div></td>
                <td>{r.agentName}</td>
                <td className="right">{fmtTokens(r.inputTokens + r.outputTokens)}</td>
                <td className="right">{fmtUsd(r.costUsd, 4)}</td>
                <td>{r.pullRequestUrls.map((u) => <div key={u}><a href={u} target="_blank" rel="noreferrer" onClick={(e) => e.stopPropagation()}>Pull request</a></div>)}
                  {r.error ? <div className="small" style={{ color: "var(--bad)" }}>{r.error}</div> : null}</td>
                <td onClick={(e) => e.stopPropagation()}>{canRun ? <div className="row">
                  {r.status === "PendingApproval" ? <button className="primary" onClick={() => act.run({ id: r.id, action: "approve" })}>Onayla</button> : null}
                  {r.status === "PendingApproval" || r.status === "Queued" ? <button onClick={() => act.run({ id: r.id, action: "cancel" })}>İptal</button> : null}
                  {r.status === "Failed" || r.status === "Cancelled" ? <button onClick={() => act.run({ id: r.id, action: "retry" })}>Tekrar dene</button> : null}
                </div> : null}</td>
              </tr>
            ))}</tbody>
          </table>
        )}
      </div>
      {open ? <RunDrawer projectId={project.id} runId={open} onClose={() => setOpen(null)} /> : null}
    </div>
  );
}

const STEP_LABEL: Record<string, string> = { ModelCall: "Model", ToolCall: "Araç", Git: "Git", Error: "Hata", Log: "Log" };

function RunDrawer({ projectId, runId, onClose }: { projectId: string; runId: string; onClose: () => void }) {
  const run = useGet<RunDetail>(["run", runId], p(projectId, `/runs/${runId}`), { refetchInterval: 4_000 });
  const d = run.data;
  return (
    <Drawer title="Çalışma detayı" onClose={onClose}>
      <ErrorBox error={run.error} />
      {d ? <>
        <div className="row"><RunBadge status={d.run.status} /><strong>{d.run.agentName}</strong><span className="muted">{d.provider} / {d.model}</span></div>
        <div className="stats">
          <div className="stat"><div className="label">Maliyet</div><div className="value">{fmtUsd(d.run.costUsd, 4)}</div></div>
          <div className="stat"><div className="label">Token (girdi/çıktı)</div><div className="value">{fmtTokens(d.run.inputTokens)} / {fmtTokens(d.run.outputTokens)}</div></div>
          <div className="stat"><div className="label">Model çağrısı</div><div className="value">{d.modelCalls}</div></div>
          <div className="stat"><div className="label">Araç çağrısı</div><div className="value">{d.toolCalls}</div></div>
        </div>
        <div className="small muted">İsteyen: {d.requestedBy ?? "—"}{d.approvedBy ? ` · Onaylayan: ${d.approvedBy}` : ""} · Başlangıç {fmtDate(d.run.startedAt)} · Bitiş {fmtDate(d.run.finishedAt)}</div>
        {d.run.error ? <div className="error">{d.run.error}</div> : null}
        {d.summary ? <section><h2>Özet</h2><pre>{d.summary}</pre></section> : null}
        <section>
          <h2>Adımlar</h2>
          <table><tbody>{d.steps.map((s) => (
            <tr key={s.sequence}>
              <td className="small muted">{s.sequence}</td>
              <td><span className={`badge ${s.type === "Error" ? "bad" : s.type === "Git" ? "info" : ""}`}>{STEP_LABEL[s.type]}</span></td>
              <td>{s.title}{s.detail ? <div className="small muted">{s.detail}</div> : null}</td>
              <td className="right small">{s.inputTokens + s.outputTokens > 0 ? `${fmtTokens(s.inputTokens + s.outputTokens)} · ${fmtUsd(s.costUsd, 4)}` : ""}{s.durationMs ? <div className="muted">{(s.durationMs / 1000).toFixed(1)} sn</div> : null}</td>
            </tr>
          ))}</tbody></table>
        </section>
      </> : null}
    </Drawer>
  );
}
