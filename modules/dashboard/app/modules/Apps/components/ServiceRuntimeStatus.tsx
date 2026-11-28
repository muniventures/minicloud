import type { AppServiceInventoryResponse } from "../../../models/controlPlane";
import { runtimeLabel } from "../serviceRuntime";

export function ServiceRuntimeStatus({ service, now, unavailable = false }: { service: AppServiceInventoryResponse; now: number; unavailable?: boolean }) {
  if (unavailable) return <div className="text-sm text-muted-foreground">Unavailable while asleep</div>;
  return <div className="text-sm">
    <span>{runtimeLabel(service, now)}</span>
    {service.runtimeObservedAt ? <div className="text-xs text-muted-foreground">Last observed {new Date(service.runtimeObservedAt).toLocaleString()}</div> : null}
  </div>;
}
