using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

/// <summary>
/// T20 login/auth proxy in front of private Grafana. Every request must carry a
/// valid Minicloud proxy session cookie; caller auth-proxy headers, Authorization,
/// Grafana cookies, and org overrides are stripped and only the trusted identity
/// derived from the session is forwarded. Public login/basic-auth/admin routes,
/// datasource mutation, and direct datasource-proxy access are denied. Grafana
/// Set-Cookie headers are never passed back, so a Grafana cookie can never
/// restore access after logout or revocation. Unified-query envelopes
/// (/api/ds/query) are validated against the session's data source before
/// forwarding.
/// </summary>
[ApiController]
public sealed class GrafanaProxyController(
    GrafanaSessionService sessions,
    IHttpClientFactory httpClients,
    IOptions<GrafanaOptions> grafanaOptions,
    ILogger<GrafanaProxyController> logger) : ControllerBase
{
    private const long MaxProxyBodyBytes = 4 * 1024 * 1024;

    [HttpGet("grafana/{*path}")]
    [HttpPost("grafana/{*path}")]
    public async Task<IActionResult> Proxy(string? path, CancellationToken cancellationToken)
    {
        var cookieName = grafanaOptions.Value.ProxyCookieName;
        var sessionToken = Request.Cookies[cookieName];
        var session = await sessions.ValidateProxySessionAsync(sessionToken ?? string.Empty, cancellationToken);
        if (session is null)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, new { error = new { code = "grafana_unauthorized", message = "Grafana session is missing, expired, or revoked." } });
        }

        var grafanaPath = "/" + (path ?? string.Empty).TrimStart('/');
        var decision = GrafanaProxyGuard.DecideRoute(Request.Method, grafanaPath);
        if (decision == GrafanaProxyGuard.RouteDecision.Deny)
        {
            return StatusCode(StatusCodes.Status404NotFound, new { error = new { code = "grafana_not_found", message = "The requested resource was not found." } });
        }

        byte[]? body = null;
        string? bodyText = null;
        if (Request.ContentLength > MaxProxyBodyBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = new { code = "grafana_too_large", message = "Request body exceeds the allowed size." } });
        }

        if (decision == GrafanaProxyGuard.RouteDecision.DsQuery)
        {
            body = await ReadBoundedAsync(Request.Body, cancellationToken);
            if (body is null)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = new { code = "grafana_too_large", message = "Request body exceeds the allowed size." } });
            }

            bodyText = System.Text.Encoding.UTF8.GetString(body);
            if (!GrafanaProxyGuard.TryValidateDsQueryEnvelope(bodyText, session.DataSourceUid, out _))
            {
                return StatusCode(StatusCodes.Status404NotFound, new { error = new { code = "grafana_not_found", message = "The requested resource was not found." } });
            }
        }

        return await ForwardAsync(session, grafanaPath, body, bodyText is not null, cancellationToken);
    }

    private async Task<IActionResult> ForwardAsync(
        GrafanaSessionSnapshot session, string grafanaPath, byte[]? bufferedBody, bool hasBufferedBody, CancellationToken cancellationToken)
    {
        Uri endpoint;
        try
        {
            endpoint = new Uri(
                new Uri(grafanaOptions.Value.BaseAddress.TrimEnd('/') + "/"),
                grafanaPath.TrimStart('/') + Request.QueryString);
        }
        catch (UriFormatException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = new { code = "grafana_unavailable", message = "Log browsing is temporarily unavailable." } });
        }

        var incoming = Request.Headers
            .Where(h => !string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v ?? string.Empty)));
        var sanitized = GrafanaProxyGuard.StripUntrustedHeaders(incoming);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var client = httpClients.CreateClient();
            using var request = new HttpRequestMessage(
                Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get, endpoint);

            foreach (var (key, value) in sanitized.Where(x => !string.Equals(x.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)))
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }

            foreach (var (key, value) in GrafanaProxyGuard.TrustedIdentityHeaders(session.GrafanaLogin, email: null, displayName: null))
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }

            if (hasBufferedBody)
            {
                request.Content = new ByteArrayContent(bufferedBody!);
                if (!string.IsNullOrWhiteSpace(Request.ContentType))
                {
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType);
                }
            }
            else if (Request.ContentLength is > 0 || Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                var streamed = await ReadBoundedAsync(Request.Body, linked.Token);
                if (streamed is null)
                {
                    return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = new { code = "grafana_too_large", message = "Request body exceeds the allowed size." } });
                }

                request.Content = new ByteArrayContent(streamed);
                if (!string.IsNullOrWhiteSpace(Request.ContentType))
                {
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType);
                }
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var responseBody = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(linked.Token), linked.Token);
            if (responseBody is null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = new { code = "grafana_too_large", message = "Grafana response exceeded the allowed size." } });
            }

            Response.StatusCode = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString();
            return string.IsNullOrWhiteSpace(contentType) ? File(responseBody, "application/octet-stream") : File(responseBody, contentType);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Grafana proxy request timed out.");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = new { code = "grafana_timeout", message = "Grafana request timed out." } });
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Grafana proxy transport failure.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = new { code = "grafana_unavailable", message = "Log browsing is temporarily unavailable." } });
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(System.IO.Stream source, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var total = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxProxyBodyBytes)
            {
                return null;
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }
}
