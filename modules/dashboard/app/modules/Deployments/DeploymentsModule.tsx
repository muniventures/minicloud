import { listApps } from "@/api/appsApi";
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
import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { refreshDeployment } from "../../api/deploymentsApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { EmptyState } from "../../core/components/EmptyState";
import { PageScaffold } from "../../core/components/PageScaffold";
import {
  buildDashboardPath,
  dashboardRoutes,
} from "../../core/routing/dashboardRoutes";
import { StatusBadge } from "../../core/components/StatusBadge";
import { NavigableTableRow } from "../../core/components/NavigableTableRow";
import type {
  DeploymentStatus,
} from "../../models/controlPlane";
import { useDeploymentFeed } from "./useDeploymentFeed";
import { isActiveDeployment } from "./deploymentDetailPresentation";
import {
  deploymentCreatedTime,
  deploymentListDuration,
  isRecentDeployment,
} from "./deploymentListPresentation";

export function DeploymentsModule() {
  const { appId } = useParams();
  const { selectedOrganization } = useAuth();
  const { deployments, error: feedError, loading: isLoading, refresh: refreshFeed } =
    useDeploymentFeed(selectedOrganization?.id, appId);
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
    `${selectedOrganization?.id}:deployments:${appId ?? "all"}`,
    "createdAt",
    "desc",
  );
  const [appNames, setAppNames] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [refreshingDeploymentId, setRefreshingDeploymentId] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    setError(null);
    await refreshFeed();
  }, [refreshFeed]);
  useEffect(() => {
    let live = true;
    setAppNames({});
    if (!selectedOrganization) return;
    void listApps(selectedOrganization.id).then((apps) => {
      if (live) setAppNames(Object.fromEntries(apps.flatMap((app) => [
        [app.id, app.name],
        ...(app.branches ?? []).map((branch) => [branch.id, branch.name]),
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
            `${deployment.id} ${appNames[deployment.appId] ?? ""}`
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
            a.id,
            b.id,
          ),
        ),
    [deployments, query, appNames],
  );

  async function refreshRow(deploymentId: string) {
    setRefreshingDeploymentId(deploymentId);
    setError(null);
    try {
      await refreshFeed(() => refreshDeployment(deploymentId));
    } catch (refreshError) {
      setError(
        refreshError instanceof Error
          ? refreshError.message
          : "Unable to refresh deployment.",
      );
    } finally {
      setRefreshingDeploymentId(null);
    }
  }

  return (
    <PageScaffold
      breadcrumbs={
        appId
          ? [
              { label: "Apps", to: dashboardRoutes.apps.path },
              {
                label: appId,
                to: buildDashboardPath(dashboardRoutes.appDetail, { appId }),
              },
              { label: "Deployments" },
            ]
          : [{ label: "Deployments" }]
      }
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
                  return (
                  <NavigableTableRow
                    key={deployment.id}
                    href={appId ? `/apps/${appId}/deployments/${deployment.id}` : `/deployments/${deployment.id}`}
                  >
                    <TableCell data-label="App">
                      <Link
                        className="text-primary"
                        to={`/deployments/${deployment.id}`}
                      >
                        {appNames[deployment.appId] ?? "App unavailable"}
                      </Link>
                      <div className="flex items-center gap-1 text-xs text-muted-foreground">
                        <span className="max-w-32 truncate">
                          {deployment.id}
                        </span>
                        <CopyButton value={deployment.id} />
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
                          aria-label={`Refresh ${deployment.id}`}
                          disabled={refreshingDeploymentId === deployment.id}
                          onClick={() => void refreshRow(deployment.id)}
                          size="icon"
                          variant="outline"
                        >
                          <RotateCw
                            className={
                              refreshingDeploymentId === deployment.id
                                ? "h-4 w-4 animate-spin"
                                : "h-4 w-4"
                            }
                          />
                        </Button>
                        <Button asChild size="sm" variant="outline">
                          <Link
                            to={
                              appId
                                ? `/apps/${appId}/deployments/${deployment.id}`
                                : `/deployments/${deployment.id}`
                            }
                          >
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
