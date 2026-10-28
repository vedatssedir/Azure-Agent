import { useSearchParams } from "react-router-dom";
import { fmtTokens, fmtUsd, qs, type BudgetLine, type CostReport } from "../api";
import { useApp } from "../context";
import { Empty, ErrorBox, Loading, PageHead, Stat, UsageBar, useGet } from "../components/ui";

const GROUPS: Array<[string, string]> = [["Agent", "Agent"], ["Developer", "Developer"], ["Project", "Proje"], ["WorkItem", "Work item"], ["Day", "Gün"], ["Model", "Model"], ["Kind", "İş türü"]];

/** What the agents cost, per job; model prices are built in and not edited. */
export function Costs() {
  const { isAdmin, projects } = useApp();
  const [params, setParams] = useSearchParams();
  const groupBy = params.get("group") ?? "Agent";
  const days = Number(params.get("days") ?? 30);
  const project = params.get("project") ?? "";
  const set = (key: string, value: string) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); setParams(next); };
  const from = new Date(Date.now() - days * 86_400_000).toISOString();
  const report = useGet<CostReport>(["costs", groupBy, days, project], `/api/costs${qs({ groupBy, from, project })}`);
  const budgets = useGet<BudgetLine[]>(["budgets"], isAdmin ? "/api/costs/budgets" : null);
  const r = report.data;
  return (
    <>
      <PageHead title="Maliyet" subtitle="Model çağrıları liste fiyatlarıyla hesaplanır; iş başına maliyet ve bütçe kullanımı." />
      <div className="toolbar">
        <select aria-label="Dönem" value={days} onChange={(e) => set("days", e.target.value)}>
          <option value={7}>Son 7 gün</option><option value={30}>Son 30 gün</option><option value={90}>Son 90 gün</option><option value={365}>Son 1 yıl</option>
        </select>
        <select aria-label="Proje" value={project} onChange={(e) => set("project", e.target.value)}>
          <option value="">Tüm projeler</option>{projects.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
        </select>
        <div className="row">{GROUPS.map(([key, label]) => <button key={key} className={groupBy === key ? "primary small" : "small"} onClick={() => set("group", key)}>{label}</button>)}</div>
      </div>
      <ErrorBox error={report.error} />
      {report.isLoading ? <Loading /> : r ? <>
        <div className="stats" style={{ marginBottom: 16 }}>
          <Stat label="Toplam maliyet" value={fmtUsd(r.totalCostUsd)} />
          <Stat label="Çalıştırma" value={r.totalRuns} />
          <Stat label="Başarılı iş başına" value={fmtUsd(r.costPerSucceededRunUsd)} />
          <Stat label="Token" value={fmtTokens(r.totalTokens)} />
        </div>
        {r.rows.length === 0 ? <Empty>Bu dönemde çalıştırma yok.</Empty> : (
          <div className="table-card" style={{ marginBottom: 24 }}>
            <table>
              <thead><tr><th>{GROUPS.find(([k]) => k === groupBy)?.[1]}</th><th className="right">Çalıştırma</th><th className="right">Başarılı</th><th className="right">Token</th>
                <th className="right">Maliyet</th><th className="right">Ortalama / iş</th><th className="right">Ort. süre</th><th style={{ width: 140 }} /></tr></thead>
              <tbody>{r.rows.map((row) => (
                <tr key={row.key}>
                  <td>{row.label}</td><td className="right">{row.runs}</td><td className="right">{row.succeededRuns}</td>
                  <td className="right">{fmtTokens(row.inputTokens + row.outputTokens)}</td><td className="right"><strong>{fmtUsd(row.costUsd)}</strong></td>
                  <td className="right">{fmtUsd(row.averageCostPerRunUsd)}</td><td className="right">{row.averageDurationMinutes} dk</td>
                  <td><UsageBar percent={r.totalCostUsd > 0 ? Math.round((row.costUsd / r.totalCostUsd) * 1000) / 10 : 0} /></td>
                </tr>
              ))}</tbody>
            </table>
          </div>
        )}
      </> : null}
      {budgets.data ? <>
        <h2>Bu ayın bütçeleri</h2>
        <div className="table-card">
          <table>
            <thead><tr><th>Kapsam</th><th>Ad</th><th className="right">Harcanan</th><th className="right">Limit</th><th style={{ width: 180 }}>Kullanım</th></tr></thead>
            <tbody>{budgets.data.filter((b) => b.scope !== "agent" || b.spentUsd > 0 || b.limitUsd).map((b, i) => (
              <tr key={i}><td className="muted">{b.scope === "organization" ? "Organizasyon" : b.scope === "project" ? "Proje" : "Agent"}</td><td>{b.name}</td>
                <td className="right">{fmtUsd(b.spentUsd)}</td><td className="right">{b.limitUsd ? fmtUsd(b.limitUsd, 0) : "—"}</td><td><UsageBar percent={b.usedPercent} /></td></tr>
            ))}</tbody>
          </table>
        </div>
      </> : null}
    </>
  );
}
