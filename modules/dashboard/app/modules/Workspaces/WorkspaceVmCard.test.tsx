import { useEffect } from "react";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { getWorkspaceLaunchUrl, mutateWorkspaceVm, workspaceTask } from "@/api/workspacesApi";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import type { WorkspaceSelection } from "./WorkspaceSelectionFields";
import { WorkspaceVmCard } from "./WorkspaceVmCard";

vi.mock("@/api/workspacesApi", () => ({
  getWorkspaceLaunchUrl: vi.fn(), mutateWorkspaceVm: vi.fn(), workspaceTask: vi.fn(),
}));
vi.mock("./WorkspaceVmResourceChart", () => ({ WorkspaceVmResourceChart: () => null }));
vi.mock("./WorkspaceSelectionFields", () => ({
  WorkspaceSelectionFields: ({ value, onChange, disabled, onReadyChange }: {
    value: WorkspaceSelection; onChange: (selection: WorkspaceSelection) => void; disabled?: boolean;
    onReadyChange: (ready: boolean) => void;
  }) => {
    useEffect(() => onReadyChange(true), [onReadyChange]);
    return <><label>Profile<select value={value.profile} disabled={disabled}
      onChange={event => onChange({ ...value, profile: event.target.value })}><option>Standard</option><option>Heavy</option></select></label>
    <label>Image<select value={value.imageId} disabled={disabled}
      onChange={event => onChange({ ...value, imageId: event.target.value })}><option>image</option><option>image-two</option></select></label></>;
  },
}));

function vm(overrides: Partial<DevelopmentWorkspaceVm> = {}): DevelopmentWorkspaceVm {
  return {
    id: "task-vm", kind: "ephemeral", generation: 3, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "provider", region: "region", sku: "sku",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.1,
    estimatedMonthlyCompute: 72, computeReserved: true, revision: 1, ...overrides,
  };
}
const refresh = vi.fn<() => Promise<void>>();
function card(machine = vm(), canManage = true) {
  return <WorkspaceVmCard workspaceId="workspace" organizationId="org" vm={machine} currency="EUR" refresh={refresh} canManage={canManage} />;
}
function openMenu(scope: HTMLElement = document.body) {
  const trigger = within(scope).getByRole("button", { name: /^More actions for / });
  fireEvent.pointerDown(trigger, { button: 0, ctrlKey: false });
  return screen.getByRole("menu");
}
function menuItem(name: string, scope?: HTMLElement) {
  return within(openMenu(scope)).getByRole("menuitem", { name });
}
function choose(name: string, scope?: HTMLElement) {
  fireEvent.click(menuItem(name, scope));
}
function openPanel(name: "Next-start size…" | "Task leases…" | "VM details…") {
  choose(name);
  return screen.getByRole("dialog");
}
function closePanel() {
  fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
}
function expectStatus(text: string) {
  expect(screen.getAllByText(text).some(element => element.getAttribute("role") === "status")).toBe(true);
}
function openDiscard() {
  choose("Discard local data…");
  return screen.getByRole("dialog", { name: "Discard VM task-vm generation 3" });
}
function confirmDiscard(dialog: HTMLElement, phrase = "discard task-vm 3") {
  fireEvent.change(within(dialog).getByLabelText("Type discard task-vm 3"), { target: { value: phrase } });
  fireEvent.click(within(dialog).getByLabelText(/I acknowledge irreversible/));
}
beforeEach(() => {
  refresh.mockResolvedValue(undefined);
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({
    url: "https://gateway.example/__minicloud/launch?code=synthetic", expiresAt: "2099-01-01T00:00:00Z",
  });
  vi.mocked(workspaceTask).mockResolvedValue({
    vmId: "task-vm", taskId: "build", generation: 3, status: "active", expiresAt: "2099-01-01T00:00:00Z",
  });
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); vi.resetAllMocks(); });

