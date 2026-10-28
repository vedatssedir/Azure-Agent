import type { Me, Project } from "../api";

export const me: Me = {
  user: { id: "u-1", email: "vedat@contoso.com", displayName: "Vedat", isSystemAdmin: false, hasPassword: true },
  organizations: [{ organizationId: "org-1", name: "Contoso", slug: "contoso", role: "Member" }],
};

export const project: Project = {
  id: "p-1", name: "Test Project", key: "TEST", description: null, azureOrganizationUrl: "https://dev.azure.com/contoso", azureProjectName: "Test_Project",
  settings: {
    autoDispatch: true, requireRunApproval: false, reviewState: "Doing", closedStates: ["Done"], autoReviewPullRequests: true, syncIntervalMinutes: 5,
    autoProvisionMembers: true, autoInviteTeamMembers: false, requireUserCredentialForWrites: false, teamRoles: [], groupRoles: [],
  },
  monthlyBudgetUsd: null, lastSyncedAt: null, lastSyncError: null, myPermissions: [],
};

export const can = (...permissions: string[]) => (permission: string) => permissions.includes(permission);
