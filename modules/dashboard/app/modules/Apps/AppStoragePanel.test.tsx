import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { listAppStorage } from "@/api/appsApi";
import { AppStoragePanel } from "./AppStoragePanel";
vi.mock("@/api/appsApi", () => ({ listAppStorage: vi.fn() }));
afterEach(cleanup);

it("shows retained disks without claiming mount health and provides configuration", async () => {
  vi.mocked(listAppStorage).mockResolvedValue([{ name: "uploads", provider: "hetzner", providerVolumeId: "901", region: "nbg1", sizeGb: 20, uid: 1000, gid: 1000, status: "registered" }]);
  render(<AppStoragePanel appId="app_test" services={[]} />);
  await screen.findByText("uploads · 20 GB · nbg1");
  expect(screen.getByText("No active service mounts")).toBeInTheDocument();
  expect(screen.getByText(/does not confirm current disk or mount health/)).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Copy storage configuration" })).toBeInTheDocument();
});

it("shows unavailable inventory as an error", async () => {
  vi.mocked(listAppStorage).mockRejectedValue(new Error("Storage unavailable"));
  render(<AppStoragePanel appId="app_test" services={[]} />);
  await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("Storage unavailable"));
  expect(screen.queryByText("No storage volumes registered.")).not.toBeInTheDocument();
});