it("viewing a sleeping VM never starts compute or authorizes sessions; start uses current generation", async () => {
  let accept!: (observation: DevelopmentWorkspace) => void;
  vi.mocked(mutateWorkspaceVm).mockImplementationOnce(() => new Promise(resolve => { accept = resolve; }));
  render(card(vm({ phase: "stopped", desiredState: "stopped", computeReserved: false })));
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
  expect(screen.queryByRole("button", { name: "Connect to T3" })).not.toBeInTheDocument();
  expect(within(openPanel("VM details…")).getByText(/doesn't confirm provider deletion/)).toBeInTheDocument();
  closePanel();
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Start VM" }));
  expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "start", { generation: 3 });
  expectStatus("Starting VM…");
  expect(screen.getByRole("button", { name: "Start VM" })).toBeDisabled();
  expect(screen.queryByRole("list", { name: "VM startup stages" })).not.toBeInTheDocument();
  const observation: DevelopmentWorkspace = {
    id: "workspace", organizationId: "org", name: "Synthetic workspace", imageId: "image", profile: "Standard",
    planId: "plan", providerAccountId: "account", region: "region", sku: "sku", volumeSizeGb: 20,
    desiredState: "running", phase: "provisioning", generation: 4, estimatedMonthlyCompute: 72,
    estimatedMonthlyStorage: 1, currency: "EUR", revision: 2, createdAt: "", updatedAt: "",
    vms: [vm({ phase: "provisioning", generation: 4 })],
  };
  await act(async () => accept(observation));
  expect(refresh).toHaveBeenCalledExactlyOnceWith(observation);
});

it("shows only reported startup stages, distinguishes failure, and waits for confirmed readiness", () => {
  const view = render(card(vm({ kind: "primary", phase: "provisioning" })));
  expectStatus("Starting VM…");
  expect(screen.queryByRole("list", { name: "VM startup stages" })).not.toBeInTheDocument();
  expect(screen.getByText(/T3 and previews become available/)).toBeInTheDocument();
  expect(screen.getAllByText(/Waiting for first activity/)).not.toHaveLength(0);
  expect(screen.queryByText(/Last activity: unavailable/i)).not.toBeInTheDocument();

  view.rerender(card(vm({ kind: "primary", phase: "attaching", startupStage: "preparing_storage" })));
  const progress = screen.getByRole("list", { name: "VM startup stages" });
  expect(within(progress).getByText("Completed:")).toBeInTheDocument();
  expect(progress.querySelector("[aria-current=step]")).toHaveTextContent("Current: Preparing storage");
  expect(within(progress).getAllByText("Upcoming:")).toHaveLength(3);

  view.rerender(card(vm({ kind: "primary", phase: "blocked", startupStage: "setting_up", errorCode: "workspace_reconciliation_required" })));
  expectStatus("VM startup failed");
  expect(progress.querySelector("[aria-current=step]")).toHaveTextContent("Last reported: Setting up VM");
  expect(screen.getByText(/contact your operator/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Start VM" })).not.toBeInTheDocument();

  view.rerender(card(vm({ phase: "provisioning", startupStage: "checking_readiness" })));
  expect(screen.getByText("Checking readiness…")).toHaveAttribute("role", "status");
  expect(screen.queryByText("Preparing storage")).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Connect to T3" })).not.toBeInTheDocument();
  view.rerender(card(vm()));
  expect(screen.getByRole("button", { name: "Connect to T3" })).toBeEnabled();
  expect(screen.getAllByText("Ready").some(label => label.getAttribute("role") === "status")).toBe(true);
});
it("never requests another start when a just-created VM's startup page re-renders or remounts", () => {
  const starting = vm({ kind: "primary", generation: 1, phase: "provisioning" });
  const view = render(card(starting));
  view.rerender(card({ ...starting, revision: 2, startupStage: "preparing_vm" }));
  view.unmount();
  render(card(starting));
  expectStatus("Starting VM…");
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
});

it("shows why a newly created VM wasn't started and retries Start once for its generation", async () => {
  let accept!: () => void;
  vi.mocked(mutateWorkspaceVm).mockImplementationOnce(() => new Promise(resolve => { accept = () => resolve(undefined as never); }));
  render(card(vm({ kind: "primary", generation: 0, desiredState: "stopped", phase: "stopped", computeReserved: false,
    errorCode: "workspace_admission_limit", errorMessage: "Organization VM concurrency limit reached." })));
  expect(screen.getByRole("alert")).toHaveTextContent("workspace_admission_limit: Organization VM concurrency limit reached.");
  const retry = screen.getByRole("button", { name: "Retry start" });
  fireEvent.click(retry);
  fireEvent.click(retry);
  expect(mutateWorkspaceVm).toHaveBeenCalledExactlyOnceWith("workspace", "task-vm", "org", "start", { generation: 0 });
  await act(async () => accept());
  expect(refresh).toHaveBeenCalledTimes(1);
});

it("retries a failed startup on the same environment by stopping the attempt and queueing one start", async () => {
  const observation = { id: "workspace" } as DevelopmentWorkspace;
  vi.mocked(mutateWorkspaceVm).mockResolvedValueOnce({ id: "workspace" } as DevelopmentWorkspace).mockResolvedValueOnce(observation);
  render(card(vm({ kind: "primary", generation: 1, phase: "blocked", startupStage: "setting_up",
    errorCode: "workspace_reconciliation_required", errorMessage: "Provider state is unconfirmed." })));
  expectStatus("VM startup failed");
  expect(screen.getByRole("alert")).toHaveTextContent("Provider state is unconfirmed.");
  fireEvent.click(screen.getByRole("button", { name: "Retry start" }));
  await waitFor(() => expect(refresh).toHaveBeenCalledExactlyOnceWith(observation));
  expect(vi.mocked(mutateWorkspaceVm).mock.calls).toEqual([
    ["workspace", "task-vm", "org", "stop", { generation: 1 }], ["workspace", "task-vm", "org", "start", { generation: 1 }],
  ]);
});

it("offers failed-startup retry only to managers of the primary VM", () => {
  const failed = vm({ phase: "blocked", errorCode: "workspace_reconciliation_required" });
  const view = render(card(failed));
  expect(screen.queryByRole("button", { name: "Retry start" })).not.toBeInTheDocument();
  view.rerender(card({ ...failed, kind: "primary" }, false));
  expect(screen.queryByRole("button", { name: "Retry start" })).not.toBeInTheDocument();
});

it("scopes stop retries and shows the Git-save reason, reservation and recovery steps", async () => {
  render(card(vm({ phase: "awaiting_git_save", desiredState: "stopped", errorCode: "remote_unavailable", errorMessage: "Restore repository access." })));
  expect(screen.getByText(/server is still allocated/)).toBeInTheDocument();
  expect(screen.getByText(/Reported reason: Restore repository access/)).toBeInTheDocument();
  expect(screen.getByText(/Restore repository access and authentication/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Connect to T3" })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Retry stop" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "stop", { generation: 3 }));
});

it("queues an explicit next-generation start and allows stop to cancel that queued start", async () => {
  const machine = vm({ phase: "draining", desiredState: "stopped" });
  const view = render(card(machine));
  fireEvent.click(screen.getByRole("button", { name: "Start VM after it stops" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "start", { generation: 3 }));
  view.rerender(card({ ...machine, startAfterStop: true, revision: 2 }));
  expect(screen.getByRole("button", { name: "Start VM after it stops" })).toBeDisabled();
  await waitFor(() => expect(menuItem("Stop VM")).not.toHaveAttribute("aria-disabled"));
  fireEvent.click(screen.getByRole("menuitem", { name: "Stop VM" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenLastCalledWith("workspace", "task-vm", "org", "stop", { generation: 3 }));
});

it("isolates pending requests to the selected VM and surfaces mutation errors", async () => {
  let reject!: (reason: Error) => void;
  vi.mocked(mutateWorkspaceVm).mockImplementationOnce(() => new Promise((_, no) => { reject = no; }));
  render(<>{card()}<WorkspaceVmCard workspaceId="workspace" organizationId="org" vm={vm({ id: "sibling" })} currency="EUR" refresh={refresh} canManage /></>);
  const selected = screen.getByRole("article", { name: "VM task-vm" });
  const sibling = screen.getByRole("article", { name: "VM sibling" });
  choose("Stop VM", selected);
  expect(menuItem("Stop VM", selected)).toHaveAttribute("aria-disabled", "true");
  fireEvent.keyDown(screen.getByRole("menu"), { key: "Escape" });
  expect(menuItem("Stop VM", sibling)).not.toHaveAttribute("aria-disabled");
  fireEvent.keyDown(screen.getByRole("menu"), { key: "Escape" });
  await act(async () => reject(new Error("Shutdown was already claimed.")));
  expect(within(selected).getByRole("alert")).toHaveTextContent("Shutdown was already claimed.");
  expect(refresh).not.toHaveBeenCalled();
});

it("supports generation-scoped idle-stop cancellation, next-start resize and task lease transitions", async () => {
  render(card(vm({ shutdownDeadline: "2099-01-01T00:00:00Z" })));
  choose("Cancel idle stop");
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "cancel-idle-stop", { generation: 3 }));
  openPanel("Next-start size…");
  await waitFor(() => expect(screen.getByRole("button", { name: "Use this size at next start" })).toBeEnabled());
  fireEvent.change(screen.getByLabelText("Profile"), { target: { value: "Heavy" } });
  fireEvent.submit(screen.getByRole("form", { name: "Resize VM task-vm" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenLastCalledWith("workspace", "task-vm", "org", "resize", {
    generation: 3, imageId: "image", profile: "Heavy", planId: "plan",
  }));
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  openPanel("Next-start size…");
  fireEvent.change(screen.getByLabelText("Image"), { target: { value: "image-two" } });
  expect(screen.getByRole("button", { name: "Use this size and image at next start" })).toBeInTheDocument();
  expect(screen.getByText(/This also changes the VM's image/)).toBeInTheDocument();
  closePanel();
  openPanel("Task leases…");
  await waitFor(() => expect(screen.getByRole("button", { name: "Renew keep-awake lease" })).toBeDisabled());
  fireEvent.change(screen.getByLabelText("Task ID"), { target: { value: "build" } });
  for (const [label, complete, cancelled] of [
    ["Renew keep-awake lease", false, false], ["Complete task", true, false], ["Cancel task", true, true],
  ] as const) {
    await waitFor(() => expect(screen.getByRole("button", { name: label })).toBeEnabled());
    fireEvent.click(screen.getByRole("button", { name: label }));
    await waitFor(() => expect(workspaceTask).toHaveBeenLastCalledWith("workspace", "task-vm", "org", "build", 3, complete, cancelled));
  }
});

it("authorizes T3 only on click and preserves the valid link without an immediate refresh", async () => {
  const view = render(card());
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Connect to T3" }));
  const link = await screen.findByRole("link", { name: "Open T3" });
  expect(link).toHaveAttribute("href", expect.stringContaining("https://gateway.example"));
  expect(link).toHaveAttribute("rel", "noopener noreferrer");
  expect(getWorkspaceLaunchUrl).toHaveBeenCalledWith("workspace", "task-vm", "org", 3, "t3");
  expect(refresh).not.toHaveBeenCalled();
  view.rerender(card(vm()));
  expect(screen.getByRole("link", { name: "Open T3" })).toBeInTheDocument();
  view.rerender(card(vm({ revision: 2 })));
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it("uses the registered preview slug and invalidates authorization when the service changes", async () => {
  render(card());
  expect(screen.queryByLabelText("Preview name")).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Open preview" }));
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
  fireEvent.change(screen.getByLabelText("Preview name"), { target: { value: "web" } });
  fireEvent.click(screen.getByRole("button", { name: "Get preview link" }));
  expect(await screen.findByRole("link", { name: "Open preview" })).toBeInTheDocument();
  expect(getWorkspaceLaunchUrl).toHaveBeenCalledWith("workspace", "task-vm", "org", 3, "web");
  fireEvent.change(screen.getByLabelText("Preview name"), { target: { value: "api" } });
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it.each([
  ["http://gateway.example/launch", "2099-01-01T00:00:00Z", /link returned was unsafe/],
  ["not a url", "2099-01-01T00:00:00Z", /link returned was invalid/],
  ["https://user:password@gateway.example/launch", "2099-01-01T00:00:00Z", /link returned was unsafe/],
  ["https://gateway.example/launch", "invalid date", /invalid expiry/],
])("rejects unsafe or expired gateway authorization: %s %s", async (url, expiresAt, expected) => {
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({ url, expiresAt });
  render(card());
  fireEvent.click(screen.getByRole("button", { name: "Connect to T3" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(expected);
  expect(screen.getByRole("alert")).toHaveTextContent("This didn't start the VM");
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it("provides an actionable fallback for rejected gateway authorization without starting compute", async () => {
  vi.mocked(getWorkspaceLaunchUrl).mockRejectedValue(new Error("https://gateway.example/launch?code=synthetic-secret"));
  render(card());
  fireEvent.click(screen.getByRole("button", { name: "Connect to T3" }));
  const alert = await screen.findByRole("alert");
  expect(alert).toHaveTextContent(/Couldn't get a secure link.*Check that the VM is running/);
  expect(alert).not.toHaveTextContent("synthetic-secret");
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it.each([
  { revision: 2 },
  { generation: 4, revision: 2 },
])("rejects delayed authorization after a VM change: %o", async change => {
  let resolve!: (value: { url: string; expiresAt: string }) => void;
  vi.mocked(getWorkspaceLaunchUrl).mockImplementationOnce(() => new Promise(yes => { resolve = yes; }));
  const view = render(card());
  fireEvent.click(screen.getByRole("button", { name: "Connect to T3" }));
  view.rerender(card(vm(change)));
  await act(async () => resolve({ url: "https://gateway.example/old-generation", expiresAt: "2099-01-01T00:00:00Z" }));
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it("recomputes the countdown, expires without a click and explicitly replaces the link", async () => {
  vi.useFakeTimers();
  const now = Date.now();
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValueOnce({
    url: "https://gateway.example/launch?code=synthetic-original", expiresAt: new Date(now + 61_000).toISOString(),
  }).mockResolvedValueOnce({
    url: "https://gateway.example/launch?code=synthetic-replacement", expiresAt: new Date(now + 90_000).toISOString(),
  });
  render(card());
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Connect to T3" })));
  expect(screen.getByRole("link", { name: "Open T3" })).toBeInTheDocument();
  expect(screen.getByText("Expires in 1m").closest("[role=status]")).toBeNull();
  expect(screen.getByText(/not for the session/)).toBeInTheDocument();
  vi.setSystemTime(now + 19_000);
  fireEvent(document, new Event("visibilitychange"));
  expect(screen.getByText("Expires in 42s")).toBeInTheDocument();
  await act(async () => vi.advanceTimersByTimeAsync(42_000));
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
  expect(screen.getByText("Link expired")).toHaveAttribute("role", "status");
  expect(getWorkspaceLaunchUrl).toHaveBeenCalledTimes(1);
  vi.setSystemTime(now);
  fireEvent(window, new Event("pageshow"));
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Get a new link" })));
  expect(screen.getByRole("link", { name: "Open T3" })).toHaveAttribute("href", expect.stringContaining("synthetic-replacement"));
  expect(screen.queryByText("Link expired")).not.toBeInTheDocument();
});

it("guards an expired preview before a delayed timer can run and clears expiry on service or VM changes", async () => {
  vi.useFakeTimers();
  const deadline = Date.now() + 10_000;
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({
    url: "https://gateway.example/launch?code=synthetic-preview", expiresAt: new Date(deadline).toISOString(),
  });
  const view = render(card());
  fireEvent.click(screen.getByRole("button", { name: "Open preview" }));
  fireEvent.change(screen.getByLabelText("Preview name"), { target: { value: "web" } });
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Get preview link" })));
  const link = screen.getByRole("link", { name: "Open preview" });
  vi.setSystemTime(deadline);
  expect(fireEvent.click(link)).toBe(false);
  expect(screen.getByText("Link expired")).toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("Preview name"), { target: { value: "other-service" } });
  expect(screen.queryByText("Link expired")).not.toBeInTheDocument();
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Get preview link" })));
  expect(screen.getByText("Link expired")).toBeInTheDocument();
  view.rerender(card(vm({ id: "another-vm" })));
  expect(screen.queryByText("Link expired")).not.toBeInTheDocument();
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
  expect(getWorkspaceLaunchUrl).toHaveBeenCalledTimes(2);
});

it("clears session authorization permanently on permission loss", async () => {
  const view = render(card());
  fireEvent.click(screen.getByRole("button", { name: "Connect to T3" }));
  await screen.findByRole("link", { name: "Open T3" });
  view.rerender(card(vm(), false));
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
  view.rerender(card());
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it("gates mutations and sessions for read-only access including direct form submission", () => {
  render(card(vm({ shutdownDeadline: "2099-01-01T00:00:00Z" }), false));
  expect(screen.getByRole("button", { name: "Connect to T3" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Open preview" })).toBeDisabled();
  const menu = openMenu();
  for (const name of ["Stop VM", "Cancel idle stop", "Discard local data…"])
    expect(within(menu).getByRole("menuitem", { name })).toHaveAttribute("aria-disabled", "true");
  fireEvent.click(within(menu).getByRole("menuitem", { name: "Discard local data…" }));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  fireEvent.keyDown(menu, { key: "Escape" });
  openPanel("Next-start size…");
  fireEvent.submit(screen.getByRole("form", { name: "Resize VM task-vm" }));
  closePanel();
  openPanel("Task leases…");
  fireEvent.change(screen.getByLabelText("Task ID"), { target: { value: "build" } });
  fireEvent.submit(screen.getByRole("form", { name: "Task lease for VM task-vm" }));
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(workspaceTask).not.toHaveBeenCalled();
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
});

it("cannot renew a lease before readiness but can complete the current generation's task", async () => {
  render(card(vm({ phase: "draining", desiredState: "stopped" })));
  openPanel("Task leases…");
  fireEvent.change(screen.getByLabelText("Task ID"), { target: { value: "build" } });
  expect(screen.getByRole("button", { name: "Renew keep-awake lease" })).toBeDisabled();
  fireEvent.submit(screen.getByRole("form", { name: "Task lease for VM task-vm" }));
  expect(workspaceTask).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Complete task" }));
  await waitFor(() => expect(workspaceTask).toHaveBeenCalledWith("workspace", "task-vm", "org", "build", 3, true, false));
});

it("requires the exact VM/generation phrase and acknowledgement before irreversible discard", async () => {
  render(card());
  const dialog = openDiscard();
  confirmDiscard(dialog, "discard task-vm 2");
  expect(within(dialog).getByRole("button", { name: "Confirm irreversible discard" })).toBeDisabled();
  fireEvent.submit(within(dialog).getByRole("form"));
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  fireEvent.change(within(dialog).getByLabelText("Type discard task-vm 3"), { target: { value: "discard task-vm 3" } });
  fireEvent.click(within(dialog).getByRole("button", { name: "Confirm irreversible discard" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "discard", {
    generation: 3, discardLocalData: true, acknowledgeIgnoredDataLoss: true,
  }));
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
});

it("cancel discard performs no write and reopening clears all confirmation input", () => {
  render(card());
  const dialog = openDiscard();
  confirmDiscard(dialog);
  fireEvent.click(within(dialog).getByRole("button", { name: "Cancel discard" }));
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  const reopened = openDiscard();
  expect(within(reopened).getByLabelText("Type discard task-vm 3")).toHaveValue("");
  expect(within(reopened).getByLabelText(/I acknowledge irreversible/)).not.toBeChecked();
});

it("closes discard and resets lease inputs when the generation changes", async () => {
  const view = render(card());
  openPanel("Task leases…");
  fireEvent.change(screen.getByLabelText("Task ID"), { target: { value: "old-task" } });
  closePanel();
  confirmDiscard(openDiscard());
  view.rerender(card(vm({ generation: 4, revision: 2 })));
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  openPanel("Task leases…");
  expect(screen.getByLabelText("Task ID")).toHaveValue("");
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(workspaceTask).not.toHaveBeenCalled();
});

it("shows a compact header and keeps secondary controls and VM details in the overflow menu", () => {
  render(card(vm({ kind: "primary", lastActivityAt: new Date(Date.now() - 2 * 60_000).toISOString() })));
  const article = screen.getByRole("article", { name: "VM task-vm" });
  expect(within(article).getByRole("heading", { name: "Primary VM" })).toBeInTheDocument();
  expect(within(article).getByText("Running")).toBeInTheDocument();
  expect(article).toHaveTextContent("Standard · sku · region · Active 2m ago");
  expect(within(article).queryByText("Repository")).not.toBeInTheDocument();
  expect(within(article).queryByRole("button", { name: "Stop VM" })).not.toBeInTheDocument();
  expect(within(article).queryByText(/Storage cost/)).not.toBeInTheDocument();
  const menu = openMenu();
  expect(screen.getByRole("button", { name: "More actions for Primary VM" })).toHaveAttribute("aria-expanded", "true");
  expect(within(menu).getAllByRole("menuitem").map(item => item.textContent))
    .toEqual(["Stop VM", "Next-start size…", "Task leases…", "VM details…"]);
  fireEvent.click(within(menu).getByRole("menuitem", { name: "VM details…" }));
  const details = screen.getByRole("dialog", { name: "VM details for Primary VM" });
  expect(details).toHaveTextContent("task-vm · Generation 3");
  expect(details).toHaveTextContent("0.1000 EUR/hour · 72.00 EUR/month");
  expect(details).toHaveTextContent(/Only files on this dev environment's storage are kept/);
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
});

it("labels a temporary VM by work branch with its repository and Git save status, and keeps resources below the controls", () => {
  render(card(vm({ workBranch: "feat/search", repository: "https://git.example/repo.git", gitStatus: "remote_unavailable",
    resources: { state: "current", observedAt: "2026-11-20T10:00:00Z", cpu: { logicalCount: 4 }, memory: null, filesystems: [],
    unavailable: {}, diskUsageSupported: false, directoryDeletionSupported: false } })));
  const article = screen.getByRole("article", { name: "VM task-vm" });
  expect(within(article).getByRole("heading", { name: "feat/search" })).toBeInTheDocument();
  expect(article).toHaveTextContent("Repositoryhttps://git.example/repo.git");
  expect(article).toHaveTextContent("Git saveRemote unavailable");
  const connections = within(article).getByRole("region", { name: "Connections for VM task-vm" });
  const resources = within(article).getByRole("region", { name: "Resources for dev environment task-vm" });
  expect(resources).toHaveTextContent("Resource usage· Latest reading");
  expect(connections.compareDocumentPosition(resources) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  expect(screen.getByRole("button", { name: "More actions for feat/search" })).toBeInTheDocument();
});

it("shows prominent Git recovery with retry stop and discard, and full Git metadata in VM details", async () => {
  render(card(vm({
    phase: "awaiting_git_save", desiredState: "stopped", errorMessage: "Remote unavailable.",
    baseRef: "refs/heads/main", baseCommit: "base-sha", finalHead: "final-sha",
    verifiedRemoteRef: "refs/heads/work", verifiedRemoteCommit: "verified-sha",
  })));
  expect(screen.getAllByText("Git save blocked").length).toBeGreaterThan(0);
  const recovery = screen.getByRole("region", { name: "Git save recovery for VM task-vm" });
  expect(recovery).toHaveClass("bg-warning-surface");
  expect(recovery).toHaveTextContent("Remote unavailable.");
  expect(recovery).toHaveTextContent("server is still allocated");
  expect(within(recovery).getByRole("button", { name: "Discard local data…" })).toBeEnabled();
  const menu = openMenu();
  expect(within(menu).queryByRole("menuitem", { name: "Retry stop" })).not.toBeInTheDocument();
  expect(within(menu).queryByRole("menuitem", { name: "Discard local data…" })).not.toBeInTheDocument();
  fireEvent.click(within(menu).getByRole("menuitem", { name: "VM details…" }));
  const details = screen.getByRole("dialog");
  expect(details).toHaveTextContent("refs/heads/main@base-sha");
  expect(details).toHaveTextContent("final-sha");
  expect(details).toHaveTextContent("refs/heads/work@verified-sha");
  closePanel();
  fireEvent.click(within(recovery).getByRole("button", { name: "Retry stop" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "stop", { generation: 3 }));
});
