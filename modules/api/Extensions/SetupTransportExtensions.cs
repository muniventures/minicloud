using System.Threading.RateLimiting;
using Minicloud.Api.Controllers;

namespace Minicloud.Api.Extensions;

public static class SetupTransportExtensions
{
    public static IServiceCollection AddSetupTransport(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // One installation-wide budget, not an attacker-controlled partition cache.
            options.AddPolicy("operator-setup", _ => RateLimitPartition.GetFixedWindowLimiter(
                "installation", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.OnRejected = async (context, _) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await EndpointResults.Problem("setup_rate_limited", "Too many setup attempts. Retry in a minute.", 429)
                    .ExecuteAsync(context.HttpContext);
            };
        });
        return services;
    }

    public static IApplicationBuilder UseSetupTransport(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/api/account/setup", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.Pragma = "no-cache";
                    return Task.CompletedTask;
                });
                if (!context.Request.IsHttps)
                {
                    await EndpointResults.Problem("setup_https_required", "Setup requires HTTPS.", 400).ExecuteAsync(context);
                    return;
                }
            }
            await next(context);
        });
}
