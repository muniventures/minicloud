import type { DeploymentResponse } from "@/models/controlPlane";
import { deploymentTotalStartedAt, isActiveDeployment } from "./deploymentDetailPresentation";

export function isRecentDeployment(value: string, now: number) {
  const age = now - Date.parse(value);
  return age >= 0 && age < 86_400_000;
}

export function deploymentCreatedTime(value: string, now: number) {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) {
    return { label: "Unavailable", timestamp: undefined };
  }
  const timestamp = new Intl.DateTimeFormat(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
    timeZoneName: "short",
  }).format(date);
  if (!isRecentDeployment(value, now)) {
    return {
      label: new Intl.DateTimeFormat(undefined, {
        dateStyle: "medium",
        timeStyle: "short",
      }).format(date),
      timestamp,
    };
  }
  const minutes = Math.floor((now - date.getTime()) / 60_000);
  const count = minutes < 60 ? minutes : Math.floor(minutes / 60);
  const unit = minutes < 60 ? "minute" : "hour";
  return {
    label: minutes === 0
      ? "Less than a minute ago"
      : `${count} ${unit}${count === 1 ? "" : "s"} ago`,
    timestamp,
  };
}

export function deploymentListDuration(deployment: DeploymentResponse, now: number) {
  const active = isActiveDeployment(deployment.status) && !deployment.completedAt;
  const start = Date.parse(deploymentTotalStartedAt(deployment));
  const end = deployment.completedAt
    ? Date.parse(deployment.completedAt)
    : active ? now : NaN;
  const elapsed = end - start;
  if (!Number.isFinite(elapsed)) return "Unavailable";
  const minutes = Math.floor(Math.max(0, elapsed) / 60_000);
  const duration = minutes === 0
    ? "<1m"
    : minutes < 60
      ? `${minutes}m`
      : `${Math.floor(minutes / 60)}h${minutes % 60 ? ` ${minutes % 60}m` : ""}`;
  return `${duration}${active ? " elapsed" : ""}`;
}
