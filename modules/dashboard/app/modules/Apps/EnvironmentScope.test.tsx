import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AppDetailModule } from "./AppDetailModule";
import { createAppServiceSecretService, deleteAppServiceSecretService, getEnvironmentDetail, listAppServices, listAppServiceSecrets, listAppServiceSecretServices, renameAppServiceSecretService } from "../../api/environmentsApi";
import { deleteEnvironment } from "../../api/applicationsApi";
import { listDeployments } from "../../api/deploymentsApi";
import { getTelemetrySettings, listTelemetryCredentials, queryApplicationLogs } from "../../api/telemetryApi";
import type { AppServiceSecretServiceResponse } from "../../models/controlPlane";
import { appsFixtureApplications } from "../../api/applicationsFixtures";

vi.mock("../../api/environmentsApi", () => ({
  getEnvironmentDetail: vi.fn(),
  listAppServices: vi.fn(),
  listAppServiceSecretServices: vi.fn(),
  createAppServiceSecretService: vi.fn(),
  renameAppServiceSecretService: vi.fn(),
  deleteAppServiceSecretService: vi.fn(),
  listAppServiceSecrets: vi.fn(),
  setAppServiceSecret: vi.fn(),
  updateAppServiceSecret: vi.fn(),
  deleteAppServiceSecret: vi.fn(),
}));

vi.mock("../../api/deploymentsApi", () => ({
  listDeployments: vi.fn(),
}));

vi.mock("../../api/applicationsApi", () => ({
  getApplication: vi.fn(),
  deleteEnvironment: vi.fn(),
}));

vi.mock("../../api/telemetryApi", () => ({
  buildGrafanaUrl: () => "https://api.test/grafana",
  exchangeGrafanaLaunch: vi.fn(),
  getTelemetrySettings: vi.fn(),
  launchGrafanaSession: vi.fn(),
  listTelemetryCredentials: vi.fn(),
  queryApplicationLogs: vi.fn(),
}));

vi.mock("../../api/applicationsFixtures", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../../api/applicationsFixtures")>();
  return {
    ...actual,
    appsFixturesEnabled: () => true,
  };
});

vi.mock("../../core/auth/AuthProvider", () => ({
  useAuth: () => ({
    selectedOrganization: { id: "org_fixture_01", name: "Fixture Org", role: "owner" },
  }),
}));

const PROD = "/apps/app_munipages/environments/runtime_munipages_prod/overview";
const STAGING = "/apps/app_munipages/environments/runtime_munipages_staging/services";

function scopedRouter(initial: string) {
  return createMemoryRouter(
    [
      {
        path: "/apps/:applicationId/environments/:environmentId/:tab",
        element: <AppDetailModule />,
      },
      { path: "*", element: <div data-testid="missing" /> },
    ],
    { initialEntries: [initial] },
  );
}

