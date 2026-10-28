import { useParams, useSearchParams } from "react-router-dom";
import { fmtDay, p, qs, type Sprint, type Team } from "../api";
import { Empty, ErrorBox, Loading, PageHead, Stat, useGet } from "../components/ui";
import { WorkItemTable } from "../components/WorkItemTable";
import { WorkItemCard } from "../components/WorkItemCard";
import { useOpenWorkItem } from "../components/WorkItemForm";

/** Sprint (iteration) view: the team's items in the selected iteration, as a list and grouped by state category. */
export function SprintPage() {
  const { projectId = "" } = useParams();
  const [params, setParams] = useSearchParams();
  const team = params.get("team");
  const iteration = params.get("iteration");
  const view = params.get("view") ?? "taskboard";
  const set = (key: string, value: string | null) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); setParams(next); };
  const teams = useGet<Team[]>(["teams", projectId], p(projectId, "/teams"));
  const sprint = useGet<Sprint>(["sprint", projectId, team, iteration], p(projectId, `/sprint${qs({ team, iteration })}`), { refetchInterval: 30_000 });
  const open = useOpenWorkItem();
  const items = sprint.data?.items ?? [];
  const categories = ["Proposed", "InProgress", "Resolved", "Completed"];
  const label: Record<string, string> = { Proposed: "Yapılacak", InProgress: "Devam ediyor", Resolved: "Çözüldü", Completed: "Tamamlandı" };
  const it = sprint.data?.iteration;
  return (
    <>
      <PageHead title={it?.name ?? "Sprint"} subtitle={it ? `${sprint.data!.teamName} · ${fmtDay(it.startDate)} – ${fmtDay(it.finishDate)}` : sprint.data?.teamName}>
        <div className="row" role="tablist">
          <button className={view === "taskboard" ? "primary small" : "small"} onClick={() => set("view", null)}>Taskboard</button>
          <button className={view === "list" ? "primary small" : "small"} onClick={() => set("view", "list")}>Liste</button>
        </div>
      </PageHead>
      <div className="toolbar">
        <select aria-label="Takım" value={sprint.data?.teamId ?? team ?? ""} onChange={(e) => { const next = new URLSearchParams(params); next.set("team", e.target.value); next.delete("iteration"); setParams(next); }}>
          {(teams.data ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
        </select>
        <select aria-label="Iteration" value={it?.path ?? ""} onChange={(e) => set("iteration", e.target.value)}>
          {(sprint.data?.iterations ?? []).map((i) => <option key={i.path} value={i.path}>{i.name}{i.timeFrame === "current" ? " (güncel)" : i.timeFrame === "past" ? " (geçmiş)" : ""}</option>)}
        </select>
      </div>
      <ErrorBox error={sprint.error} />
      {sprint.isLoading ? <Loading /> : null}
      {sprint.data && !it ? <Empty>Bu takım için tanımlı iteration yok.</Empty> : null}
      {it ? <>
        <div className="stats" style={{ marginBottom: 16 }}>
          <Stat label="Toplam" value={items.length} />
          {categories.map((c) => <Stat key={c} label={label[c]!} value={items.filter((i) => i.stateCategory === c).length} />)}
          <Stat label="Story point" value={items.reduce((s, i) => s + (i.storyPoints ?? 0), 0)} />
        </div>
        {items.length === 0 ? <Empty>Bu iteration'da madde yok.</Empty> : view === "list"
          ? <WorkItemTable items={items} onOpen={(i) => open(projectId, i.azureId)} />
          : (
            <div className="board">
              {categories.map((c) => {
                const column = items.filter((i) => i.stateCategory === c || (c === "Proposed" && !categories.includes(i.stateCategory) && i.stateCategory !== "Removed"));
                return (
                  <section key={c} className="column">
                    <div className="column-head"><span>{label[c]}</span><span className="count">{column.length}</span></div>
                    <div className="column-body">{column.map((i) => <WorkItemCard key={i.azureId} item={i} showState onOpen={() => open(projectId, i.azureId)} />)}</div>
                  </section>
                );
              })}
            </div>
          )}
      </> : null}
    </>
  );
}
