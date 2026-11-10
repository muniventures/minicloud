using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Api.Providers;

namespace Minicloud.Api.Extensions;

public static class MinicloudApplicationExtensions
{
    public static async Task ConfigureMinicloudApiAsync(this WebApplication app)
    {
        var pathBase = app.Configuration["Http:PathBase"];
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            app.UsePathBase(pathBase);
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            if (db.Database.IsSqlite())
            {
                await SqliteDatabaseConfigurator.ConfigureDatabaseAsync(db);
            }

            if (app.Configuration.GetValue("Database:RunMigrationsOnStartup", app.Environment.IsDevelopment()))
            {
                await db.Database.MigrateAsync();
            }
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();
        app.UseCors("Dashboard");
        app.UseMiddleware<KratosUnavailableMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<BrowserRequestSecurityMiddleware>();
        app.UseAuthorization();
    }
}
