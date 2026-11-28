// SYNTHETIC ONLY. In-page fetch mock for browser-review harness runs.
// Every response is invented review data; unmocked paths answer an explicit
// 501 so gaps surface as visible errors rather than silent empties.

export const APP_ID = "runtime_munipages_prod";

const NOW = new Date(Date.now() - 20 * 60_000).toISOString();

export const APP = {
  id: APP_ID,
  organizationId: "org_fixture_01",
  name: "MuniPages",
  slug: "munipages",
  plan: "Production medium",
  database: "postgres",
  domains: [],
  branches: [],
  latestDeployment: { id: "dep_1", status: "succeeded", websiteUrl: "https://app.example.com", completedAt: NOW },
};

export const SERVICES = [
  {
    name: "web",
    image: "ghcr.io/example/web:1.2.3",
    port: 80,
    public: true,
    path: "/",
    healthPath: "/health",
    runtimeObservedAt: new Date(Date.now() - 20_000).toISOString(),
    runtime: { state: "running", health: "healthy", restartCount: 0, container: "web-abc123", image: "ghcr.io/example/web:1.2.3" },
    domains: [
      { id: "b1", appId: APP_ID, serviceName: "web", hostname: "app.example.com", kind: "custom_domain", pathPrefix: "/", dnsStatus: "verified", applyStatus: "applied", sslStatus: "active", status: "active", isPrimary: true },
      { id: "b2", appId: APP_ID, serviceName: "web", hostname: "web-app.example.muni.dev", kind: "selfrun_subdomain", pathPrefix: "/", dnsStatus: "verified", applyStatus: "applied", sslStatus: "pending", status: "pending", isPrimary: false },
      { id: "b3", appId: APP_ID, serviceName: "web", hostname: "old.example.com", kind: "custom_domain", pathPrefix: "/", dnsStatus: "verified", applyStatus: "applied", sslStatus: "active", status: "disabled", isPrimary: false },
    ],
  },
  {
    name: "worker",
    image: "ghcr.io/example/worker:0.9.1",
    port: 3000,
    public: false,
    path: "/",
    healthPath: "/health",
    runtimeObservedAt: new Date(Date.now() - 10 * 60_000).toISOString(),
    runtime: { state: "running", health: null, restartCount: 1, container: "worker-xyz", image: "ghcr.io/example/worker:0.9.1" },
    domains: [],
  },
];

const RUNTIME_STATUS = {
  agentInstalled: true,
  agentOnline: true,
  agentVersion: "0.4.2",
  lastSeenAt: new Date(Date.now() - 15_000).toISOString(),
  observedAt: new Date(Date.now() - 15_000).toISOString(),
  caddy: { active: true },
  services: [
    { name: "web", state: "running", health: "healthy", restartCount: 0, container: "web-abc123", image: "ghcr.io/example/web:1.2.3" },
    { name: "worker", state: "running", health: null, restartCount: 1, container: "worker-xyz", image: "ghcr.io/example/worker:0.9.1" },
  ],
};

const LOGS = Array.from({ length: 6 }, (_, i) => ({
  id: `log_${i}`,
  source: "docker-compose",
  service: "web",
  stream: i === 2 ? "stderr" : "stdout",
  observedAt: new Date(Date.now() - (6 - i) * 10_000).toISOString(),
  content: `runtime line ${i + 1}: GET /api/health 200`,
}));

function json(data: unknown, status = 200) {
  return new Response(JSON.stringify(data), { status, headers: { "content-type": "application/json" } });
}

function telemetryResponse(environmentId: string) {
  return {
    appId: environmentId,
    organizationId: "org_fixture_01",
    tenantId: "mcl_abc",
    ingestionUrl: "https://ingest.operator.test/otlp/v1/logs",
    enabled: true,
    retentionPolicy: "7d",
    provisioningStatus: "ready",
    lastAcceptedAt: null,
    createdAt: NOW,
    updatedAt: NOW,
  };
}

//  fixtures mirror the backend contract (env id == retained runtime id).
import { appsFixtureApplications } from "../app/api/applicationsFixtures";

