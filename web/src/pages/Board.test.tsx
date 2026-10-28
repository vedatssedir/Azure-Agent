import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Board } from "./Board";
import { mockApi, renderPage } from "../test/harness";
import { can, me, project } from "../test/fixtures";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const item = (azureId: number, title: string, state = "To Do") => ({
  azureId, type: "Issue", title, state, assignedToUniqueName: null, assignedToDisplayName: null, priority: 2, storyPoints: null, repositoryNames: [],
  iterationPath: null, webUrl: "", changedAt: "", lastRunStatus: null, agentName: null, costUsd: 0,
});

describe("Board", () => {
  it("filters by team and unassigned through the API", async () => {
    const calls = mockApi({
      "GET /api/projects/p-1/teams": () => [{ id: "t-1", name: "Backend", members: [], areaPaths: ["Test_Project"] }],
      "GET /api/projects/p-1/work-items": () => [item(1, "Everything")],
      "GET /api/projects/p-1/work-items?team=t-1": () => [item(2, "Team item")],
      "GET /api/projects/p-1/work-items?unassigned=true": () => [item(3, "Nobody's item")],
    });
    renderPage(<Board project={project} me={me} can={can("workitems.view")} />);
    expect(await screen.findByText("Everything")).toBeTruthy();
    const view = screen.getByLabelText("Görünüm");
    await screen.findByRole("option", { name: "Takım: Backend" });
    await userEvent.selectOptions(view, "team:t-1");
    expect(await screen.findByText("Team item")).toBeTruthy();
    await userEvent.selectOptions(view, "unassigned");
    expect(await screen.findByText("Nobody's item")).toBeTruthy();
    expect(calls.map((c) => c.path)).toContain("/api/projects/p-1/work-items?team=t-1");
    // Creating needs its own permission.
    expect(screen.queryByRole("button", { name: "Yeni madde" })).toBeNull();
  });

  it("creates a work item in Azure with the selected repositories and opens it", async () => {
    const created = { ...item(42, "Add PaginatedList tests"), repositoryNames: ["EFCore.GenericRepository"] };
    const calls = mockApi({
      "GET /api/projects/p-1/teams": () => [],
      "GET /api/projects/p-1/work-items": () => [],
      "GET /api/projects/p-1/work-item-types": () => [{ name: "Issue", states: ["To Do", "Doing", "Done"], fields: [] }, { name: "Task", states: [], fields: [] }],
      "GET /api/projects/p-1/repositories": () => [
        { id: "r-1", name: "EFCore.GenericRepository", defaultBranch: "master", webUrl: "", policy: {}, openPullRequests: 0, lastCommitAt: null },
        { id: "r-2", name: "Empty", defaultBranch: null, webUrl: "", policy: {}, openPullRequests: 0, lastCommitAt: null },
      ],
      "POST /api/projects/p-1/work-items": () => created,
      "GET /api/projects/p-1/work-items/42": () => ({ summary: created, description: "", acceptanceCriteria: "", areaPath: null, tags: [], runs: [], pullRequests: [], commits: [] }),
    });
    renderPage(<Board project={project} me={me} can={can("workitems.view", "workitems.create")} />);
    await userEvent.click(await screen.findByRole("button", { name: "Yeni madde" }));
    const drawer = screen.getByRole("dialog");
    await within(drawer).findByRole("option", { name: "Issue" });
    await userEvent.type(within(drawer).getByLabelText("Başlık"), "Add PaginatedList tests");
    await userEvent.type(within(drawer).getByLabelText(/Açıklama/), "Cover first/last page");
    expect((within(drawer).getByLabelText(/Empty/) as HTMLInputElement).disabled).toBe(true);
    await userEvent.click(within(drawer).getByLabelText(/EFCore.GenericRepository/));
    await userEvent.click(within(drawer).getByRole("button", { name: "Azure'da oluştur" }));
    await waitFor(() => expect(screen.getByRole("dialog").textContent).toContain("Issue #42"));
    expect(calls.find((c) => c.method === "POST")!.body).toEqual({
      type: "Issue", title: "Add PaginatedList tests", description: "Cover first/last page", acceptanceCriteria: "",
      repositoryNames: ["EFCore.GenericRepository"], assignToMe: true, priority: 2, tags: [],
    });
  });
});
