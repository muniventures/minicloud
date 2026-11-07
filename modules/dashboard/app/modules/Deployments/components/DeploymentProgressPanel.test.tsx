import { act, cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type {
  DeploymentEventResponse,
  DeploymentResponse,
} from "@/models/controlPlane";
import { DeploymentProgressPanel } from "./DeploymentProgressPanel";
import { formatDuration } from "../deploymentDetailPresentation";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

it("shows readable stage durations, ticks only the active stage, and freezes completed stages", () => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-10-24T01:12:15Z"));
  const deployment = {
    id: "dep",
    status: "deploying",
    createdAt: "2026-10-24T01:08:06.845Z",
    startedAt: "2026-10-24T01:08:07.293Z",
  } as DeploymentResponse;
  const events = [
    {
      id: "queued",
      message: "Deployment queued.",
      createdAt: deployment.createdAt,
      level: "info",
    },
    {
      id: "claim",
      message: "Deployment claimed by worker.",
      createdAt: deployment.startedAt,
      level: "info",
      metadataJson: JSON.stringify({
        phase: "queue_wait",
        duration_ms: 448,
        outcome: "succeeded",
      }),
    },
    {
      id: "deploy",
      message:
        "Server ready; DNS and artifact/build preparation starting concurrently.",
      createdAt: "2026-10-24T01:08:07.939Z",
      level: "info",
    },
  ] as DeploymentEventResponse[];
  const { rerender } = render(
    <DeploymentProgressPanel
      deployment={deployment}
      events={events}
      logs={[]}
      observedAt={Date.now()}
    />,
  );
  expect(
    within(screen.getByRole("listitem", { name: "Queue, complete" })).getByText(
      "<1s",
    ),
  ).toBeInTheDocument();
  expect(
    within(
      screen.getByRole("listitem", { name: "Prepare, complete" }),
    ).getByText("<1s"),
  ).toBeInTheDocument();
  expect(
    within(screen.getByRole("listitem", { name: "Deploy, running" })).getByText(
      "4m 7s",
    ),
  ).toBeInTheDocument();
  expect(screen.getByText("4m 8s elapsed")).toBeInTheDocument();
  act(() => vi.advanceTimersByTime(1000));
  expect(
    within(screen.getByRole("listitem", { name: "Deploy, running" })).getByText(
      "4m 8s",
    ),
  ).toBeInTheDocument();
  rerender(
    <DeploymentProgressPanel
      deployment={{ ...deployment, status: "verifying" }}
      events={[
        ...events,
        {
          id: "verify",
          level: "info",
          message: "Build done, containers started; running health checks.",
          createdAt: "2026-10-24T01:12:16Z",
        },
      ]}
      logs={[]}
      observedAt={Date.now()}
    />,
  );
  act(() => vi.advanceTimersByTime(2000));
  expect(
    within(
      screen.getByRole("listitem", { name: "Deploy, complete" }),
    ).getByText("4m 8s"),
  ).toBeInTheDocument();
  expect(
    within(screen.getByRole("listitem", { name: "Verify, running" })).getByText(
      "2s",
    ),
  ).toBeInTheDocument();
  expect(
    within(screen.getByRole("listitem", { name: "Queue, complete" })).getByText(
      "<1s",
    ),
  ).toBeInTheDocument();
  expect(
    within(
      screen.getByRole("listitem", { name: "Prepare, complete" }),
    ).getByText("<1s"),
  ).toBeInTheDocument();
});

it("shows human-readable durations without milliseconds, decimals, or invalid times", () => {
  expect(formatDuration(0)).toBe("<1s");
  expect(formatDuration(0.4)).toBe("<1s");
  expect(formatDuration(447.951)).toBe("<1s");
  expect(formatDuration(646)).toBe("<1s");
  expect(formatDuration(999)).toBe("<1s");
  expect(formatDuration(1000)).toBe("1s");
  expect(formatDuration(1066.966)).toBe("1s");
  expect(formatDuration(59999)).toBe("59s");
  expect(formatDuration(60000)).toBe("1m 0s");
  expect(formatDuration(248061)).toBe("4m 8s");
  expect(formatDuration(NaN)).toBe("—");
  expect(formatDuration(-100)).toBe("—");
});

it("keeps the local build duration visible and frozen when the deployment enters the queue", () => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-10-31T16:50:00Z"));
  const deployment = {
    id: "local",
    status: "waiting_for_build",
    createdAt: "2026-10-31T16:45:00Z",
    startedAt: null,
  } as DeploymentResponse;
  const events: DeploymentEventResponse[] = [{
    id: "local",
    level: "info",
    message: "Waiting for the CLI to build and publish local images.",
    createdAt: deployment.createdAt,
  }];
  const { rerender } = render(
    <DeploymentProgressPanel deployment={deployment} events={events} logs={[]} observedAt={Date.now()} />,
  );
  expect(within(screen.getByRole("listitem", { name: "Local build, running" })).getByText("5m 0s")).toBeInTheDocument();
  expect(within(screen.getByRole("listitem", { name: "Queue, pending" })).getByText("Pending")).toBeInTheDocument();

  rerender(
    <DeploymentProgressPanel
      deployment={{ ...deployment, status: "queued" }}
      events={[...events, { id: "queue", level: "info", message: "Deployment queued.", createdAt: "2026-10-31T16:50:00Z" }]}
      logs={[]}
      observedAt={Date.now()}
    />,
  );
  act(() => vi.advanceTimersByTime(2000));
  expect(within(screen.getByRole("listitem", { name: "Local build, complete" })).getByText("5m 0s")).toBeInTheDocument();
  expect(within(screen.getByRole("listitem", { name: "Queue, running" })).getByText("2s")).toBeInTheDocument();
  expect(screen.getByText("5m 2s elapsed")).toBeInTheDocument();
});
