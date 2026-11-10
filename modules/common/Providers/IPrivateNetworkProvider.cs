namespace Minicloud.Common.Providers;

public interface IPrivateNetworkProvider
{
    Task<string> GetRegionNetworkZoneAsync(string region, CancellationToken ct);
    Task<ProviderPrivateNetwork?> FindPrivateNetworkAsync(string name, CancellationToken ct);
    Task<ProviderPrivateNetwork?> GetPrivateNetworkAsync(string id, CancellationToken ct);
    Task<ProviderPrivateNetwork> CreatePrivateNetworkAsync(string name, string ipRange, string subnetRange, string networkZone, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task<ProviderPrivateAddress> AttachPrivateNetworkAsync(string networkId, string serverId, CancellationToken ct);
    Task DetachPrivateNetworkAsync(string networkId, string serverId, CancellationToken ct);
    Task DeletePrivateNetworkAsync(string networkId, CancellationToken ct);
}

public sealed record ProviderPrivateNetwork(string Id, string Name, string IpRange, string NetworkZone, IReadOnlyDictionary<string, string> Labels, IReadOnlyList<string>? ServerIds = null);
public sealed record ProviderPrivateAddress(string ServerId, string NetworkId, string IpAddress, string NetworkZone);
