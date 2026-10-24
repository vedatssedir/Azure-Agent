import { useState } from "react";
import { Route, Routes, useNavigate, useParams } from "react-router-dom";
import { api, fmtDate, p, ROLE_LABEL, type AccessRule, type Commit, type Member, type ProjectRole, type PullRequest, type Repository, type RepositoryPolicy } from "../api";
import { Empty, ErrorBox, Field, Notice, useAction, useGet } from "../components/ui";
import { PullRequestTable } from "./PullRequests";
import type { ProjectPageProps } from "./ProjectShell";

export function Repositories(props: ProjectPageProps) {
  return (
    <Routes>
      <Route index element={<RepositoryList {...props} />} />
      <Route path=":repositoryId" element={<RepositoryDetail {...props} />} />
    </Routes>
  );
}

function RepositoryList({ project }: ProjectPageProps) {
  const repos = useGet<Repository[]>(["repos", project.id], p(project.id, "/repositories"));
  const navigate = useNavigate();
  return (
    <div className="card">
      <ErrorBox error={repos.error} />
      {repos.data?.length === 0 ? <Empty>Repo yok. Azure senkronu projedeki tüm repoları getirir.</Empty> : (
        <table>
          <thead><tr><th>Repo</th><th>Varsayılan branch</th><th>Açık PR</th><th>Son commit</th><th>Agent'lar</th><th>Oto review</th></tr></thead>
          <tbody>{repos.data?.map((r) => (
            <tr key={r.id} className="clickable" onClick={() => navigate(r.id)}>
              <td><strong>{r.name}</strong></td>
              <td>{r.defaultBranch ?? <span className="muted">boş repo</span>}</td>
              <td>{r.openPullRequests}</td>
              <td>{fmtDate(r.lastCommitAt)}</td>
              <td><span className={`badge ${r.policy.agentsEnabled ? "ok" : ""}`}>{r.policy.agentsEnabled ? "Açık" : "Kapalı"}</span></td>
              <td>{r.policy.autoReview ? "Evet" : "Hayır"}</td>
            </tr>
          ))}</tbody>
        </table>
      )}
    </div>
  );
}

function RepositoryDetail({ project, can }: ProjectPageProps) {
  const { repositoryId } = useParams();
  const repos = useGet<Repository[]>(["repos", project.id], p(project.id, "/repositories"));
  const repo = repos.data?.find((r) => r.id === repositoryId);
  const commits = useGet<Commit[]>(["commits", repositoryId], p(project.id, `/repositories/${repositoryId}/commits`));
  const prs = useGet<PullRequest[]>(["prs", project.id, repositoryId], p(project.id, `/pull-requests?repositoryId=${repositoryId}`));
  const [tab, setTab] = useState<"history" | "policy" | "access">("history");
  if (!repo) return <ErrorBox error={repos.error} />;
  return (
    <div className="stack">
      <div className="row">
        <a href=".." onClick={(e) => { e.preventDefault(); history.back(); }}>← Repolar</a>
        <h2 style={{ margin: 0 }}>{repo.name}</h2>
        <span className="spacer" />
        <a className="button" href={repo.webUrl} target="_blank" rel="noreferrer">Azure'da aç</a>
      </div>
      <div className="row">
        <button className={tab === "history" ? "primary" : ""} onClick={() => setTab("history")}>Geçmiş ve PR'lar</button>
        {can("repo.policy.manage") ? <>
          <button className={tab === "policy" ? "primary" : ""} onClick={() => setTab("policy")}>Agent politikası</button>
          <button className={tab === "access" ? "primary" : ""} onClick={() => setTab("access")}>Erişim kuralları</button>
        </> : null}
      </div>
      {tab === "history" ? (
        <div className="grid">
          <section className="card"><h2>Pull request'ler</h2><PullRequestTable rows={prs.data ?? []} showRepository={false} /></section>
          <section className="card">
            <h2>Commit geçmişi</h2>
            {commits.data?.length === 0 ? <div className="muted">Commit yok.</div> : (
              <table><tbody>{commits.data?.map((c) => (
                <tr key={c.commitId}>
                  <td><a href={c.webUrl} target="_blank" rel="noreferrer"><code>{c.commitId.slice(0, 8)}</code></a></td>
                  <td>{c.message.split("\n")[0]}<div className="small muted">{c.authorName} · {fmtDate(c.committedAt)}</div></td>
                </tr>
              ))}</tbody></table>
            )}
          </section>
        </div>
      ) : tab === "policy" ? <PolicyEditor projectId={project.id} repo={repo} /> : <AccessRules projectId={project.id} repositoryId={repo.id} />}
    </div>
  );
}

