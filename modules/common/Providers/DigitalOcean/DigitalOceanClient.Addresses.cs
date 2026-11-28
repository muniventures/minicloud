using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers.DigitalOcean;

public sealed partial class DigitalOceanClient : IVpsProviderAddressClient
{
    private static readonly TimeSpan ReservedIpActionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReservedIpActionPollInterval = TimeSpan.FromSeconds(2);

    public async Task<ProviderRetainedAddress> RetainServerAddressAsync(
        string serverId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var existing = await FindReservedAddressForDropletAsync(serverId, cancellationToken);
        if (existing is not null)
            return MapRetainedAddress(existing);

        var droplet = await ReadDropletForAddressAsync(serverId, cancellationToken);
        var region = droplet.Region?.Slug;
        if (string.IsNullOrWhiteSpace(region))
            throw new ProviderApiException($"DigitalOcean Droplet {serverId} did not report a region for Reserved IP creation.", provider: Provider);

        using var request = CreateRequest(HttpMethod.Post, "reserved_ips");
        request.Content = JsonContent.Create(new DigitalOceanCreateReservedIpPayload { Region = region });
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var created = (await response.Content.ReadFromJsonAsync<DigitalOceanReservedIpResponse>(cancellationToken: cancellationToken))?.ReservedIp;
        if (created is null)
            throw new ProviderApiException("DigitalOcean API returned an empty Reserved IP in the create response.", response.StatusCode, Provider);

        var address = MapRetainedAddress(created);
        await AssignRetainedAddressAsync(address.Id, serverId, cancellationToken);
        return address;
    }

