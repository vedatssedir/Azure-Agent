import { useState } from "react";
import { api, INVITATION_LABEL, p, ROLE_LABEL, SOURCE_LABEL, type InvitationLink, type Member, type OrgMember, type ProjectPerson, type ProjectRole, type ProvisioningResult } from "../api";
import { Empty, ErrorBox, Notice, useAction, useGet } from "../components/ui";
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
      <AzurePeople projectId={project.id} manage={manage} />
      <section className="card">
        <h2>Proje üyeleri</h2>
        <ErrorBox error={members.error ?? save.error ?? remove.error} />
        <table>
          <thead><tr><th>Kişi</th><th>Azure kimliği</th><th>Kaynak</th><th>Rol</th><th /></tr></thead>
          <tbody>{members.data?.map((m) => (
            <tr key={m.userId}>
              <td>{m.displayName}<div className="small muted">{m.email}</div></td>
              <td>{m.azureUniqueName ?? "—"}</td>
              <td><span className="badge" title={m.source === "Manual" ? "Elle verildi; takım senkronu değiştirmez" : "Senkronla yönetilir; rolü değiştirirseniz elle sabitlenir"}>{SOURCE_LABEL[m.source]}</span></td>
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

/** The project's Azure teams: who has an account, membership, Azure connection and agent; invite the rest. */
function AzurePeople({ projectId, manage }: { projectId: string; manage: boolean }) {
  const people = useGet<ProjectPerson[]>(["people", projectId], p(projectId, "/people"));
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [links, setLinks] = useState<InvitationLink[]>([]);
  const keys = [["people", projectId], ["members", projectId]];
  const provision = useAction(() => api<ProvisioningResult>(p(projectId, "/people/provision"), { body: {} }), keys);
  const invite = useAction((list: ProjectPerson[]) => api<InvitationLink[]>(p(projectId, "/people/invite"), {
    body: { people: list.map((x) => ({ email: x.uniqueName, displayName: x.displayName })) },
  }), keys);
  const invitable = (people.data ?? []).filter((x) => !x.userId && x.uniqueName.includes("@") && x.invitationStatus !== "Pending");
  const toggle = (name: string) => setSelected((s) => { const n = new Set(s); if (n.has(name)) n.delete(name); else n.add(name); return n; });
  return (
    <section className="card stack">
      <div className="row">
        <h2>Azure takımları</h2><span className="spacer" />
        {manage ? <button disabled={provision.busy} onClick={() => provision.run(undefined, (r) => `Eşitlendi: ${r.added} eklendi, ${r.updated} güncellendi, ${r.removed} çıkarıldı, ${r.invited} davet.`)}>Üyelikleri eşitle</button> : null}
      </div>
      <p className="muted small">
        Azure DevOps takımlarındaki kişiler. Platform hesabı olanlar her senkronda takım → rol eşlemesine göre projeye eklenir; takımdan ayrılınca çıkarılır.
        Hesabı olmayanları davet edin: davet linkiyle şifre belirler ya da Microsoft ile girerler.
      </p>
      <ErrorBox error={people.error ?? provision.error ?? invite.error} /><Notice text={provision.message ?? invite.message} />
      {people.data?.length === 0 ? <Empty>Henüz takım üyesi yok. Ayarlar → Şimdi senkronla.</Empty> : (
        <table>
          <thead><tr>{manage ? <th /> : null}<th>Kişi</th><th>Takımlar</th><th>Eşlenen rol</th><th>Platform</th><th>Azure bağlantısı</th><th>Agent</th></tr></thead>
          <tbody>{people.data?.map((x) => (
            <tr key={x.uniqueName}>
              {manage ? <td>{invitable.includes(x) ? <input type="checkbox" aria-label={`${x.displayName} seç`} checked={selected.has(x.uniqueName)} onChange={() => toggle(x.uniqueName)} /> : null}</td> : null}
              <td>{x.displayName}<div className="small muted">{x.uniqueName}</div></td>
              <td>{x.teams.join(", ")}</td>
              <td>{x.mappedRole ? ROLE_LABEL[x.mappedRole] : <span className="muted small">eşleme yok</span>}</td>
              <td>{x.role ? <span className="badge ok">{ROLE_LABEL[x.role]}</span>
                : x.userId ? <span className="badge">Hesap var, üye değil</span>
                : x.invitationStatus ? <span className={`badge ${INVITATION_LABEL[x.invitationStatus].cls}`}>{INVITATION_LABEL[x.invitationStatus].label}</span>
                : <span className="badge">Hesap yok</span>}</td>
              <td>{x.userId ? (x.azureConnected ? <span className="badge ok">Bağlı</span> : <span className="muted small">bağlı değil</span>) : "—"}</td>
              <td>{x.agentName ?? <span className="muted small">yok</span>}</td>
            </tr>
          ))}</tbody>
        </table>
      )}
      {manage && invitable.length > 0 ? (
        <div className="row">
          <button onClick={() => setSelected(new Set(invitable.map((x) => x.uniqueName)))}>Hesabı olmayanların hepsini seç</button>
          <button className="primary" disabled={selected.size === 0 || invite.busy} onClick={async () => {
            const result = await invite.run(invitable.filter((x) => selected.has(x.uniqueName)), (r) => `${r.length} davet oluşturuldu.`);
            if (result) { setLinks(result); setSelected(new Set()); }
          }}>Seçilenleri davet et ({selected.size})</button>
        </div>
      ) : null}
      {links.length ? <InvitationLinks links={links} /> : null}
    </section>
  );
}

export function InvitationLinks({ links }: { links: InvitationLink[] }) {
  return (
    <div className="card">
      <h3>Davet linkleri</h3>
      <p className="small muted">Linkler yalnızca şimdi gösterilir (7 gün geçerli). E-posta gönderimi yapılandırılmadıysa linki kişiye iletin.</p>
      <table><tbody>{links.map((l) => (
        <tr key={l.id}>
          <td>{l.email}{l.emailSent ? <div className="small muted">e-posta gönderildi</div> : null}</td>
          <td><code className="small">{l.url}</code></td>
          <td className="right"><button onClick={() => navigator.clipboard?.writeText(l.url)}>Kopyala</button></td>
        </tr>
      ))}</tbody></table>
    </div>
  );
}
