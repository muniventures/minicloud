using System.Text.Json;
using Minicloud.Common.Contracts;

namespace Minicloud.Api.Auth;

public sealed class KratosUnavailableMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
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
                cancellationToken: context.RequestAborted);
        }
    }
}
