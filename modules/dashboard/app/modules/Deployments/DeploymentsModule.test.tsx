import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { listApps } from "@/api/appsApi";
import { listDeployments, refreshDeployment } from "@/api/deploymentsApi";
import type { DeploymentResponse } from "@/models/controlPlane";
import { DeploymentsModule } from "./DeploymentsModule";
import { deploymentCreatedTime, deploymentListDuration } from "./deploymentListPresentation";
import { deploymentProgress } from "./deploymentProgressPresentation";

vi.mock("@/api/appsApi", () => ({ listApps: vi.fn() }));
vi.mock("@/api/deploymentsApi", () => ({
  listDeployments: vi.fn(),
  refreshDeployment: vi.fn(),
}));
vi.mock("@/core/auth/AuthProvider", () => ({
  useAuth: () => ({ selectedOrganization: { id: "org_duration" } }),
}));

const deployment: DeploymentResponse = {
  id: "dep_duration",
  appId: "app_duration",
  organizationId: "org_duration",
  database: "sqlite",
  status: "deploying",
  consoleUrl: "https://console.example.test",
  services: [],
  createdAt: "2026-10-27T12:00:00Z",
  startedAt: "2026-10-27T12:01:00Z",
};
const start = Date.parse(deployment.startedAt!);

it("includes local build and queue time after the worker starts and finishes", () => {
  const localBuild = {
    ...deployment,
    createdAt: "2026-11-07T02:31:09Z",
    startedAt: "2026-11-07T02:36:09Z",
  };
  const completedAt = "2026-11-07T02:37:26Z";
  const now = Date.parse(completedAt);
  for (const status of ["waiting_for_build", "queued", "provisioning", "deploying", "verifying"] as const) {
    expect(deploymentListDuration({ ...localBuild, status }, now)).toBe("6m elapsed");
  }
  for (const status of ["succeeded", "failed", "canceled"] as const) {
    expect(deploymentListDuration({ ...localBuild, status, completedAt }, now)).toBe("6m");
  }
});

it("counts only the current retry, including its queue wait, in the list and detail", () => {
  const retried = {
    ...deployment,
    requeuedAt: "2026-11-07T02:31:09Z",
    startedAt: "2026-11-07T02:36:09Z",
  };
  const completedAt = "2026-11-07T02:37:26Z";
  const now = Date.parse(completedAt);
  expect(deploymentListDuration({ ...retried, status: "queued", startedAt: null }, now)).toBe("6m elapsed");
  expect(deploymentListDuration(retried, now)).toBe("6m elapsed");
  expect(deploymentListDuration({ ...retried, status: "succeeded", completedAt }, now)).toBe("6m");
  // Both surfaces use the deployment response even while event history is loading.
  expect(deploymentProgress(retried, [], []).totalStartedAt).toBe(retried.requeuedAt);
});

beforeEach(() => { vi.clearAllMocks(); });

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  sessionStorage.clear();
});

it.each([
  [0, "<1m"],
  [59_999, "<1m"],
  [60_000, "1m"],
  [499_000, "8m"],
  [3_600_000, "1h"],
  [4_320_000, "1h 12m"],
])("formats completed duration %i without seconds", (elapsed, expected) => {
  expect(deploymentListDuration({
    ...deployment,
    status: "succeeded",
    completedAt: new Date(Date.parse(deployment.createdAt) + elapsed).toISOString(),
  }, start + 9_000_000)).toBe(expected);
});

it("uses creation time while waiting for a local build or worker and handles missing or invalid timing", () => {
  expect(deploymentListDuration({ ...deployment, status: "waiting_for_build", startedAt: null }, start)).toBe("1m elapsed");
  expect(deploymentListDuration({ ...deployment, status: "queued", startedAt: null }, start)).toBe("1m elapsed");
  expect(deploymentListDuration({ ...deployment, status: "failed" }, start)).toBe("Unavailable");
  expect(deploymentListDuration({ ...deployment, createdAt: "invalid" }, start)).toBe("Unavailable");
  expect(deploymentListDuration({ ...deployment, requeuedAt: "invalid" }, start)).toBe("Unavailable");
  expect(deploymentListDuration(deployment, Date.parse(deployment.createdAt) - 1_000)).toBe("<1m elapsed");
  expect(deploymentListDuration(deployment, start - 1_000)).toBe("<1m elapsed");
});

