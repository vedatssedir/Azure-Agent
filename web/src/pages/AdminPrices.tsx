import { useState } from "react";
import { api, type ModelPrice, type ModelProvider } from "../api";
import { ErrorBox, Notice, useAction, useGet } from "../components/ui";

export function AdminPrices() {
  const prices = useGet<ModelPrice[]>(["prices"], "/api/model-prices");
  const [draft, setDraft] = useState({ provider: "Anthropic" as ModelProvider, model: "", inputPerMillion: "", outputPerMillion: "", cachedInputPerMillion: "" });
  const save = useAction(() => api("/api/model-prices", {
    method: "PUT",
    body: { ...draft, inputPerMillion: Number(draft.inputPerMillion), outputPerMillion: Number(draft.outputPerMillion), cachedInputPerMillion: Number(draft.cachedInputPerMillion || 0) },
  }), [["prices"]]);
  return (
    <>
      <div className="page-head"><div><h1>Model fiyatları</h1>
        <div className="subtitle">1 milyon token başına USD. Maliyetler bu tabloya göre hesaplanır; model adı önek eşleşmesiyle bulunur (örn. "claude-sonnet" → claude-sonnet-4-5).</div></div></div>
      <div className="stack">
        <section className="card">
          <ErrorBox error={prices.error} />
          <table>
            <thead><tr><th>Sağlayıcı</th><th>Model</th><th className="right">Girdi</th><th className="right">Önbellekli girdi</th><th className="right">Çıktı</th><th /></tr></thead>
            <tbody>{prices.data?.map((pr) => (
              <tr key={pr.id} className="clickable" onClick={() => setDraft({ provider: pr.provider, model: pr.model, inputPerMillion: String(pr.inputPerMillion), outputPerMillion: String(pr.outputPerMillion), cachedInputPerMillion: String(pr.cachedInputPerMillion) })}>
                <td>{pr.provider}</td><td><code>{pr.model}</code></td>
                <td className="right">${pr.inputPerMillion}</td><td className="right">${pr.cachedInputPerMillion}</td><td className="right">${pr.outputPerMillion}</td>
                <td>{pr.isDefault ? <span className="badge">varsayılan</span> : <span className="badge info">organizasyon</span>}</td>
              </tr>
            ))}</tbody>
          </table>
        </section>
        <section className="card stack">
          <h2>Fiyat tanımla / güncelle</h2>
          <ErrorBox error={save.error} /><Notice text={save.message} />
          <div className="row">
            <select value={draft.provider} onChange={(e) => setDraft({ ...draft, provider: e.target.value as ModelProvider })} style={{ maxWidth: 160 }}>
              {(["Anthropic", "OpenAI", "AzureOpenAI", "Gemini"] as const).map((x) => <option key={x}>{x}</option>)}
            </select>
            <input placeholder="Model" value={draft.model} onChange={(e) => setDraft({ ...draft, model: e.target.value })} style={{ maxWidth: 200 }} />
            <input type="number" min={0} step="0.01" placeholder="Girdi" value={draft.inputPerMillion} onChange={(e) => setDraft({ ...draft, inputPerMillion: e.target.value })} style={{ maxWidth: 110 }} />
            <input type="number" min={0} step="0.01" placeholder="Önbellekli" value={draft.cachedInputPerMillion} onChange={(e) => setDraft({ ...draft, cachedInputPerMillion: e.target.value })} style={{ maxWidth: 110 }} />
            <input type="number" min={0} step="0.01" placeholder="Çıktı" value={draft.outputPerMillion} onChange={(e) => setDraft({ ...draft, outputPerMillion: e.target.value })} style={{ maxWidth: 110 }} />
            <button className="primary" disabled={!draft.model || !draft.inputPerMillion || !draft.outputPerMillion || save.busy} onClick={() => save.run(undefined, "Fiyat kaydedildi (organizasyon için geçerli).")}>Kaydet</button>
          </div>
        </section>
      </div>
    </>
  );
}
