using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers;

public interface IVpsProviderClient
{
    CloudProvider Provider { get; }
    Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default);
    Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default);
    Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default);
    Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default);
}
