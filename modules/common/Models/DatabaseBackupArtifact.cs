namespace Minicloud.Common.Models;

public sealed class DatabaseBackupArtifact
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string DatabaseId { get; set; } = "";
    public string RepositoryAppId { get; set; } = "";
    public string BackupId { get; set; } = "";
    public string KeyVersion { get; set; } = "";
    public string Status { get; set; } = "pending";
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
