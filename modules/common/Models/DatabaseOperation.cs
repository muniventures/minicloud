namespace Minicloud.Common.Models;

public sealed class DatabaseOperation
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string? BackupPolicyId { get; set; }
    public long? ScheduledPolicyRevision { get; set; }
    public string? DatabaseId { get; set; }
    public string? ServerId { get; set; }
    public string Kind { get; set; } = "";
    public string ActorJson { get; set; } = "{}";
    public string RequestJson { get; set; } = "{}";
    public string Status { get; set; } = "queued";
    public string? OwnershipToken { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? ResultJson { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
