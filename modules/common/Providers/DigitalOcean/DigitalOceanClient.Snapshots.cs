using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Extensibility;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers.DigitalOcean;

public sealed partial class DigitalOceanClient : IVpsProviderSnapshotClient
{
    public async Task PowerOffAsync(string serverId, CancellationToken ct)
    {
        if (await ReadPowerStateAsync(serverId, "off", ct) is not null)
            return;

        var shutdown = await StartDropletActionAsync(serverId, new { type = "shutdown" }, ct);
        var outcome = await SnapshotOperations.PollAsync<object>(async token =>
        {
            shutdown = await ReadActionAsync(serverId, shutdown, token) ?? shutdown;
            return shutdown.Status == "errored" ? shutdown : await ReadPowerStateAsync(serverId, "off", token);
        }, SnapshotOperations.GracefulShutdownTimeout, ct);

        if (outcome is not ProviderServerStatus && await ReadPowerStateAsync(serverId, "off", ct) is null)
            await ChangePowerAsync(serverId, "power_off", "off", ct);
    }

    public async Task PowerOnAsync(string serverId, CancellationToken ct)
    {
        if (await ReadPowerStateAsync(serverId, "active", ct) is null)
            await ChangePowerAsync(serverId, "power_on", "active", ct);
    }

    public async Task<ProviderSnapshot> CreateSnapshotAsync(string serverId, string description, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var name = SnapshotOperations.EncodeLabels(description, labels);
        var existing = (await ReadSnapshotsAsync(ct))
            .Where(snapshot => snapshot.ResourceId == serverId && snapshot.Name == name)
            .Select(snapshot => snapshot.Id).ToHashSet();
        var action = await StartDropletActionAsync(serverId, new { type = "snapshot", name }, ct);
        await WaitForActionAsync(serverId, action, SnapshotOperations.SnapshotTimeout, ct);

        // A snapshot action's resource ID is the source droplet, not the new image.
        return await SnapshotOperations.PollAsync(async token =>
        {
            var matches = (await ReadSnapshotsAsync(token))
                .Where(snapshot => snapshot.ResourceId == serverId && snapshot.Name == name && !existing.Contains(snapshot.Id)).ToArray();
            return matches.Length switch
            {
                0 => null,
                1 => MapSnapshot(matches[0]),
                _ => throw new ProviderApiException($"DigitalOcean returned multiple new snapshots for action {action.Id}.", provider: Provider)
            };
        }, SnapshotOperations.SnapshotTimeout, ct)
            ?? throw new TimeoutException($"DigitalOcean snapshot from action {action.Id} did not become visible.");
    }

