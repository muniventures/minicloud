import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup } from "@testing-library/react";
import { sleepEnvironment, wakeEnvironment } from "@/api/applicationsApi";
import type { ApplicationEnvironment } from "@/models/application";
import { EnvironmentSleepStatus } from "./EnvironmentSleepStatus";

vi.mock("@/api/applicationsApi", () => ({
  sleepEnvironment: vi.fn(),
  wakeEnvironment: vi.fn(),
}));

const operation = {
  id: "op_1",
  kind: "wake" as const,
  status: "queued",
  step: null,
  trigger: "manual_dashboard",
};

function environment(state: string): ApplicationEnvironment {
  return {
    id: "env_1",
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
      policy: { sleepAfterMinutes: 120, httpTrafficIsActivity: true, revision: 1 },
      state,
      stateChangedAt: "2026-11-28T18:04:00Z",
      lastActivityAt: "2026-11-28T16:00:00Z",
      lastHttpRequestAt: null,
      keepAwakeUntil: null,
      sleepsAt: "2099-01-01T18:40:00Z",
      eligible: true,
      ineligibleReason: null,
      addressMode: "dynamic",
      retainedAddress: null,
      snapshot: { createdAt: "2026-11-28T18:04:00Z", sizeGb: 9.4, pendingPurge: false },
      operation: null,
      lastError: null,
    },
  };
}

describe("EnvironmentSleepStatus", () => {
  afterEach(cleanup);

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(sleepEnvironment).mockResolvedValue({ operation: { ...operation, kind: "sleep" } });
    vi.mocked(wakeEnvironment).mockResolvedValue({ operation });
  });

  it("shows asleep status and runtime unavailability, then queues a named wake action", async () => {
    const onRefresh = vi.fn().mockResolvedValue(undefined);
    render(
      <EnvironmentSleepStatus
        applicationId="app_1"
        applicationName="Sample"
        environment={environment("asleep")}
        onRefresh={onRefresh}
      />,
    );

    expect(screen.getByRole("status")).toHaveTextContent("Asleep since");
    expect(screen.getByRole("status")).toHaveTextContent("snapshot 9.4 GB");
    expect(screen.getByRole("status")).toHaveTextContent("Asleep since");
    fireEvent.click(screen.getByRole("button", { name: "Wake" }));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByRole("heading", { name: "Wake this environment?" })).toBeInTheDocument();
    expect(within(dialog).getByText(/Wake Sample \/ staging/)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "Wake environment" }));

    await waitFor(() => expect(wakeEnvironment).toHaveBeenCalledWith("app_1", "env_1", 240));
    expect(await screen.findByText("Waking request queued…")).toBeInTheDocument();
    expect(onRefresh).toHaveBeenCalled();
  });

  it("confirms a manual sleep with app and environment names and keeps failures visible", async () => {
    vi.mocked(sleepEnvironment).mockRejectedValueOnce(new Error("Custom domains must resolve to the retained address."));
    render(
      <EnvironmentSleepStatus
        applicationId="app_1"
        applicationName="Sample"
        environment={environment("awake")}
        onRefresh={vi.fn().mockResolvedValue(undefined)}
      />,
    );
    expect(screen.getByText(/Sleeps in about/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Sleep now" }));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText(/Sleep Sample \/ staging/)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "Sleep now" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("Custom domains must resolve to the retained address.");
  });

  it("sends Keep awake and reports API errors accessibly", async () => {
    vi.mocked(wakeEnvironment).mockRejectedValueOnce(new Error("A power operation is already in progress."));
    render(
      <EnvironmentSleepStatus
        applicationId="app_1"
        applicationName="Sample"
        environment={environment("awake")}
        onRefresh={vi.fn().mockResolvedValue(undefined)}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Keep awake" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("A power operation is already in progress.");
    expect(wakeEnvironment).toHaveBeenCalledWith("app_1", "env_1", 240);
  });
});
