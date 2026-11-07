// SPDX-License-Identifier: MPL-2.0

using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;

namespace Minicloud.Tests;

public sealed class AssetConfigTests
{
    [Theory]
    [InlineData("asset_immutable")]
    [InlineData(null)]
    public void AssetsAndPinsRoundTripWithoutRuntimePortsForAssets(string? releaseId)
    {
        var config = new MinicloudConfig("demo", "none", null, new Dictionary<string, MinicloudServiceConfig>
        {
            ["backend"] = new(null, null, "example:1", 8080, true, "/", "/health", AssetMounts: new Dictionary<string, AssetMountRequest>
            { ["cli"] = new(releaseId, "/app/cli-releases") })
        })
        { AppId = "app_demo", Assets = new Dictionary<string, MinicloudAssetConfig> { ["cli"] = new(".", "cli.Dockerfile", "/cli-releases") } };
        var parsed = MinicloudConfigLoader.Parse(MinicloudConfigWriter.Write(config).Split('\n'));
        Assert.True(parsed.IsValid, string.Join(';', parsed.Diagnostics));
        Assert.Equal(config.Assets["cli"], parsed.Config!.Assets["cli"]);
        Assert.Equal(config.Services["backend"].AssetMounts!["cli"], parsed.Config.Services["backend"].AssetMounts!["cli"]);
        var hash = ServiceChecksumCalculator.Compute("backend", config.Services["backend"]);
        var updated = config.Services["backend"] with { AssetMounts = new Dictionary<string, AssetMountRequest> { ["cli"] = new("asset_other", "/app/cli-releases") } };
        Assert.NotEqual(hash, ServiceChecksumCalculator.Compute("backend", updated));
    }
}
