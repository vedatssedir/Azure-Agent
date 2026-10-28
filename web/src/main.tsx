import { StrictMode, useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Link, Navigate, NavLink, Route, Routes, useLocation, useNavigate, useParams } from "react-router-dom";
import { QueryClient, QueryClientProvider, useQueryClient } from "@tanstack/react-query";
import "./styles.css";
import { session, type Me, type Project } from "./api";
import { AppContext, lastProject, useApp } from "./context";
import { Avatar, ErrorBox, Loading, useGet } from "./components/ui";
import { Icon } from "./components/icons";
import { AcceptInvitation, AuthCallback, AuthPages } from "./pages/Login";
import { Account } from "./pages/Account";
import { Home } from "./pages/Home";
import { MyWork } from "./pages/MyWork";
import { WorkItems } from "./pages/WorkItems";
import { BoardPage } from "./pages/Board";
import { SprintPage } from "./pages/Sprint";
import { Repos } from "./pages/Repos";
import { PullRequests, PullRequestPage } from "./pages/PullRequests";
import { Agents } from "./pages/Agents";
import { Runs, RunPage } from "./pages/Runs";
import { Costs } from "./pages/Costs";
import { SettingsPage } from "./pages/Settings";
import { WorkItemHost } from "./components/WorkItemForm";

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: 1, staleTime: 10_000, refetchOnWindowFocus: false } } });

function Root() {
  const [signedIn, setSignedIn] = useState(session.signedIn);
  const client = useQueryClient();
  useEffect(() => session.onSignOut(() => { client.clear(); setSignedIn(false); }), [client]);
  const onSignedIn = () => { client.clear(); setSignedIn(true); };
  return (
    <Routes>
      {/* Public pages: the Entra ID return URL and invitation links work signed in or not. */}
      <Route path="/auth/callback" element={<AuthCallback onSignedIn={onSignedIn} />} />
      <Route path="/invite/:token" element={<AcceptInvitation onSignedIn={onSignedIn} />} />
      <Route path="*" element={signedIn ? <SignedIn /> : <AuthPages onSignedIn={onSignedIn} />} />
    </Routes>
  );
}

function SignedIn() {
  const me = useGet<Me>(["me"], "/api/auth/me");
  const organization = me.data?.organizations.find((o) => o.organizationId === session.organizationId) ?? me.data?.organizations[0];
  if (organization && session.organizationId !== organization.organizationId) session.organizationId = organization.organizationId;
  const projects = useGet<Project[]>(["projects"], organization ? "/api/projects" : null, { refetchInterval: 60_000 });
  if (me.error) return <div className="auth"><div className="auth-card"><ErrorBox error={me.error} /><button onClick={() => session.signOut()}>Çıkış yap</button></div></div>;
  if (!me.data) return <Loading />;
  if (!organization) {
    return (
      <div className="auth"><div className="auth-card">
        <h2>Organizasyon yok</h2>
        <p className="muted">Hesabınız henüz bir organizasyona bağlı değil. Yöneticinizden davet isteyin.</p>
        <button onClick={() => session.signOut()}>Çıkış yap</button>
      </div></div>
    );
  }
  const isAdmin = me.data.user.isSystemAdmin || organization.role === "Admin" || organization.role === "Owner";
  return (
    <AppContext.Provider value={{ me: me.data, organization, isAdmin, projects: projects.data ?? [] }}>
      <Layout loading={projects.isLoading} />
    </AppContext.Provider>
  );
}

/** Resolves the project in the URL (or the last one used) for the left navigation. */
function useCurrentProject(): Project | null {
  const { projects } = useApp();
  const location = useLocation();
  const match = location.pathname.match(/^\/p\/([0-9a-f-]{36})/);
  const id = match?.[1] ?? lastProject.get();
  const project = projects.find((p) => p.id === id) ?? projects[0] ?? null;
  useEffect(() => { if (match?.[1]) lastProject.set(match[1]); }, [match?.[1]]);
  return project;
}

