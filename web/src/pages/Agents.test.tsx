import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Agents } from "./Agents";
import { mockApi, renderPage } from "../test/harness";
import { can, me, project } from "../test/fixtures";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("Agents → runtime", () => {
  it("creates a Claude Code CLI agent with the Anthropic provider and flags runtimes missing on the server", async () => {
    const calls = mockApi({
      "GET /api/projects/p-1/agents": () => [],
      "GET /api/projects/p-1/teams": () => [],
      "GET /api/agent-runtimes": () => [
        { runtime: "Framework", name: "Microsoft Agent Framework", available: true, version: null, detail: "Built in" },
        { runtime: "ClaudeCode", name: "Claude Code CLI", available: true, version: "2.1.312 (Claude Code)", detail: "claude" },
        { runtime: "CursorCli", name: "Cursor CLI", available: false, version: null, detail: "'cursor-agent' is not installed on this node" },
      ],
      "POST /api/projects/p-1/agents": (c) => ({ id: "a-1", ...(c.body as object), skills: [], mcpServers: [], activeRuns: 0, spentThisMonthUsd: 0 }),
    });
    renderPage(<Agents project={project} me={me} can={can("agents.view", "agents.manage")} />);
    await userEvent.click(await screen.findByRole("button", { name: "Yeni agent" }));
    const drawer = screen.getByRole("dialog");
    expect(await within(drawer).findByRole("option", { name: "Cursor CLI (kurulu değil)" })).toBeTruthy();
    await userEvent.type(within(drawer).getByLabelText("Ad"), "Vedat (Claude Code)");
    await userEvent.selectOptions(within(drawer).getByLabelText(/Çalışma ortamı/), "ClaudeCode");
    expect(within(drawer).queryByLabelText(/Sağlayıcı/)).toBeNull();
    expect(within(drawer).getByText(/2.1.312 \(Claude Code\)/)).toBeTruthy();
    expect(within(drawer).queryByLabelText(/Temperature/)).toBeNull();
    await userEvent.click(within(drawer).getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST")).toBe(true));
    expect(calls.find((c) => c.method === "POST")!.body).toMatchObject({ name: "Vedat (Claude Code)", runtime: "ClaudeCode", provider: "Anthropic", model: "claude-sonnet-4-5" });

    // Picking a runtime that is not installed explains what is missing.
    await userEvent.click(await screen.findByRole("button", { name: "Yeni agent" }));
    await userEvent.selectOptions(within(screen.getByRole("dialog")).getByLabelText(/Çalışma ortamı/), "CursorCli");
    expect(within(screen.getByRole("dialog")).getByText(/is not installed on this node/)).toBeTruthy();
  });
});
