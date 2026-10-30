import type { ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { Bot, CircleDollarSign, FolderKanban, Loader, UserCheck } from "lucide-react";
import { fmtAgo, fmtUsd, ROLE_LABEL, type Agent, type RunSummary } from "../api";
import { useApp } from "../context";
import { Avatar, Empty, PageHead, RunBadge, useGet } from "../components/ui";
import { Icon } from "../components/icons";

const DAYS = 14;

function MetricCard({ to, label, value, hint, icon }: { to?: string; label: string; value: ReactNode; hint: string; icon: ReactNode }) {
  const body = <>
    <div className="flex items-center justify-between gap-2"><span className="label">{label}</span><span className="text-muted-foreground">{icon}</span></div>
    <div className="value">{value}</div>
    <p className="mt-1 text-xs text-muted-foreground">{hint}</p>
  </>;
  return to ? <Link to={to} className="stat">{body}</Link> : <div className="stat">{body}</div>;
}

/** Runs per day for the last two weeks, oldest first. */
function runsByDay(runs: RunSummary[]) {
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const days = Array.from({ length: DAYS }, (_, i) => {
    const day = new Date(today); day.setDate(today.getDate() - (DAYS - 1 - i));
    return { day, total: 0, failed: 0 };
  });
  for (const r of runs) {
    const at = new Date(r.createdAt); at.setHours(0, 0, 0, 0);
    const slot = days.find((d) => d.day.getTime() === at.getTime());
    if (!slot) continue;
    slot.total++;
    if (r.status === "Failed") slot.failed++;
  }
  return days;
}

function RunsChart({ runs }: { runs: RunSummary[] }) {
  const days = runsByDay(runs);
  const max = Math.max(1, ...days.map((d) => d.total));
  return (
    <div className="flex min-h-56 flex-1 items-end gap-1.5 pt-6" role="img" aria-label={`Son ${DAYS} günde ${days.reduce((s, d) => s + d.total, 0)} çalıştırma`}>
      {days.map((d) => {
        const label = d.day.toLocaleDateString("tr-TR", { day: "numeric", month: "short" });
        return (
          <div key={d.day.toISOString()} className="group flex h-full min-w-0 flex-1 flex-col items-center justify-end gap-1.5" title={`${label}: ${d.total} çalıştırma${d.failed ? `, ${d.failed} başarısız` : ""}`}>
            <span className="text-[11px] font-medium text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100">{d.total}</span>
            <div className="flex w-full max-w-7 flex-col justify-end overflow-hidden rounded-t-md" style={{ height: `${(d.total / max) * 100}%`, minHeight: d.total ? 4 : 2 }}>
              {d.failed ? <div className="bg-destructive/80" style={{ height: `${(d.failed / d.total) * 100}%` }} /> : null}
              <div className={d.total ? "flex-1 bg-primary" : "flex-1 bg-muted"} />
            </div>
            <span className="truncate text-[11px] text-muted-foreground">{d.day.getDate()}</span>
          </div>
        );
      })}
    </div>
  );
}

export function Home() {
  const { organization, projects, isAdmin, me } = useApp();
  const navigate = useNavigate();
  const agents = useGet<Agent[]>(["agents", "mine"], "/api/agents?mine=true");
  const runs = useGet<RunSummary[]>(["runs", "mine-recent"], "/api/runs?mine=true", { refetchInterval: 20_000 });
  const assigned = projects.reduce((s, x) => s + x.assignedToMe, 0);
  const active = runs.data?.filter((r) => r.status === "Running" || r.status === "Queued").length;
  const since = Date.now() - DAYS * 86_400_000;
  const recentCost = runs.data?.filter((r) => new Date(r.createdAt).getTime() >= since).reduce((s, r) => s + r.costUsd, 0);
  return (
    <>
      <PageHead title="Genel bakış" subtitle={`Hoş geldin, ${me.user.displayName} · ${organization.name}`} />
      <div className="stats mb-4">
        <MetricCard to="/mine" label="Bana atanan açık iş" value={assigned} hint={`${projects.length} projede`} icon={<UserCheck size={16} />} />
        <MetricCard to="/agents" label="Agent'larım" value={agents.data?.length ?? "—"} hint="Tanımlı agent" icon={<Bot size={16} />} />
        <MetricCard to="/runs" label="Çalışan agent" value={active ?? "—"} hint="Çalışıyor veya sırada" icon={<Loader size={16} />} />
        <MetricCard to="/costs" label={`Maliyet (${DAYS} gün)`} value={recentCost === undefined ? "—" : fmtUsd(recentCost)} hint="Son çalıştırmalarım" icon={<CircleDollarSign size={16} />} />
      </div>

      <div className="mb-8 grid gap-4 lg:grid-cols-7">
        <div className="card flex flex-col lg:col-span-4">
          <h2 className="mb-0">Çalıştırmalar</h2>
          <p className="text-sm text-muted-foreground">Son {DAYS} günde başlattığım çalıştırmalar; kırmızı kısım başarısız olanlar.</p>
          <RunsChart runs={runs.data ?? []} />
        </div>
        <div className="card lg:col-span-3">
          <div className="flex items-start justify-between gap-2">
            <div><h2 className="mb-0">Son çalıştırmalarım</h2><p className="text-sm text-muted-foreground">En yeni {Math.min(runs.data?.length ?? 0, 6)} çalıştırma</p></div>
            <Link to="/runs" className="text-sm font-medium">Tümü</Link>
          </div>
          {runs.data?.length ? (
            <ul className="mt-4 flex flex-col gap-1">
              {runs.data.slice(0, 6).map((r) => (
                <li key={r.id}>
                  <button className="ghost h-auto w-full justify-start gap-3 px-2 py-2 text-left font-normal" onClick={() => navigate(`/runs/${r.id}`)}>
                    <Avatar name={r.agentName} size="lg" />
                    <span className="grid min-w-0 flex-1 content-center leading-tight">
                      <span className="truncate text-sm font-medium">{r.agentName}</span>
                      <span className="truncate text-xs text-muted-foreground">
                        {r.workItemAzureId ? `#${r.workItemAzureId}` : r.kind === "Review" ? "Kod incelemesi" : "—"} · {fmtAgo(r.createdAt)}
                      </span>
                    </span>
                    <span className="flex flex-col items-end gap-1"><RunBadge status={r.status} /><span className="text-xs font-medium">{fmtUsd(r.costUsd)}</span></span>
                  </button>
                </li>
              ))}
            </ul>
          ) : <div className="mt-4"><Empty>Henüz çalıştırma yok. Board'dan bir maddeyi açıp "Agent'a geliştirt" deyin.</Empty></div>}
        </div>
      </div>

      <div className="section-title"><FolderKanban size={18} className="text-muted-foreground" /><h2>Projeler</h2></div>
      {projects.length === 0 ? (
        <Empty>
          {isAdmin ? <>Henüz senkronize edilmiş proje yok. <Link to="/settings/azure">Azure DevOps bağlantısını</Link> kontrol edin; senkron birkaç dakika sürebilir.</>
            : "Erişebildiğiniz proje yok. Azure DevOps'ta bir takıma eklendiğinizde projeleriniz burada görünür."}
        </Empty>
      ) : (
        <div className="grid cards">
          {projects.map((x) => (
            <div key={x.id} className="card hover stack tight" onClick={() => navigate(`/p/${x.id}/board`)}>
              <div className="row nowrap">
                <span className="avatar xl text-primary-foreground" style={{ background: "var(--primary)" }}>{x.name.slice(0, 1).toUpperCase()}</span>
                <div className="min-w-0">
                  <div className="truncate text-base font-semibold">{x.name}</div>
                  <div className="small muted">{x.myRole ? ROLE_LABEL[x.myRole] : "Erişim"} · senkron {fmtAgo(x.lastSyncedAt)}</div>
                </div>
              </div>
              {x.description ? <div className="small muted min-h-[18px]">{x.description}</div> : null}
              {x.lastSyncError ? <div className="small text-destructive">{x.lastSyncError}</div> : null}
              <div className="row small mt-1 border-t pt-3">
                <Link to={`/p/${x.id}/workitems`} className="inline-flex items-center gap-1 text-foreground" onClick={(e) => e.stopPropagation()}><Icon name="workitems" size={14} /> {x.openWorkItems} açık</Link>
                <Link to={`/p/${x.id}/workitems?who=me`} className="inline-flex items-center gap-1 text-foreground" onClick={(e) => e.stopPropagation()}><Icon name="mine" size={14} /> {x.assignedToMe} bana</Link>
                <Link to={`/p/${x.id}/repos/files`} className="inline-flex items-center gap-1 text-foreground" onClick={(e) => e.stopPropagation()}><Icon name="repos" size={14} /> {x.repositories} repo</Link>
                <span className="muted">{x.teams} takım</span>
                <span className="spacer" />
                <a href={x.webUrl} target="_blank" rel="noreferrer" className="text-muted-foreground" onClick={(e) => e.stopPropagation()} title="Azure DevOps'ta aç"><Icon name="external" size={14} /></a>
              </div>
            </div>
          ))}
        </div>
      )}
    </>
  );
}
