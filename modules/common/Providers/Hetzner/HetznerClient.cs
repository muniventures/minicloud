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

namespace Minicloud.Common.Providers.Hetzner;

public sealed partial class HetznerClient : IVpsProviderClient, IVpsProviderSshKeyClient
{
    private readonly HttpClient _httpClient;
    private readonly HetznerOptions _options;
    private readonly ILogger<HetznerClient>? _logger;

    public CloudProvider Provider => CloudProvider.Hetzner;

    [ActivatorUtilitiesConstructor]
    public HetznerClient(
        HttpClient httpClient,
        IOptions<HetznerOptions> options,
        ILogger<HetznerClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public HetznerClient(
        HttpClient httpClient,
        HetznerOptions options,
        ILogger<HetznerClient>? logger = null)
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
        var keys = new List<ProviderSshKeyInfo>();
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest(HttpMethod.Get, $"ssh_keys?per_page=50&page={page}");
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<HetznerSshKeyListResponse>(cancellationToken: cancellationToken);
            keys.AddRange((result?.SshKeys ?? []).Where(x => x.Id > 0)
                .Select(x => new ProviderSshKeyInfo(x.Id.ToString(), x.Name ?? string.Empty, x.PublicKey)));
            if (result?.Meta?.Pagination?.NextPage is null)
                return keys;
        }
    }

    public async Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default)
    {
        var payload = new HetznerCreateSshKeyPayload { Name = name, PublicKey = publicKey };
        using var request = CreateRequest(HttpMethod.Post, "ssh_keys");
        request.Content = JsonContent.Create(payload);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<HetznerSshKeyResponse>(cancellationToken: cancellationToken);
        if (result?.SshKey is not { Id: > 0 } key)
            throw new ProviderApiException("Hetzner API returned an empty SSH key in the registration response.", response.StatusCode, Provider);
        return new ProviderSshKeyInfo(key.Id.ToString(), key.Name ?? name, key.PublicKey);
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        using var request = CreateRequest(HttpMethod.Get, $"servers?name={Uri.EscapeDataString(serverName)}");
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);

        var listResponse = await response.Content.ReadFromJsonAsync<HetznerServerListResponse>(cancellationToken: cancellationToken);
        var server = listResponse?.Servers?.FirstOrDefault();
        if (server == null)
        {
            return null;
        }

        return MapServerInfo(server);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);

        var payload = new HetznerCreateServerPayload
        {
            Name = request.Name,
            ServerType = request.Plan,
            Location = request.Region,
            Image = string.IsNullOrWhiteSpace(request.Image) ? "ubuntu-24.04" : request.Image,
            UserData = request.UserDataCloudInit,
            StartAfterCreate = true,
            Labels = request.Labels != null ? new Dictionary<string, string>(request.Labels) : new Dictionary<string, string>()
        };

        if (request.SshKeyReferences.Count > 0)
        {
            payload.SshKeys = request.SshKeyReferences.Select(reference =>
                long.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                    ? id
                    : throw new ArgumentException("Hetzner SSH key references must be numeric IDs.", nameof(request))).ToArray();
        }

        using var httpRequest = CreateRequest(HttpMethod.Post, "servers");
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var createResponse = await response.Content.ReadFromJsonAsync<HetznerCreateServerResponse>(cancellationToken: cancellationToken);
        if (createResponse?.Server == null)
        {
            throw new ProviderApiException("Hetzner API returned empty server in create response", response.StatusCode, Provider);
        }

        _logger?.LogInformation("Created Hetzner server {ServerName} with ID {ServerId}", request.Name, createResponse.Server.Id);
        return MapServerInfo(createResponse.Server);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using var request = CreateRequest(HttpMethod.Get, $"servers/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "404");
        }

        await EnsureSuccessAsync(response, cancellationToken);

        var serverResponse = await response.Content.ReadFromJsonAsync<HetznerServerResponse>(cancellationToken: cancellationToken);
        var server = serverResponse?.Server;
        if (server == null)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "empty");
        }

        var status = server.Status ?? "unknown";
        var isRunning = string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);
        var ipv4 = server.PublicNet?.Ipv4?.Ip;
        var ipv6 = server.PublicNet?.Ipv6?.Ip;

        return new ProviderServerStatus(serverId, status, ipv4, ipv6, isRunning, RawStatus: status);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using var request = CreateRequest(HttpMethod.Delete, $"servers/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("Hetzner server {ServerId} not found when deleting; ignoring.", serverId);
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        _logger?.LogInformation("Deleted Hetzner server {ServerId}.", serverId);
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
                // Ignore read failures on error body
            }

            var redactedBody = TokenRedactor.Redact(body, _options.ApiToken);
            var message = $"Hetzner API request failed with HTTP {(int)response.StatusCode}.";
            _logger?.LogError("Hetzner API call failed: {Message}", message);
            throw new ProviderApiException(message, response.StatusCode, Provider, redactedBody);
        }
    }

    private static ProviderServerInfo MapServerInfo(HetznerServerDto server)
    {
        var id = server.Id.ToString();
        var name = server.Name ?? string.Empty;
        var ipv4 = server.PublicNet?.Ipv4?.Ip ?? string.Empty;
        var ipv6 = server.PublicNet?.Ipv6?.Ip;
        var status = server.Status ?? "unknown";

        return new ProviderServerInfo(id, name, ipv4, status, ipv6, server.Labels);
    }

    private sealed class HetznerCreateServerPayload
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("server_type")]
        public string ServerType { get; set; } = string.Empty;

        [JsonPropertyName("location")]
        public string Location { get; set; } = string.Empty;

        [JsonPropertyName("image")]
        public string Image { get; set; } = string.Empty;

        [JsonPropertyName("user_data")]
        public string UserData { get; set; } = string.Empty;

        [JsonPropertyName("start_after_create")]
        public bool StartAfterCreate { get; set; } = true;

        [JsonPropertyName("labels")]
        public Dictionary<string, string> Labels { get; set; } = new();

        [JsonPropertyName("ssh_keys")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long[]? SshKeys { get; set; }
    }

    private sealed class HetznerCreateSshKeyPayload
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("public_key")]
        public string PublicKey { get; set; } = string.Empty;
    }

    private sealed class HetznerSshKeyListResponse
    {
        [JsonPropertyName("ssh_keys")]
        public List<HetznerSshKeyDto>? SshKeys { get; set; }

        [JsonPropertyName("meta")]
        public HetznerMetaDto? Meta { get; set; }
    }

    private sealed class HetznerSshKeyResponse
    {
        [JsonPropertyName("ssh_key")]
        public HetznerSshKeyDto? SshKey { get; set; }
    }

    private sealed class HetznerSshKeyDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("public_key")]
        public string? PublicKey { get; set; }
    }

    private sealed class HetznerMetaDto
    {
        [JsonPropertyName("pagination")]
        public HetznerPaginationDto? Pagination { get; set; }
    }

    private sealed class HetznerPaginationDto
    {
        [JsonPropertyName("next_page")]
        public int? NextPage { get; set; }
    }

    private sealed class HetznerCreateServerResponse
    {
        [JsonPropertyName("server")]
        public HetznerServerDto? Server { get; set; }
    }

    private sealed class HetznerServerResponse
    {
        [JsonPropertyName("server")]
        public HetznerServerDto? Server { get; set; }
    }

    private sealed class HetznerServerListResponse
    {
        [JsonPropertyName("servers")]
        public List<HetznerServerDto>? Servers { get; set; }
    }

    private sealed class HetznerServerDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("public_net")]
        public HetznerPublicNetDto? PublicNet { get; set; }

        [JsonPropertyName("labels")]
        public Dictionary<string, string>? Labels { get; set; }
    }

    private sealed class HetznerPublicNetDto
    {
        [JsonPropertyName("ipv4")]
        public HetznerIpDto? Ipv4 { get; set; }

        [JsonPropertyName("ipv6")]
        public HetznerIpDto? Ipv6 { get; set; }
    }

    private sealed class HetznerIpDto
    {
        [JsonPropertyName("ip")]
        public string? Ip { get; set; }
    }
}
