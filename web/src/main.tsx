import { StrictMode, useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Link, Navigate, NavLink, Route, Routes, useLocation, useNavigate, useParams } from "react-router-dom";
import { QueryClient, QueryClientProvider, useQueryClient } from "@tanstack/react-query";
import "./styles.css";
import { session, type Me, type Project } from "./api";
import { AppContext, lastProject, useApp } from "./context";
import { Avatar, ErrorBox, Loading, useGet } from "./components/ui";
import { Icon } from "./components/icons";
import { cn } from "./lib/utils";
import { useTheme, type Theme } from "./lib/theme";
import { ChevronRight, ChevronsUpDown, LogOut, Monitor, Moon, PanelLeftClose, PanelLeftOpen, Sun, UserRound } from "lucide-react";
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

const NAV_KEY = "azure-agent.sidebar-collapsed";
const readCollapsed = () => { try { return localStorage.getItem(NAV_KEY) === "1"; } catch { return false; } };

function Layout({ loading }: { loading: boolean }) {
  const { me, organization, isAdmin, projects } = useApp();
  const project = useCurrentProject();
  const navigate = useNavigate();
  const location = useLocation();
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const [mobileOpen, setMobileOpen] = useState(false);
  const base = project ? `/p/${project.id}` : null;
  useEffect(() => setMobileOpen(false), [location.pathname]);
  const toggle = () => {
    if (window.matchMedia?.("(max-width: 767px)").matches) { setMobileOpen(!mobileOpen); return; }
    const next = !collapsed;
    setCollapsed(next);
    try { localStorage.setItem(NAV_KEY, next ? "1" : "0"); } catch { /* not remembered */ }
  };
  const nav = (to: string, icon: string, label: string, end = false) => (
    <NavLink to={to} end={end} title={collapsed ? label : undefined}
      className={({ isActive }) => cn(
        "flex h-8 items-center gap-2 rounded-md px-2 text-sm text-foreground/80 transition-colors hover:bg-sidebar-accent hover:text-foreground hover:no-underline [&>svg]:shrink-0",
        isActive && "bg-sidebar-accent font-medium text-foreground",
        collapsed && "md:justify-center md:px-0",
      )}>
      <Icon name={icon} /><span className={cn("truncate", collapsed && "md:hidden")}>{label}</span>
    </NavLink>
  );
  const group = (label: string, children: React.ReactNode) => (
    <div className="flex flex-col gap-0.5 p-2">
      <div className={cn("flex h-8 items-center px-2 text-xs font-medium text-muted-foreground", collapsed && "md:hidden")}>{label}</div>
      {children}
    </div>
  );
  const switchProject = (id: string) => {
    lastProject.set(id);
    const rest = location.pathname.match(/^\/p\/[0-9a-f-]{36}(\/(board|sprint|workitems|repos\/\w+|pullrequests))?/)?.[1] ?? "/board";
    navigate(`/p/${id}${rest.startsWith("/repos") ? "/repos/files" : rest.startsWith("/pullrequests") ? "/pullrequests" : rest}`);
  };
  return (
    <div className="flex h-full bg-sidebar">
      {mobileOpen ? <div className="fixed inset-0 z-40 bg-black/50 md:hidden" onClick={() => setMobileOpen(false)} /> : null}
      <aside aria-label="Gezinme" className={cn(
        "fixed inset-y-0 left-0 z-50 flex w-64 flex-col bg-sidebar transition-[width,transform] duration-200 md:static md:translate-x-0",
        mobileOpen ? "translate-x-0 border-r shadow-lg" : "-translate-x-full",
        collapsed && "md:w-14",
      )}>
        <div className="flex flex-col gap-2 p-2">
          <Link to="/" className={cn("flex items-center gap-2 rounded-md p-2 text-foreground hover:bg-sidebar-accent hover:no-underline", collapsed && "md:justify-center md:p-1.5")}>
            <span className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-primary text-primary-foreground"><Icon name="logo" /></span>
            <span className={cn("grid min-w-0 flex-1 content-center leading-tight", collapsed && "md:hidden")}>
              <span className="truncate text-sm font-semibold">Azure Agent</span>
              <span className="truncate text-xs text-muted-foreground">{organization.name}</span>
            </span>
          </Link>
          {projects.length > 0 && !collapsed ? (
            <select aria-label="Proje" className="h-9 bg-background font-medium shadow-xs" value={project?.id ?? ""} onChange={(e) => switchProject(e.target.value)}>
              {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          ) : null}
        </div>
        <nav className="min-h-0 flex-1 overflow-y-auto overflow-x-hidden">
          {group("Genel", <>
            {nav("/", "home", "Genel bakış", true)}
            {nav("/mine", "mine", "Bana atananlar")}
          </>)}
          {base ? <>
            {group("Boards", <>
              {nav(`${base}/board`, "boards", "Board")}
              {nav(`${base}/workitems`, "workitems", "Work items")}
              {nav(`${base}/sprint`, "sprint", "Sprint")}
            </>)}
            {group("Repos", <>
              {nav(`${base}/repos/files`, "files", "Dosyalar")}
              {nav(`${base}/repos/commits`, "commits", "Commit'ler")}
              {nav(`${base}/repos/branches`, "branch", "Branch'ler")}
              {nav(`${base}/pullrequests`, "pr", "Pull request'ler")}
            </>)}
          </> : null}
          {group("Agent'lar", <>
            {nav("/agents", "agents", "Agent'larım")}
            {nav("/runs", "runs", "Çalıştırmalar")}
            {nav("/costs", "costs", "Maliyet")}
          </>)}
          {isAdmin ? group("Yönetim", nav("/settings", "settings", "Ayarlar")) : null}
        </nav>
        <div className="p-2">
          <UserMenu name={me.user.displayName} email={me.user.email} compact={collapsed} />
        </div>
      </aside>
      <div className="flex min-w-0 flex-1 flex-col overflow-hidden bg-background md:m-2 md:ml-0 md:rounded-xl md:border md:shadow-xs">
        <header className="flex h-14 shrink-0 items-center gap-2 border-b px-4">
          <button className="ghost size-8 p-0" aria-label="Menüyü aç/kapat" onClick={toggle}>{collapsed ? <PanelLeftOpen size={16} /> : <PanelLeftClose size={16} />}</button>
          <span className="h-4 w-px bg-border" />
          <nav aria-label="Konum" className="flex min-w-0 items-center gap-1.5 text-sm text-muted-foreground">
            <Link to="/" className="truncate text-muted-foreground hover:text-foreground hover:no-underline">{organization.name}</Link>
            {project && location.pathname.startsWith("/p/") ? <>
              <ChevronRight size={14} className="shrink-0" />
              <Link to={`/p/${project.id}/board`} className="truncate font-medium text-foreground hover:no-underline">{project.name}</Link>
            </> : null}
          </nav>
          <span className="flex-1" />
          <ThemeToggle />
        </header>
        <main className="min-w-0 flex-1 overflow-auto px-4 py-6 md:px-8">
          <div className="mx-auto w-full max-w-[1600px]">
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
          </div>
          <WorkItemHost />
        </main>
      </div>
    </div>
  );
}

function ProjectRoute({ children }: { children: React.ReactNode }) {
  const { projectId } = useParams();
  const { projects } = useApp();
  if (!projects.some((p) => p.id === projectId)) return <Navigate to="/" replace />;
  return <>{children}</>;
}

function ThemeToggle() {
  const [theme, setTheme] = useTheme();
  const next: Record<Theme, Theme> = { light: "dark", dark: "system", system: "light" };
  const label: Record<Theme, string> = { light: "Açık tema", dark: "Koyu tema", system: "Sistem teması" };
  return (
    <button className="ghost size-8 p-0" title={label[theme]} aria-label={`${label[theme]} (değiştir)`} onClick={() => setTheme(next[theme])}>
      {theme === "light" ? <Sun size={16} /> : theme === "dark" ? <Moon size={16} /> : <Monitor size={16} />}
    </button>
  );
}

function UserMenu({ name, email, compact }: { name: string; email: string; compact: boolean }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", close);
    return () => document.removeEventListener("mousedown", close);
  }, []);
  return (
    <div className="relative" ref={ref}>
      <button aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(!open)}
        className={cn("ghost h-12 w-full justify-start gap-2 px-2 text-left font-normal hover:bg-sidebar-accent", compact && "md:justify-center md:px-0")}>
        <Avatar name={name} size="lg" />
        <span className={cn("grid min-w-0 flex-1 content-center leading-tight", compact && "md:hidden")}>
          <span className="truncate text-sm font-semibold">{name}</span>
          <span className="truncate text-xs text-muted-foreground">{email}</span>
        </span>
        <ChevronsUpDown size={16} className={cn("shrink-0 text-muted-foreground", compact && "md:hidden")} />
      </button>
      {open ? (
        <div className="menu bottom-full left-0 right-auto top-auto mb-2 w-60" role="menu">
          <div className="px-2 py-1.5 text-sm"><div className="font-semibold">{name}</div><div className="truncate text-xs text-muted-foreground">{email}</div></div>
          <div className="-mx-1 my-1 h-px bg-border" />
          <Link to="/account" onClick={() => setOpen(false)}><UserRound size={16} /> Hesabım ve Azure bağlantım</Link>
          <div className="-mx-1 my-1 h-px bg-border" />
          <button onClick={() => session.signOut()}><LogOut size={16} /> Çıkış yap</button>
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
