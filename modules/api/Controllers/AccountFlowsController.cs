using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
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
    private const string ProviderPendingCookie = "__Host-minicloud-oidc";
    private const string ProviderContinuePath = "/sign-in/continue";

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
        if (!IsDashboardOrigin())
            return EndpointResults.Problem("csrf_invalid", "Request origin is invalid.", 403);
        var (body, problem) = await ReadBoundedJsonAsync(cancellationToken);
        if (problem is not null) return problem;
        using (body)
            return await Execute(kind, "submit", flow, null, body!.RootElement, cancellationToken);
    }

    /// <summary>
    /// Explicit user action for optional Google sign-in/linking: returns a validated provider URL for top-level navigation.
    /// </summary>
    [HttpPost("oidc")]
    [RequestSizeLimit(KratosBrowserFlowClient.BodyLimit)]
    public async Task<IResult> StartProvider(string kind, [FromQuery] string? flow, [FromQuery] string? returnTo,
        CancellationToken cancellationToken)
    {
        NoStore();
        if (!IsDashboardOrigin())
            return EndpointResults.Problem("csrf_invalid", "Request origin is invalid.", 403);
        if (Request.Headers.ContainsKey("Authorization"))
            return EndpointResults.Problem("unauthenticated", "A browser session is required.", 401);
        var principal = HttpContext.User.GetControlPlanePrincipal();
        if (!options.Value.GoogleEnabled || kind is not ("login" or "settings") || !Request.Host.HasValue ||
            !Guid.TryParseExact(flow, "D", out var flowId) || flowId == Guid.Empty)
            return EndpointResults.Problem("auth_flow_invalid", "The account form is invalid.", 400);
        if (kind == "settings" && principal is not { IsDashboardUser: true, BrowserSessionId: not null })
            return EndpointResults.Problem("unauthenticated", "A browser session is required.", 401);
        var (body, problem) = await ReadBoundedJsonAsync(cancellationToken);
        if (problem is not null) return problem;
        using (body)
        {
            try
            {
                var returnPath = new Uri(flows.ValidateReturnTo(returnTo ?? DefaultReturnPath(kind))).PathAndQuery;
                var redirect = await flows.StartProviderAsync(kind, flowId, body!.RootElement,
                    Request.Cookies.ToDictionary(x => x.Key, x => x.Value),
                    $"https://{Request.Host.Value}{KratosBrowserFlowClient.GoogleCallbackPath}", cancellationToken);
                WriteCookies(redirect.Cookies);
                Response.Cookies.Append(ProviderPendingCookie, string.Join('.', kind, flowId.ToString("N"),
                    StateHash(redirect.State), WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(returnPath))), new CookieOptions
                {
                    HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromMinutes(10)
                });
                return Results.Ok(new AccountProviderRedirectResponse(KratosBrowserFlowClient.GoogleProvider, redirect.RedirectUrl));
            }
            catch (AccountFlowException exception)
            {
                return EndpointResults.Problem(exception.Code, exception.Message, exception.StatusCode);
            }
        }
    }

    /// <summary>
    /// Fixed Google callback (Kratos <c>base_redirect_uri</c> + its callback path). Requires the pending cookie set by
    /// <see cref="StartProvider"/> and a matching state, then returns the browser to the dashboard continuation route.
    /// </summary>
    [HttpGet("/api/account/oidc/self-service/methods/oidc/callback/google")]
    public async Task<IResult> CompleteGoogle(CancellationToken cancellationToken)
    {
        NoStore();
        var pending = ReadPendingProvider();
        if (Request.Cookies.ContainsKey(ProviderPendingCookie))
            Response.Cookies.Delete(ProviderPendingCookie, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/"
            });
        var kind = pending?.Kind ?? "login";
        var returnPath = pending?.ReturnPath ?? DefaultReturnPath(kind);
        var state = SingleQuery("state");
        var code = SingleQuery("code");
        var error = SingleQuery("error");
        var principal = HttpContext.User.GetControlPlanePrincipal();
        if (!options.Value.GoogleEnabled || pending is null || Request.Headers.ContainsKey("Authorization") ||
            state is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(StateHash(state)), Encoding.ASCII.GetBytes(pending.Value.StateHash)) ||
            kind == "settings" && principal is not { IsDashboardUser: true, BrowserSessionId: not null })
            return ProviderContinue(kind, "failed", null, returnPath);
        try
        {
            var result = await flows.CompleteProviderAsync(pending.Value.FlowId, state, code, error,
                Request.Cookies.ToDictionary(x => x.Key, x => x.Value), cancellationToken);
            if (result.IdentityId is null || result.SessionId is null)
            {
                WriteCookies(result.Cookies);
                return ProviderContinue(kind, "failed", result.ContinueFlowId, returnPath);
            }
            var (session, _) = await AdmitAsync(kind, principal, result.IdentityId, result.SessionId, result.Cookies, cancellationToken);
            return ProviderContinue(kind, session is null ? "forbidden" : "success", null, returnPath);
        }
        catch (AccountFlowException)
        {
            return ProviderContinue(kind, "failed", null, returnPath);
        }
        catch (KratosUnavailableException)
        {
            return ProviderContinue(kind, "unavailable", null, returnPath);
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
            var (session, problem) = await AdmitAsync(kind, principal, result.IdentityId, result.SessionId, result.Cookies, cancellationToken);
            return problem ?? Results.Ok(new AccountFlowResponse(null, session));
        }
        catch (AccountFlowException exception)
        {
            return EndpointResults.Problem(exception.Code, exception.Message, exception.StatusCode);
        }
    }

    private async Task<(AccountSessionResponse? Session, IResult? Problem)> AdmitAsync(string kind, ControlPlanePrincipal? principal,
        string identityId, string sessionId, IReadOnlyDictionary<string, string> cookies, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.KratosIdentityId == identityId, cancellationToken);
        if (user is null || kind == "settings" && user.Id != principal?.UserId)
            return (null, EndpointResults.Problem("forbidden", "This identity is not admitted to this instance.", 403));
        if (kind is "settings" or "recovery")
        {
            var previous = await db.BrowserSessions.AsNoTracking()
                .Where(x => x.UserId == user.Id && x.RevokedAt == null)
                .Select(x => x.KratosSessionId).Distinct().ToListAsync(cancellationToken);
            await sessions.RevokeUserSessionsAsync(user.Id, cancellationToken);
            await grafanaSessions.RevokeUserSessionsAsync(user.Id, cancellationToken);
            foreach (var upstreamId in previous.Where(x => x != sessionId))
                await kratosSessions.RevokeSessionAsync(upstreamId, cancellationToken);
        }
        else if (Request.Cookies.TryGetValue("__Host-minicloud-session", out var oldCookie))
        {
            await sessions.RevokeAsync(oldCookie, cancellationToken);
        }
        var created = await sessions.CreateOrRotateAsync(user.Id, identityId, sessionId, cancellationToken);
        WriteCookies(cookies);
        Response.Cookies.Append("__Host-minicloud-session", created.Cookie, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/",
            Expires = created.Session.ExpiresAt
        });
        return (new AccountSessionResponse(user.Id, created.Session.ExpiresAt, created.Session.IdleExpiresAt, created.CsrfToken), null);
    }

    private bool IsDashboardOrigin()
    {
        var origin = Request.Headers.Origin;
        return origin.Count == 1 && origins.IsAllowed(origin.ToString()) &&
            string.Equals(origin.ToString(), options.Value.DashboardOrigin, StringComparison.Ordinal);
    }

    private async Task<(JsonDocument? Body, IResult? Problem)> ReadBoundedJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Bound even chunked requests before parsing; no request fields enter logs/errors.
            await using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > KratosBrowserFlowClient.BodyLimit)
                    return (null, EndpointResults.Problem("auth_flow_invalid", "The account form is too large.", 413));
                buffer.Write(chunk, 0, read);
            }
            return (JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 }), null);
        }
        catch (JsonException)
        {
            return (null, EndpointResults.Problem("auth_flow_invalid", "The account form is invalid.", 400));
        }
    }

    private static string DefaultReturnPath(string kind) => kind == "settings" ? "/settings/account" : "/apps";

    private static string StateHash(string state) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    private string? SingleQuery(string name) =>
        Request.Query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;

    private (string Kind, Guid FlowId, string StateHash, string ReturnPath)? ReadPendingProvider()
    {
        if (!Request.Cookies.TryGetValue(ProviderPendingCookie, out var value) || value.Length > 4096) return null;
        var parts = value.Split('.');
        if (parts.Length != 4 || parts[0] is not ("login" or "settings") ||
            !Guid.TryParseExact(parts[1], "N", out var flowId) || flowId == Guid.Empty || parts[2].Length != 43)
            return null;
        try
        {
            var returnPath = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[3]));
            return (parts[0], flowId, parts[2], new Uri(flows.ValidateReturnTo(returnPath)).PathAndQuery);
        }
        catch (Exception exception) when (exception is FormatException or AccountFlowException or ArgumentException)
        {
            return null;
        }
    }

    private IResult ProviderContinue(string kind, string result, Guid? flowId, string returnPath)
    {
        var query = new Dictionary<string, string?>
        {
            ["result"] = result, ["kind"] = kind, ["returnTo"] = returnPath
        };
        if (flowId is not null) query["flow"] = flowId.Value.ToString("D");
        try
        {
            Response.Headers.Location = flows.ValidateReturnTo(QueryHelpers.AddQueryString(ProviderContinuePath, query));
        }
        catch (AccountFlowException)
        {
            return EndpointResults.Problem("auth_unavailable", "Identity service is temporarily unavailable.", 503);
        }
        return Results.StatusCode(StatusCodes.Status303SeeOther);
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
