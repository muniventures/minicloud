using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers.Hetzner;

public sealed partial class HetznerClient : IVpsProviderAddressClient
{
    public async Task<ProviderRetainedAddress> RetainServerAddressAsync(
        string serverId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using var request = CreateRequest(HttpMethod.Get, $"servers/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ProviderApiException($"Hetzner server {serverId} was not found while retaining its IPv4 address.", response.StatusCode, Provider);

        await EnsureSuccessAsync(response, cancellationToken);
        var server = (await response.Content.ReadFromJsonAsync<HetznerServerResponse>(cancellationToken: cancellationToken))?.Server;
        if (server?.PublicNet?.Ipv4 is not { Id: > 0, Ip: { Length: > 0 } } ipv4)
            throw new ProviderApiException($"Hetzner server {serverId} has no Primary IPv4 address to retain.", provider: Provider);

        var addressId = ipv4.Id.ToString(CultureInfo.InvariantCulture);
        var address = await GetRetainedAddressAsync(addressId, cancellationToken)
            ?? throw new ProviderApiException($"Hetzner Primary IP {addressId} was not found.", provider: Provider);
        await EnsureSurvivesServerDeletionAsync(addressId, cancellationToken);
        return address;
    }

    public async Task<ProviderRetainedAddress?> GetRetainedAddressAsync(
        string addressId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);

        using var request = CreateRequest(HttpMethod.Get, $"primary_ips/{Uri.EscapeDataString(addressId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        var primaryIp = (await response.Content.ReadFromJsonAsync<HetznerPrimaryIpResponse>(cancellationToken: cancellationToken))?.PrimaryIp
            ?? throw new ProviderApiException($"Hetzner API returned an empty Primary IP response for {addressId}.", response.StatusCode, Provider);
        return MapRetainedAddress(primaryIp);
    }

    public async Task EnsureSurvivesServerDeletionAsync(
        string addressId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);

        var primaryIp = await ReadPrimaryIpAsync(addressId, cancellationToken)
            ?? throw new ProviderApiException($"Hetzner Primary IP {addressId} was not found.", HttpStatusCode.NotFound, Provider);
        if (primaryIp.AutoDelete == false)
            return;

        using var request = CreateRequest(HttpMethod.Put, $"primary_ips/{Uri.EscapeDataString(addressId)}");
        request.Content = JsonContent.Create(new HetznerUpdatePrimaryIpPayload { AutoDelete = false });
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task AssignRetainedAddressAsync(
        string addressId,
        string serverId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (!long.TryParse(serverId, NumberStyles.None, CultureInfo.InvariantCulture, out var serverNumericId) || serverNumericId <= 0)
            throw new ArgumentException("Hetzner server references must be numeric IDs.", nameof(serverId));

        var primaryIp = await ReadPrimaryIpAsync(addressId, cancellationToken)
            ?? throw new ProviderApiException($"Hetzner Primary IP {addressId} was not found.", HttpStatusCode.NotFound, Provider);
        if (string.Equals(primaryIp.AssigneeType, "server", StringComparison.OrdinalIgnoreCase) &&
            primaryIp.AssigneeId == serverNumericId)
            return;
        if (primaryIp.AssigneeId is not null)
            throw new ProviderApiException($"Hetzner Primary IP {addressId} is already assigned to another resource.", provider: Provider);

        using var request = CreateRequest(HttpMethod.Put, $"primary_ips/{Uri.EscapeDataString(addressId)}");
        request.Content = JsonContent.Create(new HetznerUpdatePrimaryIpPayload
        {
            AutoDelete = false,
            AssigneeId = serverNumericId,
            AssigneeType = "server"
        });
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ReleaseRetainedAddressAsync(
        string addressId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);

        using var request = CreateRequest(HttpMethod.Delete, $"primary_ips/{Uri.EscapeDataString(addressId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HetznerPrimaryIpDto?> ReadPrimaryIpAsync(string addressId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"primary_ips/{Uri.EscapeDataString(addressId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<HetznerPrimaryIpResponse>(cancellationToken: cancellationToken))?.PrimaryIp
            ?? throw new ProviderApiException($"Hetzner API returned an empty Primary IP response for {addressId}.", response.StatusCode, Provider);
    }

    private ProviderRetainedAddress MapRetainedAddress(HetznerPrimaryIpDto primaryIp)
    {
        if (primaryIp.Id <= 0 || string.IsNullOrWhiteSpace(primaryIp.Ip))
            throw new ProviderApiException("Hetzner API returned an invalid Primary IP.", provider: Provider);

        return new ProviderRetainedAddress(
            primaryIp.Id.ToString(CultureInfo.InvariantCulture),
            primaryIp.Ip,
            Datacenter: primaryIp.Datacenter);
    }

    private sealed class HetznerPrimaryIpResponse
    {
        [JsonPropertyName("primary_ip")]
        public HetznerPrimaryIpDto? PrimaryIp { get; set; }
    }

    private sealed class HetznerPrimaryIpDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("ip")]
        public string? Ip { get; set; }

        [JsonPropertyName("datacenter")]
        public string? Datacenter { get; set; }

        [JsonPropertyName("auto_delete")]
        public bool? AutoDelete { get; set; }

        [JsonPropertyName("assignee_id")]
        public long? AssigneeId { get; set; }

        [JsonPropertyName("assignee_type")]
        public string? AssigneeType { get; set; }
    }

    private sealed class HetznerUpdatePrimaryIpPayload
    {
        [JsonPropertyName("auto_delete")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? AutoDelete { get; set; }

        [JsonPropertyName("assignee_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? AssigneeId { get; set; }

        [JsonPropertyName("assignee_type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AssigneeType { get; set; }
    }
}
