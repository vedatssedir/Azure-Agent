import { useState } from "react";
import { api, p, ROLE_LABEL, type Member, type OrgMember, type ProjectRole } from "../api";
import { ErrorBox, useAction, useGet } from "../components/ui";
import type { ProjectPageProps } from "./ProjectShell";

const ROLE_HELP: Record<ProjectRole, string> = {
  Viewer: "Board, repo ve agent'ları görür",
  Developer: "Kendine iş atar; agent'ı onun adına kod yazar",
  Reviewer: "Developer + PR review başlatır",
  Maintainer: "Reviewer + agent, repo politikası, ayarlar ve maliyet",
  Admin: "Tüm yetkiler (secret'lar, bütçe, üyeler)",
};

export function Members({ project, can }: ProjectPageProps) {
  const members = useGet<Member[]>(["members", project.id], p(project.id, "/members"));
  const manage = can("project.members.manage");
  const orgMembers = useGet<OrgMember[]>(["org-members"], manage ? "/api/organizations/members" : null);
  const [draft, setDraft] = useState<{ userId: string; role: ProjectRole; azureUniqueName: string }>({ userId: "", role: "Developer", azureUniqueName: "" });
  const keys = [["members", project.id]];
  const save = useAction((m: { userId: string; role: ProjectRole; azureUniqueName: string | null }) =>
    api(p(project.id, `/members/${m.userId}`), { method: "PUT", body: { role: m.role, azureUniqueName: m.azureUniqueName || null } }), keys);
  const remove = useAction((userId: string) => api(p(project.id, `/members/${userId}`), { method: "DELETE" }), keys);
  const candidates = orgMembers.data?.filter((o) => !members.data?.some((m) => m.userId === o.userId)) ?? [];
  return (
    <div className="stack">
      <section className="card">
        <ErrorBox error={members.error ?? save.error ?? remove.error} />
        <table>
          <thead><tr><th>Kişi</th><th>Azure kimliği</th><th>Rol</th><th /></tr></thead>
          <tbody>{members.data?.map((m) => (
            <tr key={m.userId}>
              <td>{m.displayName}<div className="small muted">{m.email}</div></td>
              <td>{m.azureUniqueName ?? "—"}</td>
              <td>{manage ? (
                <select value={m.role} onChange={(e) => save.run({ userId: m.userId, role: e.target.value as ProjectRole, azureUniqueName: m.azureUniqueName })} style={{ maxWidth: 200 }}>
                  {(Object.keys(ROLE_LABEL) as ProjectRole[]).map((r) => <option key={r} value={r}>{ROLE_LABEL[r]}</option>)}
                </select>
              ) : ROLE_LABEL[m.role]}</td>
              <td className="right">{manage ? <button className="danger" onClick={() => remove.run(m.userId)}>Çıkar</button> : null}</td>
            </tr>
          ))}</tbody>
        </table>
      </section>
      {manage ? (
        <section className="card stack">
          <h2>Projeye kişi ekle</h2>
          <div className="row">
            <select value={draft.userId} onChange={(e) => setDraft({ ...draft, userId: e.target.value, azureUniqueName: orgMembers.data?.find((o) => o.userId === e.target.value)?.email ?? "" })} style={{ maxWidth: 260 }}>
              <option value="">Kişi seçin</option>
              {candidates.map((o) => <option key={o.userId} value={o.userId}>{o.displayName} ({o.email})</option>)}
            </select>
            <select value={draft.role} onChange={(e) => setDraft({ ...draft, role: e.target.value as ProjectRole })} style={{ maxWidth: 200 }}>
              {(Object.keys(ROLE_LABEL) as ProjectRole[]).map((r) => <option key={r} value={r}>{ROLE_LABEL[r]}</option>)}
            </select>
            <input placeholder="Azure kullanıcı adı (e-posta)" value={draft.azureUniqueName} onChange={(e) => setDraft({ ...draft, azureUniqueName: e.target.value })} style={{ maxWidth: 260 }} />
            <button className="primary" disabled={!draft.userId || save.busy} onClick={async () => { await save.run(draft); setDraft({ userId: "", role: "Developer", azureUniqueName: "" }); }}>Ekle</button>
          </div>
          <p className="small muted">Kişi listede yoksa önce Organizasyon → Kullanıcılar'dan ekleyin.</p>
        </section>
      ) : null}
      <section className="card">
        <h2>Roller</h2>
        <table><tbody>{(Object.keys(ROLE_HELP) as ProjectRole[]).map((r) => <tr key={r}><td><strong>{ROLE_LABEL[r]}</strong></td><td className="muted">{ROLE_HELP[r]}</td></tr>)}</tbody></table>
      </section>
    </div>
  );
}
