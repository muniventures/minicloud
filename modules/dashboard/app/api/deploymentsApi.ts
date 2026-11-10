import { apiRequest } from "./apiClient";
import type { DeploymentActivityResponse, DeploymentEventResponse, DeploymentLogResponse, DeploymentResponse } from "../models/controlPlane";

export function listDeployments(params: { organizationId: string; appId?: string }, signal?: AbortSignal) {
  const search = new URLSearchParams({ organizationId: params.organizationId, all: "true" });
  if (params.appId) {
    search.set("appId", params.appId);
  }

  return apiRequest<DeploymentResponse[]>(`/api/deployments?${search.toString()}`, { signal });
}

export function getDeployment(deploymentId: string, signal?: AbortSignal) {
  return apiRequest<DeploymentResponse>(`/api/deployments/${encodeURIComponent(deploymentId)}`, { signal });
}

export function dispatchDeployment(deploymentId: string) {
  return apiRequest<DeploymentResponse>(`/api/deployments/${encodeURIComponent(deploymentId)}/dispatch`, { method: "POST" });
}

export function cancelDeployment(deploymentId: string) {
  return apiRequest<{ id: string; cancellationRequested: boolean }>(`/api/deployments/${encodeURIComponent(deploymentId)}/cancel`, { method: "POST" });
}

export function refreshDeployment(deploymentId: string) {
  return apiRequest<DeploymentResponse>(`/api/deployments/${encodeURIComponent(deploymentId)}/refresh`, { method: "POST" });
}

export function listDeploymentEvents(deploymentId: string, signal?: AbortSignal) {
  return apiRequest<DeploymentEventResponse[]>(`/api/deployments/${encodeURIComponent(deploymentId)}/events`, { signal });
}

export function listDeploymentActivities(deploymentId: string, signal?: AbortSignal) {
  return apiRequest<DeploymentActivityResponse[]>(`/api/deployments/${encodeURIComponent(deploymentId)}/activities`, { signal });
}

export function listDeploymentLogs(deploymentId: string, signal?: AbortSignal) {
  return apiRequest<DeploymentLogResponse[]>(`/api/deployments/${encodeURIComponent(deploymentId)}/logs`, { signal });
}
