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
