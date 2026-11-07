using System.ComponentModel.DataAnnotations;

namespace Minicloud.Common.Models;

public sealed class AppTelemetrySettings
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public bool Enabled { get; set; }
    public bool Deleting { get; set; }
    public string RetentionPolicy { get; set; } = "7d";
    public DateTimeOffset? LastAcceptedAt { get; set; }
    public string ProvisioningStatus { get; set; } = "pending";
    public int ProvisioningVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class TelemetryIngestionCredential
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string KeyPrefix { get; set; } = default!;
    public string KeyHash { get; set; } = default!;
    public int CredentialVersion { get; set; }
    public string? CreatedByActor { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GrafanaOrganizationBinding
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public long GrafanaOrgId { get; set; }
    public string ReconciliationStatus { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GrafanaUserBinding
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string MinicloudUserId { get; set; } = default!;
    public long GrafanaUserId { get; set; }
    public string ExpectedRole { get; set; } = "Viewer";
    public string ReconciliationStatus { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GrafanaDataSourceBinding
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string GrafanaUid { get; set; } = default!;
    public string CredentialReference { get; set; } = default!;
    public string ReconciliationStatus { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class GrafanaLaunchCode
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string MinicloudUserId { get; set; } = default!;
    public string CodeHash { get; set; } = default!;
    public string Destination { get; set; } = default!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GrafanaProxySession
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string MinicloudUserId { get; set; } = default!;
    public string SessionHash { get; set; } = default!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TelemetryCleanupJob
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Status { get; set; } = "pending";
    public DateTimeOffset? CutoffAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
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

public sealed class TelemetryAuthorityOutbox
{
    [Key]
    public long Sequence { get; set; }
    public string EventType { get; set; } = default!; // "tenant" | "credential" | "deployment" | "datasource" | "tombstone"
    public string EntityId { get; set; } = default!;
    public string PayloadJson { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
