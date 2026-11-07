import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { options, useCollectionQuery } from "@/core/components/collectionQuery";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ExternalLink, RefreshCw } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  buildGrafanaUrl,
  exchangeGrafanaLaunch,
  getTelemetrySettings,
  launchGrafanaSession,
  queryApplicationLogs
} from "../../api/telemetryApi";
import { EmptyState } from "../../core/components/EmptyState";
import { ApiError, type AppResponse, type TelemetryLogsResponse, type TelemetrySettingsResponse } from "../../models/controlPlane";
import { TelemetryCredentialsCard } from "./components/TelemetryCredentialsCard";
import { TelemetrySettingsCard } from "./components/TelemetrySettingsCard";

type Props = {
  app: AppResponse;
  serviceNames?: string[];
};

const RANGE_PRESETS = [
  { value: "5m", label: "Last 5 minutes", minutes: 5 },
  { value: "15m", label: "Last 15 minutes", minutes: 15 },
  { value: "1h", label: "Last hour", minutes: 60 },
  { value: "6h", label: "Last 6 hours", minutes: 360 },
  { value: "24h", label: "Last 24 hours", minutes: 1440 },
  { value: "7d", label: "Last 7 days", minutes: 10080 }
];

const LIMIT_OPTIONS = [100, 200, 500, 1000];
const SEVERITIES = ["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"];
const AUTO_REFRESH_MS = 30_000;

