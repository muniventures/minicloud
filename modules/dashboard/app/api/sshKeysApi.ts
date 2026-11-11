import { apiRequest } from "./apiClient";
import type { ProviderSshKey } from "../models/application";

// Provider-account SSH key inventory. Failures are errors (403 denied,
// 503 ssh_inventory_unavailable); an unavailable inventory is never [].
export function listProviderSshKeys(organizationId: string, providerAccountId: string, signal?: AbortSignal) {
  return apiRequest<ProviderSshKey[]>(
    `/api/providers/${encodeURIComponent(providerAccountId)}/ssh-keys?${new URLSearchParams({ organizationId })}`,
    { signal },
  );
}
