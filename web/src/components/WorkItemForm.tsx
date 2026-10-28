import { useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, fmtAgo, fmtDate, fmtUsd, p, type Agent, type Comment, type Team, type WorkItemDetail } from "../api";
import { useApp } from "../context";
import { Avatar, ErrorBox, Field, hex, Loading, Modal, Notice, RunBadge, StateLabel, useAction, useGet } from "./ui";
import { Icon } from "./icons";
import { htmlToText, sanitizeHtml } from "./html";

/** Opens a work item from anywhere: `?wi=<projectId>:<azureId>` in the URL, like Azure Boards' work item dialog. */
export function useOpenWorkItem() {
  const [params, setParams] = useSearchParams();
  return (projectId: string, azureId: number) => {
    const next = new URLSearchParams(params);
    next.set("wi", `${projectId}:${azureId}`);
    setParams(next);
  };
}

export function WorkItemHost() {
  const [params, setParams] = useSearchParams();
  const value = params.get("wi");
  const match = value?.match(/^([0-9a-f-]{36}):(\d+)$/);
  if (!match) return null;
  const close = () => { const next = new URLSearchParams(params); next.delete("wi"); setParams(next); };
  return <WorkItemForm projectId={match[1]!} azureId={Number(match[2])} onClose={close} />;
}

function RichText({ html, empty }: { html: string; empty: string }) {
  if (!html?.trim()) return <p className="subtle small">{empty}</p>;
  return <div className="rich" dangerouslySetInnerHTML={{ __html: sanitizeHtml(html) }} />;
}

function EditableRich({ label, html, onSave, busy }: { label: string; html: string; onSave: (text: string) => Promise<unknown>; busy: boolean }) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState("");
  return (
    <section style={{ marginBottom: 20 }}>
      <div className="row" style={{ marginBottom: 6 }}>
        <h3 className="panel-title" style={{ margin: 0 }}>{label}</h3><span className="spacer" />
        {!editing ? <button className="small ghost" onClick={() => { setText(htmlToText(html)); setEditing(true); }}>Düzenle</button> : null}
      </div>
      {editing ? (
        <div className="stack tight">
          <textarea aria-label={label} value={text} onChange={(e) => setText(e.target.value)} style={{ minHeight: 160 }} />
          <div className="row"><span className="spacer" />
            <button className="small" onClick={() => setEditing(false)}>Vazgeç</button>
            <button className="small primary" disabled={busy} onClick={async () => { if (await onSave(text) !== undefined) setEditing(false); }}>Kaydet</button>
          </div>
        </div>
      ) : <RichText html={html} empty="Henüz bir şey yazılmamış." />}
    </section>
  );
}

