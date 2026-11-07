import { getCurrentIdToken } from "../core/auth/firebase";
import { runtimeConfig } from "../core/config/runtimeConfig";
import { ApiError, type ApiErrorBody } from "../models/controlPlane";

type RequestOptions = {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  body?: unknown;
  signal?: AbortSignal;
};

export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const token = await getCurrentIdToken();
  if (!token) {
    throw new ApiError(401, { code: "unauthenticated", message: "Sign in is required.", details: [] });
  }

  const response = await fetch(`${runtimeConfig.apiBaseUrl}${path}`, {
    method: options.method ?? "GET",
    signal: options.signal,
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json"
    },
    body: options.body === undefined ? undefined : JSON.stringify(options.body)
  });

  if (response.status === 204) {
    return undefined as T;
  }

  const contentType = response.headers.get("content-type") ?? "";
  const payload = contentType.includes("application/json") ? await response.json() : undefined;

  if (!response.ok) {
    const error = normalizeError(response.status, payload);
    throw new ApiError(response.status, error);
  }

  return payload as T;
}

function normalizeError(status: number, payload: unknown): ApiErrorBody {
  if (
    payload &&
    typeof payload === "object" &&
    "error" in payload &&
    payload.error &&
    typeof payload.error === "object" &&
    "code" in payload.error &&
    "message" in payload.error
  ) {
    const error = payload.error as { code: string; message: string; details?: unknown };
    return {
      code: error.code,
      message: error.message,
      details: Array.isArray(error.details) ? (error.details as ApiErrorBody["details"]) : []
    };
  }

  return {
    code: `http_${status}`,
    message: "The request failed.",
    details: []
  };
}
