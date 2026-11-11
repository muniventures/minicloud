using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers;

public interface IVpsProviderSshKeyClient
{
    Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default);
    Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default);
}
