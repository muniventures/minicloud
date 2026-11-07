import type { AppServiceInventoryResponse } from "../../models/controlPlane";

export function runtimeFresh(service: AppServiceInventoryResponse, now = Date.now()) {
  const observed = service.runtimeObservedAt ? Date.parse(service.runtimeObservedAt) : NaN;
  return Number.isFinite(observed) && now - observed <= 120_000 && observed <= now + 30_000;
}

export function runtimeLabel(service: AppServiceInventoryResponse, now = Date.now()) {
  if (!service.runtime) return "Telemetry unavailable";
  if (!runtimeFresh(service, now)) return "Stale telemetry";
  const state = service.runtime.state;
  const label = state.charAt(0).toUpperCase() + state.slice(1);
  return service.runtime.health ? `${label} / ${service.runtime.health}` : label;
}
