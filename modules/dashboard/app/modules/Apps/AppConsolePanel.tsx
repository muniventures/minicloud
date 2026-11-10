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
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { EnvironmentDetailResponse, RuntimeDiagnosticBundleResponse, RuntimeLogResponse, RuntimeLogSourceResponse, RuntimeStatusResponse } from "../../models/controlPlane";

type Props = {
  app: EnvironmentDetailResponse;
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
  // Diagnostics are independent of log/status reads: their failure is shown
  // locally and never discards otherwise successful output.
  const [diagnosticsError, setDiagnosticsError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const services = useMemo(() => {
    const fromLogs = sources.map((item) => item.service).filter((value): value is string => Boolean(value));
    const fromStatus = status?.services.map((item) => item.name) ?? [];
    return Array.from(new Set([...fromLogs, ...fromStatus])).sort();
  }, [sources, status]);

  const load = useCallback(async () => {
    setError(null);
    setDiagnosticsError(null);
    setLoading(true);
    const selectedSource = source === "all" ? undefined : source;
    const selectedService = service === "all" ? undefined : service;
    // Independent reads: each outcome is applied (or reported) locally so
    // one failing read never discards the others.
    const [statusResult, sourcesResult, logsResult, diagnosticsResult] = await Promise.allSettled([
      getRuntimeStatus(app.id),
      getRuntimeLogSources(app.id),
      getRuntimeLogs(app.id, {source: selectedSource, service: selectedService, tail, since }),
      getRuntimeDiagnostics(app.id, since)
    ]);
    if (statusResult.status === "fulfilled") setStatus(statusResult.value);
    if (sourcesResult.status === "fulfilled") setSources(sourcesResult.value);
    if (logsResult.status === "fulfilled") setLogs(logsResult.value);
    if (diagnosticsResult.status === "fulfilled") {
      setDiagnostics(diagnosticsResult.value);
      setDiagnosticsError(null);
    } else {
      setDiagnostics(null);
      setDiagnosticsError(
        diagnosticsResult.reason instanceof Error ? diagnosticsResult.reason.message : "Diagnostics unavailable.",
      );
    }
    const primaryFailure = [statusResult, sourcesResult, logsResult].find((r) => r.status === "rejected");
    if (primaryFailure?.status === "rejected") {
      setError(primaryFailure.reason instanceof Error ? primaryFailure.reason.message : "Unable to load runtime console.");
    }
    setLoading(false);
  }, [app.id, service, since, source, tail]);

  useEffect(() => {
    void load();
  }, [load]);

  const serviceCollection = useRecordCollection(`${app.id}:runtime-services`, status?.services ?? [], { name: item => item.name, state: item => item.state }, item => item.name);
  const observedAge = status?.observedAt ? Date.now() - Date.parse(status.observedAt) : null;
  const snapshotStale = observedAge !== null && observedAge > 120_000;
  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="flex flex-wrap items-center gap-x-6 gap-y-2 pt-6 text-sm">
          <span className="inline-flex items-center gap-2">
            <span className="text-muted-foreground">Agent</span>
            <StatusBadge status={statusLabel(status)} />
          </span>
          <span className="inline-flex items-center gap-2">
            <span className="text-muted-foreground">Last seen</span>
            <span>{status?.lastSeenAt ? formatDate(status.lastSeenAt) : "Unknown"}</span>
          </span>
          <span className="inline-flex items-center gap-2">
            <span className="text-muted-foreground">Version</span>
            <span>{status?.agentVersion ?? "Unknown"}</span>
          </span>
          <span className="inline-flex items-center gap-2">
            <span className="text-muted-foreground">Proxy (Caddy)</span>
            <span>{status?.caddy ? (status.caddy.active ? "Active" : "Inactive") : "Unknown"}</span>
          </span>
          {snapshotStale ? (
            <span className="inline-flex items-center gap-2">
              <StatusBadge status="stale" />
              <span className="text-xs text-muted-foreground">Last known state</span>
            </span>
          ) : null}
        </CardContent>
      </Card>

      {diagnosticsError ? (
        <p role="alert" className="text-sm text-destructive">
          Diagnostics unavailable: {diagnosticsError}
        </p>
      ) : null}

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

      <Card>
        <CardHeader>
          <CardTitle>Runtime logs</CardTitle>
          <p className="text-sm text-muted-foreground">Runtime output, not a command terminal. Server order.</p>
        </CardHeader>
        <CardContent className="space-y-4">
          <CollectionToolbar label="console logs" query={query} onChange={setQuery} onRefresh={() => load()} loading={loading} count={logs.length} countLabel="returned" searchSupported={false} sorts={[]} filters={[{ id: "source", label: "Source", options: options([...new Set(["caddy","docker-compose","minicloud-agent",...sources.map(item => item.source)])]) }, { id: "service", label: "Service", options: options(services) }, { id: "since", label: "Since", type: "text" }, { id: "tail", label: "Tail (1–1000)", type: "number" }]} />
          <p className="text-xs text-muted-foreground">Server-filtered tail: up to {tail} records since {since}, in server order. Full-text search and alternate ordering are unavailable for this API.</p>
          {error ? <p className="text-sm text-destructive">{error}</p> : null}
          {loading ? <p className="text-sm text-muted-foreground">Loading runtime console...</p> : null}
          {!loading && logs.length === 0 ? (
            <EmptyState title={emptyTitle(status, source)}>No records matched the selected filters.</EmptyState>
          ) : (
            <div className="min-h-[280px] max-h-[520px] overflow-auto rounded-md border bg-muted p-3 font-mono text-xs text-muted-foreground" aria-label="Runtime log output">
              {logs.map((log) => (
                <div className="grid gap-2 py-1 md:grid-cols-[190px_180px_1fr]" key={log.id}>
                  <span className="text-muted-foreground">{formatDate(log.observedAt)}</span>
                  {/* Neutral stream treatment: stderr labels its stream but
                      must not read as error severity. */}
                  <span className="text-muted-foreground">{log.stream === "stderr" ? `stderr · ${log.source}` : log.source}</span>
                  <span className="whitespace-pre-wrap break-words">{log.content}</span>
                </div>
              ))}
            </div>
          )}
          <p className="text-xs text-muted-foreground" aria-live="polite">Loaded {logs.length} records (server order).</p>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Runtime services</CardTitle><CollectionToolbar label="runtime services" {...serviceCollection.toolbar} onRefresh={() => load()} loading={loading} />
        </CardHeader>
        <CardContent className="p-0">
          {status?.services.length ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Service</TableHead>
                  <TableHead>State</TableHead>
                  <TableHead>Restarts</TableHead>
                  <TableHead>Details</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {serviceCollection.records.map((runtimeService) => (
                  <TableRow key={runtimeService.name}>
                    <TableCell data-label="Service" className="font-medium">{runtimeService.name}</TableCell>
                    <TableCell data-label="State">
                      <StatusBadge status={!status?.observedAt ? "unavailable" : snapshotStale ? "stale" : runtimeService.health ?? runtimeService.state} />
                    </TableCell>
                    <TableCell data-label="Restarts">{runtimeService.restartCount ?? "Unavailable"}</TableCell>
                    <TableCell data-label="Details" className="break-all font-mono text-xs text-muted-foreground">
                      {runtimeService.container ?? runtimeService.image ?? "No container reported"}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : (
            <div className="p-4">
              <EmptyState title={status?.agentInstalled ? "Waiting for status" : "Agent not installed"}>
                {status?.agentInstalled ? "The agent has not reported container status yet." : "Install the Minicloud runtime agent during the next deployment."}
              </EmptyState>
            </div>
          )}
        </CardContent>
      </Card>
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
