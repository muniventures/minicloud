// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record PublishAssetRequest(string AppId, string Name, string ArtifactId, string InputSha256, string OutputPath);
public sealed record AssetMountRequest(string? ReleaseId, string MountPath, int ProtocolVersion = 1);
public sealed record AssetReleaseResponse(string Id, string AppId, string Name, string InputSha256, string? DeploymentId,
    string Status, string? ManifestSha256, object? Manifest, DateTimeOffset CreatedAt);
