using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers;

public interface IVpsProviderAddressClient
{
    Task<ProviderRetainedAddress> RetainServerAddressAsync(string serverId, CancellationToken cancellationToken = default);
    Task<ProviderRetainedAddress?> GetRetainedAddressAsync(string addressId, CancellationToken cancellationToken = default);
    Task EnsureSurvivesServerDeletionAsync(string addressId, CancellationToken cancellationToken = default);
    Task AssignRetainedAddressAsync(string addressId, string serverId, CancellationToken cancellationToken = default);
    Task ReleaseRetainedAddressAsync(string addressId, CancellationToken cancellationToken = default);
}
