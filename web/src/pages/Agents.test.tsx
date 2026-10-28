import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Agents } from "./Agents";
import { mockApi, renderPage } from "../test/harness";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const providers = {
  defaultRuntime: "ClaudeCode", defaultProvider: "Anthropic", defaultModel: "claude-sonnet-4-5",
  providers: [
    { key: "claude-code", name: "Claude Code CLI", kind: "cli", runtime: "ClaudeCode", provider: "Anthropic", defaultModel: "claude-sonnet-4-5", description: "Claude Code", configured: true, installed: true, version: "2.0" },
    { key: "openai", name: "OpenAI", kind: "api", runtime: "Framework", provider: "OpenAI", defaultModel: "gpt-5", description: "GPT", configured: false, installed: null, version: null },
  ],
};

describe("Agents", () => {
  it("creates an agent with its own runtime, instructions and skills", async () => {
    const calls = mockApi({
      "GET /api/agents?mine=true": () => [],
      "GET /api/settings/providers": () => providers,
      "POST /api/agents": (c) => ({ id: "a-1", ...(c.body as object) }),
    });
    renderPage(<Agents />);
    await userEvent.click(await screen.findByRole("button", { name: /Yeni agent/ }));
    await userEvent.type(screen.getByLabelText("Ad"), "Backend dev");
    await userEvent.click(screen.getByRole("button", { name: "Çalışma ortamı" }));
    await userEvent.click(await screen.findByRole("button", { name: /OpenAI/ }));
    expect(screen.getByText(/OpenAI henüz kullanılamıyor/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: /Claude Code CLI/ }));
    await userEvent.click(screen.getByRole("button", { name: "Talimatlar" }));
    await userEvent.type(screen.getByLabelText(/Talimatlar \(system prompt\)/), "Write tests.");
    await userEvent.click(screen.getByRole("button", { name: /Skill'ler/ }));
    await userEvent.click(screen.getByRole("button", { name: /Skill ekle/ }));
    const name = screen.getByLabelText("Skill adı");
    await userEvent.clear(name);
    await userEvent.type(name, "ef-migrations");
    await userEvent.click(screen.getByRole("button", { name: "Oluştur" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path === "/api/agents")).toBe(true));
    const body = calls.find((c) => c.method === "POST")!.body as Record<string, unknown>;
    expect(body).toMatchObject({ name: "Backend dev", kind: "Developer", runtime: "ClaudeCode", provider: "Anthropic", model: "claude-sonnet-4-5", instructions: "Write tests.", ownerUserId: "u-1" });
    expect(body.skills).toEqual([expect.objectContaining({ name: "ef-migrations", enabled: true })]);
  });
});
