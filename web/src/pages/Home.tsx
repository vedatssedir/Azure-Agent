import { Link, useNavigate } from "react-router-dom";
import { fmtAgo, fmtUsd, ROLE_LABEL, type Agent, type RunSummary } from "../api";
import { useApp } from "../context";
import { Avatar, Empty, PageHead, RunBadge, useGet } from "../components/ui";
import { Icon } from "../components/icons";

export function Home() {
  const { organization, projects, isAdmin, me } = useApp();
  const navigate = useNavigate();
  const agents = useGet<Agent[]>(["agents", "mine"], "/api/agents?mine=true");
  const runs = useGet<RunSummary[]>(["runs", "mine-recent"], "/api/runs?mine=true", { refetchInterval: 20_000 });
  const assigned = projects.reduce((s, x) => s + x.assignedToMe, 0);
  return (
    <>
      <PageHead title={organization.name} subtitle={`Hoş geldiniz, ${me.user.displayName}`} />
      <div className="stats" style={{ marginBottom: 20 }}>
        <Link to="/mine" className="stat" style={{ color: "inherit" }}><div className="label">Bana atanan açık iş</div><div className="value">{assigned}</div></Link>
        <Link to="/agents" className="stat" style={{ color: "inherit" }}><div className="label">Agent'larım</div><div className="value">{agents.data?.length ?? "—"}</div></Link>
        <Link to="/runs" className="stat" style={{ color: "inherit" }}><div className="label">Çalışan agent</div><div className="value">{runs.data?.filter((r) => r.status === "Running" || r.status === "Queued").length ?? "—"}</div></Link>
        <div className="stat"><div className="label">Projeler</div><div className="value">{projects.length}</div></div>
      </div>
      <div className="section-title"><h2>Projeler</h2></div>
      {projects.length === 0 ? (
        <Empty>
          {isAdmin ? <>Henüz senkronize edilmiş proje yok. <Link to="/settings/azure">Azure DevOps bağlantısını</Link> kontrol edin; senkron birkaç dakika sürebilir.</>
            : "Erişebildiğiniz proje yok. Azure DevOps'ta bir takıma eklendiğinizde projeleriniz burada görünür."}
        </Empty>
      ) : (
        <div className="grid cards" style={{ marginBottom: 24 }}>
          {projects.map((x) => (
            <div key={x.id} className="card hover stack tight" onClick={() => navigate(`/p/${x.id}/board`)}>
              <div className="row nowrap">
                <span className="avatar xl" style={{ background: "#0078d4" }}>{x.name.slice(0, 1).toUpperCase()}</span>
                <div style={{ minWidth: 0 }}>
                  <div style={{ fontSize: 16, fontWeight: 600 }} className="truncate">{x.name}</div>
                  <div className="small muted">{x.myRole ? ROLE_LABEL[x.myRole] : "Erişim"} · senkron {fmtAgo(x.lastSyncedAt)}</div>
                </div>
              </div>
              {x.description ? <div className="small muted" style={{ minHeight: 18 }}>{x.description}</div> : null}
              {x.lastSyncError ? <div className="small" style={{ color: "var(--bad)" }}>{x.lastSyncError}</div> : null}
              <div className="row small">
                <Link to={`/p/${x.id}/workitems`} onClick={(e) => e.stopPropagation()}><Icon name="workitems" /> {x.openWorkItems} açık</Link>
                <Link to={`/p/${x.id}/workitems?who=me`} onClick={(e) => e.stopPropagation()}><Icon name="mine" /> {x.assignedToMe} bana</Link>
                <Link to={`/p/${x.id}/repos/files`} onClick={(e) => e.stopPropagation()}><Icon name="repos" /> {x.repositories} repo</Link>
                <span className="muted">{x.teams} takım</span>
                <span className="spacer" />
                <a href={x.webUrl} target="_blank" rel="noreferrer" onClick={(e) => e.stopPropagation()} title="Azure DevOps'ta aç"><Icon name="external" /></a>
              </div>
            </div>
          ))}
        </div>
      )}
      <div className="section-title"><h2>Son agent çalıştırmalarım</h2><span className="spacer" /><Link to="/runs">Tümü</Link></div>
      {runs.data?.length ? (
        <div className="table-card">
          <table>
            <thead><tr><th>Durum</th><th>Agent</th><th>İş</th><th>Maliyet</th><th>Başladı</th></tr></thead>
            <tbody>{runs.data.slice(0, 8).map((r) => (
              <tr key={r.id} className="clickable" onClick={() => navigate(`/runs/${r.id}`)}>
                <td><RunBadge status={r.status} /></td>
                <td><span className="row nowrap"><Avatar name={r.agentName} />{r.agentName}</span></td>
                <td>{r.workItemAzureId ? `#${r.workItemAzureId}` : r.kind === "Review" ? "Kod incelemesi" : "—"}</td>
                <td>{fmtUsd(r.costUsd)}</td>
                <td className="muted small">{fmtAgo(r.createdAt)}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      ) : <Empty>Henüz çalıştırma yok. Board'dan bir maddeyi açıp "Agent'a geliştirt" deyin.</Empty>}
    </>
  );
}
