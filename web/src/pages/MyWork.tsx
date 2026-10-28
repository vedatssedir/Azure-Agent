import { useSearchParams } from "react-router-dom";
import { type MyWorkItem } from "../api";
import { Empty, ErrorBox, Loading, PageHead, useGet } from "../components/ui";
import { WorkItemTable } from "../components/WorkItemTable";
import { WorkItemCard } from "../components/WorkItemCard";
import { useOpenWorkItem } from "../components/WorkItemForm";

/** Everything assigned to the signed-in developer across projects, grouped by state category like a personal board. */
export function MyWork() {
  const [params, setParams] = useSearchParams();
  const closed = params.get("closed") === "1";
  const view = params.get("view") ?? "board";
  const set = (key: string, value: string | null) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); setParams(next); };
  const items = useGet<MyWorkItem[]>(["my-work", closed], `/api/work-items/mine?includeClosed=${closed}`, { refetchInterval: 30_000 });
  const open = useOpenWorkItem();
  const data = items.data ?? [];
  const columns = [
    { key: "Proposed", label: "Yapılacak" }, { key: "InProgress", label: "Devam ediyor" }, { key: "Resolved", label: "Çözüldü" },
    ...(closed ? [{ key: "Completed", label: "Tamamlandı" }] : []),
  ];
  return (
    <>
      <PageHead title="Bana atananlar" subtitle="Tüm projelerde size atanmış work item'lar. Agent'larınız bunları sizin adınıza geliştirebilir.">
        <div className="row">
          <button className={view === "board" ? "primary small" : "small"} onClick={() => set("view", null)}>Board</button>
          <button className={view === "list" ? "primary small" : "small"} onClick={() => set("view", "list")}>Liste</button>
        </div>
      </PageHead>
      <div className="toolbar">
        <label className="check"><input type="checkbox" checked={closed} onChange={(e) => set("closed", e.target.checked ? "1" : null)} /> Kapalıları göster</label>
      </div>
      <ErrorBox error={items.error} />
      {items.isLoading ? <Loading /> : data.length === 0 ? <Empty>Size atanmış açık iş yok. Azure Boards'ta size atanan maddeler burada görünür.</Empty> : view === "list"
        ? <WorkItemTable project items={data.map((m) => ({ ...m.item, projectName: m.projectName }))}
            onOpen={(i) => { const m = data.find((d) => d.item.azureId === i.azureId && d.projectName === (i as { projectName?: string }).projectName); if (m) open(m.projectId, i.azureId); }} />
        : (
          <div className="board">
            {columns.map((c) => {
              const column = data.filter((m) => m.item.stateCategory === c.key || (c.key === "Proposed" && !["InProgress", "Resolved", "Completed", "Removed"].includes(m.item.stateCategory)));
              return (
                <section key={c.key} className="column">
                  <div className="column-head"><span>{c.label}</span><span className="count">{column.length}</span></div>
                  <div className="column-body">{column.map((m) => (
                    <div key={`${m.projectId}-${m.item.azureId}`}>
                      <div className="small subtle" style={{ margin: "0 0 2px 2px" }}>{m.projectName}</div>
                      <WorkItemCard item={m.item} showState onOpen={() => open(m.projectId, m.item.azureId)} />
                    </div>
                  ))}</div>
                </section>
              );
            })}
          </div>
        )}
    </>
  );
}
