import { useState } from "react";
import { fmtDate, p, type PullRequest } from "../api";
import { ErrorBox, useGet } from "../components/ui";
import type { ProjectPageProps } from "./ProjectShell";

const STATUS: Record<PullRequest["status"], { label: string; cls: string }> = {
  Active: { label: "Açık", cls: "info" },
  Completed: { label: "Birleşti", cls: "ok" },
  Abandoned: { label: "Vazgeçildi", cls: "" },
};

export function PullRequestTable({ rows, showRepository = true }: { rows: PullRequest[]; showRepository?: boolean }) {
  if (rows.length === 0) return <div className="muted">Pull request yok.</div>;
  return (
    <table><tbody>{rows.map((pr) => (
      <tr key={pr.id}>
        <td>
          <a href={pr.webUrl} target="_blank" rel="noreferrer">!{pr.azureId} {pr.title}</a>
          <div className="small muted">{showRepository ? `${pr.repository} · ` : ""}{pr.sourceBranch} → {pr.targetBranch} · {pr.createdBy} · {fmtDate(pr.openedAt)}</div>
        </td>
        <td>{pr.workItemAzureIds.map((id) => <span key={id} className="badge">#{id}</span>)}</td>
        <td>{pr.createdByAgent ? <span className="badge">agent</span> : null}</td>
        <td><span className={`badge ${STATUS[pr.status].cls}`}>{STATUS[pr.status].label}</span></td>
      </tr>
    ))}</tbody></table>
  );
}

export function PullRequests({ project }: ProjectPageProps) {
  const [status, setStatus] = useState<"" | PullRequest["status"]>("Active");
  const prs = useGet<PullRequest[]>(["prs", project.id, status], p(project.id, `/pull-requests${status ? `?status=${status}` : ""}`));
  return (
    <div className="stack">
      <div className="row">
        {(["Active", "Completed", "Abandoned", ""] as const).map((s) => (
          <button key={s || "all"} className={status === s ? "primary" : ""} onClick={() => setStatus(s)}>{s ? STATUS[s].label : "Tümü"}</button>
        ))}
      </div>
      <ErrorBox error={prs.error} />
      <div className="card"><PullRequestTable rows={prs.data ?? []} /></div>
    </div>
  );
}
