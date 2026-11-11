// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record PublishAssetRequest(string AppId, string Name, string ArtifactId, string InputSha256, string OutputPath, string? ApplicationId = null, string? EnvironmentId = null);
public sealed record AssetMountRequest(string? ReleaseId, string MountPath, int ProtocolVersion = 1);
public sealed record AssetReleaseResponse(string Id, string AppId, string Name, string InputSha256, string? DeploymentId,
    string Status, string? ManifestSha256, object? Manifest, DateTimeOffset CreatedAt, string? ApplicationId = null, string? EnvironmentId = null, string? DeploymentNumber = null);
