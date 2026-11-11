using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using System.Net.Http;

namespace Minicloud.Common.Services;

public sealed class OpenBaoSecretStore(HttpClient httpClient, IOptions<OpenBaoSecretStoreOptions> options) : ISecretStore
{
    public const string ProviderName = "openbao";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OpenBaoSecretStoreOptions _options = options.Value;

    public async Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken)
    {
        var path = request.ProviderAccountId is null
            ? PathFor(request.OrganizationId, request.AppId, request.ServiceName, request.Name)
            : ProviderPathFor(_options.EnvironmentPrefix, request.OrganizationId, request.ProviderAccountId,
                request.CredentialGeneration ?? throw new ArgumentException("A credential generation is required."));
        var response = await SendAsync(
            HttpMethod.Post,
            $"v1/{EscapePath(_options.MountPath)}/data/{path}",
            new { data = new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = request.Value } },
            cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<OpenBaoWriteResponse>(JsonOptions, cancellationToken);
        return new StoredSecretReference(ProviderName, path, body?.Data?.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            $"v1/{EscapePath(_options.MountPath)}/data/{reference.Path}" +
                (reference.Version is null ? "" : $"?version={Uri.EscapeDataString(reference.Version)}"),
            body: null,
            cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<OpenBaoReadResponse>(JsonOptions, cancellationToken);
        if (body?.Data?.Data is null || !body.Data.Data.TryGetValue("value", out var value))
        {
            throw new SecretStoreUnavailableException("OpenBao secret response did not include a value.");
        }

        return value;
    }

    public async Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Delete,
            $"v1/{EscapePath(_options.MountPath)}/metadata/{reference.Path}",
            body: null,
            cancellationToken);
        response.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, object? body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Address))
        {
            throw new SecretStoreUnavailableException("OpenBao is not configured.");
        }

        var token = await ReadTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(_options.Address.TrimEnd('/') + "/"), relativePath));
        request.Headers.Add("X-Vault-Token", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.Timeout);
            var response = await httpClient.SendAsync(request, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (method == HttpMethod.Delete) return response;
                response.Dispose();
                throw new SecretStoreUnavailableException("OpenBao secret was not found.");
            }

            var statusCode = response.StatusCode;
            response.Dispose();
            throw new SecretStoreUnavailableException($"OpenBao returned HTTP {(int)statusCode}.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SecretStoreUnavailableException("OpenBao request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SecretStoreUnavailableException("OpenBao request failed.", ex);
        }
    }

    private async Task<string> ReadTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.TokenFile))
        {
            string token;
            try
            {
                token = (await File.ReadAllTextAsync(_options.TokenFile, cancellationToken)).TrimEnd('\r', '\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                throw new SecretStoreUnavailableException("OpenBao token file could not be read.", ex);
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new SecretStoreUnavailableException("OpenBao token file is empty.");
            }

            return token;
        }

        if (!string.IsNullOrWhiteSpace(_options.Token))
        {
            return _options.Token;
        }

        throw new SecretStoreUnavailableException("OpenBao is not configured.");
    }

    private string PathFor(string organizationId, string appId, string serviceName, string name) =>
        string.Join(
            '/',
            "env",
            Segment(_options.EnvironmentPrefix),
            "orgs",
            Segment(organizationId),
            "apps",
            Segment(appId),
            "services",
            Segment(serviceName),
            Segment(name));

    private static string Segment(string value) => Uri.EscapeDataString(value);

    public static string ProviderPathFor(string environment, string organizationId, string providerAccountId, string generation) =>
        string.Join('/', "env", Segment(environment), "orgs", Segment(organizationId), "providers", Segment(providerAccountId), "credentials", Segment(generation));

    private static string EscapePath(string value) => string.Join('/', value.Split('/').Select(Segment));

    private sealed record OpenBaoWriteResponse(OpenBaoWriteData? Data);
    private sealed record OpenBaoWriteData(int? Version);
    private sealed record OpenBaoReadResponse(OpenBaoReadOuterData? Data);
    private sealed record OpenBaoReadOuterData(Dictionary<string, string>? Data);
}
