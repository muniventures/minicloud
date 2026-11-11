// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Minicloud.Cli.Auth;
using Minicloud.Common.Contracts;

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

    public async Task<InstanceInfoResponse> GetInstanceInfoAsync(string apiUrl, CancellationToken ct)
    {
        try
        {
            // Discovery never sends the current instance's credentials.
            using var response = await _httpClient.GetAsync(apiUrl + "/api/instance", ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Instance discovery returned HTTP {(int)response.StatusCode}.");
            var info = await response.Content.ReadFromJsonAsync<InstanceInfoResponse>(JsonOptions, ct);
            if (info is null || info.Product != "minicloud" || info.ProtocolVersion != 1 ||
                string.IsNullOrWhiteSpace(info.AppDomain) || Uri.CheckHostName(info.AppDomain) != UriHostNameType.Dns)
                throw new JsonException("Invalid Minicloud instance response.");
            return info;
        }
        catch (JsonException)
        {
            throw new Commands.CliCommandException(CliExitCodes.ValidationError, $"{apiUrl} did not return valid Minicloud instance information.");
        }
    }

    public Task<MeResponse> GetMeAsync(CancellationToken cancellationToken) =>
        SendAsync<MeResponse>(HttpMethod.Get, "/api/me", null, cancellationToken);

    public Task<MeResponse> GetMeWithTokenAsync(string token, CancellationToken cancellationToken) =>
        SendAsync<MeResponse>(HttpMethod.Get, "/api/me", null, cancellationToken, token);

    public Task<IReadOnlyList<ApplicationResponse>> GetApplicationsAsync(string organizationId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<ApplicationResponse>>(HttpMethod.Get, $"/api/apps?organizationId={Uri.EscapeDataString(organizationId)}", null, cancellationToken);

    public Task<ApplicationResponse> GetApplicationAsync(string applicationId, CancellationToken ct) =>
        SendAsync<ApplicationResponse>(HttpMethod.Get, $"/api/apps/{Uri.EscapeDataString(applicationId)}", null, ct);

    public Task<ApplicationEnvironmentResponse> CreateEnvironmentAsync(string applicationId, CreateEnvironmentRequest request, CancellationToken ct) =>
        SendAsync<ApplicationEnvironmentResponse>(HttpMethod.Post, $"/api/apps/{Uri.EscapeDataString(applicationId)}/environments", request, ct);

    public Task<ApplicationEnvironmentResponse> UpdateEnvironmentAsync(string applicationId, string environmentId, UpdateEnvironmentRequest request, CancellationToken ct) =>
        SendAsync<ApplicationEnvironmentResponse>(HttpMethod.Patch, $"/api/apps/{Uri.EscapeDataString(applicationId)}/environments/{Uri.EscapeDataString(environmentId)}", request, ct);

    public Task<EnvironmentDetailResponse> GetEnvironmentAsync(string environmentId, CancellationToken cancellationToken) =>
        SendAsync<EnvironmentDetailResponse>(HttpMethod.Get, $"/api/environments/{Uri.EscapeDataString(environmentId)}", null, cancellationToken);

    public Task<ImageBuildTarget> GetImageBuildTargetAsync(string appId, CancellationToken ct) =>
        SendAsync<ImageBuildTarget>(HttpMethod.Get, $"/api/environments/{Uri.EscapeDataString(appId)}/image-build-target", null, ct);
    public Task<ImagePublicationResponse> CreateImagePublicationAsync(string appId, CreateImagePublicationRequest request, CancellationToken ct) =>
        SendAsync<ImagePublicationResponse>(HttpMethod.Post, $"/api/environments/{Uri.EscapeDataString(appId)}/image-publications", request, ct);
    public Task<ImagePublicationCredentialsResponse> GetImagePublicationCredentialsAsync(string appId, string id, CancellationToken ct) =>
        SendAsync<ImagePublicationCredentialsResponse>(HttpMethod.Post, $"/api/environments/{Uri.EscapeDataString(appId)}/image-publications/{Uri.EscapeDataString(id)}/credentials", null, ct);
    public Task<ImagePublicationResponse> CompleteImagePublicationAsync(string appId, string id, string digest, CancellationToken ct) =>
        SendAsync<ImagePublicationResponse>(HttpMethod.Post, $"/api/environments/{Uri.EscapeDataString(appId)}/image-publications/{Uri.EscapeDataString(id)}/complete", new CompleteImagePublicationRequest(digest), ct);
    public Task CancelImagePublicationAsync(string appId, string id, CancellationToken ct) =>
        SendNoContentAsync(HttpMethod.Delete, $"/api/environments/{Uri.EscapeDataString(appId)}/image-publications/{Uri.EscapeDataString(id)}", ct);

    public Task<ApplicationResponse> CreateAppAsync(CreateApplicationRequest request, CancellationToken cancellationToken) =>
        SendAsync<ApplicationResponse>(HttpMethod.Post, "/api/apps", request, cancellationToken);

    public Task<EnvironmentDetailResponse> SelectPlanAsync(string appId, string plan, CancellationToken cancellationToken) =>
        SendAsync<EnvironmentDetailResponse>(HttpMethod.Post, $"/api/environments/{Uri.EscapeDataString(appId)}/plan", new SelectAppPlanRequest(plan), cancellationToken);

    public Task<IReadOnlyList<ProviderPlanResponse>> GetPlansAsync(string organizationId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<ProviderPlanResponse>>(HttpMethod.Get, $"/api/plans?organizationId={Uri.EscapeDataString(organizationId)}", null, cancellationToken);

    public Task<ProviderAccountResponse> CreateProviderAsync(CreateProviderRequest request, CancellationToken ct) =>
        SendAsync<ProviderAccountResponse>(HttpMethod.Post, "/api/providers", request, ct);

    public Task<IReadOnlyList<ProviderSshKeyResponse>> GetProviderSshKeysAsync(string providerAccountId, string organizationId, CancellationToken ct) =>
        SendAsync<IReadOnlyList<ProviderSshKeyResponse>>(HttpMethod.Get, $"/api/providers/{Uri.EscapeDataString(providerAccountId)}/ssh-keys?organizationId={Uri.EscapeDataString(organizationId)}", null, ct);

    public Task<ProviderLegacyImportResult> ImportProviderLegacyAsync(ProviderLegacyImportRequest request, CancellationToken ct) =>
        SendAsync<ProviderLegacyImportResult>(HttpMethod.Post, $"/api/providers/{Uri.EscapeDataString(request.ProviderAccountId)}/import-legacy", request, ct);

    public Task<EnvironmentDetailResponse> EnsureBranchAsync(string appId, EnsureAppBranchRequest request, CancellationToken cancellationToken) =>
        SendAsync<EnvironmentDetailResponse>(HttpMethod.Post, $"/api/environments/{Uri.EscapeDataString(appId)}/branches", request, cancellationToken);

    public Task DestroyBranchAsync(string appId, string branchAppId, CancellationToken cancellationToken) =>
        SendNoContentAsync(HttpMethod.Delete, $"/api/environments/{Uri.EscapeDataString(appId)}/branches/{Uri.EscapeDataString(branchAppId)}", cancellationToken);

    public Task<IReadOnlyList<AppServiceInventoryResponse>> GetAppServicesAsync(string appId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<AppServiceInventoryResponse>>(HttpMethod.Get, $"/api/domains/services?appId={Uri.EscapeDataString(appId)}", null, cancellationToken);

    public Task<IReadOnlyList<DomainBindingResponse>> GetDomainsAsync(string appId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<DomainBindingResponse>>(HttpMethod.Get, $"/api/domains?appId={Uri.EscapeDataString(appId)}", null, cancellationToken);

    public Task<IReadOnlyList<AppServiceSecretResponse>> GetSecretsAsync(string appId, string? serviceName, CancellationToken cancellationToken)
    {
        var path = $"/api/secrets?appId={Uri.EscapeDataString(appId)}";
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            path += $"&service={Uri.EscapeDataString(serviceName)}";
        }

        return SendAsync<IReadOnlyList<AppServiceSecretResponse>>(HttpMethod.Get, path, null, cancellationToken);
    }

    public Task ValidateDeploymentSecretsAsync(ValidateDeploymentSecretsRequest request, CancellationToken cancellationToken) =>
        SendNoContentAsync(HttpMethod.Post, "/api/deployments/validate-secrets", request, cancellationToken);

    public Task<AppServiceSecretResponse> SetSecretAsync(string appId, SetAppServiceSecretRequest request, CancellationToken cancellationToken) =>
        SendAsync<AppServiceSecretResponse>(HttpMethod.Post, $"/api/secrets?appId={Uri.EscapeDataString(appId)}", request, cancellationToken);

    public Task DeleteSecretAsync(string appId, string secretId, CancellationToken cancellationToken) =>
        SendNoContentAsync(HttpMethod.Delete, $"/api/secrets/{Uri.EscapeDataString(secretId)}?appId={Uri.EscapeDataString(appId)}", cancellationToken);

    public Task<DomainBindingResponse> CreateDomainAsync(string appId, CreateDomainBindingRequest request, CancellationToken cancellationToken) =>
        SendAsync<DomainBindingResponse>(HttpMethod.Post, $"/api/domains?appId={Uri.EscapeDataString(appId)}", request, cancellationToken);

    public Task<DomainBindingResponse> UpdateDomainAsync(string appId, string domainId, UpdateDomainBindingRequest request, CancellationToken cancellationToken) =>
        SendAsync<DomainBindingResponse>(HttpMethod.Patch, $"/api/domains/{Uri.EscapeDataString(domainId)}?appId={Uri.EscapeDataString(appId)}", request, cancellationToken);

    public Task DeleteDomainAsync(string appId, string domainId, CancellationToken cancellationToken) =>
        SendNoContentAsync(HttpMethod.Delete, $"/api/domains/{Uri.EscapeDataString(domainId)}?appId={Uri.EscapeDataString(appId)}", cancellationToken);

    public Task<AssetReleaseResponse> PublishAssetAsync(PublishAssetRequest request, CancellationToken ct) =>
        SendAsync<AssetReleaseResponse>(HttpMethod.Post, "/api/assets", request, ct);
    public Task<AssetReleaseResponse> GetAssetAsync(string id, CancellationToken ct) =>
        SendAsync<AssetReleaseResponse>(HttpMethod.Get, $"/api/assets/{Uri.EscapeDataString(id)}", null, ct);

    public Task<DeploymentCreateResponse> StartDeploymentPreparationAsync(StartDeploymentPreparationRequest request, CancellationToken ct) =>
        SendAsync<DeploymentCreateResponse>(HttpMethod.Post, "/api/deployments/preparations", request, ct);

    public Task UpdateDeploymentPreparationAsync(string id, UpdateDeploymentPreparationRequest request, CancellationToken ct) =>
        SendNoContentAsync(HttpMethod.Post, $"/api/deployments/preparations/{Uri.EscapeDataString(id)}", request, ct);

    public Task<DeploymentCreateResponse> CreateDeploymentAsync(CreateDeploymentRequest request, CancellationToken cancellationToken) =>
        SendAsync<DeploymentCreateResponse>(HttpMethod.Post, "/api/deployments", request, cancellationToken);

    public Task<DeploymentArtifactCreateResponse> CreateDeploymentArtifactAsync(CreateDeploymentArtifactRequest request, CancellationToken cancellationToken) =>
        SendAsync<DeploymentArtifactCreateResponse>(HttpMethod.Post, "/api/artifacts", request, cancellationToken);

    public async Task<DeploymentArtifactResponse> UploadDeploymentArtifactContentAsync(string artifactId, string uploadUrl, string filePath, string sha256, long sizeBytes, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(uploadUrl)
            ? $"/api/artifacts/{Uri.EscapeDataString(artifactId)}/file"
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

        return SendAsync<IReadOnlyList<DeploymentResponse>>(HttpMethod.Get, $"/api/deployments?{query}", null, cancellationToken);
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
        SendAsync<DeploymentResponse>(HttpMethod.Get, $"/api/deployments/{Uri.EscapeDataString(deploymentId)}", null, cancellationToken);

    public Task<DeploymentCancellationResponse> CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken) =>
        SendAsync<DeploymentCancellationResponse>(HttpMethod.Post, $"/api/deployments/{Uri.EscapeDataString(deploymentId)}/cancel", null, cancellationToken);

    public Task<DeploymentResponse> RefreshDeploymentAsync(string deploymentId, CancellationToken cancellationToken) =>
        SendAsync<DeploymentResponse>(HttpMethod.Post, $"/api/deployments/{Uri.EscapeDataString(deploymentId)}/refresh", null, cancellationToken);

    public Task<IReadOnlyList<DeploymentEventResponse>> GetDeploymentEventsAsync(string deploymentId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<DeploymentEventResponse>>(HttpMethod.Get, $"/api/deployments/{Uri.EscapeDataString(deploymentId)}/events", null, cancellationToken);

    public Task<IReadOnlyList<DeploymentLogResponse>> GetDeploymentLogsAsync(string deploymentId, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<DeploymentLogResponse>>(HttpMethod.Get, $"/api/deployments/{Uri.EscapeDataString(deploymentId)}/logs", null, cancellationToken);

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
        return SendAsync<IReadOnlyList<RuntimeLogResponse>>(HttpMethod.Get, $"/api/runtime/logs?{string.Join("&", query)}", null, cancellationToken);
    }

    public Task<CliLoginSessionCreateResponse> CreateCliLoginSessionAsync(CancellationToken cancellationToken) =>
        SendUnauthenticatedAsync<CliLoginSessionCreateResponse>(HttpMethod.Post, "/api/cli-login-sessions", null, cancellationToken);

    public Task<CliLoginSessionExchangeResponse?> ExchangeCliLoginSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        SendUnauthenticatedMaybeAcceptedAsync<CliLoginSessionExchangeResponse>(HttpMethod.Post, $"/api/cli-login-sessions/{Uri.EscapeDataString(sessionId)}/exchange", null, cancellationToken);

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

    private Task SendNoContentAsync(HttpMethod method, string pathAndQuery, CancellationToken cancellationToken) =>
        SendNoContentAsync(method, pathAndQuery, null, cancellationToken);

    private async Task SendNoContentAsync(HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken)
    {
        var token = _tokenStore.GetToken();
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
