namespace Minicloud.Common.Models;

public sealed class DatabaseBackupPolicy
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string DatabaseId { get; set; } = "";
    public long Revision { get; set; } = 1;
    public bool KeepLastCopies { get; set; }
    public bool Enabled { get; set; }
    public int FrequencyHours { get; set; } = 24;
    public int RetentionDays { get; set; } = 30;
    public int MinimumCopies { get; set; } = 2;
    public string? RepositoryAppId { get; set; }
    public DateTimeOffset? NextDueAt { get; set; }
    public string Status { get; set; } = "unconfigured";
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EncryptedKeyMaterialJson { get; set; }
    public string? KeyVersion { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
