import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import {
  getVmResourceHistory, type DevelopmentWorkspaceVmResources, type VmResourceHistory, type VmResourceHistorySample,
} from "@/api/workspaceFilesystemApi";
import type { DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { formatClock } from "./WorkspaceVmResourceChart";
import { formatBytes, WorkspaceVmResources } from "./WorkspaceVmResources";

vi.mock("@/api/workspaceFilesystemApi", () => ({ getVmResourceHistory: vi.fn() }));

const GiB = 1024 ** 3;
const now = new Date("2026-01-01T10:30:00Z");

function vm(resources: Partial<DevelopmentWorkspaceVmResources> | null, overrides: Partial<DevelopmentWorkspaceVm> = {}): DevelopmentWorkspaceVm {
  return {
    id: "vm-1", kind: "primary", generation: 2, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "provider", region: "region", sku: "sku",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.1,
    estimatedMonthlyCompute: 72, computeReserved: true, revision: 1,
    resources: resources && {
      state: "current", observedAt: "2026-01-01T10:00:00Z", receivedAt: "2026-01-01T10:00:01Z",
      cpu: { logicalCount: 4, utilizationPercent: 37.4, windowSeconds: 15 },
      memory: { totalBytes: 8 * GiB, availableBytes: 3 * GiB },
      filesystems: [{ paths: ["/", "/workspace"], totalBytes: 100 * GiB, freeBytes: 45 * GiB, availableBytes: 40 * GiB,
        inodesTotal: 1000, inodesAvailable: 600, nestedMountsExcluded: 1 }],
      unavailable: {}, diskUsageSupported: true, directoryDeletionSupported: true, ...resources,
    },
    ...overrides,
  };
}

/** A sample observed 20 s into the given minute of 10:00 UTC (negative minutes fall before 10:00). */
function sample(minute: number, overrides: Partial<VmResourceHistorySample> = {}): VmResourceHistorySample {
  const start = Date.parse("2026-01-01T10:00:00Z") + minute * 60_000;
  return {
    minute: new Date(start).toISOString(), observedAt: new Date(start + 20_000).toISOString(),
    cpuUtilizationPercent: 10 + minute, memoryUsedBytes: 5 * GiB, memoryTotalBytes: 8 * GiB,
    workspaceDiskUsedBytes: 60 * GiB, workspaceDiskTotalBytes: 100 * GiB, ...overrides,
  };
}

function history(samples: VmResourceHistorySample[], overrides: Partial<VmResourceHistory> = {}): VmResourceHistory {
  return { vmId: "vm-1", generation: 2, windowStart: "2026-01-01T09:30:00Z", windowEnd: now.toISOString(), samples, ...overrides };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

const flush = () => act(async () => { await vi.advanceTimersByTimeAsync(0); });
const props = { workspaceId: "ws-1", organizationId: "org" };
const live = () => document.querySelector("[aria-live=polite]")!.textContent;
const summary = (slider: HTMLElement) => document.getElementById(slider.getAttribute("aria-describedby")!)!.textContent;

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(now);
  vi.mocked(getVmResourceHistory).mockReset();
  vi.mocked(getVmResourceHistory).mockResolvedValue(history([sample(27), sample(28), sample(29, { workspaceDiskUsedBytes: null })]));
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
});

it("shows current CPU, memory, disk and file slot availability with the sample time", async () => {
  render(<WorkspaceVmResources {...props} vm={vm({})} />);
  await flush();
  const section = screen.getByRole("region", { name: "Resources for dev environment vm-1" });
  expect(within(section).getByText("Current")).toBeTruthy();
  expect(within(section).getByText("37% of 4 cores over 15s")).toBeTruthy();
  expect(within(section).getByText("3.0 GiB available of 8.0 GiB")).toBeTruthy();
  expect(section.textContent).toContain("40.0 GiB available of 100 GiB");
  expect(section.textContent).toContain("600 of 1,000 file slots free");
  expect(section.textContent).toContain("Excludes 1 separately mounted folder");
  expect(section.textContent).toContain(new Date("2026-01-01T10:00:00Z").toLocaleString());
});

