import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getEnvironment, updateEnvironment } from "@/api/applicationsApi";
import type { ApplicationEnvironment } from "@/models/application";
import { EnvironmentSleepSettings } from "./EnvironmentSleepSettings";

vi.mock("@/api/applicationsApi", () => ({ getEnvironment: vi.fn(), updateEnvironment: vi.fn() }));

function environment(overrides: Partial<ApplicationEnvironment> = {}): ApplicationEnvironment {
  return {
    id: "env_stage",
    applicationId: "app_1",
    organizationId: "org_1",
    name: "staging",
    kind: "persistent",
    runtimeSlug: "sample-staging",
    plan: "small",
    planId: null,
    database: "sqlite",
    sourceBranch: null,
    sourceEnvironmentId: null,
    deletionStatus: null,
    deletionError: null,
    placementLocked: false,
    domains: [],
    latestDeployment: null,
    runtimeObservation: {
      state: "running",
      connected: true,
      stale: false,
      observedAt: null,
      lastSeenAt: null,
      totalServices: 0,
      healthyServices: 0,
    },
    createdAt: "2026-11-28T12:00:00Z",
    updatedAt: "2026-11-28T12:00:00Z",
    sleep: {
      policy: { sleepAfterMinutes: null, httpTrafficIsActivity: true, revision: 7 },
      state: "awake",
      stateChangedAt: "2026-11-28T12:00:00Z",
      lastActivityAt: null,
      lastHttpRequestAt: null,
      keepAwakeUntil: null,
      sleepsAt: null,
      eligible: true,
      ineligibleReason: null,
      addressMode: "dynamic",
      retainedAddress: null,
      snapshot: null,
      operation: null,
      lastError: null,
    },
    ...overrides,
  };
}

function renderSettings(env = environment()) {
  return render(
    <MemoryRouter>
      <EnvironmentSleepSettings
        applicationId="app_1"
        environment={env}
        canManage
        onSaved={vi.fn().mockResolvedValue(undefined)}
      />
    </MemoryRouter>,
  );
}

describe("EnvironmentSleepSettings", () => {
  afterEach(cleanup);
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(updateEnvironment).mockResolvedValue(environment());
  });

  it("loads the selected environment policy before enabling edits", async () => {
    let resolveEnvironment!: (value: ApplicationEnvironment) => void;
    vi.mocked(getEnvironment).mockReturnValueOnce(new Promise(resolve => { resolveEnvironment = resolve; }));
    renderSettings(environment({ sleep: null }));

    expect(screen.getByRole("status")).toHaveTextContent("Loading sleep settings…");
    expect(screen.getByRole("button", { name: "Save sleep settings" })).toBeDisabled();
    resolveEnvironment(environment());
    expect(await screen.findByRole("switch", { name: "Sleep when idle" })).toBeEnabled();
    expect(screen.queryByText("Loading sleep settings…")).not.toBeInTheDocument();
  });

  it("defaults the opt-in timeout to two hours and saves the activity policy", async () => {
    renderSettings();
    expect(screen.getByRole("switch", { name: "Sleep when idle" })).not.toBeChecked();
    expect(screen.getByLabelText("Idle timeout")).toHaveValue("120");
    expect(screen.getByRole("switch", { name: "Count website traffic as activity" })).toBeChecked();
    expect(screen.getByText(/Selfrun updates its managed DNS records/)).toBeInTheDocument();
    expect(screen.getByText(/On DigitalOcean, restoring the server from a snapshot may give it a different IP address/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("switch", { name: "Sleep when idle" }));
    fireEvent.click(screen.getByRole("switch", { name: "Count website traffic as activity" }));
    fireEvent.click(screen.getByRole("button", { name: "Save sleep settings" }));

    await waitFor(() => expect(updateEnvironment).toHaveBeenCalledWith("app_1", "env_stage", {
      sleep: { policy: { enabled: true, sleepAfterMinutes: 120, httpTrafficIsActivity: false, revision: 7 } },
    }));
  });

  it("explains address changes and disables controls when the environment is ineligible", () => {
    const env = environment({
      sleep: {
        ...environment().sleep!,
        eligible: false,
        ineligibleReason: "Provider snapshots are unavailable.",
        addressMode: "retained",
        retainedAddress: { ipv4: "203.0.113.20", state: "assigned", previousIpv4: "203.0.113.10" },
      },
    });
    renderSettings(env);

    expect(screen.getByText(/provider bills it while the environment is asleep/)).toBeInTheDocument();
    expect(screen.getByText(/on DigitalOcean it may differ from the server's previous IP/)).toBeInTheDocument();
    const statusMessages = screen.getAllByRole("status").map(status => status.textContent);
    expect(statusMessages.some(message => message?.includes("Provider snapshots are unavailable."))).toBe(true);
    expect(statusMessages.some(message => message?.includes("203.0.113.10"))).toBe(true);
    expect(statusMessages.some(message => message?.includes("203.0.113.20"))).toBe(true);
    expect(screen.getByText(/Update each verified custom domain.s A record/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Review environment domains" })).toHaveAttribute(
      "href",
      "/apps/app_1/environments/env_stage/domains",
    );
    expect(screen.getByRole("switch", { name: "Sleep when idle" })).toBeDisabled();
  });

  it("keeps the form mounted and exposes save errors", async () => {
    vi.mocked(updateEnvironment).mockRejectedValueOnce(new Error("Policy revision conflict."));
    renderSettings();
    fireEvent.click(screen.getByRole("switch", { name: "Sleep when idle" }));
    fireEvent.click(screen.getByRole("button", { name: "Save sleep settings" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Policy revision conflict.");
    expect(screen.getByRole("switch", { name: "Sleep when idle" })).toBeChecked();
  });

  it("announces the saving state while the policy request is pending", async () => {
    let resolveUpdate!: (value: ApplicationEnvironment) => void;
    vi.mocked(updateEnvironment).mockReturnValueOnce(new Promise(resolve => { resolveUpdate = resolve; }));
    renderSettings();
    fireEvent.click(screen.getByRole("switch", { name: "Sleep when idle" }));
    fireEvent.click(screen.getByRole("button", { name: "Save sleep settings" }));

    expect(await screen.findByRole("button", { name: "Saving…" })).toBeDisabled();
    resolveUpdate(environment());
    await waitFor(() => expect(updateEnvironment).toHaveBeenCalledTimes(1));
  });
});
