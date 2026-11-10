import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AppDetailModule } from "./AppDetailModule";
import { getApplication } from "../../api/applicationsApi";
import { getEnvironmentDetail, listAppServices, listAppServiceSecretServices } from "../../api/environmentsApi";
import { listDeployments } from "../../api/deploymentsApi";
import type { Application } from "../../models/application";

vi.mock("../../api/applicationsApi", () => ({ getApplication: vi.fn() }));
vi.mock("../../api/applicationsFixtures", () => ({ appsFixturesEnabled: () => false }));
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
vi.mock("../../api/deploymentsApi", () => ({ listDeployments: vi.fn() }));
vi.mock("../../core/auth/AuthProvider", () => ({
  useAuth: () => ({ selectedOrganization: { id: "org_1", name: "Org", role: "owner" } }),
}));
vi.mock("./AppConsolePanel", () => ({ AppConsolePanel: () => <div data-testid="console-panel" /> }));

// Regression: the dashboard default can be staging. Identity must bind to
// the selected environment id itself (the retained runtime id), never to
// the production environment — there is no fixture-only runtimeAppId field.
function customApp(): Application {
  const base = {
    applicationId: "app_1",
    organizationId: "org_1",
    kind: "persistent",
    runtimeSlug: "x",
    plan: "p",
    planId: null,
    database: "sqlite",
    sourceBranch: null,
    sourceEnvironmentId: null,
    deletionStatus: null,
    deletionError: null,
    placementLocked: false,
    domains: [],
    latestDeployment: null,
    runtimeObservation: {
      state: "unknown", connected: false, stale: false,
      observedAt: null, lastSeenAt: null, totalServices: 0, healthyServices: 0,
    },
    createdAt: "2026-09-01T12:00:00Z",
    updatedAt: "2026-09-01T12:00:00Z",
    // Deliberately NO runtimeAppId field: production code must not need one.
    ...({} as Record<string, unknown>),
  };
  return {
    id: "app_1",
    organizationId: "org_1",
    name: "MuniPages",
    slug: "munipages",
    defaultEnvironmentId: "runtime_staging", // dashboard default IS staging
    deletionStatus: null,
    deletionError: null,
    environmentCount: 2,
    persistentEnvironmentCount: 2,
    previewEnvironmentCount: 0,
    createdAt: "2026-09-01T12:00:00Z",
    updatedAt: "2026-09-01T12:00:00Z",
    environments: [
      { ...base, id: "runtime_production", name: "production" },
      { ...base, id: "runtime_staging", name: "staging" },
    ],
  };
}

function scopedRouter(tab = "overview") {
  return createMemoryRouter(
    [{ path: "/apps/:applicationId/environments/:environmentId/:tab", element: <AppDetailModule /> }],
    { initialEntries: [`/apps/app_1/environments/runtime_staging/${tab}`] },
  );
}

describe("EnvironmentIdentity (regression)", () => {
  afterEach(cleanup);
  beforeEach(() => {
    sessionStorage.clear();
    vi.clearAllMocks();
    vi.mocked(getApplication).mockResolvedValue(customApp());
    vi.mocked(listDeployments).mockResolvedValue([]);
    vi.mocked(listAppServiceSecretServices).mockResolvedValue([]);
    vi.mocked(listAppServices).mockResolvedValue([]);
    vi.mocked(getEnvironmentDetail).mockImplementation(async (id: string) => ({
      id,
      organizationId: "org_1",
      name: "MuniPages",
      slug: "munipages",
      plan: "p",
      database: "sqlite",
      domains: [],
    }));
  });

  it("rejects application responses outside the selected organization before loading runtime resources", async () => {
    vi.mocked(getApplication).mockResolvedValue({ ...customApp(), organizationId: "org_other" });
    render(<RouterProvider router={scopedRouter()} />);
    await screen.findByText("Application does not belong to the selected organization.");
    await waitFor(() => expect(getEnvironmentDetail).not.toHaveBeenCalled());
    expect(listAppServices).not.toHaveBeenCalled();
  });

  it("binds selected staging (dashboard default) to its own runtime identity, not the production environment", async () => {
    render(<RouterProvider router={scopedRouter()} />);
    expect(await screen.findByText("In environment staging.")).toBeInTheDocument();

    const loadedIds = vi.mocked(getEnvironmentDetail).mock.calls.map(([id]) => id);
    // Staging's own id loaded; production (production environment) never loaded.
    expect(new Set(loadedIds)).toEqual(new Set(["runtime_staging"]));
  });
});
