import { useState } from "react";
import { api, type Me, type OrganizationRole, type OrgMember } from "../api";
import { ErrorBox, Field, Notice, useAction, useGet } from "../components/ui";

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
