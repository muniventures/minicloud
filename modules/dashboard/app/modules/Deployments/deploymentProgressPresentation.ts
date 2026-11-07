import type {
  DeploymentEventResponse,
  DeploymentLogResponse,
  DeploymentResponse,
} from "@/models/controlPlane";
import {
  deploymentTotalStartedAt,
  isActiveDeployment,
  summarizeEvent,
} from "./deploymentDetailPresentation";

const phaseLabels: Record<string, string> = {
  preflight: "Checking deployment requirements",
  acquire_lock: "Waiting for deployment lock",
  prepare_paths: "Preparing deployment files",
  render_next_configs: "Preparing configuration",
  validate_next_configs: "Validating configuration",
  build_artifacts: "Building application images",
  wait_for_rollout_gate: "Waiting for DNS preparation",
  migrate_postgres_if_needed: "Preparing database",
  ensure_postgres_data_owner: "Preparing database storage",
  ensure_observability_data_owner: "Preparing log storage",
  start_observability: "Starting monitoring",
  configure_openbao: "Configuring secrets",
  start_infra: "Starting supporting services",
  sync_postgres_password: "Configuring database access",
  start_app_services: "Starting application containers",
  verify_local_health: "Checking container health",
  activate_caddy: "Activating routing",
  cleanup_deployment_artifacts: "Cleaning up deployment files",
  record_deploy: "Recording deployment",
  runtime_agent_install: "Installing runtime agent",
  complete: "Rollout completed",
};
const statuses = [
  ["queued", "dispatching"],
  ["provisioning"],
  ["deploying"],
  ["verifying"],
];
const phaseGroups: Record<string, number> = {
  "resolve-provision": 1,
  "dns-setup": 2,
  "ssh-deploy": 2,
  "health-finalize": 3,
};

type Timing = {
  phase?: string;
  outcome?: string;
  started_at_utc?: string;
  completed_at_utc?: string;
  duration_ms?: number;
};
function timing(event: DeploymentEventResponse): Timing {
  try {
    const value = JSON.parse(event.metadataJson ?? "null");
    return value && typeof value === "object" ? value : {};
  } catch {
    return {};
  }
}

export function deploymentAttemptTiming(
  deployment: DeploymentResponse,
  events: DeploymentEventResponse[],
) {
  const orderedEvents = [...events].sort(
    (a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt),
  );
  const queueEvent = [...orderedEvents]
    .reverse()
    .find((event) =>
      /^Deployment (?:manually )?queued\.?$/i.test(event.message.trim()),
    );
  const manuallyQueued = /manually queued/i.test(queueEvent?.message ?? "");
  const hasLocalBuild =
    !manuallyQueued &&
    (deployment.status === "waiting_for_build" ||
      orderedEvents.some(
        (event) =>
          event.message.trim() ===
          "Waiting for the CLI to build and publish local images.",
      ));
  const queuedAt =
    queueEvent?.createdAt ?? (hasLocalBuild ? null : deployment.createdAt);
  const totalStartedAt = deploymentTotalStartedAt(deployment);
  return { orderedEvents, queuedAt, totalStartedAt, hasLocalBuild };
}

