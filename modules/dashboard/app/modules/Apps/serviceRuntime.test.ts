import { describe, expect, it } from "vitest";
import type { AppServiceInventoryResponse } from "../../models/controlPlane";
import { runtimeFresh, runtimeLabel } from "./serviceRuntime";

const now = Date.parse("2026-09-14T14:00:00Z");
const service: AppServiceInventoryResponse = {
  name: "worker", image: "worker:v1", port: 8080, public: false, path: "/", healthPath: "/health", domains: [],
  runtimeObservedAt: new Date(now).toISOString(), runtime: { name: "worker", state: "running" }
};
describe("service runtime", () => {
  it("shows private workers without a health check as running", () => expect(runtimeLabel(service, now)).toBe("Running"));
  it("distinguishes missing telemetry", () => expect(runtimeLabel({ ...service, runtime: null }, now)).toBe("Telemetry unavailable"));
  it("does not present old observations as current", () => {
    expect(runtimeFresh(service, now + 120_001)).toBe(false);
    expect(runtimeLabel(service, now + 120_001)).toBe("Stale telemetry");
  });
  it("shows health independently of process state", () => expect(runtimeLabel({ ...service, runtime: { name: "worker", state: "running", health: "unhealthy" } }, now)).toBe("Running / unhealthy"));
});
