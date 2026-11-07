import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Check, Circle, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CopyButton } from "@/core/components/CopyButton";
import type {
  DeploymentEventResponse,
  DeploymentLogResponse,
  DeploymentResponse,
} from "@/models/controlPlane";
import { deploymentProgress } from "../deploymentProgressPresentation";
import {
  formatDeploymentTime,
  formatDuration,
  isActiveDeployment,
} from "../deploymentDetailPresentation";

export function DeploymentProgressPanel({
  deployment,
  events,
  logs,
  observedAt,
}: {
  deployment: DeploymentResponse;
  events: DeploymentEventResponse[];
  logs: DeploymentLogResponse[];
  observedAt: number;
}) {
  const [now, setNow] = useState(Date.now);
  const [inspector, setInspector] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const preview = useRef<HTMLPreElement>(null);
  const panel = useRef<HTMLElement>(null);
  useEffect(() => {
    if (inspector) {
      panel.current?.focus({ preventScroll: true });
      if (window.innerWidth < 1024)
        panel.current?.scrollIntoView({ block: "nearest" });
    }
  }, [inspector]);
  const active = isActiveDeployment(deployment.status);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  useEffect(
    () => setInspector(deployment.status === "failed"),
    [deployment.id, deployment.status],
  );
  const close = useCallback(() => {
    setInspector(false);
    trigger.current?.focus();
  }, []);
  const model = useMemo(
    () => deploymentProgress(deployment, events, logs),
    [deployment, events, logs],
  );
  const previewText = model.preview
    .map(
      (line) =>
        `${formatDeploymentTime(line.createdAt)} [${line.source}] ${line.content}`,
    )
    .join("\n");
  useEffect(() => {
    if (preview.current)
      preview.current.scrollTop = preview.current.scrollHeight;
  }, [previewText]);
  const orderedLogs = useMemo(
    () =>
      [...logs].sort(
        (a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt),
      ),
    [logs],
  );
  const elapsed = formatDuration(
    (deployment.completedAt ? Date.parse(deployment.completedAt) : now) -
      Date.parse(model.totalStartedAt),
  );
  const lastUpdate = Math.max(0, Math.floor((now - observedAt) / 1000));
  return (
    <section
      aria-label="Deployment progress"
      className="rounded-xl border border-border bg-background"
    >
      <div
        className={
          inspector
            ? "grid min-w-0 lg:grid-cols-[minmax(0,1.2fr)_minmax(0,1fr)]"
            : "min-w-0"
        }
      >
        <div className="min-w-0 p-4 sm:p-6">
          <ol
            aria-label="Deployment stages"
            className={`mb-7 grid grid-cols-2 gap-3 ${model.stages.length === 5 ? "sm:grid-cols-5" : "sm:grid-cols-4"}`}
          >
            {model.stages.map((stage) => {
              const color =
                stage.state === "complete"
                  ? "text-success"
                  : stage.state === "running"
                    ? "text-info"
                    : stage.state === "failed"
                      ? "text-destructive"
                      : "text-muted-foreground";
              return (
                <li
                  key={stage.label}
                  aria-label={`${stage.label}, ${stage.state}`}
                  aria-current={stage.state === "running" ? "step" : undefined}
                  className="min-w-0"
                >
                  <div
                    className={`mb-3 h-1 rounded-full ${stage.state === "complete" ? "bg-success" : stage.state === "running" ? "bg-info" : stage.state === "failed" ? "bg-destructive" : "bg-muted"}`}
                  />
                  <div
                    className={`flex items-center gap-1.5 text-xs sm:text-sm ${color}`}
                  >
                    {stage.state === "complete" ? (
                      <Check className="h-3 w-3 shrink-0" />
                    ) : stage.state === "failed" ||
                      stage.state === "canceled" ? (
                      <X className="h-3 w-3 shrink-0" />
                    ) : (
                      <Circle
                        className={`h-2.5 w-2.5 shrink-0 ${stage.state === "running" ? "fill-current" : ""}`}
                      />
                    )}{" "}
                    {stage.label}
                  </div>
                  <p className="mt-1 text-xs tabular-nums text-muted-foreground">
                    {stage.start && (stage.end || stage.state === "running")
                      ? formatDuration(
                          (stage.end ? Date.parse(stage.end) : now) -
                            Date.parse(stage.start),
                        )
                      : stage.state === "complete"
                        ? "Complete"
                        : stage.state === "running"
                          ? "In progress"
                          : stage.state.replace(/^./, (c) => c.toUpperCase())}
                  </p>
                </li>
              );
            })}
          </ol>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="text-xs uppercase tracking-wide text-muted-foreground">
              {active ? "Happening now" : "Result"}
            </p>
            <span className="text-xs tabular-nums text-muted-foreground">
              {elapsed} {active ? "elapsed" : "total"}
            </span>
          </div>
          <h2
            className="mt-2 text-lg font-medium tracking-tight"
            aria-live="polite"
          >
            {model.headline}
          </h2>
          {deployment.failureCode ? (
            <p role="alert" className="mt-2 text-sm text-destructive">
              {deployment.failureCode.replaceAll("_", " ")}. View all logs for
              details.
            </p>
          ) : null}
          <div className="mt-4 rounded-lg bg-muted/40 p-3 sm:p-4">
            <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
              <h3 className="text-sm font-medium">
                {active ? "Current step output" : "Last step output"}
              </h3>
              <span className="text-xs text-muted-foreground">
                {active ? "Updates automatically" : "Finished"}
              </span>
            </div>
            <pre
              ref={preview}
              aria-label="Current step logs"
              className="max-h-56 overflow-auto whitespace-pre-wrap break-words rounded-md border bg-background p-3 font-mono text-xs leading-6"
            >
              {model.preview.length
                ? previewText
                : model.scoped
                  ? active
                    ? "Waiting for output from this step."
                    : "No output was recorded for this step."
                  : "No step output reported yet. View all logs for deployment output."}
            </pre>
            <div className="mt-2 flex flex-wrap items-center justify-between gap-2">
              <span className="text-xs text-muted-foreground">
                Latest {model.preview.length || 0} lines ·{" "}
                {active ? "Current" : "Last"} step only
              </span>
              <Button
                ref={trigger}
                variant="link"
                size="sm"
                className="h-auto px-0"
                onClick={() => (inspector ? close() : setInspector(true))}
                aria-expanded={inspector}
                aria-controls="deployment-inspector"
              >
                {inspector ? "Hide details" : "View all logs"}
              </Button>
            </div>
          </div>
          <p
            className={`mt-4 text-xs ${lastUpdate > 15 && active ? "text-warning" : "text-muted-foreground"}`}
          >
            Updated {lastUpdate}s ago
            {lastUpdate > 15 && active ? " · Updates delayed" : ""}
          </p>
        </div>
        {inspector ? (
          <aside
            ref={panel}
            tabIndex={-1}
            id="deployment-inspector"
            aria-label="Deployment details"
            className="min-w-0 border-t bg-muted/20 p-4 sm:p-6 lg:border-l lg:border-t-0"
            onKeyDown={(event) => {
              if (event.key === "Escape") close();
            }}
          >
            <div className="flex items-center justify-between gap-3">
              <h3 className="font-medium">Deployment details</h3>
              <Button variant="ghost" size="sm" onClick={close}>
                Close details
              </Button>
            </div>
            <div className="mt-4 flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
              <span className="break-all font-mono">{deployment.id}</span>
              <CopyButton
                value={deployment.id}
                label="Copy ID"
                ariaLabel="Copy deployment ID"
              />
            </div>
            {deployment.failureMessage ? (
              <p className="mt-4 whitespace-pre-wrap break-words text-xs text-destructive">
                {deployment.failureMessage}
              </p>
            ) : null}
            <h4 className="mb-2 mt-5 text-xs font-medium text-muted-foreground">
              Full deployment logs
            </h4>
            <pre
              aria-label="Full deployment logs"
              className="max-h-80 overflow-auto whitespace-pre-wrap break-words rounded-md border bg-background p-3 font-mono text-xs leading-6"
            >
              {orderedLogs.length
                ? orderedLogs
                    .map(
                      (log) =>
                        `${formatDeploymentTime(log.createdAt)} [${log.source}] ${log.content}`,
                    )
                    .join("\n")
                : "No logs recorded yet."}
            </pre>
            <h4 className="mb-2 mt-5 text-xs font-medium text-muted-foreground">
              Events
            </h4>
            <ol className="max-h-64 divide-y overflow-auto">
              {model.events.map((event) => (
                <li key={event.id} className="py-2 text-xs">
                  <time
                    className="tabular-nums text-muted-foreground"
                    dateTime={event.createdAt}
                  >
                    {formatDeploymentTime(event.createdAt)}
                  </time>
                  <p
                    className={`mt-1 whitespace-pre-wrap break-words ${event.level === "error" ? "text-destructive" : ""}`}
                  >
                    {event.message}
                  </p>
                  {event.metadataJson ? (
                    <pre className="mt-1 whitespace-pre-wrap break-words text-muted-foreground">
                      {event.metadataJson}
                    </pre>
                  ) : null}
                </li>
              ))}
            </ol>
          </aside>
        ) : null}
      </div>
    </section>
  );
}
