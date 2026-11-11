import { apiRequest } from "./apiClient";
import type {
  DeploymentActivityResponse,
  DeploymentCancellationResponse,
  DeploymentEventResponse,
  DeploymentLogResponse,
  DeploymentResponse,
} from "../models/controlPlane";

// Customer deployment routes resolve only by organization and deployment number.
export type DeploymentRef = { organizationId: string; number: string };

function deploymentPath(ref: DeploymentRef, suffix = "") {
  const search = new URLSearchParams({ organizationId: ref.organizationId });
  return `/api/deployments/${encodeURIComponent(ref.number)}${suffix}?${search.toString()}`;
}

export function listDeployments(params: { organizationId: string; appId?: string }, signal?: AbortSignal) {
  const search = new URLSearchParams({ organizationId: params.organizationId, all: "true" });
  if (params.appId) {
    search.set("appId", params.appId);
  }

  return apiRequest<DeploymentResponse[]>(`/api/deployments?${search.toString()}`, { signal });
}

export function getDeployment(ref: DeploymentRef, signal?: AbortSignal) {
  return apiRequest<DeploymentResponse>(deploymentPath(ref), { signal });
}

export function dispatchDeployment(ref: DeploymentRef) {
  return apiRequest<DeploymentResponse>(deploymentPath(ref, "/dispatch"), { method: "POST" });
}

export function cancelDeployment(ref: DeploymentRef) {
  return apiRequest<DeploymentCancellationResponse>(deploymentPath(ref, "/cancel"), { method: "POST" });
}

export function refreshDeployment(ref: DeploymentRef) {
  return apiRequest<DeploymentResponse>(deploymentPath(ref, "/refresh"), { method: "POST" });
}

export function listDeploymentEvents(ref: DeploymentRef, signal?: AbortSignal) {
  return apiRequest<DeploymentEventResponse[]>(deploymentPath(ref, "/events"), { signal });
}

export function listDeploymentActivities(ref: DeploymentRef, signal?: AbortSignal) {
  return apiRequest<DeploymentActivityResponse[]>(deploymentPath(ref, "/activities"), { signal });
}

export function listDeploymentLogs(ref: DeploymentRef, signal?: AbortSignal) {
  return apiRequest<DeploymentLogResponse[]>(deploymentPath(ref, "/logs"), { signal });
}
