using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Registry;

public sealed class ImagePublicationService(
    MinicloudDbContext db, IOptions<RegistryOptions> options, ImageBuildTargetService targets,
    ImagePublicationLifecycleService lifecycle, RegistryClient registry, ControlPlaneAuthorization authorization,
    IIdGenerator ids, TimeProvider clock, AuditEventService audit)
{
    private readonly RegistryOptions _options = options.Value;
    public bool Enabled => _options.Enabled;
    public string RepositoryScope(string organizationId, string appId) => new Uri(_options.PublicUrl).Authority + "/" + ImagePublicationValidation.Repository(organizationId, appId, "scope")[..^5];
    public const long LockKey = 714615415;

    public void RequireEnabled()
    {
        if (!_options.Enabled || !Uri.TryCreate(_options.PublicUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(_options.AllowInsecureLoopback && uri.Scheme == "http" && uri.IsLoopback)))
        {
            throw Error(ImagePublicationErrors.Unavailable, "Local image publication is not enabled.");
        }
    }

    public async Task LockAsync(CancellationToken ct)
    {
        if (db.Database.IsNpgsql()) { await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct); }
    }

    public async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(ct);
            var result = await action();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    public async Task CheckMaintenanceAsync(CancellationToken ct)
    {
        if (await db.RegistryMaintenanceStates.AnyAsync(x => x.Id == "registry" && x.WritesBlocked, ct))
        {
            throw Error(ImagePublicationErrors.Maintenance, "Registry maintenance is in progress; retry later.");
        }
    }

    public async Task<ImagePublication> CreateAsync(MinicloudApp app, CreateImagePublicationRequest request, ActorIdentity actor, CancellationToken ct)
    {
        RequireEnabled();
        if (!ImagePublicationValidation.Validate(request).IsValid) { throw Error(ImagePublicationErrors.Unsupported, "Invalid image publication request."); }
        return await LockedAsync(async () =>
        {
            await CheckMaintenanceAsync(ct);
            var key = Guid.Parse(request.IdempotencyKey).ToString("D");
            var existing = await db.ImagePublications.SingleOrDefaultAsync(x => x.AppId == app.Id && x.IdempotencyKey == key, ct);
            if (existing is not null)
            {
                if (existing.ServiceName != request.ServiceName || existing.InputSha256 != request.InputSha256 || existing.TargetVersion != request.Target.Version || existing.Platform != request.Target.Platform)
                { throw Error(ImagePublicationErrors.Unsupported, "Idempotency key was already used for different image inputs."); }
                return existing;
            }
            if (await db.ImagePublications.CountAsync(x => x.AppId == app.Id && x.RetiredAt == null, ct) >= 128)
            { throw Error(ImagePublicationErrors.QuotaExceeded, "App image publication limit reached."); }
            var currentApp = await db.Apps.AsNoTracking().SingleAsync(x => x.Id == app.Id && x.OrganizationId == app.OrganizationId, ct);
            var target = await targets.ResolveAsync(currentApp, ct);
            var publication = lifecycle.Create(currentApp, request, target);
            db.ImagePublications.Add(publication);
            await audit.AppendAsync(app.OrganizationId, app.Id, actor.Describe(), "image_publication.created", publication.Id, ct);
            return publication;
        }, ct);
    }

    public async Task<ImagePublicationCredentialsResponse> CredentialsAsync(ImagePublication publication, ActorIdentity actor, CancellationToken ct)
    {
        RequireEnabled();
        return await LockedAsync(async () =>
        {
            await CheckMaintenanceAsync(ct);
            await db.Entry(publication).ReloadAsync(ct);
            await RequireActiveAppAsync(publication.AppId, ct);
            lifecycle.BeginUpload(publication);
            var now = clock.GetUtcNow();
            var open = await db.RegistrySessions.Where(x => x.Kind == RegistrySessionKind.Push && x.ReclaimedAt == null).ToListAsync(ct);
            if (open.Count(x => x.RevokedAt == null && x.ExpiresAt > now) >= 8 || open.Count(x => x.AppId == publication.AppId && x.RevokedAt == null && x.ExpiresAt > now) >= 2)
            { throw Error(ImagePublicationErrors.QuotaExceeded, "Too many active image uploads; retry later."); }
            CheckStorage();
            var secret = RegistryTokens.NewSecret();
            var session = new RegistrySession
            {
                Id = ids.NewId("rs"),
                AppId = publication.AppId,
                OrganizationId = publication.OrganizationId,
                ImagePublicationId = publication.Id,
                SubjectId = actor.ApiKeyId ?? actor.UserId ?? throw new UnauthorizedAccessException(),
                SubjectKind = actor.Kind,
                Kind = RegistrySessionKind.Push,
                CredentialHash = RegistryTokens.Hash(secret),
                CreatedAt = now,
                ExpiresAt = now.AddHours(2)
            };
            db.RegistrySessions.Add(session);
            await db.SaveChangesAsync(ct);
            return new ImagePublicationCredentialsResponse(new Uri(_options.PublicUrl).Authority, publication.Repository, publication.CandidateTag, session.Id, secret, session.ExpiresAt);
        }, ct);
    }

    public void CheckStorage()
    {
        if (!Directory.Exists(_options.StoragePath)) { throw Error(ImagePublicationErrors.RegistryUnavailable, "Registry storage volume is not mounted."); }
        var drive = new DriveInfo(_options.StoragePath);
        var floor = Math.Max(_options.MinFreeBytes, (long)(drive.TotalSize * 0.15));
        if (drive.AvailableFreeSpace < floor)
        { throw Error(ImagePublicationErrors.QuotaExceeded, "Registry shared-volume reserve is exhausted."); }
    }

    public async Task<ImagePublication> CompleteAsync(ImagePublication publication, string digest, ActorIdentity actor, CancellationToken ct)
    {
        RequireEnabled();
        return await LockedAsync(async () =>
        {
            await CheckMaintenanceAsync(ct);
            await RequireActiveAppAsync(publication.AppId, ct);
            var verified = await registry.VerifyAsync(publication.Repository, digest, publication.Platform, ct);
            await db.Entry(publication).ReloadAsync(ct);
            if (publication.Status != ImagePublicationStatus.Ready)
            {
                var app = await db.Apps.AsNoTracking().SingleAsync(x => x.Id == publication.AppId, ct);
                if (app.DeletionStatus is not null || (await targets.ResolveAsync(app, ct)) != new ImageBuildTarget(publication.TargetVersion, publication.Platform))
                { throw Error(ImagePublicationErrors.PlatformChanged, "Build target changed before image publication."); }
            }
            lifecycle.Complete(publication, verified.Manifest, verified.Config, digest);
            foreach (var session in await db.RegistrySessions.Where(x => x.ImagePublicationId == publication.Id && x.Kind == RegistrySessionKind.Push && x.RevokedAt == null).ToListAsync(ct))
            { session.RevokedAt = clock.GetUtcNow(); session.Revision++; }
            await audit.AppendAsync(publication.OrganizationId, publication.AppId, actor.Describe(), "image_publication.ready", publication.Id + " " + digest, ct);
            return publication;
        }, ct);
    }

    public async Task<RegistrySession?> ActiveSessionAsync(string id, CancellationToken ct)
    {
        if (!_options.Enabled) { return null; }
        var now = clock.GetUtcNow();
        var session = await db.RegistrySessions.AsNoTracking().Include(x => x.ImagePublication).Include(x => x.App)
            .SingleOrDefaultAsync(x => x.Id == id && x.RevokedAt == null, ct);
        if (session is null || session.ExpiresAt <= now || session.App.DeletionStatus is not null || session.ImagePublication is null || session.ImagePublication.RetiredAt is not null) { return null; }
        if (session.Kind == RegistrySessionKind.Pull)
        {
            return await db.Deployments.AnyAsync(x => x.Id == session.DeploymentId && x.AppId == session.AppId && x.OrganizationId == session.OrganizationId &&
                x.OwnershipToken == session.OwnershipToken && x.OwnershipToken != null && x.WorkerId == session.SubjectId &&
                db.ServerInstances.Any(server => server.Id == session.ServerId && server.AppId == session.AppId && server.OrganizationId == session.OrganizationId) &&
                (x.Status == DeploymentStatus.Provisioning || x.Status == DeploymentStatus.Deploying || x.Status == DeploymentStatus.Verifying), ct) ? session : null;
        }
        if (session.ImagePublication.Status is not (ImagePublicationStatus.Pending or ImagePublicationStatus.Uploading) || session.ImagePublication.ExpiresAt <= now) { return null; }
        if (session.SubjectKind == "api_key")
        {
            var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session.SubjectId && x.OrganizationId == session.OrganizationId && x.RevokedAt == null, ct);
            if (key is null || key.ExpiresAt <= now) { return null; }
            var actor = new ActorIdentity("api_key", null, key.Id, key.OrganizationId, JsonSerializer.Deserialize<HashSet<string>>(key.ScopesJson) ?? []);
            return await authorization.CanAccessAppAsync(actor, session.AppId, OrganizationAction.CreateDeployments, ct) ? session : null;
        }
        var user = new ActorIdentity("firebase", session.SubjectId, null, null, new HashSet<string>());
        return await authorization.CanAccessAppAsync(user, session.AppId, OrganizationAction.CreateDeployments, ct) ? session : null;
    }

    public async Task ChargeAsync(string id, int bytes, CancellationToken ct)
    {
        await CheckMaintenanceAsync(ct);
        var drive = new DriveInfo(_options.StoragePath);
        if (drive.AvailableFreeSpace - bytes < Math.Max(_options.MinFreeBytes, (long)(drive.TotalSize * 0.15)))
        { throw Error(ImagePublicationErrors.QuotaExceeded, "Registry shared-volume reserve is exhausted."); }
        var now = clock.GetUtcNow();
        var changed = await db.RegistrySessions.Where(x => x.Id == id && x.Kind == RegistrySessionKind.Push && x.RevokedAt == null && x.ExpiresAt > now && x.ChargedBytes <= long.MaxValue - bytes)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ChargedBytes, x => x.ChargedBytes + bytes).SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        if (changed != 1) { throw Error(ImagePublicationErrors.QuotaExceeded, "Upload session is unavailable or byte accounting overflowed."); }
    }

    public async Task<ImagePublicationCredentialsResponse> PullCredentialsAsync(Deployment deployment, ImagePublication publication, string serverId, CancellationToken ct)
    {
        RequireEnabled();
        var secret = RegistryTokens.NewSecret();
        var session = new RegistrySession
        {
            Id = ids.NewId("rs"),
            AppId = deployment.AppId,
            OrganizationId = deployment.OrganizationId,
            ImagePublicationId = publication.Id,
            DeploymentId = deployment.Id,
            OwnershipToken = deployment.OwnershipToken,
            ServerId = serverId,
            SubjectId = deployment.WorkerId ?? "worker",
            SubjectKind = "worker",
            Kind = RegistrySessionKind.Pull,
            CredentialHash = RegistryTokens.Hash(secret),
            CreatedAt = clock.GetUtcNow(),
            ExpiresAt = clock.GetUtcNow().AddMinutes(30)
        };
        if (publication.AppId != deployment.AppId || publication.OrganizationId != deployment.OrganizationId || publication.Status != ImagePublicationStatus.Ready || publication.RetiredAt != null || deployment.OwnershipToken is null ||
            !await db.ServerInstances.AnyAsync(x => x.Id == serverId && x.AppId == deployment.AppId && x.OrganizationId == deployment.OrganizationId, ct))
        { throw new UnauthorizedAccessException("Image pull scope is invalid."); }
        db.RegistrySessions.Add(session);
        await db.SaveChangesAsync(ct);
        return new ImagePublicationCredentialsResponse(new Uri(_options.PublicUrl).Authority, publication.Repository, publication.CandidateTag, session.Id, secret, session.ExpiresAt);
    }

    private async Task RequireActiveAppAsync(string appId, CancellationToken ct)
    {
        if (!await db.Apps.AsNoTracking().AnyAsync(x => x.Id == appId && x.DeletionStatus == null, ct))
        { throw Error(ImagePublicationErrors.Incomplete, "Application is unavailable or being deleted."); }
    }

    public async Task FinalizeDeploymentAsync(MinicloudApp app, Deployment deployment, CancellationToken ct, MinicloudDbContext? bindingDb = null)
    {
        RequireEnabled();
        await CheckMaintenanceAsync(ct);
        await RequireActiveAppAsync(app.Id, ct);
        var store = bindingDb ?? db;
        var target = await targets.ResolveAsync(app, ct);
        var requested = deployment.RequestedServiceNamesJson is null ? null : JsonSerializer.Deserialize<HashSet<string>>(deployment.RequestedServiceNamesJson);
        foreach (var service in deployment.Services.Where(x => x.ImagePublicationId != null))
        {
            var publication = await store.ImagePublications.SingleOrDefaultAsync(x => x.Id == service.ImagePublicationId, ct);
            var previouslyBound = await store.DeploymentServices.AnyAsync(x => x.ImagePublicationId == service.ImagePublicationId, ct);
            var bindingTarget = publication is not null && requested is not null && !requested.Contains(service.Name) ? target with { Version = publication.TargetVersion } : target;
            if (publication is null || !lifecycle.ValidateBinding(publication, app, service.Name, bindingTarget, previouslyBound).IsValid)
            { throw Error(ImagePublicationErrors.Incomplete, "Image publication is unavailable or does not match the app/service/target."); }
            service.ImagePublication = publication;
            service.ImageDigest = publication.ManifestDigest;
            service.ImagePlatform = publication.Platform;
            service.Image = new Uri(_options.PublicUrl).Authority + "/" + publication.Repository + "@" + publication.ManifestDigest;
        }
    }

    public static ImagePublicationResponse Response(ImagePublication x) => new(x.Id, x.AppId, x.ServiceName, x.Status.ToString().ToLowerInvariant(), x.Repository, x.CandidateTag,
        new ImageBuildTarget(x.TargetVersion, x.Platform), x.InputSha256, x.ManifestDigest, x.SizeBytes, x.CreatedAt, x.ExpiresAt, x.FailureCode);

    private static RegistryImageValidationException Error(string code, string message) => new(code, message);
}
