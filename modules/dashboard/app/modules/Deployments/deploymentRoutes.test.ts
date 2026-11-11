import { expect, it } from "vitest";
import { deploymentDetailPath, isDeploymentNumber } from "./deploymentRoutes";

it.each(["20261110.1", "20240229.42", "20261110.9223372036854775807"])("accepts deployment number %s", (value) => {
  expect(isDeploymentNumber(value)).toBe(true);
});

it.each([
  "", "dep_123", "20261110", "20261110.0", "20261110.01", "20261110.-1", " 20261110.1", "20261110.1 ",
  "20261310.1", "20230229.1", "2026111.1", "20261110.9223372036854775808", "２０２６１１１０.1", "20261110.1/x",
])("rejects malformed deployment number %j", (value) => {
  expect(isDeploymentNumber(value)).toBe(false);
});

it("builds organization-scoped number links without inferring a tenant", () => {
  expect(deploymentDetailPath("org 1/x", "20261110.1")).toBe("/deployments/20261110.1?organizationId=org+1%2Fx");
});
