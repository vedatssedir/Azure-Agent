import type { ReactNode } from "react";
import { render } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { vi } from "vitest";

export interface Call { method: string; path: string; body: unknown; headers: Record<string, string> }

type Handler = (call: Call) => { status?: number; body?: unknown } | unknown;

/**
 * Fake backend: routes "METHOD /path" (query string included when given) to handlers and
 * records every call, so tests assert on what the UI sent.
 */
export function mockApi(routes: Record<string, Handler>) {
  const calls: Call[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const call: Call = {
      method: init?.method ?? "GET",
      path: url.pathname + url.search,
      body: init?.body ? JSON.parse(String(init.body)) : undefined,
      headers: (init?.headers ?? {}) as Record<string, string>,
    };
    calls.push(call);
    const handler = routes[`${call.method} ${call.path}`] ?? routes[`${call.method} ${url.pathname}`];
    if (!handler) return new Response(JSON.stringify({ title: `No mock for ${call.method} ${call.path}` }), { status: 500 });
    const result = handler(call);
    const { status = 200, body = result } = result && typeof result === "object" && "status" in (result as object) ? result as { status: number; body?: unknown } : { body: result };
    return status === 204 ? new Response(null, { status }) : new Response(JSON.stringify(body ?? {}), { status, headers: { "Content-Type": "application/json" } });
  });
  vi.stubGlobal("fetch", fetchMock);
  return calls;
}

export function renderPage(ui: ReactNode, { path = "/", route = "/" }: { path?: string; route?: string } = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Routes><Route path={route} element={ui} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}
