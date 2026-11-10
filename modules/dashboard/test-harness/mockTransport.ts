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
      { id: "b2", appId: APP_ID, serviceName: "web", hostname: "web-app.example.muni.dev", kind: "minicloud_subdomain", pathPrefix: "/", dnsStatus: "verified", applyStatus: "applied", sslStatus: "pending", status: "pending", isPrimary: false },
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

function json(data: unknown) {
  return new Response(JSON.stringify(data), { headers: { "content-type": "application/json" } });
}

//  fixtures mirror the backend contract (env id == retained runtime id).
import { appsFixtureApplications } from "../app/api/applicationsFixtures";

export function installMockTransport() {
  const realFetch = window.fetch.bind(window);
  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(input instanceof Request ? input.url : String(input), "http://localhost/");
    const path = url.pathname;
    const method = (input instanceof Request ? input.method : init?.method) ?? "GET";
    if (!path.startsWith("/api/") && path !== "/me") return realFetch(input as never, init);
    if (path === "/api/apps") return json(appsFixtureApplications());
    const application = appsFixtureApplications().find(app => path === `/api/apps/${app.id}`);
    if (application) return json(application);
    if (path === `/api/environments/${APP_ID}`) return json(APP);
    if (path === "/api/domains/services") return json(SERVICES);
    if (path === "/api/secrets/services") return json([]);
    if (path === "/api/deployments") return json([{
      id: "dep_1", appId: APP_ID, organizationId: "org_fixture_01", database: "postgres",
      status: "succeeded", websiteUrl: "https://app.example.com", consoleUrl: "https://console.example.test",
      services: [], createdAt: NOW, startedAt: NOW, completedAt: new Date(Date.now() - 15 * 60_000).toISOString(),
    }]);
    if (path === `/api/environments/${APP_ID}/telemetry` && method === "PUT") {
      return json({
        appId: APP_ID, organizationId: "org_fixture_01", tenantId: "mcl_abc",
        ingestionUrl: "https://ingest.operator.test/otlp/v1/logs", enabled: true,
        retentionPolicy: "7d", provisioningStatus: "ready",
        lastAcceptedAt: null, createdAt: NOW, updatedAt: NOW,
      });
    }
    if (path === `/api/environments/${APP_ID}/telemetry`) return json({
      appId: APP_ID, organizationId: "org_fixture_01", tenantId: "mcl_abc",
      ingestionUrl: "https://ingest.operator.test/otlp/v1/logs", enabled: true,
      retentionPolicy: "7d", provisioningStatus: "ready",
      lastAcceptedAt: null, createdAt: NOW, updatedAt: NOW,
    });
    if (path === `/api/environments/${APP_ID}/logs`) return json({ state: "ok", entries: [], truncated: false, truncationReason: null, retentionCutoff: null, lastAcceptedAt: null });
    if (path === "/api/runtime/status") return json(RUNTIME_STATUS);
    if (path === "/api/runtime/log-sources") return json([{ source: "docker-compose", service: "web", count: 6 }]);
    if (path === "/api/runtime/logs") return json(LOGS);
    if (path === "/api/runtime/diagnostics") return json({ caddyErrors: [], serviceErrors: [], latestDeploymentUrl: "https://app.example.com" });
    if (path === "/api/custom-domains") return json([
      { id: "reg_1", appId: APP_ID, hostname: "example.com", status: "verified", verificationToken: "vt-1", lastCheckedAt: NOW, createdAt: NOW, updatedAt: NOW },
      { id: "reg_2", appId: APP_ID, hostname: "unverified.example.org", status: "pending", verificationToken: "vt-2", lastCheckedAt: null, createdAt: NOW, updatedAt: NOW },
    ]);
    if (path === `/api/environments/${APP_ID}/storage`) return json([]);
    if (path === `/api/environments/${APP_ID}/telemetry/credentials`) return json([]);
    if (path === "/api/plans") return json([]);
    if (path === "/me" || path === "/api/me") return json({
      id: "user_shot", email: "shot@example.test", displayName: "Shot",
      organizations: [{ id: "org_fixture_01", name: "Fixture Org", slug: "fixture", role: "owner" }],
    });
    console.warn("[harness] unmocked", method, path);
    return new Response(JSON.stringify({ error: { code: "harness_unmocked", message: `Unmocked: ${method} ${path}`, details: [] } }), { status: 501, headers: { "content-type": "application/json" } });
  };
}
