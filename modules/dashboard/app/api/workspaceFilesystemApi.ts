import { apiRequest } from "./apiClient";

export type DevelopmentWorkspaceVmResources = {
  state: "current" | "stale" | "historical" | "unsupported";
  observedAt?: string | null;
  receivedAt?: string | null;
  cpu?: { logicalCount: number; utilizationPercent?: number | null; windowSeconds?: number | null } | null;
  memory?: { totalBytes: number; availableBytes: number } | null;
  filesystems: {
    paths: string[]; totalBytes: number; freeBytes: number; availableBytes: number;
    inodesTotal?: number | null; inodesAvailable?: number | null; nestedMountsExcluded?: number | null;
    /** Storage tiers sharing this filesystem; absent on older images. */
    roles?: ("system" | "workspace" | "scratch")[] | null; scratchBindMounts?: number | null;
  }[];
  unavailable: Record<string, string>;
  diskUsageSupported: boolean;
  directoryDeletionSupported: boolean;
};

export type DiskUsageEntry = {
  name: string; path: string; bytes?: number | null; state: "complete" | "partial" | "unreadable" | "excluded";
  errors: string[]; canOpen: boolean; canDelete: boolean; targetToken?: string | null; protectedReason?: string | null;
  /** "scratch" for build outputs kept on the VM's local scratch disk. */
  storage?: "scratch" | null;
};

export type FilesystemJob = {
  id: string; vmId: string; generation: number; kind: "disk_usage" | "directory_deletion";
  /** Deletion jobs finish with their outcome as the state. */
  state: "queued" | "running" | "completed" | "failed" | "cancelled" | "succeeded" | "partially_deleted" | "outcome_unknown";
  path: string;
  createdAt: string; startedAt?: string | null; completedAt?: string | null; expiresAt: string;
  cancelRequested: boolean; errorCode?: string | null; errorMessage?: string | null;
  diskUsage?: {
    totalBytes: number; filesHereBytes: number; complete: boolean; limitReached?: string | null;
    omittedEntries: number; omittedBytes: number; hardLinksApproximate: boolean;
    errors: Record<string, number>; targetsExpireAt?: string | null; entries: DiskUsageEntry[];
  } | null;
  deletion?: {
    outcome: "succeeded" | "partially_deleted" | "failed" | "cancelled" | "outcome_unknown";
    removedEntries: number; removedBytes: number; limitReached?: string | null; errors: Record<string, number>;
  } | null;
};

function vmPath(id: string, vmId: string, organizationId: string, operation: string) {
  return `/api/development-workspaces/${encodeURIComponent(id)}/vms/${encodeURIComponent(vmId)}/${operation}`
    + `?organizationId=${encodeURIComponent(organizationId)}`;
}

export function startDiskUsageScan(id: string, vmId: string, organizationId: string,
  request: { generation: number; idempotencyKey: string; targetToken?: string | null }) {
  return apiRequest<FilesystemJob>(vmPath(id, vmId, organizationId, "disk-usage/scans"), { method: "POST", body: request });
}

export function startDirectoryDeletion(id: string, vmId: string, organizationId: string, request: {
  generation: number; targetToken: string; confirmationPath: string; acknowledgePermanentLoss: boolean; idempotencyKey: string;
}) {
  return apiRequest<FilesystemJob>(vmPath(id, vmId, organizationId, "directory-deletions"), { method: "POST", body: request });
}

export function getFilesystemJob(id: string, vmId: string, organizationId: string, jobId: string, signal?: AbortSignal) {
  return apiRequest<FilesystemJob>(vmPath(id, vmId, organizationId, `filesystem-jobs/${encodeURIComponent(jobId)}`), { signal });
}

export function cancelFilesystemJob(id: string, vmId: string, organizationId: string, jobId: string) {
  return apiRequest<FilesystemJob>(vmPath(id, vmId, organizationId, `filesystem-jobs/${encodeURIComponent(jobId)}/cancel`), { method: "POST" });
}

export type VmResourceHistorySample = {
  minute: string;
  observedAt: string;
  cpuUtilizationPercent?: number | null;
  memoryUsedBytes?: number | null;
  memoryTotalBytes?: number | null;
  workspaceDiskUsedBytes?: number | null;
  workspaceDiskTotalBytes?: number | null;
};

export type VmResourceHistory = {
  vmId: string;
  generation: number;
  windowStart: string;
  windowEnd: string;
  samples: VmResourceHistorySample[];
};

export function getVmResourceHistory(id: string, vmId: string, organizationId: string, signal?: AbortSignal) {
  return apiRequest<VmResourceHistory>(vmPath(id, vmId, organizationId, "resource-history"), { signal });
}
