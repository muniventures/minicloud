import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, type EnvironmentDetailResponse, type TelemetryLogEntry, type TelemetryLogsResponse } from "../../models/controlPlane";
import { AppTelemetryPanel } from "./AppTelemetryPanel";
import {
  exchangeGrafanaLaunch,
  getTelemetrySettings,
  launchGrafanaSession,
  listTelemetryCredentials,
  queryApplicationLogs
} from "../../api/telemetryApi";

vi.mock("../../api/telemetryApi", () => ({
  buildGrafanaUrl: (destination: string) => `https://api.test/grafana${destination}`,
  exchangeGrafanaLaunch: vi.fn(),
  getTelemetrySettings: vi.fn(),
  launchGrafanaSession: vi.fn(),
  listTelemetryCredentials: vi.fn(),
  queryApplicationLogs: vi.fn()
}));

const app: EnvironmentDetailResponse = {
  id: "app_123",
  organizationId: "org_123",
  name: "Demo",
  slug: "demo",
  plan: "p0",
  database: "sqlite",
  domains: [],
  branches: [],
  latestDeployment: null
};

function notFound() {
  return new ApiError(404, { code: "telemetry_not_found", message: "Not found.", details: [] });
}

function settings(enabled = true, lastAcceptedAt: string | null = "2026-09-08T11:59:00Z") {
  return {
    appId: "app_123",
    organizationId: "org_123",
    tenantId: "mcl_abc",
    ingestionUrl: "https://ingest.operator.test/otlp/v1/logs",
    enabled,
    retentionPolicy: "7d",
    provisioningStatus: "ready",
    lastAcceptedAt,
    createdAt: "2026-09-08T10:00:00Z",
    updatedAt: "2026-09-08T11:00:00Z"
  };
}

function logsResponse(entries: TelemetryLogEntry[] = [], truncated = false): TelemetryLogsResponse {
  return {
    state: "ok",
    entries,
    truncated,
    truncationReason: truncated ? "entry_limit" : null,
    retentionCutoff: "2026-09-01T12:00:00Z",
    lastAcceptedAt: "2026-09-08T11:59:00Z"
  };
}

function entry(body: string, severity = "INFO"): TelemetryLogEntry {
  return {
    timestamp: "2026-09-08T11:59:30Z",
    service: "backend",
    severity,
    traceId: null,
    body,
    attributes: null
  };
}