function Layout({ loading }: { loading: boolean }) {
  const { me, organization, isAdmin, projects } = useApp();
  const project = useCurrentProject();
  const navigate = useNavigate();
  const location = useLocation();
  const base = project ? `/p/${project.id}` : null;
  const nav = (to: string, icon: string, label: string, sub = false, end = false) => (
    <NavLink to={to} end={end} className={({ isActive }) => `nav-item${sub ? " nav-sub" : ""}${isActive ? " active" : ""}`}><Icon name={icon} />{label}</NavLink>
  );
  const switchProject = (id: string) => {
    lastProject.set(id);
    const rest = location.pathname.match(/^\/p\/[0-9a-f-]{36}(\/(board|sprint|workitems|repos\/\w+|pullrequests))?/)?.[1] ?? "/board";
    navigate(`/p/${id}${rest.startsWith("/repos") ? "/repos/files" : rest.startsWith("/pullrequests") ? "/pullrequests" : rest}`);
  };
  return (
    <div className="app">
      <header className="topbar">
        <Link to="/" className="logo"><Icon name="logo" size={20} /> Azure Agent</Link>
        <nav className="crumbs" aria-label="Konum">
          <span>/</span><Link to="/">{organization.name}</Link>
          {project && location.pathname.startsWith("/p/") ? <><span>/</span><Link to={`/p/${project.id}/board`}>{project.name}</Link></> : null}
        </nav>
        <span className="spacer" />
        <UserMenu name={me.user.displayName} email={me.user.email} />
      </header>
      <aside className="nav">
        {projects.length > 0 ? (
          <div className="project-switch">
            <select aria-label="Proje" value={project?.id ?? ""} onChange={(e) => switchProject(e.target.value)}>
              {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </div>
        ) : null}
        {nav("/", "home", "Genel bakış", false, true)}
        {nav("/mine", "mine", "Bana atananlar")}
        {base ? <>
          <div className="nav-section">Boards</div>
          {nav(`${base}/board`, "boards", "Board")}
          {nav(`${base}/workitems`, "workitems", "Work items")}
          {nav(`${base}/sprint`, "sprint", "Sprint")}
          <div className="nav-section">Repos</div>
          {nav(`${base}/repos/files`, "files", "Dosyalar")}
          {nav(`${base}/repos/commits`, "commits", "Commit'ler")}
          {nav(`${base}/repos/branches`, "branch", "Branch'ler")}
          {nav(`${base}/pullrequests`, "pr", "Pull request'ler")}
        </> : null}
        <div className="nav-section">Agent'lar</div>
        {nav("/agents", "agents", "Agent'larım")}
        {nav("/runs", "runs", "Çalıştırmalar")}
        {nav("/costs", "costs", "Maliyet")}
        <div className="bottom">
          {isAdmin ? nav("/settings", "settings", "Ayarlar") : null}
        </div>
      </aside>
      <main className="main">
        {loading ? <Loading /> : (
          <Routes>
            <Route path="/" element={<Home />} />
            <Route path="/mine" element={<MyWork />} />
            <Route path="/p/:projectId" element={<Navigate to="board" replace />} />
            <Route path="/p/:projectId/board" element={<ProjectRoute><BoardPage /></ProjectRoute>} />
            <Route path="/p/:projectId/workitems" element={<ProjectRoute><WorkItems /></ProjectRoute>} />
            <Route path="/p/:projectId/sprint" element={<ProjectRoute><SprintPage /></ProjectRoute>} />
            <Route path="/p/:projectId/repos/:view" element={<ProjectRoute><Repos /></ProjectRoute>} />
            <Route path="/p/:projectId/pullrequests" element={<ProjectRoute><PullRequests /></ProjectRoute>} />
            <Route path="/p/:projectId/pullrequests/:pullRequestId" element={<ProjectRoute><PullRequestPage /></ProjectRoute>} />
            <Route path="/agents" element={<Agents />} />
            <Route path="/runs" element={<Runs />} />
            <Route path="/runs/:runId" element={<RunPage />} />
            <Route path="/costs" element={<Costs />} />
            <Route path="/settings" element={isAdmin ? <SettingsPage /> : <Navigate to="/" replace />} />
            <Route path="/settings/:tab" element={isAdmin ? <SettingsPage /> : <Navigate to="/" replace />} />
            <Route path="/account" element={<Account />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        )}
        <WorkItemHost />
      </main>
    </div>
  );
}

function ProjectRoute({ children }: { children: React.ReactNode }) {
  const { projectId } = useParams();
  const { projects } = useApp();
  if (!projects.some((p) => p.id === projectId)) return <Navigate to="/" replace />;
  return <>{children}</>;
}

function UserMenu({ name, email }: { name: string; email: string }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", close);
    return () => document.removeEventListener("mousedown", close);
  }, []);
  return (
    <div className="user" ref={ref}>
      <button className="ghost" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(!open)}><Avatar name={name} /> {name}</button>
      {open ? (
        <div className="menu" role="menu">
          <div style={{ padding: "8px 12px" }}><strong>{name}</strong><div className="small muted">{email}</div></div>
          <Link to="/account" onClick={() => setOpen(false)}>Hesabım ve Azure bağlantım</Link>
          <button onClick={() => session.signOut()}>Çıkış yap</button>
        </div>
      ) : null}
    </div>
  );
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter><Root /></BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
);
