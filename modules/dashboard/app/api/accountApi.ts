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
export type BrowserFlow = {
  id: string;
  kind: FlowKind;
  expiresAt: string;
  ui: { action: string; method: string; nodes: FlowNode[]; messages: FlowMessage[] };
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
        ["/sign-in", "/sign-up", "/recovery"].includes(url.pathname)) return "/apps";
    return `${url.pathname}${url.search}${url.hash}`;
  } catch {
    return "/apps";
  }
}

function flowPath(kind: FlowKind) {
  if (!["login", "settings", "recovery"].includes(kind)) throw new Error("Unsupported account flow.");
  return `/api/account/flows/${kind}`;
}

function flowId(id: string) {
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)) {
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
  return flow;
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
