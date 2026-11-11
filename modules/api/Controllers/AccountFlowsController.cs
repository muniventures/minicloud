using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;

namespace Minicloud.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/account/flows/{kind}")]
public sealed class AccountFlowsController(
    KratosBrowserFlowClient flows,
    AccountOriginValidator origins,
    IOptions<KratosOptions> options,
    MinicloudDbContext db,
    BrowserSessionService sessions,
    GrafanaSessionService grafanaSessions,
    IKratosSessionValidator kratosSessions) : ControllerBase
{
    [HttpGet("browser")]
    public Task<IResult> Start(string kind, [FromQuery] string? returnTo, CancellationToken cancellationToken) =>
        Execute(kind, "start", null, returnTo, null, cancellationToken);

    [HttpGet]
    public Task<IResult> Read(string kind, [FromQuery] string? id, CancellationToken cancellationToken) =>
        Execute(kind, "read", id, null, null, cancellationToken);

    [HttpPost]
    [RequestSizeLimit(KratosBrowserFlowClient.BodyLimit)]
    public async Task<IResult> Submit(string kind, [FromQuery] string? flow, CancellationToken cancellationToken)
    {
        NoStore();
        var origin = Request.Headers.Origin;
        if (origin.Count != 1 || !origins.IsAllowed(origin.ToString()) ||
            !string.Equals(origin.ToString(), options.Value.DashboardOrigin, StringComparison.Ordinal))
            return EndpointResults.Problem("csrf_invalid", "Request origin is invalid.", 403);
        try
        {
            // Bound even chunked requests before parsing; no request fields enter logs/errors.
            await using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > KratosBrowserFlowClient.BodyLimit)
                    return EndpointResults.Problem("auth_flow_invalid", "The account form is too large.", 413);
                buffer.Write(chunk, 0, read);
            }
            using var body = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            return await Execute(kind, "submit", flow, null, body.RootElement, cancellationToken);
        }
        catch (JsonException)
        {
            return EndpointResults.Problem("auth_flow_invalid", "The account form is invalid.", 400);
        }
    }

    private async Task<IResult> Execute(string kind, string operation, string? id, string? returnTo,
        JsonElement? fields, CancellationToken cancellationToken)
    {
        NoStore();
        var principal = HttpContext.User.GetControlPlanePrincipal();
        if (Request.Headers.ContainsKey("Authorization"))
            return EndpointResults.Problem("unauthenticated", "A browser session is required.", 401);
        if (kind == "settings" && (principal is not { IsDashboardUser: true, BrowserSessionId: not null }))
            return EndpointResults.Problem("unauthenticated", "A browser session is required.", 401);
        if (operation != "start" && (!Guid.TryParseExact(id, "D", out _) || id == Guid.Empty.ToString()))
            return EndpointResults.Problem("auth_flow_invalid", "The account form is invalid.", 400);
        try
        {
            var result = await flows.SendAsync(kind, operation, id is null ? null : Guid.Parse(id), returnTo, fields,
                Request.Cookies.ToDictionary(x => x.Key, x => x.Value), cancellationToken);
            if (result.IdentityId is null || result.SessionId is null)
            {
                WriteCookies(result.Cookies);
                return Results.Ok(new AccountFlowResponse(result.Flow, null));
            }
            var user = await db.Users.SingleOrDefaultAsync(x => x.KratosIdentityId == result.IdentityId, cancellationToken);
            if (user is null || kind == "settings" && user.Id != principal?.UserId)
                return EndpointResults.Problem("forbidden", "This identity is not admitted to this instance.", 403);
            if (kind is "settings" or "recovery")
            {
                var previous = await db.BrowserSessions.AsNoTracking()
                    .Where(x => x.UserId == user.Id && x.RevokedAt == null)
                    .Select(x => x.KratosSessionId).Distinct().ToListAsync(cancellationToken);
                await sessions.RevokeUserSessionsAsync(user.Id, cancellationToken);
                await grafanaSessions.RevokeUserSessionsAsync(user.Id, cancellationToken);
                foreach (var upstreamId in previous.Where(x => x != result.SessionId))
                    await kratosSessions.RevokeSessionAsync(upstreamId, cancellationToken);
            }
            else if (Request.Cookies.TryGetValue("__Host-minicloud-session", out var oldCookie))
            {
                await sessions.RevokeAsync(oldCookie, cancellationToken);
            }
            var created = await sessions.CreateOrRotateAsync(user.Id, result.IdentityId, result.SessionId, cancellationToken);
            WriteCookies(result.Cookies);
            Response.Cookies.Append("__Host-minicloud-session", created.Cookie, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/",
                Expires = created.Session.ExpiresAt
            });
            return Results.Ok(new AccountFlowResponse(null, new AccountSessionResponse(
                user.Id, created.Session.ExpiresAt, created.Session.IdleExpiresAt, created.CsrfToken)));
        }
        catch (AccountFlowException exception)
        {
            return EndpointResults.Problem(exception.Code, exception.Message, exception.StatusCode);
        }
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    private void WriteCookies(IReadOnlyDictionary<string, string> cookies)
    {
        foreach (var cookie in cookies)
        {
            var cookieOptions = new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/" };
            if (cookie.Value.Length == 0) Response.Cookies.Delete(cookie.Key, cookieOptions);
            else Response.Cookies.Append(cookie.Key, cookie.Value, cookieOptions);
        }
    }
}
