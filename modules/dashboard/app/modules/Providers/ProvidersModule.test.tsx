import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { ProviderAccountsModule, ProvidersModule } from "./ProvidersModule";
import * as api from "../../api/providersApi";
import { ApiError } from "../../models/controlPlane";

const auth = vi.hoisted(() => ({
  organizationId: "org-1",
  role: "owner",
}));

vi.mock("../../core/auth/AuthProvider", () => ({
  useAuth: () => ({
    selectedOrganization: auth.organizationId
      ? { id: auth.organizationId, role: auth.role }
      : null,
  }),
}));

vi.mock("../../api/providersApi", () => ({
  listProviders: vi.fn(),
  listPlans: vi.fn(),
  listProviderTypes: vi.fn(),
  createProvider: vi.fn(),
  updateProvider: vi.fn(),
  replaceProviderCredential: vi.fn(),
  validateProvider: vi.fn(),
  deleteProvider: vi.fn(),
  createPlan: vi.fn(),
  updatePlan: vi.fn(),
  deletePlan: vi.fn(),
}));

const providerTypes: api.ProviderType[] = [
  {
    type: "hetzner",
    name: "Hetzner",
    supportsStorage: true,
    defaultImage: "ubuntu-24.04",
  },
  {
    type: "digital-ocean",
    name: "DigitalOcean",
    supportsStorage: false,
    defaultImage: "ubuntu-24-04-x64",
  },
  {
    type: "vultr",
    name: "Vultr",
    supportsStorage: false,
    defaultImage: null,
    defaultOsId: 2284,
  },
];

const accountHetznerPrimary: api.ProviderAccount = {
  id: "account-h1",
  organizationId: "org-1",
  name: "Hetzner primary",
  providerType: "hetzner",
  enabled: true,
  validationStatus: "valid",
  lastValidatedAt: "2026-11-09T12:00:00Z",
  revision: 2,
  planCount: 2,
};

const accountHetznerSecondary: api.ProviderAccount = {
  id: "account-h2",
  organizationId: "org-1",
  name: "Hetzner secondary",
  providerType: "hetzner",
  enabled: true,
  validationStatus: "unchecked",
  lastValidatedAt: null,
  revision: 1,
  planCount: 1,
};

const accountVultr: api.ProviderAccount = {
  id: "account-v1",
  organizationId: "org-1",
  name: "Vultr primary",
  providerType: "vultr",
  enabled: true,
  defaultOsId: 2284,
  validationStatus: "valid",
  lastValidatedAt: "2026-11-09T11:00:00Z",
  revision: 1,
  planCount: 1,
};

const planLocked: api.ProviderPlan = {
  id: "plan-1",
  organizationId: "org-1",
  providerAccountId: "account-h1",
  name: "hetzner-small",
  providerName: "Hetzner primary",
  providerType: "hetzner",
  region: "nbg1",
  sku: "cx23",
  image: "ubuntu-24.04",
  buildPlatform: "linux/amd64",
  enabled: true,
  accountEnabled: true,
  revision: 3,
  provisioningLocked: true,
};

const planUnlockedSecondary: api.ProviderPlan = {
  id: "plan-2",
  organizationId: "org-1",
  providerAccountId: "account-h2",
  name: "hetzner-secondary-small",
  providerName: "Hetzner secondary",
  providerType: "hetzner",
  region: "nbg1",
  sku: "cx23",
  image: "ubuntu-24.04",
  buildPlatform: "linux/arm64",
  enabled: true,
  accountEnabled: true,
  revision: 1,
  provisioningLocked: false,
};

const planVultr: api.ProviderPlan = {
  id: "plan-3",
  organizationId: "org-1",
  providerAccountId: "account-v1",
  name: "vultr-basic-1gb",
  providerName: "Vultr primary",
  providerType: "vultr",
  region: "ewr",
  sku: "vc2-1c-1gb",
  image: "2284",
  buildPlatform: "linux/amd64",
  enabled: true,
  accountEnabled: true,
  revision: 1,
  provisioningLocked: false,
};

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location-path">{location.pathname}</div>;
}

