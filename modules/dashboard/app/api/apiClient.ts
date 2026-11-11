import { runtimeConfig } from "../core/config/runtimeConfig";
import { ApiError, type ApiErrorBody, type ApiErrorDetail } from "../models/controlPlane";

type RequestOptions = {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  body?: unknown;
  signal?: AbortSignal;
  browserFlow?: boolean;
  notifyAuth?: boolean;
};

let csrfToken: string | null = null;
const authListeners = new Set<(status: 401 | 503) => void>();

export function setCsrfToken(token: string | null) {
  csrfToken = token;
}

export function subscribeAuthFailures(listener: (status: 401 | 503) => void) {
  authListeners.add(listener);
  return () => { authListeners.delete(listener); };
}

export function apiUrl(path: string) {
  const origin = new URL(runtimeConfig.apiBaseUrl);
  if (!["http:", "https:"].includes(origin.protocol) || origin.username || origin.password ||
      origin.origin !== runtimeConfig.apiBaseUrl || !path.startsWith("/api/") ||
      /[\\\s#]/.test(path)) {
    throw new Error("Invalid API destination.");
  }
  const target = new URL(path, origin);
  if (target.origin !== origin.origin || !target.pathname.startsWith("/api/")) {
    throw new Error("Invalid API destination.");
  }
  return target.href;
}

export function accountErrorMessage(error: unknown) {
  if (error instanceof ApiError) {
    switch (error.code) {
      case "auth_flow_expired": return "This form expired. Restart to try again.";
      case "auth_flow_invalid": return "Check your details and try again.";
      case "unauthenticated": return "Sign in was rejected or your session expired.";
      case "csrf_invalid": return "This form is no longer valid. Restart to try again.";
      case "forbidden": return "You do not have permission to perform this action.";
    }
  }
  return "Account service is unavailable. Retry when it is available.";
}

async function request(path: string, options: RequestOptions): Promise<Response> {
  const url = apiUrl(path);
  const method = options.method ?? "GET";
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (method !== "GET") {
    if (csrfToken) headers["X-Minicloud-CSRF"] = csrfToken;
    else if (!options.browserFlow) {
      throw new ApiError(401, { code: "unauthenticated", message: "Refresh your session before trying again.", details: [] });
    }
  }
  const response = await fetch(url, {
    method,
    signal: options.signal,
    credentials: "include",
    redirect: "error",
    cache: "no-store",
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body)
  });
  return response;
}

export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const response = await request(path, options);
  if (response.status === 204) return undefined as T;
  const payload = (response.headers.get("content-type") ?? "").includes("application/json")
    ? await response.json() : undefined;
  if (!response.ok) throw requestError(response.status, payload, options);
  return payload as T;
}

function requestError(status: number, payload: unknown, options: RequestOptions) {
  const error = normalizeError(status, payload);
  if (options.notifyAuth !== false && (status === 401 || status === 503 && error.code === "auth_unavailable")) {
    if (status === 401) setCsrfToken(null);
    authListeners.forEach(listener => listener(status));
  }
  return new ApiError(status, error);
}

function isErrorDetail(value: unknown): value is ApiErrorDetail {
  return Boolean(value && typeof value === "object" && "field" in value && typeof value.field === "string" &&
    "message" in value && typeof value.message === "string");
}

function normalizeError(status: number, payload: unknown): ApiErrorBody {
  if (payload && typeof payload === "object" && "error" in payload &&
      payload.error && typeof payload.error === "object" &&
      "code" in payload.error && typeof payload.error.code === "string" &&
      "message" in payload.error && typeof payload.error.message === "string") {
    return {
      code: payload.error.code,
      message: payload.error.message,
      details: "details" in payload.error && Array.isArray(payload.error.details)
        ? payload.error.details.filter(isErrorDetail) : []
    };
  }
  return { code: `http_${status}`, message: "The request failed.", details: [] };
}

export async function apiDownload(path: string, signal?: AbortSignal): Promise<Blob> {
  const response = await request(path, { signal });
  if (!response.ok) {
    const payload = (response.headers.get("content-type") ?? "").includes("application/json")
      ? await response.json() : undefined;
    throw requestError(response.status, payload, {});
  }
  return response.blob();
}
