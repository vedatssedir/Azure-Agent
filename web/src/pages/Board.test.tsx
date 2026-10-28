import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { BoardPage } from "./Board";
import { mockApi, renderPage } from "../test/harness";
import { PROJECT_ID, workItem } from "../test/fixtures";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const TEAM = "22222222-2222-2222-2222-222222222222";
const board = (todo = [workItem()], doing = [] as ReturnType<typeof workItem>[]) => ({
  teamId: TEAM, teamName: "Test Team", board: "Issues", boards: ["Issues", "Epics"],
  columns: [
    { name: "To Do", columnType: "incoming", itemLimit: 0, stateMappings: { Issue: "To Do" }, items: todo },
    { name: "Doing", columnType: "inProgress", itemLimit: 1, stateMappings: { Issue: "Doing" }, items: doing },
    { name: "Done", columnType: "outgoing", itemLimit: 0, stateMappings: { Issue: "Done" }, items: [] },
  ],
});

describe("Board", () => {
  it("shows Azure's columns with WIP limits and moves a dropped card to that column in Azure", async () => {
    let moved = false;
    const calls = mockApi({
      [`GET /api/projects/${PROJECT_ID}/teams`]: () => [{ id: TEAM, name: "Test Team", members: [], areaPaths: [], boards: ["Issues"], iterations: [] }],
      [`GET /api/projects/${PROJECT_ID}/board`]: () => (moved ? board([], [workItem({ state: "Doing", boardColumn: "Doing" })]) : board()),
      [`POST /api/projects/${PROJECT_ID}/work-items/1/move`]: () => { moved = true; return { status: 204 }; },
    });
    renderPage(<BoardPage />, { path: `/p/${PROJECT_ID}/board`, route: "/p/:projectId/board" });
    const card = await screen.findByRole("button", { name: /Issue 1: Login sayfası/ });
    const doing = screen.getByRole("region", { name: "Doing" });
    expect(within(doing).getByText("0/1")).toBeTruthy();
    fireEvent.dragStart(card);
    fireEvent.dragOver(doing);
    fireEvent.drop(doing);
    await waitFor(() => expect(calls.some((c) => c.path.endsWith("/work-items/1/move"))).toBe(true));
    expect(calls.find((c) => c.path.endsWith("/move"))!.body).toEqual({ teamId: TEAM, board: "Issues", column: "Doing" });
    await waitFor(() => expect(within(screen.getByRole("region", { name: "Doing" })).getByText("Login sayfası")).toBeTruthy());
  });

  it("filters to my items through the server", async () => {
    const calls = mockApi({
      [`GET /api/projects/${PROJECT_ID}/teams`]: () => [],
      [`GET /api/projects/${PROJECT_ID}/board`]: () => board(),
    });
    renderPage(<BoardPage />, { path: `/p/${PROJECT_ID}/board`, route: "/p/:projectId/board" });
    await screen.findByText("Login sayfası");
    fireEvent.click(screen.getByLabelText(/Sadece bana atananlar/));
    await waitFor(() => expect(calls.some((c) => c.path === `/api/projects/${PROJECT_ID}/board?mine=true`)).toBe(true));
  });
});