function mount(initialEntry = "/plans") {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <LocationProbe />
      <Routes>
        <Route path="/plans" element={<ProvidersModule />} />
        <Route path="/plans/accounts" element={<ProviderAccountsModule />} />
        <Route path="/providers" element={<ProvidersModule />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.stubGlobal(
    "ResizeObserver",
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  sessionStorage.clear();
  auth.organizationId = "org-1";
  auth.role = "owner";
  vi.mocked(api.listProviders).mockResolvedValue([
    accountHetznerPrimary,
    accountHetznerSecondary,
    accountVultr,
  ]);
  vi.mocked(api.listPlans).mockResolvedValue([
    planLocked,
    planUnlockedSecondary,
    planVultr,
  ]);
  vi.mocked(api.listProviderTypes).mockResolvedValue(providerTypes);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.resetAllMocks();
});

function openDropdown(button: HTMLElement) {
  fireEvent.pointerDown(button, { button: 0, ctrlKey: false });
  fireEvent.click(button);
}

it("UX01/UX02/UX03/UX04: redirects /providers to /plans, renders plan-first table, and does not open editor on row click", async () => {
  mount("/providers");
  await waitFor(() =>
    expect(screen.getByTestId("location-path")).toHaveTextContent("/plans"),
  );
  expect(await screen.findByRole("heading", { name: "Plans" })).toBeInTheDocument();

  // Shared toolbar controls
  expect(screen.getByRole("button", { name: "Refresh plans" })).toBeInTheDocument();
  expect(screen.getByLabelText("Search plans")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: /Filter & sort/i })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Provider accounts" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Add plan" })).toBeInTheDocument();

  // Standard table columns
  expect(screen.getByRole("columnheader", { name: "Name" })).toBeInTheDocument();
  expect(
    screen.getByRole("columnheader", { name: "Provider account" }),
  ).toBeInTheDocument();
  expect(screen.getByRole("columnheader", { name: "Location" })).toBeInTheDocument();
  expect(screen.getByRole("columnheader", { name: "Server type" })).toBeInTheDocument();
  expect(screen.getByRole("columnheader", { name: "Status" })).toBeInTheDocument();
  expect(screen.getByRole("columnheader", { name: "Actions" })).toBeInTheDocument();

  // Separate accounts of the same provider and lock indicator
  expect(screen.getByText("Hetzner primary")).toBeInTheDocument();
  expect(screen.getByText("Hetzner secondary")).toBeInTheDocument();
  expect(screen.getByLabelText("Placement locked")).toBeInTheDocument();
  expect(screen.getByText("3 plans")).toBeInTheDocument();

  // Clicking plain row cell does not open an editor
  fireEvent.click(screen.getByText("hetzner-small"));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});

it("UX05/UX10: searches, filters by account/status, and exposes dual disabled states", async () => {
  const disabledPlanOnDisabledAccount: api.ProviderPlan = {
    ...planUnlockedSecondary,
    id: "plan-disabled-both",
    name: "both-disabled-plan",
    enabled: false,
    accountEnabled: false,
  };
  const enabledPlanOnDisabledAccount: api.ProviderPlan = {
    ...planVultr,
    id: "plan-acc-disabled",
    name: "account-disabled-plan",
    enabled: true,
    accountEnabled: false,
  };
  vi.mocked(api.listProviders).mockResolvedValue([
    accountHetznerPrimary,
    { ...accountHetznerSecondary, enabled: false },
    { ...accountVultr, enabled: false },
  ]);
  vi.mocked(api.listPlans).mockResolvedValue([
    planLocked,
    disabledPlanOnDisabledAccount,
    enabledPlanOnDisabledAccount,
  ]);

  mount("/plans");
  expect(await screen.findByText("hetzner-small")).toBeInTheDocument();
  expect(screen.getByText("Account disabled")).toBeInTheDocument();

  // Disclosure on plan where both plan and account are disabled
  const disclosureBtn = screen.getByRole("button", { name: "Disabled" });
  fireEvent.click(disclosureBtn);
  expect(screen.getAllByText("Account disabled").length).toBeGreaterThanOrEqual(2);

  // Search by account name distinguishes two Hetzner accounts
  fireEvent.change(screen.getByLabelText("Search plans"), {
    target: { value: "Hetzner secondary" },
  });
  expect(screen.getByText("both-disabled-plan")).toBeInTheDocument();
  expect(screen.queryByText("hetzner-small")).not.toBeInTheDocument();
  expect(screen.getByText("1 of 3 plans")).toBeInTheDocument();
});

