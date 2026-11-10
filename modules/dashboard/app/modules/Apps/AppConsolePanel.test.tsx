import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AppConsolePanel } from "./AppConsolePanel";
import * as runtimeApi from "../../api/runtimeApi";
import type { EnvironmentDetailResponse } from "../../models/controlPlane";

vi.mock("../../api/runtimeApi", () => ({
  getRuntimeStatus: vi.fn(),
  getRuntimeLogSources: vi.fn(),
  getRuntimeLogs: vi.fn(),
  getRuntimeDiagnostics: vi.fn(),
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
  latestDeployment: null,
};

describe("AppConsolePanel partial failures", () => {
  afterEach(cleanup);
  beforeEach(() => {
    sessionStorage.clear();
    vi.clearAllMocks();
    vi.mocked(runtimeApi.getRuntimeStatus).mockResolvedValue({
      serverId: "server_123",
      agentInstalled: true,
      agentOnline: true,
      agentVersion: "0.4.2",
      lastSeenAt: new Date().toISOString(),
      observedAt: new Date().toISOString(),
      caddy: { active: true },
      services: [
        { name: "web", state: "running", health: "healthy", restartCount: 0, container: "web1", image: "img" },
      ],
    });
    vi.mocked(runtimeApi.getRuntimeLogSources).mockResolvedValue([
      { source: "docker-compose", service: "web", lastObservedAt: new Date().toISOString() },
    ]);
    vi.mocked(runtimeApi.getRuntimeLogs).mockResolvedValue([
      {
        id: "log_1",
        serverId: "srv_1",
        source: "docker-compose",
        service: "web",
        stream: "stderr",
        content: "something on stderr",
        observedAt: new Date().toISOString(),
        receivedAt: new Date().toISOString(),
      },
    ]);
    vi.mocked(runtimeApi.getRuntimeDiagnostics).mockResolvedValue({
      caddyErrors: [],
      serviceErrors: [],
      latestDeploymentUrl: "https://app.example.com",
    });
  });

  it("keeps logs when diagnostics fail and reports diagnostics locally", async () => {
    vi.mocked(runtimeApi.getRuntimeDiagnostics).mockRejectedValue(new Error("diagnostics down"));
    render(<AppConsolePanel app={app} />);
    // Log output survives the diagnostics failure.
    expect(await screen.findByText("something on stderr")).toBeInTheDocument();
    // Diagnostics failure is local, not a page-level failure.
    expect(await screen.findByRole("alert")).toHaveTextContent("Diagnostics unavailable: diagnostics down");
    // stderr is labeled neutrally, not as severity-colored error text.
    expect(screen.getByText("stderr · docker-compose")).toBeInTheDocument();
  });
});
