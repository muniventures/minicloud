import { apiRequest } from "./apiClient";
import type { MeResponse } from "../models/controlPlane";

export function getMe(signal?: AbortSignal) {
  return apiRequest<MeResponse>("/api/me", { signal });
}
