import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Navigate, NavLink, Route, Routes, useNavigate } from "react-router-dom";
import { QueryClient, QueryClientProvider, useQueryClient } from "@tanstack/react-query";
import "./styles.css";
import { session, type Me, type Project } from "./api";
import { useGet } from "./components/ui";
import { Login } from "./pages/Login";
import { Projects } from "./pages/Projects";
import { ProjectShell } from "./pages/ProjectShell";
import { AdminUsers } from "./pages/AdminUsers";
import { AdminSkills } from "./pages/AdminSkills";
import { AdminPrices } from "./pages/AdminPrices";

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: 1, staleTime: 10_000, refetchOnWindowFocus: false } } });

function Shell() {
  const [signedIn, setSignedIn] = useState(session.signedIn);
  const client = useQueryClient();
  useEffect(() => session.onSignOut(() => { client.clear(); setSignedIn(false); }), [client]);
  if (!signedIn) return <Login onSignedIn={() => setSignedIn(true)} />;
  return <SignedIn />;
}

function SignedIn() {
  const me = useGet<Me>(["me"], "/api/auth/me");
  const navigate = useNavigate();
  const client = useQueryClient();
  const organization = me.data?.organizations.find((o) => o.organizationId === session.organizationId) ?? me.data?.organizations[0];
  if (organization && session.organizationId !== organization.organizationId) session.organizationId = organization.organizationId;
  const projects = useGet<Project[]>(["projects", organization?.organizationId], organization ? "/api/projects" : null);

  if (me.isLoading) return null;
  if (me.error || !me.data) return <div className="login card error">{(me.error as Error)?.message ?? "Oturum açılamadı"}</div>;
  if (!organization) return <div className="login card">Hiçbir organizasyona üye değilsiniz. Yöneticinizden davet isteyin.</div>;
  const isOrgAdmin = organization.role !== "Member" || me.data.user.isSystemAdmin;

  return (
    <div className="layout">
      <nav className="sidebar">
        <div className="brand">Azure Agent</div>
        {me.data.organizations.length > 1 ? (
          <select value={organization.organizationId} onChange={(e) => { session.organizationId = e.target.value; client.clear(); navigate("/"); }}>
            {me.data.organizations.map((o) => <option key={o.organizationId} value={o.organizationId}>{o.name}</option>)}
          </select>
        ) : null}
        <NavLink to="/" end className={({ isActive }) => `nav-link${isActive ? " active" : ""}`}>Projeler</NavLink>
        {projects.data?.length ? <div className="nav-section">Projeler</div> : null}
        {projects.data?.map((project) => (
          <NavLink key={project.id} to={`/projects/${project.id}`} className={({ isActive }) => `nav-link${isActive ? " active" : ""}`}>
            {project.key} · {project.name}
          </NavLink>
        ))}
        {isOrgAdmin ? <>
          <div className="nav-section">Organizasyon</div>
          <NavLink to="/admin/users" className={({ isActive }) => `nav-link${isActive ? " active" : ""}`}>Kullanıcılar</NavLink>
          <NavLink to="/admin/skills" className={({ isActive }) => `nav-link${isActive ? " active" : ""}`}>Skill & MCP</NavLink>
          <NavLink to="/admin/prices" className={({ isActive }) => `nav-link${isActive ? " active" : ""}`}>Model fiyatları</NavLink>
        </> : null}
        <div className="sidebar-footer">
          <span>{me.data.user.displayName}</span>
          <span>{organization.name} · {organization.role}</span>
          <a href="#" onClick={(e) => { e.preventDefault(); void session.signOut(); }}>Çıkış yap</a>
        </div>
      </nav>
      <main className="main">
        <Routes>
          <Route path="/" element={<Projects canCreate={isOrgAdmin} />} />
          <Route path="/projects/:projectId/*" element={<ProjectShell me={me.data} />} />
          <Route path="/admin/users" element={<AdminUsers me={me.data} />} />
          <Route path="/admin/skills" element={<AdminSkills />} />
          <Route path="/admin/prices" element={<AdminPrices />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </div>
  );
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter><Shell /></BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
);
