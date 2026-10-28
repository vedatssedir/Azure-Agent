import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AuthPages } from "./Login";
import { mockApi, renderPage } from "../test/harness";

const auth = { accessToken: "a", accessTokenExpiresAt: "", refreshToken: "r", user: { id: "u", email: "x@y.z", displayName: "X", isSystemAdmin: false, hasPassword: true } };
const providers = () => ({ local: true, entra: false, entraName: null });

beforeEach(() => localStorage.clear());
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("First-run setup", () => {
  it("asks once for the admin account and the Azure organization, then signs in", async () => {
    const calls = mockApi({ "GET /api/auth/status": () => ({ setupRequired: true, selfSignUp: false }), "GET /api/auth/providers": providers, "POST /api/auth/setup": () => auth });
    const onSignedIn = vi.fn();
    renderPage(<AuthPages onSignedIn={onSignedIn} />);
    await userEvent.type(await screen.findByLabelText("Adınız"), "Vedat");
    await userEvent.type(screen.getByLabelText(/^E-posta/), "vedat@contoso.com");
    await userEvent.type(screen.getByLabelText(/^Şifre/), "Sup3rSecretPass");
    await userEvent.type(screen.getByLabelText(/Organizasyon URL/), "https://dev.azure.com/contoso");
    await userEvent.type(screen.getByLabelText(/PAT/), " abcdefghijklmnopqrstuvwxyz0123 ");
    await userEvent.click(screen.getByRole("button", { name: "Bağlan ve senkronize et" }));
    await waitFor(() => expect(onSignedIn).toHaveBeenCalled());
    expect(calls.find((c) => c.path === "/api/auth/setup")!.body).toEqual({
      displayName: "Vedat", email: "vedat@contoso.com", password: "Sup3rSecretPass", azureOrganizationUrl: "https://dev.azure.com/contoso", personalAccessToken: "abcdefghijklmnopqrstuvwxyz0123",
    });
  });
});

describe("Developer sign-up", () => {
  it("is offered when self sign-up is on and sends the PAT that proves the Azure identity", async () => {
    const calls = mockApi({ "GET /api/auth/status": () => ({ setupRequired: false, selfSignUp: true }), "GET /api/auth/providers": providers, "POST /api/auth/signup": () => auth });
    const onSignedIn = vi.fn();
    renderPage(<AuthPages onSignedIn={onSignedIn} />);
    await userEvent.click(await screen.findByRole("link", { name: /Azure hesabınızla kaydolun/ }));
    await userEvent.type(screen.getByLabelText("Adınız"), "Nur");
    await userEvent.type(screen.getByLabelText(/Azure DevOps e-postanız/), "nur@contoso.com");
    await userEvent.type(screen.getByLabelText(/^Şifre/), "Sup3rSecretPass");
    await userEvent.type(screen.getByLabelText(/PAT/), "abcdefghijklmnopqrstuvwxyz0123");
    await userEvent.click(screen.getByRole("button", { name: "Hesap oluştur" }));
    await waitFor(() => expect(onSignedIn).toHaveBeenCalled());
    expect(calls.find((c) => c.path === "/api/auth/signup")!.body).toEqual({ email: "nur@contoso.com", displayName: "Nur", password: "Sup3rSecretPass", personalAccessToken: "abcdefghijklmnopqrstuvwxyz0123" });
  });

  it("is hidden when self sign-up is off", async () => {
    mockApi({ "GET /api/auth/status": () => ({ setupRequired: false, selfSignUp: false }), "GET /api/auth/providers": providers });
    renderPage(<AuthPages onSignedIn={vi.fn()} />);
    await screen.findByRole("button", { name: "Giriş yap" });
    expect(screen.queryByText(/kaydolun/)).toBeNull();
  });
});
