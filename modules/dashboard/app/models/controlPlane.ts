export type StorageMount = { sizeGb: number; mountPath: string; uid: number; gid: number };

export type OrganizationSummary = {
  id: string;
  name: string;
  slug: string;
  role: string;
};

export type MeResponse = {
  id: string;
  email: string;
  displayName?: string | null;
  organizations: OrganizationSummary[];
};

export type OrganizationResponse = {
  id: string;
  name: string;
  slug: string;
  createdAt: string;
};

export type LatestDeployment = {
  id: string;
  status: DeploymentStatus;
  websiteUrl?: string | null;
  completedAt?: string | null;
};

export type AppEnvironmentResponse = { id: string; name: string; plan: AppPlan; database: DatabaseMode; deletionStatus?: string | null };

export type AppResponse = {
  environmentName?: string | null;
  environments?: AppEnvironmentResponse[];
  deletionStatus?: string | null;
  deletionError?: string | null;
  id: string;
  organizationId: string;
  name: string;
  slug: string;
  parentAppId?: string | null;
  branchName?: string | null;
  plan: AppPlan;
  database: DatabaseMode;
  domains: AppDomain[];
  branches?: AppBranchResponse[];
  latestDeployment?: LatestDeployment | null;
};

export type AppBranchResponse = {
  urls?: string[];
  id: string;
  name: string;
  slug: string;
  branchName: string;
  plan: AppPlan;
  database: DatabaseMode;
  websiteUrl?: string | null;
  createdAt: string;
};

export type AppDomain = {
  id: string;
  hostname: string;
  url: string;
  sslStatus: string;
  reserved: boolean;
};

export type DomainBindingResponse = {
  id: string;
  appId: string;
  serviceName: string;
  hostname: string;
  kind: "minicloud_subdomain" | "custom_domain";
  targetIp?: string | null;
  verificationToken?: string | null;
  lastCheckedAt?: string | null;
  pathPrefix: "/";
  dnsStatus: string;
  applyStatus: string;
  sslStatus: string;
  status: string;
  isPrimary?: boolean;
  failureCode?: string | null;
  failureMessage?: string | null;
  lastAppliedAt?: string | null;
  createdAt: string;
  updatedAt: string;
};

export type DomainAvailabilityResponse = {
  label: string;
  hostname: string;
  available: boolean;
  message?: string | null;
};

export type DatabaseMode = "sqlite" | "postgres";
export type AppPlan = "p0" | "p0-h-0" | "p0-v-0" | "p0-v-1" | "p0-v-2" | "p0-d-0" | "p0-d-1" | "p0-d-2";
export type DeploymentStatus = "waiting_for_build" | "queued" | "dispatching" | "provisioning" | "deploying" | "verifying" | "succeeded" | "failed" | "canceled";

export type ApiKeyScope = "apps:read" | "apps:write" | "deployments:create" | "deployments:read" | "api_keys:read" | "api_keys:write" | "logs:read" | "telemetry:manage";

export type ApiKeyResponse = {
  id: string;
  name: string;
  prefix: string;
  scopes: ApiKeyScope[];
  lastUsedAt?: string | null;
  expiresAt?: string | null;
  revokedAt?: string | null;
  createdAt: string;
};

export type ApiKeyCreateResponse = ApiKeyResponse & {
  token: string;
};

export type CliLoginSessionResponse = {
  sessionId: string;
  status: "pending" | "approved" | "exchanged" | "expired";
  scopes: ApiKeyScope[];
  expiresAt: string;
  approvedAt?: string | null;
  exchangedAt?: string | null;
};

export type DeploymentServiceResponse = {
  storage?: Record<string, StorageMount> | null;
  urls?: string[];
  name: string;
  image: string;
  port: number;
  public: boolean;
  path: string;
  healthPath: string;
};

export type DeploymentResponse = {
  cancellationRequestedAt?: string | null;
  id: string;
  appId: string;
  organizationId: string;
  database: DatabaseMode;
  status: DeploymentStatus;
  websiteUrl?: string | null;
  consoleUrl: string;
  failureCode?: string | null;
  failureMessage?: string | null;
  services: DeploymentServiceResponse[];
  createdAt: string;
  requeuedAt?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
};

export type DeploymentEventResponse = {
  id: string;
  level: "info" | "warning" | "error";
  message: string;
  metadataJson?: string | null;
  createdAt: string;
};

export type DeploymentLogResponse = {
  id: string;
  source: string;
  content: string;
  createdAt: string;
};

export type RuntimeServiceStatus = {
  name: string;
  container?: string | null;
  state: string;
  health?: string | null;
  image?: string | null;
  restartCount?: number | null;
};

