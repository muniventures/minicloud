namespace Minicloud.Common.Auth;

/// <summary>
/// Plain actor identity decoupled from HTTP claims and token adapters.
/// Shared authorization and audit services depend only on this record;
/// API translates its claims principal into it at the HTTP boundary.
/// </summary>
public sealed record ActorIdentity(
    string Kind,
    string? UserId,
    string? ApiKeyId,
    string? OrganizationId,
    IReadOnlySet<string> Scopes)
{
    public bool IsDashboardUser => Kind == "browser_session";

    public bool IsApiKey => Kind == "api_key";

    public string Describe() =>
        UserId ?? (ApiKeyId is not null ? $"api_key:{ApiKeyId}" : Kind);
}