it("UX06: guides connecting an account when no enabled accounts exist and resumes Add plan", async () => {
  vi.mocked(api.listProviders).mockResolvedValueOnce([]);
  vi.mocked(api.listPlans).mockResolvedValueOnce([]);
  vi.mocked(api.createProvider).mockResolvedValue(accountHetznerPrimary);

  mount("/plans");
  expect(await screen.findByText("No plans")).toBeInTheDocument();

  // Add plan remains discoverable
  fireEvent.click(screen.getAllByRole("button", { name: "Add plan" })[0]);
  const dialog = await screen.findByRole("dialog");
  expect(
    within(dialog).getByText(
      /Connect an enabled provider account before adding a deployment plan/i,
    ),
  ).toBeInTheDocument();

  // Connect account transitions to Add account and then resumes Add plan with new account selected
  vi.mocked(api.listProviders).mockResolvedValue([accountHetznerPrimary]);
  fireEvent.click(within(dialog).getByRole("button", { name: "Connect account" }));
  expect(
    await within(dialog).findByRole("heading", { name: "Add account" }),
  ).toBeInTheDocument();

  fireEvent.change(within(dialog).getByLabelText("Account name"), {
    target: { value: "Hetzner primary" },
  });
  fireEvent.change(within(dialog).getByLabelText("API token or key"), {
    target: { value: "secret-token-123" },
  });
  fireEvent.click(within(dialog).getByRole("button", { name: "Add account" }));

  await waitFor(() =>
    expect(api.createProvider).toHaveBeenCalledWith({
      organizationId: "org-1",
      name: "Hetzner primary",
      providerType: "hetzner",
      credential: "secret-token-123",
      projectId: undefined,
      defaultOsId: undefined,
    }),
  );

  // Resumes Add plan with new account selected
  expect(
    await within(screen.getByRole("dialog")).findByRole("heading", {
      name: "Add plan",
    }),
  ).toBeInTheDocument();
  expect(screen.getByLabelText("Provider account")).toHaveValue("account-h1");
});

it("UX06: warns when a newly added plan is hidden by active filters and offers Clear filters", async () => {
  const createdPlan: api.ProviderPlan = {
    ...planVultr,
    id: "plan-new",
    name: "vultr-hidden-plan",
  };
  vi.mocked(api.createPlan).mockResolvedValue(createdPlan);

  mount("/plans");
  await screen.findByText("hetzner-small");

  // Search for hetzner so a vultr plan will be hidden
  fireEvent.change(screen.getByLabelText("Search plans"), {
    target: { value: "hetzner" },
  });
  expect(screen.queryByText("vultr-basic-1gb")).not.toBeInTheDocument();

  vi.mocked(api.listPlans).mockResolvedValue([
    planLocked,
    planUnlockedSecondary,
    planVultr,
    createdPlan,
  ]);

  fireEvent.click(screen.getByRole("button", { name: "Add plan" }));
  const dialog = await screen.findByRole("dialog");
  fireEvent.change(within(dialog).getByLabelText("Name"), {
    target: { value: "vultr-hidden-plan" },
  });
  fireEvent.change(within(dialog).getByLabelText("Provider account"), {
    target: { value: "account-v1" },
  });
  fireEvent.change(within(dialog).getByLabelText("Location"), {
    target: { value: "ewr" },
  });
  fireEvent.change(within(dialog).getByLabelText("Server type"), {
    target: { value: "vc2-1c-1gb" },
  });
  fireEvent.click(within(dialog).getByRole("button", { name: "Add plan" }));

  expect(
    await screen.findByText(/was added, but is hidden by active filters/i),
  ).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
  expect(await screen.findByText("vultr-hidden-plan")).toBeInTheDocument();
});

