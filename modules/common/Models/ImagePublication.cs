using System.ComponentModel.DataAnnotations;

namespace Minicloud.Common.Models;

public sealed class ImagePublication
{
    [Key] public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public string IdempotencyKey { get; set; } = default!;
    public string Repository { get; set; } = default!;
    public string CandidateTag { get; set; } = default!;
    public string TargetVersion { get; set; } = default!;
    public string Platform { get; set; } = default!;
    public string InputSha256 { get; set; } = default!;
    public ImagePublicationStatus Status { get; set; }
    public string? ManifestDigest { get; set; }
    public string? ManifestJson { get; set; }
    public long? SizeBytes { get; set; }
    public string? FailureCode { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

public enum ImagePublicationStatus
{
    Pending,
    Uploading,
    Ready,
    Failed,
    Canceled,
    Expired
}

public sealed class RegistrySession
{
    [Key] public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string? ImagePublicationId { get; set; }
    public ImagePublication? ImagePublication { get; set; }
    public string? DeploymentId { get; set; }
    public Deployment? Deployment { get; set; }
    public string? ServerId { get; set; }
    public string? OwnershipToken { get; set; }
    public string SubjectId { get; set; } = default!;
    public string SubjectKind { get; set; } = "api_key";
    public RegistrySessionKind Kind { get; set; }
    public string CredentialHash { get; set; } = default!;
    public long ReservedBytes { get; set; }
    public long ChargedBytes { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? ReclaimedAt { get; set; }
}

public enum RegistrySessionKind
{
    Push,
    Pull
}

public sealed class RegistryMaintenanceState
{
    [Key] public string Id { get; set; } = "registry";
    public bool WritesBlocked { get; set; }
    public string? OwnerToken { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RegistryUpload
{
    [Key] public string Id { get; set; } = default!;
    public string SessionId { get; set; } = default!;
    public RegistrySession Session { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RegistryGarbageCandidate
{
    [Key] public string Id { get; set; } = default!;
    public string Repository { get; set; } = default!;
    public string ManifestDigest { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}