    public async Task<ProviderRetainedAddress?> GetRetainedAddressAsync(
        string addressId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);
        var reservedIp = await ReadReservedIpAsync(addressId, cancellationToken);
        return reservedIp is null ? null : MapRetainedAddress(reservedIp);
    }

    public Task EnsureSurvivesServerDeletionAsync(
        string addressId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);
        cancellationToken.ThrowIfCancellationRequested();
        // DigitalOcean Reserved IPs are independent of the Droplet lifecycle.
        return Task.CompletedTask;
    }

    public async Task AssignRetainedAddressAsync(
        string addressId,
        string serverId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (!long.TryParse(serverId, NumberStyles.None, CultureInfo.InvariantCulture, out var dropletId) || dropletId <= 0)
            throw new ArgumentException("DigitalOcean Droplet references must be numeric IDs.", nameof(serverId));

        var reservedIp = await ReadReservedIpAsync(addressId, cancellationToken)
            ?? throw new ProviderApiException($"DigitalOcean Reserved IP {addressId} was not found.", HttpStatusCode.NotFound, Provider);
        if (reservedIp.Droplet?.Id == dropletId)
            return;
        if (reservedIp.Droplet is not null)
            throw new ProviderApiException($"DigitalOcean Reserved IP {addressId} is already assigned to another Droplet.", provider: Provider);

        await StartReservedIpActionAsync(
            addressId,
            "assign",
            dropletId,
            cancellationToken);

        var assigned = await ReadReservedIpAsync(addressId, cancellationToken);
        if (assigned?.Droplet?.Id != dropletId)
            throw new ProviderApiException($"DigitalOcean Reserved IP {addressId} did not become assigned to Droplet {serverId}.", provider: Provider);
    }

    public async Task ReleaseRetainedAddressAsync(
        string addressId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);

        var reservedIp = await ReadReservedIpAsync(addressId, cancellationToken);
        if (reservedIp is null)
            return;
        if (reservedIp.Droplet is not null)
            await UnassignRetainedAddressAsync(addressId, cancellationToken);

        using var request = CreateRequest(HttpMethod.Delete, $"reserved_ips/{Uri.EscapeDataString(addressId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task UnassignRetainedAddressAsync(string addressId, CancellationToken cancellationToken)
    {
        var reservedIp = await ReadReservedIpAsync(addressId, cancellationToken);
        if (reservedIp is null || reservedIp.Droplet is null)
            return;

        await StartReservedIpActionAsync(addressId, "unassign", null, cancellationToken);
        var unassigned = await ReadReservedIpAsync(addressId, cancellationToken);
        if (unassigned?.Droplet is not null)
            throw new ProviderApiException($"DigitalOcean Reserved IP {addressId} remained assigned after unassign completed.", provider: Provider);
    }

    private async Task StartReservedIpActionAsync(
        string addressId,
        string actionType,
        long? dropletId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"reserved_ips/{Uri.EscapeDataString(addressId)}/actions");
        request.Content = JsonContent.Create(new DigitalOceanReservedIpActionPayload
        {
            Type = actionType,
            DropletId = dropletId
        });
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var action = (await response.Content.ReadFromJsonAsync<DigitalOceanReservedIpActionResponse>(cancellationToken: cancellationToken))?.Action;
        if (action is null || action.Id <= 0)
            throw new ProviderApiException($"DigitalOcean API returned an empty action for Reserved IP {addressId}.", response.StatusCode, Provider);

        if (IsActionComplete(action, actionType))
            return;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReservedIpActionTimeout);
        try
        {
            while (true)
            {
                using var pollRequest = CreateRequest(
                    HttpMethod.Get,
                    $"reserved_ips/{Uri.EscapeDataString(addressId)}/actions/{action.Id.ToString(CultureInfo.InvariantCulture)}");
                using var pollResponse = await SendAsync(pollRequest, deadline.Token);
                await EnsureSuccessAsync(pollResponse, deadline.Token);
                action = (await pollResponse.Content.ReadFromJsonAsync<DigitalOceanReservedIpActionResponse>(cancellationToken: deadline.Token))?.Action;
                if (action is null)
                    throw new ProviderApiException($"DigitalOcean API returned an empty action while polling Reserved IP {addressId}.", pollResponse.StatusCode, Provider);
                if (IsActionComplete(action, actionType))
                    return;

                await Task.Delay(ReservedIpActionPollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProviderApiException($"DigitalOcean Reserved IP {addressId} {actionType} action timed out.", provider: Provider);
        }
    }

    private bool IsActionComplete(DigitalOceanReservedIpActionDto action, string actionType)
    {
        if (string.Equals(action.Status, "completed", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(action.Status, "errored", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action.Status, "failed", StringComparison.OrdinalIgnoreCase))
            throw new ProviderApiException(
                $"DigitalOcean Reserved IP {action.ResourceId} {actionType} action failed with status '{action.Status}'.",
                provider: Provider,
                providerErrorCode: action.ErrorCode);
        return false;
    }

    private async Task<DigitalOceanReservedIpDto?> ReadReservedIpAsync(string addressId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"reserved_ips/{Uri.EscapeDataString(addressId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<DigitalOceanReservedIpResponse>(cancellationToken: cancellationToken))?.ReservedIp
            ?? throw new ProviderApiException($"DigitalOcean API returned an empty Reserved IP response for {addressId}.", response.StatusCode, Provider);
    }

    private async Task<DigitalOceanAddressDropletDto> ReadDropletForAddressAsync(string serverId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"droplets/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ProviderApiException($"DigitalOcean Droplet {serverId} was not found while retaining an address.", response.StatusCode, Provider);

        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<DigitalOceanAddressDropletResponse>(cancellationToken: cancellationToken))?.Droplet
            ?? throw new ProviderApiException($"DigitalOcean API returned an empty Droplet {serverId}.", response.StatusCode, Provider);
    }

    private async Task<DigitalOceanReservedIpDto?> FindReservedAddressForDropletAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var reservedIps = new List<DigitalOceanReservedIpDto>();
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest(HttpMethod.Get, $"reserved_ips?per_page={pageSize}&page={page}");
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<DigitalOceanReservedIpListResponse>(cancellationToken: cancellationToken);
            var pageIps = result?.ReservedIps
                ?? throw new ProviderApiException("DigitalOcean API returned an empty Reserved IP list.", response.StatusCode, Provider);
            var matching = pageIps.FirstOrDefault(x =>
                string.Equals(x.Droplet?.Id.ToString(CultureInfo.InvariantCulture), serverId, StringComparison.Ordinal));
            if (matching is not null)
                return matching;

            reservedIps.AddRange(pageIps);
            if (pageIps.Count < pageSize || result?.Meta?.Total is int total && reservedIps.Count >= total)
                return null;
        }
    }

    private ProviderRetainedAddress MapRetainedAddress(DigitalOceanReservedIpDto reservedIp)
    {
        if (string.IsNullOrWhiteSpace(reservedIp.Ip))
            throw new ProviderApiException("DigitalOcean API returned an invalid Reserved IP.", provider: Provider);

        return new ProviderRetainedAddress(reservedIp.Ip, reservedIp.Ip, reservedIp.Region?.Slug);
    }

    private sealed class DigitalOceanCreateReservedIpPayload
    {
        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;
    }

    private sealed class DigitalOceanAddressDropletResponse
    {
        [JsonPropertyName("droplet")]
        public DigitalOceanAddressDropletDto? Droplet { get; set; }
    }

    private sealed class DigitalOceanAddressDropletDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("region")]
        public DigitalOceanRegionDto? Region { get; set; }
    }

    private sealed class DigitalOceanRegionDto
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }
    }

    private sealed class DigitalOceanReservedIpActionPayload
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("droplet_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? DropletId { get; set; }
    }

    private sealed class DigitalOceanReservedIpResponse
    {
        [JsonPropertyName("reserved_ip")]
        public DigitalOceanReservedIpDto? ReservedIp { get; set; }
    }

    private sealed class DigitalOceanReservedIpListResponse
    {
        [JsonPropertyName("reserved_ips")]
        public List<DigitalOceanReservedIpDto>? ReservedIps { get; set; }

        [JsonPropertyName("meta")]
        public DigitalOceanReservedIpMetaDto? Meta { get; set; }
    }

    private sealed class DigitalOceanReservedIpMetaDto
    {
        [JsonPropertyName("total")]
        public int? Total { get; set; }
    }

    private sealed class DigitalOceanReservedIpDto
    {
        [JsonPropertyName("ip")]
        public string? Ip { get; set; }

        [JsonPropertyName("droplet")]
        public DigitalOceanReservedIpDropletDto? Droplet { get; set; }

        [JsonPropertyName("region")]
        public DigitalOceanRegionDto? Region { get; set; }
    }

    private sealed class DigitalOceanReservedIpDropletDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }

    private sealed class DigitalOceanReservedIpActionResponse
    {
        [JsonPropertyName("action")]
        public DigitalOceanReservedIpActionDto? Action { get; set; }
    }

    private sealed class DigitalOceanReservedIpActionDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("resource_id")]
        public long ResourceId { get; set; }

        [JsonPropertyName("error_code")]
        public string? ErrorCode { get; set; }
    }
}
