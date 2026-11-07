// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Cli.Config;

public sealed record MinicloudConfig(
    string App,
    string? Database,
    string? CommitSha,
    IReadOnlyDictionary<string, MinicloudServiceConfig> Services)
{
    public string? AppId { get; init; }
    public int? BuildJobs { get; init; }
    public IReadOnlyDictionary<string, MinicloudAssetConfig> Assets { get; init; } = new Dictionary<string, MinicloudAssetConfig>();
}

public sealed record MinicloudServiceConfig(
    string? SourcePath,
    string? Dockerfile,
    string? Image,
    int? Port,
    bool? Public,
    string? Path,
    string? HealthPath,
    IReadOnlyDictionary<string, string>? Env = null,
    IReadOnlyDictionary<string, string>? SecretEnv = null,
    IReadOnlyDictionary<string, string>? BuildEnv = null,
    IReadOnlyDictionary<string, Minicloud.Common.Contracts.AssetMountRequest>? AssetMounts = null,
    string BuildMode = Minicloud.Common.Contracts.ServiceBuildModes.Local,
    string? BuildPlatform = null,
    IReadOnlyDictionary<string, Minicloud.Common.Contracts.StorageMountRequest>? Storage = null);

public sealed record ConfigDiagnostic(string Field, string Message);

public sealed record ConfigLoadResult(MinicloudConfig? Config, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0 && Config is not null;
}

public sealed record MinicloudAssetConfig(string SourcePath, string Dockerfile, string OutputPath);