export type AppServiceInventoryResponse = {
  storage?: Record<string, StorageMount> | null;
  name: string;
  image: string;
  port: number;
  public: boolean;
  path: string;
  healthPath: string;
  runtime?: RuntimeServiceStatus | null;
  runtimeObservedAt?: string | null;
  domains: DomainBindingResponse[];
};

export type AppServiceSecretResponse = {
  id: string;
  appId: string;
  serviceName: string;
  name: string;
  status: string;
  createdAt: string;
  updatedAt: string;
};

export type AppServiceSecretServiceResponse = {
  id: string;
  appId: string;
  serviceName: string;
  secretCount: number;
  createdAt: string;
  updatedAt: string;
};

export type RuntimeCaddyStatus = {
  active: boolean;
};

export type RuntimeStatusResponse = {
  serverId: string;
  agentVersion?: string | null;
  lastSeenAt?: string | null;
  agentInstalled: boolean;
  agentOnline: boolean;
  observedAt?: string | null;
  services: RuntimeServiceStatus[];
  caddy?: RuntimeCaddyStatus | null;
};

export type RuntimeLogResponse = {
  id: string;
  serverId: string;
  source: string;
  service?: string | null;
  stream: "stdout" | "stderr";
  content: string;
  observedAt: string;
  receivedAt: string;
};

export type RuntimeLogSourceResponse = {
  source: string;
  service?: string | null;
  lastObservedAt: string;
};

export type RuntimeDiagnosticBundleResponse = {
  caddyErrors: RuntimeLogResponse[];
  serviceErrors: RuntimeLogResponse[];
  status?: RuntimeStatusResponse | null;
  latestDeploymentUrl?: string | null;
};

export type DeploymentActivityResponse = {
  id: string;
  name: string;
  status: string;
  conclusion?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
  steps: DeploymentActivityStepResponse[];
};

export type DeploymentActivityStepResponse = {
  number: number;
  name: string;
  status: string;
  conclusion?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
};

export type TelemetrySettingsResponse = {
  ingestionUrl?: string | null;
  appId: string;
  organizationId: string;
  tenantId: string;
  enabled: boolean;
  retentionPolicy: string;
  provisioningStatus: string;
  lastAcceptedAt?: string | null;
  createdAt: string;
  updatedAt: string;
};

export type TelemetryCredentialResponse = {
  id: string;
  serviceName: string;
  name: string;
  keyPrefix: string;
  credentialVersion: number;
  createdByActor?: string | null;
  expiresAt?: string | null;
  revokedAt?: string | null;
  lastUsedAt?: string | null;
  createdAt: string;
};

export type TelemetryCredentialIssueResponse = {
  id: string;
  serviceName: string;
  name: string;
  keyPrefix: string;
  credentialVersion: number;
  expiresAt?: string | null;
  createdAt: string;
  token: string;
};

export type TelemetryLogEntry = {
  timestamp: string;
  service: string;
  severity: string;
  traceId?: string | null;
  body: string;
  attributes?: Record<string, string> | null;
};

export type TelemetryLogsState = "ok" | "not_configured" | "disabled" | "provisioning";

export type TelemetryLogsResponse = {
  state: TelemetryLogsState;
  entries: TelemetryLogEntry[];
  truncated: boolean;
  truncationReason?: string | null;
  retentionCutoff: string;
  lastAcceptedAt?: string | null;
};

export type GrafanaLaunchResponse = {
  launchCode: string;
  destination: string;
  expiresAt: string;
  exchangeUrl?: string | null;
  grafanaBaseUrl?: string | null;
};

export type TelemetryReadTokenIssueResponse = {
  readToken: string;
  expiresAt: string;
  queryUrl: string;
};

export type GrafanaExchangeResponse = {
  destination: string;
  expiresAt: string;
};

export type ApiErrorDetail = {
  field: string;
  message: string;
};

export type ApiErrorBody = {
  code: string;
  message: string;
  details: ApiErrorDetail[];
};

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly details: ApiErrorDetail[];

  constructor(status: number, error: ApiErrorBody) {
    super(error.message);
    this.name = "ApiError";
    this.status = status;
    this.code = error.code;
    this.details = error.details;
  }

  fieldError(field: string) {
    return this.details.find((detail) => detail.field === field)?.message;
  }
}

export type CustomDomainRegistrationResponse = {
  appId: string | null;
  id: string;
  hostname: string;
  verificationToken: string;
  status: "pending" | "verified";
  failureMessage: string | null;
  lastCheckedAt: string | null;
};

export type AppStorageVolume = { name: string; provider: string; providerVolumeId: string | null; region: string; sizeGb: number; uid: number; gid: number; status: "registered" | "creation_unresolved" | "pending" };
