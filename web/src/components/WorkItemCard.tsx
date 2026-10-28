import { type WorkItem } from "../api";
import { Avatar, hex, RunBadge, StateLabel } from "./ui";

export function WorkItemCard({ item, onOpen, draggable, onDragStart, showState }: {
  item: WorkItem; onOpen: () => void; draggable?: boolean; onDragStart?: (e: React.DragEvent) => void; showState?: boolean;
}) {
  return (
    <div className="wi-card" draggable={draggable} onDragStart={onDragStart} onClick={onOpen} role="button" tabIndex={0}
      onKeyDown={(e) => { if (e.key === "Enter") onOpen(); }} aria-label={`${item.type} ${item.azureId}: ${item.title}`}>
      <span className="type-dot" style={{ background: hex(item.typeColor) }} />
      <div className="body">
        <div className="title"><span className="subtle" style={{ fontWeight: 400 }}>{item.azureId}</span> {item.title}</div>
        <div className="meta">
          {item.assignedToDisplayName ? <><Avatar name={item.assignedToDisplayName} /> <span className="truncate">{item.assignedToDisplayName}</span></> : <span className="subtle">Atanmamış</span>}
        </div>
        {showState || item.lastRunStatus || item.tags.length ? (
          <div className="meta">
            {showState ? <StateLabel state={item.state} color={item.stateColor} /> : null}
            {item.lastRunStatus ? <><RunBadge status={item.lastRunStatus} />{item.agentName ? <span className="truncate">{item.agentName}</span> : null}</> : null}
            {item.tags.slice(0, 4).map((t) => <span key={t} className={`tag${t.startsWith("agent:") ? " agent" : t.startsWith("repo:") ? " repo" : ""}`}>{t}</span>)}
          </div>
        ) : null}
      </div>
    </div>
  );
}