it("labels the retained volume and scratch storage when the image reports storage tiers", async () => {
  render(<WorkspaceVmResources {...props} vm={vm({ filesystems: [
    { paths: ["/"], roles: ["system", "scratch"], totalBytes: 30 * GiB, freeBytes: 20 * GiB, availableBytes: 18 * GiB },
    { paths: ["/workspace"], roles: ["workspace"], totalBytes: 100 * GiB, freeBytes: 45 * GiB, availableBytes: 40 * GiB,
      nestedMountsExcluded: 0, scratchBindMounts: 2 }] })} />);
  await flush();
  const section = screen.getByRole("region", { name: "Resources for dev environment vm-1" });
  expect(within(section).getByText("System disk /")).toBeTruthy();
  expect(within(section).getByText("Retained volume /workspace")).toBeTruthy();
  expect(section.textContent).toContain("Also holds scratch storage: caches and build outputs");
  expect(section.textContent).toContain("2 build output folders are kept on scratch storage, not counted here");
  expect(section.textContent).not.toContain("separately mounted");
});

it("marks stale and stopped samples instead of presenting them as current", async () => {
  const { rerender } = render(<WorkspaceVmResources {...props} vm={vm({ state: "stale" })} />);
  await flush();
  expect(screen.getAllByText("Stale").length).toBeGreaterThan(0);
  expect(screen.getByText(/No fresh sample for over 2 minutes\. Last sample at/)).toBeTruthy();
  expect(screen.getByText(/The chart shows the last readings received/)).toBeTruthy();
  rerender(<WorkspaceVmResources {...props} vm={vm({ state: "historical", cpu: { logicalCount: 4 } }, { phase: "stopped", desiredState: "stopped" })} />);
  expect(screen.getByText("Last known")).toBeTruthy();
  expect(screen.getByText(/is not running\. Values are from the last sample/)).toBeTruthy();
  expect(screen.getByText("4 cores · utilization not yet measured")).toBeTruthy();
  expect(screen.getByText("Historical")).toBeTruthy();
  expect(screen.getByText(/Historical: recorded before the dev environment stopped/)).toBeTruthy();
  expect(summary(screen.getByRole("slider"))).toContain("Readings are historical");
});

it("explains old images without fetching history and renders nothing without a sample while stopped", async () => {
  const { rerender, container } = render(<WorkspaceVmResources {...props}
    vm={vm({ state: "unsupported", filesystems: [], cpu: null, memory: null })} />);
  await flush();
  expect(screen.getByText("Not supported")).toBeTruthy();
  expect(screen.queryByText(/available of/)).toBeNull();
  expect(screen.queryByTestId("resource-history")).toBeNull();
  rerender(<WorkspaceVmResources {...props} vm={vm(null, { phase: "stopped", desiredState: "stopped" })} />);
  expect(container.textContent).toBe("");
  expect(getVmResourceHistory).not.toHaveBeenCalled();
});

it("waits for samples on a running VM that has not reported yet", async () => {
  vi.mocked(getVmResourceHistory).mockResolvedValue(history([]));
  render(<WorkspaceVmResources {...props} vm={vm(null)} />);
  expect(screen.getByText("Loading last-hour usage…")).toBeTruthy();
  await flush();
  expect(getVmResourceHistory).toHaveBeenCalledWith("ws-1", "vm-1", "org", expect.any(AbortSignal));
  expect(screen.getByText("Waiting for the first sample from this dev environment.")).toBeTruthy();
  expect(screen.getByText(/^Waiting for samples\./)).toBeTruthy();
  expect(screen.queryByText("Loading last-hour usage…")).toBeNull();
});

it("charts CPU by default and switches to memory and workspace disk with GiB axes and totals", async () => {
  render(<WorkspaceVmResources {...props} vm={vm({})} />);
  await flush();
  const metric = screen.getByRole("group", { name: "Chart metric" });
  expect(within(metric).getByRole("button", { name: "CPU" }).getAttribute("aria-pressed")).toBe("true");
  expect(screen.getByText("CPU (%), last hour")).toBeTruthy();
  const chart = screen.getByTestId("resource-history");
  expect(chart.textContent).toContain("100%");
  expect(chart.textContent).toContain("Latest 39%");
  expect(chart.textContent).toContain(`observed ${formatClock(sample(29).observedAt, true)}`);
  expect(chart.textContent).toContain(formatClock(now.getTime()));
  expect(chart.textContent).toContain(formatClock(now.getTime() - 60 * 60_000));

  fireEvent.click(within(metric).getByRole("button", { name: "Memory" }));
  expect(within(metric).getByRole("button", { name: "Memory" }).getAttribute("aria-pressed")).toBe("true");
  expect(screen.getByText("Memory (GiB), last hour")).toBeTruthy();
  expect(chart.textContent).toContain("8 GiB");
  expect(chart.textContent).toContain("Latest 5.0 GiB of 8.0 GiB");

  fireEvent.click(within(metric).getByRole("button", { name: "Workspace disk" }));
  expect(screen.getByText("Workspace disk (GiB), last hour")).toBeTruthy();
  expect(chart.textContent).toContain("100 GiB");
  expect(chart.textContent).toContain("Workspace disk unavailable (no /workspace reading) in the latest sample");
});

