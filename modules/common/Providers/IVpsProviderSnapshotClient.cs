using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers;

public interface IVpsProviderSnapshotClient
{
    Task PowerOffAsync(string serverId, CancellationToken ct);
    Task PowerOnAsync(string serverId, CancellationToken ct);
    Task<ProviderSnapshot> CreateSnapshotAsync(string serverId, string description, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task<ProviderSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken ct);
    Task<IReadOnlyList<ProviderSnapshot>> ListSnapshotsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task DeleteSnapshotAsync(string snapshotId, CancellationToken ct);
    Task<ProviderServerInfo> CreateServerFromSnapshotAsync(CreateServerRequest request, string snapshotId, string? retainedAddressId, CancellationToken ct);
}
