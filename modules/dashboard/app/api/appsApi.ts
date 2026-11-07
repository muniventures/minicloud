import { apiRequest } from "./apiClient";
import type {
  AppPlan,
  AppResponse,
  AppServiceInventoryResponse,
  AppServiceSecretServiceResponse,
  AppServiceSecretResponse,
  DatabaseMode,
  DomainAvailabilityResponse,
  DomainBindingResponse
} from "../models/controlPlane";

export type CreateAppRequest = {
  organizationId: string;
  name: string;
  slug: string;
  plan: AppPlan;
  database: DatabaseMode;
};

export function listApps(organizationId: string, signal?: AbortSignal, includeBranches = false) {
  return apiRequest<AppResponse[]>(`/v1/apps?organizationId=${encodeURIComponent(organizationId)}${includeBranches ? "&includeBranches=true" : ""}`, { signal });
}

export function getApp(appId: string, signal?: AbortSignal) {
  return apiRequest<AppResponse>(`/v1/apps/${encodeURIComponent(appId)}`, { signal });
}

export function listAppServices(appId: string, signal?: AbortSignal) {
  return apiRequest<AppServiceInventoryResponse[]>(`/v1/domains/services?appId=${encodeURIComponent(appId)}`, { signal });
}

export function listAppServiceSecrets(appId: string, serviceName: string, signal?: AbortSignal) {
  const search = new URLSearchParams({ appId, service: serviceName });
  return apiRequest<AppServiceSecretResponse[]>(`/v1/secrets?${search.toString()}`, { signal });
}

export function listAppServiceSecretServices(appId: string, signal?: AbortSignal) {
  return apiRequest<AppServiceSecretServiceResponse[]>(`/v1/secrets/services?appId=${encodeURIComponent(appId)}`, { signal });
}

export function createAppServiceSecretService(appId: string, serviceName: string) {
  return apiRequest<AppServiceSecretServiceResponse>(`/v1/secrets/services?appId=${encodeURIComponent(appId)}`, {
    method: "POST",
    body: { serviceName }
  });
}

export function renameAppServiceSecretService(appId: string, serviceId: string, serviceName: string) {
  return apiRequest<AppServiceSecretServiceResponse>(
    `/v1/secrets/services/${encodeURIComponent(serviceId)}?appId=${encodeURIComponent(appId)}`,
    {
      method: "PUT",
      body: { serviceName }
    }
  );
}

export function deleteAppServiceSecretService(appId: string, serviceId: string) {
  return apiRequest<void>(`/v1/secrets/services/${encodeURIComponent(serviceId)}?appId=${encodeURIComponent(appId)}`, {
    method: "DELETE"
  });
}

export function setAppServiceSecret(appId: string, serviceName: string, name: string, value: string) {
  return apiRequest<AppServiceSecretResponse>(`/v1/secrets?appId=${encodeURIComponent(appId)}`, {
    method: "POST",
    body: { serviceName, name, value }
  });
}

export function updateAppServiceSecret(appId: string, secretId: string, value: string) {
  return apiRequest<AppServiceSecretResponse>(`/v1/secrets/${encodeURIComponent(secretId)}?appId=${encodeURIComponent(appId)}`, {
    method: "PUT",
    body: { value }
  });
}

export function deleteAppServiceSecret(appId: string, secretId: string) {
  return apiRequest<void>(`/v1/secrets/${encodeURIComponent(secretId)}?appId=${encodeURIComponent(appId)}`, {
    method: "DELETE"
  });
}

export function createAppDomain(appId: string, serviceName: string, label?: string | null) {
  return apiRequest<DomainBindingResponse>(`/v1/domains?appId=${encodeURIComponent(appId)}`, {
    method: "POST",
    body: {
      serviceName,
      label: label?.trim() ? label.trim() : null
    }
  });
}

export function checkAppDomainAvailability(appId: string, label: string) {
  return apiRequest<DomainAvailabilityResponse>(
    `/v1/domains/availability?appId=${encodeURIComponent(appId)}&label=${encodeURIComponent(label)}`
  );
}

export function disableAppDomain(appId: string, domainId: string) {
  return apiRequest<DomainBindingResponse>(`/v1/domains/${encodeURIComponent(domainId)}?appId=${encodeURIComponent(appId)}`, {
    method: "PATCH",
    body: { enabled: false }
  });
}

export function setAppDomainPrimary(appId: string, domainId: string) {
  return apiRequest<DomainBindingResponse>(`/v1/domains/${encodeURIComponent(domainId)}?appId=${encodeURIComponent(appId)}`, {
    method: "PATCH",
    body: { isPrimary: true }
  });
}

export function deleteAppDomain(appId: string, domainId: string) {
  return apiRequest<void>(`/v1/domains/${encodeURIComponent(domainId)}?appId=${encodeURIComponent(appId)}`, {
    method: "DELETE"
  });
}

export function createApp(request: CreateAppRequest) {
  return apiRequest<AppResponse>("/v1/apps", {
    method: "POST",
    body: request
  });
}

export function deleteApp(appId: string, confirmation: string) {
  return apiRequest<void>(`/v1/apps/${encodeURIComponent(appId)}`, { method: "DELETE", body: { confirmation } });
}

export function createAppCustomDomain(appId: string, serviceName: string, hostname: string) {
  return apiRequest<DomainBindingResponse>(`/v1/domains?appId=${encodeURIComponent(appId)}`, {
    method: "POST", body: { serviceName, hostname }
  });
}

export function verifyAppDomain(appId: string, domainId: string) {
  return apiRequest<DomainBindingResponse>(`/v1/domains/${encodeURIComponent(domainId)}/verify?appId=${encodeURIComponent(appId)}`, { method: "POST" });
}
