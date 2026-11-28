using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Extensibility;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers.Vultr;

public sealed partial class VultrClient : IVpsProviderAddressClient
{
    public async Task<ProviderRetainedAddress> RetainServerAddressAsync(
        string serverId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using (var request = CreateRequest(HttpMethod.Get, $"instances/{Uri.EscapeDataString(serverId)}"))
        using (var response = await SendAsync(request, cancellationToken))
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ProviderApiException($"Vultr instance {serverId} was not found while retaining its IPv4 address.", response.StatusCode, Provider);

            await EnsureSuccessAsync(response, cancellationToken);
            var instance = (await response.Content.ReadFromJsonAsync<VultrInstanceResponse>(cancellationToken: cancellationToken))?.Instance;
            if (instance is null || string.IsNullOrWhiteSpace(instance.MainIp) || instance.MainIp == "0.0.0.0")
                throw new ProviderApiException($"Vultr instance {serverId} has no IPv4 address to retain.", provider: Provider);

            var existing = await FindReservedAddressForInstanceAsync(serverId, cancellationToken);
            if (existing is not null)
                return MapRetainedAddress(existing);

            using var convertRequest = CreateRequest(HttpMethod.Post, "reserved-ips/convert");
            convertRequest.Options.Set(ProviderHttpOptions.NoRetry, true);
            convertRequest.Content = JsonContent.Create(new VultrConvertReservedIpPayload
            {
                IpAddress = instance.MainIp,
                Label = $"Selfrun retained IPv4 for {serverId}"
            });
            using var convertResponse = await SendAsync(convertRequest, cancellationToken);
            await EnsureSuccessAsync(convertResponse, cancellationToken);
            var converted = (await convertResponse.Content.ReadFromJsonAsync<VultrReservedIpResponse>(cancellationToken: cancellationToken))?.ReservedIp;
            return converted is null
                ? throw new ProviderApiException("Vultr API returned an empty reserved IP after conversion.", convertResponse.StatusCode, Provider)
                : MapRetainedAddress(converted);
        }
    }

    public async Task<ProviderRetainedAddress?> GetRetainedAddressAsync(
        string addressId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);

        using var request = CreateRequest(HttpMethod.Get, $"reserved-ips/{Uri.EscapeDataString(addressId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        var reservedIp = (await response.Content.ReadFromJsonAsync<VultrReservedIpResponse>(cancellationToken: cancellationToken))?.ReservedIp
            ?? throw new ProviderApiException($"Vultr API returned an empty reserved IP response for {addressId}.", response.StatusCode, Provider);
        return MapRetainedAddress(reservedIp);
    }

    public Task EnsureSurvivesServerDeletionAsync(
        string addressId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);
        cancellationToken.ThrowIfCancellationRequested();
        // Vultr keeps the reserved address independently; creation binds it through reserved_ipv4.
        return Task.CompletedTask;
    }

    public Task AssignRetainedAddressAsync(
        string addressId,
        string serverId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        cancellationToken.ThrowIfCancellationRequested();
        // Vultr binds the address through reserved_ipv4 when the instance is created.
        return Task.CompletedTask;
    }

    public async Task ReleaseRetainedAddressAsync(
        string addressId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressId);

        using var request = CreateRequest(HttpMethod.Delete, $"reserved-ips/{Uri.EscapeDataString(addressId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<VultrReservedIpDto?> FindReservedAddressForInstanceAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest(HttpMethod.Get, $"reserved-ips?per_page={pageSize}&page={page}");
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<VultrReservedIpListResponse>(cancellationToken: cancellationToken);
            var addresses = result?.ReservedIps
                ?? throw new ProviderApiException("Vultr API returned an empty reserved IP list.", response.StatusCode, Provider);
            var existing = addresses.FirstOrDefault(x => string.Equals(x.InstanceId, serverId, StringComparison.Ordinal));
            if (existing is not null)
                return existing;
            if (addresses.Count < pageSize || string.IsNullOrEmpty(result?.Meta?.Links?.Next))
                return null;
        }
    }

    private ProviderRetainedAddress MapRetainedAddress(VultrReservedIpDto reservedIp)
    {
        if (string.IsNullOrWhiteSpace(reservedIp.Id) || string.IsNullOrWhiteSpace(reservedIp.Subnet))
            throw new ProviderApiException("Vultr API returned an invalid reserved IPv4 address.", provider: Provider);
        if (!string.IsNullOrWhiteSpace(reservedIp.IpType) &&
            !string.Equals(reservedIp.IpType, "v4", StringComparison.OrdinalIgnoreCase))
            throw new ProviderApiException($"Vultr reserved IP {reservedIp.Id} is not IPv4.", provider: Provider);

        return new ProviderRetainedAddress(reservedIp.Id, reservedIp.Subnet, Region: reservedIp.Region);
    }

    private sealed class VultrConvertReservedIpPayload
    {
        [JsonPropertyName("ip_address")]
        public string IpAddress { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;
    }

    private sealed class VultrReservedIpResponse
    {
        [JsonPropertyName("reserved_ip")]
        public VultrReservedIpDto? ReservedIp { get; set; }
    }

    private sealed class VultrReservedIpListResponse
    {
        [JsonPropertyName("reserved_ips")]
        public List<VultrReservedIpDto>? ReservedIps { get; set; }

        [JsonPropertyName("meta")]
        public VultrReservedIpMetaDto? Meta { get; set; }
    }

    private sealed class VultrReservedIpMetaDto
    {
        [JsonPropertyName("links")]
        public VultrReservedIpLinksDto? Links { get; set; }
    }

    private sealed class VultrReservedIpLinksDto
    {
        [JsonPropertyName("next")]
        public string? Next { get; set; }
    }

    private sealed class VultrReservedIpDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("region")]
        public string? Region { get; set; }

        [JsonPropertyName("ip_type")]
        public string? IpType { get; set; }

        [JsonPropertyName("subnet")]
        public string? Subnet { get; set; }

        [JsonPropertyName("instance_id")]
        public string? InstanceId { get; set; }
    }
}
