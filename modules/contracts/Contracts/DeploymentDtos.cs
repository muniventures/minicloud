// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record DeploymentCancellationResponse(string Id, bool CancellationRequested);

public sealed record CreateDeploymentRequest(
    string AppId,
    string Database,
    string? CommitSha,
    IReadOnlyList<DeploymentServiceRequest> Services,
    string? PostgresPassword = null,
    string? PreparationId = null,
    string? Plan = null);

public sealed record DeploymentServiceRequest(
    string Name,
    string? Image,
    int Port,
    bool Public,
    string Path,
    string HealthPath,
    IReadOnlyDictionary<string, string>? Env = null,
    IReadOnlyDictionary<string, string>? SecretEnv = null,
    string? ArtifactId = null,
    string? Sha256 = null,
    IReadOnlyDictionary<string, AssetMountRequest>? AssetMounts = null,
    string? ImagePublicationId = null,
    IReadOnlyDictionary<string, StorageMountRequest>? Storage = null);

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
    DateTimeOffset? CompletedAt,
    string? AssetReleaseId = null,
    DateTimeOffset? CancellationRequestedAt = null,
    DateTimeOffset? RequeuedAt = null);

public sealed record DeploymentServiceResponse(
    string Name,
    string Image,
    int Port,
    bool Public,
    string Path,
    string HealthPath,
    IReadOnlyList<string> Urls,
    string? Sha256 = null,
    IReadOnlyDictionary<string, AssetMountRequest>? AssetMounts = null,
    IReadOnlyDictionary<string, StorageMountRequest>? Storage = null);

public sealed record CreateDeploymentArtifactRequest(
    string AppId,
    string ServiceName,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    object Manifest);

public sealed record DeploymentArtifactCreateResponse(
    string Id,
    string AppId,
    string ServiceName,
    string Status,
    string UploadUrl,
    long SizeBytes,
    string Sha256,
    DateTimeOffset ExpiresAt);

public sealed record DeploymentArtifactResponse(
    string Id,
    string AppId,
    string ServiceName,
    string Status,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

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

public sealed record DeploymentActivityResponse(
    string Id,
    string Name,
    string Status,
    string? Conclusion,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<DeploymentActivityStepResponse> Steps);

public sealed record DeploymentActivityStepResponse(
    int Number,
    string Name,
    string Status,
    string? Conclusion,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record StartDeploymentPreparationRequest(string AppId, string IdempotencyKey, IReadOnlyList<string> ServiceNames, string? Plan = null);
public sealed record UpdateDeploymentPreparationRequest(string AppId, string Status, string Stage = "build");
