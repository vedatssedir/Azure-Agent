import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Members } from "./Members";
import { mockApi, renderPage } from "../test/harness";
import { can, me, project } from "../test/fixtures";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const person = (uniqueName: string, extra: object = {}) => ({
  uniqueName, displayName: uniqueName.split("@")[0], teams: ["Test_Project Team"], mappedRole: "Developer", userId: null, role: null, source: null,
  azureConnected: false, invitationStatus: null, agentName: null, ...extra,
});

describe("Members → Azure teams", () => {
  it("shows each team member's status and invites the selected people", async () => {
    const calls = mockApi({
      "GET /api/projects/p-1/members": () => [{ userId: "u-1", email: "vedat@contoso.com", displayName: "Vedat", role: "Developer", azureUniqueName: "vedat@contoso.com", source: "AzureTeam" }],
      "GET /api/organizations/members": () => [],
      "GET /api/projects/p-1/people": () => [
        person("vedat@contoso.com", { userId: "u-1", role: "Developer", source: "AzureTeam", azureConnected: true, agentName: "Vedat (agent)" }),
        person("ayse@contoso.com"),
        person("mert@contoso.com", { invitationStatus: "Pending" }),
      ],
      "POST /api/projects/p-1/people/invite": () => [{ id: "i-1", email: "ayse@contoso.com", url: "https://agent.test/invite/abc", emailSent: false }],
    });
    renderPage(<Members project={project} me={me} can={can("project.view", "project.members.manage")} />);
    expect(await screen.findByText("Vedat (agent)")).toBeTruthy();
    expect(screen.getByText("Bağlı")).toBeTruthy();
    expect(screen.getByText("Davet bekliyor")).toBeTruthy();
    expect(screen.getAllByText("Azure takımı").length).toBeGreaterThan(0);
    // Only people without an account or a pending invitation can be selected.
    expect(screen.queryByLabelText("mert seç")).toBeNull();
    await userEvent.click(screen.getByLabelText("ayse seç"));
    await userEvent.click(screen.getByRole("button", { name: "Seçilenleri davet et (1)" }));
    expect(await screen.findByText("https://agent.test/invite/abc")).toBeTruthy();
    await waitFor(() => expect(calls.find((c) => c.method === "POST")!.body).toEqual({ people: [{ email: "ayse@contoso.com", displayName: "ayse" }] }));
  });
});
