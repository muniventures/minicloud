// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Minicloud.Common.Contracts;

public sealed record AppServiceSecretResponse(
    string Id,
    string AppId,
    string ServiceName,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AppServiceSecretServiceResponse(
    string Id,
    string AppId,
    string ServiceName,
    int SecretCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateAppServiceSecretServiceRequest(string ServiceName);

public sealed record UpdateAppServiceSecretServiceRequest(string ServiceName);

public sealed record SetAppServiceSecretRequest(
    string? ServiceName,
    string Name,
    string Value);

public sealed record UpdateAppServiceSecretRequest(string Value);

public sealed record ValidateDeploymentSecretsRequest(
    string AppId,
    IReadOnlyList<DeploymentSecretServiceRequest> Services,
    string? ApplicationId = null,
    string? EnvironmentId = null);

public sealed record ResolveDeploymentSecretsRequest(
    IReadOnlyList<DeploymentSecretServiceRequest> Services);

public sealed record DeploymentSecretServiceRequest(
    string Name,
    [property: JsonPropertyName("secret_env")]
    IReadOnlyDictionary<string, string>? SecretEnv = null);
