namespace Minicloud.Common.Models;

public sealed class ManagedDatabaseServer
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "shared";
    public string Engine { get; set; } = "postgres";
    public string EngineVersion { get; set; } = "16";
    public string PlanId { get; set; } = "";
    public string? RuntimeAppId { get; set; }
    public string? RuntimeServerId { get; set; }
    public long Revision { get; set; } = 1;
    public string Status { get; set; } = "creating";
    public string? RuntimeDeploymentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
