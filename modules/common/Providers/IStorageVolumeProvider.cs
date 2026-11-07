namespace Minicloud.Common.Providers;

public sealed record ProviderStorageVolume(string Id, string Name, int SizeGb, string Region, string? ServerId, IReadOnlyDictionary<string, string> Labels);

public interface IStorageVolumeProvider
{
    Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct);
    Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct);
    Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task AttachVolumeAsync(string id, string serverId, CancellationToken ct);
    Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct);
}
