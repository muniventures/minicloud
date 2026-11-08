// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record CreateAppRequest(
    string OrganizationId,
    string Name,
    string Slug,
    string Plan,
    string Database,
    string EnvironmentName = "production");

public sealed record UpdateAppRequest(
    string? Name,
    string? Plan,
    string? Database);

public sealed record DeleteAppRequest(string Confirmation);

public sealed record EnsureAppBranchRequest(string BranchName);

public sealed record AppBranchResponse(
    string Id,
    string Name,
    string Slug,
    string BranchName,
    string Plan,
    string Database,
    string? WebsiteUrl,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Urls);

public sealed record AppResponse(
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
    IReadOnlyList<AppEnvironmentResponse>? Environments = null);

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

public sealed record CreateAppEnvironmentRequest(string Name, string Plan, string? Database = null);
public sealed record RenameAppEnvironmentRequest(string Name);
public sealed record AppEnvironmentResponse(string Id, string Name, string Plan, string Database, string? DeletionStatus);
