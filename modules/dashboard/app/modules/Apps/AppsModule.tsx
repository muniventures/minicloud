import { Fragment, useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { listApplications } from "@/api/applicationsApi";
import { useAuth } from "@/core/auth/AuthProvider";
import { PageScaffold } from "@/core/components/PageScaffold";
import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { StatusBadge } from "@/core/components/StatusBadge";
import { deploymentStatuses, options, useCollectionQuery } from "@/core/components/collectionQuery";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/core/components/EmptyState";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ChevronDown, ChevronRight, MoreHorizontal } from "lucide-react";
import type { ApplicationEnvironment, Application } from "@/models/application";
import {
  appMatchesFilters,
  matchedEnvironment,
  matchesSearch,
  orderedEnvironments,
  sortApplications,
  visibleEnvironments,
} from "./appCollection";

export function appOverviewPath(applicationId: string, environmentId: string) {
  return `/apps/${encodeURIComponent(applicationId)}/environments/${encodeURIComponent(environmentId)}/overview`;
}

export function appSettingsPath(applicationId: string) {
  return `/apps/${encodeURIComponent(applicationId)}/settings`;
}

export function defaultEnvironment(app: Application): ApplicationEnvironment | null {
  const ordered = orderedEnvironments(app);
  return (
    ordered.find((env) => env.id === app.defaultEnvironmentId) ?? ordered[0] ?? null
  );
}

export function AppsModule() {
  const { selectedOrganization } = useAuth();
  const navigate = useNavigate();
  const [apps, setApps] = useState<Application[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const pending = useRef<{ organizationId: string } | null>(null);
  const [expandedPreviews, setExpandedPreviews] = useState<Record<string, boolean>>({});
  const [query, setQuery] = useCollectionQuery(
    `${selectedOrganization?.id}:appsv2`,
    "name",
    "asc",
  );

  const refresh = useCallback(async () => {
    if (
      !selectedOrganization ||
      pending.current?.organizationId === selectedOrganization.id
    )
      return;
    const request = { organizationId: selectedOrganization.id };
    pending.current = request;
    setLoading(true);
    setError(null);
    try {
      const next = await listApplications(selectedOrganization.id);
      if (pending.current === request) setApps(next);
    } catch (err) {
      if (pending.current === request)
        setError(
          err instanceof Error ? err.message : "Unable to load apps.",
        );
    } finally {
      if (pending.current === request) {
        pending.current = null;
        setLoading(false);
      }
    }
  }, [selectedOrganization?.id]);

  useEffect(() => {
    setApps([]);
    void refresh();
  }, [refresh]);

  const envFilter = {
    kind: query.filters.environmentKind || undefined,
    deployment: query.filters.deployment || undefined,
  };
  const filtered = sortApplications(
    apps.filter((app) => matchesSearch(app, query.search) && appMatchesFilters(app, envFilter)),
    query.sort === "updatedAt" ? "updatedAt" : "name",
    query.sort === "updatedAt" ? (query.direction === "desc" ? "desc" : "asc") : "asc",
  );
  const isDefaultSort = query.sort === "name" && query.direction === "asc";

  const canCreate = ["owner", "admin", "developer"].includes(
    selectedOrganization?.role?.toLowerCase() ?? "",
  );

  return (
    <PageScaffold title="Apps">
      <CollectionToolbar
        label="apps"
        query={query}
        onChange={(next) => setQuery({ ...next, view: "table" })}
        onRefresh={() => refresh()}
        loading={loading}
        count={filtered.length}
        filters={[
          {
            id: "environmentKind",
            label: "Environment kind",
            options: options(["persistent", "preview"]),
          },
          {
            id: "deployment",
            label: "Latest deployment",
            options: [
              ...options(deploymentStatuses),
              { value: "none", label: "No deployments" },
            ],
          },
        ]}
        sorts={[
          { value: "name", label: "Name" },
          { value: "updatedAt", label: "Recently updated" },
        ]}
        primary={{
          id: "create",
          label: "Create app",
          visible: canCreate,
          onClick: () => navigate("/apps/new"),
        }}
        secondary={{
          id: "deployments",
          label: "Deployments",
          onClick: () => navigate("/deployments"),
        }}
      />
      <p className="text-xs text-muted-foreground">
        {filtered.length} of {apps.length} apps
      </p>
      {error ? (
        <div role="alert" className="text-destructive">
          <p>{error}</p>
          <Button variant="outline" size="sm" onClick={() => void refresh()}>
            Retry
          </Button>
        </div>
      ) : null}
      {loading && apps.length === 0 ? <p role="status">Loading apps…</p> : null}
      {!loading && !error && filtered.length === 0 ? (
        <EmptyState title={apps.length ? "No matching applications" : "No apps"}>
          {apps.length
            ? "Adjust search or Filter & sort."
            : canCreate
              ? "Create an app to get started."
              : "Ask an organization admin to create an app."}
        </EmptyState>
      ) : null}
      {filtered.length > 0 ? (
        <Table aria-label="Applications">
          <TableHeader>
            <TableRow>
              <TableHead>App</TableHead>
              <TableHead>Environment</TableHead>
              <TableHead>Latest deployment</TableHead>
              <TableHead className="text-right">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {filtered.map((app) => {
              const match = matchedEnvironment(app, envFilter);
              const { visible, hiddenPreviewCount } = visibleEnvironments(app, {
                previewsExpanded: expandedPreviews[app.id] === true,
                matchedId: match?.id ?? null,
              });
              const fallback = defaultEnvironment(app);
              const rows = visible.length
                ? visible
                : [null];
              return (
                <Fragment key={app.id}>
                  {rows.map((env, index) => (
                    <TableRow key={env?.id ?? "none"}>
                      {index === 0 ? (
                        <TableCell rowSpan={rows.length} data-label="App">
                          <div className="space-y-1">
                            <Link
                              className="font-medium text-primary hover:underline"
                              to={
                                fallback
                                  ? appOverviewPath(app.id, fallback.id)
                                  : appSettingsPath(app.id)
                              }
                            >
                              {app.name}
                            </Link>
                            <p className="text-xs text-muted-foreground">{app.slug}</p>
                            <p className="text-xs text-muted-foreground">
                              {app.environmentCount}{" "}
                              {app.environmentCount === 1 ? "environment" : "environments"}
                            </p>
                            <Link
                              className="text-xs text-muted-foreground hover:underline"
                              to={appSettingsPath(app.id)}
                            >
                              App settings
                            </Link>
                          </div>
                        </TableCell>
                      ) : null}
                      <TableCell data-label="Environment">
                        {env ? (
                          <div className="space-y-0.5">
                            <Link
                              className="font-medium hover:underline"
                              to={appOverviewPath(app.id, env.id)}
                            >
                              {env.name}
                              {match?.id === env.id ? (
                                <span className="ml-2 rounded bg-muted px-1.5 py-0.5 text-xs">
                                  Matches filter
                                </span>
                              ) : null}
                            </Link>
                            <p className="text-xs text-muted-foreground">
                              {env.kind === "preview" ? "Preview" : "Persistent"}
                            </p>
                          </div>
                        ) : (
                          <span className="text-sm text-muted-foreground">No environments</span>
                        )}
                      </TableCell>
                      <TableCell data-label="Latest deployment">
                        {env ? (
                          <LatestDeploymentCell app={app} env={env} />
                        ) : (
                          <span className="text-sm text-muted-foreground">No deployments yet</span>
                        )}
                      </TableCell>
                      <TableCell data-label="Actions" className="text-right">
                        {env ? (
                          <EnvironmentActions app={app} env={env} />
                        ) : (
                          <Button size="sm" variant="outline" onClick={() => navigate(`${appSettingsPath(app.id)}?addEnvironment=1`)}>
                            Add environment
                          </Button>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                  {hiddenPreviewCount > 0 ? (
                    <TableRow>
                      <TableCell data-label="App" />
                      <TableCell data-label="Environment" colSpan={3}>
                        <Button
                          size="sm"
                          variant="ghost"
                          aria-expanded={false}
                          onClick={() =>
                            setExpandedPreviews((current) => ({ ...current, [app.id]: true }))
                          }
                        >
                          <ChevronRight className="mr-1 h-4 w-4" />
                          Show {hiddenPreviewCount}{" "}
                          {hiddenPreviewCount === 1 ? "preview" : "previews"}
                        </Button>
                      </TableCell>
                    </TableRow>
                  ) : null}
                </Fragment>
              );
            })}
          </TableBody>
        </Table>
      ) : null}
      {isDefaultSort ? null : (
        <p className="text-xs text-muted-foreground">
          Default order: application name ascending. Reset sort to return.
        </p>
      )}
    </PageScaffold>
  );
}

function LatestDeploymentCell({
  app,
  env,
}: {
  app: Application;
  env: ApplicationEnvironment;
}) {
  const latest = env.latestDeployment;
  if (env.deletionStatus === "failed") {
    return (
      <div className="space-y-1">
        <StatusBadge status="Deletion failed" />
        <p className="text-xs text-destructive">{env.deletionError ?? "Environment deletion failed."}</p>
        <Link
          className="text-xs text-primary hover:underline"
          to={appSettingsPath(app.id)}
        >
          Review &amp; retry
        </Link>
      </div>
    );
  }
  if (env.deletionStatus === "deleting") {
    return <StatusBadge status="Deleting" />;
  }
  if (!latest) {
    return <span className="text-sm text-muted-foreground">No deployments yet</span>;
  }
  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <StatusBadge status={latest.status} />
      <span className="text-xs text-muted-foreground">
        {latest.completedAt ? `Completed ${formatAge(latest.completedAt)}` : "In progress"}
      </span>
    </span>
  );
}

function EnvironmentActions({
  app,
  env,
}: {
  app: Application;
  env: ApplicationEnvironment;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          aria-label={`Actions for ${app.name} ${env.name}`}
          size="icon"
          variant="ghost"
          className="h-8 w-8 text-muted-foreground hover:text-foreground"
        >
          <MoreHorizontal className="h-4 w-4" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem asChild>
          <Link to={appOverviewPath(app.id, env.id)}>Open {env.name} overview</Link>
        </DropdownMenuItem>
        <DropdownMenuItem asChild>
          <Link to={`/apps/${encodeURIComponent(app.id)}/environments/${encodeURIComponent(env.id)}/deployments`}>
            {env.name} deployments
          </Link>
        </DropdownMenuItem>
        <DropdownMenuItem asChild>
          <Link to={`${appSettingsPath(app.id)}#environment-${env.id}`}>
            <span className="inline-flex items-center gap-1">
              <ChevronDown className="hidden" />
              {env.name} settings…
            </span>
          </Link>
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

export function formatAge(iso: string): string {
  const time = Date.parse(iso);
  if (!Number.isFinite(time)) return "Unknown time";
  const diff = Date.now() - time;
  const minutes = Math.floor(diff / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}
