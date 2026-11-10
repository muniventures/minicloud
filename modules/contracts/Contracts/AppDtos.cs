// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record CreateAppRequest(
    string OrganizationId,
    string Name,
    string Slug,
    string Plan,
    string Database,
    string EnvironmentName = "production");

public sealed record DeleteAppRequest(string Confirmation);
public sealed record SelectAppPlanRequest(string Plan);

public sealed record EnsureAppBranchRequest(string BranchName, string? Plan = null);

public sealed record AppBranchResponse(
    string Id,
    string Name,
    string Slug,
    string BranchName,
    string Plan,
    string Database,
    string? WebsiteUrl,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Urls,
    string? PlanId = null);

public sealed record EnvironmentDetailResponse(
    string Id,
    string OrganizationId,
    string Name,
    string Slug,
    string? ParentAppId,
    string? BranchName,
    string Plan,
    string Database,
    IReadOnlyList<AppDomainResponse> Domains,
    IReadOnlyList<AppBranchResponse> Branches,
    LatestDeploymentResponse? LatestDeployment,
    string? DeletionStatus = null,
    string? DeletionError = null,
    string? EnvironmentName = null,
    IReadOnlyList<AppEnvironmentResponse>? Environments = null,
    string? PlanId = null,
    string? ApplicationId = null,
    string? DefaultEnvironmentId = null);

public sealed record AppDomainResponse(
    string Id,
    string Hostname,
    string Url,
    string SslStatus,
    bool Reserved);

public sealed record LatestDeploymentResponse(
    string Id,
    string Status,
    string? WebsiteUrl,
    DateTimeOffset? CompletedAt);

public sealed record AppEnvironmentResponse(string Id, string Name, string Plan, string Database, string? DeletionStatus, string? PlanId = null);

public sealed record CreateInitialEnvironmentRequest(
    string? Name,
    string Plan,
    string Database = "sqlite");

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateApplicationRequest(
    string OrganizationId,
    string Name,
    string Slug,
    CreateInitialEnvironmentRequest? InitialEnvironment = null);

public sealed record UpdateApplicationRequest(
    string? Name = null,
    string? DefaultEnvironmentId = null,
    string? Plan = null,
    string? Database = null);

public sealed record CreateEnvironmentRequest(
    string Name,
    string Plan,
    string? Database = "none",
    string? Kind = "persistent",
    string? SourceBranch = null,
    string? SourceEnvironmentId = null);

public sealed record UpdateEnvironmentRequest(
    string? Name = null,
    string? Plan = null,
    string? Database = null,
    bool? SetAsDefault = null);

public sealed record DeleteEnvironmentRequest(string Confirmation);
public sealed record DeleteApplicationRequest(string Confirmation);

public sealed record EnvironmentRuntimeObservationResponse(
    string State,
    bool Connected,
    bool Stale,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? LastSeenAt,
    int TotalServices,
    int HealthyServices);

public sealed record ApplicationEnvironmentResponse(
    string Id,
    string ApplicationId,
    string OrganizationId,
    string Name,
    string Kind,
    string RuntimeSlug,
    string Plan,
    string? PlanId,
    string Database,
    string? SourceBranch,
    string? SourceEnvironmentId,
    string? DeletionStatus,
    string? DeletionError,
    bool PlacementLocked,
    IReadOnlyList<AppDomainResponse> Domains,
    LatestDeploymentResponse? LatestDeployment,
    EnvironmentRuntimeObservationResponse RuntimeObservation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApplicationResponse(
    string Id,
    string OrganizationId,
    string Name,
    string Slug,
    string? DefaultEnvironmentId,
    string? DeletionStatus,
    string? DeletionError,
    int EnvironmentCount,
    int PersistentEnvironmentCount,
    int PreviewEnvironmentCount,
    IReadOnlyList<ApplicationEnvironmentResponse> Environments,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
