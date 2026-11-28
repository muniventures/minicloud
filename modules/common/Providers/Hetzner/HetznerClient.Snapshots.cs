using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers.Hetzner;

public sealed partial class HetznerClient : IVpsProviderSnapshotClient
{
    public async Task PowerOffAsync(string serverId, CancellationToken ct)
    {
        if (await ReadPowerStateAsync(serverId, "off", ct) is not null)
            return;

        var shutdown = (await StartSnapshotActionAsync(serverId, "shutdown", null, ct)).Action!;
        var outcome = await SnapshotOperations.PollAsync<object>(async token =>
        {
            shutdown = await ReadActionAsync(shutdown, token) ?? shutdown;
            return shutdown.Status == "error" ? shutdown : await ReadPowerStateAsync(serverId, "off", token);
        }, SnapshotOperations.GracefulShutdownTimeout, ct);

        if (outcome is not ProviderServerStatus && await ReadPowerStateAsync(serverId, "off", ct) is null)
            await ChangePowerAsync(serverId, "poweroff", "off", ct);
    }

    public async Task PowerOnAsync(string serverId, CancellationToken ct)
    {
        if (await ReadPowerStateAsync(serverId, "running", ct) is null)
            await ChangePowerAsync(serverId, "poweron", "running", ct);
    }

    public async Task<ProviderSnapshot> CreateSnapshotAsync(string serverId, string description, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var started = await StartSnapshotActionAsync(serverId, "create_image", new { type = "snapshot", description, labels }, ct);
        var imageId = started.Image is { Id: > 0 } image ? image.Id.ToString(CultureInfo.InvariantCulture)
            : throw new ProviderApiException("Hetzner did not return a snapshot ID.", provider: Provider);
        await WaitForActionAsync(started.Action!, SnapshotOperations.SnapshotTimeout, ct);

        var snapshot = await SnapshotOperations.PollAsync(async token =>
            await GetSnapshotAsync(imageId, token) is { IsAvailable: true } ready ? ready : null,
            SnapshotOperations.SnapshotTimeout, ct)
            ?? throw new TimeoutException($"Hetzner snapshot {imageId} did not become available.");
        return SnapshotOperations.VerifyOwnership(snapshot, labels, Provider);
    }

    public async Task<ProviderSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        using var request = CreateRequest(HttpMethod.Get, $"images/{Uri.EscapeDataString(snapshotId)}");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        var result = await ReadSnapshotResponseAsync(response, ct);
        return result.Image is { Type: not "snapshot" } ? null : MapSnapshot(result.Image);
    }

    public async Task<IReadOnlyList<ProviderSnapshot>> ListSnapshotsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var snapshots = new List<ProviderSnapshot>();
        int? page = 1;
        do
        {
            using var request = CreateRequest(HttpMethod.Get, $"images?type=snapshot&per_page=50&page={page}");
            using var response = await SendAsync(request, ct);
            var result = await ReadSnapshotResponseAsync(response, ct);
            snapshots.AddRange((result.Images ?? throw new ProviderApiException("Hetzner did not return an image list.", provider: Provider))
                .Where(image => image.Type == "snapshot")
                .Select(MapSnapshot)
                .Where(image => SnapshotOperations.Matches(image.Labels, labels)));
            page = result.Meta?.Pagination?.NextPage;
        } while (page is not null);
        return snapshots;
    }

    public async Task DeleteSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        using var request = CreateRequest(HttpMethod.Delete, $"images/{Uri.EscapeDataString(snapshotId)}");
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
        var result = await StartSnapshotActionAsync(serverId, command, null, ct);
        await WaitForActionAsync(result.Action!, SnapshotOperations.PowerTimeout, ct);
        if (await SnapshotOperations.PollAsync(token => ReadPowerStateAsync(serverId, expectedState, token), SnapshotOperations.PowerTimeout, ct) is null)
            throw new TimeoutException($"Hetzner server {serverId} did not reach power state {expectedState}.");
    }

    private async Task<ProviderServerStatus?> ReadPowerStateAsync(string serverId, string expectedState, CancellationToken ct)
    {
        var status = await GetServerStatusAsync(serverId, ct);
        if (status.Status == "not_found")
            throw new ProviderApiException($"Hetzner server {serverId} was not found.", HttpStatusCode.NotFound, Provider);
        return status.Status == expectedState ? status : null;
    }

    private async Task<HetznerSnapshotResponse> StartSnapshotActionAsync(string serverId, string command, object? payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        using var request = CreateRequest(HttpMethod.Post, $"servers/{Uri.EscapeDataString(serverId)}/actions/{command}");
        request.Options.Set(NoRetry, true);
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        using var response = await SendAsync(request, ct);
        var result = await ReadSnapshotResponseAsync(response, ct);
        if (result.Action is not { Id: > 0 })
            throw new ProviderApiException($"Hetzner did not return an action for {command}.", response.StatusCode, Provider);
        return result;
    }

    private async Task WaitForActionAsync(HetznerSnapshotAction action, TimeSpan timeout, CancellationToken ct)
    {
        var completed = await SnapshotOperations.PollAsync(token => ReadActionAsync(action, token), timeout, ct)
            ?? throw new TimeoutException($"Hetzner action {action.Id} did not complete.");
        if (completed.Status == "error")
            throw new ProviderApiException($"Hetzner action {action.Id} failed.", provider: Provider, providerErrorCode: completed.Error?.Code);
    }

    private async Task<HetznerSnapshotAction?> ReadActionAsync(HetznerSnapshotAction initial, CancellationToken ct)
    {
        var action = initial;
        if (action.Status == "running")
        {
            using var request = CreateRequest(HttpMethod.Get, $"servers/actions/{initial.Id}");
            using var response = await SendAsync(request, ct);
            var result = await ReadSnapshotResponseAsync(response, ct);
            action = result.Action is { } current && current.Id == initial.Id ? current
                : throw new ProviderApiException($"Hetzner did not return action {initial.Id}.", response.StatusCode, Provider);
        }

        return action.Status switch
        {
            "success" or "error" => action,
            "running" => null,
            _ => throw new ProviderApiException($"Hetzner action {initial.Id} returned an unrecognized status.", provider: Provider)
        };
    }

    private async Task<HetznerSnapshotResponse> ReadSnapshotResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<HetznerSnapshotResponse>(cancellationToken: ct)
            ?? throw new ProviderApiException("Hetzner returned an empty snapshot response.", response.StatusCode, Provider);
    }

    private ProviderSnapshot MapSnapshot(HetznerImage? image) =>
        image is { Id: > 0, Created: not null }
            ? new(image.Id.ToString(CultureInfo.InvariantCulture), image.Description ?? string.Empty,
                image.Status == "available", image.SizeGb, image.Created.Value, [], image.Labels ?? [])
            : throw new ProviderApiException("Hetzner did not return snapshot metadata.", provider: Provider);

    private sealed record HetznerSnapshotResponse(HetznerSnapshotAction? Action, HetznerImage? Image, List<HetznerImage>? Images, HetznerMetaDto? Meta);
    private sealed record HetznerSnapshotAction(long Id, string? Status, HetznerActionError? Error);
    private sealed record HetznerActionError(string? Code);

    private sealed record HetznerImage(
        long Id, string? Type, string? Status, string? Description, DateTimeOffset? Created,
        [property: JsonPropertyName("image_size")] decimal? SizeGb, Dictionary<string, string>? Labels);
}
