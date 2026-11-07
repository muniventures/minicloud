import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { listDeployments } from "@/api/deploymentsApi";
import type { DeploymentResponse } from "@/models/controlPlane";
import { useDeploymentFeed } from "./useDeploymentFeed";

vi.mock("@/api/deploymentsApi", () => ({ listDeployments: vi.fn() }));
const deployment = { id: "old", status: "failed" } as DeploymentResponse;
const localBuild = { id: "new", status: "waiting_for_build" } as DeploymentResponse;
beforeEach(() => {
  vi.useFakeTimers();
  vi.resetAllMocks();
  vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  vi.mocked(listDeployments).mockResolvedValue([]);
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

it.each([{ initial: [] }, { initial: [deployment] }])("discovers new CLI builds from an initially empty or terminal feed", async ({ initial }) => {
  vi.mocked(listDeployments).mockResolvedValueOnce(initial);
  const { result } = renderHook(() => useDeploymentFeed("org", "app"));
  await act(async () => {});
  expect(result.current.deployments).toEqual(initial);
  for (const status of ["waiting_for_build", "queued", "succeeded"] as const) {
    vi.mocked(listDeployments).mockResolvedValue([{ ...localBuild, status }]);
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(result.current.deployments[0].status).toBe(status);
  }
  expect(listDeployments).toHaveBeenLastCalledWith({ organizationId: "org", appId: "app" }, expect.any(AbortSignal));
});

it("serializes initial, manual and row-action reads without overlapping polls", async () => {
  let resolve!: (value: DeploymentResponse[]) => void;
  vi.mocked(listDeployments).mockImplementationOnce(() => new Promise((done) => { resolve = done; }));
  const { result } = renderHook(() => useDeploymentFeed("org"));
  const action = vi.fn().mockResolvedValue(undefined);
  let manual!: Promise<void>;
  let row!: Promise<void>;
  act(() => {
    manual = result.current.refresh();
    row = result.current.refresh(action);
  });
  await act(async () => { await vi.advanceTimersByTimeAsync(20000); });
  expect(listDeployments).toHaveBeenCalledTimes(1);
  expect(action).not.toHaveBeenCalled();
  await act(async () => { resolve([deployment]); await manual; await row; });
  expect(action).toHaveBeenCalledTimes(1);
  expect(listDeployments).toHaveBeenCalledTimes(2);
});

it("aborts old tenant/app reads, ignores stale responses and cancels on unmount", async () => {
  let resolve!: (value: DeploymentResponse[]) => void;
  vi.mocked(listDeployments).mockImplementationOnce(() => new Promise((done) => { resolve = done; }));
  const { result, rerender, unmount } = renderHook(
    ({ org, app }) => useDeploymentFeed(org, app),
    { initialProps: { org: "old-org", app: "old-app" } },
  );
  await act(async () => {});
  const oldSignal = vi.mocked(listDeployments).mock.calls[0][1]!;
  const oldRefresh = result.current.refresh;
  rerender({ org: "new-org", app: "new-app" });
  await act(async () => {});
  expect(oldSignal.aborted).toBe(true);
  expect(result.current.deployments).toEqual([]);
  await act(async () => { resolve([deployment]); await oldRefresh(); });
  expect(listDeployments).toHaveBeenCalledTimes(2);
  expect(result.current.deployments).toEqual([]);
  expect(listDeployments).toHaveBeenLastCalledWith({ organizationId: "new-org", appId: "new-app" }, expect.any(AbortSignal));
  const lastSignal = vi.mocked(listDeployments).mock.calls.at(-1)![1]!;
  unmount();
  expect(lastSignal.aborted).toBe(true);
  expect(vi.getTimerCount()).toBe(0);
});

it("pauses hidden-page polling, aborts active reads and refreshes when visible", async () => {
  const { result } = renderHook(() => useDeploymentFeed("org"));
  await act(async () => {});
  let resolve!: (value: DeploymentResponse[]) => void;
  vi.mocked(listDeployments).mockImplementationOnce(() => new Promise((done) => { resolve = done; }));
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  const signal = vi.mocked(listDeployments).mock.calls.at(-1)![1]!;
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(true);
  act(() => document.dispatchEvent(new Event("visibilitychange")));
  expect(signal.aborted).toBe(true);
  await act(async () => { resolve([deployment]); await vi.advanceTimersByTimeAsync(30000); });
  expect(result.current.deployments).toEqual([]);
  expect(listDeployments).toHaveBeenCalledTimes(2);
  vi.mocked(listDeployments).mockResolvedValue([localBuild]);
  hidden.mockReturnValue(false);
  await act(async () => document.dispatchEvent(new Event("visibilitychange")));
  expect(result.current.deployments).toEqual([localBuild]);
  expect(listDeployments).toHaveBeenCalledTimes(3);
});

it("retains previous rows after transient failures and recovers on the next poll", async () => {
  vi.mocked(listDeployments).mockResolvedValue([deployment]);
  const { result } = renderHook(() => useDeploymentFeed("org"));
  await act(async () => {});
  vi.mocked(listDeployments).mockRejectedValueOnce(new Error("Offline"));
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(result.current.deployments).toEqual([deployment]);
  expect(result.current.error).toBe("Offline");
  vi.mocked(listDeployments).mockResolvedValue([localBuild]);
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(result.current.deployments).toEqual([localBuild]);
  expect(result.current.error).toBeNull();
});
