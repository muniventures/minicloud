import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AppLandingRedirect } from "./AppLandingRedirect";
import { AppSettingsModule } from "./AppSettingsModule";
import {
  createEnvironment,
  deleteApplication,
  deleteEnvironment,
  getApplication,
  updateApplication,
  updateEnvironment,
} from "../../api/applicationsApi";
import { listPlans, type ProviderPlan } from "../../api/providersApi";
import type { Application } from "../../models/application";

vi.mock("../../api/applicationsApi", () => ({
  getApplication: vi.fn(),
  updateApplication: vi.fn(),
  createEnvironment: vi.fn(),
  updateEnvironment: vi.fn(),
  deleteEnvironment: vi.fn(),
  deleteApplication: vi.fn(),
}));

vi.mock("../../api/applicationsFixtures", () => ({
  appsFixturesEnabled: () => false,
  appsFixtureApplications: () => [],
}));

vi.mock("../../api/providersApi", () => ({
  listPlans: vi.fn(),
}));

const authState = vi.hoisted(() => ({ organization: { id: "org_1", name: "Org", role: "owner" } }));
vi.mock("../../core/auth/AuthProvider", () => ({
  useAuth: () => ({
    selectedOrganization: authState.organization,
  }),
}));

function sampleApplication(): Application {
  const baseEnv = {
    applicationId: "appl_munipages",
    organizationId: "org_1",
    kind: "persistent",
    runtimeSlug: "munipages",
    plan: "Small NB",
    planId: "plan_1",
    database: "postgres",
    sourceBranch: null,
    sourceEnvironmentId: null,
    deletionStatus: null,
    deletionError: null,
    placementLocked: true,
    domains: [],
    latestDeployment: null,
    runtimeObservation: {
      state: "unknown",
      connected: false,
      stale: false,
      observedAt: null,
      lastSeenAt: null,
      totalServices: 0,
      healthyServices: 0,
    },
    createdAt: "2026-09-01T12:00:00Z",
    updatedAt: "2026-09-01T12:00:00Z",
  };
  return {
    id: "appl_munipages",
    organizationId: "org_1",
    name: "MuniPages",
    slug: "munipages",
    defaultEnvironmentId: "app_staging",
    deletionStatus: null,
    deletionError: null,
    environmentCount: 3,
    persistentEnvironmentCount: 2,
    previewEnvironmentCount: 1,
    createdAt: "2026-09-01T12:00:00Z",
    updatedAt: "2026-09-01T12:00:00Z",
    environments: [
      { ...baseEnv, id: "app_prod", name: "production" },
      { ...baseEnv, id: "app_staging", name: "staging", runtimeSlug: "munipages-env-staging", placementLocked: false },
      {
        ...baseEnv,
        id: "app_pr_42",
        name: "feat-checkout",
        kind: "preview",
        runtimeSlug: "munipages-b-feat-checkout",
        sourceBranch: "feat/checkout",
        sourceEnvironmentId: "app_prod",
        placementLocked: false,
      },
    ],
  };
}

