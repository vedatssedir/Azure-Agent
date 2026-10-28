import type { AppState } from "../context";
import type { Me, Project, WorkItem } from "../api";

export const me: Me = {
  user: { id: "u-1", email: "vedat@contoso.com", displayName: "Vedat", isSystemAdmin: false, hasPassword: true },
  organizations: [{ organizationId: "org-1", name: "Contoso", slug: "contoso", role: "Member" }],
};

export const PROJECT_ID = "11111111-1111-1111-1111-111111111111";

export const project: Project = {
  id: PROJECT_ID, name: "Test Project", key: "TEST", description: null, azureProjectName: "Test_Project", webUrl: "https://dev.azure.com/contoso/Test_Project",
  lastSyncedAt: null, lastSyncError: null, myRole: "Developer", myPermissions: ["workitems.view", "workitems.assign", "workitems.create", "agents.run", "repo.view"],
  openWorkItems: 2, assignedToMe: 1, repositories: 1, teams: 1, monthlyBudgetUsd: null,
};

export const app: AppState = { me, organization: me.organizations[0]!, isAdmin: false, projects: [project] };
export const adminApp: AppState = { ...app, isAdmin: true, organization: { ...me.organizations[0]!, role: "Admin" } };

export const workItem = (overrides: Partial<WorkItem> = {}): WorkItem => ({
  azureId: 1, type: "Issue", typeColor: "339947", title: "Login sayfası", state: "To Do", stateColor: "b2b2b2", stateCategory: "Proposed", boardColumn: "To Do",
  assignedToUniqueName: "vedat@contoso.com", assignedToDisplayName: "Vedat", priority: 2, storyPoints: null, tags: [], repositoryNames: [], areaPath: "Test_Project",
  iterationPath: "Test_Project\\Sprint 1", webUrl: "https://dev.azure.com/contoso/Test_Project/_workitems/edit/1", changedAt: "2026-10-01T10:00:00Z",
  lastRunStatus: null, agentName: null, costUsd: 0, ...overrides,
});
