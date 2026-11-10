import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { DatabaseAccessForm } from "./DatabaseAccessForm";
import * as api from "../../api/databasesApi";
vi.mock("../../api/databasesApi", () => ({ listDatabaseNetworks: vi.fn(), listDatabases: vi.fn(), createDatabaseNetwork: vi.fn(), deleteDatabaseNetwork: vi.fn() }));
vi.mock("../Providers/usePlanCatalog", () => ({ usePlanCatalog: () => ({ plans: [{ id: "plan-1", name: "small", providerAccountId: "account-1", providerType: "hetzner", region: "nbg1" }, { id: "plan-2", name: "other-account", providerAccountId: "account-2", providerType: "hetzner", region: "nbg1" }] }), planDescription: () => "Hetzner · nbg1" }));
const database: api.Database = { id: "db-1", name: "orders", engine: "postgres", engineVersion: "16", placement: "shared", status: "ready", accessMode: "internal", allowedCidrsJson: "[]" };
const capabilities: api.DatabaseCapabilities = { accessModes: ["internal", "private", "public"], providerAccountId: "account-1", region: "nbg1", networkZone: "eu-central" };
const network: api.DatabaseNetwork = { id: "network-1", organizationId: "org-1", providerAccountId: "account-1", name: "private-eu", region: "nbg1", networkZone: "eu-central", ipRange: "10.72.0.0/16", subnetRange: "10.72.1.0/24", status: "ready" };
beforeEach(() => { vi.mocked(api.listDatabases).mockResolvedValue([]); vi.mocked(api.listDatabaseNetworks).mockResolvedValue([network, { ...network, id: "other", providerAccountId: "account-2", name: "other-network" }, { ...network, id: "pending", name: "pending-network", status: "creating" }]); });
afterEach(() => { cleanup(); vi.clearAllMocks(); });
it("only selects ready networks in the proven source provider account and zone", async () => {
  const save = vi.fn(); render(<DatabaseAccessForm database={database} capabilities={capabilities} organizationId="org-1" busy={false} onSave={save} />);
  fireEvent.change(screen.getByLabelText("Access mode"), { target: { value: "private" } });
  expect(await screen.findByRole("option", { name: /private-eu/ })).toBeInTheDocument();
  expect(screen.queryByRole("option", { name: /other-network/ })).not.toBeInTheDocument();
  expect(screen.queryByRole("option", { name: /pending-network/ })).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Apply access rules" })).toBeDisabled();
  fireEvent.change(screen.getByLabelText("Provider network"), { target: { value: "network-1" } });
  fireEvent.click(screen.getByRole("button", { name: "Apply access rules" }));
  expect(save).toHaveBeenCalledWith({ mode: "private", allowedCidrs: [], networkId: "network-1" });
});
it("queues network creation with defaults and does not advertise a creating network as ready", async () => {
  vi.mocked(api.createDatabaseNetwork).mockResolvedValue({ ...network, status: "creating" });
  vi.mocked(api.listDatabaseNetworks).mockResolvedValue([{ ...network, status: "creating" }]);
  render(<DatabaseAccessForm database={database} capabilities={capabilities} organizationId="org-1" busy={false} onSave={vi.fn()} />);
  fireEvent.change(screen.getByLabelText("Access mode"), { target: { value: "private" } });
  fireEvent.click(screen.getByRole("button", { name: "Create provider network" }));
  expect(screen.queryByRole("option", { name: /other-account/ })).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("Network name"), { target: { value: "private-eu" } });
  fireEvent.change(screen.getByLabelText("Network plan"), { target: { value: "small" } });
  fireEvent.click(screen.getByRole("button", { name: "Create network" }));
  await waitFor(() => expect(api.createDatabaseNetwork).toHaveBeenCalledWith({ organizationId: "org-1", name: "private-eu", plan: "small", ipRange: "10.72.0.0/16", subnetRange: "10.72.1.0/24" }));
  expect(screen.queryByRole("option", { name: /private-eu/ })).not.toBeInTheDocument();
});
it("preserves public allowlist mode without sending a stale private network identity", async () => {
  const save = vi.fn(); render(<DatabaseAccessForm database={database} capabilities={capabilities} organizationId="org-1" busy={false} onSave={save} />);
  fireEvent.change(screen.getByLabelText("Access mode"), { target: { value: "public" } });
  fireEvent.change(screen.getByLabelText("Allowed public IPs / CIDRs"), { target: { value: "192.0.2.10/32, 198.51.100.0/24" } });
  fireEvent.click(screen.getByRole("button", { name: "Apply access rules" }));
  expect(save).toHaveBeenCalledWith({ mode: "public", allowedCidrs: ["192.0.2.10/32", "198.51.100.0/24"] });
});
it("does not expose private mode or call its APIs when unsupported", () => {
  render(<DatabaseAccessForm database={database} capabilities={{ accessModes: ["internal"] }} organizationId="org-1" busy={false} onSave={vi.fn()} />);
  expect(screen.queryByRole("option", { name: "Provider private network" })).not.toBeInTheDocument();
  expect(api.listDatabaseNetworks).not.toHaveBeenCalled();
});

it("blocks network deletion before the API when any database uses it", async () => {
  vi.mocked(api.listDatabases).mockResolvedValue([{ ...database, id: "neighbor", networkId: network.id }]);
  render(<DatabaseAccessForm database={database} capabilities={capabilities} organizationId="org-1" busy={false} onSave={vi.fn()} />);
  fireEvent.change(screen.getByLabelText("Access mode"), { target: { value: "private" } });
  const remove = await screen.findByRole("button", { name: "Delete network private-eu" });
  expect(remove).toBeDisabled();
  fireEvent.click(remove);
  expect(api.deleteDatabaseNetwork).not.toHaveBeenCalled();
});
it("requests confirmed unused-network cleanup without claiming deletion completed", async () => {
  const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
  vi.mocked(api.deleteDatabaseNetwork).mockResolvedValue(undefined);
  render(<DatabaseAccessForm database={database} capabilities={capabilities} organizationId="org-1" busy={false} onSave={vi.fn()} />);
  fireEvent.change(screen.getByLabelText("Access mode"), { target: { value: "private" } });
  fireEvent.click(await screen.findByRole("button", { name: "Delete network private-eu" }));
  await waitFor(() => expect(api.deleteDatabaseNetwork).toHaveBeenCalledWith("network-1"));
  expect(await screen.findByText(/Deletion requested/)).toBeInTheDocument();
  expect(screen.getByText("private-eu · ready · 10.72.0.0/16")).toBeInTheDocument();
  confirm.mockRestore();
});