    public async Task<ProviderSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        using var request = CreateRequest(HttpMethod.Get, $"snapshots/{Uri.EscapeDataString(snapshotId)}");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        var result = await ReadSnapshotResponseAsync(response, ct);
        return result.Snapshot is { ResourceType: not "droplet" } ? null : MapSnapshot(result.Snapshot);
    }

    public async Task<IReadOnlyList<ProviderSnapshot>> ListSnapshotsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
        (await ReadSnapshotsAsync(ct))
            .Select(MapSnapshot)
            .Where(snapshot => SnapshotOperations.Matches(snapshot.Labels, labels)).ToArray();

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
        if (!long.TryParse(snapshotId, NumberStyles.None, CultureInfo.InvariantCulture, out var imageId) || imageId <= 0)
            throw new ArgumentException("DigitalOcean droplet snapshot IDs must be positive integers.", nameof(snapshotId));
        return CreateServerAsync(request, imageId, ct);
    }

    private async Task ChangePowerAsync(string serverId, string command, string expectedState, CancellationToken ct)
    {
        var action = await StartDropletActionAsync(serverId, new { type = command }, ct);
        await WaitForActionAsync(serverId, action, SnapshotOperations.PowerTimeout, ct);
        if (await SnapshotOperations.PollAsync(token => ReadPowerStateAsync(serverId, expectedState, token), SnapshotOperations.PowerTimeout, ct) is null)
            throw new TimeoutException($"DigitalOcean droplet {serverId} did not reach power state {expectedState}.");
    }

    private async Task<ProviderServerStatus?> ReadPowerStateAsync(string serverId, string expectedState, CancellationToken ct)
    {
        var status = await GetServerStatusAsync(serverId, ct);
        if (status.Status == "not_found")
            throw new ProviderApiException($"DigitalOcean droplet {serverId} was not found.", HttpStatusCode.NotFound, Provider);
        return status.Status == expectedState ? status : null;
    }

    private async Task<DigitalOceanSnapshotAction> StartDropletActionAsync(string serverId, object payload, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, $"droplets/{Uri.EscapeDataString(serverId)}/actions");
        request.Options.Set(ProviderHttpOptions.NoRetry, true);
        request.Content = JsonContent.Create(payload);
        using var response = await SendAsync(request, ct);
        var result = await ReadSnapshotResponseAsync(response, ct);
        return result.Action is { Id: > 0 } action ? action
            : throw new ProviderApiException("DigitalOcean did not return a droplet action.", response.StatusCode, Provider);
    }

    private async Task WaitForActionAsync(string serverId, DigitalOceanSnapshotAction action, TimeSpan timeout, CancellationToken ct)
    {
        var completed = await SnapshotOperations.PollAsync(token => ReadActionAsync(serverId, action, token), timeout, ct)
            ?? throw new TimeoutException($"DigitalOcean action {action.Id} did not complete.");
        if (completed.Status == "errored")
            throw new ProviderApiException($"DigitalOcean action {action.Id} failed.", provider: Provider);
    }

    private async Task<DigitalOceanSnapshotAction?> ReadActionAsync(string serverId, DigitalOceanSnapshotAction initial, CancellationToken ct)
    {
        var action = initial;
        if (action.Status == "in-progress")
        {
            using var request = CreateRequest(HttpMethod.Get, $"droplets/{Uri.EscapeDataString(serverId)}/actions/{initial.Id}");
            using var response = await SendAsync(request, ct);
            var result = await ReadSnapshotResponseAsync(response, ct);
            action = result.Action is { } current && current.Id == initial.Id ? current
                : throw new ProviderApiException($"DigitalOcean did not return action {initial.Id}.", response.StatusCode, Provider);
        }

        return action.Status switch
        {
            "completed" or "errored" => action,
            "in-progress" => null,
            _ => throw new ProviderApiException($"DigitalOcean action {initial.Id} returned an unrecognized status.", provider: Provider)
        };
    }

    private async Task<List<DigitalOceanSnapshot>> ReadSnapshotsAsync(CancellationToken ct)
    {
        var snapshots = new List<DigitalOceanSnapshot>();
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest(HttpMethod.Get, $"snapshots?resource_type=droplet&per_page=200&page={page}");
            using var response = await SendAsync(request, ct);
            var result = await ReadSnapshotResponseAsync(response, ct);
            var items = result.Snapshots
                ?? throw new ProviderApiException("DigitalOcean did not return a snapshot list.", response.StatusCode, Provider);
            snapshots.AddRange(items.Where(snapshot => snapshot.ResourceType == "droplet"));
            if (string.IsNullOrEmpty(result.Links?.Pages?.Next))
                return snapshots;
        }
    }

    private async Task<DigitalOceanSnapshotResponse> ReadSnapshotResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<DigitalOceanSnapshotResponse>(cancellationToken: ct)
            ?? throw new ProviderApiException("DigitalOcean returned an empty snapshot response.", response.StatusCode, Provider);
    }

    private ProviderSnapshot MapSnapshot(DigitalOceanSnapshot? snapshot)
    {
        if (snapshot is not { Id: { Length: > 0 }, Name: not null, CreatedAt: not null })
            throw new ProviderApiException("DigitalOcean did not return snapshot metadata.", provider: Provider);

        var (description, labels) = SnapshotOperations.DecodeLabels(snapshot.Name);
        return new(snapshot.Id, description, true, snapshot.SizeGb, snapshot.CreatedAt.Value, snapshot.Regions ?? [], labels);
    }

    private sealed record DigitalOceanSnapshotResponse(DigitalOceanSnapshotAction? Action, DigitalOceanSnapshot? Snapshot,
        List<DigitalOceanSnapshot>? Snapshots, DigitalOceanPageLinks? Links);
    private sealed record DigitalOceanSnapshotAction(long Id, string? Status);
    private sealed record DigitalOceanPageLinks(DigitalOceanPage? Pages);
    private sealed record DigitalOceanPage(string? Next);

    private sealed record DigitalOceanSnapshot(
        string? Id, string? Name, List<string>? Regions,
        [property: JsonPropertyName("resource_id")] string? ResourceId,
        [property: JsonPropertyName("resource_type")] string? ResourceType,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("size_gigabytes")] decimal? SizeGb);
}
