using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Extensibility;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers.Vultr;

public sealed partial class VultrClient : IVpsProviderSnapshotClient
{
    public Task PowerOffAsync(string serverId, CancellationToken ct) => ChangePowerAsync(serverId, "halt", "stopped", ct);

    public Task PowerOnAsync(string serverId, CancellationToken ct) => ChangePowerAsync(serverId, "start", "running", ct);

    public async Task<ProviderSnapshot> CreateSnapshotAsync(string serverId, string description, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        using var request = CreateRequest(HttpMethod.Post, "snapshots");
        request.Options.Set(ProviderHttpOptions.NoRetry, true);
        request.Content = JsonContent.Create(new { instance_id = serverId, description = SnapshotOperations.EncodeLabels(description, labels) });
        using var response = await SendAsync(request, ct);
        var result = await ReadSnapshotResponseAsync(response, ct);
        var id = result.Snapshot?.Id;
        if (string.IsNullOrWhiteSpace(id))
            throw new ProviderApiException("Vultr did not return a snapshot ID.", response.StatusCode, Provider);

        var snapshot = await SnapshotOperations.PollAsync(async token =>
            await GetSnapshotAsync(id, token) is { IsAvailable: true } ready ? ready : null,
            SnapshotOperations.SnapshotTimeout, ct)
            ?? throw new TimeoutException($"Vultr snapshot {id} did not become available.");
        return SnapshotOperations.VerifyOwnership(snapshot, labels, Provider);
    }

    public async Task<ProviderSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        using var request = CreateRequest(HttpMethod.Get, $"snapshots/{Uri.EscapeDataString(snapshotId)}");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        var snapshot = (await ReadSnapshotResponseAsync(response, ct)).Snapshot;
        return snapshot?.Status == "deleted" ? null : MapSnapshot(snapshot);
    }

    public async Task<IReadOnlyList<ProviderSnapshot>> ListSnapshotsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var snapshots = new List<ProviderSnapshot>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var query = cursor is null ? "snapshots?per_page=500" : $"snapshots?per_page=500&cursor={Uri.EscapeDataString(cursor)}";
            using var request = CreateRequest(HttpMethod.Get, query);
            using var response = await SendAsync(request, ct);
            var result = await ReadSnapshotResponseAsync(response, ct);
            snapshots.AddRange((result.Snapshots ?? throw new ProviderApiException("Vultr did not return a snapshot list.", provider: Provider))
                .Where(snapshot => snapshot.Status != "deleted")
                .Select(MapSnapshot)
                .Where(snapshot => SnapshotOperations.Matches(snapshot.Labels, labels)));
            cursor = result.Meta?.Links?.Next;
            if (!string.IsNullOrEmpty(cursor) && !seenCursors.Add(cursor))
                throw new ProviderApiException("Vultr returned a repeated snapshot page cursor.", provider: Provider);
        } while (!string.IsNullOrEmpty(cursor));
        return snapshots;
    }

    public async Task DeleteSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        using var request = CreateRequest(HttpMethod.Delete, $"snapshots/{Uri.EscapeDataString(snapshotId)}");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, ct);
    }

    public Task<ProviderServerInfo> CreateServerFromSnapshotAsync(CreateServerRequest request, string snapshotId, string? retainedAddressId, CancellationToken ct)
    {
        SnapshotOperations.ValidateRestore(snapshotId, retainedAddressId);
        return CreateServerAsync(request, snapshotId, ct);
    }

    private async Task ChangePowerAsync(string serverId, string command, string expectedState, CancellationToken ct)
    {
        if (await ReadPowerStateAsync(serverId, expectedState, ct) is not null)
            return;

        using var request = CreateRequest(HttpMethod.Post, $"instances/{Uri.EscapeDataString(serverId)}/{command}");
        request.Options.Set(ProviderHttpOptions.NoRetry, true);
        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        if (await SnapshotOperations.PollAsync(token => ReadPowerStateAsync(serverId, expectedState, token), SnapshotOperations.PowerTimeout, ct) is null)
            throw new TimeoutException($"Vultr instance {serverId} did not reach power state {expectedState}.");
    }

    private async Task<VultrInstanceDto?> ReadPowerStateAsync(string serverId, string expectedState, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        using var request = CreateRequest(HttpMethod.Get, $"instances/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync<VultrInstanceResponse>(cancellationToken: ct);
        var instance = result?.Instance ?? throw new ProviderApiException("Vultr did not return the instance.", response.StatusCode, Provider);
        return instance.PowerStatus == expectedState ? instance : null;
    }

    private async Task<VultrSnapshotResponse> ReadSnapshotResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<VultrSnapshotResponse>(cancellationToken: ct)
            ?? throw new ProviderApiException("Vultr returned an empty snapshot response.", response.StatusCode, Provider);
    }

    private ProviderSnapshot MapSnapshot(VultrSnapshot? snapshot)
    {
        if (snapshot is not { Id: { Length: > 0 }, CreatedAt: not null })
            throw new ProviderApiException("Vultr did not return snapshot metadata.", provider: Provider);
        if (snapshot.Status is not ("pending" or "complete"))
            throw new ProviderApiException($"Vultr snapshot {snapshot.Id} returned an unrecognized status.", provider: Provider);

        var (description, labels) = SnapshotOperations.DecodeLabels(snapshot.Description ?? string.Empty);
        return new(snapshot.Id, description, snapshot.Status == "complete", snapshot.Size / 1_073_741_824m, snapshot.CreatedAt.Value, [], labels);
    }

    private sealed record VultrSnapshotResponse(VultrSnapshot? Snapshot, List<VultrSnapshot>? Snapshots, VultrMetaDto? Meta);
    private sealed record VultrSnapshot(string? Id, string? Description, string? Status, long? Size,
        [property: JsonPropertyName("date_created")] DateTimeOffset? CreatedAt);
}
