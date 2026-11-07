using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;
using System.Net.Http;

namespace Minicloud.Common.Providers.Vultr;

public sealed class VultrClient : IVpsProviderClient
{
    private readonly HttpClient _httpClient;
    private readonly VultrOptions _options;
    private readonly ILogger<VultrClient>? _logger;
    private int? _resolvedOsId;

    public CloudProvider Provider => CloudProvider.Vultr;

    [ActivatorUtilitiesConstructor]
    public VultrClient(
        HttpClient httpClient,
        IOptions<VultrOptions> options,
        ILogger<VultrClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public VultrClient(
        HttpClient httpClient,
        VultrOptions options,
        ILogger<VultrClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _resolvedOsId = options.DefaultOsId;

        if (_httpClient.BaseAddress == null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        using var request = CreateRequest(HttpMethod.Get, $"instances?label={Uri.EscapeDataString(serverName)}");
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);

        var listResponse = await response.Content.ReadFromJsonAsync<VultrInstanceListResponse>(cancellationToken: cancellationToken);
        var instance = listResponse?.Instances?.FirstOrDefault(i => string.Equals(i.Label, serverName, StringComparison.OrdinalIgnoreCase))
                       ?? listResponse?.Instances?.FirstOrDefault();
        if (instance == null)
        {
            return null;
        }

        return MapInstanceInfo(instance);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);

        var osId = await ResolveOsIdAsync(cancellationToken);
        var base64UserData = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.UserDataCloudInit ?? string.Empty));

        var tags = new List<string> { "minicloud" };
        if (request.Tags != null)
        {
            tags.AddRange(request.Tags);
        }

        var payload = new VultrCreateInstancePayload
        {
            Region = request.Region,
            Plan = request.Plan,
            OsId = osId,
            Label = request.Name,
            Hostname = request.Hostname ?? request.Name,
            UserData = base64UserData,
            EnableIpv6 = true,
            ActivationEmail = false,
            Tags = tags.Distinct().ToList()
        };

        if (!string.IsNullOrWhiteSpace(request.SshKeyIdOrPublicKey))
        {
            payload.SshKeyId = [request.SshKeyIdOrPublicKey];
        }

        using var httpRequest = CreateRequest(HttpMethod.Post, "instances");
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var createResponse = await response.Content.ReadFromJsonAsync<VultrInstanceResponse>(cancellationToken: cancellationToken);
        if (createResponse?.Instance == null)
        {
            throw new ProviderApiException("Vultr API returned empty instance in create response", response.StatusCode, Provider);
        }

        _logger?.LogInformation("Created Vultr instance {InstanceLabel} with ID {InstanceId}", request.Name, createResponse.Instance.Id);
        return MapInstanceInfo(createResponse.Instance);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using var request = CreateRequest(HttpMethod.Get, $"instances/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "404");
        }

        await EnsureSuccessAsync(response, cancellationToken);

        var instanceResponse = await response.Content.ReadFromJsonAsync<VultrInstanceResponse>(cancellationToken: cancellationToken);
        var instance = instanceResponse?.Instance;
        if (instance == null)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "empty");
        }

        var status = instance.Status ?? "unknown";
        var powerStatus = instance.PowerStatus ?? "unknown";
        var isRunning = string.Equals(powerStatus, "running", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);

        var ipv4 = !string.IsNullOrWhiteSpace(instance.MainIp) && instance.MainIp != "0.0.0.0" ? instance.MainIp : null;
        var ipv6 = !string.IsNullOrWhiteSpace(instance.V6MainIp) ? instance.V6MainIp : null;

        return new ProviderServerStatus(serverId, status, ipv4, ipv6, isRunning, RawStatus: $"{status}/{powerStatus}");
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using var request = CreateRequest(HttpMethod.Delete, $"instances/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("Vultr instance {ServerId} not found when deleting; ignoring.", serverId);
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        _logger?.LogInformation("Deleted Vultr instance {ServerId}.", serverId);
    }

    public async Task<int> ResolveOsIdAsync(CancellationToken cancellationToken = default)
    {
        if (_resolvedOsId.HasValue && _resolvedOsId.Value > 0)
        {
            return _resolvedOsId.Value;
        }

        using var request = CreateRequest(HttpMethod.Get, "os");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var osResponse = await response.Content.ReadFromJsonAsync<VultrOsListResponse>(cancellationToken: cancellationToken);
        var ubuntu = osResponse?.Os?.FirstOrDefault(o =>
            string.Equals(o.Name, "Ubuntu 24.04 x64", StringComparison.OrdinalIgnoreCase));

        if (ubuntu != null && ubuntu.Id > 0)
        {
            _resolvedOsId = ubuntu.Id;
            return ubuntu.Id;
        }

        // Fallback to default Ubuntu 24.04 ID
        _resolvedOsId = 2284;
        return _resolvedOsId.Value;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = string.Empty;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch
            {
                // Ignore body read errors
            }

            var redactedBody = TokenRedactor.Redact(body, _options.ApiKey);
            var message = $"Vultr API request failed with HTTP {(int)response.StatusCode}: {redactedBody}";
            _logger?.LogError("Vultr API call failed: {Message}", message);
            throw new ProviderApiException(message, response.StatusCode, Provider, redactedBody);
        }
    }

    private static ProviderServerInfo MapInstanceInfo(VultrInstanceDto instance)
    {
        var id = instance.Id ?? string.Empty;
        var name = instance.Label ?? instance.Hostname ?? string.Empty;
        var ipv4 = !string.IsNullOrWhiteSpace(instance.MainIp) && instance.MainIp != "0.0.0.0" ? instance.MainIp : string.Empty;
        var ipv6 = !string.IsNullOrWhiteSpace(instance.V6MainIp) ? instance.V6MainIp : null;
        var status = instance.Status ?? "unknown";

        return new ProviderServerInfo(id, name, ipv4, status, ipv6);
    }

    private sealed class VultrCreateInstancePayload
    {
        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        [JsonPropertyName("plan")]
        public string Plan { get; set; } = string.Empty;

        [JsonPropertyName("os_id")]
        public int OsId { get; set; }

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("hostname")]
        public string Hostname { get; set; } = string.Empty;

        [JsonPropertyName("user_data")]
        public string UserData { get; set; } = string.Empty;

        [JsonPropertyName("enable_ipv6")]
        public bool EnableIpv6 { get; set; } = true;

        [JsonPropertyName("activation_email")]
        public bool ActivationEmail { get; set; } = false;

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonPropertyName("sshkey_id")]
        public string[]? SshKeyId { get; set; }
    }

    private sealed class VultrInstanceResponse
    {
        [JsonPropertyName("instance")]
        public VultrInstanceDto? Instance { get; set; }
    }

    private sealed class VultrInstanceListResponse
    {
        [JsonPropertyName("instances")]
        public List<VultrInstanceDto>? Instances { get; set; }
    }

    private sealed class VultrInstanceDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("hostname")]
        public string? Hostname { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("power_status")]
        public string? PowerStatus { get; set; }

        [JsonPropertyName("main_ip")]
        public string? MainIp { get; set; }

        [JsonPropertyName("v6_main_ip")]
        public string? V6MainIp { get; set; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }
    }

    private sealed class VultrOsListResponse
    {
        [JsonPropertyName("os")]
        public List<VultrOsDto>? Os { get; set; }
    }

    private sealed class VultrOsDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
