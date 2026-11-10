namespace Minicloud.Common.Models;

public sealed class ManagedDatabase
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Engine { get; set; } = "postgres";
    public string EngineVersion { get; set; } = "16";
    public string Placement { get; set; } = "application";
    public string? ServerId { get; set; }
    public string? RecoverySourceAppId { get; set; }
    public string? AppId { get; set; }
    public string AccessMode { get; set; } = "internal";
    public string? NetworkId { get; set; }
    public string? PrivateIp { get; set; }
    public string AllowedCidrsJson { get; set; } = "[]";
    public string Status { get; set; } = "creating";
    public long Revision { get; set; } = 1;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EncryptedCredentialsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
