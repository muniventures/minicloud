import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AppDetailModule } from "./AppDetailModule";
import {
  checkAppDomainAvailability,
  createAppDomain,
  createAppServiceSecretService,
  deleteAppDomain,
  deleteAppServiceSecretService,
  deleteAppServiceSecret,
  disableAppDomain,
  getApp,
  listAppServices,
  listAppServiceSecrets,
  listAppServiceSecretServices,
  renameAppServiceSecretService,
  setAppDomainPrimary,
  setAppServiceSecret,
  updateAppServiceSecret
} from "../../api/appsApi";
import { listDeployments } from "../../api/deploymentsApi";

vi.mock("../../api/appsApi", () => ({
  getApp: vi.fn(),
  listAppServices: vi.fn(),
  checkAppDomainAvailability: vi.fn(),
  createAppDomain: vi.fn(),
  setAppDomainPrimary: vi.fn(),
  disableAppDomain: vi.fn(),
  deleteAppDomain: vi.fn(),
  listAppServiceSecretServices: vi.fn(),
  createAppServiceSecretService: vi.fn(),
  renameAppServiceSecretService: vi.fn(),
  deleteAppServiceSecretService: vi.fn(),
  listAppServiceSecrets: vi.fn(),
  setAppServiceSecret: vi.fn(),
  updateAppServiceSecret: vi.fn(),
  deleteAppServiceSecret: vi.fn()
}));

vi.mock("../../api/deploymentsApi", () => ({
  listDeployments: vi.fn()
}));

vi.mock("../../core/auth/AuthProvider", () => ({
  useAuth: () => ({
    selectedOrganization: { id: "org_123", name: "Org" }
  })
}));

vi.mock("./AppConsolePanel", () => ({
  AppConsolePanel: () => <div data-testid="console-panel" />
}));

