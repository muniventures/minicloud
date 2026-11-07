// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Cli.Commands;

public sealed record DeploymentArtifactManifest(
    int SchemaVersion,
    string AppId,
    string ServiceName,
    string SourcePath,
    string? DockerfilePath,
    string BuildContextPath,
    int Port,
    bool Public,
    string Path,
    string HealthPath,
    string? CommitSha,
    int FileCount,
    long SourceBytes,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string>? BuildEnv = null);