it("gives exact values by keyboard and pointer with an accessible summary", async () => {
  render(<WorkspaceVmResources {...props} vm={vm({})} />);
  await flush();
  const slider = screen.getByRole("slider", { name: "CPU (%), last hour" });
  expect(summary(slider)).toContain("CPU over the last 60 minutes ending");
  expect(summary(slider)).toContain("3 of 3 readings available, lowest 37%, highest 39%");
  slider.focus();
  fireEvent.keyDown(slider, { key: "ArrowLeft" });
  expect(live()).toBe(`${formatClock(sample(29).observedAt, true)} · 39%`);
  fireEvent.keyDown(slider, { key: "ArrowLeft" });
  expect(live()).toBe(`${formatClock(sample(28).observedAt, true)} · 38%`);
  expect(slider.getAttribute("aria-valuetext")).toBe(`${formatClock(sample(28).observedAt, true)}: 38%`);
  fireEvent.keyDown(slider, { key: "Home" });
  expect(live()).toBe(`${formatClock(sample(27).observedAt, true)} · 37%`);
  fireEvent.keyDown(slider, { key: "ArrowRight" });
  expect(slider.getAttribute("aria-valuenow")).toBe("1");
  fireEvent.click(screen.getByRole("button", { name: "Memory" }));
  fireEvent.keyDown(slider, { key: "End" });
  expect(live()).toBe(`${formatClock(sample(29).observedAt, true)} · 5.0 GiB of 8.0 GiB`);
  fireEvent.keyDown(slider, { key: "Escape" });
  expect(live()).toMatch(/use the arrow keys/);

  const svg = slider.querySelector("svg")!;
  svg.getBoundingClientRect = () => ({ left: 0, width: 600, top: 0, height: 168, right: 600, bottom: 168, x: 0, y: 0, toJSON: () => ({}) });
  fireEvent.pointerMove(svg, { clientX: 0 });
  expect(live()).toBe(`${formatClock(sample(27).observedAt, true)} · 5.0 GiB of 8.0 GiB`);
});

it("breaks the line for missing minutes and unknown values and plots single readings as points", async () => {
  vi.mocked(getVmResourceHistory).mockResolvedValue(history([
    sample(0), sample(1), sample(3), sample(5, { cpuUtilizationPercent: null }), sample(6), sample(7),
  ]));
  const { container } = render(<WorkspaceVmResources {...props} vm={vm({})} />);
  await flush();
  expect(container.querySelectorAll("[data-segment=line]")).toHaveLength(2);
  expect(container.querySelectorAll("[data-segment=point]")).toHaveLength(1);
  const slider = screen.getByRole("slider");
  expect(summary(slider)).toContain("5 of 6 readings available");
  expect(summary(slider)).toContain("2 breaks for missing or unknown readings");
  fireEvent.keyDown(slider, { key: "End" });
  fireEvent.keyDown(slider, { key: "ArrowLeft" });
  fireEvent.keyDown(slider, { key: "ArrowLeft" });
  expect(live()).toBe(`${formatClock(sample(5).observedAt, true)} · CPU not yet measured`);
});

it("plots a lone reading as a point", async () => {
  vi.mocked(getVmResourceHistory).mockResolvedValue(history([sample(29)]));
  const { container } = render(<WorkspaceVmResources {...props} vm={vm({})} />);
  await flush();
  expect(container.querySelectorAll("[data-segment=point]")).toHaveLength(1);
  expect(container.querySelectorAll("[data-segment=line]")).toHaveLength(0);
});

it("lets historical points age out of the rolling window after stop", async () => {
  vi.mocked(getVmResourceHistory).mockResolvedValue(history([sample(-29)]));
  render(<WorkspaceVmResources {...props} vm={vm({ state: "historical" }, { phase: "stopped", desiredState: "stopped" })} />);
  await flush();
  expect(screen.getByRole("slider")).toBeTruthy();
  await act(async () => { await vi.advanceTimersByTimeAsync(120_000); });
  expect(screen.queryByRole("slider")).toBeNull();
  expect(screen.getByText("No usage recorded in the last hour. The dev environment is not running.")).toBeTruthy();
});

