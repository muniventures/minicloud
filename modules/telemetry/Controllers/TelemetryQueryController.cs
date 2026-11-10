using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using Minicloud.Telemetry.Services;

namespace Minicloud.Telemetry.Controllers;

[ApiController]
[Route("api/logs")]
public sealed class TelemetryQueryController(
    TelemetryQueryService queries,
    TimeProvider timeProvider,
    IOptions<TelemetryOptions> options) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> Query(
        [FromQuery] string? appId,
        [FromQuery] string? start,
        [FromQuery] string? end,
        [FromQuery] string? service,
        [FromQuery] string? severity,
        [FromQuery(Name = "q")] string? text,
        [FromQuery] string? traceId,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { error = new { code = "unauthorized", message = "Missing or invalid authorization header." } }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var token = authHeader["Bearer ".Length..].Trim();
        if (!TelemetryReadToken.TryValidate(token, options.Value.AuthSigningKey, timeProvider, out var claim, out var error) || claim is null)
        {
            return Results.Json(new { error = new { code = error ?? "unauthorized", message = "Read token is invalid or expired." } }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!string.IsNullOrWhiteSpace(appId) && !string.Equals(appId.Trim(), claim.AppId, StringComparison.Ordinal))
        {
            return Results.Json(new { error = new { code = "forbidden", message = "Read token does not authorize access to this app." } }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!TryParseBound(start, out var parsedStart))
        {
            return Results.BadRequest(new { error = new { code = "validation_failed", message = "The start timestamp is not a valid ISO-8601 value." } });
        }

        if (!TryParseBound(end, out var parsedEnd))
        {
            return Results.BadRequest(new { error = new { code = "validation_failed", message = "The end timestamp is not a valid ISO-8601 value." } });
        }

        var outcome = await queries.QueryAsync(
            claim,
            new TelemetryLogFilters(parsedStart, parsedEnd, service, severity, text, traceId, limit),
            cancellationToken);

        return outcome switch
        {
            null => Results.NotFound(new { error = new { code = "telemetry_not_found", message = "The requested resource was not found." } }),
            TelemetryQueryInvalid invalid => Results.BadRequest(new { error = new { code = invalid.Code, message = invalid.Message } }),
            TelemetryQueryUnavailable unavailable => Retryable(unavailable),
            TelemetryQueryOk ok => Results.Ok(ok.Response),
            _ => Results.NotFound(new { error = new { code = "telemetry_not_found", message = "The requested resource was not found." } })
        };
    }

    private IResult Retryable(TelemetryQueryUnavailable unavailable)
    {
        if (unavailable.RetryAfter is not null)
        {
            Response.Headers.RetryAfter = Math.Clamp(
                (int)Math.Ceiling(unavailable.RetryAfter.Value.TotalSeconds), 1, 300)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var status = unavailable.Code == "query_rate_limited"
            ? StatusCodes.Status429TooManyRequests
            : StatusCodes.Status503ServiceUnavailable;
        return Results.Json(
            new { error = new { code = unavailable.Code, message = unavailable.Message } },
            statusCode: status);
    }

    private static bool TryParseBound(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(value, out var instant))
        {
            parsed = instant;
            return true;
        }

        return false;
    }
}
