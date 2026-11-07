import { describe, expect, it } from "vitest";
import type {
  DeploymentEventResponse,
  DeploymentLogResponse,
  DeploymentResponse,
} from "@/models/controlPlane";
import { deploymentProgress } from "./deploymentProgressPresentation";

const deployment = {
  id: "dep",
  status: "deploying",
  createdAt: "2026-10-23T12:00:00Z",
  startedAt: "2026-10-23T12:00:01Z",
} as DeploymentResponse;
const log = (
  id: string,
  content: string,
  source = "deploy",
): DeploymentLogResponse => ({
  id,
  content,
  source,
  createdAt: `2026-10-23T12:00:${id.padStart(2, "0")}Z`,
});

describe("deployment progress evidence", () => {
  it("shows local build progress without claiming that a worker started", () => {
    const model = deploymentProgress(
      { ...deployment, status: "waiting_for_build", startedAt: null },
      [{ id: "build", createdAt: deployment.createdAt, message: "Waiting for the CLI to build and publish local images.", level: "info" }],
      [],
    );
    expect(model.headline).toBe("Waiting for local build");
    expect(model.stages.map(({ label, state }) => ({ label, state }))).toEqual([
      { label: "Local build", state: "running" },
      { label: "Queue", state: "pending" },
      { label: "Prepare", state: "pending" },
      { label: "Deploy", state: "pending" },
      { label: "Verify", state: "pending" },
    ]);
    expect(model.stages[0].start).toBe(deployment.createdAt);
    expect(model.stages[1].start).toBeNull();
    expect(model.queuedAt).toBeNull();
    expect(model.preview).toEqual([]);
  });
  it("uses the latest phase and bounds multiline output to six lines", () => {
    const model = deploymentProgress(
      deployment,
      [],
      [
        log("1", "===== phase: build_artifacts ====="),
        log("2", "Old build"),
        log("3", "===== phase: start_app_services ====="),
        log("4", "1\n2\n3\n4\n5\n6\n7", "compose"),
        log("5", "Application output", "container/web"),
      ],
    );
    expect(model.headline).toBe("Starting application containers");
    expect(model.preview.map((item) => item.content)).toEqual([
      "2",
      "3",
      "4",
      "5",
      "6",
      "7",
    ]);
  });
  it("does not label unscoped or previous-step output as current", () => {
    const model = deploymentProgress(
      deployment,
      [],
      [log("1", "Unscoped output")],
    );
    expect(model.preview).toEqual([]);
    expect(model.scoped).toBe(false);
    expect(
      deploymentProgress(
        { ...deployment, status: "verifying" },
        [],
        [log("1", "===== phase: complete ====="), log("2", "Earlier output")],
      ).preview,
    ).toEqual([]);
  });
  it("ignores old rollout markers when the same deployment is dispatched again", () => {
    const retried = { ...deployment, startedAt: "2026-10-23T12:01:00Z" };
    const model = deploymentProgress(
      retried,
      [],
      [
        log("1", "===== phase: complete ====="),
        log("2", "Old successful release"),
      ],
    );
    expect(model.headline).toBe("Deploying application");
    expect(model.preview).toEqual([]);
  });
  it("does not use a parallel DNS completion as the active rollout operation", () => {
    const events = [
      {
        id: "e",
        createdAt: "2026-10-23T12:00:09Z",
        level: "info",
        message: "Deployment phase 'dns-setup' succeeded in 100 ms.",
      },
    ] as DeploymentEventResponse[];
    expect(
      deploymentProgress(deployment, events, [
        log("1", "===== phase: build_artifacts ====="),
      ]).headline,
    ).toBe("Building application images");
  });
  it("retains failure on the reported phase and does not complete later stages", () => {
    const events = [
      {
        id: "e",
        createdAt: "2026-10-23T12:00:09Z",
        level: "error",
        message: "Phase failed",
        metadataJson: JSON.stringify({
          phase: "ssh-deploy",
          outcome: "failed",
        }),
      },
    ] as DeploymentEventResponse[];
    const model = deploymentProgress(
      { ...deployment, status: "failed" },
      events,
      [],
    );
    expect(model.stages.map((stage) => stage.state)).toEqual([
      "complete",
      "complete",
      "failed",
      "not run",
    ]);
    expect(model.headline).toBe("Deployment failed");
  });
  it("handles malformed timing metadata and successful terminal deployments", () => {
    const events = [
      {
        id: "e",
        createdAt: "2026-10-23T12:00:09Z",
        message: "Unknown",
        metadataJson: "{",
      },
    ] as DeploymentEventResponse[];
    expect(
      deploymentProgress(
        { ...deployment, status: "succeeded" },
        events,
        [],
      ).stages.every((stage) => stage.state === "complete"),
    ).toBe(true);
    expect(
      deploymentProgress(
        { ...deployment, status: "queued" },
        [],
        [],
      ).stages.map((stage) => stage.state),
    ).toEqual(["running", "pending", "pending", "pending"]);
  });
});

