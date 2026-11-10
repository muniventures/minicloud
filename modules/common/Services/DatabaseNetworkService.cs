using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;

namespace Minicloud.Common.Services;

public sealed class DatabaseNetworkService(MinicloudDbContext db, DatabaseManagementService databases, ProviderCatalogService catalog, ProviderAccountClientFactory clients, IIdGenerator ids, TimeProvider clock)
{
    public async Task<ManagedDatabaseNetwork> CreateAsync(CreateDatabaseNetworkRequest request, ActorIdentity actor, CancellationToken ct)
    {
        await databases.RequireAsync(actor, request.OrganizationId, true, ct);
        if (!Regex.IsMatch(request.Name, "^[a-z][a-z0-9_-]{0,62}$")) { throw new ArgumentException("Use a lowercase network name with letters, digits, underscores or dashes."); }
        ValidateRanges(request.IpRange, request.SubnetRange);
        var plan = await catalog.ResolveAsync(request.OrganizationId, request.Plan, ct);
        if (plan.ProviderAccount.ProviderType != CloudProvider.Hetzner) { throw new ArgumentException("Provider-native database networks currently require Hetzner."); }
        if (await db.ManagedDatabaseNetworks.AnyAsync(x => x.OrganizationId == request.OrganizationId && x.Name == request.Name && x.Status != "deleted", ct))
        { throw new InvalidOperationException("Network name is already in use."); }
        var now = clock.GetUtcNow();
        var network = new ManagedDatabaseNetwork { Id = ids.NewId("dbnetwork"), OrganizationId = request.OrganizationId, Name = request.Name,
            ProviderAccountId = plan.ProviderAccountId, ProviderAccountRevision = plan.ProviderAccount.Revision,
            Region = plan.Region, IpRange = request.IpRange, SubnetRange = request.SubnetRange, CreatedAt = now, UpdatedAt = now };
        db.ManagedDatabaseNetworks.Add(network);
        databases.Queue(network.OrganizationId, null, null, "create-network", new { networkId = network.Id }, actor);
        await db.SaveChangesAsync(ct);
        return network;
    }

