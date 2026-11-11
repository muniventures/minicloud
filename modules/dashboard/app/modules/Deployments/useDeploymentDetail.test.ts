import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import {
  getDeployment,
  listDeploymentActivities,
  listDeploymentEvents,
  listDeploymentLogs,
  refreshDeployment,
  dispatchDeployment,
  cancelDeployment,
} from "@/api/deploymentsApi";
import type { DeploymentResponse } from "@/models/controlPlane";
import { useDeploymentDetail } from "./useDeploymentDetail";

vi.mock("@/api/deploymentsApi", () => ({
  getDeployment: vi.fn(),
  listDeploymentActivities: vi.fn(),
  listDeploymentEvents: vi.fn(),
  listDeploymentLogs: vi.fn(),
  refreshDeployment: vi.fn(),
  dispatchDeployment: vi.fn(),
  cancelDeployment: vi.fn(),
}));
const ref = { organizationId: "org-1", number: "20261110.1" };
const deployment = {
  id: "dep",
  number: "20261110.1",
  appId: "app",
  status: "deploying",
} as DeploymentResponse;
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(getDeployment).mockResolvedValue(deployment);
  vi.mocked(listDeploymentActivities).mockResolvedValue([]);
  vi.mocked(listDeploymentEvents).mockResolvedValue([]);
  vi.mocked(listDeploymentLogs).mockResolvedValue([]);
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

it.each(["deploying", "waiting_for_build"] as const)("polls %s deployments, serializes refresh, preserves failed observations, and stops on completion", async (status) => {
  const activeDeployment = { ...deployment, status };
  vi.mocked(getDeployment).mockResolvedValue(activeDeployment);
  vi.useFakeTimers();
  const { result } = renderHook(() => useDeploymentDetail(ref));
  await act(async () => {});
  expect(result.current.snapshot?.deployment.id).toBe("dep");
  let resolveRead!: (value: DeploymentResponse) => void;
  vi.mocked(getDeployment).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        resolveRead = resolve;
      }),
  );
  await act(async () => {
    await vi.advanceTimersByTimeAsync(3000);
  });
  expect(getDeployment).toHaveBeenCalledTimes(2);
  let refresh!: Promise<unknown>;
  act(() => {
    refresh = result.current.refresh();
  });
  expect(refreshDeployment).not.toHaveBeenCalled();
  await act(async () => {
    resolveRead(activeDeployment);
    await refresh;
  });
  expect(refreshDeployment).toHaveBeenCalledTimes(1);
  expect(getDeployment).toHaveBeenCalledTimes(3);
  vi.mocked(listDeploymentLogs).mockRejectedValueOnce(new Error("Offline"));
  await act(async () => {
    await vi.advanceTimersByTimeAsync(3000);
  });
  expect(result.current.error).toBe("Offline");
  expect(result.current.snapshot?.deployment).toEqual(activeDeployment);
  vi.mocked(getDeployment).mockResolvedValue({
    ...deployment,
    status: "succeeded",
  });
  await act(async () => {
    await vi.advanceTimersByTimeAsync(3000);
  });
  const count = vi.mocked(getDeployment).mock.calls.length;
  expect(result.current.snapshot?.deployment.status).toBe("succeeded");
  await act(async () => {
    await vi.advanceTimersByTimeAsync(9000);
  });
  expect(getDeployment).toHaveBeenCalledTimes(count);
});

it("aborts old reads and ignores stale responses after route changes", async () => {
  let resolveOld!: (value: DeploymentResponse) => void;
  vi.mocked(getDeployment).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        resolveOld = resolve;
      }),
  );
  const { result, rerender, unmount } = renderHook(
    ({ number }) => useDeploymentDetail({ organizationId: "org-1", number }),
    { initialProps: { number: "20261110.1" } },
  );
  const signal = vi.mocked(getDeployment).mock.calls[0][1]!;
  vi.mocked(getDeployment).mockResolvedValue({ ...deployment, id: "new" });
  rerender({ number: "20261110.2" });
  await waitFor(() =>
    expect(result.current.snapshot?.deployment.id).toBe("new"),
  );
  expect(signal.aborted).toBe(true);
  await act(async () => resolveOld({ ...deployment, id: "old" }));
  expect(result.current.snapshot?.deployment.id).toBe("new");
  const lastSignal = vi.mocked(getDeployment).mock.calls.at(-1)![1]!;
  unmount();
  expect(lastSignal.aborted).toBe(true);
});

