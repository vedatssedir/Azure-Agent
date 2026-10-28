import { useEffect, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { fmtAgo, fmtBytes, fmtDate, qs, type Blob, type CommitDetail, type GitCommit, type Repository, type RepositoryDetail, type TreeEntry } from "../api";
import { Avatar, Empty, ErrorBox, Loading, PageHead, useGet } from "../components/ui";
import { CodeView, DiffSummary, FileDiff } from "../components/Code";
import { Icon } from "../components/icons";

type View = "files" | "commits" | "branches";

/** Azure Repos-like code browser over the local mirror of every synced repository. */
export function Repos() {
  const { projectId = "", view = "files" } = useParams();
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const repos = useGet<Repository[]>(["repos", projectId], `/api/repos?project=${projectId}`);
  const repoId = params.get("repo") ?? repos.data?.[0]?.id ?? null;
  const detail = useGet<RepositoryDetail>(["repo", repoId], repoId ? `/api/repos/${repoId}` : null);
  const repo = detail.data?.repository;
  const ref = params.get("ref") ?? repo?.defaultBranch ?? null;
  const update = (changes: Record<string, string | null>, to?: View) => {
    const next = new URLSearchParams(params);
    if (!next.get("repo") && repoId) next.set("repo", repoId);
    Object.entries(changes).forEach(([k, v]) => (v ? next.set(k, v) : next.delete(k)));
    if (to) navigate(`/p/${projectId}/repos/${to}?${next}`);
    else setParams(next);
  };
  if (repos.isLoading) return <Loading />;
  if (repos.data?.length === 0) return <><PageHead title="Repos" /><Empty>Bu projede erişebildiğiniz repo yok.</Empty></>;

  return (
    <>
      <div className="page-head">
        <select aria-label="Repo" value={repoId ?? ""} style={{ width: "auto", fontWeight: 600, fontSize: 16 }}
          onChange={(e) => navigate(`/p/${projectId}/repos/${view}?repo=${e.target.value}`)}>
          {(repos.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
        </select>
        {detail.data?.branches.length ? (
          <select aria-label="Branch" value={ref ?? ""} style={{ width: "auto" }} onChange={(e) => update({ ref: e.target.value, path: null, commit: null })}>
            {detail.data.branches.map((b) => <option key={b.name} value={b.name}>{b.name}</option>)}
          </select>
        ) : null}
        <div className="tabs" style={{ margin: 0, border: 0 }}>
          {(["files", "commits", "branches"] as View[]).map((v) => (
            <button key={v} className={`tab${view === v ? " active" : ""}`} onClick={() => update({ commit: null }, v)}>
              {v === "files" ? "Dosyalar" : v === "commits" ? "Commit'ler" : "Branch'ler"}
            </button>
          ))}
          <Link className="tab" to={`/p/${projectId}/pullrequests?repo=${repoId}`}>Pull request'ler{repo?.openPullRequests ? ` (${repo.openPullRequests})` : ""}</Link>
        </div>
        <span className="spacer" />
        {repo ? <span className="small muted">{fmtBytes(repo.sizeBytes)} · senkron {fmtAgo(repo.mirrorSyncedAt)}</span> : null}
        {repo ? <a className="button ghost small" href={repo.webUrl} target="_blank" rel="noreferrer"><Icon name="external" /> Azure</a> : null}
      </div>
      <ErrorBox error={repos.error ?? detail.error} />
      {repo?.mirrorError ? <div className="error" style={{ marginBottom: 12 }}>Repo kopyalanamadı: {repo.mirrorError}</div> : null}
      {repo && !repo.defaultBranch ? <Empty>Bu repo boş.</Empty> : null}
      {repo && repo.defaultBranch && !repo.mirrorSyncedAt && !repo.mirrorError ? <Empty>Repo geçmişiyle birlikte kopyalanıyor… birkaç dakika içinde burada olacak.</Empty> : null}
      {repo && ref && repo.mirrorSyncedAt ? (
        view === "commits" ? (
          params.get("commit") ? <CommitView repoId={repo.id} commitId={params.get("commit")!} onBack={() => update({ commit: null })} onPath={(path) => update({ path, commit: null }, "files")} />
            : <CommitList repoId={repo.id} gitRef={ref} path={params.get("path")} onOpen={(c) => update({ commit: c })} onClearPath={() => update({ path: null })} />
        ) : view === "branches" ? (
          <Branches detail={detail.data!} onOpen={(b) => update({ ref: b, path: null }, "files")} />
        ) : (
          <Files repoId={repo.id} gitRef={ref} path={params.get("path") ?? ""} onPath={(path) => update({ path: path || null })}
            onHistory={(path) => update({ path }, "commits")} onCommit={(c) => update({ commit: c }, "commits")} />
        )
      ) : null}
    </>
  );
}

function TreeNode({ repoId, gitRef, entry, depth, current, onPath }: { repoId: string; gitRef: string; entry: TreeEntry; depth: number; current: string; onPath: (p: string) => void }) {
  const inside = current === entry.path || current.startsWith(`${entry.path}/`);
  const [open, setOpen] = useState(inside);
  useEffect(() => { if (inside) setOpen(true); }, [inside]);
  const children = useGet<TreeEntry[]>(["tree", repoId, gitRef, entry.path], entry.isDirectory && open ? `/api/repos/${repoId}/tree${qs({ ref: gitRef, path: entry.path })}` : null);
  const expanded = open;
  return (
    <>
      <div className={`tree-item${current === entry.path ? " active" : ""}`} style={{ paddingLeft: 10 + depth * 14 }}
        onClick={() => { if (entry.isDirectory) setOpen(current === entry.path ? !open : true); onPath(entry.path); }}>
        {entry.isDirectory ? <span style={{ transform: expanded ? "rotate(90deg)" : undefined, display: "inline-flex", color: "var(--subtle)" }}><Icon name="chevron" size={12} /></span> : <span style={{ width: 12 }} />}
        <span style={{ color: entry.isDirectory ? "#c19c00" : "var(--muted)", display: "inline-flex" }}><Icon name={entry.isDirectory ? "folder" : "file"} /></span>
        <span className="truncate">{entry.name}</span>
      </div>
      {entry.isDirectory && expanded ? (children.data ?? []).map((c) => (
        <TreeNode key={c.path} repoId={repoId} gitRef={gitRef} entry={c} depth={depth + 1} current={current} onPath={onPath} />
      )) : null}
    </>
  );
}

function Files({ repoId, gitRef, path, onPath, onHistory, onCommit }: {
  repoId: string; gitRef: string; path: string; onPath: (p: string) => void; onHistory: (p: string) => void; onCommit: (c: string) => void;
}) {
  const root = useGet<TreeEntry[]>(["tree", repoId, gitRef, ""], `/api/repos/${repoId}/tree${qs({ ref: gitRef })}`);
  const parent = path.includes("/") ? path.slice(0, path.lastIndexOf("/")) : "";
  const parentEntries = useGet<TreeEntry[]>(["tree", repoId, gitRef, parent], path ? `/api/repos/${repoId}/tree${qs({ ref: gitRef, path: parent })}` : null);
  const entry = path ? parentEntries.data?.find((e) => e.path === path) : undefined;
  const isFile = !!path && entry !== undefined && !entry.isDirectory;
  const isDir = !path || entry?.isDirectory === true;
  const listing = useGet<TreeEntry[]>(["tree", repoId, gitRef, path], isDir && path ? `/api/repos/${repoId}/tree${qs({ ref: gitRef, path })}` : null);
  const entries = path ? listing.data : root.data;
  const readme = isDir ? entries?.find((e) => !e.isDirectory && /^readme(\.md|\.txt)?$/i.test(e.name)) : undefined;
  const last = useGet<GitCommit[]>(["commits", repoId, gitRef, path, 0, 1], `/api/repos/${repoId}/commits${qs({ ref: gitRef, path, take: 1 })}`);
  const segments = path ? path.split("/") : [];
  return (
    <div className="repo-layout">
      <nav className="tree" aria-label="Dosya ağacı">
        <div className={`tree-item${!path ? " active" : ""}`} onClick={() => onPath("")}><Icon name="repos" /> <strong>/</strong></div>
        {(root.data ?? []).map((e) => <TreeNode key={e.path} repoId={repoId} gitRef={gitRef} entry={e} depth={0} current={path} onPath={onPath} />)}
      </nav>
      <div className="stack" style={{ minWidth: 0 }}>
        <div className="row">
          <div className="breadcrumb">
            <a href="#" onClick={(e) => { e.preventDefault(); onPath(""); }}>root</a>
            {segments.map((s, i) => <span key={i} className="row nowrap" style={{ gap: 4 }}><span className="subtle">/</span>
              {i === segments.length - 1 ? <strong>{s}</strong> : <a href="#" onClick={(e) => { e.preventDefault(); onPath(segments.slice(0, i + 1).join("/")); }}>{s}</a>}</span>)}
          </div>
          <span className="spacer" />
          {path ? <button className="small" onClick={() => onHistory(path)}><Icon name="history" /> Geçmiş</button> : null}
        </div>
        {last.data?.[0] ? (
          <div className="card row small" style={{ padding: "8px 12px" }}>
            <Avatar name={last.data[0].authorName} /><strong>{last.data[0].authorName}</strong>
            <a href="#" className="truncate" style={{ flex: 1 }} onClick={(e) => { e.preventDefault(); onCommit(last.data![0]!.commitId); }}>{last.data[0].subject}</a>
            <span className="mono subtle">{last.data[0].commitId.slice(0, 8)}</span><span className="muted">{fmtAgo(last.data[0].authoredAt)}</span>
          </div>
        ) : null}
        {isFile ? <FileContent repoId={repoId} gitRef={gitRef} path={path} /> : null}
        {isDir && entries ? (
          <div className="table-card">
            <table>
              <thead><tr><th>Ad</th><th className="right">Boyut</th></tr></thead>
              <tbody>
                {[...entries].sort((a, b) => Number(b.isDirectory) - Number(a.isDirectory) || a.name.localeCompare(b.name)).map((e) => (
                  <tr key={e.path} className="clickable" onClick={() => onPath(e.path)}>
                    <td><span className="row nowrap"><span style={{ color: e.isDirectory ? "#c19c00" : "var(--muted)", display: "inline-flex" }}><Icon name={e.isDirectory ? "folder" : "file"} /></span>{e.name}</span></td>
                    <td className="right muted small">{e.isDirectory ? "" : fmtBytes(e.size)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
        {readme ? <FileContent repoId={repoId} gitRef={gitRef} path={readme.path} title={readme.name} /> : null}
        {(root.isLoading || listing.isLoading || parentEntries.isLoading) ? <Loading /> : null}
        <ErrorBox error={root.error ?? listing.error ?? parentEntries.error} />
      </div>
    </div>
  );
}

function FileContent({ repoId, gitRef, path, title }: { repoId: string; gitRef: string; path: string; title?: string }) {
  const blob = useGet<Blob>(["blob", repoId, gitRef, path], `/api/repos/${repoId}/file${qs({ ref: gitRef, path })}`);
  if (blob.isLoading) return <Loading />;
  if (blob.error) return <ErrorBox error={blob.error} />;
  if (!blob.data) return null;
  return (
    <div className="stack tight">
      <div className="row small muted">{title ? <strong style={{ color: "var(--text)" }}>{title}</strong> : null}<span>{fmtBytes(blob.data.size)}</span>
        {blob.data.isTruncated ? <span className="badge warn">ilk kısmı gösteriliyor</span> : null}</div>
      {blob.data.isBinary || blob.data.content === null ? <Empty>İkili dosya ({fmtBytes(blob.data.size)}) gösterilemiyor.</Empty> : <CodeView path={path} content={blob.data.content} />}
    </div>
  );
}

function CommitList({ repoId, gitRef, path, onOpen, onClearPath }: { repoId: string; gitRef: string; path: string | null; onOpen: (c: string) => void; onClearPath: () => void }) {
  const [take, setTake] = useState(50);
  const commits = useGet<GitCommit[]>(["commits", repoId, gitRef, path, 0, take], `/api/repos/${repoId}/commits${qs({ ref: gitRef, path, take })}`);
  return (
    <div className="stack">
      {path ? <div className="callout row"><Icon name="history" /> <span><strong>{path}</strong> dosyasının geçmişi</span><span className="spacer" /><button className="small" onClick={onClearPath}>Tüm commit'ler</button></div> : null}
      <ErrorBox error={commits.error} />
      {commits.isLoading ? <Loading /> : commits.data?.length === 0 ? <Empty>Commit yok.</Empty> : (
        <div className="table-card">
          <table>
            <thead><tr><th>Commit</th><th>Mesaj</th><th>Yazar</th><th>Tarih</th></tr></thead>
            <tbody>{(commits.data ?? []).map((c) => (
              <tr key={c.commitId} className="clickable" onClick={() => onOpen(c.commitId)}>
                <td className="mono"><a href="#" onClick={(e) => e.preventDefault()}>{c.commitId.slice(0, 8)}</a></td>
                <td><span className="truncate" style={{ display: "block", maxWidth: 640 }}>{c.subject}</span></td>
                <td><span className="row nowrap"><Avatar name={c.authorName} />{c.authorName}</span></td>
                <td className="muted small" title={fmtDate(c.authoredAt)}>{fmtAgo(c.authoredAt)}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
      {commits.data && commits.data.length >= take ? <div className="row"><span className="spacer" /><button onClick={() => setTake(take + 50)}>Daha fazla</button><span className="spacer" /></div> : null}
    </div>
  );
}

function CommitView({ repoId, commitId, onBack, onPath }: { repoId: string; commitId: string; onBack: () => void; onPath: (p: string) => void }) {
  const detail = useGet<CommitDetail>(["commit", repoId, commitId], `/api/repos/${repoId}/commits/${commitId}`);
  if (detail.isLoading) return <Loading />;
  if (detail.error) return <ErrorBox error={detail.error} />;
  const d = detail.data!;
  return (
    <div className="stack">
      <div className="card stack tight">
        <div className="row"><button className="small" onClick={onBack}>← Commit'ler</button><span className="spacer" /><span className="mono subtle">{d.commit.commitId}</span></div>
        <h2 style={{ margin: 0 }}>{d.commit.subject}</h2>
        {d.commit.message.trim() !== d.commit.subject ? <pre className="prewrap small muted" style={{ margin: 0 }}>{d.commit.message.slice(d.commit.subject.length).trim()}</pre> : null}
        <div className="row small"><Avatar name={d.commit.authorName} /><strong>{d.commit.authorName}</strong><span className="muted">{d.commit.authorEmail}</span>
          <span className="muted">· {fmtDate(d.commit.authoredAt)}</span><span className="spacer" /><DiffSummary files={d.files} />
          {d.commit.parentIds.length ? <span className="small muted">parent <span className="mono">{d.commit.parentIds.map((x) => x.slice(0, 8)).join(", ")}</span></span> : null}</div>
      </div>
      {d.isTruncated ? <div className="callout small">Değişiklik çok büyük; ilk dosyalar gösteriliyor.</div> : null}
      {d.files.map((f) => (
        <div key={f.path}>
          <FileDiff file={f} defaultOpen={d.files.length <= 20} />
          {f.status !== "delete" ? <div className="small" style={{ margin: "-8px 0 12px 4px" }}><a href="#" onClick={(e) => { e.preventDefault(); onPath(f.path); }}>Dosyayı görüntüle</a></div> : null}
        </div>
      ))}
    </div>
  );
}

function Branches({ detail, onOpen }: { detail: RepositoryDetail; onOpen: (b: string) => void }) {
  return (
    <div className="table-card">
      <table>
        <thead><tr><th>Branch</th><th>Son commit</th><th>Tarih</th></tr></thead>
        <tbody>{detail.branches.map((b) => (
          <tr key={b.name} className="clickable" onClick={() => onOpen(b.name)}>
            <td><span className="row nowrap"><Icon name="branch" /><strong>{b.name}</strong>
              {b.name === detail.repository.defaultBranch ? <span className="badge info">varsayılan</span> : null}
              {detail.repository.policy.protectedBranches.includes(b.name) ? <span className="badge warn">korumalı</span> : null}
              {b.name.startsWith("agent/") ? <span className="tag agent">agent</span> : null}</span></td>
            <td><span className="mono subtle">{b.commitId.slice(0, 8)}</span> <span className="truncate">{b.subject}</span></td>
            <td className="muted small">{fmtAgo(b.committedAt)}</td>
          </tr>
        ))}</tbody>
      </table>
    </div>
  );
}
