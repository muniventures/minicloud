import { apiRequest } from "./apiClient";
import type { CustomDomainRegistrationResponse } from "@/models/controlPlane";

export function listCustomDomains(appId: string, signal?: AbortSignal) {
  return apiRequest<CustomDomainRegistrationResponse[]>(`/api/custom-domains?appId=${encodeURIComponent(appId)}`, { signal });
}

export function registerCustomDomain(appId: string, hostname: string) {
  return apiRequest<CustomDomainRegistrationResponse>(`/api/custom-domains?appId=${encodeURIComponent(appId)}`, {
    method: "POST", body: { hostname }
  });
}

export function verifyCustomDomainOwnership(appId: string, id: string) {
  return apiRequest<CustomDomainRegistrationResponse>(`/api/custom-domains/${encodeURIComponent(id)}/verify?appId=${encodeURIComponent(appId)}`, { method: "POST" });
}

export function removeCustomDomain(appId: string, id: string) {
  return apiRequest<void>(`/api/custom-domains/${encodeURIComponent(id)}?appId=${encodeURIComponent(appId)}`, { method: "DELETE" });
}
