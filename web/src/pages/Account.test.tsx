import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Account } from "./Account";
import { mockApi, renderPage } from "../test/harness";
import { app, me } from "../test/fixtures";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("Account → Azure DevOps connection", () => {
  it("verifies a PAT for the project's Azure organization and reports a foreign token", async () => {
    let attempt = 0;
    const calls = mockApi({
      "GET /api/me/azure-credentials": () => [],
      "PUT /api/me/azure-credentials/pat": () => (attempt++ === 0
        ? { status: 422, body: { title: "This PAT belongs to joe@contoso.com, not to your account (vedat@contoso.com)" } }
        : { id: "c-1", kind: "PersonalAccessToken", azureOrganizationUrl: "https://dev.azure.com/contoso", azureUniqueName: "vedat@contoso.com", azureDisplayName: "Vedat", verifiedAt: "", expiresAt: null, lastError: null }),
    });
    renderPage(<Account />);
    await screen.findByRole("option", { name: "https://dev.azure.com/contoso" });
    const pat = screen.getByLabelText(/^PAT/);
    await userEvent.type(pat, "abcdefghijklmnopqrstuvwxyz012345");
    await userEvent.click(screen.getByRole("button", { name: "Doğrula ve kaydet" }));
    expect(await screen.findByText(/belongs to joe@contoso.com/)).toBeTruthy();
    expect((pat as HTMLInputElement).value).toBe("abcdefghijklmnopqrstuvwxyz012345");
    await userEvent.click(screen.getByRole("button", { name: "Doğrula ve kaydet" }));
    expect(await screen.findByText("Bağlandı: vedat@contoso.com")).toBeTruthy();
    await waitFor(() => expect((pat as HTMLInputElement).value).toBe(""));
    expect(calls.filter((c) => c.method === "PUT")[1].body).toEqual({
      azureOrganizationUrl: "https://dev.azure.com/contoso", personalAccessToken: "abcdefghijklmnopqrstuvwxyz012345", expiresAt: null,
    });
  });

  it("lets password-less (Microsoft) accounts set a password without the current one", async () => {
    const calls = mockApi({ "GET /api/me/azure-credentials": () => [], "POST /api/auth/change-password": () => ({ status: 204 }) });
    renderPage(<Account />, { app: { ...app, me: { ...me, user: { ...me.user, hasPassword: false } } } });
    expect(await screen.findByRole("heading", { name: "Şifre belirle" })).toBeTruthy();
    expect(screen.queryByLabelText("Mevcut şifre")).toBeNull();
    await userEvent.type(screen.getByLabelText(/Yeni şifre/), "Sup3rSecretPass");
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")!.body).toEqual({ currentPassword: null, newPassword: "Sup3rSecretPass" }));
  });
});
