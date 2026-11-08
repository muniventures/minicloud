using System.Text.Json.Serialization;

namespace Minicloud.Common.Contracts;

public sealed record CreateRuntimeServerRequest(
    string Provider,
    string? ProviderServerId,
    string? Hostname,
    string? AgentVersion,
    string? ProviderAccountId = null);

public sealed record RuntimeServerCreateResponse(
    string Id,
    string OrganizationId,
    string AppId,
    string TokenPrefix,
    string Token,
    DateTimeOffset CreatedAt);

public sealed record RuntimeServerResponse(
    string Id,
    string OrganizationId,
    string AppId,
    string Provider,
    string? ProviderServerId,
    string? Hostname,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt);

public sealed record RuntimeLogIngestRequest(IReadOnlyList<RuntimeLogIngestRecord> Records);

public sealed record RuntimeLogIngestRecord(
    string Source,
    string? Service,
    string Stream,
    DateTimeOffset ObservedAt,
    string Cursor,
    string Message);

public sealed record RuntimeLogIngestResponse(
    int Accepted,
    int Rejected,
    IReadOnlyDictionary<string, string> LastAcceptedCursorBySource);

public sealed record RuntimeHeartbeatRequest(
    string? AgentVersion,
    DateTimeOffset? ObservedAt);

public sealed record RuntimeStatusIngestRequest(
    string? AgentVersion,
    DateTimeOffset ObservedAt,
    string ComposeProject,
    IReadOnlyList<RuntimeServiceStatusDto> Services,
    RuntimeCaddyStatusDto? Caddy);

public sealed record RuntimeCaddyStatusDto(bool Active);

public sealed record RuntimeStatusResponse(
    string ServerId,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    bool AgentInstalled,
    bool AgentOnline,
    DateTimeOffset? ObservedAt,
    IReadOnlyList<RuntimeServiceStatusDto> Services,
    RuntimeCaddyStatusDto? Caddy);

public sealed record RuntimeLogSourceResponse(
    string Source,
    string? Service,
    DateTimeOffset LastObservedAt);

public sealed record RuntimeDiagnosticBundleResponse(
    IReadOnlyList<RuntimeLogResponse> CaddyErrors,
    IReadOnlyList<RuntimeLogResponse> ServiceErrors,
    RuntimeStatusResponse? Status,
    string? LatestDeploymentUrl);

public sealed record StoredRuntimeStatus(
    string? AgentVersion,
    DateTimeOffset ObservedAt,
    string ComposeProject,
    IReadOnlyList<RuntimeServiceStatusDto> Services,
    RuntimeCaddyStatusDto? Caddy);
