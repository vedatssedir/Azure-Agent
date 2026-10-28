import { useState } from "react";
import { useParams, useSearchParams } from "react-router-dom";
import { p, qs, type WorkItem, type WorkItemType } from "../api";
import { useApp } from "../context";
import { Empty, ErrorBox, Loading, PageHead, useGet } from "../components/ui";
import { WorkItemTable } from "../components/WorkItemTable";
import { NewWorkItem } from "../components/NewWorkItem";
import { useOpenWorkItem } from "../components/WorkItemForm";
import { Icon } from "../components/icons";

export function WorkItems() {
  const { projectId = "" } = useParams();
  const { projects } = useApp();
  const project = projects.find((x) => x.id === projectId)!;
  const [params, setParams] = useSearchParams();
  const filter = { type: params.get("type") ?? "", state: params.get("state") ?? "", who: params.get("who") ?? "", search: params.get("q") ?? "", closed: params.get("closed") === "1" };
  const set = (key: string, value: string) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); setParams(next, { replace: true }); };
  const [search, setSearch] = useState(filter.search);
  const types = useGet<WorkItemType[]>(["work-item-types", projectId], p(projectId, "/work-item-types"));
  const query = qs({ type: filter.type, state: filter.state, mine: filter.who === "me", unassigned: filter.who === "none", search: filter.search, includeClosed: String(filter.closed) });
  const items = useGet<WorkItem[]>(["work-items", projectId, query], p(projectId, `/work-items${query}`));
  const open = useOpenWorkItem();
  const [creating, setCreating] = useState(false);
  const states = [...new Set((types.data ?? []).filter((t) => !filter.type || t.name === filter.type).flatMap((t) => t.states.map((s) => s.name)))];
  return (
    <>
      <PageHead title="Work items" subtitle={`${project.name} · ${items.data?.length ?? 0} madde`}>
        {project.myPermissions.includes("workitems.create") ? <button className="primary" onClick={() => setCreating(true)}><Icon name="plus" /> Yeni work item</button> : null}
      </PageHead>
      <div className="toolbar">
        <form onSubmit={(e) => { e.preventDefault(); set("q", search); }} style={{ display: "contents" }}>
          <input aria-label="Ara" placeholder="Başlık veya ID ile ara" value={search} onChange={(e) => setSearch(e.target.value)} onBlur={() => set("q", search)} />
        </form>
        <select aria-label="Tür" value={filter.type} onChange={(e) => set("type", e.target.value)}>
          <option value="">Tüm türler</option>{(types.data ?? []).map((t) => <option key={t.name}>{t.name}</option>)}
        </select>
        <select aria-label="Durum" value={filter.state} onChange={(e) => set("state", e.target.value)}>
          <option value="">Tüm durumlar</option>{states.map((s) => <option key={s}>{s}</option>)}
        </select>
        <select aria-label="Atanan" value={filter.who} onChange={(e) => set("who", e.target.value)}>
          <option value="">Herkes</option><option value="me">Bana atananlar</option><option value="none">Atanmamış</option>
        </select>
        <label className="check"><input type="checkbox" checked={filter.closed} onChange={(e) => set("closed", e.target.checked ? "1" : "")} /> Kapalıları göster</label>
      </div>
      <ErrorBox error={items.error} />
      {items.isLoading ? <Loading /> : items.data?.length === 0 ? <Empty>Filtreye uyan work item yok.</Empty>
        : items.data ? <WorkItemTable items={items.data} onOpen={(i) => open(projectId, i.azureId)} /> : null}
      {creating ? <NewWorkItem projectId={projectId} onClose={() => setCreating(false)} onCreated={(id) => { setCreating(false); open(projectId, id); }} /> : null}
    </>
  );
}
