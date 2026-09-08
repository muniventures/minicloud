using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Minicloud.Cli.Auth;

namespace Minicloud.Cli.Api;

public sealed class MinicloudApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CliEnvironment _environment;
    private readonly TokenStore _tokenStore;
    private readonly HttpClient _httpClient;

    public MinicloudApiClient(CliEnvironment environment, TokenStore tokenStore)
        : this(environment, tokenStore, new HttpClient())
    {
    }

    public MinicloudApiClient(CliEnvironment environment, TokenStore tokenStore, HttpClient httpClient)
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

    public Task<AppResponse> EnsureBranchAsync(string appId, EnsureAppBranchRequest request, CancellationToken cancellationToken) =>
        SendAsync<AppResponse>(HttpMethod.Post, $"/v1/apps/{Uri.EscapeDataString(appId)}/branches", request, cancellationToken);

    public Task DestroyBranchAsync(string appId, string branchAppId, CancellationToken cancellationToken) =>
        SendNoContentAsync(HttpMethod.Delete, $"/v1/apps/{Uri.EscapeDataString(appId)}/branches/{Uri.EscapeDataString(branchAppId)}", cancellationToken);

    public Task<IReadOnlyList<AppServiceInventoryResponse>> GetAppServicesAsync(string appId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<AppServiceInventoryResponse>>(HttpMethod.Get, $"/v1/domains/services?appId={Uri.EscapeDataString(appId)}", null, cancellationToken);

    public Task<IReadOnlyList<DomainBindingResponse>> GetDomainsAsync(string appId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<DomainBindingResponse>>(HttpMethod.Get, $"/v1/domains?appId={Uri.EscapeDataString(appId)}", null, cancellationToken);

    public Task<IReadOnlyList<AppServiceSecretResponse>> GetSecretsAsync(string appId, string? serviceName, CancellationToken cancellationToken)
    {
        var path = $"/v1/secrets?appId={Uri.EscapeDataString(appId)}";
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            path += $"&service={Uri.EscapeDataString(serviceName)}";
        }

        return SendAsync<IReadOnlyList<AppServiceSecretResponse>>(HttpMethod.Get, path, null, cancellationToken);
    }

    public Task<AppServiceSecretResponse> SetSecretAsync(string appId, SetAppServiceSecretRequest request, CancellationToken cancellationToken) =>
        SendAsync<AppServiceSecretResponse>(HttpMethod.Post, $"/v1/secrets?appId={Uri.EscapeDataString(appId)}", request, cancellationToken);

    public Task DeleteSecretAsync(string appId, string secretId, CancellationToken cancellationToken) =>
        SendNoContentAsync(HttpMethod.Delete, $"/v1/secrets/{Uri.EscapeDataString(secretId)}?appId={Uri.EscapeDataString(appId)}", cancellationToken);

    public Task<DomainBindingResponse> CreateDomainAsync(string appId, CreateDomainBindingRequest request, CancellationToken cancellationToken) =>
        SendAsync<DomainBindingResponse>(HttpMethod.Post, $"/v1/domains?appId={Uri.EscapeDataString(appId)}", request, cancellationToken);

    public Task<DomainBindingResponse> UpdateDomainAsync(string appId, string domainId, UpdateDomainBindingRequest request, CancellationToken cancellationToken) =>
        SendAsync<DomainBindingResponse>(HttpMethod.Patch, $"/v1/domains/{Uri.EscapeDataString(domainId)}?appId={Uri.EscapeDataString(appId)}", request, cancellationToken);

    public Task DeleteDomainAsync(string appId, string domainId, CancellationToken cancellationToken) =>
        SendNoContentAsync(HttpMethod.Delete, $"/v1/domains/{Uri.EscapeDataString(domainId)}?appId={Uri.EscapeDataString(appId)}", cancellationToken);

    public Task<DeploymentCreateResponse> CreateDeploymentAsync(CreateDeploymentRequest request, CancellationToken cancellationToken) =>
        SendAsync<DeploymentCreateResponse>(HttpMethod.Post, "/v1/deployments", request, cancellationToken);

    public Task<DeploymentArtifactCreateResponse> CreateDeploymentArtifactAsync(CreateDeploymentArtifactRequest request, CancellationToken cancellationToken) =>
        SendAsync<DeploymentArtifactCreateResponse>(HttpMethod.Post, "/v1/artifacts", request, cancellationToken);

    public async Task<DeploymentArtifactResponse> UploadDeploymentArtifactContentAsync(string artifactId, string uploadUrl, string filePath, string sha256, long sizeBytes, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(uploadUrl)
            ? $"/v1/artifacts/{Uri.EscapeDataString(artifactId)}/file"
            : UploadPathAndQuery(uploadUrl);
        var token = _tokenStore.GetToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ApiException((int)HttpStatusCode.Unauthorized, "missing_token", "Set MINICLOUD_TOKEN or run 'minicloud token set <token>'.");
        }

        await using var stream = File.OpenRead(filePath);
        using var request = new HttpRequestMessage(HttpMethod.Put, _environment.ApiBaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Minicloud-Artifact-Sha256", sha256);
        request.Headers.Add("X-Minicloud-Artifact-Size", sizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        request.Content.Headers.ContentLength = sizeBytes;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, cancellationToken);
        }

        var result = await response.Content.ReadFromJsonAsync<DeploymentArtifactResponse>(JsonOptions, cancellationToken);
        return result ?? throw new ApiException((int)response.StatusCode, "empty_response", "Minicloud API returned an empty response.");
    }

    public Task<IReadOnlyList<DeploymentResponse>> GetDeploymentsAsync(string organizationId, string? appId, CancellationToken cancellationToken)
    {
        var query = $"organizationId={Uri.EscapeDataString(organizationId)}";
        if (!string.IsNullOrWhiteSpace(appId))
        {
            query += $"&appId={Uri.EscapeDataString(appId)}";
        }

        return SendAsync<IReadOnlyList<DeploymentResponse>>(HttpMethod.Get, $"/v1/deployments?{query}", null, cancellationToken);
    }

    private static string UploadPathAndQuery(string uploadUrl)
    {
        if (Uri.TryCreate(uploadUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.PathAndQuery;
        }

        return uploadUrl.StartsWith("/", StringComparison.Ordinal) ? uploadUrl : "/" + uploadUrl;
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

        query.Insert(0, $"appId={Uri.EscapeDataString(appId)}");
        return SendAsync<IReadOnlyList<RuntimeLogResponse>>(HttpMethod.Get, $"/v1/runtime/logs?{string.Join("&", query)}", null, cancellationToken);
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
            throw new ApiException((int)HttpStatusCode.Unauthorized, "missing_token", "Set MINICLOUD_TOKEN or run 'minicloud token set <token>'.");
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
        return result ?? throw new ApiException((int)response.StatusCode, "empty_response", "Minicloud API returned an empty response.");
    }

    private async Task SendNoContentAsync(HttpMethod method, string pathAndQuery, CancellationToken cancellationToken)
    {
        var token = _tokenStore.GetToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ApiException((int)HttpStatusCode.Unauthorized, "missing_token", "Set MINICLOUD_TOKEN or run 'minicloud token set <token>'.");
        }

        using var request = new HttpRequestMessage(method, _environment.ApiBaseUrl + pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, cancellationToken);
        }
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
        return result ?? throw new ApiException((int)response.StatusCode, "empty_response", "Minicloud API returned an empty response.");
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

        throw new ApiException((int)response.StatusCode, "api_error", $"Minicloud API returned HTTP {(int)response.StatusCode}.");
    }
}
