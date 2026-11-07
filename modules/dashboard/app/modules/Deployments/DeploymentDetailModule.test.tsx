import {
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getApp } from "../../api/appsApi";
import {
  dispatchDeployment,
  cancelDeployment,
  getDeployment,
  listDeploymentActivities,
  listDeploymentEvents,
  listDeploymentLogs,
  refreshDeployment,
} from "../../api/deploymentsApi";
import { DeploymentDetailModule } from "./DeploymentDetailModule";
import { summarizeEvent } from "./deploymentDetailPresentation";

vi.mock("../../api/appsApi", () => ({ getApp: vi.fn() }));
vi.mock("../../api/deploymentsApi", () => ({
  dispatchDeployment: vi.fn(),
  cancelDeployment: vi.fn(),
  getDeployment: vi.fn(),
  listDeploymentActivities: vi.fn(),
  listDeploymentEvents: vi.fn(),
  listDeploymentLogs: vi.fn(),
  refreshDeployment: vi.fn(),
}));
vi.mock("../../core/auth/AuthProvider", () => ({
  useAuth: () => ({ selectedOrganization: { id: "org_123", name: "Org" } }),
}));

const deployment = {
  id: "dep_123456789",
  appId: "app_123",
  organizationId: "org_123",
  database: "sqlite" as const,
  status: "deploying" as const,
  consoleUrl: "https://console.example.test",
  services: [
    {
      name: "web",
      image: "ghcr.io/example/web:sha",
      port: 3000,
      public: true,
      path: "/",
      healthPath: "/health",
      urls: ["https://web.example.test"],
    },
  ],
  createdAt: "2026-09-25T12:00:00Z",
  startedAt: "2026-09-25T12:01:00Z",
  completedAt: null,
};

