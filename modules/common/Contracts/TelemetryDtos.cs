namespace Minicloud.Common.Contracts;

public sealed record TelemetrySettingsResponse(
    string AppId,
    string OrganizationId,
    string TenantId,
    bool Enabled,
    string RetentionPolicy,
    string ProvisioningStatus,
    DateTimeOffset? LastAcceptedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? IngestionUrl = null);

public sealed record UpdateTelemetrySettingsRequest(bool Enabled);

public sealed record IssueTelemetryCredentialRequest(
    string ServiceName,
    string Name,
    DateTimeOffset? ExpiresAt);

public sealed record TelemetryCredentialResponse(
    string Id,
    string ServiceName,
    string Name,
    string KeyPrefix,
    int CredentialVersion,
    string? CreatedByActor,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset CreatedAt);

public sealed record TelemetryCredentialIssueResponse(
    string Id,
    string ServiceName,
    string Name,
    string KeyPrefix,
    int CredentialVersion,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    string Token);

public sealed record TelemetryLogEntryResponse(
    DateTimeOffset Timestamp,
    string Service,
    string Severity,
    string? TraceId,
    string Body,
    IReadOnlyDictionary<string, string>? Attributes);

public sealed record TelemetryLogsResponse(
    string State,
    IReadOnlyList<TelemetryLogEntryResponse> Entries,
    bool Truncated,
    string? TruncationReason,
    DateTimeOffset RetentionCutoff,
    DateTimeOffset? LastAcceptedAt);

public sealed record TelemetryReadTokenIssueResponse(
    string Token,
    DateTimeOffset ExpiresAt,
    string QueryUrl);
