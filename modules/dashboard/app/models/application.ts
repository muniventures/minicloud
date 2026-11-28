// Application/environment contract (mirrors modules/contracts/Contracts/AppDtos.cs).
// Explicit application/environment API wire contract. Synthetic review data
// lives in api/applicationsFixtures.ts.
import type { AppDomain, LatestDeployment } from "./controlPlane";

export type EnvironmentKind = "persistent" | "preview";

export type EnvironmentRuntimeObservation = {
  state: string;
  connected: boolean;
  stale: boolean;
  observedAt: string | null;
  lastSeenAt: string | null;
  totalServices: number;
  healthyServices: number;
};

// SSH access contract (mirrors modules/contracts/Contracts/SshAccessDtos.cs).
export type SshRegistrationState =
  | "none" | "not_registered" | "discovered" | "pending" | "registering" | "ready" | "failed" | "unknown";

export type SshAccessRequest =
  | { mode: "none"; expectedRevision?: number }
  | { mode: "existing"; keyId: string; expectedRevision?: number }
  | { mode: "new"; name: string; publicKey: string; expectedRevision?: number };

export type ProviderSshKey = {
  id: string;
  providerAccountId: string;
  name: string;
  fingerprint: string;
  keyType: "ed25519" | "rsa" | string;
  registrationState: SshRegistrationState | string;
};

export type EnvironmentSshAccess = {
  mode: "none" | "existing" | "new" | string;
  providerAccountId: string | null;
  keyId: string | null;
  name: string | null;
  fingerprint: string | null;
  keyType: string | null;
  revision: number;
  registrationState: SshRegistrationState | string;
  selectionLocked: boolean;
};

export type SshConnection = { address: string; username: string };

export type EnvironmentSleep = {
  policy: {
    sleepAfterMinutes: number | null;
    httpTrafficIsActivity: boolean;
    revision: number;
  };
  state: "awake" | "sleeping" | "asleep" | "waking" | "sleep_failed" | "wake_failed" | string;
  stateChangedAt: string;
  lastActivityAt: string | null;
  lastHttpRequestAt: string | null;
  keepAwakeUntil: string | null;
  sleepsAt: string | null;
  eligible: boolean;
  ineligibleReason: string | null;
  addressMode: "dynamic" | "retained" | string;
  retainedAddress: { ipv4: string; state: string; previousIpv4: string | null } | null;
  snapshot: { createdAt: string; sizeGb: number | null; pendingPurge: boolean } | null;
  operation: {
    id: string;
    kind: "sleep" | "wake" | string;
    status: string;
    step: string | null;
    trigger: string;
  } | null;
  lastError: { code: string; message: string } | null;
};

export type ApplicationEnvironment = {
  id: string;
  applicationId: string;
  organizationId: string;
  name: string;
  kind: string;
  runtimeSlug: string;
  plan: string;
  planId: string | null;
  database: string;
  sourceBranch: string | null;
  sourceEnvironmentId: string | null;
  deletionStatus: string | null;
  deletionError: string | null;
  placementLocked: boolean;
  domains: AppDomain[];
  latestDeployment: LatestDeployment | null;
  runtimeObservation: EnvironmentRuntimeObservation;
  createdAt: string;
  updatedAt: string;
  sshAccess?: EnvironmentSshAccess | null;
  sshConnection?: SshConnection | null;
  sleep?: EnvironmentSleep | null;
};

export type Application = {
  id: string;
  organizationId: string;
  name: string;
  slug: string;
  defaultEnvironmentId: string | null;
  deletionStatus: string | null;
  deletionError: string | null;
  environmentCount: number;
  persistentEnvironmentCount: number;
  previewEnvironmentCount: number;
  environments: ApplicationEnvironment[];
  createdAt: string;
  updatedAt: string;
};
