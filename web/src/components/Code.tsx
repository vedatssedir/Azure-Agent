import { useMemo, useState } from "react";
import hljs from "highlight.js/lib/common";
import { type FileChange } from "../api";
import { Icon } from "./icons";

const LANGUAGES: Record<string, string> = {
  cs: "csharp", csproj: "xml", props: "xml", targets: "xml", sln: "plaintext", ts: "typescript", tsx: "typescript", js: "javascript", jsx: "javascript", mjs: "javascript",
  json: "json", md: "markdown", yml: "yaml", yaml: "yaml", xml: "xml", html: "xml", htm: "xml", css: "css", scss: "scss", less: "less", py: "python", go: "go", rs: "rust",
  java: "java", kt: "kotlin", rb: "ruby", php: "php", sh: "bash", bash: "bash", ps1: "powershell", sql: "sql", c: "c", h: "c", cpp: "cpp", hpp: "cpp", swift: "swift",
  ini: "ini", toml: "ini", dockerfile: "dockerfile", razor: "xml", cshtml: "xml", vue: "xml", graphql: "graphql", r: "r", lua: "lua", pl: "perl",
};

export function languageOf(path: string) {
  const name = path.split("/").pop()?.toLowerCase() ?? "";
  if (name === "dockerfile") return "dockerfile";
  const ext = name.includes(".") ? name.split(".").pop()! : "";
  const language = LANGUAGES[ext];
  return language && hljs.getLanguage(language) ? language : null;
}

/** Read-only source view with line numbers and syntax highlighting. */
export function CodeView({ path, content }: { path: string; content: string }) {
  const html = useMemo(() => {
    const language = languageOf(path);
    if (content.length > 400_000) return null; // very large files are shown as plain text
    try {
      return language ? hljs.highlight(content, { language, ignoreIllegals: true }).value : null;
    } catch {
      return null;
    }
  }, [path, content]);
  const lines = content.split("\n").length - (content.endsWith("\n") ? 1 : 0);
  return (
    <div className="code">
      <table>
        <tbody>
          <tr>
            <td className="ln"><pre style={{ margin: 0, font: "inherit" }}>{Array.from({ length: Math.max(lines, 1) }, (_, i) => i + 1).join("\n")}</pre></td>
            <td>
              {html !== null
                ? <pre style={{ margin: 0, font: "inherit" }}><code className="hljs" dangerouslySetInnerHTML={{ __html: html }} /></pre>
                : <pre style={{ margin: 0, font: "inherit" }}><code>{content}</code></pre>}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  );
}

const STATUS_LABEL: Record<string, { label: string; cls: string }> = {
  add: { label: "eklendi", cls: "ok" }, edit: { label: "değişti", cls: "info" }, delete: { label: "silindi", cls: "bad" }, rename: { label: "taşındı", cls: "warn" },
  copy: { label: "kopyalandı", cls: "warn" },
};

export function FileDiff({ file, defaultOpen = true }: { file: FileChange; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  const status = STATUS_LABEL[file.status] ?? { label: file.status, cls: "" };
  return (
    <div className="card flush" style={{ marginBottom: 12, overflow: "hidden" }}>
      <div className="file-head" onClick={() => setOpen(!open)} style={{ cursor: "pointer" }}>
        <span style={{ transform: open ? "rotate(90deg)" : undefined, display: "inline-flex" }}><Icon name="chevron" size={14} /></span>
        <span className="truncate" style={{ flex: 1 }}>{file.oldPath && file.oldPath !== file.path ? `${file.oldPath} → ` : ""}{file.path}</span>
        <span className={`badge ${status.cls}`}>{status.label}</span>
        <span className="add-n">+{file.additions}</span><span className="del-n">−{file.deletions}</span>
      </div>
      {open ? (
        file.isBinary ? <div className="empty small">İkili dosya</div>
          : file.patch ? (
            <div className="diff">
              {file.patch.split("\n").map((line, i) => {
                const cls = line.startsWith("@@") ? "hunk" : line.startsWith("+++") || line.startsWith("---") || line.startsWith("diff ") || line.startsWith("index ") ? "meta"
                  : line.startsWith("+") ? "add" : line.startsWith("-") ? "del" : "";
                return <div key={i} className={cls}>{line || " "}</div>;
              })}
            </div>
          ) : <div className="empty small">Gösterilecek değişiklik yok.</div>
      ) : null}
    </div>
  );
}

export function DiffSummary({ files }: { files: FileChange[] }) {
  const add = files.reduce((s, f) => s + f.additions, 0);
  const del = files.reduce((s, f) => s + f.deletions, 0);
  return <span className="small muted">{files.length} dosya · <span className="add-n">+{add}</span> <span className="del-n">−{del}</span></span>;
}
