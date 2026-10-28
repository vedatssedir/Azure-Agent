import { createContext, useContext } from "react";
import type { Me, OrganizationRole, Project } from "./api";

export interface AppState {
  me: Me;
  organization: { organizationId: string; name: string; slug: string; role: OrganizationRole };
  isAdmin: boolean;
  projects: Project[];
}

export const AppContext = createContext<AppState | null>(null);

export function useApp() {
  const state = useContext(AppContext);
  if (!state) throw new Error("useApp outside AppContext");
  return state;
}

const LAST_PROJECT = "azure-agent.project";
export const lastProject = {
  get: () => { try { return localStorage.getItem(LAST_PROJECT); } catch { return null; } },
  set: (id: string) => { try { localStorage.setItem(LAST_PROJECT, id); } catch { /* storage unavailable */ } },
};
