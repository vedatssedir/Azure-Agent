import { useMemo, useState } from "react";
import { api, fmtDate, fmtUsd, p, type Repository, type Team, type WorkItem, type WorkItemDetail, type WorkItemType } from "../api";
import { Drawer, Empty, ErrorBox, Field, FieldGroup, RunBadge, useAction, useGet } from "../components/ui";
import type { ProjectPageProps } from "./ProjectShell";

const STATE_ORDER = ["New", "To Do", "Proposed", "Approved", "Active", "Committed", "Doing", "In Progress", "Resolved", "Closed", "Done", "Removed"];

type View = "all" | "mine" | "unassigned" | `team:${string}`;

export function Board({ project, can }: ProjectPageProps) {
  const [view, setView] = useState<View>("all");
  const [search, setSearch] = useState("");
  const [iteration, setIteration] = useState("");
  const [open, setOpen] = useState<number | null>(null);
  const [creating, setCreating] = useState(false);
  const teams = useGet<Team[]>(["teams", project.id], p(project.id, "/teams"));
  const filter: Record<string, string> = view === "mine" ? { mine: "true" } : view === "unassigned" ? { unassigned: "true" } : view.startsWith("team:") ? { team: view.slice(5) } : {};
  const query = new URLSearchParams({ ...filter, ...(search ? { search } : {}), ...(iteration ? { iteration } : {}) }).toString();
  const items = useGet<WorkItem[]>(["work-items", project.id, query], p(project.id, `/work-items${query ? `?${query}` : ""}`), { refetchInterval: 15_000 });
  const closed = new Set(project.settings.closedStates);
  const [showClosed, setShowClosed] = useState(false);

  const iterations = useMemo(() => [...new Set((items.data ?? []).map((i) => i.iterationPath).filter(Boolean) as string[])].sort(), [items.data]);
  const columns = useMemo(() => {
    const byState = new Map<string, WorkItem[]>();
    for (const item of items.data ?? []) {
      if (!showClosed && closed.has(item.state)) continue;
      byState.set(item.state, [...(byState.get(item.state) ?? []), item]);
    }
    const rank = (state: string) => STATE_ORDER.indexOf(state) + 1 || 99;
    return [...byState.entries()].sort(([a], [b]) => rank(a) - rank(b));
  }, [items.data, showClosed]); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <>
      <div className="row" style={{ marginBottom: 12 }}>
        <select aria-label="Görünüm" value={view} onChange={(e) => setView(e.target.value as View)} style={{ maxWidth: 220 }}>
          <option value="all">Tüm maddeler</option>
          <option value="mine">Bana atananlar</option>
          <option value="unassigned">Atanmamış</option>
          {teams.data?.map((t) => <option key={t.id} value={`team:${t.id}`}>Takım: {t.name}</option>)}
        </select>
        <input placeholder="Ara: başlık veya #ID" value={search} onChange={(e) => setSearch(e.target.value)} style={{ maxWidth: 240 }} />
        <select value={iteration} onChange={(e) => setIteration(e.target.value)} style={{ maxWidth: 220 }}>
          <option value="">Tüm iterasyonlar</option>
          {iterations.map((i) => <option key={i} value={i}>{i}</option>)}
        </select>
        <button className={showClosed ? "primary" : ""} onClick={() => setShowClosed(!showClosed)}>Kapananlar</button>
        <span className="spacer" />
        <span className="small muted">Developer bir maddeyi kendine atadığında, onun agent'ı işi başlatır.</span>
        {can("workitems.create") ? <button className="primary" onClick={() => setCreating(true)}>Yeni madde</button> : null}
      </div>
      <ErrorBox error={items.error} />
      {!items.isLoading && columns.length === 0 ? (
        <div className="card"><Empty>{(items.data?.length ?? 0) > 0
          ? <>Açık work item yok; {items.data!.length} kapalı madde gizli. <button onClick={() => setShowClosed(true)}>Kapananları göster</button></>
          : "Work item yok. Ayarlar sekmesinden Azure senkronunu çalıştırın."}</Empty></div>
      ) : null}
      <div className="board">
        {columns.map(([state, list]) => (
          <section key={state} className="column">
            <div className="column-head"><span>{state}</span><span className="muted">{list.length}</span></div>
            {list.map((item) => (
              <article key={item.azureId} className="wi-card" onClick={() => setOpen(item.azureId)}>
                <div className="row small muted"><span>{item.type} #{item.azureId}</span>{item.priority ? <span>P{item.priority}</span> : null}{item.storyPoints ? <span>{item.storyPoints} SP</span> : null}</div>
                <div className="wi-title">{item.title}</div>
                <div className="row small">
                  <span className="muted">{item.assignedToDisplayName ?? "Atanmamış"}</span>
                  {item.lastRunStatus ? <><span className="spacer" /><RunBadge status={item.lastRunStatus} /></> : null}
                </div>
                {item.repositoryNames.length || item.costUsd > 0 ? (
                  <div className="row small">
                    {item.repositoryNames.map((r) => <span key={r} className="badge">{r}</span>)}
                    {item.costUsd > 0 ? <><span className="spacer" /><span className="muted">{fmtUsd(item.costUsd, 2)}</span></> : null}
                  </div>
                ) : null}
              </article>
            ))}
          </section>
        ))}
      </div>
      {creating ? <NewWorkItemDrawer projectId={project.id} onClose={() => setCreating(false)} onCreated={(id) => { setCreating(false); setOpen(id); }} /> : null}
      {open !== null ? <WorkItemDrawer projectId={project.id} azureId={open} canAssign={can("workitems.assign")} canRun={can("agents.run")} onClose={() => setOpen(null)} /> : null}
    </>
  );
}

