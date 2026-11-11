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

        if (!db.Database.IsNpgsql())
        {
            await db.Database.MigrateAsync(cancellationToken);
            return;
        }

        // EF's history-table lock does not cover first-run schema creation, so concurrent
        // one-shot migrators serialize on a session advisory lock held on this connection.
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(5577006791947779410)", cancellationToken);
            try
            {
                await RejectUnrecognizedStateAsync(db, cancellationToken);
                await db.Database.MigrateAsync(cancellationToken);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(5577006791947779410)", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    // Refuse to adopt a populated database that this release did not create, or one
    // already migrated by a newer release.
    public static async Task RejectUnrecognizedStateAsync(MinicloudDbContext db, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql() || !await db.Database.CanConnectAsync(cancellationToken))
        {
            return;
        }

        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    to_regclass('public."__EFMigrationsHistory"') IS NOT NULL,
                    EXISTS (
                        SELECT 1 FROM pg_catalog.pg_class c
                        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f'))
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var hasHistory = reader.GetBoolean(0);
            var hasObjects = reader.GetBoolean(1);
            if (!hasHistory && hasObjects)
            {
                throw new InvalidOperationException(
                    "The Minicloud database contains objects without migration history; refusing to adopt unknown populated state.");
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknown = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .Where(id => !known.Contains(id))
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"The Minicloud database has migrations unknown to this release ({string.Join(", ", unknown)}); refusing to downgrade.");
        }
    }
}
