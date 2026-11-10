using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/environments/{appId}/logs")]
public sealed class TelemetryLogsController : ControllerBase
{
    private static IResult HiddenNotFound() =>
        EndpointResults.NotFound("telemetry_not_found", "The requested resource was not found.");

    [HttpGet]
    public async Task<IResult> Query(
        [FromRoute] string appId,
        [FromQuery] string? start,
        [FromQuery] string? end,
        [FromQuery] string? service,
        [FromQuery] string? severity,
        [FromQuery(Name = "q")] string? text,
        [FromQuery] string? traceId,
        [FromQuery] int? limit,
        [FromServices] TelemetryQueryService queries,
        CancellationToken cancellationToken)
    {
        if (!TryParseBound(start, out var parsedStart))
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody(
                "validation_failed", "The start timestamp is not a valid ISO-8601 value.", [])));
        }

        if (!TryParseBound(end, out var parsedEnd))
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody(
                "validation_failed", "The end timestamp is not a valid ISO-8601 value.", [])));
        }

        var principal = RequirePrincipal(HttpContext);
        var outcome = await queries.QueryAsync(
            principal,
            appId,
            new TelemetryLogFilters(parsedStart, parsedEnd, service, severity, text, traceId, limit),
            cancellationToken);

        return outcome switch
        {
            null => HiddenNotFound(),
            TelemetryQueryInvalid invalid => Results.BadRequest(new ErrorResponse(new ErrorBody(
                invalid.Code, invalid.Message, []))),
            TelemetryQueryUnavailable unavailable => Retryable(unavailable),
            TelemetryQueryOk ok => Results.Ok(ok.Response),
            _ => HiddenNotFound()
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
            new ErrorResponse(new ErrorBody(unavailable.Code, unavailable.Message, [])),
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
