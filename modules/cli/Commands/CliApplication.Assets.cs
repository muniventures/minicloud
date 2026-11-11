// SPDX-License-Identifier: MPL-2.0

using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunAssetsAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2 || args[0] != "publish")
        { throw new CliCommandException(CliExitCodes.ValidationError, "Usage: minicloud assets publish <name> [--config path]"); }
        var loaded = MinicloudConfigLoader.Load(GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath());
        if (!loaded.IsValid || loaded.Config is null) { PrintDiagnostics(loaded.Diagnostics); return CliExitCodes.ValidationError; }
        var config = loaded.Config;
        if (!config.Assets.TryGetValue(args[1], out var asset))
        { throw new CliCommandException(CliExitCodes.ValidationError, "Asset is not declared in minicloud.yml."); }
        var app = await ResolveAppOptionAsync(args, ct);
        var plan = GetOption(args, "--plan") ?? config.Plan;
        if (args.Any(x => x == "--plan" || x.StartsWith("--plan=", StringComparison.Ordinal)) && string.IsNullOrWhiteSpace(GetOption(args, "--plan")))
            throw new CliCommandException(CliExitCodes.ValidationError, "--plan requires a plan name.");
        if (plan is not null || app.PlanId is not null) app = await _apiClient.SelectPlanAsync(app.Id, plan ?? app.Plan, ct);
        var source = new MinicloudServiceConfig(asset.SourcePath, asset.Dockerfile, null, 1, false, "/", "/",
            Env: new Dictionary<string, string> { ["outputPath"] = asset.OutputPath, ["protocolVersion"] = "1" },
            BuildMode: ServiceBuildModes.Source);
        using var inputs = new DeploymentInputSession();
        var snapshot = inputs.Get(args[1], source);
        var fingerprint = ServiceChecksumCalculator.Compute(args[1], source, snapshot);
        var directory = Directory.CreateTempSubdirectory("minicloud-asset-");
        try
        {
            var bundle = DeploymentArtifactBundler.Create(app.Id, args[1], source, config.CommitSha, directory.FullName, snapshot: snapshot);
            var artifact = await _apiClient.CreateDeploymentArtifactAsync(new(app.Id, args[1], Path.GetFileName(bundle.ZipPath), "application/zip", bundle.SizeBytes, bundle.Sha256, bundle.Manifest), ct);
            await _apiClient.UploadDeploymentArtifactContentAsync(artifact.Id, artifact.UploadUrl, bundle.ZipPath, bundle.Sha256, bundle.SizeBytes, ct);
            var release = await _apiClient.PublishAssetAsync(new(app.Id, args[1], artifact.Id, fingerprint, asset.OutputPath), ct);
            if (release.DeploymentNumber is null) { throw new CliCommandException(CliExitCodes.ValidationError, "Asset publication did not return a queued deployment."); }
            _console.WriteLine($"Publishing asset {release.Name}: {release.Id} (deployment {release.DeploymentNumber}).");
            await PollDeploymentAsync(app.OrganizationId, release.DeploymentNumber, release.Status, ct);
            release = await _apiClient.GetAssetAsync(release.Id, ct);
            if (release.Status != "succeeded") { throw new CliCommandException(CliExitCodes.ValidationError, $"Asset publication {release.Id} {release.Status}."); }
            _console.WriteLine($"Asset ready: releaseId={release.Id} input_sha256={release.InputSha256} manifest_sha256={release.ManifestSha256}");
            _console.WriteLine("Published assets are now current for unpinned mounts; services do not restart. Explicitly pinned mounts remain unchanged.");
            return CliExitCodes.Success;
        }
        finally { directory.Delete(true); }
    }
}
