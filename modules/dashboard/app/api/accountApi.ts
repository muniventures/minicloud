import { apiRequest, apiUrl, setCsrfToken } from "./apiClient";

export type AccountCapabilities = {
  password: boolean;
  google: boolean;
  emailRecovery: boolean;
  bootstrapState: "required" | "ready" | "blocked";
};

export type AccountSession = {
  userId: string;
  expiresAt: string;
  idleExpiresAt: string;
  csrfToken: string;
};

export type FlowKind = "login" | "settings" | "recovery";
export type FlowMessage = { id: number; text: string; type: string };
export type FlowNode = {
  group: string;
  type: string;
  name: string;
  value: string | null;
  required: boolean;
  messages: FlowMessage[];
};
export type FlowProvider = { id: string; action: string; method: string };
export type BrowserFlow = {
  id: string;
  kind: FlowKind;
  expiresAt: string;
  ui: { action: string; method: string; nodes: FlowNode[]; messages: FlowMessage[]; providers?: FlowProvider[] };
};
export type ProviderContinuation = {
  result: "success" | "failed" | "forbidden" | "unavailable";
  kind: "login" | "settings";
  flowId: string | null;
  returnTo: string;
};
export type FlowResponse = { flow: BrowserFlow | null; session: AccountSession | null };

export const getAccountCapabilities = () =>
  apiRequest<AccountCapabilities>("/api/account/capabilities", { notifyAuth: false });

export async function getAccountSession() {
  return apiRequest<AccountSession>("/api/account/session", { notifyAuth: false });
}

export const logoutAccount = () =>
  apiRequest<void>("/api/account/logout", { method: "POST", notifyAuth: false });

export function safeReturnPath(value: unknown): string {
  if (typeof value !== "string" || !value.startsWith("/") || value.startsWith("//") ||
      /[\\\u0000-\u0020\u007f]/.test(value)) return "/apps";
  try {
    const decoded = decodeURIComponent(value);
    if (decoded.startsWith("//") || /[\\\u0000-\u0020\u007f]/.test(decoded)) return "/apps";
    const url = new URL(value, window.location.origin);
    if (url.origin !== window.location.origin ||
        ["/sign-in", "/sign-in/continue", "/sign-up", "/recovery"].includes(url.pathname)) return "/apps";
    return `${url.pathname}${url.search}${url.hash}`;
  } catch {
    return "/apps";
  }
}

function flowPath(kind: FlowKind) {
  if (!["login", "settings", "recovery"].includes(kind)) throw new Error("Unsupported account flow.");
  return `/api/account/flows/${kind}`;
}

const flowIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function flowId(id: string) {
  if (!flowIdPattern.test(id)) {
    throw new Error("Invalid account form.");
  }
  return id;
}

export function validateFlow(flow: BrowserFlow, kind: FlowKind) {
  const expected = apiUrl(`${flowPath(kind)}?flow=${flowId(flow.id)}`);
  const action = new URL(flow.ui.action, apiUrl(flowPath(kind)));
  if (flow.kind !== kind || flow.ui.method.toUpperCase() !== "POST" || action.href !== expected) {
    throw new Error("Invalid account form destination.");
  }
  for (const provider of flow.ui.providers ?? []) {
    const providerAction = new URL(provider.action, apiUrl(flowPath(kind)));
    if (kind === "recovery" || provider.id !== "google" || provider.method.toUpperCase() !== "POST" ||
        providerAction.href !== apiUrl(`${flowPath(kind)}/oidc?flow=${flow.id}`)) {
      throw new Error("Invalid account provider destination.");
    }
  }
  return flow;
}

/** Only HTTPS Google authorization URLs are navigable; the API validates the same allowlist first. */
export function validateProviderUrl(value: unknown) {
  if (typeof value !== "string") throw new Error("Invalid account provider destination.");
  const url = new URL(value);
  if (url.protocol !== "https:" || url.hostname !== "accounts.google.com" || url.port || url.username ||
      url.password || url.hash || !url.pathname.startsWith("/o/oauth2/")) {
    throw new Error("Invalid account provider destination.");
  }
  return url.href;
}

export const browserNavigation = { assign: (url: string) => window.location.assign(url) };

export async function startProviderRedirect(flow: BrowserFlow, provider: "google", returnTo: string) {
  validateFlow(flow, flow.kind);
  if (!flow.ui.providers?.some(item => item.id === provider)) throw new Error("Invalid account provider destination.");
  const csrf = flow.ui.nodes.find(node => node.name === "csrf_token" && node.type === "hidden")?.value;
  const result = await apiRequest<{ provider: string; redirectUrl: string }>(
    `${flowPath(flow.kind)}/oidc?flow=${flowId(flow.id)}&returnTo=${encodeURIComponent(safeReturnPath(returnTo))}`, {
      method: "POST",
      body: { csrf_token: csrf ?? "", provider },
      browserFlow: flow.kind !== "settings",
      notifyAuth: false
    });
  if (result.provider !== provider) throw new Error("Invalid account provider destination.");
  return validateProviderUrl(result.redirectUrl);
}

export function parseProviderContinuation(search: URLSearchParams): ProviderContinuation {
  const result = search.get("result");
  const kind = search.get("kind") === "settings" ? "settings" : "login";
  const flow = search.get("flow");
  return {
    result: result === "success" || result === "forbidden" || result === "unavailable" ? result : "failed",
    kind,
    flowId: flow && flowIdPattern.test(flow) ? flow : null,
    returnTo: safeReturnPath(search.get("returnTo") ?? (kind === "settings" ? "/settings/account" : "/apps"))
  };
}

async function readEnvelope(response: Promise<FlowResponse>, kind: FlowKind) {
  const result = await response;
  if (Boolean(result.flow) === Boolean(result.session)) throw new Error("Invalid account response.");
  if (result.flow) validateFlow(result.flow, kind);
  if (result.session) setCsrfToken(result.session.csrfToken);
  return result;
}

export function startAccountFlow(kind: FlowKind, returnTo: string) {
  return readEnvelope(apiRequest<FlowResponse>(
    `${flowPath(kind)}/browser?returnTo=${encodeURIComponent(safeReturnPath(returnTo))}`,
    { notifyAuth: false }
  ), kind);
}

export function getAccountFlow(kind: FlowKind, id: string) {
  return readEnvelope(apiRequest<FlowResponse>(
    `${flowPath(kind)}?id=${flowId(id)}`, { notifyAuth: false }
  ), kind);
}

export function submitAccountFlow(flow: BrowserFlow, fields: Record<string, string>) {
  validateFlow(flow, flow.kind);
  return readEnvelope(apiRequest<FlowResponse>(`${flowPath(flow.kind)}?flow=${flowId(flow.id)}`, {
    method: "POST",
    body: fields,
    browserFlow: flow.kind !== "settings",
    notifyAuth: false
  }), flow.kind);
}