describe("stage transition timing", () => {
  const affected = {
    ...deployment,
    createdAt: "2026-10-24T01:08:06.845439+00:00",
    startedAt: "2026-10-24T01:08:07.293389+00:00",
  };
  const events = [
    {
      id: "queued",
      level: "info",
      message: "Deployment queued.",
      createdAt: affected.createdAt,
    },
    {
      id: "claim",
      level: "info",
      message: "Deployment claimed by worker.",
      createdAt: affected.startedAt,
      metadataJson: JSON.stringify({
        phase: "queue_wait",
        duration_ms: 447.951,
        outcome: "succeeded",
      }),
    },
    {
      id: "prepare",
      level: "info",
      message: "Deployment phase 'resolve-provision' succeeded in 282 ms.",
      createdAt: "2026-10-24T01:08:07.823873+00:00",
      metadataJson: JSON.stringify({
        phase: "resolve-provision",
        started_at_utc: "2026-10-24T01:08:07.5410714+00:00",
        completed_at_utc: "2026-10-24T01:08:07.8238739+00:00",
        duration_ms: 282.431,
        outcome: "succeeded",
      }),
    },
    {
      id: "deploy",
      level: "info",
      message:
        "Server ready; DNS and artifact/build preparation starting concurrently.",
      createdAt: "2026-10-24T01:08:07.939613+00:00",
    },
  ] as DeploymentEventResponse[];

  it("separates queue, whole preparation, and rollout instead of using a nested operation's timestamps", () => {
    const model = deploymentProgress(affected, events, []);
    expect(model.stages[0].start).toBe(affected.createdAt);
    expect(model.stages[0].end).toBe(affected.startedAt);
    expect(model.stages[1].start).toBe(affected.startedAt);
    expect(model.stages[1].end).toBe(events[3].createdAt);
    expect(model.stages[2].start).toBe(events[3].createdAt);
    const durations = model.stages
      .slice(0, 2)
      .map((stage) => Date.parse(stage.end!) - Date.parse(stage.start!));
    expect(durations).toEqual([448, 646]);
  });

  it("freezes deploy at verification start and uses completion for verification end", () => {
    const verifyAt = "2026-10-24T01:12:16+00:00";
    const completedAt = "2026-10-24T01:12:18+00:00";
    const model = deploymentProgress(
      { ...affected, status: "succeeded", completedAt },
      [
        ...events,
        {
          id: "verify",
          level: "info",
          message: "Build done, containers started; running health checks.",
          createdAt: verifyAt,
        },
      ],
      [],
    );
    expect(model.stages[2].end).toBe(verifyAt);
    expect(model.stages[3].start).toBe(verifyAt);
    expect(model.stages[3].end).toBe(completedAt);
    const stageSum = model.stages.reduce(
      (sum, stage) => sum + Date.parse(stage.end!) - Date.parse(stage.start!),
      0,
    );
    expect(stageSum).toBe(Date.parse(completedAt) - Date.parse(model.queuedAt!));
  });

  it("resets queue time to the latest manual dispatch and does not reuse old stage boundaries", () => {
    const requeuedAt = "2026-10-24T02:00:00Z";
    const model = deploymentProgress(
      { ...affected, status: "queued", startedAt: null, requeuedAt },
      [
        ...events,
        {
          id: "retry",
          level: "info",
          message: "Deployment manually queued.",
          createdAt: requeuedAt,
        },
      ],
      [],
    );
    expect(model.queuedAt).toBe(requeuedAt);
    expect(model.totalStartedAt).toBe(requeuedAt);
    expect(model.stages[0].start).toBe(requeuedAt);
    expect(model.stages[0].end).toBeNull();
    expect(
      model.stages
        .slice(1)
        .every((stage) => stage.start === null && stage.end === null),
    ).toBe(true);
  });

  it("does not invent a prepare start if the worker claim was not reported", () => {
    const model = deploymentProgress(
      affected,
      events.filter((event) => event.id !== "claim"),
      [],
    );
    expect(model.stages[0].end).toBeNull();
    expect(model.stages[1].start).toBeNull();
  });
});


