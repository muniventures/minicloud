import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, MemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CreateAppModule } from "./CreateAppModule";
import { AppSettingsModule } from "./AppSettingsModule";
import { buildSshAccessRequest, defaultSshAccessDraft } from "./sshAccess";
import { listPlans, type ProviderPlan } from "../../api/providersApi";
import { createApplication, getApplication, updateEnvironment } from "../../api/applicationsApi";
import { listProviderSshKeys } from "../../api/sshKeysApi";
import type { Application, ApplicationEnvironment, ProviderSshKey } from "../../models/application";
import { ApiError } from "../../models/controlPlane";

vi.mock("../../api/providersApi", () => ({ listPlans: vi.fn() }));
vi.mock("../../api/sshKeysApi", () => ({ listProviderSshKeys: vi.fn() }));
vi.mock("../../api/applicationsApi", () => ({
  createApplication: vi.fn(),
  getApplication: vi.fn(),
  updateApplication: vi.fn(),
  createEnvironment: vi.fn(),
  updateEnvironment: vi.fn(),
  deleteEnvironment: vi.fn(),
  deleteApplication: vi.fn(),
}));
vi.mock("../../api/applicationsFixtures", () => ({ appsFixturesEnabled: () => false, appsFixtureApplications: () => [] }));
vi.mock("../../core/auth/AuthProvider", () => ({ useAuth: () => ({ selectedOrganization: { id: "org-1", role: "owner" } }) }));

const PUBLIC_KEY = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFakeSyntheticKeyMaterialForTestsOnly0000 me@example";
const PRIVATE_KEY = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----";

const plans = [
  { id: "plan-a", name: "Plan A", providerAccountId: "acct-a", providerName: "Hetzner main", providerType: "hetzner", region: "nbg1", sku: "cx23", enabled: true, accountEnabled: true },
  { id: "plan-b", name: "Plan B", providerAccountId: "acct-b", providerName: "Vultr backup", providerType: "vultr", region: "ams", sku: "vc2", enabled: true, accountEnabled: true },
] as ProviderPlan[];