export function AppTelemetryPanel({ app, serviceNames = [] }: Props) {
  const [settings, setSettings] = useState<TelemetrySettingsResponse | null>(null);
  const [settingsLoading, setSettingsLoading] = useState(true);
  const [settingsError, setSettingsError] = useState<string | null>(null);
  const [logs, setLogs] = useState<TelemetryLogsResponse | null>(null);
  const [logsLoading, setLogsLoading] = useState(false);
  const [logsError, setLogsError] = useState<{ message: string; code?: string } | null>(null);
  const [query,setQuery] = useCollectionQuery(`${app.organizationId}:${app.id}:application-logs`, "timestamp", "desc");
  const range = query.filters.range || "15m";
  const service = query.filters.service || "";
  const severity = query.filters.severity || "all";
  const text = query.search;
  const traceId = query.filters.traceId || "";
  const limit = Number(query.filters.limit || 200);
  const [autoRefresh, setAutoRefresh] = useState(true);
  const [visible, setVisible] = useState(true);
  const [grafanaBusy, setGrafanaBusy] = useState(false);
  const [grafanaError, setGrafanaError] = useState<string | null>(null);
  const [credentialsToken, setCredentialsToken] = useState(0);

  const rootRef = useRef<HTMLDivElement | null>(null);
  const visibleRef = useRef(true);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => {
    const root = rootRef.current;
    if (!root || typeof IntersectionObserver === "undefined") {
      return;
    }

    const observer = new IntersectionObserver(
      (entries) => {
        const isVisible = entries.some((entry) => entry.isIntersecting);
        visibleRef.current = isVisible;
        setVisible(isVisible);
      },
      { threshold: 0 }
    );
    observer.observe(root);
    return () => observer.disconnect();
  }, []);

  const loadSettings = useCallback(async (signal?: AbortSignal) => {
    setSettingsLoading(true);
    setSettingsError(null);
    try {
      setSettings(await getTelemetrySettings(app.id, signal));
    } catch (loadError) {
      if (loadError instanceof Error && loadError.name === "AbortError") {
        return null;
      }

      if (loadError instanceof ApiError && loadError.status === 404) {
        setSettings(null);
        return null;
      }

      setSettingsError(loadError instanceof Error ? loadError.message : "Unable to load telemetry settings.");
      return undefined;
    } finally {
      setSettingsLoading(false);
    }

    return null;
  }, [app.id]);

  const loadLogs = useCallback(async (signal?: AbortSignal) => {
    const preset = RANGE_PRESETS.find((item) => item.value === range) ?? RANGE_PRESETS[1];
    const end = new Date();
    const start = new Date(end.getTime() - preset.minutes * 60_000);
    setLogsLoading(true);
    setLogsError(null);
    try {
      setLogs(await queryApplicationLogs(app.id, {
        start: start.toISOString(),
        end: end.toISOString(),
        service: service.trim() || undefined,
        severity: severity === "all" ? undefined : severity,
        q: text || undefined,
        traceId: traceId.trim() || undefined,
        limit
      }, signal));
    } catch (loadError) {
      if (loadError instanceof Error && loadError.name === "AbortError") {
        return;
      }

      setLogsError({
        message: loadError instanceof Error ? loadError.message : "Unable to load application logs.",
        code: loadError instanceof ApiError ? loadError.code : undefined
      });
    } finally {
      setLogsLoading(false);
    }
  }, [app.id, limit, range, service, severity, text, traceId]);

  // Cancel in-flight requests whenever the app changes or the panel unmounts.
  useEffect(() => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    setLogsError(null);
    void loadSettings(controller.signal).then(() => {
      if (!controller.signal.aborted) {
        void loadLogs(controller.signal);
      }
    });
    return () => controller.abort();
  }, [app.id, loadLogs, loadSettings]);

  // Bounded polling only: no live-tail socket. Ticks are skipped while the
  // panel is scrolled out of view, the browser tab is hidden, or a load is
  // already running.
  useEffect(() => {
    if (!autoRefresh || !settings?.enabled || logsLoading) {
      return;
    }

    const timer = window.setInterval(() => {
      if (!visibleRef.current || document.hidden || abortRef.current?.signal.aborted) {
        return;
      }

      void loadLogs(abortRef.current?.signal ?? undefined);
    }, AUTO_REFRESH_MS);
    return () => window.clearInterval(timer);
  }, [autoRefresh, loadLogs, settings?.enabled, logsLoading]);

  const refresh = useCallback(() => {
    const signal = abortRef.current?.signal ?? undefined;
    void loadSettings(signal).then(() => void loadLogs(signal));
  }, [loadLogs, loadSettings]);

  async function openInGrafana() {
    setGrafanaBusy(true);
    setGrafanaError(null);
    try {
      const launch = await launchGrafanaSession(app.id, "/explore");
      const exchange = launch.exchangeUrl
        ? await exchangeGrafanaLaunch(launch.launchCode, launch.exchangeUrl)
        : await exchangeGrafanaLaunch(launch.launchCode);
      const url = launch.grafanaBaseUrl
        ? buildGrafanaUrl(exchange.destination, launch.grafanaBaseUrl)
        : buildGrafanaUrl(exchange.destination);
      window.open(url, "_blank", "noopener,noreferrer");
    } catch (openError) {
      setGrafanaError(openError instanceof Error ? openError.message : "Unable to open Grafana.");
    } finally {
      setGrafanaBusy(false);
    }
  }

  const windowLabel = (RANGE_PRESETS.find((item) => item.value === range) ?? RANGE_PRESETS[1]).label;

  return (
    <div className="space-y-4" ref={rootRef}>
      <TelemetrySettingsCard appId={app.id} settings={settings} onChanged={setSettings} />
      {settingsLoading ? <p className="text-sm text-muted-foreground">Loading telemetry settings...</p> : null}
      {settingsError ? <p className="text-sm text-destructive">{settingsError}</p> : null}

      <TelemetryCredentialsCard
        appId={app.id}
        services={serviceNames}
        refreshToken={credentialsToken}
        onChanged={() => setCredentialsToken((value) => value + 1)}
      />

      <Card>
        <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-3">
          <CardTitle>Application logs</CardTitle>

        </CardHeader>
        <CardContent className="space-y-4">
          {grafanaError ? <p className="text-sm text-destructive">{grafanaError}</p> : null}

          <CollectionToolbar label="application logs" query={query} onChange={setQuery} onRefresh={refresh} loading={logsLoading || settingsLoading} count={logs?.entries.length ?? 0} countLabel="returned" sorts={[]}
            filters={[{ id: "range", label: "Time range", defaultLabel: "Last 15 minutes (default)", options: RANGE_PRESETS }, { id: "service", label: "Service", type: "text" }, { id: "severity", label: "Severity", options: options(SEVERITIES) }, { id: "traceId", label: "Trace ID", type: "text" }, { id: "limit", label: "Limit", defaultLabel: "200 (default)", options: options(LIMIT_OPTIONS.map(String)) }]}
            secondary={{ id: "grafana", label: "Open in Grafana", onClick: () => void openInGrafana(), disabled: !settings?.enabled, loading: grafanaBusy }} />
          <label className="flex items-center gap-2 text-sm">
            <Checkbox checked={autoRefresh} onCheckedChange={(checked) => setAutoRefresh(checked === true)} />
            Auto-refresh every 30 seconds while visible
            {!visible && autoRefresh ? <span className="text-muted-foreground">(paused — panel hidden)</span> : null}
          </label>

          {logs?.retentionCutoff ? (
            <p className="text-xs text-muted-foreground">
              Window: {windowLabel}. Retention boundary {formatDate(logs.retentionCutoff)} — older queries are rejected.
              {logs.lastAcceptedAt ? ` Last received ${formatDate(logs.lastAcceptedAt)}.` : " No logs received yet."}
            </p>
          ) : null}

          {logsError ? (
            logsError.code === "query_rate_limited" ? (
              <p className="rounded-md border border-border bg-warning-surface p-3 text-sm">
                Query budget exhausted — too many concurrent log queries. Wait a few seconds and refresh.
              </p>
            ) : (
              <div className="rounded-md border border-destructive/40 p-3 text-sm">
                <p className="text-destructive">Application logs are unavailable: {logsError.message}</p>
                <Button className="mt-2" onClick={refresh} size="sm" variant="outline">Retry</Button>
              </div>
            )
          ) : null}

          {logsLoading && !logs ? <p className="text-sm text-muted-foreground">Loading application logs...</p> : null}

          {!logsLoading && !logsError && logs && logs.state !== "ok" ? (
            <EmptyState title={stateTitle(logs.state)}>
              {stateHint(logs.state, settings?.lastAcceptedAt ?? logs.lastAcceptedAt)}
            </EmptyState>
          ) : null}

          {!logsLoading && !logsError && logs?.state === "ok" && logs.entries.length === 0 ? (
            <EmptyState title={logs.lastAcceptedAt ? "No entries in this window" : "Waiting for first export"}>
              {logs.lastAcceptedAt
                ? "Widen the time range or clear filters. Runtime stdout logs, if any, stay in Console."
                : "Configure the OpenTelemetry logs exporter above. This view only shows explicitly exported OTLP records — it never implies collection from settings alone."}
            </EmptyState>
          ) : null}

          {logs?.truncated ? (
            <p className="rounded-md border border-border bg-warning-surface p-3 text-sm">
              Results truncated ({logs.truncationReason ?? "limit"}). Narrow the time range or add filters to see
              the newest {logs.entries.length} entries.
            </p>
          ) : null}

          {logs && logs.entries.length > 0 ? (
            <div className="max-h-[520px] overflow-auto rounded-md border bg-muted p-3 font-mono text-xs text-muted-foreground">
              {logs.entries.map((entry, index) => (
                <div className="grid gap-2 py-1 md:grid-cols-[190px_120px_80px_1fr]" key={`${entry.timestamp}-${index}`}>
                  <span className="text-muted-foreground">{formatDate(entry.timestamp)}</span>
                  <span className="break-words text-success">{entry.service || "unknown"}</span>
                  <span className={entry.severity === "ERROR" || entry.severity === "FATAL" ? "text-destructive" : "text-info"}>
                    {entry.severity || "-"}
                  </span>
                  <span className="whitespace-pre-wrap break-words">
                    {entry.traceId ? <span className="mr-2 text-muted-foreground">trace {entry.traceId}</span> : null}
                    {entry.body}
                  </span>
                </div>
              ))}
            </div>
          ) : null}

          <p className="text-xs text-muted-foreground">
            OTLP application logs only — distinct from the runtime Console and deployment logs. At most 1,000
            entries per query.
          </p>
        </CardContent>
      </Card>
    </div>
  );
}

function stateTitle(state: string) {
  switch (state) {
    case "not_configured": return "Application logs not configured";
    case "disabled": return "Application logs disabled";
    case "provisioning": return "Log storage provisioning";
    default: return "No application logs";
  }
}

function stateHint(state: string, lastAcceptedAt?: string | null) {
  switch (state) {
    case "not_configured":
      return "Enable application logs and configure an exporter. Nothing is collected until then.";
    case "disabled":
      return lastAcceptedAt
        ? `Disabled. Historical entries remain readable until retention expires. Last received ${lastAcceptedAt}.`
        : "Disabled. Historical entries remain readable until retention expires.";
    case "provisioning":
      return "Storage is provisioning. Exported logs will appear here once it completes.";
    default:
      return "No entries matched.";
  }
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: "short", timeStyle: "medium" }).format(new Date(value));
}
