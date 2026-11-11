using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Route("api/account")]
public sealed class AccountController(
    BootstrapService bootstrap,
    BrowserSessionService sessions,
    GrafanaSessionService grafanaSessions,
    IKratosSessionValidator kratosSessions,
    IOptions<KratosOptions> kratosOptions,
    IConfiguration configuration) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("capabilities")]
    public async Task<IResult> GetCapabilities(CancellationToken cancellationToken)
    {
        NoStore();
        var installationId = configuration["Bootstrap:InstallationId"];
        string state;
        try
        {
            state = await GetBootstrapStateAsync(installationId, cancellationToken);
        }
        catch (BootstrapException exception) when (exception.Code == "auth_unavailable")
        {
            return EndpointResults.Problem("auth_unavailable", "Identity service is temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Ok(new AccountCapabilitiesResponse(
            Password: true,
            Google: kratosOptions.Value.GoogleEnabled,
            EmailRecovery: kratosOptions.Value.EmailRecoveryEnabled,
            BootstrapState: state));
    }

    [Authorize]
    [HttpGet("session")]
    public async Task<IResult> GetSession(CancellationToken cancellationToken)
    {
        NoStore();
        var principal = RequirePrincipal(HttpContext);
        var cookie = Request.Cookies["__Host-minicloud-session"];
        var sessionId = principal.BrowserSessionId ?? User.FindFirstValue("browser_session_id");
        if (!principal.IsDashboardUser || principal.UserId is null ||
            string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(sessionId))
        {
            return EndpointResults.Problem("unauthenticated", "A browser session is required.", StatusCodes.Status401Unauthorized);
        }

        var session = await sessions.FindByIdAsync(sessionId, cancellationToken);
        var csrfToken = session is null ? null : sessions.GetCsrfToken(session, cookie);
        if (session is null || session.UserId != principal.UserId ||
            session.RevokedAt is not null || csrfToken is null)
        {
            return EndpointResults.Problem("unauthenticated", "A browser session is required.", StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new AccountSessionResponse(
            session.UserId,
            session.ExpiresAt,
            session.IdleExpiresAt,
            csrfToken));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IResult> Logout(CancellationToken cancellationToken)
    {
        NoStore();
        var principal = RequirePrincipal(HttpContext);
        var cookie = Request.Cookies["__Host-minicloud-session"];
        if (!principal.IsDashboardUser || principal.UserId is null ||
            string.IsNullOrWhiteSpace(cookie) || principal.BrowserSessionId is null)
        {
            ClearAuthCookies();
            return EndpointResults.Problem("unauthenticated", "A browser session is required.", StatusCodes.Status401Unauthorized);
        }

        var session = await sessions.FindByIdAsync(principal.BrowserSessionId, cancellationToken);
        await sessions.RevokeByIdAsync(principal.BrowserSessionId, cancellationToken);
        await grafanaSessions.RevokeUserSessionsAsync(principal.UserId, cancellationToken);
        ClearAuthCookies();
        if (session is not null)
        {
            try
            {
                await kratosSessions.RevokeSessionAsync(session.KratosSessionId, cancellationToken);
            }
            catch (KratosUnavailableException)
            {
                return EndpointResults.Problem("auth_unavailable", "Identity service is temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
            }
        }

        return Results.NoContent();
    }

    private async Task<string> GetBootstrapStateAsync(string? installationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installationId))
        {
            return "blocked";
        }

        try
        {
            var status = await bootstrap.StatusAsync(installationId, cancellationToken);
            return status.State switch
            {
                "required" or "in_progress" => "required",
                "ready" => "ready",
                _ => "blocked"
            };
        }
        catch (BootstrapException exception) when (exception.Code == "setup_state_conflict")
        {
            return "blocked";
        }
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    private void ClearAuthCookies()
    {
        var names = Request.Cookies.Keys
            .Where(name => name.StartsWith("csrf_token_", StringComparison.Ordinal))
            .Append("__Host-minicloud-session")
            .Append("ory_kratos_session")
            .Distinct(StringComparer.Ordinal);
        foreach (var name in names)
        {
            Response.Cookies.Delete(name, new CookieOptions
            {
                Path = "/",
                Secure = true,
                HttpOnly = true,
                SameSite = SameSiteMode.Lax
            });
        }
    }
}