describe("local build timeline", () => {
  const createdAt = "2026-10-24T12:00:00Z";
  const queuedAt = "2026-10-24T12:10:00Z";
  const claimedAt = "2026-10-24T12:10:02Z";
  const events: DeploymentEventResponse[] = [
    { id: "local", level: "info", message: "Waiting for the CLI to build and publish local images.", createdAt },
    { id: "queue", level: "info", message: "Deployment queued.", createdAt: queuedAt },
    { id: "claim", level: "info", message: "Deployment claimed by worker.", createdAt: claimedAt },
  ];

  it("preserves the full local build interval after admission and starts Queue separately", () => {
    const model = deploymentProgress(
      { ...deployment, status: "provisioning", createdAt, startedAt: claimedAt },
      events,
      [],
    );
    expect(model.stages[0]).toEqual({ label: "Local build", state: "complete", start: createdAt, end: queuedAt });
    expect(model.stages[1]).toEqual({ label: "Queue", state: "complete", start: queuedAt, end: claimedAt });
    expect(Date.parse(model.stages[0].end!) - Date.parse(model.stages[0].start!)).toBe(600000);
    expect(Date.parse(model.stages[1].end!) - Date.parse(model.stages[1].start!)).toBe(2000);
    expect(model.totalStartedAt).toBe(createdAt);
    expect(model.queuedAt).toBe(queuedAt);
  });

  it("accounts for the complete duration from local build through verification", () => {
    const completedAt = "2026-10-24T12:12:10Z";
    const model = deploymentProgress(
      { ...deployment, status: "succeeded", createdAt, startedAt: claimedAt, completedAt },
      ([
        ...events,
        { id: "deploy", level: "info", message: "Server ready; DNS and artifact/build preparation starting concurrently.", createdAt: "2026-10-24T12:10:03Z" },
        { id: "verify", level: "info", message: "Build done, containers started; running health checks.", createdAt: "2026-10-24T12:12:00Z" },
      ] satisfies DeploymentEventResponse[]).reverse(),
      [],
    );
    expect(model.stages.map((stage) => stage.label)).toEqual(["Local build", "Queue", "Prepare", "Deploy", "Verify"]);
    expect(model.stages.every((stage) => stage.state === "complete")).toBe(true);
    const stageSum = model.stages.reduce(
      (sum, stage) => sum + Date.parse(stage.end!) - Date.parse(stage.start!), 0,
    );
    expect(stageSum).toBe(Date.parse(completedAt) - Date.parse(model.totalStartedAt));
  });

  it.each([
    { status: "failed", failureCode: "local_build_failed" },
    { status: "failed", failureCode: "local_build_abandoned" },
    { status: "canceled", failureCode: null },
  ] as const)("freezes $status local preparation ($failureCode) without reporting worker stages", ({ status, failureCode }) => {
    const completedAt = "2026-10-24T12:05:00Z";
    const model = deploymentProgress(
      { ...deployment, status, failureCode, createdAt, startedAt: null, completedAt },
      events.slice(0, 1),
      [],
    );
    expect(model.stages[0]).toEqual({ label: "Local build", state: status, start: createdAt, end: completedAt });
    expect(model.stages.slice(1).every((stage) => stage.state === "not run" && stage.start === null && stage.end === null)).toBe(true);
    expect(model.queuedAt).toBeNull();
  });

  it("does not count the original local build again on manual redispatch", () => {
    const requeuedAt = "2026-10-24T13:00:00Z";
    const model = deploymentProgress(
      { ...deployment, status: "queued", createdAt, startedAt: null, requeuedAt },
      [...events, { id: "retry", level: "info", message: "Deployment manually queued.", createdAt: requeuedAt }],
      [],
    );
    expect(model.stages.map((stage) => stage.label)).toEqual(["Queue", "Prepare", "Deploy", "Verify"]);
    expect(model.stages[0]).toEqual({ label: "Queue", state: "running", start: requeuedAt, end: null });
    expect(model.totalStartedAt).toBe(requeuedAt);
  });
});
