import { useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { api, fmtAgo, fmtDate, fmtUsd, qs, VERDICT_LABEL, type Agent, type PullRequest, type PullRequestDetail, type PullRequestStatus, type Repository } from "../api";
import { useApp } from "../context";
import { Avatar, Empty, ErrorBox, Loading, Notice, PageHead, RunBadge, useAction, useGet } from "../components/ui";
import { DiffSummary, FileDiff } from "../components/Code";
import { useOpenWorkItem } from "../components/WorkItemForm";
import { Icon } from "../components/icons";

const STATUS: Record<PullRequestStatus, { label: string; cls: string }> = { Active: { label: "Aktif", cls: "info" }, Completed: { label: "Tamamlandı", cls: "ok" }, Abandoned: { label: "Vazgeçildi", cls: "" } };

export function PullRequests() {
  const { projectId = "" } = useParams();
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const status = (params.get("status") ?? "Active") as PullRequestStatus;
  const repo = params.get("repo");
  const set = (key: string, value: string | null) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); setParams(next); };
  const repos = useGet<Repository[]>(["repos", projectId], `/api/repos?project=${projectId}`);
  const prs = useGet<PullRequest[]>(["prs", projectId, repo, status], `/api/pull-requests${qs({ project: projectId, repository: repo, status })}`, { refetchInterval: 30_000 });
  return (
    <>
      <PageHead title="Pull request'ler" subtitle="Agent'ların açtığı ve ekibin açtığı PR'lar; AI kod incelemeleriyle birlikte." />
      <div className="tabs">
        {(Object.keys(STATUS) as PullRequestStatus[]).map((s) => <button key={s} className={`tab${status === s ? " active" : ""}`} onClick={() => set("status", s === "Active" ? null : s)}>{STATUS[s].label}</button>)}
        <span className="spacer" />
        <select aria-label="Repo" value={repo ?? ""} onChange={(e) => set("repo", e.target.value || null)} style={{ width: "auto", marginBottom: 4 }}>
          <option value="">Tüm repolar</option>{(repos.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
        </select>
      </div>
      <ErrorBox error={prs.error} />
      {prs.isLoading ? <Loading /> : prs.data?.length === 0 ? <Empty>Bu durumda pull request yok.</Empty> : (
        <div className="table-card">
          <table>
            <tbody>{(prs.data ?? []).map((pr) => (
              <tr key={pr.id} className="clickable" onClick={() => navigate(`/p/${projectId}/pullrequests/${pr.id}`)}>
                <td style={{ width: 36 }}><Avatar name={pr.createdBy} /></td>
                <td>
                  <div className="row nowrap"><strong className="truncate">{pr.title}</strong>{pr.createdByAgent ? <span className="tag agent">agent</span> : null}</div>
                  <div className="small muted">!{pr.azureId} · {pr.createdBy} · {pr.repository} · <span className="mono">{pr.sourceBranch.replace("refs/heads/", "")}</span> → <span className="mono">{pr.targetBranch.replace("refs/heads/", "")}</span> · {fmtAgo(pr.openedAt)}</div>
                </td>
                <td>{pr.workItemAzureIds.map((id) => <span key={id} className="tag" style={{ marginRight: 4 }}>#{id}</span>)}</td>
                <td className="right">{pr.lastReviewVerdict ? <span className={`badge ${VERDICT_LABEL[pr.lastReviewVerdict].cls}`}>AI: {VERDICT_LABEL[pr.lastReviewVerdict].label}</span> : null}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
    </>
  );
}

export function PullRequestPage() {
  const { projectId = "", pullRequestId = "" } = useParams();
  const { projects } = useApp();
  const project = projects.find((x) => x.id === projectId)!;
  const key = ["pr", pullRequestId];
  const detail = useGet<PullRequestDetail>(key, `/api/pull-requests/${pullRequestId}`, { refetchInterval: 20_000 });
  const agents = useGet<Agent[]>(["agents", "all"], "/api/agents");
  const reviewers = (agents.data ?? []).filter((a) => a.kind === "Reviewer" && a.enabled);
  const [agentId, setAgentId] = useState("");
  const [tab, setTab] = useState<"overview" | "files">("overview");
  const start = useAction((id: string) => api("/api/runs", { body: { projectId, agentId: id, workItemAzureId: null, pullRequestId } }), [key]);
  const open = useOpenWorkItem();
  if (detail.isLoading) return <Loading />;
  if (detail.error) return <ErrorBox error={detail.error} />;
  const d = detail.data!;
  const pr = d.pullRequest;
  const activeReview = d.runs.find((r) => ["Queued", "Running", "PendingApproval"].includes(r.status));
  const chosen = agentId || reviewers[0]?.id || "";
  return (
    <>
      <div className="small"><Link to={`/p/${projectId}/pullrequests`}>← Pull request'ler</Link></div>
      <PageHead title={<span>{pr.title} <span className="subtle" style={{ fontWeight: 400 }}>!{pr.azureId}</span></span>}
        subtitle={<span className="row"><span className={`badge ${STATUS[pr.status].cls}`}>{STATUS[pr.status].label}</span><Avatar name={pr.createdBy} />{pr.createdBy}
          <span>{pr.repository}: <span className="mono">{pr.sourceBranch.replace("refs/heads/", "")}</span> → <span className="mono">{pr.targetBranch.replace("refs/heads/", "")}</span></span>
          <span>· {fmtDate(pr.openedAt)}</span></span>}>
        <a className="button" href={pr.webUrl} target="_blank" rel="noreferrer"><Icon name="external" /> Azure'da aç</a>
      </PageHead>
      <div className="tabs">
        <button className={`tab${tab === "overview" ? " active" : ""}`} onClick={() => setTab("overview")}>Genel bakış</button>
        <button className={`tab${tab === "files" ? " active" : ""}`} onClick={() => setTab("files")}>Dosyalar ({d.files.length})</button>
      </div>
      {tab === "files" ? (
        <>{d.files.length ? <><div style={{ marginBottom: 8 }}><DiffSummary files={d.files} /></div>{d.files.map((f) => <FileDiff key={f.path} file={f} defaultOpen={d.files.length <= 15} />)}</>
          : <Empty>Değişiklik bulunamadı (branch silinmiş olabilir).</Empty>}</>
      ) : (
        <div className="wi-form">
          <div className="stack">
            <h2 style={{ margin: 0 }}>AI kod incelemeleri</h2>
            {d.reviews.length === 0 ? <Empty>Henüz inceleme yok.</Empty> : d.reviews.map((r) => (
              <div className="card stack tight" key={r.runId}>
                <div className="row"><Avatar name={r.agentName} /><strong>{r.agentName}</strong><span className={`badge ${VERDICT_LABEL[r.verdict].cls}`}>{VERDICT_LABEL[r.verdict].label}</span>
                  <span className="spacer" /><span className="small muted">{fmtAgo(r.createdAt)} · <span className="mono">{r.sourceCommit.slice(0, 8)}</span></span></div>
                <div className="prewrap">{r.summary}</div>
                {r.findings.length ? (
                  <table>
                    <thead><tr><th>Önem</th><th>Dosya</th><th>Bulgu</th></tr></thead>
                    <tbody>{r.findings.map((f, i) => (
                      <tr key={i}>
                        <td><span className={`badge ${f.severity === "high" || f.severity === "critical" ? "bad" : f.severity === "medium" ? "warn" : ""}`}>{f.severity}</span></td>
                        <td className="mono small">{f.file}{f.line ? `:${f.line}` : ""}</td>
                        <td>{f.message}</td>
                      </tr>
                    ))}</tbody>
                  </table>
                ) : null}
              </div>
            ))}
          </div>
          <aside className="stack">
            <section className="card stack tight">
              <h3 className="panel-title">İnceleme başlat</h3>
              <ErrorBox error={start.error} /><Notice text={start.message} />
              {activeReview ? <div className="row small"><RunBadge status={activeReview.status} />{activeReview.agentName}<Link to={`/runs/${activeReview.id}`}>izle</Link></div> : null}
              {reviewers.length && project.myPermissions.includes("pullrequests.review") ? <>
                <select aria-label="Reviewer agent" value={chosen} onChange={(e) => setAgentId(e.target.value)}>{reviewers.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}</select>
                <button className="primary" disabled={!!activeReview || start.busy || pr.status !== "Active"} onClick={() => start.run(chosen, "İnceleme başladı")}>AI ile incele</button>
              </> : <p className="small muted">Reviewer türünde bir agent yok. <Link to="/agents">Agent'lar</Link> sayfasından ekleyin.</p>}
            </section>
            <section className="card">
              <h3 className="panel-title">Work item'lar</h3>
              {pr.workItemAzureIds.length ? pr.workItemAzureIds.map((id) => (
                <div key={id} className="link-row"><Icon name="workitems" /><a href="#" onClick={(e) => { e.preventDefault(); open(projectId, id); }}>#{id}</a></div>
              )) : <p className="small subtle">Bağlı work item yok.</p>}
            </section>
            {d.runs.length ? (
              <section className="card">
                <h3 className="panel-title">Agent çalıştırmaları</h3>
                {d.runs.map((r) => <div key={r.id} className="link-row"><RunBadge status={r.status} /><Link to={`/runs/${r.id}`} style={{ flex: 1 }}>{r.agentName}</Link><span className="small subtle">{fmtUsd(r.costUsd)}</span></div>)}
              </section>
            ) : null}
          </aside>
        </div>
      )}
    </>
  );
}
