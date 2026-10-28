import { useState } from "react";
import { api, p, type Agent, type Repository, type WorkItem, type WorkItemType } from "../api";
import { Check, ErrorBox, Field, FieldGroup, Modal, useAction, useGet } from "./ui";

/** Creates a work item in Azure Boards; tagging it for an agent hands it to that agent. */
export function NewWorkItem({ projectId, iterationPath, onClose, onCreated }: {
  projectId: string; iterationPath?: string | null; onClose: () => void; onCreated: (azureId: number) => void;
}) {
  const types = useGet<WorkItemType[]>(["work-item-types", projectId], p(projectId, "/work-item-types"));
  const repos = useGet<Repository[]>(["repos", projectId], `/api/repos?project=${projectId}`);
  const agents = useGet<Agent[]>(["agents", "mine"], "/api/agents?mine=true");
  const [form, setForm] = useState({ type: "", title: "", description: "", acceptanceCriteria: "", repositoryNames: [] as string[], assignToMe: true, agentSlug: "", priority: 2 });
  const type = types.data?.find((t) => t.name === form.type) ?? types.data?.[0];
  const create = useAction(() => api<WorkItem>(p(projectId, "/work-items"), {
    body: { ...form, type: type?.name, tags: [], iterationPath: iterationPath ?? null, agentSlug: form.agentSlug || null },
  }), [["board", projectId], ["work-items", projectId], ["my-work"], ["sprint", projectId]]);
  const developers = (agents.data ?? []).filter((a) => a.kind === "Developer" && a.enabled);
  return (
    <Modal title="Yeni work item" onClose={onClose} narrow>
      <form className="stack" onSubmit={async (e) => { e.preventDefault(); const item = await create.run(undefined); if (item) onCreated(item.azureId); }}>
        <ErrorBox error={types.error ?? create.error} />
        <div className="form-2">
          <Field label="Tür">
            <select value={type?.name ?? ""} onChange={(e) => setForm({ ...form, type: e.target.value })}>
              {(types.data ?? []).map((t) => <option key={t.name} value={t.name}>{t.name}</option>)}
            </select>
          </Field>
          <Field label="Öncelik">
            <select value={form.priority} onChange={(e) => setForm({ ...form, priority: Number(e.target.value) })}>{[1, 2, 3, 4].map((n) => <option key={n}>{n}</option>)}</select>
          </Field>
        </div>
        <Field label="Başlık"><input required autoFocus value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} /></Field>
        <Field label="Açıklama"><textarea value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} /></Field>
        {type?.hasAcceptanceCriteria ? (
          <Field label="Kabul kriterleri"><textarea value={form.acceptanceCriteria} onChange={(e) => setForm({ ...form, acceptanceCriteria: e.target.value })} /></Field>
        ) : null}
        {repos.data?.length ? (
          <FieldGroup label="Repolar" hint="Agent'ın çalışacağı repolar (repo:<ad> etiketi olarak eklenir)">
            <div className="row">{repos.data.map((r) => (
              <Check key={r.id} label={r.name} checked={form.repositoryNames.includes(r.name)} onChange={(on) =>
                setForm({ ...form, repositoryNames: on ? [...form.repositoryNames, r.name] : form.repositoryNames.filter((n) => n !== r.name) })} />
            ))}</div>
          </FieldGroup>
        ) : null}
        <div className="form-2">
          <Check label="Bana ata" checked={form.assignToMe} onChange={(assignToMe) => setForm({ ...form, assignToMe })} />
          <Field label="Agent'a ver" hint="Seçerseniz agent:<slug> etiketi eklenir ve agent başlar">
            <select value={form.agentSlug} onChange={(e) => setForm({ ...form, agentSlug: e.target.value })}>
              <option value="">Hayır</option>
              {developers.map((a) => <option key={a.id} value={a.slug}>{a.name}</option>)}
            </select>
          </Field>
        </div>
        <div className="row"><span className="spacer" /><button type="button" onClick={onClose}>Vazgeç</button>
          <button className="primary" disabled={create.busy || !form.title.trim() || !type}>Oluştur</button></div>
      </form>
    </Modal>
  );
}
