namespace Minicloud.Common.Models;

// Intentionally retained independently of app deletion; provider disks require explicit cleanup.
public sealed class AppStorageVolume
{
    public string Id { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public string? OrganizationId { get; set; }
    public string? ProviderAccountId { get; set; }
    public string Provider { get; set; } = "hetzner";
    public string Name { get; set; } = default!;
    public string Region { get; set; } = default!;
    public string? ProviderVolumeId { get; set; }
    public bool CreationAttempted { get; set; }
    public int SizeGb { get; set; }
    public int Uid { get; set; }
    public int Gid { get; set; }
}
