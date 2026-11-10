import { apiRequest } from "./apiClient";

export type ProviderType = { type: string; name: string; supportsStorage: boolean; defaultImage: string | null; defaultOsId?: number | null };
export type ProviderAccount = {
  id: string; organizationId: string; name: string; providerType: string; enabled: boolean;
  projectId?: string | null; defaultOsId?: number | null; validationStatus: string;
  lastValidatedAt?: string | null; revision: number; planCount: number;
};
export type ProviderPlan = {
  id: string; organizationId: string; providerAccountId: string; name: string;
  providerName: string; providerType: string; region: string; sku: string; image?: string | null;
  buildPlatform: string; enabled: boolean; accountEnabled: boolean; revision: number; provisioningLocked: boolean;
};
export type CreateProvider = { organizationId: string; name: string; providerType: string; credential: string; projectId?: string; defaultOsId?: number };
export type PlanInput = { name?: string; region: string; sku: string; image?: string | null; buildPlatform: string; enabled?: boolean };

export function listProviderTypes(signal?: AbortSignal) { return apiRequest<ProviderType[]>("/api/provider-types", { signal }); }
export function listProviders(organizationId: string, signal?: AbortSignal) {
  return apiRequest<ProviderAccount[]>(`/api/providers?${new URLSearchParams({ organizationId })}`, { signal });
}
export function createProvider(body: CreateProvider) { return apiRequest<ProviderAccount>("/api/providers", { method: "POST", body }); }
export function updateProvider(id: string, body: { name?: string; enabled?: boolean; revision: number }) {
  return apiRequest<ProviderAccount>(`/api/providers/${encodeURIComponent(id)}`, { method: "PATCH", body });
}
export function replaceProviderCredential(id: string, credential: string, revision: number) {
  return apiRequest<ProviderAccount>(`/api/providers/${encodeURIComponent(id)}/credentials`, { method: "PUT", body: { credential, revision } });
}
export function validateProvider(id: string) { return apiRequest<{ status: string; checkedAt: string }>(`/api/providers/${encodeURIComponent(id)}/validate`, { method: "POST" }); }
export function deleteProvider(id: string) { return apiRequest<void>(`/api/providers/${encodeURIComponent(id)}`, { method: "DELETE" }); }
export function listPlans(organizationId: string, signal?: AbortSignal, providerAccountId?: string) {
  const query = new URLSearchParams({ organizationId });
  if (providerAccountId) query.set("providerAccountId", providerAccountId);
  return apiRequest<ProviderPlan[]>(`/api/plans?${query}`, { signal });
}
export function createPlan(body: PlanInput & { organizationId: string; providerAccountId: string }) {
  return apiRequest<ProviderPlan>("/api/plans", { method: "POST", body });
}
export function updatePlan(id: string, body: Partial<PlanInput> & { revision: number }) {
  return apiRequest<ProviderPlan>(`/api/plans/${encodeURIComponent(id)}`, { method: "PATCH", body });
}
export function deletePlan(id: string) { return apiRequest<void>(`/api/plans/${encodeURIComponent(id)}`, { method: "DELETE" }); }
