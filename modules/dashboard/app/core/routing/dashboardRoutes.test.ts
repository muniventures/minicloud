import { describe, expect, it } from "vitest";
import { buildDashboardPath, dashboardRoutes, navigationRoutes } from "./dashboardRoutes";

describe("dashboardRoutes", () => {
  it("builds parameterized paths", () => {
    expect(buildDashboardPath(dashboardRoutes.environmentDetail, { applicationId: "application_123", environmentId: "environment_123", tab: "deployments" })).toBe(
      "/apps/application_123/environments/environment_123/deployments"
    );
  });

  it("exposes primary navigation routes", () => {
    expect(navigationRoutes.map((route) => route.key)).toEqual(["apps", "databases", "plans", "deployments"]);
    expect(dashboardRoutes.databases.path).toBe("/databases");
    expect(dashboardRoutes.plans.path).toBe("/plans");
    expect(dashboardRoutes.providerAccounts.path).toBe("/plans/accounts");
  });
});