it("UX07/UX08: keeps account read-only on edit, locks placement for reserved plans, and preserves collapsed advanced settings", async () => {
  vi.mocked(api.updatePlan).mockResolvedValue({
    ...planLocked,
    name: "hetzner-renamed",
  });
  mount("/plans");
  await screen.findByText("hetzner-small");

  // Edit locked plan (hetzner-small row)
  const lockedRow = screen.getByText("hetzner-small").closest("tr")!;
  fireEvent.click(within(lockedRow).getByRole("button", { name: "Edit" }));
  const dialog = await screen.findByRole("dialog");

  expect(
    within(dialog).getByText(
      "This plan's placement is locked. You can rename it or duplicate it to choose a different placement.",
    ),
  ).toBeInTheDocument();
  expect(within(dialog).getByLabelText("Provider account")).toBeDisabled();
  expect(within(dialog).getByLabelText("Location")).toBeDisabled();
  expect(within(dialog).getByLabelText("Server type")).toBeDisabled();

  fireEvent.change(within(dialog).getByLabelText("Name"), {
    target: { value: "hetzner-renamed" },
  });
  fireEvent.click(within(dialog).getByRole("button", { name: "Save changes" }));

  await waitFor(() =>
    expect(api.updatePlan).toHaveBeenCalledWith("plan-1", {
      name: "hetzner-renamed",
      revision: 3,
    }),
  );

  // Edit unlocked plan (hetzner-secondary-small row) - Advanced settings remain collapsed, yet image & arm64 platform survive
  vi.mocked(api.updatePlan).mockResolvedValue({
    ...planUnlockedSecondary,
    region: "fsn1",
  });
  const unlockedRow = screen.getByText("hetzner-secondary-small").closest("tr")!;
  fireEvent.click(within(unlockedRow).getByRole("button", { name: "Edit" }));
  const unlockedDialog = await screen.findByRole("dialog");
  expect(within(unlockedDialog).getByLabelText("Provider account")).toBeDisabled();
  expect(within(unlockedDialog).getByLabelText("Location")).toBeEnabled();
  fireEvent.change(within(unlockedDialog).getByLabelText("Location"), {
    target: { value: "fsn1" },
  });
  fireEvent.click(
    within(unlockedDialog).getByRole("button", { name: "Save changes" }),
  );

  await waitFor(() =>
    expect(api.updatePlan).toHaveBeenCalledWith("plan-2", {
      name: "hetzner-secondary-small",
      region: "fsn1",
      sku: "cx23",
      image: "ubuntu-24.04",
      buildPlatform: "linux/arm64",
      revision: 1,
    }),
  );
});

it("UX06/UX07: duplicates a plan with a fresh UUID and clears incompatible placement when switching account", async () => {
  vi.mocked(api.createPlan).mockResolvedValue(planVultr);
  mount("/plans");
  await screen.findByText("hetzner-small");

  openDropdown(screen.getByRole("button", { name: "Actions for hetzner-small" }));
  fireEvent.click(await screen.findByRole("menuitem", { name: "Duplicate plan" }));

  const dialog = await screen.findByRole("dialog");
  expect(
    within(dialog).getByRole("heading", { name: "Duplicate plan" }),
  ).toBeInTheDocument();
  const nameInput = within(dialog).getByLabelText("Name") as HTMLInputElement;
  expect(nameInput.value).toMatch(/^[0-9a-f-]{36}$/);
  expect(within(dialog).getByLabelText("Location")).toHaveValue("nbg1");
  expect(within(dialog).getByLabelText("Server type")).toHaveValue("cx23");

  // Switch account to Vultr primary: clears region/sku and resets image default
  fireEvent.change(within(dialog).getByLabelText("Provider account"), {
    target: { value: "account-v1" },
  });
  expect(within(dialog).getByLabelText("Location")).toHaveValue("");
  expect(within(dialog).getByLabelText("Server type")).toHaveValue("");

  fireEvent.change(nameInput, { target: { value: "vultr-dup" } });
  fireEvent.change(within(dialog).getByLabelText("Location"), {
    target: { value: "ewr" },
  });
  fireEvent.change(within(dialog).getByLabelText("Server type"), {
    target: { value: "vc2-1c-1gb" },
  });
  fireEvent.click(within(dialog).getByRole("button", { name: "Add plan" }));

  await waitFor(() =>
    expect(api.createPlan).toHaveBeenCalledWith({
      organizationId: "org-1",
      providerAccountId: "account-v1",
      name: "vultr-dup",
      region: "ewr",
      sku: "vc2-1c-1gb",
      image: null,
      buildPlatform: "linux/amd64",
    }),
  );
});

