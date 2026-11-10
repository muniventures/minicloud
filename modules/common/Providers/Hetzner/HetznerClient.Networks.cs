using System.Net;
using System.Text.Json;

namespace Minicloud.Common.Providers.Hetzner;

public sealed partial class HetznerClient : IPrivateNetworkProvider
{
    public async Task<string> GetRegionNetworkZoneAsync(string region, CancellationToken ct)
    {
        using var doc = await VolumeRequestAsync(HttpMethod.Get, "locations?name=" + Uri.EscapeDataString(region), null, ct);
        var locations = doc!.RootElement.GetProperty("locations").EnumerateArray().ToArray();
        if (locations.Length != 1 || !locations[0].TryGetProperty("network_zone", out var zone) || string.IsNullOrWhiteSpace(zone.GetString()))
        { throw new InvalidOperationException("Provider region has no verified network zone."); }
        return zone.GetString()!;
    }

    public async Task<ProviderPrivateNetwork?> FindPrivateNetworkAsync(string name, CancellationToken ct)
    {
        using var doc = await VolumeRequestAsync(HttpMethod.Get, "networks?name=" + Uri.EscapeDataString(name), null, ct);
        var matches = doc!.RootElement.GetProperty("networks").EnumerateArray().ToArray();
        if (matches.Length > 1) { throw new InvalidOperationException("Provider network identity is ambiguous."); }
        return matches.Length == 0 ? null : ReadPrivateNetwork(matches[0]);
    }

    public async Task<ProviderPrivateNetwork?> GetPrivateNetworkAsync(string id, CancellationToken ct)
    {
        using var doc = await VolumeRequestAsync(HttpMethod.Get, "networks/" + Uri.EscapeDataString(id), null, ct);
        return doc is null ? null : ReadPrivateNetwork(doc.RootElement.GetProperty("network"));
    }

    public async Task<ProviderPrivateNetwork> CreatePrivateNetworkAsync(string name, string ipRange, string subnetRange, string networkZone, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        using var doc = await VolumeRequestAsync(HttpMethod.Post, "networks", new
        {
            name, ip_range = ipRange, labels,
            subnets = new[] { new { type = "cloud", ip_range = subnetRange, network_zone = networkZone } }
        }, ct);
        return ReadPrivateNetwork(doc!.RootElement.GetProperty("network"));
    }

    public async Task<ProviderPrivateAddress> AttachPrivateNetworkAsync(string networkId, string serverId, CancellationToken ct)
    {
        var network = await GetPrivateNetworkAsync(networkId, ct) ?? throw new InvalidOperationException("Provider network disappeared.");
        using var before = await VolumeRequestAsync(HttpMethod.Get, "servers/" + Uri.EscapeDataString(serverId), null, ct);
        if (before is null) { throw new InvalidOperationException("Provider server disappeared."); }
        var server = before.RootElement.GetProperty("server");
        var zone = server.GetProperty("datacenter").GetProperty("location").GetProperty("network_zone").GetString();
        if (zone != network.NetworkZone) { throw new InvalidOperationException("Server and network zones differ."); }
        var existing = ReadPrivateAddress(server, networkId, network.NetworkZone);
        if (existing is not null) { return existing; }
        using var attached = await VolumeRequestAsync(HttpMethod.Post, "servers/" + Uri.EscapeDataString(serverId) + "/actions/attach_to_network", new { network = long.Parse(networkId) }, ct);
        await WaitForNetworkActionAsync(attached!, ct);
        using var after = await VolumeRequestAsync(HttpMethod.Get, "servers/" + Uri.EscapeDataString(serverId), null, ct);
        return after is null ? throw new InvalidOperationException("Server disappeared after attachment.")
            : ReadPrivateAddress(after.RootElement.GetProperty("server"), networkId, network.NetworkZone)
                ?? throw new InvalidOperationException("Network attachment has no observed private address.");
    }

    private async Task WaitForNetworkActionAsync(JsonDocument document, CancellationToken ct)
    {
        var action = document.RootElement.GetProperty("action");
        var id = action.GetProperty("id").GetInt64();
        var status = action.GetProperty("status").GetString();
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (status == "success") { return; }
            if (status == "error") { throw new InvalidOperationException("Provider network action failed."); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var pending = await VolumeRequestAsync(HttpMethod.Get, "actions/" + id, null, ct);
            status = pending!.RootElement.GetProperty("action").GetProperty("status").GetString();
        }
        throw new TimeoutException("Provider network action did not complete within five minutes.");
    }

    public async Task DetachPrivateNetworkAsync(string networkId, string serverId, CancellationToken ct)
    {
        using var before = await VolumeRequestAsync(HttpMethod.Get, "servers/" + Uri.EscapeDataString(serverId), null, ct);
        if (before is null || ReadPrivateAddress(before.RootElement.GetProperty("server"), networkId, "") is null) { return; }
        using var detached = await VolumeRequestAsync(HttpMethod.Post, "servers/" + Uri.EscapeDataString(serverId) + "/actions/detach_from_network", new { network = long.Parse(networkId) }, ct);
        await WaitForNetworkActionAsync(detached!, ct);
        using var after = await VolumeRequestAsync(HttpMethod.Get, "servers/" + Uri.EscapeDataString(serverId), null, ct);
        if (after is not null && ReadPrivateAddress(after.RootElement.GetProperty("server"), networkId, "") is not null)
        { throw new InvalidOperationException("Provider server remains attached after network detach."); }
    }

    public async Task DeletePrivateNetworkAsync(string networkId, CancellationToken ct)
    {
        var network = await GetPrivateNetworkAsync(networkId, ct);
        if (network is null) { return; }
        if (network.ServerIds is null || network.ServerIds.Count != 0) { throw new InvalidOperationException("Only an observed empty provider network can be deleted."); }
        using var request = CreateRequest(HttpMethod.Delete, "networks/" + Uri.EscapeDataString(networkId));
        request.Options.Set(NoRetry, true);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.NotFound) { await EnsureSuccessAsync(response, ct); }
        if (await GetPrivateNetworkAsync(networkId, ct) is not null)
        { throw new InvalidOperationException("Provider network still exists after deletion."); }
    }

    private static ProviderPrivateNetwork ReadPrivateNetwork(JsonElement network)
    {
        var subnets = network.GetProperty("subnets").EnumerateArray().ToArray();
        var zones = subnets.Select(x => x.GetProperty("network_zone").GetString()).Distinct().ToArray();
        if (zones.Length != 1 || string.IsNullOrWhiteSpace(zones[0])) { throw new InvalidOperationException("Provider network zone is ambiguous."); }
        return new(network.GetProperty("id").GetInt64().ToString(), network.GetProperty("name").GetString()!, network.GetProperty("ip_range").GetString()!, zones[0]!,
            network.GetProperty("labels").Deserialize<Dictionary<string, string>>() ?? [],
            network.TryGetProperty("servers", out var servers) ? servers.EnumerateArray().Select(x => x.GetInt64().ToString()).ToArray() : null);
    }

    private static ProviderPrivateAddress? ReadPrivateAddress(JsonElement server, string networkId, string zone)
    {
        var matches = server.GetProperty("private_net").EnumerateArray().Where(x => x.GetProperty("network").GetInt64().ToString() == networkId).ToArray();
        if (matches.Length > 1) { throw new InvalidOperationException("Provider private address is ambiguous."); }
        if (matches.Length == 0) { return null; }
        var ip = matches[0].GetProperty("ip").GetString();
        if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        { throw new InvalidOperationException("Provider private address is invalid."); }
        return new(server.GetProperty("id").GetInt64().ToString(), networkId, ip!, zone);
    }
}
