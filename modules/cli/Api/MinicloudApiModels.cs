using System.Text.Json.Serialization;

namespace Minicloud.Cli.Api;

public sealed record MeResponse(
    string UserId,
    string Email,
    string? DisplayName,
    IReadOnlyList<OrganizationSummary> Organizations);

public sealed record OrganizationSummary(
    string Id,
    string Name,
    string Slug,
    string Role);

public sealed record AppResponse(
    string Id,
    string OrganizationId,
    string Name,
    string Slug,
    string? ParentAppId,
    string? BranchName,
    string Plan,
    string Database,
    IReadOnlyList<AppBranchResponse> Branches,
    LatestDeploymentResponse? LatestDeployment);

public sealed record AppBranchResponse(
    string Id,
    string Name,
    string Slug,
    string BranchName,
    string Plan,
    string Database,
    string? WebsiteUrl,
    DateTimeOffset CreatedAt);

public sealed record EnsureAppBranchRequest(string BranchName);

public sealed record DomainBindingResponse(
    string Id,
    string AppId,
    string ServiceName,
    string Hostname,
    string Kind,
    string PathPrefix,
    string DnsStatus,
    string ApplyStatus,
    string SslStatus,
    string Status,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset? LastAppliedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AppServiceInventoryResponse(
    string Name,
    string Image,
    int Port,
    bool Public,
    string Path,
    string HealthPath,
    RuntimeServiceStatusResponse? Runtime,
    IReadOnlyList<DomainBindingResponse> Domains);

public sealed record RuntimeServiceStatusResponse(
    string Name,
    string? Container,
    string State,
    string? Health,
    string? Image,
    int? RestartCount);

public sealed record CreateDomainBindingRequest(
    string ServiceName,
    string? Label);

public sealed record UpdateDomainBindingRequest(bool? Disabled);

public sealed record AppServiceSecretResponse(
    string Id,
    string AppId,
    string ServiceName,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SetAppServiceSecretRequest(
    string? ServiceName,
    string Name,
    string Value);

public sealed record CreateAppRequest(
    string OrganizationId,
    string Name,
    string Slug,
    string Plan,
    string Database);

public sealed record LatestDeploymentResponse(
    string Id,
    string Status,
    string? WebsiteUrl,
    DateTimeOffset? CompletedAt);

public sealed record CreateDeploymentRequest(
    string AppId,
    string Database,
    string? CommitSha,
    IReadOnlyList<DeploymentServiceRequest> Services,
    string? PostgresPassword = null);

public sealed record DeploymentServiceRequest(
    string Name,
    string? Image,
    int Port,
    bool Public,
    string Path,
    string HealthPath,
    IReadOnlyDictionary<string, string>? Env = null,
    IReadOnlyDictionary<string, string>? SecretEnv = null,
    string? ArtifactId = null);

public sealed record CreateDeploymentArtifactRequest(
    string AppId,
    string ServiceName,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DeploymentArtifactManifest Manifest);

public sealed record DeploymentArtifactCreateResponse(
    string Id,
    string AppId,
    string ServiceName,
    string Status,
    string UploadUrl,
    long SizeBytes,
    string Sha256);

public sealed record DeploymentArtifactResponse(
    string Id,
    string AppId,
    string ServiceName,
    string Status,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAt);

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
    DateTimeOffset CreatedAt);

public sealed record DeploymentCreateResponse(
    string Id,
    string AppId,
    string Status,
    string StatusUrl,
    string? WebsiteUrl,
    DateTimeOffset CreatedAt,
    string? PostgresPassword = null);

public sealed record DeploymentResponse(
    string Id,
    string AppId,
    string OrganizationId,
    string Database,
    string Status,
    string? WebsiteUrl,
    string ConsoleUrl,
    string? FailureCode,
    string? FailureMessage,
    IReadOnlyList<DeploymentServiceResponse> Services,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record DeploymentServiceResponse(
    string Name,
    string Image,
    int Port,
    bool Public,
    string Path,
    string HealthPath);

public sealed record DeploymentEventResponse(
    string Id,
    string Level,
    string Message,
    string? MetadataJson,
    DateTimeOffset CreatedAt);

public sealed record DeploymentLogResponse(
    string Id,
    string Source,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record RuntimeLogResponse(
    string Id,
    string ServerId,
    string Source,
    string? Service,
    string Stream,
    string Content,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt);

public sealed record ErrorResponse(ErrorBody Error);
public sealed record ErrorBody(string Code, string Message, IReadOnlyList<ErrorDetail> Details);
public sealed record ErrorDetail(string Field, string Message);

public sealed record CliLoginSessionCreateResponse(
    string SessionId,
    string LoginUrl,
    string Status,
    DateTimeOffset ExpiresAt);

public sealed record CliLoginSessionExchangeResponse(
    string Token,
    string Email,
    OrganizationSummary Organization,
    IReadOnlyList<string> Scopes);

public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}
