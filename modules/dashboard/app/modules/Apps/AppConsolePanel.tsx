import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { options, useCollectionQuery } from "@/core/components/collectionQuery";
import { useRecordCollection } from "@/core/components/useRecordCollection";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { RefreshCw } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { getRuntimeDiagnostics, getRuntimeLogSources, getRuntimeLogs, getRuntimeStatus } from "../../api/runtimeApi";
import { EmptyState } from "../../core/components/EmptyState";
import { StatusBadge } from "../../core/components/StatusBadge";
import type { AppResponse, RuntimeDiagnosticBundleResponse, RuntimeLogResponse, RuntimeLogSourceResponse, RuntimeStatusResponse } from "../../models/controlPlane";

type Props = {
  app: AppResponse;
};

export function AppConsolePanel({ app }: Props) {
  const [query,setQuery] = useCollectionQuery(`${app.organizationId}:${app.id}:runtime-logs`, "observedAt");
  const source = query.filters.source || "all";
  const service = query.filters.service || "all";
  const tail = Math.min(1000, Math.max(1, Number(query.filters.tail || 200)));
  const since = query.filters.since || "30m";
  const [logs, setLogs] = useState<RuntimeLogResponse[]>([]);
  const [sources, setSources] = useState<RuntimeLogSourceResponse[]>([]);
  const [status, setStatus] = useState<RuntimeStatusResponse | null>(null);
  const [diagnostics, setDiagnostics] = useState<RuntimeDiagnosticBundleResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const services = useMemo(() => {
    const fromLogs = sources.map((item) => item.service).filter((value): value is string => Boolean(value));
    const fromStatus = status?.services.map((item) => item.name) ?? [];
    return Array.from(new Set([...fromLogs, ...fromStatus])).sort();
  }, [sources, status]);

  const load = useCallback(async () => {
    setError(null);
    setLoading(true);
    try {
      const selectedSource = source === "all" ? undefined : source;
      const selectedService = service === "all" ? undefined : service;
      const [nextStatus, nextSources, nextLogs, nextDiagnostics] = await Promise.all([
        getRuntimeStatus(app.id),
        getRuntimeLogSources(app.id),
        getRuntimeLogs(app.id, {source: selectedSource, service: selectedService, tail, since }),
        getRuntimeDiagnostics(app.id, since)
      ]);
      setStatus(nextStatus);
      setSources(nextSources);
      setLogs(nextLogs);
      setDiagnostics(nextDiagnostics);
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : "Unable to load runtime console.");
    } finally {
      setLoading(false);
    }
  }, [app.id, service, since, source, tail]);

  useEffect(() => {
    void load();
  }, [load]);

  const serviceCollection = useRecordCollection(`${app.id}:runtime-services`, status?.services ?? [], { name: item => item.name, state: item => item.state }, item => item.name);
  return (
    <div className="space-y-4">
      <div className="grid gap-4 lg:grid-cols-3">
        <Card>
          <CardHeader>
            <CardTitle>Runtime</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            <div className="flex items-center justify-between gap-3">
              <span className="text-muted-foreground">Agent</span>
              <StatusBadge status={statusLabel(status)} />
            </div>
            <div className="flex justify-between gap-3">
              <span className="text-muted-foreground">Last seen</span>
              <span>{status?.lastSeenAt ? formatDate(status.lastSeenAt) : "Never"}</span>
            </div>
            <div className="flex justify-between gap-3">
              <span className="text-muted-foreground">Version</span>
              <span>{status?.agentVersion ?? "Unknown"}</span>
            </div>
            <div className="flex justify-between gap-3">
              <span className="text-muted-foreground">Caddy</span>
              <span>{status?.caddy ? (status.caddy.active ? "Active" : "Inactive") : "Unknown"}</span>
            </div>
          </CardContent>
        </Card>

        <Card className="lg:col-span-2">
          <CardHeader>
            <CardTitle>Services</CardTitle><CollectionToolbar label="runtime services" {...serviceCollection.toolbar} onRefresh={() => load()} loading={loading} />
          </CardHeader>
          <CardContent>
            {status?.services.length ? (
              <div className="grid gap-2 md:grid-cols-2">
                {serviceCollection.records.map((runtimeService) => (
                  <div className="rounded-md border p-3 text-sm" key={runtimeService.name}>
                    <div className="flex items-center justify-between gap-3">
                      <span className="font-medium">{runtimeService.name}</span>
                      <StatusBadge status={!status?.observedAt ? "unavailable" : Date.now() - Date.parse(status.observedAt) > 120000 ? "stale" : runtimeService.health ?? runtimeService.state} />
                    </div>
                    <p className="mt-1 truncate text-muted-foreground">{runtimeService.container ?? runtimeService.image ?? "No container reported"}</p>
                    <p className="mt-1 text-xs text-muted-foreground">Restarts: {runtimeService.restartCount ?? "Unavailable"}</p>
                  </div>
                ))}
              </div>
            ) : (
              <EmptyState title={status?.agentInstalled ? "Waiting for status" : "Agent not installed"}>
                {status?.agentInstalled ? "The agent has not reported container status yet." : "Install the Minicloud runtime agent during the next deployment."}
              </EmptyState>
            )}
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Console</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <CollectionToolbar label="console logs" query={query} onChange={setQuery} onRefresh={() => load()} loading={loading} count={logs.length} countLabel="returned" searchSupported={false} sorts={[]} filters={[{ id: "source", label: "Source", options: options([...new Set(["caddy","docker-compose","minicloud-agent",...sources.map(item => item.source)])]) }, { id: "service", label: "Service", options: options(services) }, { id: "since", label: "Since", type: "text" }, { id: "tail", label: "Tail (1–1000)", type: "number" }]} />
          <p className="text-xs text-muted-foreground">Server-filtered tail: up to {tail} records since {since}. Full-text search and alternate ordering are unavailable for this API.</p>
          {error ? <p className="text-sm text-destructive">{error}</p> : null}
          {loading ? <p className="text-sm text-muted-foreground">Loading runtime console...</p> : null}
          {!loading && logs.length === 0 ? (
            <EmptyState title={emptyTitle(status, source)}>No records matched the selected filters.</EmptyState>
          ) : (
            <div className="max-h-[520px] overflow-auto rounded-md border bg-muted p-3 font-mono text-xs text-muted-foreground">
              {logs.map((log) => (
                <div className="grid gap-2 py-1 md:grid-cols-[190px_180px_1fr]" key={log.id}>
                  <span className="text-muted-foreground">{formatDate(log.observedAt)}</span>
                  <span className={log.stream === "stderr" ? "text-destructive" : "text-success"}>{log.source}</span>
                  <span className="whitespace-pre-wrap break-words">{log.content}</span>
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      {diagnostics && (diagnostics.caddyErrors.length > 0 || diagnostics.serviceErrors.length > 0) ? (
        <Card>
          <CardHeader>
            <CardTitle>Diagnostics</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            {diagnostics.latestDeploymentUrl ? <p className="text-muted-foreground">Latest public URL: {diagnostics.latestDeploymentUrl}</p> : null}
            {[...diagnostics.caddyErrors, ...diagnostics.serviceErrors].slice(0, 8).map((log) => (
              <div className="rounded-md border p-3" key={`diagnostic-${log.id}`}>
                <div className="mb-1 flex justify-between gap-3 text-xs text-muted-foreground">
                  <span>{log.source}</span>
                  <span>{formatDate(log.observedAt)}</span>
                </div>
                <p className="font-mono text-xs">{log.content}</p>
              </div>
            ))}
          </CardContent>
        </Card>
      ) : null}
    </div>
  );
}

function statusLabel(status: RuntimeStatusResponse | null) {
  if (!status) return "unavailable";
  if (!status.agentInstalled) return "not_installed";
  return status.agentOnline ? "online" : "offline";
}

function emptyTitle(status: RuntimeStatusResponse | null, source: string) {
  if (!status?.agentInstalled) return "Agent not installed";
  if (!status.agentOnline) return "Agent offline";
  return source === "all" ? "Waiting for logs" : "No logs for selected source";
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: "short", timeStyle: "medium" }).format(new Date(value));
}
