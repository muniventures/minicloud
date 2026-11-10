import { apiRequest } from "./apiClient";
import type { ApiKeyCreateResponse, ApiKeyResponse, ApiKeyScope } from "../models/controlPlane";

export type CreateApiKeyRequest = {
  organizationId: string;
  name: string;
  scopes: ApiKeyScope[];
  expiresAt: string | null;
};

export function listApiKeys(organizationId: string, signal?: AbortSignal) {
  return apiRequest<ApiKeyResponse[]>(`/api/api-keys?organizationId=${encodeURIComponent(organizationId)}`, { signal });
}

export function createApiKey(request: CreateApiKeyRequest) {
  return apiRequest<ApiKeyCreateResponse>("/api/api-keys", {
    method: "POST",
    body: request
  });
}

export function revokeApiKey(apiKeyId: string) {
  return apiRequest<void>(`/api/api-keys/${encodeURIComponent(apiKeyId)}`, {
    method: "DELETE"
  });
}
