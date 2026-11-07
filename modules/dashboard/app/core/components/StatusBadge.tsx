import { Badge } from "@/components/ui/badge";
import type { DeploymentStatus } from "@/models/controlPlane";

export function StatusBadge({ status }: { status?: DeploymentStatus | string | null }) {
  const normalized = status ?? "unknown";
  const { badgeClass, dotClass, isPulsing } = getStatusStyles(normalized);

  return (
    <Badge className={`inline-flex items-center gap-1.5 font-medium ${badgeClass}`} variant="outline">
      <span aria-hidden="true" className={`h-1.5 w-1.5 rounded-full border border-current ${isPulsing ? "animate-pulse" : ""}`} />
      {normalized === "waiting_for_build" ? "Waiting for build" : normalized.replaceAll("_", " ")}
    </Badge>
  );
}

function getStatusStyles(status: string) {
  switch (status) {
    case "succeeded":
    case "active":
      return {
        badgeClass: "border-border bg-success-surface text-success",
        dotClass: "bg-success-surface",
        isPulsing: false,
      };
    case "failed":
    case "canceled":
      return {
        badgeClass: "border-border bg-error-surface text-destructive",
        dotClass: "bg-error-surface",
        isPulsing: false,
      };
    case "waiting_for_build":
    case "pending_dns":
    case "queued":
      return {
        badgeClass: "border-border bg-warning-surface text-warning",
        dotClass: "bg-warning-surface",
        isPulsing: true,
      };
    case "canceling":
    case "dispatching":
    case "provisioning":
    case "deploying":
    case "verifying":
    case "pending_apply":
    case "ssl_pending":
      return {
        badgeClass: "border-border bg-info-surface text-info",
        dotClass: "bg-info-surface",
        isPulsing: true,
      };
    default:
      return {
        badgeClass: "border-border bg-muted text-muted-foreground",
        dotClass: "bg-muted-foreground",
        isPulsing: false,
      };
  }
}
