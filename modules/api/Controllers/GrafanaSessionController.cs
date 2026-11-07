using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

/// <summary>
/// T20 Firebase-authorized Grafana launch/exchange/logout endpoints. The launch
/// endpoint provisions idempotently and returns a single-use 60-second code; the
/// POST-only exchange sets the Secure/HttpOnly/SameSite=Lax host-only proxy
/// cookie (15m idle / 1h absolute) after Origin validation.
/// </summary>
[ApiController]
public sealed class GrafanaSessionController(
    GrafanaSessionService sessions,
    IOptions<GrafanaOptions> grafanaOptions,
    IOptions<TelemetryClientOptions> telemetryOptions,
    IHttpClientFactory httpClients,
    MinicloudDbContext db,
    ControlPlaneAuthorization authorization,
    IHostEnvironment hosting) : ControllerBase
{
    private string QueryGatewayBaseUrl()
    {
        var configured = grafanaOptions.Value.QueryGatewayBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().TrimEnd('/');
        }

        var request = HttpContext.Request;
        return $"{request.Scheme}://{request.Host}/v1/grafana/query-gateway";
    }

    private static IResult HiddenNotFound() =>
        EndpointResults.NotFound("grafana_not_found", "The requested resource was not found.");

    [Authorize]
    [HttpPost("v1/apps/{appId}/grafana-session")]
    public async Task<IResult> Launch(
        [FromRoute] string appId,
        [FromBody] GrafanaLaunchRequest? request,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!principal.IsDashboardUser || principal.UserId is null)
        {
            return EndpointResults.Forbidden();
        }

        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null || !await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ReadApplicationLogs, cancellationToken))
        {
            return HiddenNotFound();
        }

        var destination = string.IsNullOrWhiteSpace(request?.Destination) ? "/explore" : request.Destination.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(destination, @"^/(explore|d/[A-Za-z0-9_\-]{1,64})(\?.*)?$"))
        {
            return Results.BadRequest(new ErrorResponse(new ErrorBody("validation_failed", "Destination must be /explore or /d/{uid}.", [])));
        }

        var telemetryUrl = telemetryOptions.Value.TelemetryServiceUrl?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(telemetryUrl))
        {
            try
            {
                var client = httpClients.CreateClient();
                var launchReq = new Minicloud.Common.Contracts.TelemetryLaunchCodeIssueRequest
                {
                    MinicloudUserId = principal.UserId,
                    OrganizationId = app.OrganizationId,
                    AppId = app.Id,
                    Destination = destination
                };
                using var msg = new HttpRequestMessage(HttpMethod.Post, $"{telemetryUrl}/internal/v1/telemetry/launch-code")
                {
                    Content = System.Net.Http.Json.JsonContent.Create(launchReq)
                };
                msg.Headers.TryAddWithoutValidation("X-Telemetry-Management-Key", telemetryOptions.Value.ManagementApiKey);
                using var resp = await client.SendAsync(msg, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    var launchResult = await resp.Content.ReadFromJsonAsync<Minicloud.Common.Contracts.TelemetryLaunchCodeIssueResponse>(cancellationToken);
                    if (launchResult is not null)
                    {
                        Response.Headers.CacheControl = "no-store";
                        Response.Headers.Pragma = "no-cache";
                        return Results.Ok(new GrafanaLaunchResponse(
                            launchResult.LaunchCode, launchResult.Destination, launchResult.ExpiresAt, launchResult.ExchangeUrl, launchResult.GrafanaBaseUrl));
                    }
                }
            }
            catch (Exception)
            {
                // Fallback to local session service if telemetry service is not available
            }
        }

        var outcome = await sessions.IssueLaunchCodeAsync(
            principal, appId, request?.Destination, QueryGatewayBaseUrl(), cancellationToken);
        if (!outcome.Ok)
        {
            return outcome.Error switch
            {
                "unknown_app" => HiddenNotFound(),
                "invalid_destination" => Results.BadRequest(new ErrorResponse(new ErrorBody("validation_failed", "Destination must be /explore or /d/{uid}.", []))),
                "firebase_required" => EndpointResults.Forbidden(),
                _ => EndpointResults.Problem("grafana_unavailable", "Log browsing is temporarily unavailable; retry with backoff.", StatusCodes.Status503ServiceUnavailable)
            };
        }

        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var exchangeUrl = !string.IsNullOrWhiteSpace(telemetryOptions.Value.AccessOrigin)
            ? $"{telemetryOptions.Value.AccessOrigin.TrimEnd('/')}/v1/grafana/exchange"
            : null;
        var grafanaBase = !string.IsNullOrWhiteSpace(telemetryOptions.Value.AccessOrigin)
            ? $"{telemetryOptions.Value.AccessOrigin.TrimEnd('/')}/grafana/"
            : null;
        return Results.Ok(new GrafanaLaunchResponse(outcome.Code!, outcome.Destination!, outcome.ExpiresAt!.Value, exchangeUrl, grafanaBase));
    }

    /// <summary>
    /// POST-only exchange: no GET/PUT/DELETE route exists, so other verbs 405.
    /// </summary>
    [HttpPost("v1/grafana/exchange")]
    public async Task<IResult> Exchange(
        [FromBody] GrafanaExchangeRequest request,
        CancellationToken cancellationToken)
    {
        if (!sessions.IsOriginAllowed(Request.Headers.Origin.ToString()))
        {
            return EndpointResults.Problem("grafana_origin_denied", "Request origin is not allowed.", StatusCodes.Status403Forbidden);
        }

        var outcome = await sessions.ExchangeLaunchCodeAsync(request.LaunchCode, QueryGatewayBaseUrl(), cancellationToken);
        if (!outcome.Ok)
        {
            return outcome.Error switch
            {
                "membership_revoked" => EndpointResults.Problem("grafana_forbidden", "Organization membership was revoked.", StatusCodes.Status403Forbidden),
                _ => EndpointResults.Problem("grafana_invalid_code", "The launch code is invalid or expired.", StatusCodes.Status400BadRequest)
            };
        }

        AppendProxyCookie(outcome.SessionToken!, outcome.ExpiresAt!.Value);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Ok(new GrafanaExchangeResponse(outcome.Destination!, outcome.ExpiresAt!.Value));
    }

    [HttpPost("v1/grafana/logout")]
    public async Task<IResult> Logout(CancellationToken cancellationToken)
    {
        if (!sessions.IsOriginAllowed(Request.Headers.Origin.ToString()))
        {
            return EndpointResults.Problem("grafana_origin_denied", "Request origin is not allowed.", StatusCodes.Status403Forbidden);
        }

        var token = Request.Cookies[grafanaOptions.Value.ProxyCookieName];
        if (!string.IsNullOrWhiteSpace(token))
        {
            await sessions.LogoutAsync(token, cancellationToken);
        }

        Response.Cookies.Delete(grafanaOptions.Value.ProxyCookieName, new CookieOptions
        {
            Path = "/",
            Secure = !hosting.IsDevelopment(),
            HttpOnly = true,
            SameSite = SameSiteMode.Lax
        });
        return Results.NoContent();
    }

    [Authorize]
    [HttpGet("v1/apps/{appId}/grafana-explore-url")]
    public async Task<IResult> ExploreUrl(
        [FromRoute] string appId,
        [FromServices] GrafanaProvisioningService provisioning,
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!principal.IsDashboardUser)
        {
            return EndpointResults.Forbidden();
        }

        var provisioned = await provisioning.EnsureProvisionedAsync(principal, appId, QueryGatewayBaseUrl(), cancellationToken);
        if (!provisioned.Ready)
        {
            return provisioned.UnavailableReason == "Unknown app."
                ? HiddenNotFound()
                : EndpointResults.Problem("grafana_unavailable", "Log browsing is temporarily unavailable; retry with backoff.", StatusCodes.Status503ServiceUnavailable);
        }

        var url = await sessions.BuildExploreUrlAsync(appId, cancellationToken);
        return url is null
            ? EndpointResults.Problem("grafana_unavailable", "Log browsing is temporarily unavailable; retry with backoff.", StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new GrafanaExploreUrlResponse(url));
    }

    private void AppendProxyCookie(string sessionToken, DateTimeOffset expiresAt)
    {
        Response.Cookies.Append(grafanaOptions.Value.ProxyCookieName, sessionToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = !hosting.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expiresAt,
            IsEssential = true
        });
    }
}