    public async Task<bool> ReconcileAsync(string id, string organizationId, CancellationToken ct)
    {
        var network = await db.ManagedDatabaseNetworks.SingleAsync(x => x.Id == id && x.OrganizationId == organizationId, ct);
        var provider = await ProviderAsync(network, ct, allowDisabled: network.ProviderNetworkId is not null);
        var zone = await provider.GetRegionNetworkZoneAsync(network.Region, ct);
        if (network.NetworkZone is not null && network.NetworkZone != zone) { throw new InvalidOperationException("The provider region network zone changed."); }
        network.NetworkZone = zone;
        var providerName = "minicloud-" + network.Id.Replace('_', '-');
        var remote = network.ProviderNetworkId is not null
            ? await provider.GetPrivateNetworkAsync(network.ProviderNetworkId, ct)
            : await provider.FindPrivateNetworkAsync(providerName, ct);
        if (remote is null)
        {
            if (network.CreationAttempted) { throw new InvalidOperationException("Prior network allocation is unresolved; reconcile it before retrying creation."); }
            network.CreationAttempted = true;
            network.UpdatedAt = clock.GetUtcNow();
            network.Revision++;
            // Persist intent before the non-retried POST; an ambiguous response
            // is reconciled by this exact provider/name identity next time.
            await db.SaveChangesAsync(ct);
            remote = await provider.CreatePrivateNetworkAsync(providerName, network.IpRange, network.SubnetRange, zone,
                new Dictionary<string, string> { ["minicloud-org"] = organizationId, ["minicloud-network"] = network.Id, ["minicloud-resource"] = "database-network" }, ct);
        }
        ValidateRemote(network, remote);
        network.ProviderNetworkId = remote.Id;
        network.Status = "ready";
        network.Revision++;
        network.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<DatabasePrivateAttachment> AttachAsync(string id, string organizationId, string serverInstanceId, CancellationToken ct)
    {
        var network = await db.ManagedDatabaseNetworks.SingleAsync(x => x.Id == id && x.OrganizationId == organizationId && x.Status == "ready", ct);
        var server = await db.ServerInstances.SingleAsync(x => x.Id == serverInstanceId && x.OrganizationId == organizationId, ct);
        if (server.ProviderAccountId != network.ProviderAccountId || !string.Equals(server.Provider, "hetzner", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(server.ProviderServerId))
        { throw new InvalidOperationException("A provider-native network and its servers must belong to the same Hetzner project/account."); }
        var provider = await ProviderAsync(network, ct);
        var remote = await provider.GetPrivateNetworkAsync(network.ProviderNetworkId!, ct) ?? throw new InvalidOperationException("Registered network disappeared.");
        ValidateRemote(network, remote);
        var zone = await provider.GetRegionNetworkZoneAsync(server.Region, ct);
        if (zone != network.NetworkZone) { throw new InvalidOperationException("Server location is outside the network zone."); }
        var attached = await provider.AttachPrivateNetworkAsync(remote.Id, server.ProviderServerId, ct);
        if (attached.NetworkId != remote.Id || attached.ServerId != server.ProviderServerId || attached.NetworkZone != network.NetworkZone || !CidrContains(network.SubnetRange, attached.IpAddress))
        { throw new InvalidOperationException("Observed provider attachment does not match the selected network."); }
        // This is provider state only. The worker must verify guest address,
        // route and TLS reachability before advertising database access ready.
        return new(network.Id, remote.Id, server.Id, attached.IpAddress, attached.NetworkZone, network.SubnetRange);
    }

    public async Task<bool> DeleteAsync(string id, string organizationId, CancellationToken ct)
    {
        var network = await db.ManagedDatabaseNetworks.SingleAsync(x => x.Id == id && x.OrganizationId == organizationId, ct);
        if (network.Status == "deleted") { return true; }
        if (await db.ManagedDatabases.AnyAsync(x => x.NetworkId == id && x.Status != "deleted", ct))
        { throw new InvalidOperationException("Remove database access references before deleting this network."); }
        var provider = await ProviderAsync(network, ct, allowDisabled: true);
        var remote = network.ProviderNetworkId is not null
            ? await provider.GetPrivateNetworkAsync(network.ProviderNetworkId, ct)
            : await provider.FindPrivateNetworkAsync("minicloud-" + network.Id.Replace('_', '-'), ct);
        if (remote is null && network.ProviderNetworkId is null && network.CreationAttempted)
        { throw new InvalidOperationException("Prior network creation is unresolved; reconcile provider state before deleting its registration."); }
        if (remote is not null)
        {
            ValidateRemote(network, remote);
            if (remote.ServerIds is null) { throw new InvalidOperationException("Provider network attachment inventory is unavailable."); }
            // Validate the complete observed inventory before the first detach.
            // An external or conflicting registration blocks the whole action.
            foreach (var serverId in remote.ServerIds)
            {
                var registrations = await db.ServerInstances.AsNoTracking().Where(x => x.Provider.ToLower() == "hetzner" && x.ProviderServerId == serverId).ToListAsync(ct);
                if (registrations.Count != 1 || registrations[0].OrganizationId != organizationId || registrations[0].ProviderAccountId != network.ProviderAccountId)
                { throw new InvalidOperationException("Network has an unknown or conflicting server attachment; reconcile ownership before deletion."); }
            }
            network.Status = "deleting";
            network.ProviderNetworkId = remote.Id;
            network.Revision++;
            network.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            foreach (var serverId in remote.ServerIds)
            {
                if (await db.ManagedDatabases.AnyAsync(x => x.NetworkId == id && x.Status != "deleted", ct))
                { throw new InvalidOperationException("A database acquired this network during deletion; reconcile its access."); }
                // Refresh both account generation and remote resource identity
                // before each mutation rather than reusing stale ownership.
                provider = await ProviderAsync(network, ct, allowDisabled: true);
                var current = await provider.GetPrivateNetworkAsync(remote.Id, ct) ?? throw new InvalidOperationException("Provider network changed during detach.");
                ValidateRemote(network, current);
                if (current.ServerIds is null || current.ServerIds.Any(x => !remote.ServerIds.Contains(x)))
                { throw new InvalidOperationException("Network acquired an unverified attachment during deletion."); }
                await provider.DetachPrivateNetworkAsync(remote.Id, serverId, ct);
            }
            provider = await ProviderAsync(network, ct, allowDisabled: true);
            var empty = await provider.GetPrivateNetworkAsync(remote.Id, ct);
            if (empty is not null)
            {
                ValidateRemote(network, empty);
                if (empty.ServerIds is null || empty.ServerIds.Count != 0) { throw new InvalidOperationException("Network still has attachments."); }
                await provider.DeletePrivateNetworkAsync(remote.Id, ct);
            }
        }
        network.Status = "deleted";
        network.Revision++;
        network.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<IPrivateNetworkProvider> ProviderAsync(ManagedDatabaseNetwork network, CancellationToken ct, bool allowDisabled = false)
    {
        var account = await db.ProviderAccounts.AsNoTracking().SingleAsync(x => x.Id == network.ProviderAccountId && x.OrganizationId == network.OrganizationId, ct);
        if (account.ProviderType != CloudProvider.Hetzner || !allowDisabled && !account.Enabled)
        { throw new InvalidOperationException("Network provider account is unsupported or disabled for new allocation/attachment."); }
        if (account.Revision != network.ProviderAccountRevision && network.ProviderNetworkId is null)
        { throw new InvalidOperationException("Unallocated network account changed; reconcile its original allocation intent before continuing."); }
        var provider = await clients.CreateAsync(network.OrganizationId, network.ProviderAccountId, ct, allowDisabled: allowDisabled) as IPrivateNetworkProvider
            ?? throw new InvalidOperationException("This provider adapter does not support private networks.");
        if (account.Revision != network.ProviderAccountRevision)
        {
            // Renames, credential rotation, disablement and re-enablement do not
            // orphan an existing resource when the current project proves the
            // exact pinned identity. A missing resource is NOT deletion proof
            // under changed credentials and must retain its registration.
            var remote = await provider.GetPrivateNetworkAsync(network.ProviderNetworkId!, ct)
                ?? throw new InvalidOperationException("Current provider credentials cannot verify the registered network; restore its project access or reconcile ownership.");
            ValidateRemote(network, remote);
            if (!await db.ProviderAccounts.AnyAsync(x => x.Id == account.Id && x.OrganizationId == account.OrganizationId && x.Revision == account.Revision && (allowDisabled || x.Enabled), ct))
            { throw new InvalidOperationException("Provider account changed during network ownership verification."); }
            network.ProviderAccountRevision = account.Revision;
            network.Revision++;
            network.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return provider;
    }

    private static void ValidateRemote(ManagedDatabaseNetwork local, ProviderPrivateNetwork remote)
    {
        if (local.ProviderNetworkId is not null && remote.Id != local.ProviderNetworkId || remote.Name != "minicloud-" + local.Id.Replace('_', '-') || remote.IpRange != local.IpRange || remote.NetworkZone != local.NetworkZone ||
            !remote.Labels.TryGetValue("minicloud-org", out var org) || org != local.OrganizationId ||
            !remote.Labels.TryGetValue("minicloud-network", out var id) || id != local.Id)
        { throw new InvalidOperationException("Provider network ownership or configuration differs from its registration."); }
    }

    public static void ValidateRanges(string range, string subnet)
    {
        var (network, prefix) = ReadCidr(range);
        var (child, childPrefix) = ReadCidr(subnet);
        var octets = network.GetAddressBytes();
        var privateRange = octets[0] == 10 || octets[0] == 172 && octets[1] is >= 16 and <= 31 || octets[0] == 192 && octets[1] == 168;
        if (!privateRange || octets[0] == 172 && prefix < 12 || octets[0] == 192 && prefix < 16 || prefix is < 8 or > 28 || childPrefix < prefix || childPrefix > 28 || !CidrContains(range, child.ToString()))
        { throw new ArgumentException("Use canonical RFC1918 IPv4 ranges with a contained cloud subnet (prefix /8 through /28)."); }
    }

    public static bool CidrContains(string range, string address)
    {
        var (network, prefix) = ReadCidr(range);
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) { return false; }
        var mask = uint.MaxValue << (32 - prefix);
        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes()) & mask) == System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(network.GetAddressBytes());
    }

    private static (IPAddress Address, int Prefix) ReadCidr(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || !int.TryParse(parts[1], out var prefix) || prefix is < 1 or > 32)
        { throw new ArgumentException("A canonical IPv4 CIDR is required."); }
        var number = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        var mask = uint.MaxValue << (32 - prefix);
        if ((number & mask) != number || value != ip + "/" + prefix) { throw new ArgumentException("A canonical IPv4 network address is required."); }
        return (ip, prefix);
    }
}