it("UX09: preserves entered name on revision/lock conflict and requires reload before saving", async () => {
  vi.mocked(api.updatePlan).mockRejectedValueOnce(
    new ApiError(409, {
      code: "plan_mapping_locked",
      message:
        "This plan has been reserved for provisioning. Duplicate it to change its mapping.",
      details: [],
    }),
  );

  mount("/plans");
  await screen.findByText("hetzner-secondary-small");

  // Edit unlocked plan-2
  const unlockedRow = screen.getByText("hetzner-secondary-small").closest("tr")!;
  fireEvent.click(within(unlockedRow).getByRole("button", { name: "Edit" }));
  const dialog = await screen.findByRole("dialog");

  fireEvent.change(within(dialog).getByLabelText("Name"), {
    target: { value: "kept-renamed-plan" },
  });
  fireEvent.change(within(dialog).getByLabelText("Location"), {
    target: { value: "hel1" },
  });
  fireEvent.click(within(dialog).getByRole("button", { name: "Save changes" }));

  // Conflict shown, dialog stays open, Save changes disabled until reload, entered name kept
  expect(await within(dialog).findByRole("alert")).toHaveTextContent(
    "This plan has been reserved for provisioning.",
  );
  expect(within(dialog).getByLabelText("Name")).toHaveValue("kept-renamed-plan");
  expect(within(dialog).getByRole("button", { name: "Save changes" })).toBeDisabled();

  // Reload returns plan-2 now locked with revision 5
  const newlyLockedPlan: api.ProviderPlan = {
    ...planUnlockedSecondary,
    revision: 5,
    provisioningLocked: true,
  };
  vi.mocked(api.listPlans).mockResolvedValue([
    planLocked,
    newlyLockedPlan,
    planVultr,
  ]);
  vi.mocked(api.updatePlan).mockResolvedValue({
    ...newlyLockedPlan,
    name: "kept-renamed-plan",
  });

  fireEvent.click(
    within(dialog).getByRole("button", { name: "Reload current data" }),
  );

  await waitFor(() =>
    expect(within(dialog).getByLabelText("Location")).toBeDisabled(),
  );
  expect(within(dialog).getByLabelText("Name")).toHaveValue("kept-renamed-plan");

  fireEvent.click(within(dialog).getByRole("button", { name: "Save changes" }));
  await waitFor(() =>
    expect(api.updatePlan).toHaveBeenCalledWith("plan-2", {
      name: "kept-renamed-plan",
      revision: 5,
    }),
  );
});

