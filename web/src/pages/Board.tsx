import { useState } from "react";
import { useParams, useSearchParams } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { api, p, qs, type Board, type Team, type WorkItem } from "../api";
import { useApp } from "../context";
import { Empty, ErrorBox, Loading, PageHead, useGet } from "../components/ui";
import { WorkItemCard } from "../components/WorkItemCard";
import { NewWorkItem } from "../components/NewWorkItem";
import { useOpenWorkItem } from "../components/WorkItemForm";
import { Icon } from "../components/icons";

/** A team's Kanban board as configured in Azure Boards; dragging a card moves it to that column (and its mapped state) in Azure. */
export function BoardPage() {
  const { projectId = "" } = useParams();
  const { projects } = useApp();
  const project = projects.find((x) => x.id === projectId)!;
  const [params, setParams] = useSearchParams();
  const teamId = params.get("team");
  const boardName = params.get("board");
  const mine = params.get("mine") === "1";
  const setParam = (key: string, value: string | null) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); setParams(next); };
  const teams = useGet<Team[]>(["teams", projectId], p(projectId, "/teams"));
  const key = ["board", projectId, teamId, boardName, mine];
  const board = useGet<Board>(key, p(projectId, `/board${qs({ team: teamId, board: boardName, mine })}`), { refetchInterval: 30_000 });
  const client = useQueryClient();
  const open = useOpenWorkItem();
  const [dragging, setDragging] = useState<WorkItem | null>(null);
  const [over, setOver] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const canMove = project.myPermissions.includes("workitems.assign");

  const drop = async (column: string) => {
    const item = dragging;
    setDragging(null);
    setOver(null);
    if (!item || !board.data || item.boardColumn === column) return;
    setError(null);
    // Move the card right away; the server answer (or a refetch) settles it.
    client.setQueryData<Board>(key, (data) => data && {
      ...data,
      columns: data.columns.map((c) => ({
        ...c,
        items: c.name === column ? [{ ...item, boardColumn: column }, ...c.items.filter((i) => i.azureId !== item.azureId)] : c.items.filter((i) => i.azureId !== item.azureId),
      })),
    });
    try {
      await api(p(projectId, `/work-items/${item.azureId}/move`), { body: { teamId: board.data.teamId, board: board.data.board, column } });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      client.invalidateQueries({ queryKey: ["board", projectId] });
      client.invalidateQueries({ queryKey: ["work-item", projectId, item.azureId] });
    }
  };

  return (
    <>
      <PageHead title={`${board.data?.teamName ?? project.name} board`} subtitle={board.data ? `${board.data.board} · Azure Boards ile senkron` : undefined}>
        {project.myPermissions.includes("workitems.create") ? <button className="primary" onClick={() => setCreating(true)}><Icon name="plus" /> Yeni work item</button> : null}
      </PageHead>
      <div className="toolbar">
        <select aria-label="Takım" value={board.data?.teamId ?? teamId ?? ""} onChange={(e) => { const next = new URLSearchParams(params); next.set("team", e.target.value); next.delete("board"); setParams(next); }}>
          {(teams.data ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
        </select>
        {board.data && board.data.boards.length > 1 ? (
          <div className="row" role="tablist">
            {board.data.boards.map((b) => (
              <button key={b} role="tab" aria-selected={b === board.data!.board} className={b === board.data!.board ? "primary small" : "small"} onClick={() => setParam("board", b)}>{b}</button>
            ))}
          </div>
        ) : null}
        <span className="spacer" />
        <label className="check"><input type="checkbox" checked={mine} onChange={(e) => setParam("mine", e.target.checked ? "1" : null)} /> Sadece bana atananlar</label>
        <button className="ghost" title="Yenile" onClick={() => board.refetch()}><Icon name="refresh" /></button>
      </div>
      <ErrorBox error={error ?? board.error} />
      {board.isLoading ? <Loading /> : null}
      {board.data && board.data.columns.length === 0 ? <Empty>Bu takımın board'u henüz senkronize edilmedi.</Empty> : null}
      {board.data ? (
        <div className="board">
          {board.data.columns.map((column) => {
            const overLimit = column.itemLimit > 0 && column.items.length > column.itemLimit;
            return (
              <section key={column.name} className={`column${over === column.name ? " drop" : ""}`} aria-label={column.name}
                onDragOver={(e) => { if (dragging && canMove) { e.preventDefault(); setOver(column.name); } }}
                onDragLeave={() => setOver((o) => (o === column.name ? null : o))}
                onDrop={(e) => { e.preventDefault(); drop(column.name); }}>
                <div className="column-head">
                  <span>{column.name}</span>
                  <span className={`count${overLimit ? " over" : ""}`}>{column.items.length}{column.itemLimit > 0 ? `/${column.itemLimit}` : ""}</span>
                </div>
                <div className="column-body">
                  {column.items.map((item) => (
                    <WorkItemCard key={item.azureId} item={item} draggable={canMove} onDragStart={() => setDragging(item)} onOpen={() => open(projectId, item.azureId)}
                      showState={Object.keys(column.stateMappings).length > 0 && !Object.values(column.stateMappings).includes(item.state)} />
                  ))}
                </div>
              </section>
            );
          })}
        </div>
      ) : null}
      {creating ? <NewWorkItem projectId={projectId} onClose={() => setCreating(false)} onCreated={(id) => { setCreating(false); open(projectId, id); }} /> : null}
    </>
  );
}
