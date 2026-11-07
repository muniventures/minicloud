using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Providers.Common;
using System.Net.Http;

namespace Minicloud.Common.Providers.Dns;

public sealed class CloudflareDnsClient : ICloudflareDnsClient
{
    private readonly HttpClient _httpClient;
    private readonly CloudflareOptions _options;
    private readonly IDnsResolver _dnsResolver;
    private readonly ILogger<CloudflareDnsClient>? _logger;

    [ActivatorUtilitiesConstructor]
    public CloudflareDnsClient(
        HttpClient httpClient,
        IOptions<CloudflareOptions> options,
        IDnsResolver? dnsResolver = null,
        ILogger<CloudflareDnsClient>? logger = null)
        : this(httpClient, options.Value, dnsResolver, logger)
    {
    }

    public CloudflareDnsClient(
        HttpClient httpClient,
        CloudflareOptions options,
        IDnsResolver? dnsResolver = null,
        ILogger<CloudflareDnsClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dnsResolver = dnsResolver ?? new SystemDnsResolver();
        _logger = logger;

        if (_httpClient.BaseAddress == null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task DeleteDnsRecordsAsync(string hostname, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ZoneId))
        {
            throw new InvalidOperationException("Cloudflare ZoneId must be configured.");
        }
        foreach (var type in new[] { "A", "AAAA" })
        {
            using var request = CreateRequest(HttpMethod.Get, $"zones/{_options.ZoneId}/dns_records?type={type}&name={Uri.EscapeDataString(hostname)}&per_page=100");
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var records = await response.Content.ReadFromJsonAsync<CloudflareDnsRecordListResponse>(cancellationToken: cancellationToken);
            if (records?.Success != true || records.Result is null || records.Result.Count >= 100)
            {
                throw new InvalidOperationException("Unable to verify DNS records for deletion.");
            }
            foreach (var record in records.Result)
            {
                if (!string.Equals(record.Name, hostname, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(record.Id))
                {
                    throw new InvalidOperationException("DNS record identity does not match the application.");
                }
                using var delete = CreateRequest(HttpMethod.Delete, $"zones/{_options.ZoneId}/dns_records/{record.Id}");
                using var deleted = await SendAsync(delete, cancellationToken);
                if (deleted.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    await EnsureSuccessAsync(deleted, cancellationToken);
                }
            }
        }
    }

    public async Task UpsertDnsRecordAsync(string hostname, string ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(ipAddress);

        if (string.IsNullOrWhiteSpace(_options.ZoneId))
        {
            throw new InvalidOperationException("Cloudflare ZoneId must be configured.");
        }

        var recordType = ipAddress.Contains(':') ? "AAAA" : "A";
        var existingRecord = await FindExistingRecordAsync(hostname, recordType, cancellationToken);

        var payload = new CloudflareDnsRecordPayload
        {
            Type = recordType,
            Name = hostname,
            Content = ipAddress,
            Ttl = _options.Ttl,
            Proxied = _options.Proxied
        };

        if (existingRecord is not null &&
            string.Equals(existingRecord.Type, payload.Type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existingRecord.Content, payload.Content, StringComparison.OrdinalIgnoreCase) &&
            existingRecord.Ttl == payload.Ttl && existingRecord.Proxied == payload.Proxied)
        {
            _logger?.LogDebug("DNS record for {Hostname} already matches desired state", hostname);
        }
        else if (!string.IsNullOrEmpty(existingRecord?.Id))
        {
            _logger?.LogInformation("Updating DNS {RecordType} record for {Hostname} -> {IpAddress}", recordType, hostname, ipAddress);
            using var putRequest = CreateRequest(HttpMethod.Put, $"zones/{_options.ZoneId}/dns_records/{existingRecord.Id}");
            putRequest.Content = JsonContent.Create(payload);

            using var putResponse = await SendAsync(putRequest, cancellationToken);
            await EnsureSuccessAsync(putResponse, cancellationToken);
        }
        else
        {
            _logger?.LogInformation("Creating DNS {RecordType} record for {Hostname} -> {IpAddress}", recordType, hostname, ipAddress);
            using var postRequest = CreateRequest(HttpMethod.Post, $"zones/{_options.ZoneId}/dns_records");
            postRequest.Content = JsonContent.Create(payload);

            using var postResponse = await SendAsync(postRequest, cancellationToken);
            await EnsureSuccessAsync(postResponse, cancellationToken);
        }
    }

    public async Task WaitForPropagationAsync(
        string hostname,
        string expectedIp,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIp);

        var deadline = DateTimeOffset.UtcNow + timeout;
        var pollInterval = _options.PropagationPollInterval > TimeSpan.Zero
            ? _options.PropagationPollInterval
            : TimeSpan.FromSeconds(5);

        while (DateTimeOffset.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var resolvedIps = await _dnsResolver.ResolveIpAddressesAsync(hostname, cancellationToken);
                if (resolvedIps.Contains(expectedIp, StringComparer.OrdinalIgnoreCase))
                {
                    _logger?.LogInformation("Hostname {Hostname} successfully resolved to {ExpectedIp}", hostname, expectedIp);
                    return;
                }

                _logger?.LogDebug(
                    "Waiting for {Hostname} to resolve to {ExpectedIp}; current resolved IPs: {ResolvedIps}",
                    hostname,
                    expectedIp,
                    string.Join(", ", resolvedIps));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "DNS query for {Hostname} failed while waiting for propagation", hostname);
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var waitTime = remaining < pollInterval ? remaining : pollInterval;
            await Task.Delay(waitTime, cancellationToken);
        }

        throw new TimeoutException(
            $"Hostname '{hostname}' did not resolve to '{expectedIp}' within {timeout.TotalSeconds:F0}s.");
    }

    private async Task<CloudflareDnsRecordDto?> FindExistingRecordAsync(string hostname, string recordType, CancellationToken cancellationToken)
    {
        var path = $"zones/{_options.ZoneId}/dns_records?type={Uri.EscapeDataString(recordType)}&name={Uri.EscapeDataString(hostname)}";
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var listResponse = await response.Content.ReadFromJsonAsync<CloudflareDnsRecordListResponse>(cancellationToken: cancellationToken);
        return listResponse?.Result?.FirstOrDefault();
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
                // Ignore read failures
            }

            var redactedBody = TokenRedactor.Redact(body, _options.ApiToken);
            var message = $"Cloudflare API request failed with HTTP {(int)response.StatusCode}: {redactedBody}";
            _logger?.LogError("Cloudflare API call failed: {Message}", message);
            throw new ProviderApiException(message, response.StatusCode, null, redactedBody);
        }
    }

    private sealed class CloudflareDnsRecordPayload
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "A";

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("ttl")]
        public int Ttl { get; set; } = 120;

        [JsonPropertyName("proxied")]
        public bool Proxied { get; set; } = false;
    }

    private sealed class CloudflareDnsRecordListResponse
    {
        [JsonPropertyName("result")]
        public List<CloudflareDnsRecordDto>? Result { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }

    private sealed class CloudflareDnsRecordDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("ttl")]
        public int Ttl { get; set; }

        [JsonPropertyName("proxied")]
        public bool Proxied { get; set; }
    }
}