it("UX11/UX12: manages secondary Provider accounts collection, credential rotation, View plans filter, and delete protection", async () => {
  vi.mocked(api.replaceProviderCredential).mockImplementation(
    () => new Promise(() => {}),
  );
  mount("/plans");
  await screen.findByText("hetzner-small");

  // Navigate to Provider accounts
  fireEvent.click(screen.getByRole("button", { name: "Provider accounts" }));
  expect(
    await screen.findByRole("heading", { name: "Provider accounts" }),
  ).toBeInTheDocument();
  expect(screen.getByText("Hetzner primary")).toBeInTheDocument();
  expect(screen.getByText("Hetzner secondary")).toBeInTheDocument();

  // Accessible connection timestamp disclosure
  const connDisclosure = screen.getAllByRole("button", {
    name: /Valid · Checked/i,
  })[0];
  fireEvent.click(connDisclosure);
  expect(screen.getAllByText(/Checked/i).length).toBeGreaterThanOrEqual(2);

  // Replace credential keeps input write-only and clears immediately on submit
  openDropdown(
    screen.getByRole("button", { name: "Actions for Hetzner primary" }),
  );
  fireEvent.click(
    await screen.findByRole("menuitem", { name: "Replace credential" }),
  );
  const dialog = await screen.findByRole("dialog");
  const credField = within(dialog).getByLabelText("New API token or key");
  expect(credField).toHaveAttribute("type", "password");
  expect(credField).toHaveValue("");
  fireEvent.change(credField, { target: { value: "synthetic-secret" } });
  fireEvent.click(within(dialog).getByRole("button", { name: "Save credential" }));

  await waitFor(() =>
    expect(api.replaceProviderCredential).toHaveBeenCalledWith(
      "account-h1",
      "synthetic-secret",
      2,
    ),
  );
  expect(credField).toHaveValue("");
  expect(screen.queryByText("synthetic-secret")).not.toBeInTheDocument();
});

it("UX11/UX12: View plans filters the Plans collection by account, and blocked delete suggests Disable", async () => {
  vi.mocked(api.deleteProvider).mockRejectedValue(
    new Error("Provider retains volumes."),
  );
  mount("/plans/accounts");
  await screen.findByText("Hetzner primary");

  // Delete account blocked by server shows error and offers Disable account
  openDropdown(
    screen.getByRole("button", { name: "Actions for Hetzner primary" }),
  );
  fireEvent.click(
    await screen.findByRole("menuitem", { name: "Delete account" }),
  );
  const dialog = await screen.findByRole("dialog");
  expect(within(dialog).getByText("Hetzner primary")).toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
  expect(await within(dialog).findByRole("alert")).toHaveTextContent(
    "Provider retains volumes.",
  );
  expect(
    within(dialog).getByRole("button", { name: "Disable account" }),
  ).toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

  // View plans navigates to /plans with exact account filter applied
  openDropdown(
    screen.getByRole("button", { name: "Actions for Hetzner secondary" }),
  );
  fireEvent.click(await screen.findByRole("menuitem", { name: "View plans" }));

  expect(await screen.findByRole("heading", { name: "Plans" })).toBeInTheDocument();
  expect(screen.getByText("hetzner-secondary-small")).toBeInTheDocument();
  expect(screen.queryByText("hetzner-small")).not.toBeInTheDocument();
  expect(screen.getByText("1 of 3 plans")).toBeInTheDocument();
});

it("UX13/UX14: retains rows on failed refresh, isolates organization switches, and hides mutations for viewers", async () => {
  auth.role = "viewer";
  const view = mount("/plans");
  expect(await screen.findByText("hetzner-small")).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Add plan" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Actions for hetzner-small" }),
  ).not.toBeInTheDocument();

  // Failed refresh retains current rows and shows error
  vi.mocked(api.listPlans).mockRejectedValueOnce(
    new Error("Temporary network error."),
  );
  fireEvent.click(screen.getByRole("button", { name: "Refresh plans" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Temporary network error.",
  );
  expect(screen.getByText("hetzner-small")).toBeInTheDocument();

  // Organization switch clears previous organization's rows immediately
  let resolveOrg2Plans!: (value: api.ProviderPlan[]) => void;
  vi.mocked(api.listPlans).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        resolveOrg2Plans = resolve;
      }),
  );
  auth.organizationId = "org-2";
  view.rerender(
    <MemoryRouter initialEntries={["/plans"]}>
      <LocationProbe />
      <Routes>
        <Route path="/plans" element={<ProvidersModule />} />
      </Routes>
    </MemoryRouter>,
  );

  expect(screen.queryByText("hetzner-small")).not.toBeInTheDocument();
  expect(screen.getByRole("status")).toHaveTextContent("Loading plans…");

  await act(async () => {
    resolveOrg2Plans([]);
  });
  expect(await screen.findByText("No plans")).toBeInTheDocument();
});
