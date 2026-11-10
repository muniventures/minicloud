using System.Security.Claims;
using System.Text.Json;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Api.Auth;

public sealed class BrowserRequestSecurityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        BrowserSessionService sessions,
        AccountOriginValidator origins)
    {
        if (IsUnsafe(context.Request.Method) &&
            string.Equals(context.User.FindFirstValue("auth_kind"), "browser_session", StringComparison.Ordinal) &&
            !IsInitialAccountFlow(context.Request.Path))
        {
            var sessionId = context.User.FindFirstValue("browser_session_id");
            var session = string.IsNullOrWhiteSpace(sessionId)
                ? null
                : await sessions.FindByIdAsync(sessionId, context.RequestAborted);
            var origin = context.Request.Headers.Origin;
            var csrf = context.Request.Headers["X-Minicloud-CSRF"].ToString();
            if (session is null || session.RevokedAt is not null ||
                !origins.IsAllowed(origin.Count == 1 ? origin.ToString() : null) ||
                !sessions.ValidateCsrf(session, csrf))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                context.Response.Headers.CacheControl = "no-store";
                await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    new ErrorResponse(new ErrorBody("csrf_invalid", "Request origin or CSRF token is invalid.", [])),
                    cancellationToken: context.RequestAborted);
                return;
            }
        }

        await next(context);
    }

    private static bool IsUnsafe(string method) =>
        !HttpMethods.IsGet(method) &&
        !HttpMethods.IsHead(method) &&
        !HttpMethods.IsOptions(method);

    private static bool IsInitialAccountFlow(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return segments.Length >= 4 &&
            segments[0] == "api" &&
            segments[1] == "account" &&
            segments[2] == "flows" &&
            (segments[3] == "login" || segments[3] == "recovery") ||
            segments.Length == 3 &&
            segments[0] == "api" &&
            segments[1] == "account" &&
            segments[2] == "setup";
    }
}