export function deploymentProgress(
  deployment: DeploymentResponse,
  events: DeploymentEventResponse[],
  logs: DeploymentLogResponse[],
) {
  const { orderedEvents, queuedAt, totalStartedAt, hasLocalBuild } =
    deploymentAttemptTiming(deployment, events);
  const beforeAdmission = hasLocalBuild && !queuedAt;
  const attemptStart = Math.max(
    Date.parse(queuedAt ?? totalStartedAt),
    Date.parse(deployment.startedAt ?? queuedAt ?? totalStartedAt),
  );
  const currentEvents = orderedEvents.filter(
    (event) => Date.parse(event.createdAt) >= attemptStart,
  );
  const timings = currentEvents.map(timing);
  const activeIndex = statuses.findIndex((group) =>
    group.includes(deployment.status),
  );
  const failedPhase = [...timings]
    .reverse()
    .find((item) => item.outcome === "failed" || item.outcome === "canceled");
  const interruptedIndex = failedPhase?.phase
    ? phaseGroups[failedPhase.phase]
    : undefined;
  const stageIndex = beforeAdmission
    ? -1
    : activeIndex >= 0
      ? activeIndex
      : interruptedIndex;
  const provision = timings.find((item) => item.phase === "resolve-provision");
  const deployStart = currentEvents.find((event) =>
    /Server ready; DNS and artifact\/build preparation starting concurrently/i.test(
      event.message,
    ),
  )?.createdAt;
  const verifyStart = currentEvents.find((event) =>
    /Build done, containers started; running health checks/i.test(
      event.message,
    ),
  )?.createdAt;
  const claimedAt = currentEvents.find(
    (event) =>
      timing(event).phase === "queue_wait" ||
      /Deployment claimed by worker/i.test(event.message),
  )?.createdAt;
  const starts = [queuedAt, claimedAt, deployStart, verifyStart];
  const ends = [
    claimedAt,
    deployStart,
    verifyStart,
    beforeAdmission ? null : deployment.completedAt,
  ];
  const stages = ["Queue", "Prepare", "Deploy", "Verify"].map(
    (label, index) => {
      const complete =
        deployment.status === "succeeded" ||
        (stageIndex !== undefined && index < stageIndex) ||
        (index === 1 && provision?.outcome === "succeeded");
      const current = index === stageIndex;
      const state = current
        ? deployment.status === "failed"
          ? "failed"
          : deployment.status === "canceled"
            ? "canceled"
            : "running"
        : complete
          ? "complete"
          : stageIndex !== undefined &&
              (deployment.status === "failed" ||
                deployment.status === "canceled")
            ? "not run"
            : !isActiveDeployment(deployment.status)
              ? "not reported"
              : "pending";
      return {
        label,
        state,
        start: starts[index] ?? null,
        end:
          ends[index] ??
          (current && !isActiveDeployment(deployment.status)
            ? deployment.completedAt
            : null),
      };
    },
  );
  if (hasLocalBuild) {
    stages.unshift({
      label: "Local build",
      state: queuedAt
        ? "complete"
        : deployment.status === "waiting_for_build"
          ? "running"
          : deployment.status === "failed" || deployment.status === "canceled"
            ? deployment.status
            : "not reported",
      start: deployment.createdAt,
      end: queuedAt ?? deployment.completedAt ?? null,
    });
  }

  const workerLogs = logs
    .filter(
      (log) =>
        ["deploy", "build", "compose", "health"].includes(log.source) &&
        (!deployment.startedAt || Date.parse(log.createdAt) >= attemptStart),
    )
    .sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
  const lines = workerLogs.flatMap((log) =>
    log.content
      .split(/\r?\n/)
      .filter(Boolean)
      .map((content) => ({ ...log, content })),
  );
  let marker = -1;
  let phase = "";
  for (const [index, line] of lines.entries()) {
    const match = line.content.match(/^===== phase: ([a-z_]+) =====$/);
    if (match) {
      marker = index;
      phase = match[1];
    }
  }
  const verifying = deployment.status === "verifying";
  const inRollout =
    deployment.status === "deploying" ||
    deployment.status === "failed" ||
    deployment.status === "canceled" ||
    deployment.status === "succeeded";
  const verificationOutput = verifying || interruptedIndex === 3;
  const stepLines = verificationOutput
    ? lines.filter(
        (line) =>
          line.source === "health" &&
          Boolean(verifyStart) &&
          Date.parse(line.createdAt) >= Date.parse(verifyStart!),
      )
    : inRollout && marker >= 0
      ? lines.slice(marker + 1)
      : [];
  const headline =
    deployment.status === "waiting_for_build"
      ? "Waiting for local build"
      : deployment.status === "succeeded"
        ? "Deployment completed"
        : deployment.status === "failed"
          ? "Deployment failed"
          : deployment.status === "canceled"
            ? "Deployment canceled"
            : verifying
              ? "Running health checks"
              : deployment.status === "deploying" && phase
                ? (phaseLabels[phase] ?? phase.replaceAll("_", " "))
                : deployment.status === "deploying"
                  ? "Deploying application"
                  : orderedEvents.length
                    ? summarizeEvent(orderedEvents.at(-1)!.message)
                    : deployment.status === "queued"
                      ? "Waiting for a worker"
                      : "Preparing deployment";
  return {
    stages,
    queuedAt,
    totalStartedAt,
    headline,
    preview: stepLines.slice(-6),
    scoped: verificationOutput || marker >= 0,
    events: orderedEvents,
  };
}
