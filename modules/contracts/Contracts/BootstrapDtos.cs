namespace Minicloud.Common.Contracts;

public sealed record BootstrapStatusResponse(string State, DateTimeOffset? ExpiresAt);

public sealed record CompleteSetupRequest(
    string SetupToken,
    string Email,
    string Password,
    string DisplayName,
    string OrganizationName,
    string OrganizationSlug);

public sealed record BootstrapResult(
    string InstallationId,
    string OperationId,
    string State,
    string? UserId = null,
    string? OrganizationId = null,
    DateTimeOffset? CompletedAt = null);
