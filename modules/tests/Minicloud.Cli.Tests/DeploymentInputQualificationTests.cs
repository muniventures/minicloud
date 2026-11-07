// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Text.Json;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;

namespace Minicloud.Tests;

public sealed class DeploymentInputQualificationTests
{
    [Fact]
    public void Customer_inputs_share_frozen_bytes_and_export_replayable_cli_context()
    {
        var workspace = Environment.GetEnvironmentVariable("MINICLOUD_TEST_CUSTOMER_WORKSPACE");
        var output = Environment.GetEnvironmentVariable("MINICLOUD_TEST_INPUT_OUTPUT");
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(output)) { return; }
        var loaded = MinicloudConfigLoader.Load(Path.Combine(workspace, "minicloud.yml"));
        Assert.True(loaded.IsValid, string.Join(';', loaded.Diagnostics));
        Directory.CreateDirectory(output);
        using var inputs = new DeploymentInputSession();
        var measurements = new List<object>();
        foreach (var (name, raw) in loaded.Config!.Services)
        {
            if (raw.SourcePath is null) { continue; }
            var service = raw with
            {
                SourcePath = Path.GetFullPath(Path.Combine(workspace, raw.SourcePath)),
                Dockerfile = Path.GetFullPath(Path.Combine(workspace, raw.Dockerfile!))
            };
            var timer = Stopwatch.StartNew();
            var snapshot = inputs.Get(name, service);
            var hash = ServiceChecksumCalculator.Compute(name, service, snapshot);
            measurements.Add(new { name, files = snapshot.Files.Count, uniqueFiles = inputs.SourceFilesRead, capturedBytes = inputs.CapturedBytes, snapshotBytes = inputs.SnapshotBytes, milliseconds = timer.Elapsed.TotalMilliseconds, hash });
        }
        var asset = loaded.Config.Assets["cli"];
        var cli = new MinicloudServiceConfig(Path.GetFullPath(Path.Combine(workspace, asset.SourcePath)),
            Path.GetFullPath(Path.Combine(workspace, asset.Dockerfile)), null, 1, false, "/", "/");
        var frozen = inputs.Get("asset-cli", cli);
        var bundle = DeploymentArtifactBundler.Create(loaded.Config.AppId!, "cli", cli, null, output, snapshot: frozen);
        File.WriteAllText(Path.Combine(output, "inputs.json"), JsonSerializer.Serialize(new
        {
            measurements,
            uniqueFiles = inputs.SourceFilesRead,
            capturedBytes = inputs.CapturedBytes,
            snapshotBytes = inputs.SnapshotBytes,
            cliArtifact = bundle.ZipPath,
            cliArtifactSha256 = bundle.Sha256
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(frozen.Files.Count, frozen.Files.Select(f => f.RelativePath).Distinct(StringComparer.Ordinal).Count());
    }
}
