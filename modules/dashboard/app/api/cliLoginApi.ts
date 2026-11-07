import { apiRequest } from "./apiClient";
import type { CliLoginSessionResponse } from "../models/controlPlane";

export function getCliLoginSession(sessionId: string) {
  return apiRequest<CliLoginSessionResponse>(`/v1/cli-login-sessions/${encodeURIComponent(sessionId)}`);
}

export function approveCliLoginSession(sessionId: string, organizationId: string) {
  return apiRequest<CliLoginSessionResponse>(`/v1/cli-login-sessions/${encodeURIComponent(sessionId)}/approve`, {
    method: "POST",
    body: { organizationId }
  });
}
