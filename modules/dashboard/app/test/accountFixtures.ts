import type { ContextType } from "react";
import type { AuthContext } from "../core/auth/AuthProvider";
import type { BrowserFlow } from "../api/accountApi";
import { vi } from "vitest";

export const accountCapabilities = { password: true, google: false, emailRecovery: false, bootstrapState: "ready" as const };
export const accountSession = {
  userId: "user-1", csrfToken: "synthetic-csrf",
  expiresAt: "2099-01-01T00:00:00Z", idleExpiresAt: "2099-01-01T00:00:00Z"
};
export const accountProfile = {
  id: "user-1", email: "operator@example.test", displayName: "Operator",
  organizations: [{ id: "org-1", name: "Example", slug: "example", role: "owner" }]
};
export function authFixture(overrides: Partial<ContextType<typeof AuthContext>> = {}): ContextType<typeof AuthContext> {
  return {
    status: "authenticated", capabilities: accountCapabilities, profile: accountProfile,
    selectedOrganization: accountProfile.organizations[0], selectOrganization: vi.fn().mockReturnValue(false), profileError: null,
    refreshProfile: vi.fn().mockResolvedValue(undefined), refreshSession: vi.fn().mockResolvedValue(undefined),
    logout: vi.fn().mockResolvedValue(undefined), ...overrides
  };
}
export function browserFlow(kind: BrowserFlow["kind"] = "login"): BrowserFlow {
  const id = "11111111-2222-4333-8444-555555555555";
  return {
    id, kind, expiresAt: "2099-01-01T00:00:00Z",
    ui: {
      action: `/api/account/flows/${kind}?flow=${id}`, method: "POST", messages: [],
      nodes: [
        { group: "default", type: "hidden", name: "csrf_token", value: "upstream-csrf", required: true, messages: [] },
        { group: "password", type: "text", name: "identifier", value: null, required: true, messages: [] },
        { group: "password", type: "password", name: "password", value: null, required: true, messages: [] },
        { group: "password", type: "submit", name: "method", value: "password", required: false, messages: [] }
      ]
    }
  };
}
export const jsonResponse = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
export function googleFlow(kind: "login" | "settings" = "login"): BrowserFlow {
  const flow = browserFlow(kind);
  return { ...flow, ui: { ...flow.ui, providers: [{ id: "google", action: `/api/account/flows/${kind}/oidc?flow=${flow.id}`, method: "POST" }] } };
}
export const googleUrl = "https://accounts.google.com/o/oauth2/v2/auth?client_id=synthetic&response_type=code&state=synthetic-state";
