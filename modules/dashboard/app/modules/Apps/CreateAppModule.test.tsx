import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, expect, it, vi } from "vitest";
import { CreateAppModule } from "./CreateAppModule";
import { listPlans, type ProviderPlan } from "../../api/providersApi";
import { createApp } from "../../api/appsApi";
vi.mock("../../api/providersApi", () => ({ listPlans: vi.fn() }));
vi.mock("../../api/appsApi", () => ({ createApp: vi.fn() }));
vi.mock("../../core/auth/AuthProvider", () => ({ useAuth: () => ({ selectedOrganization: { id: "org-1", role: "owner" } }) }));
afterEach(() => { cleanup(); vi.resetAllMocks(); });
it("selects a custom organization plan by name without a provider field", async () => {
  vi.mocked(listPlans).mockResolvedValue([
    { id: "plan-a", name: "Small São Paulo", providerName: "Hetzner second", providerType: "hetzner", region: "nbg1", sku: "cx23", enabled: true, accountEnabled: true },
    { id: "plan-b", name: "Disabled account", enabled: true, accountEnabled: false },
  ] as ProviderPlan[]);
  vi.mocked(createApp).mockResolvedValue({ id: "app-a" } as never);
  render(<MemoryRouter><CreateAppModule /></MemoryRouter>);
  expect(await screen.findByRole("option", { name: /Small São Paulo/ })).toBeInTheDocument();
  expect(screen.queryByRole("option", { name: /Disabled account/ })).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Demo" } });
  fireEvent.change(screen.getByLabelText("Plan"), { target: { value: "Small São Paulo" } });
  fireEvent.click(screen.getByRole("button", { name: "Create app" }));
  await waitFor(() => expect(createApp).toHaveBeenCalledWith({ organizationId: "org-1", environmentName: "production", name: "Demo", slug: "demo", plan: "Small São Paulo", database: "sqlite" }));
});
it("blocks creation and links to provider setup when no plans exist", async () => {
  vi.mocked(listPlans).mockResolvedValue([]);
  render(<MemoryRouter><CreateAppModule /></MemoryRouter>);
  expect(await screen.findByRole("link", { name: "Configure a provider and plan" })).toHaveAttribute("href", "/plans");
  expect(screen.getByRole("button", { name: "Create app" })).toBeDisabled();
});
it("shows catalog errors without substituting hardcoded plans", async () => {
  vi.mocked(listPlans).mockRejectedValue(new Error("Plan catalog unavailable."));
  render(<MemoryRouter><CreateAppModule /></MemoryRouter>);
  expect(await screen.findByRole("alert")).toHaveTextContent("Plan catalog unavailable.");
  expect(screen.getByRole("button", { name: "Create app" })).toBeDisabled();
});