it("requests cancellation, keeps polling while stopping, and stops after cancellation completes", async () => {
  vi.useFakeTimers();
  const { result } = renderHook(() => useDeploymentDetail(ref));
  await act(async () => {});
  vi.mocked(getDeployment).mockResolvedValue({ ...deployment, cancellationRequestedAt: "2026-10-27T12:00:00Z" });
  await act(async () => { await result.current.cancel(); });
  expect(cancelDeployment).toHaveBeenCalledWith(ref);
  expect(result.current.snapshot?.deployment.cancellationRequestedAt).toBeTruthy();
  const count = vi.mocked(getDeployment).mock.calls.length;
  await act(async () => { await vi.advanceTimersByTimeAsync(3000); });
  expect(getDeployment).toHaveBeenCalledTimes(count + 1);
  vi.mocked(getDeployment).mockResolvedValue({ ...deployment, status: "canceled" });
  await act(async () => { await vi.advanceTimersByTimeAsync(3000); });
  const terminalCount = vi.mocked(getDeployment).mock.calls.length;
  await act(async () => { await vi.advanceTimersByTimeAsync(9000); });
  expect(getDeployment).toHaveBeenCalledTimes(terminalCount);
});

it("never shows or targets a stale organization when both organizations contain the same number", async () => {
  let resolveOrgOne!: (value: DeploymentResponse) => void;
  vi.mocked(getDeployment).mockImplementationOnce(
    () => new Promise((resolve) => { resolveOrgOne = resolve; }),
  );
  const { result, rerender } = renderHook(
    ({ organizationId }) => useDeploymentDetail({ organizationId, number: "20261110.1" }),
    { initialProps: { organizationId: "org-1" } },
  );
  const orgOneSignal = vi.mocked(getDeployment).mock.calls[0][1]!;
  expect(vi.mocked(getDeployment).mock.calls[0][0]).toEqual({ organizationId: "org-1", number: "20261110.1" });
  vi.mocked(getDeployment).mockResolvedValue({ ...deployment, id: "org-2-dep", organizationId: "org-2" });
  rerender({ organizationId: "org-2" });
  expect(result.current.snapshot).toBeNull();
  await waitFor(() => expect(result.current.snapshot?.deployment.id).toBe("org-2-dep"));
  expect(orgOneSignal.aborted).toBe(true);
  await act(async () => resolveOrgOne({ ...deployment, id: "org-1-dep", organizationId: "org-1" }));
  expect(result.current.snapshot?.deployment.id).toBe("org-2-dep");
  for (const call of [listDeploymentEvents, listDeploymentActivities, listDeploymentLogs]) {
    expect(vi.mocked(call).mock.calls.at(-1)![0]).toEqual({ organizationId: "org-2", number: "20261110.1" });
  }
  await act(async () => { await result.current.cancel(); });
  expect(cancelDeployment).toHaveBeenCalledTimes(1);
  expect(cancelDeployment).toHaveBeenCalledWith({ organizationId: "org-2", number: "20261110.1" });
});

it("discards a pending action response after the organization changes", async () => {
  vi.mocked(getDeployment).mockResolvedValue({ ...deployment, id: "org-1-dep" });
  let finishDispatch!: () => void;
  vi.mocked(dispatchDeployment).mockImplementationOnce(
    () => new Promise((resolve) => { finishDispatch = () => resolve(deployment); }),
  );
  const { result, rerender } = renderHook(
    ({ organizationId }) => useDeploymentDetail({ organizationId, number: "20261110.1" }),
    { initialProps: { organizationId: "org-1" } },
  );
  await waitFor(() => expect(result.current.snapshot?.deployment.id).toBe("org-1-dep"));
  let pending!: Promise<unknown>;
  act(() => { pending = result.current.dispatch(); });
  expect(dispatchDeployment).toHaveBeenCalledWith({ organizationId: "org-1", number: "20261110.1" });
  vi.mocked(getDeployment).mockResolvedValue({ ...deployment, id: "org-2-dep" });
  rerender({ organizationId: "org-2" });
  expect(result.current.dispatching).toBe(false);
  await waitFor(() => expect(result.current.snapshot?.deployment.id).toBe("org-2-dep"));
  const reads = vi.mocked(getDeployment).mock.calls.length;
  await act(async () => { finishDispatch(); await pending; });
  expect(getDeployment).toHaveBeenCalledTimes(reads);
  expect(result.current.snapshot?.deployment.id).toBe("org-2-dep");
  expect(dispatchDeployment).toHaveBeenCalledTimes(1);
});

it("does not request anything without an organization-scoped number", () => {
  const { result } = renderHook(() => useDeploymentDetail(undefined));
  expect(result.current.snapshot).toBeNull();
  expect(getDeployment).not.toHaveBeenCalled();
});
