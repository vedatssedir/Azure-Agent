import { StrictMode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AcceptInvitation, AuthCallback, Login } from "./Login";
import { mockApi, renderPage } from "../test/harness";

const auth = { accessToken: "a", accessTokenExpiresAt: "", refreshToken: "refresh-1", user: { id: "u", email: "x@y.z", displayName: "X", isSystemAdmin: false, hasPassword: false } };

beforeEach(() => localStorage.clear());
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("Login", () => {
  it("offers Microsoft sign-in when Entra ID is configured and redirects to the authorize URL", async () => {
    const assign = vi.fn();
    vi.stubGlobal("location", { ...window.location, assign });
    const calls = mockApi({
      "GET /api/auth/providers": () => ({ local: true, entra: true, entraName: "Contoso Entra" }),
      "POST /api/auth/entra/start": () => ({ authorizeUrl: "https://login.microsoftonline.com/x/authorize?state=s" }),
    });
    renderPage(<Login onSignedIn={vi.fn()} />);
    await userEvent.click(await screen.findByRole("button", { name: /Contoso Entra ile giriş yap/ }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith("https://login.microsoftonline.com/x/authorize?state=s"));
    expect(calls.some((c) => c.method === "POST" && c.path === "/api/auth/entra/start")).toBe(true);
  });

  it("shows only the password form without Entra ID", async () => {
    mockApi({ "GET /api/auth/providers": () => ({ local: true, entra: false, entraName: null }) });
    renderPage(<Login onSignedIn={vi.fn()} />);
    await screen.findByRole("button", { name: "Giriş yap" });
    expect(screen.queryByRole("button", { name: /ile giriş yap/ })).toBeNull();
  });
});

describe("AuthCallback", () => {
  it("redeems the code exactly once, even under StrictMode, and stores the session", async () => {
    const calls = mockApi({ "POST /api/auth/entra/callback": () => auth });
    const onSignedIn = vi.fn();
    renderPage(<StrictMode><AuthCallback onSignedIn={onSignedIn} /></StrictMode>, { path: "/auth/callback?code=c-1&state=s-1", route: "/auth/callback" });
    await waitFor(() => expect(onSignedIn).toHaveBeenCalledOnce());
    expect(calls.filter((c) => c.path === "/api/auth/entra/callback")).toHaveLength(1);
    expect(calls[0].body).toEqual({ code: "c-1", state: "s-1" });
    expect(localStorage.getItem("azure-agent.refresh")).toBe("refresh-1");
  });

  it("shows the error Entra ID returned", async () => {
    mockApi({});
    renderPage(<AuthCallback onSignedIn={vi.fn()} />, { path: "/auth/callback?error=access_denied&error_description=User+cancelled", route: "/auth/callback" });
    expect(await screen.findByText("User cancelled")).toBeTruthy();
  });
});

describe("AcceptInvitation", () => {
  it("lets a new person choose a name and password", async () => {
    const calls = mockApi({
      "GET /api/auth/invitations/tok%2B1": () => ({ email: "nur@contoso.com", displayName: "Nur", organizationName: "Contoso", expiresAt: "", hasAccount: false, ssoEnabled: false }),
      "GET /api/auth/providers": () => ({ local: true, entra: false, entraName: null }),
      "POST /api/auth/invitations/accept": () => auth,
    });
    const onSignedIn = vi.fn();
    renderPage(<AcceptInvitation onSignedIn={onSignedIn} />, { path: "/invite/tok+1", route: "/invite/:token" });
    expect(await screen.findByText(/organizasyonuna davet edildiniz/)).toBeTruthy();
    await userEvent.type(screen.getByLabelText(/Şifre belirleyin/), "Sup3rSecretPass");
    await userEvent.click(screen.getByRole("button", { name: "Daveti kabul et" }));
    await waitFor(() => expect(onSignedIn).toHaveBeenCalled());
    expect(calls.find((c) => c.method === "POST")!.body).toEqual({ token: "tok+1", displayName: "Nur", password: "Sup3rSecretPass" });
  });

  it("asks existing accounts for their current password", async () => {
    mockApi({
      "GET /api/auth/invitations/t2": () => ({ email: "ali@contoso.com", displayName: "Ali", organizationName: "Contoso", expiresAt: "", hasAccount: true, ssoEnabled: false }),
      "GET /api/auth/providers": () => ({ local: true, entra: false, entraName: null }),
    });
    renderPage(<AcceptInvitation onSignedIn={vi.fn()} />, { path: "/invite/t2", route: "/invite/:token" });
    expect(await screen.findByLabelText(/Mevcut şifreniz/)).toBeTruthy();
    expect(screen.queryByLabelText(/Şifre belirleyin/)).toBeNull();
  });

  it("explains used or expired links", async () => {
    mockApi({
      "GET /api/auth/invitations/used": () => ({ status: 404, body: { title: "Invitation not found" } }),
      "GET /api/auth/providers": () => ({ local: true, entra: false, entraName: null }),
    });
    renderPage(<AcceptInvitation onSignedIn={vi.fn()} />, { path: "/invite/used", route: "/invite/:token" });
    expect(await screen.findByText(/süresi dolmuş ya da kullanılmış/)).toBeTruthy();
  });
});
