import { useEffect, useId, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { getVmResourceHistory, type VmResourceHistory, type VmResourceHistorySample } from "@/api/workspaceFilesystemApi";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { DevelopmentWorkspaceVm } from "@/models/controlPlane";

export const historyRefreshMs = 60_000;
const windowMs = 60 * 60_000;
const minuteMs = 60_000;
const GiB = 1024 ** 3;
const width = 600;
const height = 168;
const plot = { left: 52, right: 8, top: 8, bottom: 24 };
const plotWidth = width - plot.left - plot.right;
const plotHeight = height - plot.top - plot.bottom;

type Metric = "cpu" | "memory" | "disk";
type MetricDefinition = {
  label: string; axis: string; unavailable: string;
  value: (sample: VmResourceHistorySample) => number | null;
  total: (sample: VmResourceHistorySample) => number | null;
};

const gib = (bytes: number | null | undefined) => bytes == null ? null : bytes / GiB;
const metrics: Record<Metric, MetricDefinition> = {
  cpu: { label: "CPU", axis: "CPU (%)", unavailable: "not yet measured",
    value: sample => sample.cpuUtilizationPercent ?? null, total: () => null },
  memory: { label: "Memory", axis: "Memory (GiB)", unavailable: "unavailable",
    value: sample => gib(sample.memoryUsedBytes), total: sample => gib(sample.memoryTotalBytes) },
  disk: { label: "Workspace disk", axis: "Workspace disk (GiB)", unavailable: "unavailable (no /workspace reading)",
    value: sample => gib(sample.workspaceDiskUsedBytes), total: sample => gib(sample.workspaceDiskTotalBytes) },
};

export function formatClock(value: string | number, seconds = false) {
  return new Date(value).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", ...(seconds ? { second: "2-digit" } : {}) });
}

function formatValue(metric: Metric, value: number) {
  return metric === "cpu" ? `${value.toFixed(0)}%` : `${value.toFixed(1)} GiB`;
}

function readingText(metric: Metric, sample: VmResourceHistorySample) {
  const definition = metrics[metric];
  const value = definition.value(sample);
  if (value == null) return `${definition.label} ${definition.unavailable}`;
  const total = definition.total(sample);
  return total == null ? formatValue(metric, value) : `${formatValue(metric, value)} of ${formatValue(metric, total)}`;
}

/** Splits samples into drawable runs, breaking on missing minutes and unknown values. */
export function segmentSamples(samples: VmResourceHistorySample[], value: (sample: VmResourceHistorySample) => number | null) {
  const segments: VmResourceHistorySample[][] = [];
  let current: VmResourceHistorySample[] = [];
  let previousMinute: number | null = null;
  for (const sample of samples) {
    const minute = Date.parse(sample.minute);
    const gap = previousMinute != null && minute - previousMinute > minuteMs;
    previousMinute = minute;
    if (value(sample) == null || gap) {
      if (current.length) segments.push(current);
      current = [];
    }
    if (value(sample) != null) current.push(sample);
  }
  if (current.length) segments.push(current);
  return segments;
}

type Loaded = { key: string; data: VmResourceHistory; error: string | null };
type Failed = { key: string; error: string };

function useVmResourceHistory(workspaceId: string, organizationId: string, vm: DevelopmentWorkspaceVm, enabled: boolean) {
  const key = `${organizationId}:${workspaceId}:${vm.id}:${vm.generation}`;
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [failed, setFailed] = useState<Failed | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const request = useRef(0);

  useEffect(() => {
    if (!enabled) return;
    let controller: AbortController | null = null;
    let timer: ReturnType<typeof setInterval> | null = null;
    const { id: vmId, generation } = vm;
    const load = () => {
      controller?.abort();
      const own = new AbortController();
      controller = own;
      const token = ++request.current;
      getVmResourceHistory(workspaceId, vmId, organizationId, own.signal).then(data => {
        if (token !== request.current || data.vmId !== vmId || data.generation !== generation) return;
        setLoaded({ key, data, error: null });
        setFailed(null);
        setNow(Date.now());
      }, (reason: unknown) => {
        if (token !== request.current || own.signal.aborted) return;
        const message = reason instanceof Error ? reason.message : "Usage history is unavailable.";
        setLoaded(previous => previous?.key === key ? { ...previous, error: message } : previous);
        setFailed({ key, error: message });
        setNow(Date.now());
      });
    };
    const stop = () => {
      if (timer) clearInterval(timer);
      timer = null;
      controller?.abort();
      ++request.current;
    };
    const start = () => {
      stop();
      setNow(Date.now());
      load();
      timer = setInterval(() => { setNow(Date.now()); load(); }, historyRefreshMs);
    };
    const onVisibility = () => document.visibilityState === "hidden" ? stop() : start();
    if (document.visibilityState !== "hidden") start();
    document.addEventListener("visibilitychange", onVisibility);
    return () => { document.removeEventListener("visibilitychange", onVisibility); stop(); };
    // vm is read only for id and generation, which are part of key.
  }, [enabled, key]);

  const data = loaded?.key === key ? loaded : null;
  const error = data ? data.error : failed?.key === key ? failed.error : null;
  return { data: data?.data ?? null, error, now };
}

export function WorkspaceVmResourceChart({ workspaceId, organizationId, vm, active }: {
  workspaceId: string; organizationId: string; vm: DevelopmentWorkspaceVm; active: boolean;
}) {
  const [metric, setMetric] = useState<Metric>("cpu");
  const [cursor, setCursor] = useState<string | null>(null);
  const summaryId = useId();
  const titleId = useId();
  const { data, error, now } = useVmResourceHistory(workspaceId, organizationId, vm, active);
  useEffect(() => { setCursor(null); }, [vm.id, vm.generation]);

  const state = vm.resources?.state;
  const historical = state === "historical" || (!vm.resources && vm.phase !== "ready");
  const windowStart = now - windowMs;
  const samples = (data?.samples ?? []).filter(sample => Date.parse(sample.observedAt) > windowStart)
    .sort((a, b) => Date.parse(a.observedAt) - Date.parse(b.observedAt));
  const definition = metrics[metric];

  let body;
  if (!data && error) body = <p role="alert" className="text-sm text-destructive">Couldn't load usage history: {error}</p>;
  else if (!data) body = <p role="status" className="text-sm text-muted-foreground">Loading last-hour usage…</p>;
  else if (!samples.length) body = <p role="status" className="text-sm text-muted-foreground">{historical
    ? "No usage recorded in the last hour. The dev environment is not running."
    : "Waiting for samples. Usage appears here about once a minute while the dev environment runs."}</p>;
  else {
    const index = cursor == null ? -1 : samples.findIndex(sample => sample.minute === cursor);
    const selected = index >= 0 ? samples[index] : null;
    const latest = samples[samples.length - 1];
    const values = samples.map(definition.value).filter((value): value is number => value != null);
    const totals = samples.map(definition.total).filter((value): value is number => value != null);
    const max = metric === "cpu" ? 100 : Math.max(1, Math.ceil(Math.max(0, ...values, ...totals)));
    const x = (sample: VmResourceHistorySample) =>
      plot.left + Math.min(1, Math.max(0, (Date.parse(sample.observedAt) - windowStart) / windowMs)) * plotWidth;
    const y = (value: number) => plot.top + plotHeight * (1 - Math.min(1, Math.max(0, value / max)));
    const segments = segmentSamples(samples, definition.value);
    const breaks = Math.max(0, segments.length - 1);
    const ticks = [0, max / 2, max];
    const timeTicks = [windowStart, now - windowMs / 2, now];
    const latestText = definition.value(latest) == null
      ? `${definition.label} ${definition.unavailable} in the latest sample`
      : `Latest ${readingText(metric, latest)}`;
    const summary = `${definition.label} over the last 60 minutes ending ${formatClock(now)}: `
      + `${values.length} of ${samples.length} readings available`
      + (values.length ? `, lowest ${formatValue(metric, Math.min(...values))}, highest ${formatValue(metric, Math.max(...values))}` : "")
      + `. ${latestText}, observed ${formatClock(latest.observedAt, true)}.`
      + (breaks ? ` The line has ${breaks} break${breaks === 1 ? "" : "s"} for missing or unknown readings.` : "")
      + (historical ? " Readings are historical; the dev environment is not running." : "");
    const move = (next: number) => setCursor(samples[Math.min(samples.length - 1, Math.max(0, next))].minute);
    const onKeyDown = (event: KeyboardEvent) => {
      const current = index >= 0 ? index : samples.length;
      const next = { ArrowLeft: current - 1, ArrowDown: current - 1, ArrowRight: index >= 0 ? index + 1 : samples.length - 1,
        ArrowUp: index >= 0 ? index + 1 : samples.length - 1, Home: 0, End: samples.length - 1 }[event.key];
      if (next != null) { event.preventDefault(); move(next); }
      else if (event.key === "Escape") setCursor(null);
    };
    const onPointerMove = (event: PointerEvent<SVGSVGElement>) => {
      const rect = event.currentTarget.getBoundingClientRect();
      if (!rect.width) return;
      const position = (event.clientX - rect.left) / rect.width * width;
      let nearest = 0;
      samples.forEach((sample, i) => { if (Math.abs(x(sample) - position) < Math.abs(x(samples[nearest]) - position)) nearest = i; });
      move(nearest);
    };
    const selectedValue = selected && definition.value(selected);
    body = <>
      <p className="text-sm"><span className="font-medium">{latestText}</span>
        <span className="text-muted-foreground"> · observed {formatClock(latest.observedAt, true)}</span></p>
      <div role="slider" tabIndex={0} aria-labelledby={titleId} aria-describedby={summaryId}
        aria-valuemin={0} aria-valuemax={samples.length - 1} aria-valuenow={index >= 0 ? index : samples.length - 1}
        aria-valuetext={`${formatClock((selected ?? latest).observedAt, true)}: ${readingText(metric, selected ?? latest)}`}
        onKeyDown={onKeyDown} onBlur={() => setCursor(null)}
        className="rounded-md focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring">
        <svg viewBox={`0 0 ${width} ${height}`} className="h-auto w-full touch-none select-none" aria-hidden="true"
          onPointerMove={onPointerMove} onPointerLeave={() => setCursor(null)}>
          {ticks.map(tick => <g key={tick}>
            <line x1={plot.left} x2={width - plot.right} y1={y(tick)} y2={y(tick)} className="stroke-border" strokeWidth={1} />
            <text x={plot.left - 6} y={y(tick)} textAnchor="end" dominantBaseline="middle" className="fill-muted-foreground text-[10px]">
              {metric === "cpu" ? `${tick.toFixed(0)}%` : `${Number.isInteger(tick) ? tick : tick.toFixed(1)} GiB`}</text>
          </g>)}
          {timeTicks.map((tick, i) => <text key={i} x={plot.left + plotWidth * i / 2} y={height - 6}
            textAnchor={i === 0 ? "start" : i === 2 ? "end" : "middle"} className="fill-muted-foreground text-[10px]">{formatClock(tick)}</text>)}
          {segments.map(segment => segment.length === 1
            ? <circle key={segment[0].minute} data-segment="point" cx={x(segment[0])} cy={y(definition.value(segment[0])!)} r={3}
              className={historical ? "fill-muted-foreground" : "fill-primary"} />
            : <path key={segment[0].minute} data-segment="line" fill="none" strokeWidth={2} strokeLinejoin="round"
              className={historical ? "stroke-muted-foreground" : "stroke-primary"} strokeDasharray={historical ? "4 3" : undefined}
              d={segment.map((sample, i) => `${i ? "L" : "M"}${x(sample).toFixed(1)} ${y(definition.value(sample)!).toFixed(1)}`).join(" ")} />)}
          {selected && <g data-cursor="">
            <line x1={x(selected)} x2={x(selected)} y1={plot.top} y2={plot.top + plotHeight} className="stroke-foreground/40" strokeWidth={1} />
            {selectedValue != null && <circle cx={x(selected)} cy={y(selectedValue)} r={4} className="fill-background stroke-foreground" strokeWidth={2} />}
          </g>}
        </svg>
      </div>
      <p aria-live="polite" className="min-h-5 text-xs text-muted-foreground">
        {selected ? `${formatClock(selected.observedAt, true)} · ${readingText(metric, selected)}`
          : "Hover or focus the chart and use the arrow keys to read exact values."}</p>
      <p id={summaryId} className="sr-only">{summary}</p>
    </>;
  }

  return <div className="mt-3 space-y-2" data-testid="resource-history">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h4 id={titleId} className="flex flex-wrap items-center gap-2 text-xs font-medium text-muted-foreground">
        {definition.axis}, last hour
        {data && historical && <Badge variant="outline">Historical</Badge>}
        {data && !historical && state === "stale" && <Badge variant="outline">Stale</Badge>}
      </h4>
      <div role="group" aria-label="Chart metric" className="inline-flex rounded-md border p-0.5">
        {(Object.keys(metrics) as Metric[]).map(option => <button key={option} type="button" aria-pressed={metric === option}
          onClick={() => setMetric(option)}
          className={cn("rounded px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
            metric === option ? "bg-secondary font-medium text-secondary-foreground" : "text-muted-foreground hover:text-foreground")}>
          {metrics[option].label}</button>)}
      </div>
    </div>
    {data && historical && samples.length > 0 && <p className="text-xs text-muted-foreground">Historical: recorded before the dev environment stopped. Points leave the chart as they pass one hour old.</p>}
    {data && !historical && state === "stale" && <p className="text-xs text-muted-foreground">No fresh sample for over 2 minutes. The chart shows the last readings received.</p>}
    {data && error && <p role="alert" className="text-xs text-destructive">Couldn't refresh usage history: {error}. Showing the last loaded readings.</p>}
    {body}
  </div>;
}