export function installMockTransport() {
  const realFetch = window.fetch.bind(window);
  const sleepStates = new Map<string, ReturnType<typeof sleepState>>();
  function sleepState(environment: { id: string; name: string }) {
    const asleep = environment.name === "staging";
    const retained = environment.name === "production";
    return {
      policy: { sleepAfterMinutes: 120, httpTrafficIsActivity: true, revision: 1 },
      state: asleep ? "asleep" : "awake",
      stateChangedAt: NOW,
      lastActivityAt: NOW,
      lastHttpRequestAt: null,
      keepAwakeUntil: null,
      sleepsAt: asleep ? null : new Date(Date.now() + 100 * 60_000).toISOString(),
      eligible: true,
      ineligibleReason: null,
      addressMode: retained ? "retained" : "dynamic",
      retainedAddress: retained
        ? { ipv4: "203.0.113.20", state: "assigned", previousIpv4: "203.0.113.10" }
        : null,
      snapshot: asleep ? { createdAt: NOW, sizeGb: 9.4, pendingPurge: false } : null,
      operation: null as null | { id: string; kind: string; status: string; step: string | null; trigger: string },
      lastError: null,
    };
  }
  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(input instanceof Request ? input.url : String(input), "http://localhost/");
    const path = url.pathname;
    const method = (input instanceof Request ? input.method : init?.method) ?? "GET";
    if (!path.startsWith("/api/") && path !== "/me") return realFetch(input as never, init);
    if (path === "/api/apps") return json(appsFixtureApplications());
    const application = appsFixtureApplications().find(app => path === `/api/apps/${app.id}`);
    if (application) return json(application);
    const environmentRoute = path.match(/^\/api\/apps\/([^/]+)\/environments\/([^/]+)(?:\/(sleep|wake))?$/);
    if (environmentRoute) {
      const [, encodedApplicationId, encodedEnvironmentId, action] = environmentRoute;
      const applicationId = decodeURIComponent(encodedApplicationId);
      const environmentId = decodeURIComponent(encodedEnvironmentId);
      const targetApp = appsFixtureApplications().find(app => app.id === applicationId);
      const environment = targetApp?.environments.find(item => item.id === environmentId);
      if (!environment) return new Response(JSON.stringify({ error: { code: "not_found", message: "Environment not found.", details: [] } }), { status: 404, headers: { "content-type": "application/json" } });
      const current = sleepStates.get(environmentId) ?? sleepState(environment);
      if (method === "GET" && !action) return json({ ...environment, sleep: current });
      if (method === "PATCH" && !action) {
        const body = typeof init?.body === "string" ? JSON.parse(init.body) as { sleep?: { policy?: Record<string, unknown> } } : {};
        const policy = body.sleep?.policy ?? {};
        const enabled = policy.enabled === true || policy.sleepAfterMinutes != null;
        const minutes = typeof policy.sleepAfterMinutes === "number" ? policy.sleepAfterMinutes : current.policy.sleepAfterMinutes;
        const updated = {
          ...current,
          policy: {
            ...current.policy,
            sleepAfterMinutes: enabled ? minutes : null,
            httpTrafficIsActivity: typeof policy.httpTrafficIsActivity === "boolean" ? policy.httpTrafficIsActivity : current.policy.httpTrafficIsActivity,
            revision: current.policy.revision + 1,
          },
        };
        sleepStates.set(environmentId, updated);
        return json({ ...environment, sleep: updated });
      }
      if (method === "POST" && action === "sleep") {
        const updated = { ...current, state: "sleeping", stateChangedAt: new Date().toISOString(), operation: { id: `op_${environmentId}`, kind: "sleep", status: "queued", step: null, trigger: "manual_dashboard" } };
        sleepStates.set(environmentId, updated);
        return json({ operation: updated.operation }, 202);
      }
      if (method === "POST" && action === "wake") {
        if (current.state === "awake") {
          const updated = { ...current, keepAwakeUntil: new Date(Date.now() + 4 * 60 * 60_000).toISOString() };
          sleepStates.set(environmentId, updated);
          return json(updated);
        }
        const updated = { ...current, state: "waking", stateChangedAt: new Date().toISOString(), operation: { id: `op_${environmentId}`, kind: "wake", status: "queued", step: null, trigger: "manual_dashboard" } };
        sleepStates.set(environmentId, updated);
        return json({ operation: updated.operation }, 202);
      }
    }
    const environmentApiRoute = path.match(/^\/api\/environments\/([^/]+)(?:\/(telemetry|telemetry\/credentials|logs|storage))?$/);
    if (environmentApiRoute) {
      const [, environmentId, endpoint] = environmentApiRoute;
      const knownEnvironment = appsFixtureApplications().some(app => app.environments.some(environment => environment.id === environmentId));
      if (knownEnvironment) {
        if (endpoint === "telemetry") return json(telemetryResponse(environmentId));
        if (endpoint === "telemetry/credentials" || endpoint === "storage") return json([]);
        if (endpoint === "logs") return json({ state: "ok", entries: [], truncated: false, truncationReason: null, retentionCutoff: null, lastAcceptedAt: null });
        return json({ ...APP, id: environmentId });
      }
    }
    if (path === "/api/domains/services") return json(SERVICES);
    if (path === "/api/secrets/services") return json([]);
    if (path === "/api/deployments") return json([{
      id: "dep_1", appId: APP_ID, organizationId: "org_fixture_01", database: "postgres",
      status: "succeeded", websiteUrl: "https://app.example.com", consoleUrl: "https://console.example.test",
      services: [], createdAt: NOW, startedAt: NOW, completedAt: new Date(Date.now() - 15 * 60_000).toISOString(),
    }]);
    if (path === "/api/runtime/status") return json(RUNTIME_STATUS);
    if (path === "/api/runtime/log-sources") return json([{ source: "docker-compose", service: "web", count: 6 }]);
    if (path === "/api/runtime/logs") return json(LOGS);
    if (path === "/api/runtime/diagnostics") return json({ caddyErrors: [], serviceErrors: [], latestDeploymentUrl: "https://app.example.com" });
    if (path === "/api/custom-domains") return json([
      { id: "reg_1", appId: APP_ID, hostname: "example.com", status: "verified", verificationToken: "vt-1", lastCheckedAt: NOW, createdAt: NOW, updatedAt: NOW },
      { id: "reg_2", appId: APP_ID, hostname: "unverified.example.org", status: "pending", verificationToken: "vt-2", lastCheckedAt: null, createdAt: NOW, updatedAt: NOW },
    ]);
    if (path === "/api/plans") return json([]);
    if (path === "/me" || path === "/api/me") return json({
      id: "user_shot", email: "shot@example.test", displayName: "Shot",
      organizations: [{ id: "org_fixture_01", name: "Fixture Org", slug: "fixture", role: "owner" }],
    });
    console.warn("[harness] unmocked", method, path);
    return new Response(JSON.stringify({ error: { code: "harness_unmocked", message: `Unmocked: ${method} ${path}`, details: [] } }), { status: 501, headers: { "content-type": "application/json" } });
  };
}
