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
const deployment = {
  id: "dep",
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
  const { result } = renderHook(() => useDeploymentDetail("dep"));
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
    ({ id }) => useDeploymentDetail(id),
    { initialProps: { id: "old" } },
  );
  const signal = vi.mocked(getDeployment).mock.calls[0][1]!;
  vi.mocked(getDeployment).mockResolvedValue({ ...deployment, id: "new" });
  rerender({ id: "new" });
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
  const { result } = renderHook(() => useDeploymentDetail("dep"));
  await act(async () => {});
  vi.mocked(getDeployment).mockResolvedValue({ ...deployment, cancellationRequestedAt: "2026-10-27T12:00:00Z" });
  await act(async () => { await result.current.cancel(); });
  expect(cancelDeployment).toHaveBeenCalledWith("dep");
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