describe("AppDetailModule", () => {
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  beforeEach(() => {
    vi.mocked(getApp).mockResolvedValue({
      id: "app_123",
      organizationId: "org_123",
      name: "Demo",
      slug: "demo",
      plan: "p0",
      database: "sqlite",
      domains: [],
      branches: [],
      latestDeployment: null
    });
    vi.mocked(listDeployments).mockResolvedValue([]);
    vi.mocked(checkAppDomainAvailability).mockResolvedValue({
      label: "demo-api",
      hostname: "demo-api.app.muni.dev",
      available: true,
      message: null
    });
    vi.mocked(createAppDomain).mockResolvedValue({
      id: "dom_new",
      appId: "app_123",
      serviceName: "web",
      hostname: "demo-api.app.muni.dev",
      kind: "minicloud_subdomain",
      pathPrefix: "/",
      dnsStatus: "verified",
      applyStatus: "pending",
      sslStatus: "pending",
      status: "pending_apply",
      failureCode: null,
      failureMessage: null,
      lastAppliedAt: null,
      createdAt: "2026-05-18T12:00:00Z",
      updatedAt: "2026-05-18T12:00:00Z"
    });
    vi.mocked(disableAppDomain).mockResolvedValue({} as never);
    vi.mocked(deleteAppDomain).mockResolvedValue(undefined);
    vi.mocked(listAppServiceSecretServices).mockResolvedValue([
      {
        id: "svc_123",
        appId: "app_123",
        serviceName: "web",
        secretCount: 1,
        createdAt: "2026-05-18T12:00:00Z",
        updatedAt: "2026-05-18T13:00:00Z"
      }
    ]);
    vi.mocked(createAppServiceSecretService).mockResolvedValue({
      id: "svc_456",
      appId: "app_123",
      serviceName: "web",
      secretCount: 0,
      createdAt: "2026-05-18T14:00:00Z",
      updatedAt: "2026-05-18T14:00:00Z"
    });
    vi.mocked(renameAppServiceSecretService).mockResolvedValue({
      id: "svc_123",
      appId: "app_123",
      serviceName: "api",
      secretCount: 1,
      createdAt: "2026-05-18T12:00:00Z",
      updatedAt: "2026-05-18T14:00:00Z"
    });
    vi.mocked(deleteAppServiceSecretService).mockResolvedValue(undefined);
    vi.mocked(listAppServiceSecrets).mockResolvedValue([
      {
        id: "sec_123",
        appId: "app_123",
        serviceName: "web",
        name: "DATABASE_URL",
        status: "active",
        createdAt: "2026-05-18T12:00:00Z",
        updatedAt: "2026-05-18T13:00:00Z"
      }
    ]);
    vi.mocked(setAppServiceSecret).mockResolvedValue({
      id: "sec_456",
      appId: "app_123",
      serviceName: "web",
      name: "API_TOKEN",
      status: "active",
      createdAt: "2026-05-18T14:00:00Z",
      updatedAt: "2026-05-18T14:00:00Z"
    });
    vi.mocked(updateAppServiceSecret).mockResolvedValue({} as never);
    vi.mocked(deleteAppServiceSecret).mockResolvedValue(undefined);
    vi.mocked(listAppServices).mockResolvedValue([
      {
        name: "web",
        image: "ghcr.io/example/web:sha",
        port: 3000,
        public: true,
        path: "/",
        healthPath: "/health",
        runtimeObservedAt: new Date().toISOString(),
        runtime: {
          name: "web",
          container: "minicloud-demo-web-1",
          state: "running",
          health: "healthy",
          image: "ghcr.io/example/web:sha",
          restartCount: 0
        },
        domains: [
          {
            id: "dom_123",
            appId: "app_123",
            serviceName: "web",
            hostname: "org-demo-web.app.muni.dev",
            kind: "minicloud_subdomain",
            pathPrefix: "/",
            dnsStatus: "verified",
            applyStatus: "applied",
            sslStatus: "ready",
            status: "active",
            failureCode: null,
            failureMessage: null,
            lastAppliedAt: "2026-05-17T12:00:00Z",
            createdAt: "2026-05-17T12:00:00Z",
            updatedAt: "2026-05-17T12:00:00Z"
          }
        ]
      },
      {
        name: "worker",
        image: "ghcr.io/example/worker:sha",
        port: 9000,
        public: false,
        path: "/",
        healthPath: "/health",
        runtime: null,
        domains: []
      }
    ]);
  });


  it("discovers CLI builds on an open app history page and updates its summary without reloading secrets", async () => {
    vi.useFakeTimers();
    const deployment = {
      id: "previous", appId: "app_123", organizationId: "org_123", database: "sqlite" as const,
      status: "failed" as const, services: [], consoleUrl: "https://console.example.test",
      createdAt: "2026-10-27T12:00:00Z",
    };
    vi.mocked(listDeployments).mockResolvedValue([deployment]);
    await act(async () => {
      render(<MemoryRouter initialEntries={["/apps/app_123"]}><Routes>
        <Route path="/apps/:appId" element={<AppDetailModule />} />
      </Routes></MemoryRouter>);
    });
    const secretReads = vi.mocked(listAppServiceSecretServices).mock.calls.length;
    const appReads = vi.mocked(getApp).mock.calls.length;
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Deployments" }), { button: 0 });
    fireEvent.click(screen.getByRole("tab", { name: "Deployments" }));
    for (const status of ["waiting_for_build", "queued", "succeeded"] as const) {
      vi.mocked(listDeployments).mockResolvedValue([{ ...deployment, id: "new-build", status }]);
      await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
      expect(screen.getAllByText(status === "waiting_for_build" ? "Waiting for build" : status).length).toBeGreaterThanOrEqual(2);
      expect(screen.getByText("new-build")).toBeInTheDocument();
    }
    expect(listAppServiceSecretServices).toHaveBeenCalledTimes(secretReads);
    expect(getApp).toHaveBeenCalledTimes(appReads);
  });

  it("shows service URLs in the overview and keeps deletion behind More", async () => {
    render(
      <MemoryRouter initialEntries={["/apps/app_123"]}>
        <Routes>
          <Route path="/apps/:appId" element={<AppDetailModule />} />
        </Routes>
      </MemoryRouter>,
    );

    await screen.findByRole("heading", { name: "Demo" });
    expect(screen.getByRole("button", { name: "Refresh Demo" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Deployment history" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "https://org-demo-web.app.muni.dev" })).toBeInTheDocument();
    expect(screen.getByText("Internal only")).toBeInTheDocument();
    expect(screen.queryByText(/Image: ghcr\.io/)).not.toBeInTheDocument();
    fireEvent.pointerDown(screen.getByRole("button", { name: "More" }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole("menuitem", { name: "Delete application" }));
    expect(screen.getByRole("heading", { name: "Delete Demo?" })).toBeInTheDocument();
  });

  it("renders service routing without custom-domain controls", async () => {
    render(
      <MemoryRouter initialEntries={["/apps/app_123"]}>
        <Routes>
          <Route path="/apps/:appId" element={<AppDetailModule />} />
        </Routes>
      </MemoryRouter>
    );

    await waitFor(() => expect(screen.getByRole("tab", { name: "Overview" })).toBeInTheDocument());
    switchTab("Services");
    await waitFor(() => expect(screen.getByText("Service routing")).toBeInTheDocument());

    expect(screen.getByRole("columnheader", { name: "URL" })).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Runtime" })).toBeInTheDocument();
    expect(screen.getAllByText("web").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("worker").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Public")).toBeInTheDocument();
    expect(screen.getByText("Private")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "https://org-demo-web.app.muni.dev" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy web URL" })).toBeInTheDocument();
    expect(screen.getByText("Internal only")).toBeInTheDocument();
    expect(screen.getByRole("table")).toHaveClass("table-fixed");

    const webDetailsButton = screen.getByRole("button", { name: "web" });
    expect(webDetailsButton).toHaveAttribute("aria-expanded", "false");
    fireEvent.click(webDetailsButton);
    expect(webDetailsButton).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText("Image reference")).toBeInTheDocument();
    expect(screen.getByText("ghcr.io/example/web:sha")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Actions for web" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Actions for worker" })).toBeInTheDocument();
    expect(screen.queryByText(/custom domain/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/path prefix/i)).not.toBeInTheDocument();
  });

  it("renders branch deployments as child apps", async () => {
    vi.mocked(getApp).mockResolvedValue({
      id: "app_123",
      organizationId: "org_123",
      name: "Demo",
      slug: "demo",
      plan: "p0",
      database: "sqlite",
      domains: [],
      branches: [{
        id: "app_branch",
        name: "Demo (feature-cart)",
        slug: "demo-feature-cart",
        branchName: "feature-cart",
        plan: "p0-v-0",
        database: "sqlite",
        websiteUrl: "https://old-main.example",
        urls: ["https://org-demo-web-feature-cart.app.muni.dev", "https://org-demo-api-feature-cart.app.muni.dev"],
        createdAt: "2026-07-14T12:00:00Z"
      }],
      latestDeployment: null
    });

    render(
      <MemoryRouter initialEntries={["/apps/app_123"]}>
        <Routes>
          <Route path="/apps/:appId" element={<AppDetailModule />} />
        </Routes>
      </MemoryRouter>
    );

    expect(await screen.findByText("feature-cart")).toBeInTheDocument();
    expect(screen.getByText("P0-V-0")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "https://org-demo-api-feature-cart.app.muni.dev" })).toBeInTheDocument();
    expect(screen.queryByText("https://old-main.example")).not.toBeInTheDocument();
    expect(screen.getByText("https://org-demo-web-feature-cart.app.muni.dev")).toBeInTheDocument();
  });

  it("checks availability before saving a subdomain", async () => {
    render(
      <MemoryRouter initialEntries={["/apps/app_123"]}>
        <Routes>
          <Route path="/apps/:appId" element={<AppDetailModule />} />
        </Routes>
      </MemoryRouter>
    );

    await waitFor(() => expect(screen.getByRole("tab", { name: "Overview" })).toBeInTheDocument());
    switchTab("Services");
    await waitFor(() => expect(screen.getByText("Service routing")).toBeInTheDocument());
    const actionsBtn = screen.getByRole("button", { name: "Actions for web" });
    fireEvent.pointerDown(actionsBtn);
    fireEvent.click(await screen.findByRole("menuitem", { name: /manage domains/i }));
    fireEvent.change(screen.getByLabelText("Subdomain"), { target: { value: "demo-api" } });
    fireEvent.click(screen.getByRole("button", { name: "Check availability" }));

    await waitFor(() => expect(checkAppDomainAvailability).toHaveBeenCalledWith("app_123", "demo-api"));
    expect(await screen.findByText("demo-api.app.muni.dev is available.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Add subdomain" }));

    await waitFor(() => expect(createAppDomain).toHaveBeenCalledWith("app_123", "web", "demo-api"));
  });

  it("allows setting a domain as primary", async () => {
    vi.mocked(listAppServices).mockResolvedValue([
      {
        name: "web",
        image: "ghcr.io/example/web:sha",
        port: 8080,
        public: true,
        path: "/",
        healthPath: "/health",
        runtime: null,
        domains: [
          {
            id: "dom_default",
            appId: "app_123",
            serviceName: "web",
            hostname: "org-demo-web.app.muni.dev",
            kind: "minicloud_subdomain",
            pathPrefix: "/",
            dnsStatus: "verified",
            applyStatus: "applied",
            sslStatus: "ready",
            status: "active",
            isPrimary: true,
            createdAt: "2026-05-17T12:00:00Z",
            updatedAt: "2026-05-17T12:00:00Z"
          },
          {
            id: "dom_custom",
            appId: "app_123",
            serviceName: "web",
            hostname: "portal.app.muni.dev",
            kind: "minicloud_subdomain",
            pathPrefix: "/",
            dnsStatus: "verified",
            applyStatus: "applied",
            sslStatus: "ready",
            status: "active",
            isPrimary: false,
            createdAt: "2026-05-18T12:00:00Z",
            updatedAt: "2026-05-18T12:00:00Z"
          }
        ]
      }
    ]);

    render(
      <MemoryRouter initialEntries={["/apps/app_123"]}>
        <Routes>
          <Route path="/apps/:appId" element={<AppDetailModule />} />
        </Routes>
      </MemoryRouter>
    );

    await waitFor(() => expect(screen.getByRole("tab", { name: "Overview" })).toBeInTheDocument());
    switchTab("Services");
    await waitFor(() => expect(screen.getByText("Service routing")).toBeInTheDocument());

    fireEvent.pointerDown(screen.getByRole("button", { name: "Actions for web" }));
    fireEvent.click(await screen.findByRole("menuitem", { name: /manage domains/i }));

    const setPrimaryBtn = await screen.findByRole("button", { name: "Set as primary" });
    fireEvent.click(setPrimaryBtn);

    await waitFor(() => expect(setAppDomainPrimary).toHaveBeenCalledWith("app_123", "dom_custom"));
  });

  it("opens service secrets without rendering secret values", async () => {
    render(
      <MemoryRouter initialEntries={["/apps/app_123"]}>
        <Routes>
          <Route path="/apps/:appId" element={<AppDetailModule />} />
        </Routes>
      </MemoryRouter>
    );

    await waitFor(() => expect(screen.getByRole("tab", { name: "Overview" })).toBeInTheDocument());
    switchTab("Secrets");
    await waitFor(() => expect(screen.getByText("Service secrets")).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "Secrets" }));

    await waitFor(() => expect(listAppServiceSecrets).toHaveBeenCalledWith("app_123", "web"));
    expect(await screen.findByText("DATABASE_URL")).toBeInTheDocument();
    expect(screen.getByText("Existing values are never shown.", { exact: false })).toBeInTheDocument();
    expect(screen.queryByText("postgres://user:password@example/db")).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "API_TOKEN" } });
    fireEvent.change(screen.getByLabelText("Value"), { target: { value: "super-secret-token" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(setAppServiceSecret).toHaveBeenCalledWith("app_123", "web", "API_TOKEN", "super-secret-token"));
  });

  it("opens service secrets before the app has deployed services", async () => {
    vi.mocked(listAppServices).mockResolvedValue([]);
    vi.mocked(listAppServiceSecretServices).mockResolvedValueOnce([]);

    render(
      <MemoryRouter initialEntries={["/apps/app_123"]}>
        <Routes>
          <Route path="/apps/:appId" element={<AppDetailModule />} />
        </Routes>
      </MemoryRouter>
    );

    await waitFor(() => expect(screen.getByRole("tab", { name: "Overview" })).toBeInTheDocument());
    switchTab("Secrets");
    await waitFor(() => expect(screen.getByText("Service secrets")).toBeInTheDocument());
    fireEvent.change(screen.getByLabelText("Service name"), { target: { value: "web" } });
    fireEvent.click(screen.getByRole("button", { name: "Add service" }));

    await waitFor(() => expect(createAppServiceSecretService).toHaveBeenCalledWith("app_123", "web"));
    await waitFor(() => expect(listAppServiceSecrets).toHaveBeenCalledWith("app_123", "web"));
  });

  it("renames a service used for secrets", async () => {
    render(
      <MemoryRouter initialEntries={["/apps/app_123"]}>
        <Routes>
          <Route path="/apps/:appId" element={<AppDetailModule />} />
        </Routes>
      </MemoryRouter>
    );

    await waitFor(() => expect(screen.getByRole("tab", { name: "Overview" })).toBeInTheDocument());
    switchTab("Secrets");
    await waitFor(() => expect(screen.getByText("Service secrets")).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "Rename" }));
    fireEvent.change(screen.getByLabelText("Service name for web"), { target: { value: "api" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(renameAppServiceSecretService).toHaveBeenCalledWith("app_123", "svc_123", "api"));
  });
});

function switchTab(name: string) {
  const tab = screen.getByRole("tab", { name });
  fireEvent.pointerDown(tab, { button: 0, ctrlKey: false });
  fireEvent.pointerUp(tab);
  fireEvent.click(tab);
}
