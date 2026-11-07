using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

/// <summary>
/// HTTP implementation of <see cref="IGrafanaApiClient"/> against the private
/// Grafana listener (grafana-oss 11.6.5). Uses server-admin basic auth supplied by
/// <see cref="GrafanaOperatorCredentialProvider"/>; every failure surfaces as
/// <see cref="GrafanaProvisioningUnavailableException"/> so callers fail closed.
/// </summary>
public sealed class GrafanaApiClient(
    HttpClient httpClient,
    GrafanaOperatorCredentialProvider credentials,
    IOptions<GrafanaOptions> options,
    ILogger<GrafanaApiClient> logger) : IGrafanaApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GrafanaOptions _options = options.Value;

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/health", orgId: null, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>(JsonOptions, cancellationToken);
        return body?.Version ?? throw new GrafanaProvisioningUnavailableException("Grafana health response did not include a version.");
    }

    public async Task<GrafanaOrgState> EnsureOrganizationAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var existing = await SendAsync(HttpMethod.Get, $"api/orgs/name/{Uri.EscapeDataString(name)}", orgId: null, cancellationToken);
            if (existing.IsSuccessStatusCode)
            {
                var found = await existing.Content.ReadFromJsonAsync<OrgBody>(JsonOptions, cancellationToken);
                if (found is not null)
                {
                    return new GrafanaOrgState(found.Id, found.Name);
                }
            }
            else if (existing.StatusCode != HttpStatusCode.NotFound)
            {
                throw await FailureAsync(existing, "looking up Grafana organization", cancellationToken);
            }
        }
        catch (GrafanaProvisioningUnavailableException)
        {
            throw;
        }

        using var created = await SendAsync(HttpMethod.Post, "api/orgs", orgId: null, new { name }, cancellationToken);
        if (created.StatusCode == HttpStatusCode.Conflict)
        {
            using var retry = await SendAsync(HttpMethod.Get, $"api/orgs/name/{Uri.EscapeDataString(name)}", orgId: null, cancellationToken);
            if (retry.IsSuccessStatusCode)
            {
                var found = await retry.Content.ReadFromJsonAsync<OrgBody>(JsonOptions, cancellationToken);
                if (found is not null)
                {
                    return new GrafanaOrgState(found.Id, found.Name);
                }
            }

            throw new GrafanaProvisioningUnavailableException("Grafana organization creation raced and the retry lookup failed.");
        }

        if (!created.IsSuccessStatusCode)
        {
            throw await FailureAsync(created, "creating Grafana organization", cancellationToken);
        }

        var result = await created.Content.ReadFromJsonAsync<OrgIdBody>(JsonOptions, cancellationToken);
        if (result is null)
        {
            throw new GrafanaProvisioningUnavailableException("Grafana organization creation did not return an ID.");
        }

        return new GrafanaOrgState(result.OrgId, name);
    }

    public async Task<GrafanaUserState> EnsureUserAsync(string login, string email, string name, CancellationToken cancellationToken)
    {
        using var lookup = await SendAsync(HttpMethod.Get, $"api/users/lookup?loginOrEmail={Uri.EscapeDataString(login)}", orgId: null, cancellationToken);
        if (lookup.IsSuccessStatusCode)
        {
            var found = await lookup.Content.ReadFromJsonAsync<UserBody>(JsonOptions, cancellationToken);
            if (found is not null)
            {
                return new GrafanaUserState(found.Id, found.Login, found.Email);
            }
        }
        else if (lookup.StatusCode != HttpStatusCode.NotFound)
        {
            throw await FailureAsync(lookup, "looking up Grafana user", cancellationToken);
        }

        var password = "unused-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        using var created = await SendAsync(
            HttpMethod.Post, "api/admin/users", orgId: null,
            new { login, email, name, password }, cancellationToken);
        if (created.StatusCode == HttpStatusCode.Conflict || created.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            using var retry = await SendAsync(HttpMethod.Get, $"api/users/lookup?loginOrEmail={Uri.EscapeDataString(login)}", orgId: null, cancellationToken);
            if (retry.IsSuccessStatusCode)
            {
                var found = await retry.Content.ReadFromJsonAsync<UserBody>(JsonOptions, cancellationToken);
                if (found is not null)
                {
                    return new GrafanaUserState(found.Id, found.Login, found.Email);
                }
            }

            throw new GrafanaProvisioningUnavailableException("Grafana user creation raced and the retry lookup failed.");
        }

        if (!created.IsSuccessStatusCode)
        {
            throw await FailureAsync(created, "creating Grafana user", cancellationToken);
        }

        var result = await created.Content.ReadFromJsonAsync<UserIdBody>(JsonOptions, cancellationToken);
        if (result is null)
        {
            throw new GrafanaProvisioningUnavailableException("Grafana user creation did not return an ID.");
        }

        return new GrafanaUserState(result.Id, login, email);
    }

    public async Task<IReadOnlyList<GrafanaOrgMembership>> GetOrgUsersAsync(long grafanaOrgId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/orgs/{grafanaOrgId}/users", grafanaOrgId, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, "listing Grafana organization users", cancellationToken);
        }

        var users = await response.Content.ReadFromJsonAsync<List<OrgUserBody>>(JsonOptions, cancellationToken) ?? [];
        return users.Select(x => new GrafanaOrgMembership(x.UserId, x.Login, x.Role)).ToList();
    }

    public async Task<IReadOnlyList<long>> GetUserOrganizationsAsync(long grafanaUserId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/users/{grafanaUserId}/orgs", orgId: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, "listing Grafana user organizations", cancellationToken);
        }

        var orgs = await response.Content.ReadFromJsonAsync<List<OrgBody>>(JsonOptions, cancellationToken) ?? [];
        return orgs.Select(x => x.Id).ToList();
    }

    public async Task AddOrgUserAsync(long grafanaOrgId, long grafanaUserId, string role, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post, $"api/orgs/{grafanaOrgId}/users", grafanaOrgId,
            new { userId = grafanaUserId, role }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, "adding Grafana organization user", cancellationToken);
        }
    }

    public async Task UpdateOrgUserRoleAsync(long grafanaOrgId, long grafanaUserId, string role, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Patch, $"api/orgs/{grafanaOrgId}/users/{grafanaUserId}", grafanaOrgId,
            new { role }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, "updating Grafana organization user role", cancellationToken);
        }
    }

    public async Task RemoveOrgUserAsync(long grafanaOrgId, long grafanaUserId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"api/orgs/{grafanaOrgId}/users/{grafanaUserId}", grafanaOrgId, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            throw await FailureAsync(response, "removing Grafana organization user", cancellationToken);
        }
    }

    public async Task<GrafanaDataSourceState?> GetDataSourceAsync(long grafanaOrgId, string uid, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/datasources/uid/{Uri.EscapeDataString(uid)}", grafanaOrgId, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, "reading Grafana data source", cancellationToken);
        }

        var ds = await response.Content.ReadFromJsonAsync<DataSourceBody>(JsonOptions, cancellationToken);
        return ds is null ? null : new GrafanaDataSourceState(ds.Uid, ds.Name, ds.Url, ds.Access, ds.IsDefault);
    }

    public async Task<GrafanaDataSourceState> EnsureDataSourceAsync(
        long grafanaOrgId,
        string uid,
        string name,
        string url,
        string? authorizationHeaderValue,
        CancellationToken cancellationToken)
    {
        var existing = await GetDataSourceAsync(grafanaOrgId, uid, cancellationToken);
        // A null credential preserves the existing server-side secure value in
        // Grafana; it is only sent when a fresh credential is first created.
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["uid"] = uid,
            ["name"] = name,
            ["type"] = "loki",
            ["access"] = "proxy",
            ["url"] = url,
            ["isDefault"] = false,
            ["readOnly"] = false,
            ["jsonData"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["httpHeaderName1"] = "Authorization"
            }
        };
        if (authorizationHeaderValue is not null)
        {
            payload["secureJsonData"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["httpHeaderValue1"] = authorizationHeaderValue
            };
        }

        if (existing is null)
        {
            using var created = await SendAsync(HttpMethod.Post, "api/datasources", grafanaOrgId, payload, cancellationToken);
            if (!created.IsSuccessStatusCode)
            {
                throw await FailureAsync(created, "creating Grafana data source", cancellationToken);
            }
        }
        else
        {
            using var updated = await SendAsync(HttpMethod.Put, $"api/datasources/uid/{Uri.EscapeDataString(uid)}", grafanaOrgId, payload, cancellationToken);
            if (!updated.IsSuccessStatusCode)
            {
                throw await FailureAsync(updated, "updating Grafana data source", cancellationToken);
            }
        }

        var verified = await GetDataSourceAsync(grafanaOrgId, uid, cancellationToken);
        return verified ?? throw new GrafanaProvisioningUnavailableException("Grafana data source write did not persist.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string relativePath, long? orgId, CancellationToken cancellationToken) =>
        await SendAsync<object>(method, relativePath, orgId, body: null, cancellationToken);

    private async Task<HttpResponseMessage> SendAsync<T>(
        HttpMethod method, string relativePath, long? orgId, T? body, CancellationToken cancellationToken)
    {
        var password = await credentials.GetServerAdminPasswordAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new GrafanaProvisioningUnavailableException("Grafana server-admin credential is unavailable; provisioning cannot proceed.");
        }

        Uri endpoint;
        try
        {
            endpoint = new Uri(new Uri(_options.BaseAddress.TrimEnd('/') + "/"), relativePath);
        }
        catch (UriFormatException ex)
        {
            throw new GrafanaProvisioningUnavailableException("Grafana base address is misconfigured.", ex);
        }

        if (!endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !System.Net.IPAddress.TryParse(endpoint.Host, out _)
            && !endpoint.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Host, "grafana", StringComparison.OrdinalIgnoreCase))
        {
            throw new GrafanaProvisioningUnavailableException("Grafana base address must be private (loopback or internal host).");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var client = httpClient;
            using var request = new HttpRequestMessage(method, endpoint);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.AdminUsername}:{password}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
            if (orgId.HasValue)
            {
                request.Headers.TryAddWithoutValidation("X-Grafana-Org-Id", orgId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: JsonOptions);
            }

            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GrafanaProvisioningUnavailableException("Grafana provisioning request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Grafana provisioning transport failure.");
            throw new GrafanaProvisioningUnavailableException("Grafana is unreachable.", ex);
        }
    }

    private static async Task<GrafanaProvisioningUnavailableException> FailureAsync(
        HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        string detail;
        try
        {
            detail = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        }
        catch (OperationCanceledException)
        {
            detail = string.Empty;
        }

        if (detail.Length > 256)
        {
            detail = detail[..256];
        }

        return new GrafanaProvisioningUnavailableException(
            $"Grafana error {(int)response.StatusCode} while {action}.{(string.IsNullOrEmpty(detail) ? string.Empty : " " + detail)}");
    }

    private sealed record HealthBody(string Version);
    private sealed record OrgBody(long Id, string Name);
    private sealed record OrgIdBody(long OrgId);
    private sealed record UserBody(long Id, string Login, string Email);
    private sealed record UserIdBody(long Id);
    private sealed record OrgUserBody(long UserId, string Login, string Role);
    private sealed record DataSourceBody(string Uid, string Name, string Url, string Access, bool IsDefault);
}
