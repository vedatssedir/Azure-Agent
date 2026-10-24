import { useState } from "react";
import { fmtTokens, fmtUsd, p, type BudgetLine, type CostReport } from "../api";
import { ErrorBox, Stat, UsageBar, useGet } from "../components/ui";
import type { ProjectPageProps } from "./ProjectShell";

const GROUPS = { Agent: "Agent", WorkItem: "Work item", Day: "Gün", Model: "Model", Kind: "İş türü" } as const;
const RANGES = { 7: "Son 7 gün", 30: "Son 30 gün", 90: "Son 90 gün" } as const;

export function Costs({ project }: ProjectPageProps) {
  const [groupBy, setGroupBy] = useState<keyof typeof GROUPS>("Agent");
  const [days, setDays] = useState<7 | 30 | 90>(30);
  const from = new Date(Date.now() - days * 86_400_000).toISOString();
  const report = useGet<CostReport>(["costs", project.id, groupBy, days], p(project.id, `/costs?groupBy=${groupBy}&from=${encodeURIComponent(from)}`));
  const budgets = useGet<BudgetLine[]>(["budgets", project.id], p(project.id, "/budgets"));
  const r = report.data;
  const max = Math.max(...(r?.rows.map((row) => row.costUsd) ?? [0]), 0.000001);
  return (
    <div className="stack">
      <div className="row">
        {Object.entries(RANGES).map(([k, v]) => <button key={k} className={days === Number(k) ? "primary" : ""} onClick={() => setDays(Number(k) as 7 | 30 | 90)}>{v}</button>)}
        <span className="spacer" />
        <span className="small muted">Grupla:</span>
        {Object.entries(GROUPS).map(([k, v]) => <button key={k} className={groupBy === k ? "primary" : ""} onClick={() => setGroupBy(k as keyof typeof GROUPS)}>{v}</button>)}
      </div>
      <ErrorBox error={report.error ?? budgets.error} />
      {r ? (
        <div className="stats">
          <Stat label="Toplam maliyet" value={fmtUsd(r.totalCostUsd)} />
          <Stat label="İş sayısı" value={r.totalRuns} />
          <Stat label="Başarılı iş başına maliyet" value={fmtUsd(r.costPerSucceededRunUsd, 4)} />
          <Stat label="Toplam token" value={fmtTokens(r.totalTokens)} />
        </div>
      ) : null}
      <section className="card">
        <h2>{GROUPS[groupBy]} bazında</h2>
        <table>
          <thead><tr><th>{GROUPS[groupBy]}</th><th className="right">İş</th><th className="right">Başarılı</th><th className="right">Token</th><th className="right">Ort. iş maliyeti</th><th className="right">Ort. süre</th><th className="right">Maliyet</th><th style={{ width: "18%" }} /></tr></thead>
          <tbody>
            {r?.rows.length === 0 ? <tr><td colSpan={8} className="muted">Bu aralıkta agent çalışması yok.</td></tr> : null}
            {r?.rows.map((row) => (
              <tr key={row.key}>
                <td>{row.label}</td>
                <td className="right">{row.runs}</td>
                <td className="right">{row.succeededRuns}</td>
                <td className="right">{fmtTokens(row.inputTokens + row.outputTokens)}</td>
                <td className="right">{fmtUsd(row.averageCostPerRunUsd, 4)}</td>
                <td className="right">{row.averageDurationMinutes} dk</td>
                <td className="right"><strong>{fmtUsd(row.costUsd, 4)}</strong></td>
                <td><div className="bar"><span style={{ width: `${(row.costUsd / max) * 100}%` }} /></div></td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
      <section className="card">
        <h2>Bu ayın bütçeleri</h2>
        <p className="muted small">Herhangi bir limit dolduğunda ilgili agent'ların yeni işleri ve devam eden model çağrıları durdurulur.</p>
        <table><tbody>{budgets.data?.map((b) => (
          <tr key={`${b.scope}-${b.name}`}>
            <td><span className="badge">{b.scope === "organization" ? "Organizasyon" : b.scope === "project" ? "Proje" : "Agent"}</span> {b.name}</td>
            <td className="right">{fmtUsd(b.spentUsd)} / {fmtUsd(b.limitUsd)}</td>
            <td style={{ width: "30%" }}><UsageBar percent={b.usedPercent} /></td>
          </tr>
        ))}</tbody></table>
      </section>
    </div>
  );
}
