import { apiRequest } from "./apiClient";
import type {
  EnvironmentDetailResponse,
  AppServiceInventoryResponse,
  AppServiceSecretServiceResponse,
  AppServiceSecretResponse,
  DomainAvailabilityResponse,
  DomainBindingResponse
} from "../models/controlPlane";




export function getEnvironmentDetail(appId: string, signal?: AbortSignal) {
  return apiRequest<EnvironmentDetailResponse>(`/api/environments/${encodeURIComponent(appId)}`, { signal });
}

export function listAppServices(appId: string, signal?: AbortSignal) {
  return apiRequest<AppServiceInventoryResponse[]>(`/api/domains/services?appId=${encodeURIComponent(appId)}`, { signal });
}

export function listAppServiceSecrets(appId: string, serviceName: string, signal?: AbortSignal) {
  const search = new URLSearchParams({ appId, service: serviceName });
  return apiRequest<AppServiceSecretResponse[]>(`/api/secrets?${search.toString()}`, { signal });
}

export function listAppServiceSecretServices(appId: string, signal?: AbortSignal) {
  return apiRequest<AppServiceSecretServiceResponse[]>(`/api/secrets/services?appId=${encodeURIComponent(appId)}`, { signal });
}

export function createAppServiceSecretService(appId: string, serviceName: string) {
  return apiRequest<AppServiceSecretServiceResponse>(`/api/secrets/services?appId=${encodeURIComponent(appId)}`, {
    method: "POST",
    body: { serviceName }
  });
}

export function renameAppServiceSecretService(appId: string, serviceId: string, serviceName: string) {
  return apiRequest<AppServiceSecretServiceResponse>(
    `/api/secrets/services/${encodeURIComponent(serviceId)}?appId=${encodeURIComponent(appId)}`,
    {
      method: "PUT",
      body: { serviceName }
    }
  );
}

export function deleteAppServiceSecretService(appId: string, serviceId: string) {
  return apiRequest<void>(`/api/secrets/services/${encodeURIComponent(serviceId)}?appId=${encodeURIComponent(appId)}`, {
    method: "DELETE"
  });
}

export function setAppServiceSecret(appId: string, serviceName: string, name: string, value: string) {
  return apiRequest<AppServiceSecretResponse>(`/api/secrets?appId=${encodeURIComponent(appId)}`, {
    method: "POST",
    body: { serviceName, name, value }
  });
}

export function updateAppServiceSecret(appId: string, secretId: string, value: string) {
  return apiRequest<AppServiceSecretResponse>(`/api/secrets/${encodeURIComponent(secretId)}?appId=${encodeURIComponent(appId)}`, {
    method: "PUT",
    body: { value }
  });
}

export function deleteAppServiceSecret(appId: string, secretId: string) {
  return apiRequest<void>(`/api/secrets/${encodeURIComponent(secretId)}?appId=${encodeURIComponent(appId)}`, {
    method: "DELETE"
  });
}

export function createAppDomain(appId: string, serviceName: string, label?: string | null) {
  return apiRequest<DomainBindingResponse>(`/api/domains?appId=${encodeURIComponent(appId)}`, {
    method: "POST",
    body: {
      serviceName,
      label: label?.trim() ? label.trim() : null
    }
  });
}

export function checkAppDomainAvailability(appId: string, label: string) {
  return apiRequest<DomainAvailabilityResponse>(
    `/api/domains/availability?appId=${encodeURIComponent(appId)}&label=${encodeURIComponent(label)}`
  );
}

export function disableAppDomain(appId: string, domainId: string) {
  return apiRequest<DomainBindingResponse>(`/api/domains/${encodeURIComponent(domainId)}?appId=${encodeURIComponent(appId)}`, {
    method: "PATCH",
    body: { enabled: false }
  });
}

export function setAppDomainPrimary(appId: string, domainId: string) {
  return apiRequest<DomainBindingResponse>(`/api/domains/${encodeURIComponent(domainId)}?appId=${encodeURIComponent(appId)}`, {
    method: "PATCH",
    body: { isPrimary: true }
  });
}

export function deleteAppDomain(appId: string, domainId: string) {
  return apiRequest<void>(`/api/domains/${encodeURIComponent(domainId)}?appId=${encodeURIComponent(appId)}`, {
    method: "DELETE"
  });
}





export function createAppCustomDomain(appId: string, serviceName: string, hostname: string) {
  return apiRequest<DomainBindingResponse>(`/api/domains?appId=${encodeURIComponent(appId)}`, {
    method: "POST", body: { serviceName, hostname }
  });
}

export function verifyAppDomain(appId: string, domainId: string) {
  return apiRequest<DomainBindingResponse>(`/api/domains/${encodeURIComponent(domainId)}/verify?appId=${encodeURIComponent(appId)}`, { method: "POST" });
}

export function listAppStorage(appId: string, signal?: AbortSignal) {
  return apiRequest<import("../models/controlPlane").AppStorageVolume[]>(`/api/environments/${encodeURIComponent(appId)}/storage`, { signal });
}
