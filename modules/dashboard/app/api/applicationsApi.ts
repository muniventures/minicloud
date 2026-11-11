import { apiRequest } from "./apiClient";
import type {
  ApplicationEnvironment,
  Application,
  SshAccessRequest,
} from "../models/application";
import { appsFixtureApplications, appsFixturesEnabled } from "./applicationsFixtures";

export type CreateApplicationRequest = {
  organizationId: string;
  name: string;
  slug: string;
  initialEnvironment?: { name: string; plan: string; database?: string; sshAccess?: SshAccessRequest } | null;
};

export type UpdateApplicationRequest = {
  name?: string;
  defaultEnvironmentId?: string | null;
};

export type CreateEnvironmentRequest = {
  name: string;
  plan: string;
  database?: string;
  kind?: string;
  sourceBranch?: string | null;
  sourceEnvironmentId?: string | null;
  sshAccess?: SshAccessRequest;
};

export type UpdateEnvironmentRequest = {
  name?: string;
  plan?: string;
  database?: string;
  setAsDefault?: boolean;
  // Omit to preserve the saved selection; explicit selections require expectedRevision.
  sshAccess?: SshAccessRequest & { expectedRevision: number };
};

// Production requests use the explicit application/environment API. Synthetic
// fixtures require explicit opt-in for isolated UI review; API errors never
// fall back to fixture state.
export function listApplications(organizationId: string, signal?: AbortSignal) {
  if (appsFixturesEnabled() && !signal) return Promise.resolve(appsFixtureApplications());
  return apiRequest<Application[]>(`/api/apps?organizationId=${encodeURIComponent(organizationId)}`, { signal });
}

export function getApplication(applicationId: string, signal?: AbortSignal) {
  return apiRequest<Application>(`/api/apps/${encodeURIComponent(applicationId)}`, { signal });
}

export function createApplication(request: CreateApplicationRequest) {
  return apiRequest<Application>("/api/apps", { method: "POST", body: request });
}

export function updateApplication(applicationId: string, request: UpdateApplicationRequest) {
  return apiRequest<Application>(`/api/apps/${encodeURIComponent(applicationId)}`, { method: "PATCH", body: request });
}

export function deleteApplication(applicationId: string, confirmation: string) {
  return apiRequest<void>(`/api/apps/${encodeURIComponent(applicationId)}`, { method: "DELETE", body: { confirmation } });
}

export function listEnvironments(applicationId: string, signal?: AbortSignal) {
  return apiRequest<ApplicationEnvironment[]>(`/api/apps/${encodeURIComponent(applicationId)}/environments`, { signal });
}

export function createEnvironment(applicationId: string, request: CreateEnvironmentRequest) {
  return apiRequest<ApplicationEnvironment>(`/api/apps/${encodeURIComponent(applicationId)}/environments`, { method: "POST", body: request });
}

export function getEnvironment(applicationId: string, environmentId: string, signal?: AbortSignal) {
  return apiRequest<ApplicationEnvironment>(`/api/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(environmentId)}`, { signal });
}

export function updateEnvironment(applicationId: string, environmentId: string, request: UpdateEnvironmentRequest) {
  return apiRequest<ApplicationEnvironment>(`/api/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(environmentId)}`, { method: "PATCH", body: request });
}

export function deleteEnvironment(applicationId: string, environmentId: string, confirmation: string) {
  return apiRequest<void>(`/api/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(environmentId)}`, { method: "DELETE", body: { confirmation } });
}