it("keeps fetch errors distinct and keeps the last loaded readings", async () => {
  vi.mocked(getVmResourceHistory).mockRejectedValueOnce(new Error("Service unavailable"));
  render(<WorkspaceVmResources {...props} vm={vm({})} />);
  await flush();
  expect(screen.getByRole("alert").textContent).toBe("Couldn't load usage history: Service unavailable");
  await act(async () => { await vi.advanceTimersByTimeAsync(60_000); });
  expect(screen.queryByRole("alert")).toBeNull();
  expect(screen.getByRole("slider")).toBeTruthy();
  vi.mocked(getVmResourceHistory).mockRejectedValueOnce(new Error("Timed out"));
  await act(async () => { await vi.advanceTimersByTimeAsync(60_000); });
  expect(screen.getByRole("alert").textContent).toContain("Couldn't refresh usage history: Timed out");
  expect(screen.getByRole("slider")).toBeTruthy();
});

it("starts a fresh series for a new generation", async () => {
  const { rerender } = render(<WorkspaceVmResources {...props} vm={vm({})} />);
  await flush();
  expect(screen.getByRole("slider")).toBeTruthy();
  const next = deferred<VmResourceHistory>();
  vi.mocked(getVmResourceHistory).mockReturnValueOnce(next.promise);
  rerender(<WorkspaceVmResources {...props} vm={vm({}, { generation: 3 })} />);
  expect(screen.queryByRole("slider")).toBeNull();
  expect(screen.getByText("Loading last-hour usage…")).toBeTruthy();
  await act(async () => { next.resolve(history([sample(29, { cpuUtilizationPercent: 80 })], { generation: 3 })); });
  expect(summary(screen.getByRole("slider"))).toContain("1 of 1 readings available");
  expect(screen.getByTestId("resource-history").textContent).toContain("Latest 80%");
});

it("ignores a slow response for the previous VM or generation", async () => {
  const first = deferred<VmResourceHistory>();
  const second = deferred<VmResourceHistory>();
  vi.mocked(getVmResourceHistory).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const { rerender } = render(<WorkspaceVmResources {...props} vm={vm({})} />);
  rerender(<WorkspaceVmResources {...props} vm={vm({}, { id: "vm-2" })} />);
  expect(vi.mocked(getVmResourceHistory).mock.calls.map(call => call[1])).toEqual(["vm-1", "vm-2"]);
  expect(vi.mocked(getVmResourceHistory).mock.calls[0][3]!.aborted).toBe(true);
  await act(async () => { second.resolve(history([sample(29, { cpuUtilizationPercent: 72 })], { vmId: "vm-2" })); });
  await act(async () => { first.resolve(history([sample(28, { cpuUtilizationPercent: 5 })])); });
  const chart = screen.getByTestId("resource-history");
  expect(chart.textContent).toContain("Latest 72%");
  expect(chart.textContent).not.toContain("5%");

  vi.mocked(getVmResourceHistory).mockResolvedValueOnce(history([sample(29, { cpuUtilizationPercent: 1 })], { vmId: "vm-2", generation: 1 }));
  await act(async () => { await vi.advanceTimersByTimeAsync(60_000); });
  expect(chart.textContent).toContain("Latest 72%");
});

it("refreshes about once a minute only while visible and active", async () => {
  const { rerender } = render(<WorkspaceVmResources {...props} vm={vm({})} />);
  await flush();
  expect(getVmResourceHistory).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(59_000); });
  expect(getVmResourceHistory).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(1_000); });
  expect(getVmResourceHistory).toHaveBeenCalledTimes(2);

  Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
  act(() => { document.dispatchEvent(new Event("visibilitychange")); });
  await act(async () => { await vi.advanceTimersByTimeAsync(180_000); });
  expect(getVmResourceHistory).toHaveBeenCalledTimes(2);
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
  act(() => { document.dispatchEvent(new Event("visibilitychange")); });
  await flush();
  expect(getVmResourceHistory).toHaveBeenCalledTimes(3);

  rerender(<WorkspaceVmResources {...props} vm={vm({})} active={false} />);
  await act(async () => { await vi.advanceTimersByTimeAsync(180_000); });
  expect(getVmResourceHistory).toHaveBeenCalledTimes(3);
});

it("formats byte sizes without reporting unknown values as zero", () => {
  expect(formatBytes(null)).toBe("Unknown");
  expect(formatBytes(512)).toBe("512 B");
  expect(formatBytes(1536)).toBe("1.5 KiB");
});
