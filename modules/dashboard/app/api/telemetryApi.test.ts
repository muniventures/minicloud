import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  buildGrafanaUrl,
  exchangeGrafanaLaunch,
  getTelemetrySettings,
  issueTelemetryCredential,
  launchGrafanaSession,
  listTelemetryCredentials,
  queryApplicationLogs,
  revokeTelemetryCredential,
  updateTelemetrySettings
} from "./telemetryApi";

vi.mock("../core/auth/firebase", () => ({
  getCurrentIdToken: vi.fn().mockResolvedValue("firebase-token")
}));

function jsonResponse(payload: unknown, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => "application/json" },
    json: async () => payload
  } as unknown as Response;
}

describe("telemetryApi", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({})));
  });

  it("fetches settings and credentials with Firebase auth", async () => {
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse({ appId: "app_1", enabled: true }));
    await getTelemetrySettings("app_1");
    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining("/v1/apps/app_1/telemetry"),
      expect.objectContaining({
        headers: expect.objectContaining({ Authorization: "Bearer firebase-token" })
      })
    );

    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse([]));
    await listTelemetryCredentials("app_1");
    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining("/v1/apps/app_1/telemetry/credentials"),
      expect.objectContaining({ method: "GET" })
    );
  });

  it("enables telemetry and revokes credentials", async () => {
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse({ enabled: false }));
    await updateTelemetrySettings("app_1", false);
    const [, options] = vi.mocked(fetch).mock.calls[0];
    expect(options).toMatchObject({ method: "PUT" });
    expect(JSON.parse((options as RequestInit).body as string)).toEqual({ enabled: false });

    vi.mocked(fetch).mockResolvedValueOnce({ status: 204, headers: { get: () => "" } } as unknown as Response);
    await revokeTelemetryCredential("app_1", "cred_1");
    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining("/v1/apps/app_1/telemetry/credentials/cred_1"),
      expect.objectContaining({ method: "DELETE" })
    );
  });

  it("issues credentials with validated service payload", async () => {
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse({ id: "cred_1", token: "mcl_ingest_x" }, 201));
    const issued = await issueTelemetryCredential("app_1", {
      serviceName: "backend",
      name: "edge",
      expiresAt: null
    });
    expect(issued.token).toBe("mcl_ingest_x");
    const [, options] = vi.mocked(fetch).mock.calls[0];
    expect(JSON.parse((options as RequestInit).body as string)).toEqual({
      serviceName: "backend",
      name: "edge",
      expiresAt: null
    });
  });

  it("encodes log query filters without leaking into the path", async () => {
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse({ state: "ok", entries: [] }));
    await queryApplicationLogs("app_1", {
      start: "2026-09-08T11:45:00.000Z",
      end: "2026-09-08T12:00:00.000Z",
      service: "backend",
      severity: "ERROR",
      q: "order-1",
      traceId: "abc123",
      limit: 200
    });
    const [url] = vi.mocked(fetch).mock.calls[0];
    const parsed = new URL(url as string);
    expect(parsed.pathname).toBe("/v1/apps/app_1/logs");
    expect(parsed.searchParams.get("service")).toBe("backend");
    expect(parsed.searchParams.get("severity")).toBe("ERROR");
    expect(parsed.searchParams.get("q")).toBe("order-1");
    expect(parsed.searchParams.get("traceId")).toBe("abc123");
    expect(parsed.searchParams.get("limit")).toBe("200");
  });

  it("launches Grafana sessions and exchanges the code with cookies", async () => {
    vi.mocked(fetch).mockResolvedValueOnce(
      jsonResponse({ launchCode: "code_1", destination: "/explore", expiresAt: "2026-09-08T12:01:00Z" })
    );
    const launch = await launchGrafanaSession("app_1");
    expect(launch.launchCode).toBe("code_1");
    const [, launchOptions] = vi.mocked(fetch).mock.calls[0];
    expect(JSON.parse((launchOptions as RequestInit).body as string)).toEqual({ destination: "/explore" });

    vi.mocked(fetch).mockResolvedValueOnce(
      jsonResponse({ destination: "/explore?orgId=1", expiresAt: "2026-09-08T13:00:00Z" })
    );
    const exchange = await exchangeGrafanaLaunch("code_1");
    expect(exchange.destination).toBe("/explore?orgId=1");
    const [exchangeUrl, exchangeOptions] = vi.mocked(fetch).mock.calls[1];
    expect(exchangeUrl).toContain("/v1/grafana/exchange");
    expect(exchangeOptions).toMatchObject({ method: "POST", credentials: "include" });
    expect((exchangeOptions as RequestInit).headers).not.toMatchObject({ Authorization: expect.anything() });
  });

  it("builds Grafana proxy urls from the server destination", () => {
    expect(buildGrafanaUrl("/explore?orgId=1")).toContain("/grafana/explore?orgId=1");
  });
});
