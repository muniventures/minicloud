using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;
using System.Net.Http;

namespace Minicloud.Common.Providers.DigitalOcean;

public sealed class DigitalOceanClient : IVpsProviderClient, IVpsProviderSshKeyClient
{
    private readonly HttpClient _httpClient;
    private readonly DigitalOceanOptions _options;
    private readonly ILogger<DigitalOceanClient>? _logger;

    public CloudProvider Provider => CloudProvider.DigitalOcean;

    [ActivatorUtilitiesConstructor]
    public DigitalOceanClient(
        HttpClient httpClient,
        IOptions<DigitalOceanOptions> options,
        ILogger<DigitalOceanClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public DigitalOceanClient(
        HttpClient httpClient,
        DigitalOceanOptions options,
        ILogger<DigitalOceanClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (_httpClient.BaseAddress == null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default)
    {
        const int pageSize = 200;
        var keys = new List<ProviderSshKeyInfo>();
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest(HttpMethod.Get, $"account/keys?per_page={pageSize}&page={page}");
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<DigitalOceanSshKeyListResponse>(cancellationToken: cancellationToken);
            var pageKeys = result?.SshKeys ?? [];
            keys.AddRange(pageKeys.Where(x => x.Id > 0)
                .Select(x => new ProviderSshKeyInfo(x.Id.ToString(), x.Name ?? string.Empty, x.PublicKey)));
            if (pageKeys.Count < pageSize || (result?.Meta?.Total is int total && keys.Count >= total))
                return keys;
        }
    }

    public async Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "account/keys");
        request.Content = JsonContent.Create(new DigitalOceanCreateSshKeyPayload { Name = name, PublicKey = publicKey });
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<DigitalOceanSshKeyResponse>(cancellationToken: cancellationToken);
        if (result?.SshKey is not { Id: > 0 } key)
            throw new ProviderApiException("DigitalOcean API returned an empty SSH key in the registration response.", response.StatusCode, Provider);
        return new ProviderSshKeyInfo(key.Id.ToString(), key.Name ?? name, key.PublicKey);
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        using var request = CreateRequest(HttpMethod.Get, $"droplets?name={Uri.EscapeDataString(serverName)}");
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);

        var listResponse = await response.Content.ReadFromJsonAsync<DigitalOceanDropletListResponse>(cancellationToken: cancellationToken);
        var droplet = listResponse?.Droplets?.FirstOrDefault(d => string.Equals(d.Name, serverName, StringComparison.OrdinalIgnoreCase))
                      ?? listResponse?.Droplets?.FirstOrDefault();
        if (droplet == null)
        {
            return null;
        }

        return MapDropletInfo(droplet);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);

        var tags = new List<string> { "minicloud" };
        if (request.Tags != null)
        {
            tags.AddRange(request.Tags);
        }

        var payload = new DigitalOceanCreateDropletPayload
        {
            Name = request.Name,
            Region = request.Region,
            Size = request.Plan,
            Image = string.IsNullOrWhiteSpace(request.Image) ? "ubuntu-24-04-x64" : request.Image,
            UserData = request.UserDataCloudInit,
            Tags = tags.Distinct().ToList()
        };

        if (request.SshKeyReferences.Count > 0)
        {
            payload.SshKeys = request.SshKeyReferences.Select(reference =>
                long.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                    ? (object)id
                    : reference).ToArray();
        }

        using var httpRequest = CreateRequest(HttpMethod.Post, "droplets");
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var createResponse = await response.Content.ReadFromJsonAsync<DigitalOceanDropletResponse>(cancellationToken: cancellationToken);
        if (createResponse?.Droplet == null)
        {
            throw new ProviderApiException("DigitalOcean API returned empty droplet in create response", response.StatusCode, Provider);
        }

        var droplet = createResponse.Droplet;
        _logger?.LogInformation("Created DigitalOcean Droplet {DropletName} with ID {DropletId}", request.Name, droplet.Id);

        if (!string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            try
            {
                await AssignToProjectAsync(droplet.Id.ToString(), _options.ProjectId, cancellationToken);
            }
            catch (ProviderApiException ex)
            {
                // Assignment rejection cannot prove the preceding allocation was rejected.
                throw new ProviderApiException(
                    $"DigitalOcean droplet {droplet.Id} was created, but project assignment failed. Reconcile the existing droplet before retrying.",
                    provider: Provider, innerException: ex);
            }
        }

        return MapDropletInfo(droplet);
    }

    public async Task AssignToProjectAsync(string dropletId, string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dropletId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var resourceUrn = $"do:droplet:{dropletId}";
        var assignPayload = new DigitalOceanAssignResourcesPayload
        {
            Resources = [resourceUrn]
        };

        using var request = CreateRequest(HttpMethod.Post, $"projects/{Uri.EscapeDataString(projectId)}/resources");
        request.Content = JsonContent.Create(assignPayload);

        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        _logger?.LogInformation("Assigned Droplet {DropletId} to DigitalOcean Project {ProjectId}", dropletId, projectId);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using var request = CreateRequest(HttpMethod.Get, $"droplets/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "404");
        }

        await EnsureSuccessAsync(response, cancellationToken);

        var dropletResponse = await response.Content.ReadFromJsonAsync<DigitalOceanDropletResponse>(cancellationToken: cancellationToken);
        var droplet = dropletResponse?.Droplet;
        if (droplet == null)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "empty");
        }

        var status = droplet.Status ?? "unknown";
        var isRunning = string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);

        var publicV4 = droplet.Networks?.V4?.FirstOrDefault(n => string.Equals(n.Type, "public", StringComparison.OrdinalIgnoreCase))?.IpAddress;
        var publicV6 = droplet.Networks?.V6?.FirstOrDefault(n => string.Equals(n.Type, "public", StringComparison.OrdinalIgnoreCase))?.IpAddress;

        return new ProviderServerStatus(serverId, status, publicV4, publicV6, isRunning, RawStatus: status);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using var request = CreateRequest(HttpMethod.Delete, $"droplets/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("DigitalOcean droplet {ServerId} not found when deleting; ignoring.", serverId);
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        _logger?.LogInformation("Deleted DigitalOcean droplet {ServerId}.", serverId);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
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

            var redactedBody = TokenRedactor.Redact(body, _options.ApiToken);
            var message = $"DigitalOcean API request failed with HTTP {(int)response.StatusCode}.";
            _logger?.LogError("DigitalOcean API call failed: {Message}", message);
            throw new ProviderApiException(message, response.StatusCode, Provider, redactedBody);
        }
    }

    private static ProviderServerInfo MapDropletInfo(DigitalOceanDropletDto droplet)
    {
        var id = droplet.Id.ToString();
        var name = droplet.Name ?? string.Empty;
        var publicV4 = droplet.Networks?.V4?.FirstOrDefault(n => string.Equals(n.Type, "public", StringComparison.OrdinalIgnoreCase))?.IpAddress ?? string.Empty;
        var publicV6 = droplet.Networks?.V6?.FirstOrDefault(n => string.Equals(n.Type, "public", StringComparison.OrdinalIgnoreCase))?.IpAddress;
        var status = droplet.Status ?? "unknown";

        return new ProviderServerInfo(id, name, publicV4, status, publicV6);
    }

    private sealed class DigitalOceanCreateDropletPayload
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public string Size { get; set; } = string.Empty;

        [JsonPropertyName("image")]
        public string Image { get; set; } = string.Empty;

        [JsonPropertyName("user_data")]
        public string UserData { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonPropertyName("ssh_keys")]
        public object[]? SshKeys { get; set; }
    }

    private sealed class DigitalOceanCreateSshKeyPayload
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("public_key")]
        public string PublicKey { get; set; } = string.Empty;
    }

    private sealed class DigitalOceanSshKeyListResponse
    {
        [JsonPropertyName("ssh_keys")]
        public List<DigitalOceanSshKeyDto>? SshKeys { get; set; }

        [JsonPropertyName("meta")]
        public DigitalOceanMetaDto? Meta { get; set; }
    }

    private sealed class DigitalOceanSshKeyResponse
    {
        [JsonPropertyName("ssh_key")]
        public DigitalOceanSshKeyDto? SshKey { get; set; }
    }

    private sealed class DigitalOceanSshKeyDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("public_key")]
        public string? PublicKey { get; set; }

        [JsonPropertyName("fingerprint")]
        public string? Fingerprint { get; set; }
    }

    private sealed class DigitalOceanMetaDto
    {
        [JsonPropertyName("total")]
        public int? Total { get; set; }
    }

    private sealed class DigitalOceanAssignResourcesPayload
    {
        [JsonPropertyName("resources")]
        public List<string> Resources { get; set; } = new();
    }

    private sealed class DigitalOceanDropletResponse
    {
        [JsonPropertyName("droplet")]
        public DigitalOceanDropletDto? Droplet { get; set; }
    }

    private sealed class DigitalOceanDropletListResponse
    {
        [JsonPropertyName("droplets")]
        public List<DigitalOceanDropletDto>? Droplets { get; set; }
    }

    private sealed class DigitalOceanDropletDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("networks")]
        public DigitalOceanNetworksDto? Networks { get; set; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }
    }

    private sealed class DigitalOceanNetworksDto
    {
        [JsonPropertyName("v4")]
        public List<DigitalOceanNetworkAddressDto>? V4 { get; set; }

        [JsonPropertyName("v6")]
        public List<DigitalOceanNetworkAddressDto>? V6 { get; set; }
    }

    private sealed class DigitalOceanNetworkAddressDto
    {
        [JsonPropertyName("ip_address")]
        public string? IpAddress { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }
}
