// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public static class ServiceBuildModes
{
    public const string Source = "source";
    public const string Local = "local";

    public static bool IsSupported(string? value) => value is Source or Local;
}

public sealed record ImageBuildTarget(string Version, string Platform);

public sealed record CreateImagePublicationRequest(
    string ServiceName,
    string InputSha256,
    ImageBuildTarget Target,
    string IdempotencyKey);

public sealed record CompleteImagePublicationRequest(string ManifestDigest);

public sealed record ImagePublicationResponse(
    string Id,
    string AppId,
    string ServiceName,
    string Status,
    string Repository,
    string CandidateTag,
    ImageBuildTarget Target,
    string InputSha256,
    string? ManifestDigest,
    long? SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? FailureCode);

public sealed record ImagePublicationCredentialsResponse(
    string Registry,
    string Repository,
    string CandidateTag,
    string Username,
    string Password,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() => "ImagePublicationCredentialsResponse { credentials redacted }";
}

public static class ImagePublicationErrors
{
    public const string Unavailable = "local_build_unavailable";
    public const string PlatformUnknown = "image_platform_unknown";
    public const string PlatformChanged = "image_platform_changed";
    public const string PlatformMismatch = "image_platform_mismatch";
    public const string Incomplete = "image_incomplete";
    public const string Unsupported = "image_unsupported";
    public const string QuotaExceeded = "image_quota_exceeded";
    public const string Maintenance = "registry_maintenance";
    public const string RegistryUnavailable = "registry_unavailable";
    public const string Expired = "image_publication_expired";
}
