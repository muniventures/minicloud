using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Services;

public sealed class AssetReleaseService(MinicloudDbContext db, DeploymentLifecycleService lifecycle, IIdGenerator ids, TimeProvider timeProvider,
    ProviderCatalogService? catalog = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<(AssetRelease Release, Deployment Deployment)> PublishAsync(MinicloudApp app, PublishAssetRequest request, CancellationToken ct)
    {
        if (!AssetValidation.SafeName(request.Name) || !AssetValidation.SafeMountPath(request.OutputPath) || !Regex.IsMatch(request.InputSha256, "\\A[a-f0-9]{64}\\z"))
        { throw new ArgumentException("Invalid asset name, output path or input fingerprint."); }
        var artifact = await db.DeploymentArtifacts.SingleOrDefaultAsync(x => x.Id == request.ArtifactId && x.AppId == app.Id &&
            x.OrganizationId == app.OrganizationId && x.ServiceName == request.Name, ct);
        if (artifact is null || artifact.Status != DeploymentArtifactStatus.Ready || artifact.ExpiresAt <= timeProvider.GetUtcNow())
        { throw new ArgumentException("Asset source artifact is not ready for this app and asset."); }
        var release = new AssetRelease
        {
            Id = ids.NewId("asset"),
            AppId = app.Id,
            OrganizationId = app.OrganizationId,
            Name = request.Name,
            ArtifactId = artifact.Id,
            InputSha256 = request.InputSha256,
            OutputPath = request.OutputPath,
            CreatedAt = timeProvider.GetUtcNow()
        };
        var deployment = lifecycle.CreateQueued(app, new CreateDeploymentRequest(app.Id, "none", null, []));
        if (catalog is not null) await catalog.ReserveAsync(app, deployment, ct);
        deployment.AssetRelease = release;
        deployment.AssetReleaseId = release.Id;
        db.AssetReleases.Add(release);
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct);
        return (release, deployment);
    }

    public async Task ValidateMountsAsync(string appId, string organizationId, IEnumerable<DeploymentServiceRequest> services, CancellationToken ct)
    {
        foreach (var service in services)
        {
            if (service.AssetMounts is { Count: > 10 }) { throw new ArgumentException("At most ten asset mounts are supported per service."); }
            var paths = new List<string>();
            foreach (var (name, mount) in service.AssetMounts ?? new Dictionary<string, AssetMountRequest>())
            {
                if (mount is null || !AssetValidation.SafeName(name) || mount.ProtocolVersion != 1 || !AssetValidation.SafeMountPath(mount.MountPath) ||
                    (mount.MountPath == "/app" || new[] { "/etc", "/proc", "/sys", "/dev", "/run", "/opt/minicloud" }.Any(x => mount.MountPath == x || mount.MountPath.StartsWith(x + '/', StringComparison.Ordinal))) ||
                    paths.Any(x => x == mount.MountPath || x.StartsWith(mount.MountPath + '/') || mount.MountPath.StartsWith(x + '/')))
                { throw new ArgumentException("Invalid or overlapping asset mount."); }
                paths.Add(mount.MountPath);
                var release = await ResolveAsync(db, appId, organizationId, name, mount.ReleaseId, ct);
                if (release?.ReadyAt is null || release.ManifestJson is null || release.ManifestSha256 is null ||
                    !await db.Deployments.AnyAsync(d => d.AssetReleaseId == release.Id && d.Status == DeploymentStatus.Succeeded, ct))
                { throw new ArgumentException("No ready published asset release exists for this app and asset."); }
                using var manifest = JsonDocument.Parse(release.ManifestJson);
                if (!manifest.RootElement.TryGetProperty("protocolVersion", out var protocol) || protocol.GetInt32() != mount.ProtocolVersion)
                { throw new ArgumentException("Published asset protocol is incompatible."); }
            }
        }
    }

    public static async Task<AssetRelease?> ResolveAsync(MinicloudDbContext db, string appId, string organizationId, string name, string? releaseId, CancellationToken ct)
    {
        var releases = db.AssetReleases.Where(r => r.AppId == appId && r.OrganizationId == organizationId && r.Name == name);
        if (releaseId is not null) { return await releases.SingleOrDefaultAsync(r => r.Id == releaseId, ct); }
        var ready = await releases.Where(r => r.ReadyAt != null && r.ManifestSha256 != null &&
            db.Deployments.Any(d => d.AssetReleaseId == r.Id && d.Status == DeploymentStatus.Succeeded))
            .ToListAsync(ct);
        return ready.OrderByDescending(r => r.ReadyAt).ThenByDescending(r => r.Id).FirstOrDefault();
    }

    public static void ValidatePublishedManifest(JsonElement manifest)
    {
        var runtimes = new HashSet<string>(StringComparer.Ordinal) { "osx-arm64", "osx-x64", "linux-arm64", "linux-x64", "win-x64" };
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (manifest.GetProperty("protocolVersion").GetInt32() != 1 || string.IsNullOrWhiteSpace(manifest.GetProperty("version").GetString()) ||
            manifest.GetProperty("packages").GetArrayLength() != 5) { throw new ArgumentException("Unsupported published asset manifest."); }
        long bytes = 0;
        foreach (var package in manifest.GetProperty("packages").EnumerateArray())
        {
            var name = package.GetProperty("fileName").GetString()!;
            var size = package.GetProperty("sizeBytes").GetInt64();
            if (!runtimes.Remove(package.GetProperty("runtimeIdentifier").GetString()!) || !names.Add(name) ||
                !Regex.IsMatch(name, "\\A[A-Za-z0-9_.+-]{1,160}\\z") || size <= 0 || size > 2L * 1024 * 1024 * 1024 ||
                !Regex.IsMatch(package.GetProperty("sha256").GetString()!, "\\A[a-f0-9]{64}\\z"))
            { throw new ArgumentException("Invalid published asset package metadata."); }
            bytes += size;
        }
        if (bytes > 2L * 1024 * 1024 * 1024) { throw new ArgumentException("Published asset exceeds storage bounds."); }
    }

    public static IReadOnlyDictionary<string, AssetMountRequest> Mounts(DeploymentService service) =>
        service.AssetMountsJson is null ? new Dictionary<string, AssetMountRequest>() :
        JsonSerializer.Deserialize<Dictionary<string, AssetMountRequest>>(service.AssetMountsJson, JsonOptions)!;

    public static AssetReleaseResponse Response(AssetRelease release, Deployment? deployment) => new(release.Id, release.AppId, release.Name,
        release.InputSha256, deployment?.Id, deployment?.Status.ToString().ToLowerInvariant() ?? "unknown", release.ManifestSha256,
        release.ManifestJson is null ? null : JsonSerializer.Deserialize<JsonElement>(release.ManifestJson), release.CreatedAt,
        DeploymentNumber: string.IsNullOrEmpty(deployment?.Number) ? null : deployment.Number);
}
