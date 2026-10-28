import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError, session } from "./api";
import { mockApi } from "./test/harness";

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("api client", () => {
  it("refreshes an expired access token once and retries with the new one", async () => {
    session.store({ accessToken: "old", accessTokenExpiresAt: "", refreshToken: "r-1", user: {} as never });
    let first = true;
    const calls = mockApi({
      "GET /api/projects": (c) => {
        if (first) { first = false; return { status: 401, body: {} }; }
        return c.headers.Authorization === "Bearer new" ? [{ id: "p" }] : { status: 401, body: {} };
      },
      "POST /api/auth/refresh": () => ({ accessToken: "new", accessTokenExpiresAt: "", refreshToken: "r-2", user: {} }),
    });
    await expect(api("/api/projects")).resolves.toEqual([{ id: "p" }]);
    expect(calls.map((c) => c.path)).toEqual(["/api/projects", "/api/auth/refresh", "/api/projects"]);
    expect(calls[1].body).toEqual({ refreshToken: "r-1" });
    expect(localStorage.getItem("azure-agent.refresh")).toBe("r-2");
  });

  it("signs out when the refresh token is rejected", async () => {
    session.store({ accessToken: "old", accessTokenExpiresAt: "", refreshToken: "r-1", user: {} as never });
    const signedOut = vi.fn();
    const unsubscribe = session.onSignOut(signedOut);
    mockApi({ "GET /api/projects": () => ({ status: 401, body: {} }), "POST /api/auth/refresh": () => ({ status: 401, body: {} }), "POST /api/auth/logout": () => ({ status: 204 }) });
    await expect(api("/api/projects")).rejects.toMatchObject({ status: 401 });
    expect(signedOut).toHaveBeenCalledOnce();
    expect(session.signedIn).toBe(false);
    unsubscribe();
  });

  it("turns validation problems into one readable message and sends the organization header", async () => {
    session.organizationId = "org-9";
    session.store({ accessToken: "t", accessTokenExpiresAt: "", refreshToken: "r", user: {} as never });
    const calls = mockApi({ "POST /api/projects": () => ({ status: 400, body: { title: "Validation failed", errors: { Key: ["Key must be 2-10 uppercase letters"] } } }) });
    const error = await api("/api/projects", { body: { key: "x" } }).catch((e: ApiError) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).message).toBe("Validation failed: Key must be 2-10 uppercase letters");
    expect(calls[0].headers["X-Organization-Id"]).toBe("org-9");
  });
});
