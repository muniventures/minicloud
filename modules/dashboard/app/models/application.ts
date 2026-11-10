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