function WorkItemDrawer({ projectId, azureId, canAssign, onClose }: { projectId: string; azureId: number; canAssign: boolean; canRun: boolean; onClose: () => void }) {
  const detail = useGet<WorkItemDetail>(["work-item", projectId, azureId], p(projectId, `/work-items/${azureId}`), { refetchInterval: 5_000 });
  const assign = useAction((toMe: boolean) => api(p(projectId, `/work-items/${azureId}/assign`), { body: { toMe, uniqueName: null } }),
    [["work-item", projectId, azureId], ["work-items", projectId]]);
  const d = detail.data;
  return (
    <Drawer title={d ? `${d.summary.type} #${azureId}` : `#${azureId}`} onClose={onClose}
      actions={d ? <a className="button" href={d.summary.webUrl} target="_blank" rel="noreferrer">Azure'da aç</a> : null}>
      <ErrorBox error={detail.error ?? assign.error} />
      {d ? <>
        <h2 style={{ fontSize: 18 }}>{d.summary.title}</h2>
        <div className="row">
          <span className="badge info">{d.summary.state}</span>
          <span className="muted">{d.summary.assignedToDisplayName ?? "Atanmamış"}</span>
          <span className="spacer" />
          {canAssign ? <>
            <button className="primary" disabled={assign.busy} onClick={() => assign.run(true)}>Bana ata</button>
            {d.summary.assignedToUniqueName ? <button disabled={assign.busy} onClick={() => assign.run(false)}>Atamayı kaldır</button> : null}
          </> : null}
        </div>
        <div className="small muted">Repolar: {d.summary.repositoryNames.join(", ") || "yok — Azure'da repo:<ad> etiketi ekleyin"} · {d.summary.iterationPath} · Toplam maliyet {fmtUsd(d.summary.costUsd, 4)}</div>
        <div className="card prewrap">{stripHtml(d.description) || <span className="muted">Açıklama yok</span>}</div>
        {d.acceptanceCriteria ? <div className="card prewrap"><h3>Kabul kriterleri</h3>{stripHtml(d.acceptanceCriteria)}</div> : null}

        <section>
          <h2>Agent çalışmaları</h2>
          {d.runs.length === 0 ? <div className="muted small">Henüz yok. Madde, agent'ı olan bir developer'a atanınca otomatik başlar.</div> : (
            <table><tbody>{d.runs.map((run) => (
              <tr key={run.id}>
                <td><RunBadge status={run.status} /><div className="small muted">{fmtDate(run.createdAt)}</div></td>
                <td>{run.agentName}<div className="small muted">{run.branch}</div></td>
                <td className="right">{fmtUsd(run.costUsd, 4)}<div className="small muted">{(run.inputTokens + run.outputTokens).toLocaleString()} token</div></td>
                <td>{run.pullRequestUrls.map((u) => <div key={u}><a href={u} target="_blank" rel="noreferrer">PR</a></div>)}{run.error ? <div className="small" style={{ color: "var(--bad)" }}>{run.error}</div> : null}</td>
              </tr>
            ))}</tbody></table>
          )}
        </section>
        <section>
          <h2>Pull request'ler</h2>
          {d.pullRequests.length === 0 ? <div className="muted small">Bağlı PR yok.</div> : (
            <table><tbody>{d.pullRequests.map((pr) => (
              <tr key={pr.id}>
                <td><a href={pr.webUrl} target="_blank" rel="noreferrer">!{pr.azureId} {pr.title}</a><div className="small muted">{pr.repository}: {pr.sourceBranch} → {pr.targetBranch}</div></td>
                <td><span className={`badge ${pr.status === "Completed" ? "ok" : pr.status === "Active" ? "info" : ""}`}>{pr.status}</span></td>
                <td>{pr.createdByAgent ? <span className="badge">agent</span> : null}</td>
              </tr>
            ))}</tbody></table>
          )}
        </section>
        <section>
          <h2>Commit'ler <span className="small muted">(mesajında AB#{azureId} geçenler)</span></h2>
          {d.commits.length === 0 ? <div className="muted small">Bağlı commit yok.</div> : (
            <table><tbody>{d.commits.map((c) => (
              <tr key={c.commitId}>
                <td><a href={c.webUrl} target="_blank" rel="noreferrer"><code>{c.commitId.slice(0, 8)}</code></a></td>
                <td>{c.message.split("\n")[0]}<div className="small muted">{c.repository} · {c.authorName} · {fmtDate(c.committedAt)}</div></td>
              </tr>
            ))}</tbody></table>
          )}
        </section>
      </> : null}
    </Drawer>
  );
}

/** Creates the item in Azure Boards (as the signed-in person when their Azure account is connected). */
function NewWorkItemDrawer({ projectId, onClose, onCreated }: { projectId: string; onClose: () => void; onCreated: (azureId: number) => void }) {
  const types = useGet<WorkItemType[]>(["work-item-types", projectId], p(projectId, "/work-item-types"));
  const repos = useGet<Repository[]>(["repos", projectId], p(projectId, "/repositories"));
  const [form, setForm] = useState({ type: "", title: "", description: "", acceptanceCriteria: "", repositoryNames: [] as string[], assignToMe: true, priority: "2" });
  const type = form.type || types.data?.[0]?.name || "";
  const create = useAction(() => api<WorkItem>(p(projectId, "/work-items"), {
    body: { ...form, type, priority: form.priority ? Number(form.priority) : null, tags: [] },
  }), [["work-items", projectId]]);
  const toggleRepo = (name: string) => setForm({ ...form, repositoryNames: form.repositoryNames.includes(name) ? form.repositoryNames.filter((r) => r !== name) : [...form.repositoryNames, name] });
  return (
    <Drawer title="Yeni madde" onClose={onClose}>
      <ErrorBox error={types.error ?? create.error} />
      <div className="form-2">
        <Field label="Tür"><select value={type} onChange={(e) => setForm({ ...form, type: e.target.value })}>
          {types.data?.map((t) => <option key={t.name} value={t.name}>{t.name}</option>)}
        </select></Field>
        <Field label="Öncelik"><select value={form.priority} onChange={(e) => setForm({ ...form, priority: e.target.value })}>
          <option value="">—</option>{[1, 2, 3, 4].map((n) => <option key={n} value={n}>{n}</option>)}
        </select></Field>
      </div>
      <Field label="Başlık"><input value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} /></Field>
      <Field label="Açıklama" hint="Agent'ın ne yapacağını, ilgili dosya/sınıf adlarını ve hataları yazın">
        <textarea rows={6} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
      </Field>
      <Field label="Kabul kriterleri"><textarea rows={3} value={form.acceptanceCriteria} onChange={(e) => setForm({ ...form, acceptanceCriteria: e.target.value })} /></Field>
      <FieldGroup label="Repolar" hint="Agent bu repoları klonlar (Azure'da repo:<ad> etiketi olarak yazılır)">
        <div className="row wrap">{repos.data?.map((r) => (
          <label key={r.id} className="check"><input type="checkbox" checked={form.repositoryNames.includes(r.name)} onChange={() => toggleRepo(r.name)} disabled={!r.defaultBranch} /> {r.name}{!r.defaultBranch ? " (boş)" : ""}</label>
        ))}</div>
      </FieldGroup>
      <label className="check"><input type="checkbox" checked={form.assignToMe} onChange={(e) => setForm({ ...form, assignToMe: e.target.checked })} /> Bana ata (agent'ım işi hemen başlatır)</label>
      <div className="row"><span className="spacer" />
        <button className="primary" disabled={create.busy || !form.title || !type} onClick={async () => { const item = await create.run(undefined); if (item) onCreated(item.azureId); }}>
          {create.busy ? "Azure'da oluşturuluyor…" : "Azure'da oluştur"}
        </button>
      </div>
    </Drawer>
  );
}

// Azure HTML is rendered as text, never as markup.
function stripHtml(html: string) {
  return html.replace(/<br\s*\/?>/gi, "\n").replace(/<\/(p|div|li|h[1-6])>/gi, "\n").replace(/<[^>]+>/g, "").replace(/&nbsp;/g, " ")
    .replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&amp;/g, "&").trim();
}
