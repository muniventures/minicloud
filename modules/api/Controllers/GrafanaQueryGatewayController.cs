using Microsoft.AspNetCore.Mvc;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

/// <summary>
/// T20 private Loki query gateway. Authenticated ONLY by the app-bound internal
/// read credential held server-side in the Grafana data source; the single live
/// app is resolved from storage and its tenant is applied server-side. Unknown
/// operations fail with a non-disclosing 404; bound violations fail with 4xx;
/// upstream outages fail retryably.
/// </summary>
[ApiController]
public sealed class GrafanaQueryGatewayController(GrafanaQueryGateway gateway) : ControllerBase
{
    private const long MaxGatewayBodyBytes = 64 * 1024;

    [HttpGet("api/grafana/query-gateway/{*path}")]
    [HttpPost("api/grafana/query-gateway/{*path}")]
    public async Task<IActionResult> Query(string? path, CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaxGatewayBodyBytes)
        {
            return GatewayError(StatusCodes.Status413PayloadTooLarge, "query_too_large", "Query body exceeds the allowed size.", null);
        }

        byte[]? body = null;
        if (Request.ContentLength is > 0 || Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            body = await ReadBoundedAsync(Request.Body, cancellationToken);
            if (body is null)
            {
                return GatewayError(StatusCodes.Status413PayloadTooLarge, "query_too_large", "Query body exceeds the allowed size.", null);
            }
        }

        var authorizationValues = Request.Headers["Authorization"];
        var outcome = await gateway.ExecuteAsync(
            Request.Method,
            path ?? string.Empty,
            Request.QueryString.Value ?? string.Empty,
            Request.ContentType,
            body,
            authorizationValues.Count == 1 ? authorizationValues[0] : null,
            authorizationValues.Count,
            cancellationToken);

        if (outcome.Body is not null)
        {
            Response.StatusCode = outcome.StatusCode;
            return File(outcome.Body, outcome.ContentType ?? "application/json");
        }

        return outcome.StatusCode switch
        {
            401 => GatewayError(outcome.StatusCode, outcome.ErrorCode!, outcome.ErrorMessage!, null),
            404 => GatewayError(outcome.StatusCode, "query_not_found", "The requested resource was not found.", null),
            429 => GatewayError(outcome.StatusCode, outcome.ErrorCode!, outcome.ErrorMessage!, retryAfterSeconds: 5),
            503 or 504 => GatewayError(outcome.StatusCode, outcome.ErrorCode!, outcome.ErrorMessage!, retryAfterSeconds: 5),
            _ => GatewayError(outcome.StatusCode, outcome.ErrorCode ?? "query_invalid", outcome.ErrorMessage ?? "The query was rejected.", null)
        };
    }

    private IActionResult GatewayError(int statusCode, string code, string message, int? retryAfterSeconds)
    {
        if (retryAfterSeconds.HasValue)
        {
            Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return StatusCode(statusCode, new { error = new { code, message } });
    }

    private static async Task<byte[]?> ReadBoundedAsync(System.IO.Stream source, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var total = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxGatewayBodyBytes)
            {
                return null;
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }
}