it("ticks active rows without refetching and freezes after completion", async () => {
  vi.useFakeTimers();
  vi.setSystemTime(start + 59_000);
  vi.mocked(listApps).mockResolvedValue([]);
  vi.mocked(listDeployments).mockResolvedValue([deployment]);
  vi.mocked(refreshDeployment).mockImplementation(async () => {
    const completed = { ...deployment, status: "succeeded" as const, completedAt: new Date(start + 61_000).toISOString() };
    vi.mocked(listDeployments).mockResolvedValue([completed]);
    return completed;
  });
  await act(async () => {
    render(<MemoryRouter><DeploymentsModule /></MemoryRouter>);
  });
  expect(screen.getByText("1m elapsed")).toBeInTheDocument();
  act(() => vi.advanceTimersByTime(2_000));
  expect(screen.getByText("2m elapsed")).toBeInTheDocument();
  expect(listDeployments).toHaveBeenCalledTimes(1);
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Refresh dep_duration" }));
  });
  expect(screen.getByText("2m")).toBeInTheDocument();
  await act(async () => { await vi.advanceTimersByTimeAsync(120_000); });
  expect(screen.getByText("2m")).toBeInTheDocument();
  expect(vi.getTimerCount()).toBe(2);
});

it.each([
  [0, "Less than a minute ago"],
  [59_999, "Less than a minute ago"],
  [60_000, "1 minute ago"],
  [3_599_999, "59 minutes ago"],
  [3_600_000, "1 hour ago"],
  [86_399_999, "23 hours ago"],
])("shows relative creation time for age %i", (age, label) => {
  expect(deploymentCreatedTime(deployment.createdAt, Date.parse(deployment.createdAt) + age).label).toBe(label);
});

it("keeps older and future dates absolute and formats the hover timestamp in the browser timezone", () => {
  const date = new Date(deployment.createdAt);
  const absolute = new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium", timeStyle: "short",
  }).format(date);
  for (const age of [-1, 86_400_000]) {
    const result = deploymentCreatedTime(deployment.createdAt, date.getTime() + age);
    expect(result.label).toBe(absolute);
    expect(result.timestamp).toBe(new Intl.DateTimeFormat(undefined, {
      year: "numeric", month: "short", day: "numeric", hour: "numeric",
      minute: "2-digit", second: "2-digit", timeZoneName: "short",
    }).format(date));
  }
  expect(deploymentCreatedTime("invalid", start).label).toBe("Unavailable");
});

it("updates completed deployment ages and switches to an absolute date at 24 hours", async () => {
  vi.useFakeTimers();
  const createdAt = Date.parse(deployment.createdAt);
  vi.setSystemTime(createdAt + 59_000);
  vi.mocked(listApps).mockResolvedValue([]);
  vi.mocked(listDeployments).mockResolvedValue([{
    ...deployment, status: "succeeded", completedAt: deployment.startedAt,
  }]);
  await act(async () => {
    render(<MemoryRouter><DeploymentsModule /></MemoryRouter>);
  });
  const time = screen.getByText("Less than a minute ago");
  expect(time).toHaveAttribute("datetime", deployment.createdAt);
  expect(time).toHaveAttribute("title", deploymentCreatedTime(deployment.createdAt, Date.now()).timestamp);
  act(() => vi.advanceTimersByTime(1_000));
  expect(screen.getByText("1 minute ago")).toBeInTheDocument();
  vi.setSystemTime(createdAt + 86_399_000);
  act(() => vi.advanceTimersByTime(1_000));
  expect(screen.queryByText(/ago$/)).not.toBeInTheDocument();
  expect(time).toHaveTextContent(deploymentCreatedTime(deployment.createdAt, Date.now()).label);
  expect(vi.getTimerCount()).toBe(1);
});

it("shows local builds in deployment history with an active status and elapsed duration", async () => {
  vi.useFakeTimers();
  vi.setSystemTime(start);
  vi.mocked(listApps).mockResolvedValue([]);
  vi.mocked(listDeployments).mockResolvedValue([{
    ...deployment, status: "waiting_for_build", startedAt: null,
  }]);
  await act(async () => {
    render(<MemoryRouter><DeploymentsModule /></MemoryRouter>);
  });
  expect(screen.getByText("Waiting for build")).toHaveClass("text-warning");
  expect(screen.getByText("1m elapsed")).toBeInTheDocument();
  act(() => vi.advanceTimersByTime(60_000));
  expect(screen.getByText("2m elapsed")).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Filter & sort" }));
  fireEvent.keyDown(screen.getByRole("combobox", { name: "Status" }), { key: "ArrowDown" });
  expect(screen.getByRole("option", { name: "Waiting for build" })).toBeInTheDocument();
});


it("automatically discovers a new CLI build while the old failure history is open", async () => {
  vi.useFakeTimers();
  vi.mocked(listApps).mockResolvedValue([]);
  vi.mocked(listDeployments).mockResolvedValue([{ ...deployment, status: "failed" }]);
  await act(async () => { render(<MemoryRouter><DeploymentsModule /></MemoryRouter>); });
  expect(screen.getByText("failed")).toBeInTheDocument();
  vi.mocked(listDeployments).mockResolvedValue([{ ...deployment, id: "new-build", status: "waiting_for_build" }]);
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(screen.getByText("Waiting for build")).toBeInTheDocument();
  expect(screen.getByText("new-build")).toBeInTheDocument();
  expect(listApps).toHaveBeenCalledTimes(1);
});
