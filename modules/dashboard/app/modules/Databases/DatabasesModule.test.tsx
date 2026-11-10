import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { DatabasesModule } from "./DatabasesModule";
import * as api from "../../api/databasesApi";
const auth = vi.hoisted(() => ({ role: "owner" }));
vi.mock("../../core/auth/AuthProvider", () => ({ useAuth: () => ({ selectedOrganization: { id: "org-1", role: auth.role } }) }));
vi.mock("../Providers/usePlanCatalog", () => ({ usePlanCatalog: () => ({ plans: [{ id: "plan-1", name: "small", providerName: "Cloud", providerType: "hetzner", region: "nbg1", sku: "cx23" }] }), planDescription: () => "Cloud · nbg1" }));
vi.mock("../../api/environmentsApi", () => ({  }));
vi.mock("../../api/databasesApi", () => ({ listDatabases: vi.fn(), listDatabaseServers: vi.fn(), listRepositories: vi.fn(), createDatabaseServer: vi.fn(), createDatabase: vi.fn(), deleteRepository: vi.fn() }));
beforeEach(() => { auth.role = "owner"; vi.mocked(api.listDatabases).mockResolvedValue([{ id: "db-1", name: "Orders", engine: "postgres", engineVersion: "16", placement: "application", status: "ready", accessMode: "internal", allowedCidrsJson: "[]", appId: "app-1" }]); vi.mocked(api.listDatabaseServers).mockResolvedValue([]); vi.mocked(api.listRepositories).mockResolvedValue([]); });
afterEach(() => { cleanup(); vi.clearAllMocks(); });
function mount() { render(<MemoryRouter><DatabasesModule /></MemoryRouter>); }
it("lists application-hosted databases in the common database inventory", async () => { mount(); expect(await screen.findByRole("link", { name: "Orders" })).toHaveAttribute("href", "/databases/db-1"); expect(screen.getByRole("link", { name: "View application" })).toHaveAttribute("href", "/apps/app-1"); expect(api.listDatabases).toHaveBeenCalledWith("org-1", expect.any(AbortSignal)); });
it("requires an independent repository before creating a default-protected database", async () => { mount(); await screen.findByText("Orders"); fireEvent.click(screen.getByRole("button", { name: "Create database" })); const submit = screen.getAllByRole("button", { name: "Create database" }).at(-1)!; expect(submit).toBeDisabled(); expect(screen.getByLabelText("Enable scheduled backups")).toBeChecked(); expect(screen.getByLabelText("Frequency (hours)")).toHaveValue(24); expect(screen.getByLabelText("Retention (days)")).toHaveValue(30); });
it("submits shared server creation and shows actual server status rather than ready optimistically", async () => { vi.mocked(api.createDatabaseServer).mockResolvedValue({ id: "server-1", name: "Shared", mode: "shared", status: "creating", engine: "postgres", engineVersion: "16", planId: "plan-1" }); mount(); await screen.findByText("Orders"); fireEvent.click(screen.getByRole("button", { name: "Create database server" })); fireEvent.change(screen.getByLabelText("Server name"), { target: { value: "Shared" } }); fireEvent.change(screen.getByLabelText("Plan"), { target: { value: "small" } }); fireEvent.click(screen.getByRole("button", { name: "Create server" })); await waitFor(() => expect(api.createDatabaseServer).toHaveBeenCalledWith({ organizationId: "org-1", name: "Shared", mode: "shared", plan: "small" })); expect(screen.queryByText("ready")).not.toBeInTheDocument(); });
it("hides mutation controls from viewers", async () => { auth.role = "viewer"; mount(); await screen.findByText("Orders"); expect(screen.queryByRole("button", { name: "Create database" })).not.toBeInTheDocument(); expect(screen.queryByRole("button", { name: "Add existing application database" })).not.toBeInTheDocument(); });

it("requires the exact repository name before requesting unused repository deletion", async () => {
  vi.mocked(api.listRepositories).mockResolvedValue([{ id: "repo-1", name: "Daily backups" }]);
  vi.mocked(api.deleteRepository).mockResolvedValue(undefined);
  mount();
  fireEvent.click(await screen.findByRole("button", { name: "Delete unused repository Daily backups" }));
  const confirm = screen.getByLabelText("Confirm repository name");
  fireEvent.change(confirm, { target: { value: "daily backups" } });
  expect(screen.getByRole("button", { name: "Delete repository" })).toBeDisabled();
  expect(api.deleteRepository).not.toHaveBeenCalled();
  fireEvent.change(confirm, { target: { value: "Daily backups" } });
  fireEvent.click(screen.getByRole("button", { name: "Delete repository" }));
  await waitFor(() => expect(api.deleteRepository).toHaveBeenCalledWith("repo-1", "Daily backups"));
});

it("keeps a protected repository visible and shows the server's retention blocker", async () => {
  vi.mocked(api.listRepositories).mockResolvedValue([{ id: "repo-1", name: "Daily backups" }]);
  vi.mocked(api.deleteRepository).mockRejectedValue(new Error("Retained backups must be retired first."));
  mount();
  fireEvent.click(await screen.findByRole("button", { name: "Delete unused repository Daily backups" }));
  fireEvent.change(screen.getByLabelText("Confirm repository name"), { target: { value: "Daily backups" } });
  fireEvent.click(screen.getByRole("button", { name: "Delete repository" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Retained backups must be retired first.");
  expect(screen.getByRole("dialog", { name: "Delete backup repository" })).toBeInTheDocument();
});

it("does not expose repository deletion to viewers", async () => {
  auth.role = "viewer";
  vi.mocked(api.listRepositories).mockResolvedValue([{ id: "repo-1", name: "Daily backups" }]);
  mount(); await screen.findByText("Daily backups");
  expect(screen.queryByRole("button", { name: /Delete unused repository/ })).not.toBeInTheDocument();
});

it("keeps queued repository deletion visible without permitting another request", async () => {
  vi.mocked(api.listRepositories).mockResolvedValue([{ id: "repo-1", name: "Daily backups", deletionStatus: "queued" }]);
  mount(); await screen.findByText("· deletion queued");
  expect(screen.getByRole("button", { name: "Delete unused repository Daily backups" })).toBeDisabled();
});

it.each(["deleted", "ready"])("counts only live databases for empty-server deletion when the source is %s", async status => {
  vi.mocked(api.listDatabaseServers).mockResolvedValue([{ id: "server-1", name: "Shared", mode: "shared", engine: "postgres", engineVersion: "16", status: "ready", planId: "plan-1" }]);
  vi.mocked(api.listDatabases).mockResolvedValue([{ id: "db-1", name: "Orders", engine: "postgres", engineVersion: "16", placement: "shared", status, accessMode: "internal", allowedCidrsJson: "[]", serverId: "server-1" }]);
  mount(); await screen.findByText("Shared");
  const remove = screen.getByRole("button", { name: "Delete empty server" });
  if (status === "deleted") {
    expect(remove).toBeEnabled(); expect(screen.getByText("0 databases")).toBeInTheDocument();
  } else {
    expect(remove).toBeDisabled(); expect(screen.getByText("1 databases")).toBeInTheDocument();
  }
});

vi.mock("../../api/applicationsApi", () => ({ listApplications: vi.fn().mockResolvedValue([]) }));
