namespace Minicloud.Common.Models;

public sealed class ManagedDatabaseNetwork
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string ProviderAccountId { get; set; } = "";
    public long ProviderAccountRevision { get; set; }
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
    public string IpRange { get; set; } = "10.72.0.0/16";
    public string SubnetRange { get; set; } = "10.72.1.0/24";
    public string? NetworkZone { get; set; }
    public string? ProviderNetworkId { get; set; }
    public bool CreationAttempted { get; set; }
    public long Revision { get; set; } = 1;
    public string Status { get; set; } = "creating";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
