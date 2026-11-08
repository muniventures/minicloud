using System.Security.Cryptography;
using System.Net;
using Minicloud.Common.Providers.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;

namespace Minicloud.Common.Services;

public sealed record RuntimeStorageMount(string Name, string VolumeId, string MountPath, int Uid, int Gid);

public sealed class AppStorageService(MinicloudDbContext db, IStorageVolumeProvider provider)
{
    public static string VolumeName(string appId, string name) => "mc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appId + ":" + name)))[..32].ToLowerInvariant();

    public async Task<RuntimeStorageMount> EnsureAsync(string appId, string name, StorageMountRequest mount, string serverId, string region, CancellationToken ct,
        string? organizationId = null, string? providerAccountId = null, bool allowAllocation = true)
    {
        var identity = VolumeName(appId, name);
        var labels = new Dictionary<string, string> { ["managed-by"] = "minicloud", ["app-id"] = appId, ["storage-name"] = name, ["purpose"] = "customer-storage" };
        var stored = await db.AppStorageVolumes.SingleOrDefaultAsync(x => x.AppId == appId && x.Name == name, ct);
        if (stored is null)
        {
            if (!allowAllocation) { throw new InvalidOperationException("The provider account is disabled for new storage allocation."); }
            stored = new AppStorageVolume { Id = identity, AppId = appId, OrganizationId = organizationId, ProviderAccountId = providerAccountId,
                Name = name, Region = region, SizeGb = mount.SizeGb, Uid = mount.Uid, Gid = mount.Gid };
            db.AppStorageVolumes.Add(stored);
            await db.SaveChangesAsync(ct);
        }
        if (stored.ProviderAccountId != providerAccountId || stored.OrganizationId != organizationId)
        { throw new InvalidOperationException("Storage belongs to a different organization or provider account; automatic migration is not supported."); }
        if (stored.Region != region) { throw new InvalidOperationException("Storage is in a different region; automatic migration is not supported."); }
        if (stored.Uid != mount.Uid || stored.Gid != mount.Gid) { throw new InvalidOperationException("Existing storage ownership cannot change automatically."); }
        var volume = stored.ProviderVolumeId is null ? await provider.FindVolumeAsync(identity, ct) : await provider.GetVolumeAsync(stored.ProviderVolumeId, ct);
        if (volume is null)
        {
            if (stored.ProviderVolumeId is not null || stored.CreationAttempted)
            { throw new InvalidOperationException("Expected storage volume is missing or creation is unresolved; refusing to replace it with an empty disk. Inspect the Hetzner volume before retrying."); }
            if (!allowAllocation) { throw new InvalidOperationException("The provider account is disabled for new storage allocation."); }
            stored.CreationAttempted = true;
            await db.SaveChangesAsync(ct);
            try { volume = await provider.CreateVolumeAsync(identity, mount.SizeGb, region, labels, ct); }
            catch (ProviderApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests)
            {
                // Explicit rejection did not allocate a disk. Ambiguous transport/5xx outcomes stay unresolved.
                stored.CreationAttempted = false;
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }
        if (volume.Name != identity || volume.Region != region || labels.Any(x => !volume.Labels.TryGetValue(x.Key, out var value) || value != x.Value))
        { throw new InvalidOperationException("Provider storage identity does not match this app."); }
        if (volume.ServerId is not null && volume.ServerId != serverId)
        { throw new InvalidOperationException("Storage is attached to another server; refusing forced detach."); }
        stored.ProviderVolumeId = volume.Id;
        stored.SizeGb = Math.Max(stored.SizeGb, volume.SizeGb);
        await db.SaveChangesAsync(ct);
        if (volume.SizeGb < mount.SizeGb)
        {
            await provider.ResizeVolumeAsync(volume.Id, mount.SizeGb, ct);
            stored.SizeGb = mount.SizeGb;
            await db.SaveChangesAsync(ct);
        }
        if (volume.ServerId is null) { await provider.AttachVolumeAsync(volume.Id, serverId, ct); }
        var ready = await provider.GetVolumeAsync(volume.Id, ct);
        if (ready is null || ready.ServerId != serverId || ready.SizeGb < mount.SizeGb)
        { throw new InvalidOperationException("Provider storage is not ready on this app server."); }
        return new(name, volume.Id, mount.MountPath, mount.Uid, mount.Gid);
    }
}
