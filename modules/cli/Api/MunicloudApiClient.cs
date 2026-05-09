using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Municloud.Cli.Auth;

namespace Municloud.Cli.Api;

public sealed class MunicloudApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CliEnvironment _environment;
    private readonly TokenStore _tokenStore;
    private readonly HttpClient _httpClient;

    public MunicloudApiClient(CliEnvironment environment, TokenStore tokenStore)
        : this(environment, tokenStore, new HttpClient())
    {
    }

    public MunicloudApiClient(CliEnvironment environment, TokenStore tokenStore, HttpClient httpClient)
    {
        _environment = environment;
        _tokenStore = tokenStore;
        _httpClient = httpClient;
    }

    public Task<MeResponse> GetMeAsync(CancellationToken cancellationToken) =>
        SendAsync<MeResponse>(HttpMethod.Get, "/v1/me", null, cancellationToken);

    public Task<MeResponse> GetMeWithTokenAsync(string token, CancellationToken cancellationToken) =>
        SendAsync<MeResponse>(HttpMethod.Get, "/v1/me", null, cancellationToken, token);

    public Task<IReadOnlyList<AppResponse>> GetAppsAsync(string organizationId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<AppResponse>>(HttpMethod.Get, $"/v1/apps?organizationId={Uri.EscapeDataString(organizationId)}", null, cancellationToken);

    public Task<AppResponse> GetAppAsync(string appId, CancellationToken cancellationToken) =>
        SendAsync<AppResponse>(HttpMethod.Get, $"/v1/apps/{Uri.EscapeDataString(appId)}", null, cancellationToken);

    public Task<AppResponse> CreateAppAsync(CreateAppRequest request, CancellationToken cancellationToken) =>
        SendAsync<AppResponse>(HttpMethod.Post, "/v1/apps", request, cancellationToken);

    public Task<DeploymentCreateResponse> CreateDeploymentAsync(CreateDeploymentRequest request, CancellationToken cancellationToken) =>
        SendAsync<DeploymentCreateResponse>(HttpMethod.Post, "/v1/deployments", request, cancellationToken);

    public Task<IReadOnlyList<DeploymentResponse>> GetDeploymentsAsync(string organizationId, string? appId, CancellationToken cancellationToken)
    {
        var query = $"organizationId={Uri.EscapeDataString(organizationId)}";
        if (!string.IsNullOrWhiteSpace(appId))
        {
            query += $"&appId={Uri.EscapeDataString(appId)}";
        }

        return SendAsync<IReadOnlyList<DeploymentResponse>>(HttpMethod.Get, $"/v1/deployments?{query}", null, cancellationToken);
    }

    public Task<DeploymentResponse> GetDeploymentAsync(string deploymentId, CancellationToken cancellationToken) =>
        SendAsync<DeploymentResponse>(HttpMethod.Get, $"/v1/deployments/{Uri.EscapeDataString(deploymentId)}", null, cancellationToken);

    public Task<DeploymentResponse> RefreshDeploymentAsync(string deploymentId, CancellationToken cancellationToken) =>
        SendAsync<DeploymentResponse>(HttpMethod.Post, $"/v1/deployments/{Uri.EscapeDataString(deploymentId)}/refresh", null, cancellationToken);

    public Task<IReadOnlyList<DeploymentEventResponse>> GetDeploymentEventsAsync(string deploymentId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<DeploymentEventResponse>>(HttpMethod.Get, $"/v1/deployments/{Uri.EscapeDataString(deploymentId)}/events", null, cancellationToken);

    public Task<IReadOnlyList<DeploymentLogResponse>> GetDeploymentLogsAsync(string deploymentId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<DeploymentLogResponse>>(HttpMethod.Get, $"/v1/deployments/{Uri.EscapeDataString(deploymentId)}/logs", null, cancellationToken);

    public Task<IReadOnlyList<RuntimeLogResponse>> GetRuntimeLogsAsync(
        string appId,
        string? source,
        string? service,
        int tail,
        string? since,
        CancellationToken cancellationToken)
    {
        var query = new List<string>
        {
            $"tail={tail}"
        };
        if (!string.IsNullOrWhiteSpace(source))
        {
            query.Add($"source={Uri.EscapeDataString(source)}");
        }
        if (!string.IsNullOrWhiteSpace(service))
        {
            query.Add($"service={Uri.EscapeDataString(service)}");
        }
        if (!string.IsNullOrWhiteSpace(since))
        {
            query.Add($"since={Uri.EscapeDataString(since)}");
        }

        return SendAsync<IReadOnlyList<RuntimeLogResponse>>(HttpMethod.Get, $"/v1/apps/{Uri.EscapeDataString(appId)}/logs?{string.Join("&", query)}", null, cancellationToken);
    }

    public Task<CliLoginSessionCreateResponse> CreateCliLoginSessionAsync(CancellationToken cancellationToken) =>
        SendUnauthenticatedAsync<CliLoginSessionCreateResponse>(HttpMethod.Post, "/v1/cli-login-sessions", null, cancellationToken);

    public Task<CliLoginSessionExchangeResponse?> ExchangeCliLoginSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        SendUnauthenticatedMaybeAcceptedAsync<CliLoginSessionExchangeResponse>(HttpMethod.Post, $"/v1/cli-login-sessions/{Uri.EscapeDataString(sessionId)}/exchange", null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken, string? tokenOverride = null)
    {
        var token = tokenOverride ?? _tokenStore.GetToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ApiException((int)HttpStatusCode.Unauthorized, "missing_token", "Set MUNICLOUD_TOKEN or run 'municloud token set <token>'.");
        }

        using var request = new HttpRequestMessage(method, _environment.ApiBaseUrl + pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, cancellationToken);
        }

        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return result ?? throw new ApiException((int)response.StatusCode, "empty_response", "Municloud API returned an empty response.");
    }

    private async Task<T> SendUnauthenticatedAsync<T>(HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _environment.ApiBaseUrl + pathAndQuery);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, cancellationToken);
        }

        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return result ?? throw new ApiException((int)response.StatusCode, "empty_response", "Municloud API returned an empty response.");
    }

    private async Task<T?> SendUnauthenticatedMaybeAcceptedAsync<T>(HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _environment.ApiBaseUrl + pathAndQuery);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return default;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private static async Task ThrowApiExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions, cancellationToken);
            if (error is not null)
            {
                throw new ApiException((int)response.StatusCode, error.Error.Code, error.Error.Message);
            }
        }
        catch (JsonException)
        {
        }

        throw new ApiException((int)response.StatusCode, "api_error", $"Municloud API returned HTTP {(int)response.StatusCode}.");
    }
}
