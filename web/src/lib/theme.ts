import { useEffect, useState } from "react";

export type Theme = "light" | "dark" | "system";
const KEY = "azure-agent.theme";

function stored(): Theme {
  try {
    const value = localStorage.getItem(KEY);
    return value === "light" || value === "dark" ? value : "system";
  } catch {
    return "system";
  }
}

function apply(theme: Theme) {
  const dark = theme === "dark" || (theme === "system" && window.matchMedia?.("(prefers-color-scheme: dark)").matches);
  document.documentElement.classList.toggle("dark", !!dark);
}

/** Light / dark / system theme, remembered per browser; index.html applies it before the first paint. */
export function useTheme() {
  const [theme, setTheme] = useState<Theme>(stored);
  useEffect(() => {
    apply(theme);
    try { localStorage.setItem(KEY, theme); } catch { /* private mode: keep it for this tab only */ }
    if (theme !== "system" || !window.matchMedia) return;
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => apply("system");
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, [theme]);
  return [theme, setTheme] as const;
}
