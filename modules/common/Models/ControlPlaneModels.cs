using System.ComponentModel.DataAnnotations;

namespace Minicloud.Common.Models;

public sealed class User
{
    [Key]
    public string Id { get; set; } = default!;
    public string FirebaseUid { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<OrganizationMember> Memberships { get; set; } = [];
}

public sealed class Organization
{
    [Key]
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<OrganizationMember> Members { get; set; } = [];
    public List<MinicloudApp> Apps { get; set; } = [];
    public List<ApiKey> ApiKeys { get; set; } = [];
}

public sealed class OrganizationMember
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public User User { get; set; } = default!;
    public OrganizationRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MinicloudApp
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public string? ParentAppId { get; set; }
    public MinicloudApp? ParentApp { get; set; }
    public string? BranchName { get; set; }
    public string? EnvironmentName { get; set; }
    // Root app ID + case-folded name enforces uniqueness across root and child environments.
    public string? EnvironmentKey { get; set; }
    public string? DeletionStatus { get; set; }
    public string? DeletionError { get; set; }
    public AppPlan Plan { get; set; }
    public string? PlanId { get; set; }
    public ProviderPlan? SelectedPlan { get; set; }
    public DatabaseMode Database { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<Deployment> Deployments { get; set; } = [];
    public List<Domain> Domains { get; set; } = [];
    public List<AppSecret> Secrets { get; set; } = [];
    public List<AppServiceSecretServiceDefinition> ServiceSecretServices { get; set; } = [];
    public List<AppServiceSecret> ServiceSecrets { get; set; } = [];
    public List<DeploymentArtifact> DeploymentArtifacts { get; set; } = [];
    public List<MinicloudApp> Branches { get; set; } = [];
}

public sealed class AppSecret
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string EncryptedValue { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AppServiceSecret
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string StoreProvider { get; set; } = default!;
    public string StorePath { get; set; } = default!;
    public string? StoreVersion { get; set; }
    public string Status { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class AppServiceSecretServiceDefinition
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class ApiKey
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string KeyPrefix { get; set; } = default!;
    public string KeyHash { get; set; } = default!;
    public string ScopesJson { get; set; } = default!;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CliLoginSession
{
    [Key]
    public string Id { get; set; } = default!;
    public string? OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public string? ApprovedByUserId { get; set; }
    public User? ApprovedByUser { get; set; }
    public string Status { get; set; } = default!;
    public string ScopesJson { get; set; } = default!;
    public string? ApiKeyId { get; set; }
    public ApiKey? ApiKey { get; set; }
    public string? RawApiKeyToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? ExchangedAt { get; set; }
}

public sealed class Deployment
{
    public string? PlanId { get; set; }
    public long? PlanRevision { get; set; }
    public string? ProviderAccountId { get; set; }
    public string? ProviderType { get; set; }
    public string? ProviderRegion { get; set; }
    public string? ProviderSku { get; set; }
    public string? ProviderImage { get; set; }
    public string? ProviderBuildPlatform { get; set; }
    public bool ProviderCreationAttempted { get; set; }
    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public string? CancellationRequestedBy { get; set; }
    public string? RequestedServiceNamesJson { get; set; }
    public string? AssetReleaseId { get; set; }
    public AssetRelease? AssetRelease { get; set; }
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public DatabaseMode Database { get; set; }
    public DeploymentStatus Status { get; set; }
    public string? Domain { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? GithubRunId { get; set; }
    public string? GithubRunUrl { get; set; }
    public string? CommitSha { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public string? WorkerId { get; set; }
    public string? OwnershipToken { get; set; }
    public DateTimeOffset? OwnershipAcquiredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<DeploymentService> Services { get; set; } = [];
    public List<DeploymentEvent> Events { get; set; } = [];
    public List<DeploymentLog> Logs { get; set; } = [];
}

public sealed class DeploymentService
{
    public string? ImagePublicationId { get; set; }
    public ImagePublication? ImagePublication { get; set; }
    public string? ImageDigest { get; set; }
    public string? ImagePlatform { get; set; }
    public string? AssetMountsJson { get; set; }
    public string? StorageJson { get; set; }
    [Key]
    public string Id { get; set; } = default!;
    public string DeploymentId { get; set; } = default!;
    public Deployment Deployment { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Image { get; set; } = default!;
    public string? ArtifactId { get; set; }
    public DeploymentArtifact? Artifact { get; set; }
    public int Port { get; set; }
    public bool Public { get; set; }
    public string Path { get; set; } = default!;
    public string HealthPath { get; set; } = default!;
    public string? EnvJson { get; set; }
    public string? SecretEnvJson { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class DeploymentEvent
{
    [Key]
    public string Id { get; set; } = default!;
    public string DeploymentId { get; set; } = default!;
    public Deployment Deployment { get; set; } = default!;
    public EventLevel Level { get; set; }
    public string Message { get; set; } = default!;
    public string? MetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DeploymentLog
{
    [Key]
    public string Id { get; set; } = default!;
    public string DeploymentId { get; set; } = default!;
    public Deployment Deployment { get; set; } = default!;
    public string Source { get; set; } = default!;
    public string Content { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DeploymentArtifact
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public string FileName { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public long? ExpectedSizeBytes { get; set; }
    public string? ExpectedSha256 { get; set; }
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string ManifestJson { get; set; } = default!;
    public string StoragePath { get; set; } = default!;
    public DeploymentArtifactStatus Status { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class ServerInstance
{
    public string? ProviderAccountId { get; set; }
    public string? PlanId { get; set; }
    public string? Sku { get; set; }
    public string? Image { get; set; }
    public string? BuildPlatform { get; set; }
    public DateTimeOffset? BuildPlatformObservedAt { get; set; }
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string Provider { get; set; } = default!;
    public string? ProviderServerId { get; set; }
    public string? IpAddress { get; set; }
    public string Region { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string BootstrapStatus { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Domain
{
    public string? CustomDomainRegistrationId { get; set; }
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string AppId { get; set; } = default!;
    public MinicloudApp App { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public string Hostname { get; set; } = default!;
    public string Kind { get; set; } = default!;
    public string PathPrefix { get; set; } = "/";
    public string? VerificationToken { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? TargetIp { get; set; }
    public string DnsStatus { get; set; } = default!;
    public string ApplyStatus { get; set; } = default!;
    public string SslStatus { get; set; } = default!;
    public string Status { get; set; } = default!;
    public bool IsPrimary { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public DateTimeOffset? LastAppliedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum OrganizationRole
{
    Owner,
    Admin,
    Developer,
    Viewer
}

public enum AppPlan
{
    P0,
    P0H0,
    P0V0,
    P0V1,
    P0V2,
    P0D0,
    P0D1,
    P0D2
}

public enum DatabaseMode
{
    Sqlite,
    Postgres,
    None
}

public enum DeploymentStatus
{
    Queued,
    Dispatching,
    Provisioning,
    Deploying,
    Verifying,
    Succeeded,
    Failed,
    Canceled,
    WaitingForBuild
}

public enum DeploymentArtifactStatus
{
    Created,
    Uploading,
    Ready,
    Expired,
    Deleted,
    Failed
}

public enum EventLevel
{
    Info,
    Warning,
    Error
}
