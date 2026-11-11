// @vitest-environment-options {"url":"https://console.example.test"}
import { afterEach, describe, expect, it, vi } from "vitest";
import { completeSetup, getSetupStatus, setupErrorMessage } from "./setupApi";
import { subscribeAuthFailures, setCsrfToken } from "./apiClient";
import { ApiError } from "../models/controlPlane";
import { jsonResponse } from "../test/accountFixtures";

vi.mock("../core/config/runtimeConfig", () => ({
  runtimeConfig: { apiBaseUrl: "https://api.example.test" }
}));

afterEach(() => { vi.unstubAllGlobals(); setCsrfToken(null); });

describe("setup transport", () => {
  it("sends admission only in bounded-route HTTPS JSON, never URLs, storage or session issuance", async () => {
    const fetcher = vi.fn().mockResolvedValue(jsonResponse({ state: "ready" }));
    const storage = vi.spyOn(Storage.prototype, "setItem");
    vi.stubGlobal("fetch", fetcher);
    const body = {
      setupToken: "synthetic-token", email: "operator@example.test", password: "synthetic-password",
      displayName: "Operator", organizationName: "Workspace", organizationSlug: "workspace"
    };
    await completeSetup(body);
    expect(fetcher).toHaveBeenCalledOnce();
    expect(fetcher).toHaveBeenCalledWith("https://api.example.test/api/account/setup", expect.objectContaining({
      method: "POST", cache: "no-store", credentials: "include", redirect: "error",
      headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
    }));
    expect(storage).not.toHaveBeenCalled();
    storage.mockRestore();
  });

  it("does not turn invalid setup tokens or dependency outages into ordinary session failure", async () => {
    const listener = vi.fn();
    const unsubscribe = subscribeAuthFailures(listener);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(jsonResponse({
      error: { code: "setup_token_invalid", message: "private token diagnostic" }
    }, 401)).mockResolvedValueOnce(jsonResponse({
      error: { code: "auth_unavailable", message: "private identity diagnostic" }
    }, 503)));
    await expect(getSetupStatus()).rejects.toMatchObject({ status: 401 });
    await expect(getSetupStatus()).rejects.toMatchObject({ status: 503 });
    expect(listener).not.toHaveBeenCalled();
    unsubscribe();
  });

  it("fails closed on an unknown setup state", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({ state: "unknown" })));
    await expect(getSetupStatus()).rejects.toThrow("Invalid setup state");
  });

  it("refuses to submit secrets from an insecure dashboard before issuing a request", () => {
    const fetcher = vi.fn();
    vi.stubGlobal("fetch", fetcher);
    vi.stubGlobal("window", { location: { protocol: "http:" } });
    expect(() => completeSetup({
      setupToken: "synthetic-token", email: "operator@example.test", password: "synthetic-password",
      displayName: "Operator", organizationName: "Workspace", organizationSlug: "workspace"
    })).toThrow("Setup requires HTTPS");
    expect(fetcher).not.toHaveBeenCalled();
  });

  it.each([
    ["setup_token_invalid", "invalid"],
    ["setup_token_expired", "rotate"],
    ["setup_already_completed", "Sign in"],
    ["setup_in_progress", "reconciliation"],
    ["setup_rate_limited", "Wait a minute"],
    ["auth_unavailable", "temporarily unavailable"]
  ])("uses safe guidance for %s, never upstream diagnostics", (code, text) => {
    const message = setupErrorMessage(new ApiError(409, { code, message: "synthetic-password token diagnostic", details: [] }));
    expect(message).toContain(text);
    expect(message).not.toContain("synthetic-password");
  });
});
