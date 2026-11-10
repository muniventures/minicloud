import { afterEach, expect, it, vi } from "vitest";
import { createAppCustomDomain } from "./environmentsApi";

vi.mock("../core/auth/firebase", () => ({
  getCurrentIdToken: vi.fn().mockResolvedValue("firebase-token")
}));

afterEach(() => vi.unstubAllGlobals());

it.each(["staging.api.example.test", "example.com"])("sends %s as a JSON object through the shared API client", async hostname => {
  const domain = { id: "dom_1", hostname, status: "pending_dns" };
  const request = vi.fn().mockResolvedValue(new Response(JSON.stringify(domain), {
    status: 201,
    headers: { "Content-Type": "application/json" }
  }));
  vi.stubGlobal("fetch", request);

  expect(await createAppCustomDomain("app_1", "backend", hostname)).toEqual(domain);
  expect(request).toHaveBeenCalledWith(expect.stringContaining("/api/domains?appId=app_1"), expect.objectContaining({
    method: "POST",
    headers: expect.objectContaining({ "Content-Type": "application/json", Authorization: "Bearer firebase-token" })
  }));
  const [, options] = request.mock.calls[0];
  expect(JSON.parse(options.body)).toEqual({ serviceName: "backend", hostname });
});

it("registers a domain for an app independently of services", async () => {
  const { registerCustomDomain } = await import("./customDomainsApi");
  const request = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: "reg", status: "pending" }), {
    status: 200, headers: { "Content-Type": "application/json" }
  }));
  vi.stubGlobal("fetch", request);
  await registerCustomDomain("app_1", "example.com");
  const [url, options] = request.mock.calls[0];
  expect(url).toContain("/api/custom-domains?appId=app_1");
  expect(JSON.parse(options.body)).toEqual({ hostname: "example.com" });
});

it("uses the environment descriptor route without an application alias", async () => {
  const { getEnvironmentDetail } = await import("./environmentsApi");
  const request = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: "env 1" }), {
    status: 200, headers: { "Content-Type": "application/json" }
  }));
  vi.stubGlobal("fetch", request);
  await getEnvironmentDetail("env 1");
  expect(request).toHaveBeenCalledTimes(1);
  expect(request.mock.calls[0][0]).toContain("/api/environments/env%201");
});
