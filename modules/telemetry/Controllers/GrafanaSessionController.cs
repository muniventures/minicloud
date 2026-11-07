using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Minicloud.Telemetry.Services;

namespace Minicloud.Telemetry.Controllers;

public sealed record GrafanaExchangeRequest(string Code);

[ApiController]
[Route("v1/grafana")]
public sealed class GrafanaSessionController(
    GrafanaSessionService sessions,
    IOptions<TelemetryOptions> options) : ControllerBase
{
    [HttpPost("exchange")]
    public async Task<IResult> Exchange(
        [FromBody] GrafanaExchangeRequest request,
        CancellationToken cancellationToken)
    {
        var origin = Request.Headers.Origin.ToString();
        if (!sessions.IsOriginAllowed(origin))
        {
            return Results.Json(new { error = new { code = "forbidden_origin", message = "Origin is not allowed." } }, statusCode: StatusCodes.Status403Forbidden);
        }

        var outcome = await sessions.ExchangeLaunchCodeAsync(request.Code, cancellationToken);
        if (!outcome.Ok || outcome.SessionToken is null)
        {
            return Results.Json(
                new { error = new { code = outcome.Error ?? "invalid_code", message = "Launch code is invalid, expired, or already used." } },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = !string.Equals(options.Value.Environment, "development", StringComparison.OrdinalIgnoreCase),
            SameSite = SameSiteMode.Lax,
            Expires = outcome.ExpiresAt,
            Path = "/"
        };

        Response.Cookies.Append(options.Value.ProxyCookieName, outcome.SessionToken, cookieOptions);

        return Results.Ok(new
        {
            ok = true,
            destination = outcome.Destination ?? "/explore",
            expiresAt = outcome.ExpiresAt
        });
    }

    [HttpPost("logout")]
    public async Task<IResult> Logout(CancellationToken cancellationToken)
    {
        if (Request.Cookies.TryGetValue(options.Value.ProxyCookieName, out var sessionToken) && !string.IsNullOrWhiteSpace(sessionToken))
        {
            await sessions.LogoutAsync(sessionToken, cancellationToken);
        }

        Response.Cookies.Delete(options.Value.ProxyCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = !string.Equals(options.Value.Environment, "development", StringComparison.OrdinalIgnoreCase),
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });

        return Results.Ok(new { ok = true });
    }
}
