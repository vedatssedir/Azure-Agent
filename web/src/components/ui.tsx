import { useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient, type QueryKey } from "@tanstack/react-query";
import { api, RUN_STATUS, type RunStatus } from "../api";

export function useGet<T>(key: QueryKey, path: string | null, options: { refetchInterval?: number } = {}) {
  return useQuery({ queryKey: key, queryFn: () => api<T>(path!), enabled: path !== null, refetchInterval: options.refetchInterval });
}

/** Mutation that invalidates the given query keys and exposes a message/error for the page. */
export function useAction<TArgs, TResult = unknown>(fn: (args: TArgs) => Promise<TResult>, invalidate: QueryKey[] = []) {
  const client = useQueryClient();
  const [message, setMessage] = useState<string | null>(null);
  const mutation = useMutation({
    mutationFn: fn,
    onSuccess: () => { invalidate.forEach((key) => client.invalidateQueries({ queryKey: key })); },
  });
  return {
    run: async (args: TArgs, success?: string | ((result: TResult) => string)) => {
      setMessage(null);
      try {
        const result = await mutation.mutateAsync(args);
        setMessage(typeof success === "function" ? success(result) : success ?? null);
        return result;
      } catch {
        return undefined; // surfaced through `error`
      }
    },
    busy: mutation.isPending,
    error: mutation.error ? (mutation.error as Error).message : null,
    message,
  };
}

export function ErrorBox({ error }: { error: unknown }) {
  if (!error) return null;
  return <div className="error">{error instanceof Error ? error.message : String(error)}</div>;
}

export function Notice({ text }: { text: string | null }) {
  return text ? <div className="notice">{text}</div> : null;
}

export function RunBadge({ status }: { status: RunStatus | null }) {
  if (!status) return null;
  const s = RUN_STATUS[status];
  return <span className={`badge ${s.cls}`}>{s.label}</span>;
}

export function Drawer({ title, onClose, children, actions }: { title: ReactNode; onClose: () => void; children: ReactNode; actions?: ReactNode }) {
  return (
    <div className="backdrop" onClick={onClose}>
      <aside className="drawer" onClick={(e) => e.stopPropagation()} role="dialog" aria-modal="true">
        <div className="row"><h1>{title}</h1><span className="spacer" />{actions}<button onClick={onClose}>Kapat</button></div>
        {children}
      </aside>
    </div>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return <label className="field">{label}{hint ? <span className="hint">{hint}</span> : null}{children}</label>;
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

export function Stat({ label, value }: { label: string; value: ReactNode }) {
  return <div className="stat"><div className="label">{label}</div><div className="value">{value}</div></div>;
}

export function UsageBar({ percent }: { percent: number | null }) {
  if (percent === null) return <span className="muted small">limit yok</span>;
  const cls = percent >= 100 ? "bad" : percent >= 80 ? "warn" : "";
  return <div className={`bar ${cls}`} title={`%${percent}`}><span style={{ width: `${Math.min(percent, 100)}%` }} /></div>;
}
