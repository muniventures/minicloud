import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { ProvidersModule } from "./ProvidersModule";
import * as api from "../../api/providersApi";
const auth = vi.hoisted(() => ({ role: "owner" }));
vi.mock("../../core/auth/AuthProvider", () => ({ useAuth: () => ({ selectedOrganization: { id: "org-1", role: auth.role } }) }));
vi.mock("../../api/providersApi", () => ({ listProviders: vi.fn(), listPlans: vi.fn(), listProviderTypes: vi.fn(), createProvider: vi.fn(), updateProvider: vi.fn(), replaceProviderCredential: vi.fn(), validateProvider: vi.fn(), deleteProvider: vi.fn(), createPlan: vi.fn(), updatePlan: vi.fn(), deletePlan: vi.fn() }));
const account: api.ProviderAccount = { id: "account-a", organizationId: "org-1", name: "Hetzner first", providerType: "hetzner", enabled: true, validationStatus: "valid", lastValidatedAt: null, revision: 2, planCount: 1 };
const plan: api.ProviderPlan = { id: "plan-a", organizationId: "org-1", providerAccountId: "account-a", name: "Small São Paulo", providerName: account.name, providerType: "hetzner", region: "nbg1", sku: "cx23", image: "ubuntu-24.04", buildPlatform: "linux/amd64", enabled: true, accountEnabled: true, revision: 3, provisioningLocked: true };
beforeEach(() => {
  auth.role = "owner";
  vi.mocked(api.listProviders).mockResolvedValue([account, { ...account, id: "account-b", name: "Hetzner second", planCount: 0 }]);
  vi.mocked(api.listPlans).mockResolvedValue([plan]);
  vi.mocked(api.listProviderTypes).mockResolvedValue([{ type: "hetzner", name: "Hetzner", supportsStorage: true, defaultImage: "ubuntu-24.04" }]);
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });
function mount() { return render(<MemoryRouter><ProvidersModule /></MemoryRouter>); }
it("shows separate accounts of the same provider and custom plan names", async () => {
  mount();
  expect(await screen.findByText("Hetzner first")).toBeInTheDocument();
  expect(screen.getByText("Hetzner second")).toBeInTheDocument();
  expect(screen.getByText("Small São Paulo")).toBeInTheDocument();
  expect(api.listPlans).toHaveBeenCalledWith("org-1", expect.any(AbortSignal));
});
it("keeps credential entry write-only and clears it on submission", async () => {
  vi.mocked(api.replaceProviderCredential).mockImplementation(() => new Promise(() => {}));
  mount(); await screen.findByText("Hetzner first");
  fireEvent.click(screen.getAllByRole("button", { name: "Replace credential" })[0]);
  const field = screen.getByLabelText("New API token or key");
  expect(field).toHaveAttribute("type", "password");
  expect(field).toHaveValue("");
  fireEvent.change(field, { target: { value: "synthetic-secret" } });
  fireEvent.click(screen.getByRole("button", { name: "Save credential" }));
  await waitFor(() => expect(api.replaceProviderCredential).toHaveBeenCalledWith("account-a", "synthetic-secret", 2));
  expect(field).toHaveValue("");
  expect(screen.queryByText("synthetic-secret")).not.toBeInTheDocument();
});
it("renames a reserved plan without sending placement edits", async () => {
  vi.mocked(api.updatePlan).mockResolvedValue({ ...plan, name: "New name" });
  mount(); await screen.findByText("Small São Paulo");
  fireEvent.click(screen.getByRole("button", { name: "Edit plan" }));
  expect(screen.getByLabelText("Region")).toBeDisabled();
  fireEvent.change(screen.getByLabelText("Plan name"), { target: { value: "New name" } });
  fireEvent.click(screen.getByRole("button", { name: "Save plan" }));
  await waitFor(() => expect(api.updatePlan).toHaveBeenCalledWith("plan-a", { name: "New name", revision: 3 }));
});
it("duplicates a used mapping with a fresh editable UUID name", async () => {
  vi.mocked(api.createPlan).mockResolvedValue(plan);
  mount(); await screen.findByText("Small São Paulo");
  fireEvent.click(screen.getByRole("button", { name: "Duplicate plan" }));
  expect((screen.getByLabelText("Plan name") as HTMLInputElement).value).toMatch(/^[0-9a-f-]{36}$/);
  expect(screen.getByLabelText("Region")).toBeEnabled();
  fireEvent.change(screen.getByLabelText("Plan name"), { target: { value: "Secondary small" } });
  fireEvent.click(screen.getByRole("button", { name: "Create plan" }));
  await waitFor(() => expect(api.createPlan).toHaveBeenCalledWith({ organizationId: "org-1", providerAccountId: "account-a", name: "Secondary small", region: "nbg1", sku: "cx23", image: "ubuntu-24.04", buildPlatform: "linux/amd64" }));
});
it("shows API dependency errors when deleting a provider without closing the review", async () => {
  vi.mocked(api.deleteProvider).mockRejectedValue(new Error("Provider retains volumes."));
  mount(); await screen.findByText("Hetzner first");
  fireEvent.click(screen.getAllByRole("button", { name: "Delete provider" })[0]);
  fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Delete" }));
  expect(await within(screen.getByRole("dialog")).findByRole("alert")).toHaveTextContent("Provider retains volumes.");
});
it("allows viewers to read account and plan metadata without mutation controls", async () => {
  auth.role = "viewer"; mount(); await screen.findByText("Small São Paulo");
  expect(screen.queryByRole("button", { name: "Add provider" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Replace credential" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Edit plan" })).not.toBeInTheDocument();
});
it("shows load failures and permits retry", async () => {
  vi.mocked(api.listProviders).mockRejectedValueOnce(new Error("Providers unavailable."));
  mount(); expect(await screen.findByRole("alert")).toHaveTextContent("Providers unavailable.");
  fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
  expect(await screen.findByText("Hetzner first")).toBeInTheDocument();
});
