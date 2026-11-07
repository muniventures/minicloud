import type { DeploymentResponse } from "@/models/controlPlane";

export function deploymentTotalStartedAt(deployment: DeploymentResponse) {
  return deployment.requeuedAt ?? deployment.createdAt;
}

export function summarizeEvent(message: string) {
  const normalized = message.trim();
  if (/\bqueued\b|entered (?:the )?queue/i.test(normalized))
    return "Deployment queued";
  if (/claimed by worker/i.test(normalized)) return "Deployment claimed";
  if (
    /server ready; dns and artifact\/build preparation starting concurrently/i.test(
      normalized,
    )
  )
    return "Preparing DNS and deployment artifacts";
  if (/build done, containers started; running health checks/i.test(normalized))
    return "Running health checks";
  if (/deployment succeeded/i.test(normalized)) return "Deployment succeeded";
  const phase = normalized.match(
    /^Deployment phase '([^']+)' (succeeded|failed|canceled)/i,
  );
  if (phase) {
    const phaseName =
      (
        {
          "resolve-provision": "Server resolution",
          "dns-setup": "DNS setup",
          "ssh-deploy": "Deployment rollout",
          "health-finalize": "Health checks",
        } as Record<string, string>
      )[phase[1]] ?? phase[1].replaceAll("-", " ");
    return `${phaseName} ${phase[2] === "succeeded" ? "completed" : phase[2]}`;
  }
  if (/\b(fail|error|unable|timeout|cancel)\w*/i.test(normalized))
    return normalized;
  if (/server.*(?:resolved|ready)|(?:resolved|ready).*server/i.test(normalized))
    return "Server resolved";
  if (
    /dns.*(?:ready|configured|created|completed)|(?:ready|configured|created|completed).*dns/i.test(
      normalized,
    )
  ) {
    const explicitCount = normalized.match(/\b(\d+)\s+endpoints?\b/i)?.[1];
    const hostnames = normalized
      .match(/^DNS ready:\s*(.+?)\s*->/i)?.[1]
      .split(",")
      .filter(Boolean).length;
    const count = explicitCount ?? (hostnames ? String(hostnames) : undefined);
    return `DNS ready${count ? ` · ${count} endpoints` : ""}`;
  }
  if (/transfer|payload|artifact/i.test(normalized)) {
    const count = normalized.match(
      /\b(\d+)\s+artifact(?:\(s\)|s)?(?=\W|$)/i,
    )?.[1];
    return `Transferring deployment payload${count ? ` · ${count} artifacts` : ""}`;
  }
  return normalized;
}

export function isActiveDeployment(status?: string) {
  return [
    "waiting_for_build",
    "queued",
    "dispatching",
    "provisioning",
    "deploying",
    "verifying",
  ].includes(status ?? "");
}

export function formatDeploymentTime(value: string | number) {
  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
  }).format(new Date(value));
}

export function formatDuration(milliseconds: number) {
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return "—";
  if (milliseconds < 1000) return "<1s";
  const seconds = Math.floor(milliseconds / 1000);
  return seconds < 60
    ? `${seconds}s`
    : `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}
