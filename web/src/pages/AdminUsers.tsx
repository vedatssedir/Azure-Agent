import { useState } from "react";
import { api, fmtDate, INVITATION_LABEL, type Invitation, type InvitationLink, type Me, type OrganizationRole, type OrgMember } from "../api";
import { ErrorBox, Field, Notice, useAction, useGet } from "../components/ui";
import { InvitationLinks } from "./Members";

export function AdminUsers({ me }: { me: Me }) {
  const members = useGet<OrgMember[]>(["org-members"], "/api/organizations/members");
  const [draft, setDraft] = useState<{ email: string; displayName: string; role: OrganizationRole; initialPassword: string }>({ email: "", displayName: "", role: "Member", initialPassword: "" });
  const [orgName, setOrgName] = useState("");
  const keys = [["org-members"]];
  const add = useAction(() => api("/api/organizations/members", { body: { ...draft, initialPassword: draft.initialPassword || null } }), keys);
  const active = useAction((m: OrgMember) => api(`/api/organizations/members/${m.userId}/active`, { method: "PUT", body: { isActive: !m.isActive } }), keys);
  const createOrg = useAction(() => api("/api/organizations", { body: { name: orgName, monthlyBudgetUsd: null } }), [["me"]]);
  return (
    <>
      <div className="page-head"><div><h1>Kullanıcılar</h1><div className="subtitle">Organizasyon üyeleri; proje rolleri her projenin Üyeler sekmesinden verilir.</div></div></div>
      <div className="stack">
        <section className="card">
          <ErrorBox error={members.error ?? active.error} />
          <table>
            <thead><tr><th>Kişi</th><th>Organizasyon rolü</th><th>Durum</th><th /></tr></thead>
            <tbody>{members.data?.map((m) => (
              <tr key={m.userId}>
                <td>{m.displayName}<div className="small muted">{m.email}</div></td>
                <td>{m.role}</td>
                <td><span className={`badge ${m.isActive ? "ok" : ""}`}>{m.isActive ? "Aktif" : "Pasif"}</span></td>
                <td className="right">{m.userId !== me.user.id ? <button onClick={() => active.run(m)}>{m.isActive ? "Pasifleştir" : "Aktifleştir"}</button> : null}</td>
              </tr>
            ))}</tbody>
          </table>
        </section>
        <section className="card form">
          <h2>Kullanıcı ekle</h2>
          <ErrorBox error={add.error} /><Notice text={add.message} />
          <div className="form-2">
            <Field label="Ad"><input value={draft.displayName} onChange={(e) => setDraft({ ...draft, displayName: e.target.value })} /></Field>
            <Field label="E-posta" hint="Azure DevOps kullanıcı adıyla aynı olması önerilir"><input type="email" value={draft.email} onChange={(e) => setDraft({ ...draft, email: e.target.value })} /></Field>
            <Field label="Rol">
              <select value={draft.role} onChange={(e) => setDraft({ ...draft, role: e.target.value as OrganizationRole })}>
                <option value="Member">Üye</option><option value="Admin">Yönetici</option><option value="Owner">Sahip</option>
              </select>
            </Field>
            <Field label="İlk şifre" hint="Yeni kullanıcılar için; en az 10 karakter, büyük/küçük harf ve rakam"><input type="password" value={draft.initialPassword} onChange={(e) => setDraft({ ...draft, initialPassword: e.target.value })} /></Field>
          </div>
          <div className="row"><span className="spacer" /><button className="primary" disabled={add.busy || !draft.email || !draft.displayName} onClick={async () => {
            await add.run(undefined, `${draft.displayName} eklendi.`);
            setDraft({ email: "", displayName: "", role: "Member", initialPassword: "" });
          }}>Ekle</button></div>
        </section>
        <Invitations />
        {me.user.isSystemAdmin ? (
          <section className="card form">
            <h2>Yeni organizasyon</h2>
            <ErrorBox error={createOrg.error} /><Notice text={createOrg.message} />
            <div className="row"><input placeholder="Organizasyon adı" value={orgName} onChange={(e) => setOrgName(e.target.value)} style={{ maxWidth: 320 }} />
              <button className="primary" disabled={!orgName || createOrg.busy} onClick={() => createOrg.run(undefined, "Organizasyon oluşturuldu; sol menüden geçiş yapabilirsiniz.")}>Oluştur</button></div>
          </section>
        ) : null}
      </div>
    </>
  );
}

