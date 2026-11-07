using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Registry;

public sealed class RegistryMaintenanceService(MinicloudDbContext db, ImagePublicationService publications, RegistryClient registry, TimeProvider clock)
{
    public Task<string> BeginAsync(CancellationToken ct) => publications.LockedAsync(async () =>
    {
        var state = await db.RegistryMaintenanceStates.SingleOrDefaultAsync(x => x.Id == "registry", ct);
        if (state is null) { state = new RegistryMaintenanceState(); db.RegistryMaintenanceStates.Add(state); }
        state.WritesBlocked = true;
        state.OwnerToken ??= RegistryTokens.NewSecret();
        state.Revision++;
        state.UpdatedAt = clock.GetUtcNow();
        foreach (var session in await db.RegistrySessions.Where(x => x.Kind == RegistrySessionKind.Push && x.RevokedAt == null).ToListAsync(ct))
        { session.RevokedAt = clock.GetUtcNow(); session.Revision++; }
        return state.OwnerToken;
    }, ct);

    public async Task<int> SweepAsync(string owner, CancellationToken ct)
    {
        var retired = await publications.LockedAsync(async () =>
        {
            await RequireOwnerAsync(owner, ct);
            var now = clock.GetUtcNow();
            var successful = await db.DeploymentServices.AsNoTracking()
                .Where(x => x.Deployment.Status == DeploymentStatus.Succeeded && x.Deployment.App.DeletionStatus == null)
                .Select(x => new { x.ImagePublicationId, x.Name, x.Deployment.AppId, x.Deployment.CreatedAt, x.Deployment.Id }).ToListAsync(ct);
            var keep = successful.GroupBy(x => new { x.AppId, x.Name }).SelectMany(g => g.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id, StringComparer.Ordinal).Take(1)
                .Where(x => x.ImagePublicationId != null).Select(x => x.ImagePublicationId!)).ToHashSet(StringComparer.Ordinal);
            var active = await db.DeploymentServices.AsNoTracking().Where(x => x.ImagePublicationId != null &&
                (x.Deployment.Status == DeploymentStatus.Queued || x.Deployment.Status == DeploymentStatus.Dispatching || x.Deployment.Status == DeploymentStatus.Provisioning || x.Deployment.Status == DeploymentStatus.Deploying || x.Deployment.Status == DeploymentStatus.Verifying))
                .Select(x => x.ImagePublicationId!).ToListAsync(ct);
            keep.UnionWith(active);
            var referenced = (await db.DeploymentServices.AsNoTracking().Where(x => x.ImagePublicationId != null)
                .Select(x => x.ImagePublicationId!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            var all = await db.ImagePublications.Include(x => x.App).ToListAsync(ct);
            foreach (var publication in all.Where(x => x.RetiredAt == null))
            {
                var bound = referenced.Contains(publication.Id);
                var expired = !bound && publication.ExpiresAt <= now;
                if (!keep.Contains(publication.Id) && (expired || bound || publication.App.DeletionStatus is not null))
                {
                    db.AuditEvents.Add(new AuditEvent
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        OrganizationId = publication.OrganizationId,
                        AppId = publication.AppId,
                        Actor = "registry-maintenance",
                        Action = "image_publication.retired",
                        Details = publication.Id,
                        CreatedAt = now
                    });
                    publication.RetiredAt = now;
                    publication.Revision++;
                    publication.UpdatedAt = now;
                    if (publication.Status is ImagePublicationStatus.Pending or ImagePublicationStatus.Uploading) { publication.Status = ImagePublicationStatus.Expired; }
                }
            }
            return all.Where(x => x.RetiredAt != null && x.ManifestDigest != null && !all.Any(y => y.RetiredAt == null && y.Repository == x.Repository && y.ManifestDigest == x.ManifestDigest))
                .Select(x => new { x.Repository, Digest = x.ManifestDigest! }).Distinct().ToArray();
        }, ct);
        // Retirement commits before physical deletion: failed cleanup cannot restore a false rollback promise.
        foreach (var image in retired) { await registry.DeleteAsync(image.Repository, "manifests", image.Digest, ct); }
        foreach (var garbage in await db.RegistryGarbageCandidates.ToListAsync(ct))
        {
            if (!await db.ImagePublications.AnyAsync(x => x.Repository == garbage.Repository && x.ManifestDigest == garbage.ManifestDigest && x.RetiredAt == null, ct))
            { await registry.DeleteAsync(garbage.Repository, "manifests", garbage.ManifestDigest, ct); }
            db.RegistryGarbageCandidates.Remove(garbage);
        }
        var oldUploads = await db.RegistryUploads.Include(x => x.Session).ThenInclude(x => x.ImagePublication)
            .Where(x => x.Session.RevokedAt != null).ToListAsync(ct);
        foreach (var upload in oldUploads)
        {
            await registry.DeleteAsync(upload.Session.ImagePublication!.Repository, "blobs/uploads", upload.Id, ct);
            db.RegistryUploads.Remove(upload);
        }
        await db.SaveChangesAsync(ct);
        return retired.Length;
    }

    public Task<bool> FinishAsync(string owner, CancellationToken ct, bool collected = true) => publications.LockedAsync(async () =>
    {
        var state = await RequireOwnerAsync(owner, ct);
        await registry.PingAsync(ct);
        var old = collected ? await db.RegistrySessions.Where(x => x.Kind == RegistrySessionKind.Push && x.RevokedAt != null && x.ReclaimedAt == null)
            .Where(x => !db.RegistryUploads.Any(u => u.SessionId == x.Id)).ToListAsync(ct)
            : new List<RegistrySession>();
        foreach (var session in old) { session.ReclaimedAt = clock.GetUtcNow(); session.Revision++; }
        state.WritesBlocked = false;
        state.OwnerToken = null;
        state.Revision++;
        state.UpdatedAt = clock.GetUtcNow();
        return true;
    }, ct);

    private async Task<RegistryMaintenanceState> RequireOwnerAsync(string owner, CancellationToken ct)
    {
        var state = await db.RegistryMaintenanceStates.SingleAsync(x => x.Id == "registry", ct);
        if (!state.WritesBlocked || state.OwnerToken != owner) { throw new UnauthorizedAccessException("Registry maintenance ownership changed."); }
        return state;
    }
}
