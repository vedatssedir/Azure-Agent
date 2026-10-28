import { useEffect, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient, type QueryKey } from "@tanstack/react-query";
import { api, RUN_STATUS, type RunStatus } from "../api";
import { Icon } from "./icons";

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
    reset: () => { mutation.reset(); setMessage(null); },
  };
}

export function ErrorBox({ error }: { error: unknown }) {
  if (!error) return null;
  return <div className="error" role="alert">{error instanceof Error ? error.message : String(error)}</div>;
}

export function Notice({ text }: { text: string | null }) {
  return text ? <div className="notice">{text}</div> : null;
}

export function RunBadge({ status }: { status: RunStatus | null }) {
  if (!status) return null;
  const s = RUN_STATUS[status];
  return <span className={`badge ${s.cls}`}>{s.label}</span>;
}

export function Modal({ title, onClose, children, actions, narrow, band }: {
  title: ReactNode; onClose: () => void; children: ReactNode; actions?: ReactNode; narrow?: boolean; band?: string;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div className="backdrop" onMouseDown={onClose}>
      <div className={`modal${narrow ? " narrow" : ""}`} onMouseDown={(e) => e.stopPropagation()} role="dialog" aria-modal="true">
        <div className="modal-head">
          {band ? <span className="type-band" style={{ background: band }} /> : null}
          <div style={{ flex: 1, minWidth: 0 }}>{typeof title === "string" ? <h2 style={{ margin: 0 }}>{title}</h2> : title}</div>
          {actions}
          <button className="ghost" aria-label="Kapat" onClick={onClose}><Icon name="close" /></button>
        </div>
        <div className="modal-body">{children}</div>
      </div>
    </div>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return <label className="field">{label}{hint ? <span className="hint">{hint}</span> : null}{children}</label>;
}

/** A labelled group of several controls (checkbox lists, tables); a <label> may only wrap one control. */
export function FieldGroup({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return <fieldset className="field"><legend>{label}</legend>{hint ? <span className="hint">{hint}</span> : null}{children}</fieldset>;
}

export function Check({ label, hint, checked, onChange, disabled }: { label: string; hint?: string; checked: boolean; onChange: (value: boolean) => void; disabled?: boolean }) {
  return (
    <label className="check">
      <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      <span>{label}{hint ? <span className="hint">{hint}</span> : null}</span>
    </label>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

export function Loading() {
  return <div className="empty">Yükleniyor…</div>;
}

export function Stat({ label, value }: { label: string; value: ReactNode }) {
  return <div className="stat"><div className="label">{label}</div><div className="value">{value}</div></div>;
}

export function UsageBar({ percent }: { percent: number | null }) {
  if (percent === null) return <span className="muted small">limit yok</span>;
  const cls = percent >= 100 ? "bad" : percent >= 80 ? "warn" : "";
  return <div className={`bar ${cls}`} title={`%${percent}`}><span style={{ width: `${Math.min(percent, 100)}%` }} /></div>;
}

const AVATAR_COLORS = ["#8764b8", "#0078d4", "#038387", "#ca5010", "#498205", "#c239b3", "#4f6bed", "#986f0b"];

export function Avatar({ name, size }: { name: string | null | undefined; size?: "lg" | "xl" }) {
  const text = (name ?? "?").trim();
  const initials = text.split(/[\s.@_-]+/).filter(Boolean).slice(0, 2).map((s) => s[0]!.toUpperCase()).join("") || "?";
  const color = AVATAR_COLORS[[...text].reduce((h, c) => (h * 31 + c.charCodeAt(0)) >>> 0, 7) % AVATAR_COLORS.length];
  return <span className={`avatar${size ? ` ${size}` : ""}`} style={{ background: color }} title={name ?? undefined}>{initials}</span>;
}

export const hex = (color: string | null | undefined) => (color ? (color.startsWith("#") ? color : `#${color}`) : "#8a8886");

export function StateLabel({ state, color }: { state: string; color: string | null | undefined }) {
  return <span className="state"><i style={{ background: hex(color) }} />{state}</span>;
}

export function PageHead({ title, subtitle, children }: { title: ReactNode; subtitle?: ReactNode; children?: ReactNode }) {
  return (
    <div className="page-head">
      <div><h1>{title}</h1>{subtitle ? <div className="subtitle">{subtitle}</div> : null}</div>
      <span className="spacer" />
      {children}
    </div>
  );
}