describe("AppLandingRedirect & AppSettingsModule", () => {
  afterEach(cleanup);
  beforeEach(() => {
    vi.clearAllMocks();
    authState.organization = { id: "org_1", name: "Org", role: "owner" };
    vi.mocked(listPlans).mockResolvedValue([
      {
        id: "plan_1",
        name: "Small NB",
        providerName: "Hetzner EU",
        providerType: "hetzner",
        region: "nbg1",
        sku: "cx23",
        enabled: true,
        accountEnabled: true,
      },
    ] as ProviderPlan[]);
  });

  it("redirects /apps/:applicationId to the dashboard default environment overview", async () => {
    vi.mocked(getApplication).mockResolvedValue(sampleApplication());
    const router = createMemoryRouter(
      [
        { path: "/apps/:applicationId", element: <AppLandingRedirect /> },
        {
          path: "/apps/:applicationId/environments/:environmentId/:tab",
          element: <div data-testid="scoped-overview" />,
        },
      ],
      { initialEntries: ["/apps/appl_munipages"] },
    );
    render(<RouterProvider router={router} />);
    await waitFor(() =>
      expect(router.state.location.pathname).toBe(
        "/apps/appl_munipages/environments/app_staging/overview",
      ),
    );
  });

  it("does not substitute a different environment when the default is missing", async () => {
    vi.mocked(getApplication).mockResolvedValue({ ...sampleApplication(), defaultEnvironmentId: "missing" });
    const router = createMemoryRouter([
      { path: "/apps/:applicationId", element: <AppLandingRedirect /> },
      { path: "/apps/:applicationId/settings", element: <div data-testid="settings" /> },
    ], { initialEntries: ["/apps/appl_munipages"] });
    render(<RouterProvider router={router} />);
    await waitFor(() => expect(router.state.location.pathname).toBe("/apps/appl_munipages/settings"));
    expect(getApplication).toHaveBeenCalledTimes(1);
  });

  it("redirects /apps/:applicationId with zero environments to settings?addEnvironment=1", async () => {
    vi.mocked(getApplication).mockResolvedValue({
      ...sampleApplication(),
      defaultEnvironmentId: null,
      environmentCount: 0,
      persistentEnvironmentCount: 0,
      previewEnvironmentCount: 0,
      environments: [],
    });
    const router = createMemoryRouter(
      [
        { path: "/apps/:applicationId", element: <AppLandingRedirect /> },
        { path: "/apps/:applicationId/settings", element: <div data-testid="settings" /> },
      ],
      { initialEntries: ["/apps/appl_munipages"] },
    );
    render(<RouterProvider router={router} />);
    await waitFor(() => {
      expect(router.state.location.pathname).toBe("/apps/appl_munipages/settings");
      expect(router.state.location.search).toBe("?addEnvironment=1");
    });
  });

  it("shows canonical application errors (403/404/503) with Retry", async () => {
    for (const status of [403, 404, 503]) {
      cleanup();
      vi.clearAllMocks();
      vi.mocked(getApplication).mockRejectedValue(Object.assign(new Error(`Request failed (${status})`), { status }));
      const router = createMemoryRouter(
        [{ path: "/apps/:applicationId", element: <AppLandingRedirect /> }],
        { initialEntries: ["/apps/some_runtime_id"] },
      );
      render(<RouterProvider router={router} />);
      expect(await screen.findByRole("alert")).toHaveTextContent(`Request failed (${status})`);
      expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
      expect(screen.queryByText("Loading app...")).not.toBeInTheDocument();
    }
  });

  it("keeps application settings read-only for viewers", async () => {
    authState.organization.role = "viewer";
    vi.mocked(getApplication).mockResolvedValue(sampleApplication());
    const router = createMemoryRouter([
      { path: "/apps/:applicationId/settings", element: <AppSettingsModule /> },
    ], { initialEntries: ["/apps/appl_munipages/settings"] });
    render(<RouterProvider router={router} />);
    expect(await screen.findByText("MuniPages settings")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save name" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add environment" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Delete" })).not.toBeInTheDocument();
  });

  it("blocks configuration changes while application deletion is queued", async () => {
    vi.mocked(getApplication).mockResolvedValue({ ...sampleApplication(), deletionStatus: "queued" });
    const router = createMemoryRouter([
      { path: "/apps/:applicationId/settings", element: <AppSettingsModule /> },
    ], { initialEntries: ["/apps/appl_munipages/settings"] });
    render(<RouterProvider router={router} />);
    expect(await screen.findByText("MuniPages settings")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save name" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add environment" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Change plan" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Deleting…" })).toBeDisabled();
  });

  it("retains environment creation input and URL after a rejected write", async () => {
    vi.mocked(getApplication).mockResolvedValue(sampleApplication());
    vi.mocked(createEnvironment).mockRejectedValueOnce(new Error("Environment name already exists."));
    const router = createMemoryRouter([
      { path: "/apps/:applicationId/settings", element: <AppSettingsModule /> },
    ], { initialEntries: ["/apps/appl_munipages/settings?addEnvironment=1"] });
    render(<RouterProvider router={router} />);
    await screen.findByRole("option", { name: /Small NB/ });
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "qa" } });
    fireEvent.change(screen.getByLabelText("Plan"), { target: { value: "Small NB" } });
    fireEvent.click(screen.getByRole("button", { name: "Create environment" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Environment name already exists.");
    expect(screen.getByLabelText("Name")).toHaveValue("qa");
    expect(screen.getByLabelText("Plan")).toHaveValue("Small NB");
    expect(router.state.location.search).toBe("?addEnvironment=1");
  });

  it("fails closed when the requested app belongs to another organization", async () => {
    vi.mocked(getApplication).mockResolvedValue({ ...sampleApplication(), organizationId: "org_other" });
    const router = createMemoryRouter([
      { path: "/apps/:applicationId", element: <AppLandingRedirect /> },
    ], { initialEntries: ["/apps/appl_munipages"] });
    render(<RouterProvider router={router} />);
    expect(await screen.findByRole("alert")).toHaveTextContent("does not belong to the selected organization");
    expect(router.state.location.pathname).toBe("/apps/appl_munipages");
  });

  it("manages application rename, environment creation/rename/default/delete, and whole-app deletion in AppSettingsModule", async () => {
    vi.mocked(getApplication).mockResolvedValue(sampleApplication());
    vi.mocked(updateApplication).mockResolvedValue(sampleApplication());
    vi.mocked(createEnvironment).mockResolvedValue(sampleApplication().environments[1]);
    vi.mocked(updateEnvironment).mockResolvedValue(sampleApplication().environments[0]);
    vi.mocked(deleteEnvironment).mockResolvedValue(undefined);
    vi.mocked(deleteApplication).mockResolvedValue(undefined);

    const router = createMemoryRouter(
      [
        { path: "/apps/:applicationId/settings", element: <AppSettingsModule /> },
        { path: "/apps", element: <div data-testid="apps-list" /> },
      ],
      { initialEntries: ["/apps/appl_munipages/settings?addEnvironment=1"] },
    );
    render(<RouterProvider router={router} />);

    expect(await screen.findByText("MuniPages settings")).toBeInTheDocument();
    expect(screen.getByText("Branch: feat/checkout")).toBeInTheDocument();
    expect(screen.getByText("Placement locked")).toBeInTheDocument();
    expect(
      screen.getByText(/Affected environments: production \(persistent\), staging \(persistent\), feat-checkout \(preview\)/),
    ).toBeInTheDocument();

    // Add environment using plan catalog
    expect(await screen.findByRole("option", { name: /Small NB/ })).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "qa" } });
    fireEvent.change(screen.getByLabelText("Plan"), { target: { value: "Small NB" } });
    fireEvent.click(screen.getByRole("button", { name: "Create environment" }));
    await waitFor(() =>
      expect(createEnvironment).toHaveBeenCalledWith("appl_munipages", {
        name: "qa",
        plan: "Small NB",
        database: "sqlite",
      }),
    );

    // Delete single environment requires typing environment name
    const deleteButtons = screen.getAllByRole("button", { name: "Delete" });
    fireEvent.click(deleteButtons[0]);
    const confirmEnvInput = screen.getByLabelText("Confirm deletion of production");
    const confirmEnvButton = screen.getByRole("button", { name: "Delete environment" });
    expect(confirmEnvButton).toBeDisabled();
    fireEvent.change(confirmEnvInput, { target: { value: "production" } });
    expect(confirmEnvButton).not.toBeDisabled();
    fireEvent.click(confirmEnvButton);
    await waitFor(() =>
      expect(deleteEnvironment).toHaveBeenCalledWith("appl_munipages", "app_prod", "production"),
    );

    // Delete whole application requires typing slug
    const confirmAppInput = screen.getByLabelText("Type munipages to confirm");
    const confirmAppButton = screen.getByRole("button", { name: "Permanently delete application" });
    expect(confirmAppButton).toBeDisabled();
    fireEvent.change(confirmAppInput, { target: { value: "munipages" } });
    expect(confirmAppButton).not.toBeDisabled();
    fireEvent.click(confirmAppButton);
    await waitFor(() =>
      expect(deleteApplication).toHaveBeenCalledWith("appl_munipages", "munipages"),
    );
  });
});