function PolicyEditor({ projectId, repo }: { projectId: string; repo: Repository }) {
  const [policy, setPolicy] = useState<RepositoryPolicy>(repo.policy);
  const save = useAction(() => api(p(projectId, `/repositories/${repo.id}/policy`), { method: "PUT", body: policy }), [["repos", projectId]]);
  const list = (value: string) => value.split(",").map((v) => v.trim()).filter(Boolean);
  return (
    <section className="card form">
      <ErrorBox error={save.error} /><Notice text={save.message} />
      <label className="check"><input type="checkbox" checked={policy.agentsEnabled} onChange={(e) => setPolicy({ ...policy, agentsEnabled: e.target.checked })} /> Agent'lar bu repoda çalışabilir</label>
      <label className="check"><input type="checkbox" checked={policy.autoReview} onChange={(e) => setPolicy({ ...policy, autoReview: e.target.checked })} /> Yeni PR'lara otomatik AI code review</label>
      <Field label="Korumalı branch'ler" hint="Agent'lar asla push edemez. Virgülle ayırın; release/* gibi joker desteklenir.">
        <input value={policy.protectedBranches.join(", ")} onChange={(e) => setPolicy({ ...policy, protectedBranches: list(e.target.value) })} />
      </Field>
      <Field label="İzin verilen komutlar" hint="Agent'ın build/test için çalıştırabileceği programlar">
        <input value={policy.allowedCommands.join(", ")} onChange={(e) => setPolicy({ ...policy, allowedCommands: list(e.target.value) })} />
      </Field>
      <Field label="Çalışma başına en fazla değişen dosya">
        <input type="number" min={1} max={1000} value={policy.maxFilesChangedPerRun} onChange={(e) => setPolicy({ ...policy, maxFilesChangedPerRun: Number(e.target.value) })} />
      </Field>
      <div className="row"><span className="spacer" /><button className="primary" disabled={save.busy} onClick={() => save.run(undefined, "Politika kaydedildi.")}>Kaydet</button></div>
    </section>
  );
}

const REPO_PERMISSIONS: Record<string, string> = {
  "repo.view": "Repoyu görme",
  "repo.agent.write": "Agent'ının bu repoya yazması",
  "repo.policy.manage": "Politika yönetimi",
  "pullrequests.review": "PR review başlatma",
};

function AccessRules({ projectId, repositoryId }: { projectId: string; repositoryId: string }) {
  const rules = useGet<AccessRule[]>(["access-rules", repositoryId], p(projectId, `/repositories/${repositoryId}/access-rules`));
  const members = useGet<Member[]>(["members", projectId], p(projectId, "/members"));
  const [draft, setDraft] = useState({ target: "role:Developer", permission: "repo.agent.write", effect: "Deny" });
  const key = [["access-rules", repositoryId]];
  const add = useAction(() => {
    const [kind, value] = draft.target.split(":");
    return api(p(projectId, `/repositories/${repositoryId}/access-rules`), {
      body: { userId: kind === "user" ? value : null, role: kind === "role" ? value : null, permission: draft.permission, effect: draft.effect },
    });
  }, key);
  const remove = useAction((id: string) => api(p(projectId, `/repositories/${repositoryId}/access-rules/${id}`), { method: "DELETE" }), key);
  const who = (r: AccessRule) => r.userId ? members.data?.find((m) => m.userId === r.userId)?.displayName ?? r.userId : `Rol: ${ROLE_LABEL[r.role as ProjectRole]}`;
  return (
    <section className="card stack">
      <p className="muted small">Proje rolünün izinlerini bu repo için açar veya kapatır. Yasak (Deny) her zaman izinden (Allow) önce gelir.</p>
      <ErrorBox error={add.error ?? remove.error ?? rules.error} />
      <table><tbody>
        {rules.data?.length === 0 ? <tr><td className="muted">Kural yok — proje rolleri geçerli.</td></tr> : null}
        {rules.data?.map((r) => (
          <tr key={r.id}>
            <td>{who(r)}</td><td>{REPO_PERMISSIONS[r.permission] ?? r.permission}</td>
            <td><span className={`badge ${r.effect === "Deny" ? "bad" : "ok"}`}>{r.effect === "Deny" ? "Yasak" : "İzin"}</span></td>
            <td className="right"><button className="danger" onClick={() => remove.run(r.id)}>Kaldır</button></td>
          </tr>
        ))}
      </tbody></table>
      <div className="row">
        <select value={draft.target} onChange={(e) => setDraft({ ...draft, target: e.target.value })} style={{ maxWidth: 220 }}>
          {(Object.keys(ROLE_LABEL) as ProjectRole[]).map((r) => <option key={r} value={`role:${r}`}>Rol: {ROLE_LABEL[r]}</option>)}
          {members.data?.map((m) => <option key={m.userId} value={`user:${m.userId}`}>{m.displayName}</option>)}
        </select>
        <select value={draft.permission} onChange={(e) => setDraft({ ...draft, permission: e.target.value })} style={{ maxWidth: 240 }}>
          {Object.entries(REPO_PERMISSIONS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </select>
        <select value={draft.effect} onChange={(e) => setDraft({ ...draft, effect: e.target.value })} style={{ maxWidth: 120 }}>
          <option value="Deny">Yasak</option><option value="Allow">İzin</option>
        </select>
        <button className="primary" disabled={add.busy} onClick={() => add.run(undefined)}>Kural ekle</button>
      </div>
    </section>
  );
}