function delay(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

describe("EnvironmentScope (UX-01)", () => {
  afterEach(cleanup);
  beforeEach(() => {
    sessionStorage.clear();
    vi.clearAllMocks();
    vi.mocked(listDeployments).mockResolvedValue([]);
    vi.mocked(listAppServiceSecretServices).mockResolvedValue([]);
    vi.mocked(listAppServiceSecrets).mockResolvedValue([]);
    vi.mocked(getTelemetrySettings).mockResolvedValue({
      appId: "runtime_munipages_prod",
      organizationId: "org_fixture_01",
      tenantId: "mcl_abc",
      ingestionUrl: "https://ingest.operator.test/otlp/v1/logs",
      enabled: true,
      retentionPolicy: "7d",
      provisioningStatus: "ready",
      lastAcceptedAt: null,
      createdAt: "2026-09-08T10:00:00Z",
      updatedAt: "2026-09-08T11:00:00Z",
    });
    vi.mocked(listTelemetryCredentials).mockResolvedValue([]);
    vi.mocked(queryApplicationLogs).mockResolvedValue({
      state: "ok",
      entries: [],
      truncated: false,
      truncationReason: null,
      retentionCutoff: "2026-09-01T12:00:00Z",
      lastAcceptedAt: "2026-09-08T11:59:00Z",
    });
  });

  it("renders scoped breadcrumb and environment selector above operational content", async () => {
    vi.mocked(getEnvironmentDetail).mockResolvedValue({
      id: "runtime_munipages_prod",
      organizationId: "org_fixture_01",
      name: "MuniPages",
      slug: "munipages",
      plan: "Production medium",
      database: "postgres" as const,
      domains: [],
    });
    vi.mocked(listAppServices).mockResolvedValue([]);
    render(<RouterProvider router={scopedRouter(PROD)} />);
    expect(await screen.findByRole("navigation", { name: "Breadcrumb" })).toHaveTextContent(/Apps\/MuniPages\/production/);
    expect(screen.getByRole("combobox", { name: "Environment for MuniPages" })).toBeInTheDocument();
    expect(screen.getByText("In environment production.")).toBeInTheDocument();
  });

  it("never renders a late prior-environment response under the new header", async () => {
    const base = {
      organizationId: "org_fixture_01",
      name: "MuniPages",
      slug: "munipages",
      plan: "Production medium",
      database: "postgres" as const,
      domains: [],
    };
    vi.mocked(getEnvironmentDetail).mockImplementation(async (id: string) => {
      // Staging's environment detail is slow; production resolves fast.
      await delay(id === "runtime_munipages_staging" ? 250 : 10);
      return {
        ...base,
        id,
        plan: id === "runtime_munipages_staging" ? "Staging small" : "Production medium",
      };
    });
    vi.mocked(listAppServices).mockImplementation(async (id: string) =>
      id === "runtime_munipages_staging"
        ? [{
            name: "web",
            image: "img",
            port: 80,
            public: true,
            path: "/",
            healthPath: "/health",
            domains: [],
          }]
        : [],
    );
    const router = scopedRouter(STAGING);
    render(<RouterProvider router={router} />);
    // Switch to production before staging's slow detail resolves.
    await router.navigate(PROD);
    await waitFor(() => expect(screen.getByText("In environment production.")).toBeInTheDocument());
    expect(screen.getByText(/plan Production medium/)).toBeInTheDocument();
    // Let the slow prior response land (it must be discarded).
    await delay(320);
    expect(screen.getByText("In environment production.")).toBeInTheDocument();
    expect(screen.getByText(/plan Production medium/)).toBeInTheDocument();
    expect(screen.queryByText(/plan Staging small/)).not.toBeInTheDocument();
  });

  it("switching environment preserves the selected tab in the URL", async () => {
    vi.mocked(getEnvironmentDetail).mockImplementation(async (id: string) => ({
      id,
      organizationId: "org_fixture_01",
      name: "MuniPages",
      slug: "munipages",
      plan: "Production medium",
      database: "postgres" as const,
      domains: [],
    }));
    vi.mocked(listAppServices).mockResolvedValue([]);
    const router = scopedRouter("/apps/app_munipages/environments/runtime_munipages_prod/services");
    render(<RouterProvider router={router} />);
    expect(await screen.findByText("Service routing")).toBeInTheDocument();
    // Tab switch navigates within the same scope.
    const tab = screen.getByRole("tab", { name: "Domains" });
    fireEvent.pointerDown(tab, { button: 0, ctrlKey: false });
    fireEvent.pointerUp(tab);
    fireEvent.click(tab);
    await waitFor(() =>
      expect(router.state.location.pathname).toBe("/apps/app_munipages/environments/runtime_munipages_prod/domains"),
    );
  });

  it("View application logs sets the exact environment + service filter", async () => {
    const service = {
      name: "web",
      image: "img",
      port: 80,
      public: true,
      path: "/",
      healthPath: "/health",
      domains: [],
    };
    vi.mocked(getEnvironmentDetail).mockImplementation(async (id: string) => ({
      id,
      organizationId: "org_fixture_01",
      name: "MuniPages",
      slug: "munipages",
      plan: "Production medium",
      database: "postgres" as const,
      domains: [],
    }));
    vi.mocked(listAppServices).mockResolvedValue([service]);
    const router = scopedRouter("/apps/app_munipages/environments/runtime_munipages_prod/services");
    render(<RouterProvider router={router} />);
    await screen.findByText("Service routing");
    fireEvent.pointerDown(screen.getByRole("button", { name: "Actions for web" }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole("menuitem", { name: "View application logs" }));
    await waitFor(() =>
      expect(router.state.location.pathname).toBe("/apps/app_munipages/environments/runtime_munipages_prod/logs"),
    );
    await waitFor(() => {
      const call = vi.mocked(queryApplicationLogs).mock.calls.find(
        ([, query]) => query.service === "web",
      );
      expect(call?.[0]).toBe("runtime_munipages_prod");
      expect(call?.[1]).toMatchObject({ service: "web" });
    });
  });

  it("scoped More never offers application-wide deletion; env delete uses the  API with the selected id", async () => {
    const service = {
      name: "web",
      image: "img",
      port: 80,
      public: true,
      path: "/",
      healthPath: "/health",
      domains: [],
    };
    vi.mocked(getEnvironmentDetail).mockImplementation(async (id: string) => ({
      id,
      organizationId: "org_fixture_01",
      name: "MuniPages",
      slug: "munipages",
      plan: "Production medium",
      database: "postgres" as const,
      domains: [],
    }));
    vi.mocked(listAppServices).mockResolvedValue([service]);
    vi.mocked(deleteEnvironment).mockResolvedValue(undefined);

    // Child selection (staging): even though production environment semantics could
    // delete siblings under the old route, the scoped page uses  for this env only.
    const router = scopedRouter("/apps/app_munipages/environments/runtime_munipages_staging/overview");
    render(<RouterProvider router={router} />);
    await screen.findByText("In environment staging.");
    fireEvent.pointerDown(screen.getByRole("button", { name: "More actions for MuniPages staging" }), { button: 0, ctrlKey: false });
    const menu = await screen.findByRole("menu");
    // Application deletion belongs to application settings.
    expect(screen.queryByRole("menuitem", { name: "Delete application" })).not.toBeInTheDocument();
    expect(screen.getByRole("menuitem", { name: "Delete MuniPages / staging environment…" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("menuitem", { name: "Delete MuniPages / staging environment…" }));
    const input = await screen.findByLabelText("Type staging to confirm");
    fireEvent.change(input, { target: { value: "staging" } });
    fireEvent.click(screen.getByRole("button", { name: "Delete environment" }));
    await waitFor(() =>
      expect(deleteEnvironment).toHaveBeenCalledWith("app_munipages", "runtime_munipages_staging", "staging"),
    );
    expect(router.state.location.pathname).toBe("/apps");
    void menu;
  });

  it("clears an opened service-domain form on environment switch", async () => {
    const service = {
      name: "web",
      image: "img",
      port: 80,
      public: true,
      path: "/",
      healthPath: "/health",
      domains: [],
    };
    vi.mocked(getEnvironmentDetail).mockImplementation(async (id: string) => ({
      id,
      organizationId: "org_fixture_01",
      name: "MuniPages",
      slug: "munipages",
      plan: "Production medium",
      database: "postgres" as const,
      domains: [],
    }));
    vi.mocked(listAppServices).mockResolvedValue([service]);
    const router = scopedRouter("/apps/app_munipages/environments/runtime_munipages_prod/services");
    render(<RouterProvider router={router} />);
    await screen.findByText("Service routing");
    fireEvent.pointerDown(screen.getByRole("button", { name: "Actions for web" }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole("menuitem", { name: "Manage domains..." }));
    // Dialog open for production's web service
    await screen.findByRole("dialog", { name: "Manage Domains: web" });
    // Switch to a same-named staging service: the form must not carry over.
    await router.navigate("/apps/app_munipages/environments/runtime_munipages_staging/services");
    await waitFor(() => expect(screen.getByText("In environment staging.")).toBeInTheDocument());
    await waitFor(() =>
      expect(
        document.querySelector("[role='dialog']"),
      ).toBeNull(),
    );
  });

  it("a late secrets create landing after an environment switch never opens the new scope's dialog", async () => {
    let resolveCreate!: (value: AppServiceSecretServiceResponse) => void;
    vi.mocked(createAppServiceSecretService).mockImplementation(
      () => new Promise((resolve) => { resolveCreate = resolve; }),
    );
    const service = {
      name: "web",
      image: "img",
      port: 80,
      public: true,
      path: "/",
      healthPath: "/health",
      domains: [],
    };
    vi.mocked(getEnvironmentDetail).mockImplementation(async (id: string) => ({
      id,
      organizationId: "org_fixture_01",
      name: "MuniPages",
      slug: "munipages",
      plan: "Production medium",
      database: "postgres" as const,
      domains: [],
    }));
    vi.mocked(listAppServices).mockResolvedValue([service]);
    const router = scopedRouter("/apps/app_munipages/environments/runtime_munipages_prod/secrets");
    render(<RouterProvider router={router} />);
    await screen.findByLabelText("Service name");
    fireEvent.change(screen.getByLabelText("Service name"), { target: { value: "billing" } });
    fireEvent.click(screen.getByRole("button", { name: "Add service" }));
    // Switch before the POST resolves: the late production success must be discarded.
    await router.navigate("/apps/app_munipages/environments/runtime_munipages_staging/secrets");
    await screen.findByText("In environment staging.");
    await act(async () => {
      resolveCreate({ id: "svc_1", appId: "runtime_munipages_prod", serviceName: "billing", secretCount: 0, createdAt: "2026-11-09T12:00:00Z", updatedAt: "2026-11-09T12:00:00Z" });
    });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(listAppServiceSecrets).not.toHaveBeenCalled();
  });

  it("a late production env-delete failure never writes into staging's dialog", async () => {
    let resolveProd!: (value: unknown) => void;
    let rejectProd!: (reason: unknown) => void;
    let resolveStaging!: (value: unknown) => void;
    let rejectStaging!: (reason: unknown) => void;
    vi.mocked(deleteEnvironment).mockImplementation((_appId: string, envId: string) => {
      if (envId === "runtime_munipages_staging") {
        return new Promise((resolve, reject) => { resolveStaging = resolve; rejectStaging = reject; }) as never;
      }
      return new Promise((resolve, reject) => { resolveProd = resolve; rejectProd = reject; }) as never;
    });
    vi.mocked(getEnvironmentDetail).mockImplementation(async (id: string) => ({
      id,
      organizationId: "org_fixture_01",
      name: "MuniPages",
      slug: "munipages",
      plan: "Production medium",
      database: "postgres" as const,
      domains: [],
    }));
    vi.mocked(listAppServices).mockResolvedValue([]);
    const router = scopedRouter("/apps/app_munipages/environments/runtime_munipages_prod/overview");
    render(<RouterProvider router={router} />);
    await screen.findByText("In environment production.");

    const openDelete = async (envName: string, triggerLabel: string) => {
      fireEvent.pointerDown(screen.getByRole("button", { name: triggerLabel }), { button: 0, ctrlKey: false });
      fireEvent.click(await screen.findByRole("menuitem", { name: `Delete MuniPages / ${envName} environment…` }));
      fireEvent.change(await screen.findByLabelText(`Type ${envName} to confirm`), { target: { value: envName } });
      fireEvent.click(screen.getByRole("button", { name: "Delete environment" }));
      await waitFor(() => expect(screen.getByRole("button", { name: "Deleting…" })).toBeInTheDocument());
    };

    await openDelete("production", "More actions for MuniPages production");
    // Switch while production deletion is pending, then start staging's.
    await router.navigate("/apps/app_munipages/environments/runtime_munipages_staging/overview");
    await screen.findByText("In environment staging.");
    await openDelete("staging", "More actions for MuniPages staging");
    // Production fails late: its error must NOT land in staging's dialog,
    // and staging's pending request must not be un-busy'd.
    rejectProd(new Error("production delete failed late"));
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.getByRole("button", { name: "Deleting…" })).toBeInTheDocument();
    expect(screen.queryByText("production delete failed late")).not.toBeInTheDocument();
    // Staging's own failure lands only in staging.
    rejectStaging(new Error("staging delete failed"));
    expect(await screen.findByRole("alert")).toHaveTextContent("staging delete failed");
    expect(screen.getByRole("button", { name: "Delete environment" })).toBeEnabled();
    void resolveProd;
    void resolveStaging;
  });

  it("a late secrets rename never closes staging's editor or writes its busy flag", async () => {
    let resolveRename!: () => void;
    vi.mocked(renameAppServiceSecretService).mockImplementation(
      () => new Promise((resolve) => { resolveRename = () => resolve(undefined as never); }),
    );
    vi.mocked(listAppServiceSecretServices).mockImplementation(async (id: string) =>
      id === "runtime_munipages_prod"
        ? [{ id: "svc_prod", appId: id, serviceName: "web", name: "web", secretCount: 0, status: "ok", createdAt: "2026-09-08T11:00:00Z", updatedAt: "2026-09-08T11:30:00Z" }]
        : [],
    );
    vi.mocked(getEnvironmentDetail).mockImplementation(async (id: string) => ({
      id,
      organizationId: "org_fixture_01",
      name: "MuniPages",
      slug: "munipages",
      plan: "Production medium",
      database: "postgres" as const,
      domains: [],
    }));
    vi.mocked(listAppServices).mockResolvedValue([]);
    const router = scopedRouter("/apps/app_munipages/environments/runtime_munipages_prod/secrets");
    render(<RouterProvider router={router} />);
    fireEvent.click(await screen.findByRole("button", { name: "Rename" }));
    fireEvent.change(screen.getByLabelText("Service name for web"), { target: { value: "billing" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    // Switch while the prod rename is pending.
    await router.navigate("/apps/app_munipages/environments/runtime_munipages_staging/secrets");
    resolveRename();
    await waitFor(() => expect(screen.getByText("In environment staging.")).toBeInTheDocument());
    await new Promise((r) => setTimeout(r, 50));
    // Staging was never editing: no editor input, no busy remnant, prod's
    // refreshed inventory never lands.
    expect(screen.queryByLabelText("Service name for web")).not.toBeInTheDocument();
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.queryByText("billing")).not.toBeInTheDocument();
  });

  it("a late secrets delete never refreshes or flags staging", async () => {
    let resolveDelete!: () => void;
    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
    vi.mocked(deleteAppServiceSecretService).mockImplementation(
      () => new Promise((resolve) => { resolveDelete = () => resolve(undefined as never); }),
    );
    vi.mocked(listAppServiceSecretServices).mockImplementation(async (id: string) =>
      id === "runtime_munipages_prod"
        ? [{ id: "svc_prod", appId: id, serviceName: "web", name: "web", secretCount: 0, status: "ok", createdAt: "2026-09-08T11:00:00Z", updatedAt: "2026-09-08T11:30:00Z" }]
        : [],
    );
    vi.mocked(getEnvironmentDetail).mockImplementation(async (id: string) => ({
      id,
      organizationId: "org_fixture_01",
      name: "MuniPages",
      slug: "munipages",
      plan: "Production medium",
      database: "postgres" as const,
      domains: [],
    }));
    vi.mocked(listAppServices).mockResolvedValue([]);
    const router = scopedRouter("/apps/app_munipages/environments/runtime_munipages_prod/secrets");
    render(<RouterProvider router={router} />);
    fireEvent.click(await screen.findByRole("button", { name: "Remove" }));
    expect(confirmSpy).toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Remove" })).toBeDisabled();
    // Switch while the prod delete is pending.
    await router.navigate("/apps/app_munipages/environments/runtime_munipages_staging/secrets");
    resolveDelete();
    await waitFor(() => expect(screen.getByText("In environment staging.")).toBeInTheDocument());
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole("alert")).toBeNull();
    // Staging's secrets UI shows its own (empty) inventory, not prod's row.
    expect(screen.queryByText("svc_prod")).not.toBeInTheDocument();
    confirmSpy.mockRestore();
  });

  it("fixture data contains production and staging as peers under one app", () => {
    const app = appsFixtureApplications().find((a) => a.id === "app_munipages")!;
    expect(app.environments.map((e) => e.name)).toContain("production");
    expect(app.environments.map((e) => e.name)).toContain("staging");
  });
});
