import { apiRequest } from "./apiClient";
import type { RuntimeDiagnosticBundleResponse, RuntimeLogResponse, RuntimeLogSourceResponse, RuntimeStatusResponse } from "../models/controlPlane";

export type RuntimeLogQuery = {
  source?: string;
  service?: string;
  tail?: number;
  since?: string;
};

export async function getRuntimeLogs(appId: string, query: RuntimeLogQuery = {}) {
  const search = new URLSearchParams();
  if (query.source) search.set("source", query.source);
  if (query.service) search.set("service", query.service);
  if (query.tail) search.set("tail", String(query.tail));
  if (query.since) search.set("since", query.since);
  search.set("appId", appId);
  return apiRequest<RuntimeLogResponse[]>(`/api/runtime/logs?${search}`);
}

export async function getRuntimeStatus(appId: string) {
  return apiRequest<RuntimeStatusResponse>(`/api/runtime/status?appId=${encodeURIComponent(appId)}`);
}

export async function getRuntimeLogSources(appId: string) {
  return apiRequest<RuntimeLogSourceResponse[]>(`/api/runtime/log-sources?appId=${encodeURIComponent(appId)}`);
}

export async function getRuntimeDiagnostics(appId: string, since = "30m") {
  const search = new URLSearchParams({ appId, since });
  return apiRequest<RuntimeDiagnosticBundleResponse>(`/api/runtime/diagnostics?${search}`);
}
