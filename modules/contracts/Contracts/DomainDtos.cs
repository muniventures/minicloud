// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

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
    bool IsPrimary,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset? LastAppliedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? TargetIp = null,
    string? VerificationToken = null,
    DateTimeOffset? LastCheckedAt = null);

public sealed record CreateDomainBindingRequest(
    string ServiceName,
    string? Label = null,
    string? Hostname = null);

public sealed record DomainAvailabilityResponse(
    string Label,
    string Hostname,
    bool Available,
    string? Message);

public sealed record UpdateDomainBindingRequest(
    bool? Disabled,
    bool? IsPrimary = null);

public sealed record AppServiceInventoryResponse(
    string Name,
    string Image,
    int Port,
    bool Public,
    string Path,
    string HealthPath,
    RuntimeServiceStatusDto? Runtime,
    IReadOnlyList<DomainBindingResponse> Domains,
    string? Sha256 = null,
    DateTimeOffset? RuntimeObservedAt = null,
    IReadOnlyDictionary<string, StorageMountRequest>? Storage = null);

public sealed record RuntimeDesiredRoutingResponse(
    string Version,
    string AppId,
    string ComposeProject,
    IReadOnlyList<RuntimeDesiredRouteResponse> Routes);

public sealed record RuntimeDesiredRouteResponse(
    string BindingId,
    string Hostname,
    string PathPrefix,
    string ServiceName,
    int ServicePort);

public sealed record RuntimeRoutingApplyResultsRequest(
    string Version,
    IReadOnlyList<RuntimeRoutingApplyResultRequest> Results);

public sealed record RuntimeRoutingApplyResultRequest(
    string BindingId,
    string ApplyStatus,
    string SslStatus,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset ObservedAt);
