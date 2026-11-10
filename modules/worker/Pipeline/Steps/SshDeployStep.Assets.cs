using Minicloud.Common.Validation;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Pipeline.Steps;

public sealed partial class SshDeployStep
{
    private SshDeploymentArtifactUpload AssetHelperUpload() => RuntimeHelperUpload("asset-release.py", "asset_release_invalid");

    private SshDeploymentArtifactUpload RuntimeHelperUpload(string name, string failureCode = "runtime_helper_missing")
    {
        var relative = Path.Combine("scripts", "github-actions", "minicloud-deploy", name);
        var path = new[] { Path.Combine(AppContext.BaseDirectory, relative), Path.Combine(_environment.ContentRootPath, relative) }.FirstOrDefault(File.Exists)
            ?? throw new PipelineStepException(failureCode, "Runtime maintenance or asset helper is missing.");
        return new(Path.GetFileNameWithoutExtension(name), name, _ => Task.FromResult<Stream>(File.OpenRead(path)), new FileInfo(path).Length);
    }

    private sealed record RuntimeAssetMount(string name, string? release_id, string mount_path, int protocol_version, string manifest_sha256);

    private async Task<Dictionary<string, RuntimeAssetMount[]>> ResolveAssetMountsAsync(PipelineStepContext context, IEnumerable<DeploymentService> services, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var result = new Dictionary<string, RuntimeAssetMount[]>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            var mounts = new List<RuntimeAssetMount>();
            foreach (var (name, mount) in AssetReleaseService.Mounts(service))
            {
                var release = await AssetReleaseService.ResolveAsync(db, context.AppId, context.OrganizationId, name, mount.ReleaseId, ct);
                if (release?.ReadyAt is null || release.ManifestSha256 is null || mount.ProtocolVersion != 1 ||
                    !await db.Deployments.AnyAsync(d => d.AssetReleaseId == release.Id && d.Status == DeploymentStatus.Succeeded, ct) ||
                    !AssetValidation.SafeName(name) || !AssetValidation.SafeMountPath(mount.MountPath))
                { throw new PipelineStepException("asset_release_invalid", "Asset binding is not ready for this app."); }
                mounts.Add(new(name, mount.ReleaseId, mount.MountPath, mount.ProtocolVersion, release.ManifestSha256));
            }
            result.Add(service.Name, mounts.ToArray());
        }
        return result;
    }

    private async Task PublishAssetAsync(PipelineStepContext context, Deployment deployment, string key, string script, CancellationToken ct)
    {
        var release = deployment.AssetRelease ?? throw new PipelineStepException("asset_release_invalid", "Asset release is missing.");
        if (release.AppId != context.AppId || release.OrganizationId != context.OrganizationId || release.Artifact.AppId != context.AppId ||
            release.Artifact.OrganizationId != context.OrganizationId || release.Artifact.Status != DeploymentArtifactStatus.Ready)
        { throw new PipelineStepException("asset_release_invalid", "Asset release ownership or source is invalid."); }
        context.RemoteDirectory = RemoteDirectoryFor(context.DeploymentId);
        var image = $"minicloud/{context.AppSlug}-asset-{release.Name}:{context.DeploymentId}";
        await using var retentionScope = _scopeFactory.CreateAsyncScope();
        var retentionDb = retentionScope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var bindings = await retentionDb.DeploymentServices.AsNoTracking().Where(s => s.Deployment.AppId == context.AppId && s.AssetMountsJson != null).ToListAsync(ct);
        // Persisted releases remain recoverable and may be pinned by a request admitted during this build.
        // Retain them conservatively; only unrecorded orphan outputs are eligible for local collection.
        var recoverable = await retentionDb.AssetReleases.Where(r => r.AppId == context.AppId && r.OrganizationId == context.OrganizationId).Select(r => r.Id).ToArrayAsync(ct);
        var retained = bindings.SelectMany(s => AssetReleaseService.Mounts(s).Values).Select(m => m.ReleaseId).Concat(recoverable).Distinct(StringComparer.Ordinal).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            app_name = context.AppSlug,
            customer = context.OrganizationSlug,
            app_env = "production",
            deployment_type = "worker",
            deployment_id = context.DeploymentId,
            github_sha = context.DeploymentId,
            database = "none",
            services = Array.Empty<object>(),
            ownership_token = context.OwnershipToken,
            worker_lease_file = $"{context.RemoteDirectory}/worker.lease",
            build_artifacts = new[] { new { service_name = release.Name, artifact_archive = $"{context.RemoteDirectory}/{release.Name}.zip", image_tag = image } },
            asset_publication = new { name = release.Name, release_id = release.Id, input_sha256 = release.InputSha256, output_path = release.OutputPath, image, retained_release_ids = retained },
            build_cache = new { max_bytes = _options.BuildCacheMaxBytes, max_age_hours = _options.BuildCacheMaxAgeHours, min_free_bytes = _options.BuildCacheMinFreeBytes, min_free_percent = _options.BuildCacheMinFreePercent },
            max_parallel_builds = 1,
            max_parallel_network_operations = 1
        }, JsonOptions);
        var uploads = new[] { RuntimeHelperUpload("runtime-maintenance.py"), AssetHelperUpload(), new SshDeploymentArtifactUpload(release.Name, release.Name + ".zip", token => _artifactRetriever.OpenArtifactStreamAsync(release.ArtifactId, token), release.Artifact.SizeBytes ?? 0) };
        var logs = new DeploymentLogWriter(_scopeFactory, _timeProvider, context.DeploymentId, _options.LogBatchSize);
        SshDeploymentResult result;
        try
        {
            result = await _sshClient.ExecuteDeploymentAsync(new(context.ServerIp, string.IsNullOrWhiteSpace(context.SshUsername) ? _sshOptions.Username : context.SshUsername,
                key, context.RemoteDirectory, payload, script, uploads, _options.SshCommandTimeout),
                (source, line) => logs.AppendAsync(ClassifySource(line), line, ct), ct);
            await logs.FlushAsync(ct);
        }
        catch
        {
            await FlushFailureLogsAsync(logs, context.DeploymentId);
            throw;
        }
        if (result.ExitCode != 0 || !result.CompletionMarkerFound)
        { throw new PipelineStepException("asset_release_invalid", "Customer asset build/publication failed; consumer binding unchanged."); }
        const string marker = "__MINICLOUD_ASSET_MANIFEST__=";
        var output = result.OutputLines.LastOrDefault(x => x.StartsWith(marker, StringComparison.Ordinal));
        if (output is null || output.Length > 100000) { throw new PipelineStepException("asset_release_invalid", "Asset publication metadata missing or oversized."); }
        using var published = JsonDocument.Parse(Convert.FromBase64String(output[marker.Length..]));
        var manifestBytes = Convert.FromBase64String(published.RootElement.GetProperty("manifestBase64").GetString()!);
        if (manifestBytes.Length > 16384) { throw new PipelineStepException("asset_release_invalid", "Asset manifest is oversized."); }
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifestBytes)).ToLowerInvariant();
        using var original = JsonDocument.Parse(manifestBytes);
        var manifest = original.RootElement;
        if (digest != published.RootElement.GetProperty("manifestSha256").GetString() ||
            !JsonElement.DeepEquals(manifest, published.RootElement.GetProperty("manifest")))
        { throw new PipelineStepException("asset_release_invalid", "Published manifest checksum mismatch."); }
        AssetReleaseService.ValidatePublishedManifest(manifest);
        await PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, token) =>
        {
            if (!await db.Deployments.AnyAsync(d => d.Id == context.DeploymentId && d.OwnershipToken == context.OwnershipToken &&
                d.AssetReleaseId == release.Id && d.AppId == context.AppId && d.OrganizationId == context.OrganizationId, token))
            { throw new PipelineStepException("asset_release_invalid", "Asset publication ownership lost."); }
            var stored = await db.AssetReleases.SingleAsync(r => r.Id == release.Id, token);
            stored.ManifestJson = Encoding.UTF8.GetString(manifestBytes);
            stored.ManifestSha256 = digest;
            stored.ReadyAt = _timeProvider.GetUtcNow();
            foreach (var removed in published.RootElement.GetProperty("removedReleaseIds").EnumerateArray())
            {
                var expired = await db.AssetReleases.SingleOrDefaultAsync(r => r.Id == removed.GetString() && r.AppId == context.AppId && r.OrganizationId == context.OrganizationId, token);
                if (expired is not null) { expired.ReadyAt = null; }
            }
        }, ct);
    }
}
