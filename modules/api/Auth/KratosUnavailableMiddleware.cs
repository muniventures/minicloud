using System.Text.Json;
using Minicloud.Common.Contracts;

namespace Minicloud.Api.Auth;

public sealed class KratosUnavailableMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/account"))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
        }
        try
        {
            await next(context);
        }
        catch (KratosUnavailableException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "no-store";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new ErrorResponse(new ErrorBody("auth_unavailable", "Identity service is temporarily unavailable.", [])),
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken: context.RequestAborted);
        }
    }
}
