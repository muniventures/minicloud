using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Selfrun.Common.Data;

public static class SqliteDatabaseInitializer
{
    public static async Task InitializeAsync(SelfrunDbContext db, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsSqlite() || db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("SQLite initialization requires a SQLite database.");

        // PostgreSQL migrations cannot be replayed on SQLite. Recognized SQLite source
        // schemas instead have a bounded, transactional forward conversion.
        var script = db.Database.GenerateCreateScript();
        await using var reference = new SqliteConnection("Data Source=:memory:");
        await reference.OpenAsync(cancellationToken);
        await ExecuteAsync(reference, null, script, cancellationToken);
        var expected = await ReadSchemaAsync(reference, null, cancellationToken);
        await ExecuteAsync(reference, null, db.GetService<IHistoryRepository>().GetCreateScript(), cancellationToken);
        var historySchema = (await ReadSchemaAsync(reference, null, cancellationToken))
            .Single(entry => entry.Name == "__EFMigrationsHistory");

        await db.Database.OpenConnectionAsync(cancellationToken);
        bool? foreignKeys = null;
        try
        {
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys";
            foreignKeys = Convert.ToInt64(await pragma.ExecuteScalarAsync(cancellationToken)) != 0;
            // Rebuilding the changed tables must not cascade-delete their children.
            await ExecuteAsync(connection, null, "PRAGMA foreign_keys = OFF;", cancellationToken);
            // Reserve the writer before inspecting state so concurrent initializers cannot
            // both observe an empty database. DDL and seed inserts commit together.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var actual = await ReadSchemaAsync(connection, transaction, cancellationToken);
            var history = actual.SingleOrDefault(entry => entry.Name == "__EFMigrationsHistory");
            var hasAppliedMigrations = false;
            if (history is not null)
            {
                if (history != historySchema)
                    throw new InvalidOperationException("SQLite migration history schema is unrecognized; refusing to change existing data.");
                hasAppliedMigrations = await SqliteHistoricalUpgrade.ValidateHistoryAsync(
                    connection, transaction, db.Database.GetMigrations(), cancellationToken);
                actual.Remove(history);
            }
            if (actual.Count == 0)
            {
                if (hasAppliedMigrations)
                    throw new InvalidOperationException("SQLite migration history has no matching schema; refusing to change existing data.");
                await ExecuteAsync(connection, transaction, script, cancellationToken);
            }
            else if (!actual.SequenceEqual(expected))
            {
                if (actual.SequenceEqual(expected.Where(entry => !SqliteHistoricalUpgrade.IsEnvironmentPowerTable(entry.TableName))))
                    await SqliteHistoricalUpgrade.AddEnvironmentPowerAsync(connection, transaction, expected, cancellationToken);
                else if (SqliteHistoricalUpgrade.IsSupported(actual))
                    await SqliteHistoricalUpgrade.UpgradeAsync(connection, transaction, reference, actual, expected, cancellationToken);
                else
                    throw new InvalidOperationException(
                        "SQLite schema is not a recognized upgrade prerequisite or the current EF-created schema; " +
                        "refusing to upgrade or rebuild existing data. Supported prerequisites: the SQLite schema " +
                        "immediately before environment power persistence and the Selfrun SQLite schema based on source feb8073.");

                var upgraded = await ReadSchemaAsync(connection, transaction, cancellationToken);
                upgraded.RemoveAll(entry => entry.Name == "__EFMigrationsHistory");
                if (!upgraded.SequenceEqual(expected))
                    throw new InvalidOperationException("SQLite upgrade did not produce the current schema; changes rolled back.");
            }

            await EnsureForeignKeysAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            try
            {
                if (foreignKeys.HasValue)
                    await ExecuteAsync(connection, null, foreignKeys.Value ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;",
                        CancellationToken.None);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    internal static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task EnsureForeignKeysAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("SQLite database contains invalid foreign keys; changes rolled back.");
    }

    private static async Task<List<SchemaObject>> ReadSchemaAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT type, name, sql, tbl_name FROM sqlite_schema
            WHERE name NOT GLOB 'sqlite_*'
            ORDER BY type, name
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var schema = new List<SchemaObject>();
        while (await reader.ReadAsync(cancellationToken))
            schema.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
        return schema;
    }

    internal sealed record SchemaObject(string Type, string Name, string? Sql, string TableName);
}
