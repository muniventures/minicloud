namespace Minicloud.Common.Contracts;

public sealed class TelemetrySyncBatchRequest
{
    public long CheckpointSequence { get; set; }
    public DateTimeOffset CheckpointTimestamp { get; set; }
    public List<TelemetryTenantSyncItem> Tenants { get; set; } = [];
    public List<TelemetryCredentialSyncItem> Credentials { get; set; } = [];
    public List<TelemetryDeploymentSyncItem> Deployments { get; set; } = [];
    public List<TelemetryDataSourceSyncItem> DataSources { get; set; } = [];
    public List<TelemetryTombstoneSyncItem> Tombstones { get; set; } = [];
}

public sealed class TelemetryDataSourceSyncItem
{
    public string Id { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string GrafanaUid { get; set; } = default!;
    public string KeyPrefix { get; set; } = default!;
    public string KeyHash { get; set; } = default!;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TelemetryDeploymentSyncItem
{
    public string Id { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TelemetryTenantSyncItem
{
    public string AppId { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public bool Enabled { get; set; }
    public bool Deleting { get; set; }
    public string RetentionPolicy { get; set; } = "7d";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TelemetryCredentialSyncItem
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string KeyPrefix { get; set; } = default!;
    public string KeyHash { get; set; } = default!;
    public int CredentialVersion { get; set; }
    public string Status { get; set; } = "active"; // "active" | "revoked"
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TelemetryTombstoneSyncItem
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public DateTimeOffset? CutoffAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TelemetrySyncBatchResponse
{
    public bool Success { get; set; }
    public long AppliedSequence { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class TelemetryStatusReport
{
    public DateTimeOffset ReportedAt { get; set; }
    public List<TelemetryAppUsageStatus> AppStatuses { get; set; } = [];
    public List<TelemetryCredentialUsageStatus> CredentialStatuses { get; set; } = [];
    public List<TelemetryCleanupStatusItem> CleanupStatuses { get; set; } = [];
}

public sealed class TelemetryAppUsageStatus
{
    public string AppId { get; set; } = default!;
    public DateTimeOffset LastAcceptedAt { get; set; }
}

public sealed class TelemetryCredentialUsageStatus
{
    public string CredentialId { get; set; } = default!;
    public DateTimeOffset LastUsedAt { get; set; }
}

public sealed class TelemetryCleanupStatusItem
{
    public string JobId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Status { get; set; } = default!; // "completed" | "failed"
    public string? LastError { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class TelemetryLaunchCodeIssueRequest
{
    public string MinicloudUserId { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string Destination { get; set; } = default!;
    public string ExpectedRole { get; set; } = "Viewer";
}

public sealed class TelemetryLaunchCodeIssueResponse
{
    public string LaunchCode { get; set; } = default!;
    public string Destination { get; set; } = default!;
    public DateTimeOffset ExpiresAt { get; set; }
    public string ExchangeUrl { get; set; } = default!;
    public string GrafanaBaseUrl { get; set; } = default!;
}
