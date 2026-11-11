import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { apiDownload, apiRequest, apiUrl, setCsrfToken, subscribeAuthFailures } from "./apiClient";
import { safeReturnPath, startAccountFlow, submitAccountFlow, validateFlow } from "./accountApi";
import { browserFlow, jsonResponse } from "../test/accountFixtures";

beforeEach(() => setCsrfToken(null));
afterEach(() => vi.unstubAllGlobals());

describe("browser account transport", () => {
  it("uses only the configured API origin and never sends bearer credentials", async () => {
    const fetcher = vi.fn().mockResolvedValue(jsonResponse({}));
    vi.stubGlobal("fetch", fetcher);
    await apiRequest("/api/me");
    expect(fetcher).toHaveBeenCalledWith(apiUrl("/api/me"), expect.objectContaining({
      credentials: "include", cache: "no-store", redirect: "error",
      headers: { "Content-Type": "application/json" }
    }));
    for (const path of ["https://evil.test/api/me", "//evil.test/api/me", "/api/../../outside", "/api\\evil", "/outside"]) {
      await expect(apiRequest(path)).rejects.toThrow("Invalid API destination");
    }
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it("injects in-memory CSRF for mutations, not reads, and never replays mutations", async () => {
    const fetcher = vi.fn().mockResolvedValue(jsonResponse({ error: { code: "csrf_invalid", message: "Invalid" } }, 403));
    vi.stubGlobal("fetch", fetcher);
    setCsrfToken("synthetic-csrf");
    await expect(apiRequest("/api/apps", { method: "POST", body: { name: "Example" } })).rejects.toMatchObject({ status: 403 });
    expect(fetcher).toHaveBeenCalledTimes(1);
    expect(fetcher.mock.calls[0][1].headers).toHaveProperty("X-Minicloud-CSRF", "synthetic-csrf");
    setCsrfToken(null);
    await expect(apiRequest("/api/apps", { method: "POST" })).rejects.toMatchObject({ status: 401 });
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it("notifies confirmed expiry and identity outages, but not unrelated service errors", async () => {
    const listener = vi.fn();
    const unsubscribe = subscribeAuthFailures(listener);
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ error: { code: "provider_unavailable", message: "Retry" } }, 503))
      .mockResolvedValueOnce(jsonResponse({ error: { code: "auth_unavailable", message: "Retry" } }, 503))
      .mockResolvedValueOnce(jsonResponse({ error: { code: "unauthenticated", message: "Sign in" } }, 401));
    vi.stubGlobal("fetch", fetcher);
    await expect(apiRequest("/api/plans")).rejects.toMatchObject({ status: 503 });
    expect(listener).not.toHaveBeenCalled();
    await expect(apiDownload("/api/account/export")).rejects.toMatchObject({ status: 503 });
    expect(listener).toHaveBeenLastCalledWith(503);
    await expect(apiRequest("/api/me")).rejects.toMatchObject({ status: 401 });
    expect(listener).toHaveBeenLastCalledWith(401);
    unsubscribe();
  });

  it("preserves ordinary API field validation details", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({
      error: { code: "validation_failed", message: "Invalid", details: [{ field: "name", message: "Required" }, {}] }
    }, 400)));
    await expect(apiRequest("/api/apps")).rejects.toMatchObject({ details: [{ field: "name", message: "Required" }] });
  });

  it("preserves flow CSRF without requiring a local session and validates every form action", async () => {
    const flow = browserFlow();
    const fetcher = vi.fn().mockImplementation(async () => jsonResponse({ flow, session: null }));
    vi.stubGlobal("fetch", fetcher);
    await startAccountFlow("login", "/cli/login?session=request-1");
    expect(fetcher.mock.calls[0][0]).toContain("returnTo=%2Fcli%2Flogin%3Fsession%3Drequest-1");
    await submitAccountFlow(flow, { csrf_token: "upstream-csrf", password: "synthetic-password", method: "password" });
    expect(JSON.parse(fetcher.mock.calls[1][1].body).csrf_token).toBe("upstream-csrf");
    expect(fetcher.mock.calls[1][1].headers).not.toHaveProperty("X-Minicloud-CSRF");
    for (const action of ["https://evil.test/api/account", "//evil.test/api/account", "/api/account/logout"]) {
      expect(() => validateFlow({ ...flow, ui: { ...flow.ui, action } }, "login")).toThrow();
    }
    expect(() => validateFlow({ ...flow, kind: "settings" }, "login")).toThrow();
  });
});

it.each(["https://evil.test", "//evil.test", "/\\evil.test", "/%2f%2fevil.test", "/%5cevil", "/sign-in", "/recovery", "/\ninvalid"])(
  "rejects unsafe return path %s", value => expect(safeReturnPath(value)).toBe("/apps")
);
it("preserves dashboard deep links and query strings", () => {
  expect(safeReturnPath("/cli/login?session=request-1")).toBe("/cli/login?session=request-1");
  expect(safeReturnPath("/apps/app/environments/prod/services?service=web")).toBe("/apps/app/environments/prod/services?service=web");
  expect(safeReturnPath("/deployments/20261110.1?organizationId=org%2F2&tab=logs")).toBe("/deployments/20261110.1?organizationId=org%2F2&tab=logs");
});
