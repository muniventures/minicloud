import type { DeploymentRef } from "@/api/deploymentsApi";
import { buildDashboardPath, dashboardRoutes } from "@/core/routing/dashboardRoutes";

export const deploymentOrganizationParam = "organizationId";

// Mirrors the API contract: yyyyMMdd.N with a real Gregorian date and an unpadded 1..Int64.MaxValue counter.
export function isDeploymentNumber(value: string | null | undefined): value is string {
  if (!value || value.length > 28) return false;
  const match = /^(\d{4})(\d{2})(\d{2})\.([1-9]\d{0,18})$/.exec(value);
  if (!match) return false;
  const [, year, month, day, counter] = match;
  const date = new Date(Date.UTC(Number(year), Number(month) - 1, Number(day)));
  date.setUTCFullYear(Number(year));
  if (Number(year) < 1 || date.getUTCMonth() !== Number(month) - 1 || date.getUTCDate() !== Number(day)) return false;
  return BigInt(counter) <= 9223372036854775807n;
}

export function deploymentDetailPath(organizationId: string, number: string) {
  const search = new URLSearchParams({ [deploymentOrganizationParam]: organizationId });
  return `${buildDashboardPath(dashboardRoutes.deploymentDetail, { deploymentNumber: number })}?${search.toString()}`;
}

export function deploymentKey(ref: DeploymentRef) {
  return JSON.stringify([ref.organizationId, ref.number]);
}
