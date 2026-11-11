// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Minicloud.Common.Data;

namespace Minicloud.Api.Services;

public static class DatabaseMigrationCommand
{
    public static MinicloudDatabaseOptions ResolveOptions(IConfiguration configuration, string environmentName) =>
        new()
        {
            Provider = configuration["Database:Provider"] ?? (environmentName == "Development" ? "Sqlite" : "Postgres"),
            MinicloudConnectionString = configuration.GetConnectionString("Minicloud"),
            DefaultConnectionString = configuration.GetConnectionString("DefaultConnection"),
            DatabaseConnectionString = configuration.GetConnectionString("Database"),
            DatabaseUrl = configuration["DATABASE_URL"],
            PasswordFile = configuration["Database:PasswordFile"]
        };

    public static async Task RunAsync(
        IConfiguration configuration,
        string environmentName,
        CancellationToken cancellationToken = default)
    {
        var options = ResolveOptions(configuration, environmentName);
        var connectionString = MinicloudDatabase.ResolveConnectionString(options);
        var dbOptions = new DbContextOptionsBuilder<MinicloudDbContext>();
        MinicloudDatabase.ConfigureDbContext(dbOptions, options.Provider, connectionString);
        await using var db = new MinicloudDbContext(dbOptions.Options);
        if (db.Database.IsSqlite())
        {
            await SqliteDatabaseConfigurator.ConfigureDatabaseAsync(db);
        }

        await db.Database.MigrateAsync(cancellationToken);
    }
}
