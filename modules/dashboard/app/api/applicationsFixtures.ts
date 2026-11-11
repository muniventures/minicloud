import type { Application } from "../models/application";

// Synthetic fixtures for isolated UX review. These do not represent live state
// and are only used when VITE_APPS_FIXTURES=1 (or in tests). Enablement is
// explicit so production never silently renders invented applications.

export function appsFixturesEnabled(): boolean {
  try {
    return (import.meta as { env?: Record<string, string | undefined> }).env?.VITE_APPS_FIXTURES === "1";
  } catch {
    return false;
  }
}

const ORG = "org_fixture_01";

export function appsFixtureApplications(): Application[] {
  return [
    {
      id: "app_munipages",
      organizationId: ORG,
      name: "MuniPages",
      slug: "munipages",
      defaultEnvironmentId: "runtime_munipages_prod",
      deletionStatus: null,
      deletionError: null,
      environmentCount: 4,
      persistentEnvironmentCount: 2,
      previewEnvironmentCount: 2,
      createdAt: "2026-09-01T12:00:00Z",
      updatedAt: "2026-11-08T04:30:00Z",
      environments: [
        {
          id: "runtime_munipages_prod",
          applicationId: "app_munipages",
          organizationId: ORG,
          name: "production",
          kind: "persistent",
          runtimeSlug: "munipages",
          plan: "Production medium",
          planId: "plan_prod_medium",
          database: "postgres",
          sourceBranch: null,
          sourceEnvironmentId: null,
          deletionStatus: null,
          deletionError: null,
          placementLocked: true,
          domains: [
            { id: "dom_1", hostname: "app.example.com", url: "https://app.example.com", sslStatus: "active", reserved: false },
          ],
          latestDeployment: { id: "dep_1", number: "20261109.3", status: "succeeded", websiteUrl: "https://app.example.com", completedAt: "2026-11-09T08:12:00Z" },
          runtimeObservation: { state: "running", connected: true, stale: false, observedAt: "2026-11-09T09:00:00Z", lastSeenAt: "2026-11-09T09:00:00Z", totalServices: 4, healthyServices: 4 },
          createdAt: "2026-09-01T12:00:00Z",
          updatedAt: "2026-11-09T08:12:00Z",
        },
        {
          id: "runtime_munipages_staging",
          applicationId: "app_munipages",
          organizationId: ORG,
          name: "staging",
          kind: "persistent",
          runtimeSlug: "munipages-staging",
          plan: "Staging small",
          planId: "plan_staging_small",
          database: "sqlite",
          sourceBranch: null,
          sourceEnvironmentId: null,
          deletionStatus: null,
          deletionError: null,
          placementLocked: false,
          domains: [
            { id: "dom_2", hostname: "staging.app.example.com", url: "https://staging.app.example.com", sslStatus: "pending", reserved: false },
          ],
          latestDeployment: { id: "dep_2", number: "20261109.2", status: "failed", websiteUrl: null, completedAt: "2026-11-09T07:40:00Z" },
          runtimeObservation: { state: "stopped", connected: false, stale: true, observedAt: "2026-11-09T06:00:00Z", lastSeenAt: "2026-11-09T06:00:00Z", totalServices: 4, healthyServices: 0 },
          createdAt: "2026-09-02T12:00:00Z",
          updatedAt: "2026-11-09T07:40:00Z",
        },
        {
          id: "runtime_munipages_feat_checkout",
          applicationId: "app_munipages",
          organizationId: ORG,
          name: "feat-checkout",
          kind: "preview",
          runtimeSlug: "munipages-feat-checkout",
          plan: "Staging small",
          planId: "plan_staging_small",
          database: "sqlite",
          sourceBranch: "feat-checkout",
          sourceEnvironmentId: "runtime_munipages_staging",
          deletionStatus: null,
          deletionError: null,
          placementLocked: false,
          domains: [],
          latestDeployment: { id: "dep_3", number: "20261109.1", status: "succeeded", websiteUrl: "https://feat-checkout.munipages.example.com", completedAt: "2026-11-09T06:05:00Z" },
          runtimeObservation: { state: "running", connected: true, stale: false, observedAt: "2026-11-09T09:00:00Z", lastSeenAt: "2026-11-09T09:00:00Z", totalServices: 2, healthyServices: 2 },
          createdAt: "2026-11-08T10:00:00Z",
          updatedAt: "2026-11-09T06:05:00Z",
        },
        {
          id: "runtime_munipages_fix_billing",
          applicationId: "app_munipages",
          organizationId: ORG,
          name: "fix-billing",
          kind: "preview",
          runtimeSlug: "munipages-fix-billing",
          plan: "Staging small",
          planId: "plan_staging_small",
          database: "sqlite",
          sourceBranch: "fix-billing",
          sourceEnvironmentId: "runtime_munipages_prod",
          deletionStatus: null,
          deletionError: null,
          placementLocked: false,
          domains: [],
          latestDeployment: null,
          runtimeObservation: { state: "unknown", connected: false, stale: false, observedAt: null, lastSeenAt: null, totalServices: 0, healthyServices: 0 },
          createdAt: "2026-11-09T08:00:00Z",
          updatedAt: "2026-11-09T08:00:00Z",
        },
      ],
    },
    {
      id: "app_empty",
      organizationId: ORG,
      name: "EmptyApp",
      slug: "emptyapp",
      defaultEnvironmentId: null,
      deletionStatus: null,
      deletionError: null,
      environmentCount: 0,
      persistentEnvironmentCount: 0,
      previewEnvironmentCount: 0,
      createdAt: "2026-11-01T09:00:00Z",
      updatedAt: "2026-11-01T09:00:00Z",
      environments: [],
    },
  ];
}
