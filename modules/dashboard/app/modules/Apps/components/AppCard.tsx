import { Card, CardContent } from "@/components/ui/card";
import { StatusBadge } from "@/core/components/StatusBadge";
import { ServiceUrls } from "@/core/components/ServiceUrls";
import { Disclosure } from "@/core/components/Disclosure";
import type { EnvironmentDetailResponse } from "@/models/controlPlane";
import { isNonInteractiveNavigationClick } from "@/core/components/NavigableTableRow";
import { Link } from "react-router-dom";
export function AppCard({
  app,
  onOpen,
  compact = false,
}: {
  app: EnvironmentDetailResponse;
  compact?: boolean;
  onOpen: (id: string) => void;
}) {
  return (
    <Card
      className={compact ? "cursor-pointer hover:bg-muted/30" : undefined}
      onClick={compact ? (event) => {
        if (isNonInteractiveNavigationClick(event)) onOpen(app.id);
      } : undefined}
    >
      <CardContent
        className={
          compact
            ? "grid items-center gap-3 p-4 sm:grid-cols-2 lg:grid-cols-4"
            : "space-y-3 p-4"
        }
      >
        <div className="flex flex-wrap items-center justify-between gap-2">
          <Link
            className="min-w-0 whitespace-normal text-left text-base font-medium text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            to={`/apps/${app.id}`}
          >
            {app.name}
          </Link>
          <span className="text-xs text-muted-foreground">
            {app.plan} · {app.database}
          </span>
        </div>
        <p className="text-xs text-muted-foreground break-all">
          {app.slug}
          {app.branchName ? ` · ${app.branchName}` : ""}
        </p>
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs">Latest deployment</span>
          <StatusBadge
            status={app.latestDeployment?.status ?? "No deployments"}
          />
        </div>
        <p className="text-xs text-muted-foreground">
          {app.latestDeployment?.completedAt
            ? new Date(app.latestDeployment.completedAt).toLocaleString()
            : app.latestDeployment
              ? "Completion time unavailable"
              : "No deployments yet"}
        </p>
        <p className="text-xs text-muted-foreground">Runtime unavailable</p>
        {app.deletionStatus ? (
          <p className="text-destructive">
            {app.deletionStatus === "failed"
              ? "Deletion failed — open to retry"
              : "Deleting application…"}
          </p>
        ) : null}
        <Disclosure label={`${app.domains.length} endpoints`}>
          <ServiceUrls
            services={[{ urls: app.domains.map((domain) => domain.url) }]}
          />
        </Disclosure>
      </CardContent>
    </Card>
  );
}
