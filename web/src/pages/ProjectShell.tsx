import { NavLink, Navigate, Route, Routes, useParams } from "react-router-dom";
import type { Me, Project } from "../api";
import { ErrorBox, useGet } from "../components/ui";
import { Board } from "./Board";
import { Repositories } from "./Repositories";
import { PullRequests } from "./PullRequests";
import { Agents } from "./Agents";
import { Runs } from "./Runs";
import { Costs } from "./Costs";
import { Members } from "./Members";
import { ProjectSettingsPage } from "./ProjectSettings";
import { Audit } from "./Audit";

export interface ProjectPageProps {
  project: Project;
  me: Me;
  can: (permission: string) => boolean;
}

const TABS: Array<{ path: string; label: string; permission: string; element: (props: ProjectPageProps) => React.ReactNode }> = [
  { path: "board", label: "Board", permission: "workitems.view", element: (p) => <Board {...p} /> },
  { path: "repos", label: "Repolar", permission: "repo.view", element: (p) => <Repositories {...p} /> },
  { path: "pull-requests", label: "Pull request'ler", permission: "project.view", element: (p) => <PullRequests {...p} /> },
  { path: "agents", label: "Agent'lar", permission: "agents.view", element: (p) => <Agents {...p} /> },
  { path: "runs", label: "Çalışmalar", permission: "agents.view", element: (p) => <Runs {...p} /> },
  { path: "costs", label: "Maliyet", permission: "costs.view", element: (p) => <Costs {...p} /> },
  { path: "members", label: "Üyeler", permission: "project.view", element: (p) => <Members {...p} /> },
  { path: "settings", label: "Ayarlar", permission: "project.settings.manage", element: (p) => <ProjectSettingsPage {...p} /> },
  { path: "audit", label: "Denetim", permission: "audit.view", element: (p) => <Audit {...p} /> },
];

export function ProjectShell({ me }: { me: Me }) {
  const { projectId } = useParams();
  const project = useGet<Project>(["project", projectId], `/api/projects/${projectId}`);
  if (project.error) return <ErrorBox error={project.error} />;
  if (!project.data) return null;
  const permissions = new Set(project.data.myPermissions);
  const can = (permission: string) => permissions.has(permission);
  const props = { project: project.data, me, can };
  const tabs = TABS.filter((t) => can(t.permission));

  return (
    <>
      <div className="page-head">
        <div>
          <h1>{project.data.name} <span className="badge">{project.data.key}</span></h1>
          <div className="subtitle">{project.data.azureOrganizationUrl}/{project.data.azureProjectName}</div>
        </div>
        {project.data.lastSyncError ? <span className="badge bad" title={project.data.lastSyncError}>Son senkron başarısız</span> : null}
      </div>
      <nav className="tabs">
        {tabs.map((t) => <NavLink key={t.path} to={t.path} className={({ isActive }) => `tab${isActive ? " active" : ""}`}>{t.label}</NavLink>)}
      </nav>
      <Routes>
        {tabs.map((t) => <Route key={t.path} path={`${t.path}/*`} element={t.element(props)} />)}
        <Route path="*" element={<Navigate to={tabs[0]?.path ?? "board"} replace />} />
      </Routes>
    </>
  );
}