function Invitations() {
  const invitations = useGet<Invitation[]>(["invitations"], "/api/organizations/invitations");
  const [draft, setDraft] = useState<{ email: string; displayName: string; role: OrganizationRole }>({ email: "", displayName: "", role: "Member" });
  const [links, setLinks] = useState<InvitationLink[]>([]);
  const keys = [["invitations"]];
  const invite = useAction(() => api<InvitationLink[]>("/api/organizations/invitations", { body: { people: [{ email: draft.email, displayName: draft.displayName }], role: draft.role } }), keys);
  const renew = useAction((id: string) => api<InvitationLink>(`/api/organizations/invitations/${id}/renew`, { body: {} }), keys);
  const revoke = useAction((id: string) => api(`/api/organizations/invitations/${id}`, { method: "DELETE" }), keys);
  return (
    <section className="card stack">
      <h2>Davetler</h2>
      <p className="muted small">Davet edilen kişi linkle şifre belirler ya da Microsoft ile girer. Azure takımlarından toplu davet için projenin Üyeler sekmesini kullanın.</p>
      <ErrorBox error={invitations.error ?? invite.error ?? renew.error ?? revoke.error} /><Notice text={invite.message} />
      <div className="row">
        <input placeholder="Ad" value={draft.displayName} onChange={(e) => setDraft({ ...draft, displayName: e.target.value })} style={{ maxWidth: 200 }} />
        <input type="email" placeholder="E-posta (Azure kullanıcı adı)" value={draft.email} onChange={(e) => setDraft({ ...draft, email: e.target.value })} style={{ maxWidth: 280 }} />
        <select value={draft.role} onChange={(e) => setDraft({ ...draft, role: e.target.value as OrganizationRole })} style={{ maxWidth: 160 }}>
          <option value="Member">Üye</option><option value="Admin">Yönetici</option><option value="Owner">Sahip</option>
        </select>
        <button className="primary" disabled={!draft.email || !draft.displayName || invite.busy} onClick={async () => {
          const r = await invite.run(undefined, (l) => l.length ? "Davet oluşturuldu." : "Bu kişinin zaten bekleyen bir daveti ya da hesabı var.");
          if (r) { setLinks(r); setDraft({ email: "", displayName: "", role: "Member" }); }
        }}>Davet et</button>
      </div>
      {links.length ? <InvitationLinks links={links} /> : null}
      {invitations.data?.length ? (
        <table>
          <thead><tr><th>Kişi</th><th>Rol</th><th>Durum</th><th>Son geçerlilik</th><th>Davet eden</th><th /></tr></thead>
          <tbody>{invitations.data.map((i) => (
            <tr key={i.id}>
              <td>{i.displayName}<div className="small muted">{i.email}</div></td>
              <td>{i.role}</td>
              <td><span className={`badge ${INVITATION_LABEL[i.status].cls}`}>{INVITATION_LABEL[i.status].label}</span></td>
              <td>{fmtDate(i.expiresAt)}</td>
              <td className="small muted">{i.invitedBy}</td>
              <td className="right">{i.status !== "Accepted" ? <div className="row" style={{ justifyContent: "flex-end" }}>
                <button onClick={async () => { const l = await renew.run(i.id); if (l) setLinks([l]); }}>Yeni link</button>
                {i.status === "Pending" ? <button className="danger" onClick={() => revoke.run(i.id)}>İptal</button> : null}
              </div> : null}</td>
            </tr>
          ))}</tbody>
        </table>
      ) : null}
    </section>
  );
}
