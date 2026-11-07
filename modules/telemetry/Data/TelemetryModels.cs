using System.ComponentModel.DataAnnotations;

namespace Minicloud.Telemetry.Data;

public sealed class TelemetryAuthorityCheckpoint
{
    [Key]
    public int Id { get; set; } = 1;
    public long Sequence { get; set; }
    public DateTimeOffset CheckpointTimestamp { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; }
}

public sealed class TelemetryTenantBinding
{
    [Key]
    public string AppId { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public bool Enabled { get; set; }
    public bool Deleting { get; set; }
    public string RetentionPolicy { get; set; } = "7d";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TelemetryCredentialReplica
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string KeyPrefix { get; set; } = default!;
    public string KeyHash { get; set; } = default!;
    public int CredentialVersion { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TelemetryDailyUsage
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public DateOnly Day { get; set; }
    public long AdmittedBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TelemetryGrafanaSession
{
    [Key]
    public string Id { get; set; } = default!;
    public string SessionHash { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string MinicloudUserId { get; set; } = default!;
    public string GrafanaLogin { get; set; } = default!;
    public string DataSourceUid { get; set; } = default!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TelemetryLaunchCode
{
    [Key]
    public string Id { get; set; } = default!;
    public string CodeHash { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string MinicloudUserId { get; set; } = default!;
    public string Destination { get; set; } = default!;
    public string GrafanaLogin { get; set; } = default!;
    public string DataSourceUid { get; set; } = default!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TelemetryCleanupRecord
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Status { get; set; } = "pending"; // pending, in_progress, completed, failed
    public DateTimeOffset? CutoffAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class TelemetryStatusEvent
{
    [Key]
    public string Id { get; set; } = default!;
    public string Kind { get; set; } = default!; // "last_accepted" | "last_used"
    public string TargetId { get; set; } = default!;
    public DateTimeOffset Timestamp { get; set; }
}

public sealed class TelemetryDeploymentReplica
{
    [Key]
    public string Id { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TelemetryDataSourceReplica
{
    [Key]
    public string Id { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string GrafanaUid { get; set; } = default!;
    public string KeyPrefix { get; set; } = default!;
    public string KeyHash { get; set; } = default!;
    public DateTimeOffset UpdatedAt { get; set; }
}
