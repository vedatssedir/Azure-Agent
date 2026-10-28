import { fmtAgo, type WorkItem } from "../api";
import { Avatar, hex, RunBadge, StateLabel } from "./ui";

export function WorkItemTable({ items, onOpen, project }: { items: Array<WorkItem & { projectName?: string }>; onOpen: (item: WorkItem) => void; project?: boolean }) {
  return (
    <div className="table-card">
      <table>
        <thead><tr><th style={{ width: 70 }}>ID</th><th>Başlık</th>{project ? <th>Proje</th> : null}<th>Durum</th><th>Atanan</th><th>Iteration</th><th>Agent</th><th>Değişti</th></tr></thead>
        <tbody>{items.map((item) => (
          <tr key={`${item.projectName ?? ""}-${item.azureId}`} className="clickable" onClick={() => onOpen(item)}>
            <td className="subtle">{item.azureId}</td>
            <td>
              <div className="row nowrap">
                <span className="type-dot" style={{ background: hex(item.typeColor), height: 18 }} title={item.type} />
                <span className="truncate" style={{ maxWidth: 520 }}><strong style={{ fontWeight: 600 }}>{item.title}</strong></span>
                {item.tags.filter((t) => t.startsWith("agent:")).map((t) => <span key={t} className="tag agent">{t}</span>)}
              </div>
            </td>
            {project ? <td className="muted">{item.projectName}</td> : null}
            <td><StateLabel state={item.state} color={item.stateColor} /></td>
            <td>{item.assignedToDisplayName ? <span className="row nowrap"><Avatar name={item.assignedToDisplayName} />{item.assignedToDisplayName}</span> : <span className="subtle">—</span>}</td>
            <td className="muted small">{item.iterationPath?.split("\\").pop() ?? "—"}</td>
            <td>{item.lastRunStatus ? <span className="row nowrap"><RunBadge status={item.lastRunStatus} /><span className="small muted">{item.agentName}</span></span> : null}</td>
            <td className="muted small">{fmtAgo(item.changedAt)}</td>
          </tr>
        ))}</tbody>
      </table>
    </div>
  );
}
