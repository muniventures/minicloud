namespace Minicloud.Common.Services;

public sealed class GrafanaProvisioningUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed record GrafanaOrgState(long Id, string Name);
public sealed record GrafanaUserState(long Id, string Login, string Email);
public sealed record GrafanaOrgMembership(long UserId, string Login, string Role);
public sealed record GrafanaDataSourceState(string Uid, string Name, string Url, string Access, bool IsDefault);

/// <summary>
/// Private provisioning client for the pinned Grafana release. All operations use
/// the server-admin basic-auth credential (organization service-account tokens
/// cannot create organizations) and run against the private Grafana listener only.
/// No method on this client is reachable from a public route.
/// </summary>
public interface IGrafanaApiClient
{
    Task<string> GetVersionAsync(CancellationToken cancellationToken);
    Task<GrafanaOrgState> EnsureOrganizationAsync(string name, CancellationToken cancellationToken);
    Task<GrafanaUserState> EnsureUserAsync(string login, string email, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<GrafanaOrgMembership>> GetOrgUsersAsync(long grafanaOrgId, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> GetUserOrganizationsAsync(long grafanaUserId, CancellationToken cancellationToken);
    Task AddOrgUserAsync(long grafanaOrgId, long grafanaUserId, string role, CancellationToken cancellationToken);
    Task UpdateOrgUserRoleAsync(long grafanaOrgId, long grafanaUserId, string role, CancellationToken cancellationToken);
    Task RemoveOrgUserAsync(long grafanaOrgId, long grafanaUserId, CancellationToken cancellationToken);
    Task<GrafanaDataSourceState?> GetDataSourceAsync(long grafanaOrgId, string uid, CancellationToken cancellationToken);
    Task<GrafanaDataSourceState> EnsureDataSourceAsync(
        long grafanaOrgId,
        string uid,
        string name,
        string url,
        string? authorizationHeaderValue,
        CancellationToken cancellationToken);
}
