using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Minicloud.Api.Auth;

public sealed class KratosSessionValidator(HttpClient client, IOptions<KratosOptions> options) : IKratosSessionValidator
{
    private readonly KratosOptions _options = options.Value;

    public async Task<bool> IsSessionActiveAsync(
        string sessionId,
        string identityId,
        CancellationToken cancellationToken)
    {
        if (!TryGetAdminUri(sessionId, out var uri))
        {
            throw new KratosUnavailableException();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var upstreamToken = timeout.Token;
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, upstreamToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 64 * 1024)
            {
                throw new KratosUnavailableException();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(upstreamToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, upstreamToken);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > 64 * 1024)
                {
                    throw new KratosUnavailableException();
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), upstreamToken);
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            var active = root.TryGetProperty("active", out var activeElement) &&
                activeElement.ValueKind == JsonValueKind.True;
            var returnedId = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var returnedIdentityId = root.TryGetProperty("identity", out var identityElement) &&
                identityElement.TryGetProperty("id", out var identityIdElement)
                    ? identityIdElement.GetString()
                    : null;
            return active &&
                string.Equals(returnedId, sessionId, StringComparison.Ordinal) &&
                string.Equals(returnedIdentityId, identityId, StringComparison.Ordinal);
        }
        catch (KratosUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            throw new KratosUnavailableException();
        }
    }

    public async Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!TryGetAdminUri(sessionId, out var uri))
        {
            throw new KratosUnavailableException();
        }

        try
        {
            using var response = await client.DeleteAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode &&
                response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                throw new KratosUnavailableException();
            }
        }
        catch (KratosUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new KratosUnavailableException();
        }
    }

    private bool TryGetAdminUri(string sessionId, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(_options.AdminBaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) ||
            string.IsNullOrWhiteSpace(sessionId) || sessionId.Contains('/') || sessionId.Contains('\\'))
        {
            return false;
        }

        uri = new Uri(baseUri.ToString().TrimEnd('/') + "/admin/sessions/" + Uri.EscapeDataString(sessionId));
        return true;
    }
}
