import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { deleteApp } from "@/api/appsApi";
import type { AppResponse } from "@/models/controlPlane";
import { DeleteAppDialog } from "./DeleteAppDialog";
vi.mock("@/api/appsApi", () => ({ deleteApp: vi.fn() }));
afterEach(() => { cleanup(); vi.resetAllMocks(); });
const app = { id: "app-1", name: "Demo", slug: "demo" } as AppResponse;
describe("application deletion", () => {
  it("requires exact confirmation and submits only on confirmation", async () => {
    vi.mocked(deleteApp).mockResolvedValue(undefined);
    const requested = vi.fn();
    render(<DeleteAppDialog app={app} onRequested={requested} />);
    fireEvent.click(screen.getByText("Delete application"));
    expect(screen.getByText("Permanently delete")).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Type demo to confirm"), { target: { value: "demo" } });
    fireEvent.click(screen.getByText("Permanently delete"));
    await waitFor(() => expect(requested).toHaveBeenCalledOnce());
    expect(deleteApp).toHaveBeenCalledWith("app-1", "demo");
  });
  it("keeps failures visible and allows retry", async () => {
    vi.mocked(deleteApp).mockRejectedValue(new Error("Wait for active deployments"));
    render(<DeleteAppDialog app={app} onRequested={vi.fn()} />);
    fireEvent.click(screen.getByText("Delete application"));
    fireEvent.change(screen.getByLabelText("Type demo to confirm"), { target: { value: "demo" } });
    fireEvent.click(screen.getByText("Permanently delete"));
    expect(await screen.findByRole("alert")).toHaveTextContent("Wait for active deployments");
    expect(screen.getByText("Permanently delete")).toBeEnabled();
  });
  it("does not submit when canceled and disables pending deletion", () => {
    const view = render(<DeleteAppDialog app={app} onRequested={vi.fn()} />);
    fireEvent.click(screen.getByText("Delete application"));
    fireEvent.click(screen.getByText("Cancel"));
    expect(deleteApp).not.toHaveBeenCalled();
    view.rerender(<DeleteAppDialog app={{ ...app, deletionStatus: "queued" }} onRequested={vi.fn()} />);
    expect(screen.getByText("Deleting application…")).toBeDisabled();
  });
});
