import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { listAppServices } from "@/api/environmentsApi";
import { useAppServiceInventory } from "./useAppServiceInventory";
vi.mock("@/api/environmentsApi", () => ({ listAppServices: vi.fn() }));
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.clearAllMocks();
});
it("does not overlap initial, manual, or polling requests and ages observations after failure", async () => {
  vi.useFakeTimers();
  let resolve!: (value: never[]) => void;
  vi.mocked(listAppServices).mockReturnValueOnce(
    new Promise((done) => {
      resolve = done;
    }),
  );
  const { result } = renderHook(() => useAppServiceInventory("app", "org"));
  await act(async () => {
    void result.current.refresh();
    await vi.advanceTimersByTimeAsync(45_000);
  });
  expect(listAppServices).toHaveBeenCalledTimes(1);
  await act(async () => resolve([]));
  vi.mocked(listAppServices).mockRejectedValue(new Error("Offline"));
  const prior = result.current.now;
  await act(async () => {
    await vi.advanceTimersByTimeAsync(15_000);
  });
  expect(listAppServices).toHaveBeenCalledTimes(2);
  expect(result.current.error).toBe("Offline");
  expect(result.current.now).toBeGreaterThan(prior);
  expect(result.current.services).toEqual([]);
});