describe("DeploymentDetailModule", () => {
  afterEach(cleanup);

  beforeEach(() => {
    vi.mocked(getApp).mockResolvedValue({
      id: "app_123",
      name: "Demo",
    } as never);
    vi.mocked(getDeployment).mockResolvedValue(deployment);
    vi.mocked(listDeploymentEvents).mockResolvedValue([
      {
        id: "event_1",
        level: "info",
        message: "Deployment entered queue",
        createdAt: "2026-09-25T12:00:00Z",
      },
      {
        id: "event_2",
        level: "info",
        message: "Transferring deployment payload with 6 artifacts to worker 9",
        metadataJson: '{"workerId":"worker_9"}',
        createdAt: "2026-09-25T12:02:00Z",
      },
    ]);
    vi.mocked(listDeploymentActivities).mockResolvedValue([]);
    vi.mocked(listDeploymentLogs).mockResolvedValue([]);
    vi.mocked(refreshDeployment).mockResolvedValue(deployment);
    vi.mocked(dispatchDeployment).mockResolvedValue(deployment);
  });

  it("cancels the deployment and disables repeated requests while stopping", async () => {
    vi.mocked(cancelDeployment).mockResolvedValue({ id: deployment.id, cancellationRequested: true });
    render(<MemoryRouter initialEntries={["/apps/app_123/deployments/dep_123"]}>
      <Routes><Route path="/apps/:appId/deployments/:deploymentId" element={<DeploymentDetailModule />} /></Routes>
    </MemoryRouter>);
    const cancel = await screen.findByRole("button", { name: "Cancel deployment" });
    vi.mocked(getDeployment).mockResolvedValue({ ...deployment, cancellationRequestedAt: "2026-10-27T12:00:00Z" });
    fireEvent.click(cancel);
    expect(await screen.findByRole("button", { name: "Canceling..." })).toBeDisabled();
    expect(cancelDeployment).toHaveBeenCalledWith("dep_123");
    expect(await screen.findByText(/Waiting for execution to stop/)).toBeInTheDocument();
  });

  it("shows current step logs and keeps full output in an inspector", async () => {
    vi.mocked(listDeploymentLogs).mockResolvedValue([
      {
        id: "log_1",
        source: "deploy",
        content: "===== phase: build_artifacts =====\nOld build output",
        createdAt: "2026-09-25T12:02:00Z",
      },
      {
        id: "log_2",
        source: "deploy",
        content: "===== phase: start_app_services =====",
        createdAt: "2026-09-25T12:03:00Z",
      },
      {
        id: "log_3",
        source: "compose",
        content: "Container web Starting",
        createdAt: "2026-09-25T12:03:01Z",
      },
      {
        id: "runtime",
        source: "container/web",
        content: "Unrelated application log",
        createdAt: "2026-09-25T12:03:02Z",
      },
    ]);
    render(
      <MemoryRouter
        initialEntries={["/apps/app_123/deployments/dep_123456789"]}
      >
        <Routes>
          <Route
            path="/apps/:appId/deployments/:deploymentId"
            element={<DeploymentDetailModule />}
          />
        </Routes>
      </MemoryRouter>,
    );
    await screen.findByRole("heading", { name: "Demo" });
    expect(
      screen.getByRole("heading", { name: "Starting application containers" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("listitem", { name: "Prepare, complete" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("listitem", { name: "Deploy, running" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("listitem", { name: "Verify, pending" }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Current step logs")).toHaveTextContent(
      "Container web Starting",
    );
    expect(screen.getByLabelText("Current step logs")).not.toHaveTextContent(
      "Old build output",
    );
    expect(screen.getByLabelText("Current step logs")).not.toHaveTextContent(
      "Unrelated application log",
    );
    expect(
      screen.queryByRole("button", { name: "Console" }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Redeploy" })).toBeDisabled();
    expect(screen.queryByText("worker_9")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "View all logs" }));
    expect(screen.getByLabelText("Full deployment logs")).toHaveTextContent(
      "Old build output",
    );
    expect(screen.getByLabelText("Full deployment logs")).toHaveTextContent(
      "Unrelated application log",
    );
    fireEvent.click(screen.getByRole("button", { name: "Close details" }));
    expect(
      screen.queryByLabelText("Full deployment logs"),
    ).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "View all logs" })).toHaveFocus();
  });

  it("keeps all endpoints in a compact table and inspects configuration separately", async () => {
    vi.mocked(getDeployment).mockResolvedValue({
      ...deployment,
      services: [
        {
          ...deployment.services[0],
          urls: ["https://web.example.test", "https://second.example.test"],
        },
        { ...deployment.services[0], name: "worker", public: false, urls: [] },
      ],
    });
    render(
      <MemoryRouter
        initialEntries={["/apps/app_123/deployments/dep_123456789"]}
      >
        <Routes>
          <Route
            path="/apps/:appId/deployments/:deploymentId"
            element={<DeploymentDetailModule />}
          />
        </Routes>
      </MemoryRouter>,
    );
    await screen.findByRole("heading", { name: "Demo" });
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Services" }));
    fireEvent.click(screen.getByRole("tab", { name: "Services" }));
    const table = await screen.findByRole("table", {
      name: "Deployment services",
    });
    expect(
      within(table).getByRole("link", {
        name: "Open web endpoint https://web.example.test",
      }),
    ).toHaveAttribute("href", "https://web.example.test");
    expect(
      within(table).getByRole("link", {
        name: "Open web endpoint https://second.example.test",
      }),
    ).toHaveAttribute("href", "https://second.example.test");
    expect(
      within(table).getAllByRole("button", { name: /^Copy web endpoint/ }),
    ).toHaveLength(2);
    expect(within(table).getByText("Internal only")).toBeInTheDocument();
    expect(screen.queryByRole("searchbox")).not.toBeInTheDocument();
    expect(
      screen.queryByText("ghcr.io/example/web:sha"),
    ).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Details for web" }));
    const panel = screen.getByRole("complementary", {
      name: "web configuration",
    });
    expect(
      within(panel).getByText("ghcr.io/example/web:sha"),
    ).toBeInTheDocument();
    expect(within(panel).getByText("Health-check path")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Details for worker" }));
    expect(
      screen.getByRole("complementary", { name: "worker configuration" }),
    ).toHaveTextContent("Internal");
    fireEvent.keyDown(
      screen.getByRole("complementary", { name: "worker configuration" }),
      { key: "Escape" },
    );
    expect(screen.queryByRole("complementary")).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Details for worker" }),
    ).toHaveFocus();
  });

  it.each([false, true])("uses the timeline start in the header (manual redispatch: %s)", async (redispatched) => {
    const requeuedAt = "2026-09-25T13:00:00Z";
    vi.mocked(getDeployment).mockResolvedValue({
      ...deployment,
      requeuedAt: redispatched ? requeuedAt : null,
      startedAt: redispatched ? "2026-09-25T13:01:00Z" : deployment.startedAt,
    });
    vi.mocked(listDeploymentEvents).mockResolvedValue([
      { id: "local", level: "info", message: "Waiting for the CLI to build and publish local images.", createdAt: deployment.createdAt },
      { id: "queue", level: "info", message: "Deployment queued.", createdAt: "2026-09-25T12:00:59Z" },
      ...(redispatched ? [{ id: "retry", level: "info" as const, message: "Deployment manually queued.", createdAt: requeuedAt }] : []),
    ]);
    render(
      <MemoryRouter initialEntries={["/apps/app_123/deployments/dep_123456789"]}>
        <Routes>
          <Route path="/apps/:appId/deployments/:deploymentId" element={<DeploymentDetailModule />} />
        </Routes>
      </MemoryRouter>,
    );
    await screen.findByRole("heading", { name: "Demo" });
    const expectedStart = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" })
      .format(new Date(redispatched ? requeuedAt : deployment.createdAt));
    expect(screen.getByText(`Started ${expectedStart}`)).toBeInTheDocument();
  });

  it("creates concise summaries without claiming unreported success", () => {
    expect(summarizeEvent("DNS configured for 4 endpoints on server 12")).toBe(
      "DNS ready · 4 endpoints",
    );
    expect(summarizeEvent("Worker 9 is transferring 6 artifacts")).toBe(
      "Transferring deployment payload · 6 artifacts",
    );
    expect(
      summarizeEvent(
        "Transferring deployment payload to 192.0.2.4 (6 artifact(s)).",
      ),
    ).toBe("Transferring deployment payload · 6 artifacts");
    expect(
      summarizeEvent(
        "DNS ready: web.example.test, api.example.test -> 192.0.2.4.",
      ),
    ).toBe("DNS ready · 2 endpoints");
    expect(
      summarizeEvent("Deployment phase 'health-finalize' succeeded in 123 ms."),
    ).toBe("Health checks completed");
    expect(summarizeEvent("DNS configuration failed for 4 endpoints")).toBe(
      "DNS configuration failed for 4 endpoints",
    );
    expect(summarizeEvent("Custom operation still running")).toBe(
      "Custom operation still running",
    );
  });
});