export function WorkItemForm({ projectId, azureId, onClose }: { projectId: string; azureId: number; onClose: () => void }) {
  const { projects } = useApp();
  const project = projects.find((x) => x.id === projectId);
  const key = ["work-item", projectId, azureId];
  const detail = useGet<WorkItemDetail>(key, p(projectId, `/work-items/${azureId}`), { refetchInterval: 15_000 });
  const comments = useGet<Comment[]>(["comments", projectId, azureId], p(projectId, `/work-items/${azureId}/comments`));
  const teams = useGet<Team[]>(["teams", projectId], p(projectId, "/teams"));
  const agents = useGet<Agent[]>(["agents", "mine"], "/api/agents?mine=true");
  const refresh = [key, ["board", projectId], ["work-items", projectId], ["my-work"], ["sprint", projectId]];
  const update = useAction((body: Record<string, unknown>) => api(p(projectId, `/work-items/${azureId}`), { method: "PATCH", body }), refresh);
  const assign = useAction((body: { toMe: boolean; uniqueName: string | null }) => api(p(projectId, `/work-items/${azureId}/assign`), { body }), refresh);
  const comment = useAction((text: string) => api(p(projectId, `/work-items/${azureId}/comments`), { body: { text } }).then(() => true), [["comments", projectId, azureId]]);
  const start = useAction((agentId: string) => api<string>("/api/runs", { body: { projectId, agentId, workItemAzureId: azureId, pullRequestId: null } }), [key, ["runs"]]);
  const [title, setTitle] = useState("");
  const [tags, setTags] = useState("");
  const [newComment, setNewComment] = useState("");
  const [agentId, setAgentId] = useState("");
  const item = detail.data?.summary;
  useEffect(() => { if (item) { setTitle(item.title); setTags(item.tags.join(", ")); } }, [item?.title, item?.tags.join(",")]);
  const developers = (agents.data ?? []).filter((a) => a.kind === "Developer" && a.enabled && (a.projectId === null || a.projectId === projectId));
  useEffect(() => { if (!agentId && developers.length) setAgentId((developers.find((a) => a.isDefault) ?? developers[0]!).id); }, [developers.length]);
  const members = [...new Map((teams.data ?? []).flatMap((t) => t.members).map((m) => [m.uniqueName, m])).values()].sort((a, b) => a.displayName.localeCompare(b.displayName));
  const iterations = [...new Map((teams.data ?? []).flatMap((t) => t.iterations).map((i) => [i.path, i])).values()];
  const canEdit = project?.myPermissions.includes("workitems.assign") ?? false;
  const canRun = project?.myPermissions.includes("agents.run") ?? false;
  const saving = update.busy || assign.busy;
  const save = (body: Record<string, unknown>) => update.run(body);
  const activeRun = detail.data?.runs.find((r) => ["Queued", "Running", "PendingApproval"].includes(r.status));

  const head = item ? (
    <div className="stack tight">
      <div className="small" style={{ color: hex(item.typeColor), fontWeight: 600, textTransform: "uppercase" }}>{item.type} {item.azureId}</div>
      <input className="wi-title" aria-label="Başlık" value={title} disabled={!canEdit} onChange={(e) => setTitle(e.target.value)}
        onBlur={() => { if (title.trim() && title !== item.title) save({ title: title.trim() }); }} />
    </div>
  ) : `#${azureId}`;

  return (
    <Modal title={head} onClose={onClose} band={item ? hex(item.typeColor) : undefined} actions={item ? <>
      <a className="button ghost small" href={item.webUrl} target="_blank" rel="noreferrer"><Icon name="external" /> Azure'da aç</a>
    </> : null}>
      {detail.isLoading ? <Loading /> : <ErrorBox error={detail.error} />}
      {item && detail.data ? <>
        <ErrorBox error={update.error ?? assign.error ?? comment.error ?? start.error} />
        <Notice text={start.message} />
        <div className="wi-fields">
          <Field label="Durum">
            <select value={item.state} disabled={!canEdit || saving} onChange={(e) => save({ state: e.target.value })}>
              {detail.data.states.map((s) => <option key={s} value={s}>{s}</option>)}
            </select>
          </Field>
          <Field label="Atanan">
            <select value={item.assignedToUniqueName ?? ""} disabled={!canEdit || saving}
              onChange={(e) => assign.run({ toMe: false, uniqueName: e.target.value || null })}>
              <option value="">Atanmamış</option>
              {item.assignedToUniqueName && !members.some((m) => m.uniqueName === item.assignedToUniqueName)
                ? <option value={item.assignedToUniqueName}>{item.assignedToDisplayName ?? item.assignedToUniqueName}</option> : null}
              {members.map((m) => <option key={m.uniqueName} value={m.uniqueName}>{m.displayName}</option>)}
            </select>
          </Field>
          <Field label="Iteration">
            <select value={item.iterationPath ?? ""} disabled={!canEdit || saving} onChange={(e) => save({ iterationPath: e.target.value })}>
              {item.iterationPath && !iterations.some((i) => i.path === item.iterationPath) ? <option value={item.iterationPath}>{item.iterationPath}</option> : null}
              {iterations.map((i) => <option key={i.path} value={i.path}>{i.name}{i.timeFrame === "current" ? " (güncel)" : ""}</option>)}
            </select>
          </Field>
          <Field label="Öncelik">
            <select value={item.priority ?? ""} disabled={!canEdit || saving} onChange={(e) => save({ priority: Number(e.target.value) })}>
              {item.priority === null ? <option value="">—</option> : null}
              {[1, 2, 3, 4].map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          </Field>
        </div>
        <div className="row" style={{ marginBottom: 16 }}>
          <StateLabel state={item.state} color={item.stateColor} />
          {item.boardColumn && item.boardColumn !== item.state ? <span className="badge">{item.boardColumn}</span> : null}
          {canEdit ? <button className="small" disabled={saving} onClick={() => assign.run({ toMe: true, uniqueName: null })}>Bana ata</button> : null}
          <span className="spacer" />
          <input aria-label="Etiketler" placeholder="Etiketler (virgülle), örn. agent:vedat-s-agent" value={tags} disabled={!canEdit} style={{ width: 340 }}
            onChange={(e) => setTags(e.target.value)}
            onBlur={() => { const next = tags.split(",").map((t) => t.trim()).filter(Boolean); if (next.join(",") !== item.tags.join(",")) save({ tags: next }); }} />
        </div>
        <div className="wi-form">
          <div>
            <EditableRich label="Açıklama" html={detail.data.description} busy={update.busy} onSave={(text) => save({ description: text })} />
            <EditableRich label="Kabul kriterleri" html={detail.data.acceptanceCriteria} busy={update.busy} onSave={(text) => save({ acceptanceCriteria: text })} />
            <section>
              <h3 className="panel-title">Tartışma</h3>
              {canEdit ? (
                <div className="stack tight" style={{ marginBottom: 8 }}>
                  <textarea aria-label="Yorum" placeholder="Yorum ekle…" value={newComment} onChange={(e) => setNewComment(e.target.value)} style={{ minHeight: 70 }} />
                  <div className="row"><span className="spacer" />
                    <button className="small primary" disabled={!newComment.trim() || comment.busy}
                      onClick={async () => { if (await comment.run(newComment)) setNewComment(""); }}>Yorum yap</button>
                  </div>
                </div>
              ) : null}
              {comments.isLoading ? <p className="subtle small">Yorumlar yükleniyor…</p> : null}
              {(comments.data ?? []).map((c) => (
                <div className="comment" key={c.id}>
                  <Avatar name={c.authorDisplayName} />
                  <div className="text">
                    <div className="small"><strong>{c.authorDisplayName}</strong> <span className="subtle">{fmtAgo(c.createdDate)}</span></div>
                    <RichText html={c.text} empty="" />
                  </div>
                </div>
              ))}
              {comments.data?.length === 0 ? <p className="subtle small">Henüz yorum yok.</p> : null}
            </section>
          </div>
          <aside className="stack">
            <section className="card">
              <h3 className="panel-title">Agent</h3>
              {activeRun ? <div className="row small" style={{ marginBottom: 8 }}><RunBadge status={activeRun.status} /> {activeRun.agentName} <Link to={`/runs/${activeRun.id}`}>izle</Link></div> : null}
              {canRun && developers.length ? (
                <div className="stack tight">
                  <select aria-label="Agent" value={agentId} onChange={(e) => setAgentId(e.target.value)}>
                    {developers.map((a) => <option key={a.id} value={a.id}>{a.name}{a.isDefault ? " (varsayılan)" : ""}</option>)}
                  </select>
                  <button className="primary" disabled={!!activeRun || start.busy || !agentId} onClick={() => start.run(agentId, "Agent işe başladı")}>
                    <Icon name="runs" /> Agent'a geliştirt
                  </button>
                  <span className="small muted">Agent branch açar, kodu yazar ve bu maddeye bağlı bir pull request oluşturur.</span>
                </div>
              ) : canRun ? <p className="small muted">Developer agent'ınız yok. <Link to="/agents">Agent oluşturun</Link>.</p> : null}
              {detail.data.runs.length ? <>
                <h3 className="panel-title" style={{ marginTop: 14 }}>Çalıştırmalar</h3>
                {detail.data.runs.map((r) => (
                  <div className="link-row" key={r.id}>
                    <RunBadge status={r.status} /><Link to={`/runs/${r.id}`} className="truncate" style={{ flex: 1 }}>{r.agentName}</Link>
                    <span className="subtle small">{fmtUsd(r.costUsd)} · {fmtAgo(r.createdAt)}</span>
                  </div>
                ))}
              </> : null}
            </section>
            <section className="card">
              <h3 className="panel-title">Geliştirme</h3>
              {detail.data.pullRequests.map((pr) => (
                <div className="link-row" key={pr.id}>
                  <Icon name="pr" /><Link to={`/p/${projectId}/pullrequests/${pr.id}`} className="truncate" style={{ flex: 1 }}>!{pr.azureId} {pr.title}</Link>
                  <span className={`badge ${pr.status === "Active" ? "info" : pr.status === "Completed" ? "ok" : ""}`}>{pr.status}</span>
                </div>
              ))}
              {detail.data.commits.slice(0, 10).map((c) => (
                <div className="link-row" key={c.commitId}>
                  <Icon name="commits" />
                  <Link to={`/p/${projectId}/repos/commits?repo=${c.repositoryId}&commit=${c.commitId}`} className="mono">{c.commitId.slice(0, 8)}</Link>
                  <span className="truncate" style={{ flex: 1 }}>{c.message.split("\n")[0]}</span>
                </div>
              ))}
              {item.repositoryNames.map((r) => <span key={r} className="tag repo" style={{ marginRight: 4 }}>{r}</span>)}
              {!detail.data.pullRequests.length && !detail.data.commits.length ? <p className="small subtle">Bağlı pull request ya da commit yok.</p> : null}
            </section>
            <section className="card small">
              <h3 className="panel-title">Ayrıntılar</h3>
              <div className="stack tight">
                <div><span className="muted">Alan:</span> {item.areaPath ?? "—"}</div>
                <div><span className="muted">Oluşturan:</span> {detail.data.createdBy ?? "—"} · {fmtDate(detail.data.createdAt)}</div>
                <div><span className="muted">Son değişiklik:</span> {fmtDate(item.changedAt)}</div>
                {item.costUsd > 0 ? <div><span className="muted">Agent maliyeti:</span> {fmtUsd(item.costUsd)}</div> : null}
              </div>
            </section>
          </aside>
        </div>
      </> : null}
    </Modal>
  );
}
