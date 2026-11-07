import { apiRequest } from "./apiClient";
import { runtimeConfig } from "../core/config/runtimeConfig";
import type {
  GrafanaExchangeResponse,
  GrafanaLaunchResponse,
  TelemetryCredentialIssueResponse,
  TelemetryCredentialResponse,
  TelemetryLogsResponse,
  TelemetrySettingsResponse
} from "../models/controlPlane";

export type TelemetryLogQuery = {
  start: string;
  end: string;
  service?: string;
  severity?: string;
  q?: string;
  traceId?: string;
  limit?: number;
};

export type IssueTelemetryCredentialRequest = {
  serviceName: string;
  name: string;
  expiresAt?: string | null;
};

export function getTelemetrySettings(appId: string, signal?: AbortSignal) {
  return apiRequest<TelemetrySettingsResponse>(`/v1/apps/${encodeURIComponent(appId)}/telemetry`, { signal });
}

export function updateTelemetrySettings(appId: string, enabled: boolean) {
  return apiRequest<TelemetrySettingsResponse>(`/v1/apps/${encodeURIComponent(appId)}/telemetry`, {
    method: "PUT",
    body: { enabled }
  });
}

export function listTelemetryCredentials(appId: string, signal?: AbortSignal) {
  return apiRequest<TelemetryCredentialResponse[]>(`/v1/apps/${encodeURIComponent(appId)}/telemetry/credentials`, { signal });
}

export function issueTelemetryCredential(appId: string, request: IssueTelemetryCredentialRequest) {
  return apiRequest<TelemetryCredentialIssueResponse>(`/v1/apps/${encodeURIComponent(appId)}/telemetry/credentials`, {
    method: "POST",
    body: {
      serviceName: request.serviceName,
      name: request.name,
      expiresAt: request.expiresAt ?? null
    }
  });
}

export function revokeTelemetryCredential(appId: string, credentialId: string) {
  return apiRequest<void>(`/v1/apps/${encodeURIComponent(appId)}/telemetry/credentials/${encodeURIComponent(credentialId)}`, {
    method: "DELETE"
  });
}

export function queryApplicationLogs(appId: string, query: TelemetryLogQuery, signal?: AbortSignal) {
  const search = new URLSearchParams({ start: query.start, end: query.end });
  if (query.service) search.set("service", query.service);
  if (query.severity) search.set("severity", query.severity);
  if (query.q) search.set("q", query.q);
  if (query.traceId) search.set("traceId", query.traceId);
  if (query.limit) search.set("limit", String(query.limit));
  return apiRequest<TelemetryLogsResponse>(`/v1/apps/${encodeURIComponent(appId)}/logs?${search.toString()}`, { signal });
}

export function issueTelemetryReadToken(appId: string, signal?: AbortSignal) {
  return apiRequest<{ readToken: string; expiresAt: string; queryUrl: string }>(
    `/v1/apps/${encodeURIComponent(appId)}/telemetry/read-token`,
    {
      method: "POST",
      signal
    }
  );
}

export async function queryApplicationLogsDirect(
  queryUrl: string,
  readToken: string,
  query: TelemetryLogQuery,
  signal?: AbortSignal
): Promise<TelemetryLogsResponse> {
  const search = new URLSearchParams({ start: query.start, end: query.end });
  if (query.service) search.set("service", query.service);
  if (query.severity) search.set("severity", query.severity);
  if (query.q) search.set("q", query.q);
  if (query.traceId) search.set("traceId", query.traceId);
  if (query.limit) search.set("limit", String(query.limit));

  const response = await fetch(`${queryUrl}?${search.toString()}`, {
    method: "GET",
    headers: {
      Authorization: `Bearer ${readToken}`
    },
    signal
  });

  const payload = await response.json().catch(() => undefined);
  if (!response.ok) {
    const message =
      payload?.error?.message && typeof payload.error.message === "string"
        ? payload.error.message
        : "Failed to query logs.";
    throw new Error(message);
  }

  return payload as TelemetryLogsResponse;
}

export function launchGrafanaSession(appId: string, destination = "/explore") {
  return apiRequest<GrafanaLaunchResponse>(`/v1/apps/${encodeURIComponent(appId)}/grafana-session`, {
    method: "POST",
    body: { destination }
  });
}

/**
 * Exchanges a single-use launch code for the Grafana proxy session cookie.
 * Uses credentials:include so the host-only session cookie is stored; the
 * launch code itself is the credential, so no Authorization header is sent.
 */
export async function exchangeGrafanaLaunch(
  launchCode: string,
  exchangeUrl?: string | null
): Promise<GrafanaExchangeResponse> {
  const targetUrl = exchangeUrl || `${runtimeConfig.apiBaseUrl}/v1/grafana/exchange`;
  const response = await fetch(targetUrl, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ launchCode })
  });

  const payload = await response.json().catch(() => undefined);
  if (!response.ok) {
    const message =
      payload?.error?.message && typeof payload.error.message === "string"
        ? payload.error.message
        : "Unable to open Grafana.";
    throw new Error(message);
  }

  return payload as GrafanaExchangeResponse;
}

export function buildGrafanaUrl(destination: string, grafanaBaseUrl?: string | null) {
  const normalized = destination.startsWith("/") ? destination : `/${destination}`;
  if (grafanaBaseUrl) {
    return `${grafanaBaseUrl.replace(/\/$/, "")}${normalized}`;
  }
  return `${runtimeConfig.apiBaseUrl}/grafana${normalized}`;
}
