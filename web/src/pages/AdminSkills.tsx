import { useState } from "react";
import { api, type Catalog, type McpServer, type Skill } from "../api";
import { Drawer, ErrorBox, Field, useAction, useGet } from "../components/ui";

type SkillDraft = Omit<Skill, "id">;
type McpDraft = Omit<McpServer, "id">;

export function AdminSkills() {
  const skills = useGet<Skill[]>(["skills"], "/api/skills");
  const mcp = useGet<McpServer[]>(["mcp"], "/api/mcp-servers");
  const catalog = useGet<Catalog>(["catalog"], "/api/catalog");
  const [skill, setSkill] = useState<SkillDraft | null>(null);
  const [server, setServer] = useState<McpDraft | null>(null);
  const installed = new Set(skills.data?.map((s) => s.key));
  const installedMcp = new Set(mcp.data?.map((s) => s.name));
  return (
    <>
      <div className="page-head">
        <div><h1>Skill & MCP</h1><div className="subtitle">Agent'lara eklenebilen talimat paketleri ve araç sunucuları.</div></div>
        <div className="row"><button onClick={() => setSkill({ key: "", name: "", description: "", content: "" })}>Yeni skill</button>
          <button onClick={() => setServer({ name: "", transport: "Stdio", command: "npx", arguments: [], url: null, secretNames: [] })}>Yeni MCP sunucusu</button></div>
      </div>
      <ErrorBox error={skills.error ?? mcp.error} />
      <div className="grid">
        <section className="card">
          <h2>Skill'ler</h2>
          <table><tbody>{skills.data?.map((s) => (
            <tr key={s.key} className="clickable" onClick={() => setSkill(s)}><td><strong>{s.name}</strong> <code className="muted">{s.key}</code><div className="small muted">{s.description}</div></td></tr>
          ))}</tbody></table>
          <h3 style={{ marginTop: 16 }}>Katalog</h3>
          <table><tbody>{catalog.data?.skills.filter((s) => !installed.has(s.key)).map((s) => (
            <tr key={s.key}><td><strong>{s.name}</strong><div className="small muted">{s.description}</div></td>
              <td className="right"><button onClick={() => setSkill({ key: s.key, name: s.name, description: s.description, content: s.content })}>Ekle</button></td></tr>
          ))}</tbody></table>
        </section>
        <section className="card">
          <h2>MCP sunucuları</h2>
          <table><tbody>{mcp.data?.map((s) => (
            <tr key={s.name} className="clickable" onClick={() => setServer(s)}><td><strong>{s.name}</strong><div className="small muted"><code>{s.transport === "Http" ? s.url : [s.command, ...s.arguments].join(" ")}</code></div></td></tr>
          ))}</tbody></table>
          <h3 style={{ marginTop: 16 }}>Katalog</h3>
          <table><tbody>{catalog.data?.mcpServers.filter((s) => !installedMcp.has(s.name)).map((s) => (
            <tr key={s.name}><td><strong>{s.name}</strong><div className="small muted">{s.description}</div></td>
              <td className="right"><button onClick={() => setServer({ name: s.name, transport: s.transport as McpServer["transport"], command: s.command, arguments: s.arguments, url: s.url, secretNames: s.secretNames })}>Ekle</button></td></tr>
          ))}</tbody></table>
        </section>
      </div>
      {skill ? <SkillEditor draft={skill} exists={installed.has(skill.key)} onClose={() => setSkill(null)} /> : null}
      {server ? <McpEditor draft={server} exists={installedMcp.has(server.name)} onClose={() => setServer(null)} /> : null}
    </>
  );
}

function SkillEditor({ draft: initial, exists, onClose }: { draft: SkillDraft; exists: boolean; onClose: () => void }) {
  const [draft, setDraft] = useState(initial);
  const save = useAction(() => api(`/api/skills/${draft.key}`, { method: "PUT", body: draft }), [["skills"]]);
  const remove = useAction(() => api(`/api/skills/${draft.key}`, { method: "DELETE" }), [["skills"]]);
  return (
    <Drawer title="Skill" onClose={onClose}>
      <ErrorBox error={save.error ?? remove.error} />
      <div className="form">
        <div className="form-2">
          <Field label="Anahtar" hint="küçük harf, rakam, tire"><input disabled={exists} value={draft.key} onChange={(e) => setDraft({ ...draft, key: e.target.value })} /></Field>
          <Field label="Ad"><input value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} /></Field>
        </div>
        <Field label="Açıklama"><input value={draft.description} onChange={(e) => setDraft({ ...draft, description: e.target.value })} /></Field>
        <Field label="İçerik" hint="Markdown — agent'ın sistem talimatına eklenir"><textarea style={{ minHeight: 300 }} value={draft.content} onChange={(e) => setDraft({ ...draft, content: e.target.value })} /></Field>
        <div className="row"><button onClick={onClose}>Vazgeç</button><span className="spacer" />
          {exists ? <button className="danger" onClick={async () => { if (await remove.run(undefined) !== undefined) onClose(); }}>Sil</button> : null}
          <button className="primary" disabled={save.busy} onClick={async () => { if (await save.run(undefined) !== undefined) onClose(); }}>Kaydet</button></div>
      </div>
    </Drawer>
  );
}

function McpEditor({ draft: initial, exists, onClose }: { draft: McpDraft; exists: boolean; onClose: () => void }) {
  const [draft, setDraft] = useState(initial);
  const save = useAction(() => api(`/api/mcp-servers/${draft.name}`, { method: "PUT", body: draft }), [["mcp"]]);
  const remove = useAction(() => api(`/api/mcp-servers/${draft.name}`, { method: "DELETE" }), [["mcp"]]);
  return (
    <Drawer title="MCP sunucusu" onClose={onClose}>
      <ErrorBox error={save.error ?? remove.error} />
      <div className="form">
        <div className="form-2">
          <Field label="Ad"><input disabled={exists} value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} /></Field>
          <Field label="Bağlantı türü">
            <select value={draft.transport} onChange={(e) => setDraft({ ...draft, transport: e.target.value as McpServer["transport"] })}>
              <option value="Stdio">Komut (stdio)</option><option value="Http">HTTP</option>
            </select>
          </Field>
        </div>
        {draft.transport === "Stdio" ? <>
          <Field label="Komut"><input value={draft.command ?? ""} onChange={(e) => setDraft({ ...draft, command: e.target.value })} /></Field>
          <Field label="Argümanlar" hint="her satıra bir tane"><textarea value={draft.arguments.join("\n")} onChange={(e) => setDraft({ ...draft, arguments: e.target.value.split("\n").filter(Boolean) })} /></Field>
        </> : <Field label="URL"><input value={draft.url ?? ""} onChange={(e) => setDraft({ ...draft, url: e.target.value })} /></Field>}
        <Field label="Secret'lar" hint="Stdio: ortam değişkeni, HTTP: başlık olarak verilir. Proje veya organizasyon secret'ı olmalı (virgülle).">
          <input value={draft.secretNames.join(", ")} onChange={(e) => setDraft({ ...draft, secretNames: e.target.value.split(",").map((s) => s.trim().toUpperCase()).filter(Boolean) })} />
        </Field>
        <div className="row"><button onClick={onClose}>Vazgeç</button><span className="spacer" />
          {exists ? <button className="danger" onClick={async () => { if (await remove.run(undefined) !== undefined) onClose(); }}>Sil</button> : null}
          <button className="primary" disabled={save.busy} onClick={async () => { if (await save.run(undefined) !== undefined) onClose(); }}>Kaydet</button></div>
      </div>
    </Drawer>
  );
}
