import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Repos } from "./Repos";
import { mockApi, renderPage } from "../test/harness";
import { PROJECT_ID } from "../test/fixtures";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const REPO = "33333333-3333-3333-3333-333333333333";
const repo = { id: REPO, projectId: PROJECT_ID, projectName: "Test Project", name: "api", defaultBranch: "main", webUrl: "https://x", sizeBytes: 2048, mirrorSyncedAt: "2026-10-01T00:00:00Z",
  mirrorError: null, openPullRequests: 0, policy: { agentsEnabled: true, protectedBranches: ["main"], autoReview: true, allowedCommands: [], maxFilesChangedPerRun: 50 } };
const commit = { commitId: "abcdef1234567890", authorName: "Veysel", authorEmail: "v@x", authoredAt: "2026-10-01T00:00:00Z", subject: "Add paging", message: "Add paging", parentIds: [] };

describe("Repos", () => {
  it("browses the mirrored tree and shows a file with its latest commit", async () => {
    mockApi({
      [`GET /api/repos?project=${PROJECT_ID}`]: () => [repo],
      [`GET /api/repos/${REPO}`]: () => ({ repository: repo, branches: [{ name: "main", commitId: "abc", committedAt: "", subject: "" }], canChangePolicy: false }),
      [`GET /api/repos/${REPO}/tree?ref=main`]: () => [{ name: "src", path: "src", isDirectory: true, size: 0 }, { name: "README.md", path: "README.md", isDirectory: false, size: 10 }],
      [`GET /api/repos/${REPO}/tree?ref=main&path=src`]: () => [{ name: "Paging.cs", path: "src/Paging.cs", isDirectory: false, size: 40 }],
      [`GET /api/repos/${REPO}/commits`]: () => [commit],
      [`GET /api/repos/${REPO}/file?ref=main&path=README.md`]: () => ({ path: "README.md", size: 10, isBinary: false, isTruncated: false, content: "# API" }),
      [`GET /api/repos/${REPO}/file?ref=main&path=src%2FPaging.cs`]: () => ({ path: "src/Paging.cs", size: 40, isBinary: false, isTruncated: false, content: "public class Paging {}\n" }),
    });
    renderPage(<Repos />, { path: `/p/${PROJECT_ID}/repos/files`, route: "/p/:projectId/repos/:view" });
    expect(await screen.findByText("# API")).toBeTruthy();
    await userEvent.click((await screen.findAllByText("src"))[0]!);
    await userEvent.click((await screen.findAllByText("Paging.cs"))[0]!);
    expect(await screen.findByText("Paging")).toBeTruthy();
    expect(screen.getAllByText("Add paging").length).toBeGreaterThan(0);
  });
});