function key(id: string, accountId: string, name: string): ProviderSshKey {
  return { id, providerAccountId: accountId, name, fingerprint: `SHA256:${id}fp`, keyType: "ed25519", registrationState: "discovered" };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

afterEach(() => { cleanup(); vi.resetAllMocks(); });

describe("SSH request builder", () => {
  it("rejects private keys, multiple keys and unsupported material before sending", () => {
    const draft = { ...defaultSshAccessDraft, mode: "new" as const, name: "laptop" };
    expect(buildSshAccessRequest({ ...draft, publicKey: PRIVATE_KEY }, "acct-a").error).toMatch(/private key/);
    expect(buildSshAccessRequest({ ...draft, publicKey: `${PUBLIC_KEY}\n${PUBLIC_KEY}` }, "acct-a").error).toMatch(/exactly one/);
    expect(buildSshAccessRequest({ ...draft, publicKey: "ecdsa-sha2-nistp256 AAAA" }, "acct-a").error).toMatch(/Ed25519 or RSA/);
    expect(buildSshAccessRequest({ ...draft, publicKey: ` ${PUBLIC_KEY} ` }, "acct-a").request).toEqual({ mode: "new", name: "laptop", publicKey: PUBLIC_KEY });
    expect(buildSshAccessRequest({ ...defaultSshAccessDraft, mode: "existing", keyId: "k1", keyAccountId: "acct-a" }, "acct-b").error).toMatch(/destination provider account/);
  });
});

describe("Create app SSH access", () => {
  beforeEach(() => {
    vi.mocked(listPlans).mockResolvedValue(plans);
    vi.mocked(createApplication).mockResolvedValue({ id: "app-1" } as never);
  });

  async function renderWithPlan(planName = "Plan A") {
    render(<MemoryRouter><CreateAppModule /></MemoryRouter>);
    await screen.findByRole("option", { name: /Plan A/ });
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Demo" } });
    fireEvent.change(screen.getByLabelText("Plan"), { target: { value: planName } });
  }

  it("defaults to Minicloud access only and shows the destination account", async () => {
    await renderWithPlan();
    expect(screen.getByRole("radio", { name: "Minicloud access only" })).toBeChecked();
    expect(screen.getByText("Hetzner main · hetzner")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Create app" }));
    await waitFor(() => expect(createApplication).toHaveBeenCalled());
    expect(vi.mocked(createApplication).mock.calls[0][0].initialEnvironment?.sshAccess).toEqual({ mode: "none" });
    expect(listProviderSshKeys).not.toHaveBeenCalled();
  });

  it("sends an existing key by its local id for the selected account", async () => {
    vi.mocked(listProviderSshKeys).mockResolvedValue([key("key-1", "acct-a", "laptop")]);
    await renderWithPlan();
    fireEvent.click(screen.getByRole("radio", { name: "Use existing key" }));
    expect(screen.getByText("This key will be installed when the server is created.", { exact: false })).toBeInTheDocument();
    const option = await screen.findByRole("option", { name: "laptop — SHA256:key-1fp (ed25519)" });
    expect(listProviderSshKeys).toHaveBeenCalledWith("org-1", "acct-a", expect.any(AbortSignal));
    fireEvent.change(screen.getByLabelText("SSH key"), { target: { value: (option as HTMLOptionElement).value } });
    fireEvent.click(screen.getByRole("button", { name: "Create app" }));
    await waitFor(() => expect(createApplication).toHaveBeenCalledWith({
      organizationId: "org-1", name: "Demo", slug: "demo",
      initialEnvironment: { name: "production", plan: "Plan A", database: "sqlite", sshAccess: { mode: "existing", keyId: "key-1" } },
    }));
  });

  it("sends a new public key with its name", async () => {
    await renderWithPlan();
    fireEvent.click(screen.getByRole("radio", { name: "Add new public key" }));
    fireEvent.change(screen.getByLabelText("SSH key name"), { target: { value: " laptop " } });
    fireEvent.change(screen.getByLabelText("Public key"), { target: { value: PUBLIC_KEY } });
    fireEvent.click(screen.getByRole("button", { name: "Create app" }));
    await waitFor(() => expect(createApplication).toHaveBeenCalled());
    expect(vi.mocked(createApplication).mock.calls[0][0].initialEnvironment?.sshAccess).toEqual({ mode: "new", name: "laptop", publicKey: PUBLIC_KEY });
  });

  it("rejects pasted private keys without keeping or sending them", async () => {
    await renderWithPlan();
    fireEvent.click(screen.getByRole("radio", { name: "Add new public key" }));
    fireEvent.change(screen.getByLabelText("SSH key name"), { target: { value: "laptop" } });
    fireEvent.change(screen.getByLabelText("Public key"), { target: { value: PRIVATE_KEY } });
    expect(screen.getByRole("alert")).toHaveTextContent("looks like a private key");
    expect(screen.getByLabelText("Public key")).toHaveValue("");
    fireEvent.click(screen.getByRole("button", { name: "Create app" }));
    expect(await screen.findByText("Paste an OpenSSH public key.")).toBeInTheDocument();
    expect(createApplication).not.toHaveBeenCalled();
  });

  it("ignores a stale inventory response after switching provider accounts and clears the old key", async () => {
    const first = deferred<ProviderSshKey[]>();
    const second = deferred<ProviderSshKey[]>();
    vi.mocked(listProviderSshKeys).mockImplementation((_org, accountId) => accountId === "acct-a" ? first.promise : second.promise);
    await renderWithPlan();
    fireEvent.click(screen.getByRole("radio", { name: "Use existing key" }));
    expect(screen.getByText("Loading SSH keys…")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Plan"), { target: { value: "Plan B" } });
    expect(screen.getByText("Vultr backup · vultr")).toBeInTheDocument();
    await act(async () => { second.resolve([key("key-b", "acct-b", "bravo")]); });
    await act(async () => { first.resolve([key("key-a", "acct-a", "alpha")]); });
    expect(screen.getByRole("option", { name: /bravo/ })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /alpha/ })).not.toBeInTheDocument();
    const firstSignal = vi.mocked(listProviderSshKeys).mock.calls.find(call => call[1] === "acct-a")?.[2];
    expect(firstSignal?.aborted).toBe(true);

    fireEvent.change(screen.getByLabelText("SSH key"), { target: { value: "key-b" } });
    vi.mocked(listProviderSshKeys).mockResolvedValue([key("key-a", "acct-a", "alpha")]);
    fireEvent.change(screen.getByLabelText("Plan"), { target: { value: "Plan A" } });
    await screen.findByRole("option", { name: /alpha/ });
    expect(screen.getByLabelText("SSH key")).toHaveValue("");
    fireEvent.click(screen.getByRole("button", { name: "Create app" }));
    expect(await screen.findByText("Select an SSH key from the destination provider account.")).toBeInTheDocument();
    expect(createApplication).not.toHaveBeenCalled();
  });

  it("distinguishes denied, unavailable and empty inventories", async () => {
    vi.mocked(listProviderSshKeys).mockRejectedValueOnce(new ApiError(403, { code: "forbidden", message: "Denied.", details: [] }));
    await renderWithPlan();
    fireEvent.click(screen.getByRole("radio", { name: "Use existing key" }));
    expect(await screen.findByText("SSH key list denied.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retry" })).not.toBeInTheDocument();

    vi.mocked(listProviderSshKeys).mockRejectedValueOnce(new ApiError(503, { code: "ssh_inventory_unavailable", message: "Down.", details: [] }));
    fireEvent.change(screen.getByLabelText("Plan"), { target: { value: "Plan B" } });
    expect(await screen.findByText("SSH key list unavailable.")).toBeInTheDocument();
    expect(screen.getByText(/does not mean the account has no keys/)).toBeInTheDocument();
    expect(screen.queryByText(/has no SSH keys yet/)).not.toBeInTheDocument();

    vi.mocked(listProviderSshKeys).mockResolvedValueOnce([]);
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByText(/has no SSH keys yet/)).toBeInTheDocument();
    expect(screen.queryByText("SSH key list unavailable.")).not.toBeInTheDocument();
  });

  it("keeps the new-key draft and explains a rejected save", async () => {
    vi.mocked(createApplication).mockRejectedValueOnce(new ApiError(400, { code: "ssh_key_invalid", message: "RSA keys must be at least 3072 bits.", details: [] }));
    await renderWithPlan();
    fireEvent.click(screen.getByRole("radio", { name: "Add new public key" }));
    fireEvent.change(screen.getByLabelText("SSH key name"), { target: { value: "laptop" } });
    fireEvent.change(screen.getByLabelText("Public key"), { target: { value: PUBLIC_KEY } });
    fireEvent.click(screen.getByRole("button", { name: "Create app" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("RSA keys must be at least 3072 bits. Paste one OpenSSH Ed25519 or RSA");
    expect(screen.getByLabelText("SSH key name")).toHaveValue("laptop");
    expect(screen.getByLabelText("Public key")).toHaveValue(PUBLIC_KEY);
    expect(screen.getByRole("radio", { name: "Add new public key" })).toBeChecked();
  });
});

describe("Environment settings SSH access", () => {
  function environment(overrides: Partial<ApplicationEnvironment>): ApplicationEnvironment {
    return {
      id: "env-1", applicationId: "app-1", organizationId: "org-1", name: "production", kind: "persistent",
      runtimeSlug: "demo", plan: "Plan A", planId: "plan-a", database: "sqlite", sourceBranch: null,
      sourceEnvironmentId: null, deletionStatus: null, deletionError: null, placementLocked: false, domains: [],
      latestDeployment: null,
      runtimeObservation: { state: "unknown", connected: false, stale: false, observedAt: null, lastSeenAt: null, totalServices: 0, healthyServices: 0 },
      createdAt: "2026-11-01T00:00:00Z", updatedAt: "2026-11-01T00:00:00Z",
      ...overrides,
    };
  }
  function application(env: ApplicationEnvironment): Application {
    return {
      id: "app-1", organizationId: "org-1", name: "Demo", slug: "demo", defaultEnvironmentId: env.id, deletionStatus: null,
      deletionError: null, environmentCount: 1, persistentEnvironmentCount: 1, previewEnvironmentCount: 0,
      environments: [env], createdAt: "2026-11-01T00:00:00Z", updatedAt: "2026-11-01T00:00:00Z",
    };
  }
  function renderSettings() {
    const router = createMemoryRouter([{ path: "/apps/:applicationId/settings", element: <AppSettingsModule /> }], { initialEntries: ["/apps/app-1/settings"] });
    render(<RouterProvider router={router} />);
  }
  beforeEach(() => { vi.mocked(listPlans).mockResolvedValue(plans); });

  it("shows a locked allocated selection read-only with the verified login", async () => {
    vi.mocked(getApplication).mockResolvedValue(application(environment({
      sshAccess: { mode: "existing", providerAccountId: "acct-a", keyId: "key-1", name: "laptop", fingerprint: "SHA256:abc", keyType: "ed25519", revision: 2, registrationState: "ready", selectionLocked: true },
      sshConnection: { address: "203.0.113.10", username: "ubuntu" },
    })));
    renderSettings();
    const panel = await screen.findByLabelText("SSH access for production");
    expect(within(panel).getByText("Existing provider key")).toBeInTheDocument();
    expect(within(panel).getByText("laptop · SHA256:abc · ed25519")).toBeInTheDocument();
    expect(within(panel).getByText("Registration: Registered with provider")).toBeInTheDocument();
    expect(within(panel).getByText("Selection revision 2")).toBeInTheDocument();
    expect(within(panel).getByText("ubuntu@203.0.113.10")).toBeInTheDocument();
    expect(within(panel).getByRole("note")).toHaveTextContent("Allocated selections are immutable, and this form never updates keys on a running VPS.");
    expect(within(panel).queryByRole("button", { name: "Change SSH access" })).not.toBeInTheDocument();
  });

  it("never guesses a login user before the server is known", async () => {
    vi.mocked(getApplication).mockResolvedValue(application(environment({
      sshAccess: { mode: "none", providerAccountId: null, keyId: null, name: null, fingerprint: null, keyType: null, revision: 0, registrationState: "none", selectionLocked: false },
      sshConnection: null,
    })));
    renderSettings();
    const panel = await screen.findByLabelText("SSH access for production");
    expect(within(panel).getByText(/shown after the server is created/)).toBeInTheDocument();
    expect(within(panel).queryByText(/root@/)).not.toBeInTheDocument();
  });

  it("patches with expectedRevision and keeps the draft on a revision conflict", async () => {
    vi.mocked(getApplication).mockResolvedValue(application(environment({
      sshAccess: { mode: "none", providerAccountId: null, keyId: null, name: null, fingerprint: null, keyType: null, revision: 3, registrationState: "none", selectionLocked: false },
    })));
    vi.mocked(updateEnvironment).mockRejectedValueOnce(new ApiError(409, { code: "ssh_selection_revision_conflict", message: "Changed.", details: [] }));
    renderSettings();
    fireEvent.click(await screen.findByRole("button", { name: "Change SSH access" }));
    await screen.findByText("Hetzner main · hetzner");
    fireEvent.click(screen.getByRole("radio", { name: "Add new public key" }));
    fireEvent.change(screen.getByLabelText("SSH key name"), { target: { value: "laptop" } });
    fireEvent.change(screen.getByLabelText("Public key"), { target: { value: PUBLIC_KEY } });
    fireEvent.click(screen.getByRole("button", { name: "Save SSH access" }));
    await waitFor(() => expect(updateEnvironment).toHaveBeenCalledWith("app-1", "env-1", {
      sshAccess: { mode: "new", name: "laptop", publicKey: PUBLIC_KEY, expectedRevision: 3 },
    }));
    expect(await screen.findByRole("alert")).toHaveTextContent("SSH access changed since this page loaded. Reload the environment");
    expect(screen.getByLabelText("Public key")).toHaveValue(PUBLIC_KEY);
    expect(getApplication).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("button", { name: "Reload environment" }));
    await waitFor(() => expect(getApplication).toHaveBeenCalledTimes(2));
  });

  it("explains locked and permission failures on save", async () => {
    vi.mocked(getApplication).mockResolvedValue(application(environment({
      sshAccess: { mode: "none", providerAccountId: null, keyId: null, name: null, fingerprint: null, keyType: null, revision: 0, registrationState: "none", selectionLocked: false },
    })));
    vi.mocked(updateEnvironment)
      .mockRejectedValueOnce(new ApiError(403, { code: "forbidden", message: "The providers:write permission is required to register an SSH key.", details: [] }))
      .mockRejectedValueOnce(new ApiError(409, { code: "ssh_selection_locked", message: "Locked.", details: [] }));
    renderSettings();
    fireEvent.click(await screen.findByRole("button", { name: "Change SSH access" }));
    await screen.findByText("Hetzner main · hetzner");
    fireEvent.click(screen.getByRole("radio", { name: "Add new public key" }));
    fireEvent.change(screen.getByLabelText("SSH key name"), { target: { value: "laptop" } });
    fireEvent.change(screen.getByLabelText("Public key"), { target: { value: PUBLIC_KEY } });
    fireEvent.click(screen.getByRole("button", { name: "Save SSH access" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Permission denied: The providers:write permission");
    fireEvent.click(screen.getByRole("button", { name: "Save SSH access" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("never updates a running VPS"));
    expect(screen.getByLabelText("SSH key name")).toHaveValue("laptop");
  });
});
