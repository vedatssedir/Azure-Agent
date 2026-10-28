import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { SettingsPage } from "./Settings";
import { mockApi, renderPage } from "../test/harness";
import { adminApp } from "../test/fixtures";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const automation = {
  autoDispatch: true, requireRunApproval: false, updateWorkItemState: true, autoReviewPullRequests: true, syncIntervalMinutes: 10, autoProvisionMembers: true,
  autoInviteTeamMembers: false, allowSelfSignUp: true, createDefaultAgentForDevelopers: true, requireUserCredentialForWrites: false, teamRoles: [], groupRoles: [],
  defaultRuntime: "ClaudeCode", defaultProvider: "Anthropic", defaultModel: "claude-sonnet-4-5", azureOpenAiEndpoint: null,
};
const settings = {
  azure: { organizationUrl: "https://dev.azure.com/contoso", organizationName: "contoso", credentialType: "pat", patConfigured: true, servicePrincipalConfigured: false,
    lastSyncedAt: null, lastSyncError: null, projects: 1, repositories: 4, mirroredRepositories: 3, teams: 1, workItems: 2, serviceHookPasswordConfigured: false },
  automation, monthlyBudgetUsd: null, entraEnabled: false, emailEnabled: false,
  providers: [
    { key: "anthropic", name: "Anthropic API", kind: "api", runtime: "Framework", provider: "Anthropic", defaultModel: "claude-sonnet-4-5", description: "Claude",
      credentials: [{ name: "ANTHROPIC_API_KEY", label: "API anahtarı", configured: false, updatedAt: null }], configured: false, installed: null, version: null, installHint: null },
  ],
};

describe("Settings", () => {
  it("shows what was synced from the Azure organization and syncs on demand", async () => {
    const calls = mockApi({ "GET /api/settings": () => settings, "POST /api/settings/azure/sync": () => ({ status: 202, body: {} }) });
    renderPage(<SettingsPage />, { path: "/settings/azure", route: "/settings/:tab", app: adminApp });
    expect(await screen.findByText("contoso")).toBeTruthy();
    expect(screen.getByText("(3)")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: /Şimdi senkronize et/ }));
    await waitFor(() => expect(calls.some((c) => c.path === "/api/settings/azure/sync")).toBe(true));
  });

  it("stores a provider key from its tab", async () => {
    const calls = mockApi({ "GET /api/settings": () => settings, "PUT /api/settings/ai/credentials/ANTHROPIC_API_KEY": () => ({ status: 204 }) });
    renderPage(<SettingsPage />, { path: "/settings/ai", route: "/settings/:tab", app: adminApp });
    expect(await screen.findByText("Anahtar gerekli")).toBeTruthy();
    await userEvent.type(screen.getByLabelText(/API anahtarı/), "sk-ant-123");
    await userEvent.click(screen.getAllByRole("button", { name: "Kaydet" })[0]!);
    await waitFor(() => expect(calls.find((c) => c.method === "PUT")?.body).toEqual({ value: "sk-ant-123" }));
  });
});
