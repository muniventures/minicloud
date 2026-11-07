import type { AppServiceInventoryResponse } from "../../../models/controlPlane";
import { runtimeLabel } from "../serviceRuntime";

export function ServiceRuntimeStatus({ service, now }: { service: AppServiceInventoryResponse; now: number }) {
  return <div className="text-sm">
    <span>{runtimeLabel(service, now)}</span>
    {service.runtimeObservedAt ? <div className="text-xs text-muted-foreground">Last observed {new Date(service.runtimeObservedAt).toLocaleString()}</div> : null}
  </div>;
}
