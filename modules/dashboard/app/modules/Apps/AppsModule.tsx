import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { listApps } from "@/api/appsApi";
import { useAuth } from "@/core/auth/AuthProvider";
import { PageScaffold } from "@/core/components/PageScaffold";
import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import {
  compareRecords,
  deploymentStatuses,
  options,
  useCollectionQuery,
} from "@/core/components/collectionQuery";
import type { AppResponse } from "@/models/controlPlane";
import { AppCard } from "./components/AppCard";
export function AppsModule() {
  const { selectedOrganization } = useAuth();
  const navigate = useNavigate();
  const [apps, setApps] = useState<AppResponse[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const pending = useRef<{ organizationId: string } | null>(null);
  const [query, setQuery] = useCollectionQuery(
    `${selectedOrganization?.id}:apps`,
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
      const next = await listApps(selectedOrganization.id, undefined, true);
      if (pending.current === request) setApps(next);
    } catch (error) {
      if (pending.current === request)
        setError(
          error instanceof Error ? error.message : "Unable to load apps.",
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
  const filtered = apps
    .filter((app) => {
      const f = query.filters;
      return (
        [
          app.name,
          app.slug,
          ...app.domains.map((domain) => `${domain.hostname} ${domain.url}`),
        ]
          .join(" ")
          .toLowerCase()
          .includes(query.search.trim().toLowerCase()) &&
        (!f.database || app.database === f.database) &&
        (!f.plan || app.plan === f.plan) &&
        (!f.status || (app.latestDeployment?.status ?? "none") === f.status) &&
        (!f.kind || (app.parentAppId ? "branch" : "main") === f.kind)
      );
    })
    .sort((a, b) =>
      compareRecords(
        query.sort === "name" ? a.name : a.latestDeployment?.completedAt,
        query.sort === "name" ? b.name : b.latestDeployment?.completedAt,
        query.direction,
        a.id,
        b.id,
      ),
    );
  return (
    <PageScaffold title="Apps">
      <CollectionToolbar
        label="apps"
        query={query}
        onChange={setQuery}
        onRefresh={() => refresh()}
        loading={loading}
        count={filtered.length}
        filters={[
          {
            id: "database",
            label: "Database",
            options: options(["sqlite", "postgres"]),
          },
          {
            id: "plan",
            label: "Plan",
            options: options([...new Set(apps.map((app) => app.plan))]),
          },
          {
            id: "status",
            label: "Latest deployment",
            options: [
              ...options(deploymentStatuses),
              { value: "none", label: "No deployments" },
            ],
          },
          {
            id: "kind",
            label: "App type",
            options: options(["main", "branch"]),
          },
        ]}
        sorts={[
          { value: "name", label: "Name" },
          { value: "completedAt", label: "Latest deployment completion" },
        ]}
        views={[
          { value: "cards", label: "Cards" },
          { value: "list", label: "List" },
        ]}
        primary={{
          id: "create",
          label: "Create app",
          visible: ["owner", "admin", "developer"].includes(
            selectedOrganization?.role?.toLowerCase() ?? "",
          ),
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
        <p role="alert" className="text-destructive">
          {error}
        </p>
      ) : null}
      {loading ? <p role="status">Loading apps…</p> : null}
      {!loading && !error && !filtered.length ? (
        <p>
          {apps.length
            ? "No matching applications. Adjust search or Filter & sort."
            : "No apps. Create an app to get started."}
        </p>
      ) : null}
      <div
        className={
          query.view === "cards"
            ? "grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3"
            : "grid gap-4"
        }
      >
        {filtered.map((app) => (
          <AppCard
            key={app.id}
            app={app}
            compact={query.view !== "cards"}
            onOpen={(id) => navigate(`/apps/${id}`)}
          />
        ))}
      </div>
    </PageScaffold>
  );
}
