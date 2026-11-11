import { listApplications } from "@/api/applicationsApi";
import { CopyButton } from "@/core/components/CopyButton";
import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import {
  compareRecords,
  deploymentStatuses,
  options,
  useCollectionQuery,
} from "@/core/components/collectionQuery";
import { ServiceUrls } from "@/core/components/ServiceUrls";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { RotateCw } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { refreshDeployment } from "../../api/deploymentsApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { EmptyState } from "../../core/components/EmptyState";
import { PageScaffold } from "../../core/components/PageScaffold";
import {
  dashboardRoutes,
} from "../../core/routing/dashboardRoutes";
import { StatusBadge } from "../../core/components/StatusBadge";
import { NavigableTableRow } from "../../core/components/NavigableTableRow";
import type {
  DeploymentStatus,
} from "../../models/controlPlane";
import { useDeploymentFeed } from "./useDeploymentFeed";
import { deploymentDetailPath, deploymentKey } from "./deploymentRoutes";
import { isActiveDeployment } from "./deploymentDetailPresentation";
import {
  deploymentCreatedTime,
  deploymentListDuration,
  isRecentDeployment,
} from "./deploymentListPresentation";

export function DeploymentsModule() {
  const { selectedOrganization } = useAuth();
  const selectedOrganizationId = useRef(selectedOrganization?.id);
  selectedOrganizationId.current = selectedOrganization?.id;
  const { deployments, error: feedError, loading: isLoading, refresh: refreshFeed } =
    useDeploymentFeed(selectedOrganization?.id);
  const [now, setNow] = useState(Date.now);
  const hasActiveDeployments = deployments.some(
    (deployment) => isActiveDeployment(deployment.status) && !deployment.completedAt,
  );
  const needsClock = hasActiveDeployments || deployments.some(
    (deployment) => isRecentDeployment(deployment.createdAt, now),
  );
  useEffect(() => {
    if (!needsClock) return;
    setNow(Date.now());
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [needsClock]);
  const [query, setQuery] = useCollectionQuery(
    `${selectedOrganization?.id}:deployments`,
    "createdAt",
    "desc",
  );
  const [appNames, setAppNames] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [refreshingDeployments, setRefreshingDeployments] = useState<ReadonlySet<string>>(() => new Set());

  const refresh = useCallback(async () => {
    setError(null);
    await refreshFeed();
  }, [refreshFeed]);
  useEffect(() => {
    let live = true;
    setAppNames({});
    if (!selectedOrganization) return;
    void listApplications(selectedOrganization.id).then((apps) => {
      if (live) setAppNames(Object.fromEntries(apps.flatMap((app) => [
        ...app.environments.map((environment) => [environment.id, `${app.name} / ${environment.name}`]),
      ])));
    }).catch((error) => {
      if (live) setError(error instanceof Error ? error.message : "Unable to load apps.");
    });
    return () => { live = false; };
  }, [selectedOrganization?.id]);

  const filteredDeployments = useMemo(
    () =>
      deployments
        .filter((deployment) => {
          const f = query.filters;
          return (
            `${deployment.number} ${appNames[deployment.appId] ?? ""}`
              .toLowerCase()
              .includes(query.search.trim().toLowerCase()) &&
            (!f.status || deployment.status === f.status) &&
            (!f.app || deployment.appId === f.app) &&
            (!f.database || deployment.database === f.database) &&
            (!f.from || deployment.createdAt.slice(0, 10) >= f.from) &&
            (!f.to || deployment.createdAt.slice(0, 10) <= f.to)
          );
        })
        .sort((a, b) =>
          compareRecords(
            query.sort === "status" ? a.status : a.createdAt,
            query.sort === "status" ? b.status : b.createdAt,
            query.direction,
            a.number,
            b.number,
          ),
        ),
    [deployments, query, appNames],
  );

  // Pending row refreshes are keyed by organization+number and target the organization captured at click time.
  async function refreshRow(organizationId: string, number: string) {
    const key = deploymentKey({ organizationId, number });
    const update = (pending: boolean) => setRefreshingDeployments((current) => {
      const next = new Set(current);
      if (pending) next.add(key); else next.delete(key);
      return next;
    });
    update(true);
    setError(null);
    try {
      await refreshFeed(() => refreshDeployment({ organizationId, number }));
    } catch (refreshError) {
      if (organizationId === selectedOrganizationId.current) {
        setError(
          refreshError instanceof Error
            ? refreshError.message
            : "Unable to refresh deployment.",
        );
      }
    } finally {
      update(false);
    }
  }

  return (
    <PageScaffold
      breadcrumbs={[{ label: "Deployments" }]}
      title="Deployments"
    >
      <CollectionToolbar
        label="deployments"
        query={query}
        onChange={setQuery}
        onRefresh={refresh}
        loading={isLoading}
        count={filteredDeployments.length}
        filters={[
          {
            id: "app",
            label: "App",
            options: Object.entries(appNames).map(([value, label]) => ({
              value,
              label,
            })),
          },
          {
            id: "status",
            label: "Status",
            options: options(deploymentStatuses),
          },
          {
            id: "database",
            label: "Database",
            options: options(["sqlite", "postgres"]),
          },
          { id: "from", label: "Created from", type: "date" },
          { id: "to", label: "Created through", type: "date" },
        ]}
        sorts={[
          { value: "createdAt", label: "Created time" },
          { value: "status", label: "Status" },
        ]}
      />
      {error || feedError ? <p className="text-sm text-destructive">{error ?? feedError}</p> : null}
      {isLoading ? (
        <p className="text-sm text-muted-foreground">Loading deployments...</p>
      ) : null}
      {!isLoading && filteredDeployments.length === 0 ? (
        <EmptyState
          title={
            deployments.length ? "No matching deployments" : "No deployments"
          }
        >
          Deploy published image refs with the Minicloud API or CLI.
        </EmptyState>
      ) : null}
      {filteredDeployments.length > 0 ? (
        <Card>
          <CardContent className="p-0">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>App</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Services / endpoints</TableHead>
                  <TableHead>Duration</TableHead>
                  <TableHead>Created</TableHead>
                  <TableHead />
                </TableRow>
              </TableHeader>
              <TableBody>
                {filteredDeployments.map((deployment) => {
                  const created = deploymentCreatedTime(deployment.createdAt, now);
                  const href = deploymentDetailPath(deployment.organizationId, deployment.number);
                  const refreshing = refreshingDeployments.has(deploymentKey(deployment));
                  return (
                  <NavigableTableRow
                    key={deployment.id}
                    href={href}
                  >
                    <TableCell data-label="App">
                      <Link
                        aria-label={`Deployment ${deployment.number}, ${appNames[deployment.appId] ?? "App unavailable"}`}
                        className="text-primary"
                        to={href}
                      >
                        {appNames[deployment.appId] ?? "App unavailable"}
                      </Link>
                      <div className="flex items-center gap-1 text-xs text-muted-foreground">
                        <span className="font-mono">
                          {deployment.number}
                        </span>
                        <CopyButton value={deployment.number} ariaLabel={`Copy deployment number ${deployment.number}`} />
                      </div>
                    </TableCell>
                    <TableCell data-label="Status">
                      <StatusBadge status={deployment.status} />
                    </TableCell>
                    <TableCell data-label="Services / endpoints">
                      {deployment.services.length} /{" "}
                      {deployment.services.reduce(
                        (count, service) => count + (service.urls?.length ?? 0),
                        0,
                      )}
                    </TableCell>
                    <TableCell data-label="Duration" className="font-mono text-xs">
                      {deploymentListDuration(deployment, now)}
                    </TableCell>
                    <TableCell data-label="Created">
                      <time dateTime={deployment.createdAt} title={created.timestamp}>
                        {created.label}
                      </time>
                    </TableCell>
                    <TableCell data-label="Actions" className="text-right">
                      <div className="flex justify-end gap-2">
                        <Button
                          aria-label={`Refresh deployment ${deployment.number}`}
                          disabled={refreshing}
                          onClick={() => void refreshRow(deployment.organizationId, deployment.number)}
                          size="icon"
                          variant="outline"
                        >
                          <RotateCw
                            className={
                              refreshing
                                ? "h-4 w-4 animate-spin"
                                : "h-4 w-4"
                            }
                          />
                        </Button>
                        <Button asChild size="sm" variant="outline">
                          <Link aria-label={`Open deployment ${deployment.number}`} to={href}>
                            Open
                          </Link>
                        </Button>
                      </div>
                    </TableCell>
                  </NavigableTableRow>
                  );
                })}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      ) : null}
    </PageScaffold>
  );
}
