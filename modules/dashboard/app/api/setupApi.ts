import { apiRequest, apiUrl } from "./apiClient";
import { ApiError } from "../models/controlPlane";

export type SetupStatus = {
  state: "required" | "in_progress" | "ready" | "blocked";
  expiresAt?: string | null;
};

export type CompleteSetupRequest = {
  setupToken: string;
  email: string;
  password: string;
  displayName: string;
  organizationName: string;
  organizationSlug: string;
};

export type BootstrapResult = {
  installationId: string;
  operationId: string;
  state: string;
  userId?: string | null;
  organizationId?: string | null;
  completedAt?: string | null;
};

export async function getSetupStatus(signal?: AbortSignal) {
  const status = await apiRequest<SetupStatus>("/api/account/setup", { signal, notifyAuth: false });
  if (!["required", "in_progress", "ready", "blocked"].includes(status.state)) {
    throw new Error("Invalid setup state.");
  }
  return status;
}

export function completeSetup(request: CompleteSetupRequest) {
  if (new URL(apiUrl("/api/account/setup")).protocol !== "https:" || window.location.protocol !== "https:") {
    throw new Error("Setup requires HTTPS.");
  }
  return apiRequest<BootstrapResult>("/api/account/setup", {
    method: "POST", body: request, browserFlow: true, notifyAuth: false
  });
}

export function setupErrorMessage(error: unknown) {
  if (error instanceof ApiError) {
    switch (error.code) {
      case "setup_token_invalid": return "The setup token is invalid. Check the protected token from your host administrator.";
      case "setup_token_expired": return "The setup token expired. Ask the host administrator to rotate it using the host bootstrap command.";
      case "setup_already_completed": return "Setup is already complete. Sign in with the operator account.";
      case "setup_in_progress": return "Setup is awaiting identity reconciliation. Ask the host administrator to check the original operation before retrying.";
      case "setup_state_conflict": return "Check your operator and workspace details. If setup is blocked, the host administrator must reconcile it.";
      case "setup_rate_limited": return "Too many setup attempts. Wait a minute before trying again.";
      case "csrf_invalid": return "Setup origin was rejected. Open the configured HTTPS dashboard.";
      case "setup_https_required": return "Setup requires the configured HTTPS dashboard and API.";
      case "setup_request_invalid": return "The setup form is invalid or too large. Check your details.";
    }
  }
  return "Setup is temporarily unavailable. Check status before explicitly trying again; do not assume the previous attempt failed.";
}
