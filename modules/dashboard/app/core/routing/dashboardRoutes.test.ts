import { describe, expect, it } from "vitest";
import { buildDashboardPath, dashboardRoutes, navigationRoutes } from "./dashboardRoutes";

describe("dashboardRoutes", () => {
  it("builds parameterized paths", () => {
    expect(buildDashboardPath(dashboardRoutes.appDeploymentDetail, { appId: "app_123", deploymentId: "dep_123" })).toBe(
      "/apps/app_123/deployments/dep_123"
    );
  });

  it("exposes primary navigation routes", () => {
    expect(navigationRoutes.map((route) => route.key)).toEqual(["apps", "databases", "providers", "deployments"]);
  });
});