describe("AppTelemetryPanel", () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  beforeEach(() => {
    vi.mocked(getTelemetrySettings).mockResolvedValue(settings());
    vi.mocked(listTelemetryCredentials).mockResolvedValue([]);
    vi.mocked(queryApplicationLogs).mockResolvedValue(logsResponse([]));
    vi.mocked(launchGrafanaSession).mockResolvedValue({
      launchCode: "code_1",
      destination: "/explore",
      expiresAt: "2026-09-08T12:01:00Z"
    });
    vi.mocked(exchangeGrafanaLaunch).mockResolvedValue({
      destination: "/explore?orgId=1",
      expiresAt: "2026-09-08T13:00:00Z"
    });
  });

  it("shows the installation's configured exporter endpoint", async () => {
    render(<AppTelemetryPanel app={{ ...app }} />);
    expect(await screen.findByText(/OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=https:\/\/ingest\.operator\.test\/otlp\/v1\/logs/)).toBeInTheDocument();
  });

  it("shows the not-configured state without implying collection", async () => {
    vi.mocked(getTelemetrySettings).mockRejectedValue(notFound());
    vi.mocked(queryApplicationLogs).mockResolvedValue({ ...logsResponse([]), state: "not_configured" });

    render(<AppTelemetryPanel app={{ ...app }} />);

    expect(await screen.findByText("Application logs not configured")).toBeInTheDocument();
    expect(screen.getByText(/Nothing is collected until then/i)).toBeInTheDocument();
  });

  it("shows the disabled state with retention guidance", async () => {
    vi.mocked(getTelemetrySettings).mockResolvedValue(settings(false));
    vi.mocked(queryApplicationLogs).mockResolvedValue({ ...logsResponse([]), state: "disabled" });

    render(<AppTelemetryPanel app={{ ...app }} />);

    expect(await screen.findByText("Application logs disabled")).toBeInTheDocument();
    expect(screen.getByText(/remain readable until retention expires/i)).toBeInTheDocument();
  });

  it("renders log content as text, never HTML", async () => {
    const hostile = '<script>alert("xss")</script><img src=x onerror=alert(1)>';
    vi.mocked(queryApplicationLogs).mockResolvedValue(logsResponse([entry(hostile, "ERROR")]));

    render(<AppTelemetryPanel app={{ ...app }} />);

    expect(await screen.findByText(/alert\("xss"\)/)).toBeInTheDocument();
    expect(document.querySelector("script")).toBeNull();
    expect(document.querySelector("img")).toBeNull();
  });

  it("shows truncation and retention boundary", async () => {
    vi.mocked(queryApplicationLogs).mockResolvedValue(logsResponse([entry("one"), entry("two")], true));

    render(<AppTelemetryPanel app={{ ...app }} />);

    expect(await screen.findByText(/Results truncated/)).toBeInTheDocument();
    expect(screen.getByText(/Retention boundary/)).toBeInTheDocument();
    expect(screen.getAllByText(/Last received/).length).toBeGreaterThanOrEqual(1);
  });

  it("distinguishes quota exhaustion from unavailability", async () => {
    vi.mocked(queryApplicationLogs).mockRejectedValue(
      new ApiError(429, { code: "query_rate_limited", message: "Too many.", details: [] })
    );

    const { unmount } = render(<AppTelemetryPanel app={{ ...app }} />);
    expect(await screen.findByText(/Query budget exhausted/)).toBeInTheDocument();
    unmount();

    vi.mocked(queryApplicationLogs).mockRejectedValue(
      new ApiError(503, { code: "telemetry_unavailable", message: "Down.", details: [] })
    );
    render(<AppTelemetryPanel app={{ ...app }} />);
    expect(await screen.findByText(/Application logs are unavailable/)).toBeInTheDocument();
  });

  it("warns when no active credential exists while enabled", async () => {
    vi.mocked(listTelemetryCredentials).mockResolvedValue([
      {
        id: "cred_old",
        serviceName: "backend",
        name: "edge",
        keyPrefix: "mcl_ingest_old",
        credentialVersion: 1,
        createdByActor: "user",
        expiresAt: "2026-01-01T00:00:00Z",
        revokedAt: null,
        lastUsedAt: null,
        createdAt: "2025-10-01T00:00:00Z"
      }
    ]);

    render(<AppTelemetryPanel app={{ ...app }} />);

    expect(await screen.findByText("Expired")).toBeInTheDocument();
    expect(screen.getByText(/No active ingestion credential/)).toBeInTheDocument();
    expect(screen.queryByText(/mcl_ingest_secret/i)).not.toBeInTheDocument();
  });

  it("opens Grafana through the launch and exchange flow", async () => {
    const open = vi.spyOn(window, "open").mockReturnValue(null);

    render(<AppTelemetryPanel app={{ ...app }} />);
    fireEvent.click(await screen.findByRole("button", { name: /Open in Grafana/ }));

    await waitFor(() => expect(launchGrafanaSession).toHaveBeenCalledWith("app_123", "/explore"));
    await waitFor(() => expect(exchangeGrafanaLaunch).toHaveBeenCalledWith("code_1"));
    await waitFor(() =>
      expect(open).toHaveBeenCalledWith("https://api.test/grafana/explore?orgId=1", "_blank", "noopener,noreferrer")
    );
  });

  it("queries with the default 15 minute window and max 1000 cap", async () => {
    render(<AppTelemetryPanel app={{ ...app }} serviceNames={["backend"]} />);

    await waitFor(() => expect(queryApplicationLogs).toHaveBeenCalled());
    const [, query] = vi.mocked(queryApplicationLogs).mock.calls[0] as unknown as [
      string,
      { start: string; end: string; limit?: number }
    ];
    const windowMs = new Date(query.end).getTime() - new Date(query.start).getTime();
    expect(windowMs).toBe(15 * 60_000);
    expect(query.limit ?? 200).toBeLessThanOrEqual(1000);
  });
});
