import { useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api, fmtDate, fmtUsd, type Project } from "../api";
import { Drawer, Empty, ErrorBox, Field, useAction, useGet } from "../components/ui";

export function Projects({ canCreate }: { canCreate: boolean }) {
  const projects = useGet<Project[]>(["projects"], "/api/projects");
  const [creating, setCreating] = useState(false);
  return (
    <>
      <div className="page-head">
        <div><h1>Projeler</h1><div className="subtitle">Her proje bir Azure DevOps projesine bağlıdır.</div></div>
        {canCreate ? <button className="primary" onClick={() => setCreating(true)}>Yeni proje</button> : null}
      </div>
      <ErrorBox error={projects.error} />
      {projects.data?.length === 0 ? <div className="card"><Empty>Henüz proje yok.{canCreate ? " Yeni proje ile başlayın." : " Bir yöneticinin sizi projeye eklemesi gerekiyor."}</Empty></div> : null}
      <div className="grid">
        {projects.data?.map((project) => (
          <Link key={project.id} to={`/projects/${project.id}`} className="card stack" style={{ color: "inherit", textDecoration: "none" }}>
            <div className="row"><strong>{project.name}</strong><span className="badge">{project.key}</span></div>
            <div className="small muted">{project.azureOrganizationUrl}/{project.azureProjectName}</div>
            <div className="row small">
              <span className="muted">Son senkron: {fmtDate(project.lastSyncedAt)}</span>
              {project.lastSyncError ? <span className="badge bad">Senkron hatası</span> : null}
              <span className="spacer" />
              <span className="muted">Bütçe: {fmtUsd(project.monthlyBudgetUsd)}</span>
            </div>
          </Link>
        ))}
      </div>
      {creating ? <CreateProject onClose={() => setCreating(false)} /> : null}
    </>
  );
}

function CreateProject({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate();
  const [form, setForm] = useState({ name: "", key: "", description: "", azureOrganizationUrl: "https://dev.azure.com/", azureProjectName: "", monthlyBudgetUsd: "", pat: "" });
  const create = useAction(async () => {
    const project = await api<Project>("/api/projects", { body: { ...form, monthlyBudgetUsd: form.monthlyBudgetUsd ? Number(form.monthlyBudgetUsd) : null } });
    if (form.pat) await api(`/api/projects/${project.id}/secrets/AZURE_DEVOPS_PAT`, { method: "PUT", body: { value: form.pat } });
    return project;
  }, [["projects"]]);
  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: e.target.value });
  return (
    <Drawer title="Yeni proje" onClose={onClose}>
      <ErrorBox error={create.error} />
      <div className="form">
        <div className="form-2">
          <Field label="Ad"><input value={form.name} onChange={set("name")} /></Field>
          <Field label="Anahtar" hint="2-10 büyük harf/rakam, örn. SHOP"><input value={form.key} onChange={(e) => setForm({ ...form, key: e.target.value.toUpperCase() })} /></Field>
        </div>
        <Field label="Açıklama"><input value={form.description} onChange={set("description")} /></Field>
        <Field label="Azure DevOps organizasyon URL'i" hint="https://dev.azure.com/<organizasyon>"><input value={form.azureOrganizationUrl} onChange={set("azureOrganizationUrl")} /></Field>
        <Field label="Azure DevOps proje adı"><input value={form.azureProjectName} onChange={set("azureProjectName")} /></Field>
        <Field label="Personal access token" hint="Work Items, Code ve Project and Team (okuma/yazma). Şifreli saklanır."><input type="password" value={form.pat} onChange={set("pat")} /></Field>
        <Field label="Aylık bütçe (USD)" hint="Boş = limitsiz"><input type="number" min={0} value={form.monthlyBudgetUsd} onChange={set("monthlyBudgetUsd")} /></Field>
        <div className="row">
          <button onClick={onClose}>Vazgeç</button><span className="spacer" />
          <button className="primary" disabled={create.busy} onClick={async () => {
            const project = await create.run(undefined);
            if (project) navigate(`/projects/${project.id}/settings`);
          }}>Oluştur</button>
        </div>
      </div>
    </Drawer>
  );
}
