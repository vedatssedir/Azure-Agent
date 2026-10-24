import { fmtDate, p, type AuditEntry } from "../api";
import { ErrorBox, useGet } from "../components/ui";
import type { ProjectPageProps } from "./ProjectShell";

export function Audit({ project }: ProjectPageProps) {
  const entries = useGet<AuditEntry[]>(["audit", project.id], p(project.id, "/audit"));
  return (
    <div className="card">
      <ErrorBox error={entries.error} />
      <table>
        <thead><tr><th>Zaman</th><th>Kim</th><th>İşlem</th><th>Nesne</th><th>Ayrıntı</th></tr></thead>
        <tbody>{entries.data?.map((e, i) => (
          <tr key={i}>
            <td className="small">{fmtDate(e.at)}</td>
            <td>{e.actor}</td>
            <td><code>{e.action}</code></td>
            <td className="small">{e.entityType} {e.entityId}</td>
            <td className="small muted" style={{ maxWidth: 360, overflowWrap: "anywhere" }}>{e.details}</td>
          </tr>
        ))}</tbody>
      </table>
    </div>
  );
}
