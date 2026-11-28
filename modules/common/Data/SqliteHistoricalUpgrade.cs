using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using static Selfrun.Common.Data.SqliteDatabaseInitializer;

namespace Selfrun.Common.Data;

internal static class SqliteHistoricalUpgrade
{
    // Based on the SQLite runtime model at feb8073 with Selfrun identifiers.
    private const string SourceFingerprint = "F9550969B6E4E82FF4819CE56F5DC0B5228B7585C29EE444469F7804D31DC76F";

    internal static bool IsSupported(IReadOnlyList<SchemaObject> schema) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            schema.Select(entry => $"{entry.Type}\0{entry.Name}\0{entry.Sql}"))))) == SourceFingerprint;

    internal static bool IsEnvironmentPowerTable(string table) => table is
        "environment_sleep_settings" or "environment_power_operations" or "environment_snapshots" or "environment_retained_addresses";

    internal static async Task AddEnvironmentPowerAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<SchemaObject> target, CancellationToken cancellationToken)
    {
        foreach (var entry in target.Where(entry => IsEnvironmentPowerTable(entry.TableName)).OrderBy(entry => entry.Type != "table"))
            await ExecuteAsync(connection, transaction, entry.Sql!, cancellationToken);
        await BackfillEnvironmentSleepSettingsAsync(connection, transaction, cancellationToken);
    }

    private static Task BackfillEnvironmentSleepSettingsAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO environment_sleep_settings (environment_id, revision, state_changed_at)
            SELECT id, 0, updated_at FROM apps;
            """, cancellationToken);

    internal static async Task<bool> ValidateHistoryAsync(
        SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> migrations, CancellationToken cancellationToken)
    {
        var known = migrations.ToArray();
        var applied = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            applied.Add(reader.GetString(0));
        if (!applied.SequenceEqual(known.Take(applied.Count)))
            throw new InvalidOperationException("SQLite migration history is unknown or incomplete; refusing to change existing data.");
        return applied.Count != 0;
    }

    internal static async Task UpgradeAsync(
        SqliteConnection connection, SqliteTransaction transaction, SqliteConnection reference,
        IReadOnlyList<SchemaObject> source, IReadOnlyList<SchemaObject> target, CancellationToken cancellationToken)
    {
        await EnsureForeignKeysAsync(connection, transaction, cancellationToken);
        var changed = new[] { "users", "apps", "deployments", "server_instances" };
        foreach (var entry in source.Where(entry => entry.Type == "table" && !changed.Contains(entry.Name)))
            if (!target.Contains(entry))
                throw new InvalidOperationException("SQLite upgrade requires an explicit conversion for a changed table; changes rolled back.");

        foreach (var table in target.Where(entry => entry.Type == "table" && !source.Any(old => old.Name == entry.Name)))
        {
            await ExecuteAsync(connection, transaction, table.Sql!, cancellationToken);
            await CopySeedAsync(reference, connection, transaction, table.Name, cancellationToken);
        }

        await BackfillNumbersAsync(connection, transaction, cancellationToken);
        await RebuildAsync(connection, transaction, reference, target, "users",
            new Dictionary<string, string> { ["kratos_identity_id"] = "NULL", ["keep_signed_in"] = "0" },
            ["firebase_uid"], cancellationToken);
        await RebuildAsync(connection, transaction, reference, target, "apps",
            new Dictionary<string, string>
            {
                ["ssh_access_mode"] = "'none'",
                ["ssh_provider_account_id"] = "NULL",
                ["ssh_key_id"] = "NULL",
                ["ssh_key_name"] = "NULL",
                ["ssh_public_key"] = "NULL",
                ["ssh_fingerprint"] = "NULL",
                ["ssh_key_type"] = "NULL",
                ["ssh_registration_state"] = "'not_registered'",
                ["ssh_revision"] = "0",
                ["ssh_selected_by"] = "NULL",
                ["ssh_selected_at"] = "NULL"
            }, [], cancellationToken);
        await RebuildAsync(connection, transaction, reference, target, "deployments",
            new Dictionary<string, string>
            {
                ["ssh_selection_revision"] = "NULL",
                ["ssh_selection_mode"] = "NULL",
                ["ssh_key_id"] = "NULL",
                ["ssh_key_name"] = "NULL",
                ["ssh_public_key"] = "NULL",
                ["ssh_fingerprint"] = "NULL",
                ["ssh_key_type"] = "NULL",
                ["ssh_provider_account_id"] = "NULL",
                ["ssh_credential_generation"] = "NULL",
                ["ssh_selecting_actor"] = "NULL",
                ["ssh_provider_references_json"] = "NULL",
                ["ssh_registration_state"] = "NULL",
                ["ssh_snapshot_taken_at"] = "NULL",
                ["number"] = """(SELECT number FROM "__sqlite_upgrade_numbers" WHERE id = deployments.id)"""
            }, [], cancellationToken);
        await RebuildAsync(connection, transaction, reference, target, "server_instances",
            new Dictionary<string, string>
            {
                ["host_key_fingerprint"] = "NULL",
                ["host_key_verified_at"] = "NULL",
                ["host_key_enrollment_secret"] = "NULL"
            }, [], cancellationToken);
        await ExecuteAsync(connection, transaction, """DROP TABLE "__sqlite_upgrade_numbers";""", cancellationToken);
        await BackfillEnvironmentSleepSettingsAsync(connection, transaction, cancellationToken);

        foreach (var index in target.Where(entry => entry.Type == "index"))
        {
            // Rebuilt tables lost their indexes; unchanged tables retain their exact source indexes.
            if (!source.Contains(index) || changed.Any(table => index.Sql!.Contains($"ON \"{table}\"", StringComparison.Ordinal)))
                await ExecuteAsync(connection, transaction, index.Sql!, cancellationToken);
        }
    }

    private static async Task RebuildAsync(
        SqliteConnection connection, SqliteTransaction transaction, SqliteConnection reference,
        IReadOnlyList<SchemaObject> target, string table, IReadOnlyDictionary<string, string> added,
        string[] removed, CancellationToken cancellationToken)
    {
        var oldColumns = await ColumnsAsync(connection, transaction, table, cancellationToken);
        var columns = await ColumnsAsync(reference, null, table, cancellationToken);
        if (!oldColumns.Except(removed).Concat(added.Keys).ToHashSet(StringComparer.Ordinal).SetEquals(columns) ||
            removed.Any(column => !oldColumns.Contains(column)) || added.Keys.Any(oldColumns.Contains))
            throw new InvalidOperationException("SQLite upgrade column conversion is incomplete; changes rolled back.");

        var temporary = $"__sqlite_upgrade_{table}";
        var create = target.Single(entry => entry.Type == "table" && entry.Name == table).Sql!;
        await ExecuteAsync(connection, transaction,
            create.Replace($"CREATE TABLE \"{table}\"", $"CREATE TABLE \"{temporary}\"", StringComparison.Ordinal), cancellationToken);
        var names = string.Join(", ", columns.Select(Quote));
        var values = string.Join(", ", columns.Select(column => added.TryGetValue(column, out var expression) ? expression : Quote(column)));
        await ExecuteAsync(connection, transaction,
            $"INSERT INTO {Quote(temporary)} ({names}) SELECT {values} FROM {Quote(table)};", cancellationToken);
        await ExecuteAsync(connection, transaction,
            $"DROP TABLE {Quote(table)}; ALTER TABLE {Quote(temporary)} RENAME TO {Quote(table)};", cancellationToken);
    }

    private static async Task<List<string>> ColumnsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task CopySeedAsync(
        SqliteConnection reference, SqliteConnection connection, SqliteTransaction transaction,
        string table, CancellationToken cancellationToken)
    {
        await using var read = reference.CreateCommand();
        read.CommandText = $"SELECT * FROM {Quote(table)}";
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            var names = Enumerable.Range(0, reader.FieldCount).Select(i => Quote(reader.GetName(i)));
            var parameters = Enumerable.Range(0, reader.FieldCount).Select(i => $"$p{i}");
            insert.CommandText = $"INSERT INTO {Quote(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", parameters)})";
            for (var i = 0; i < reader.FieldCount; i++)
                insert.Parameters.AddWithValue($"$p{i}", reader.GetValue(i));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task BackfillNumbersAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var deployments = new List<(string Id, string Organization, DateTimeOffset Created)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, organization_id, created_at FROM deployments";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var created))
                    throw new InvalidOperationException("SQLite deployment has an invalid creation timestamp; changes rolled back.");
                deployments.Add((reader.GetString(0), reader.GetString(1), created));
            }
        }
        await ExecuteAsync(connection, transaction,
            """CREATE TABLE "__sqlite_upgrade_numbers" (id TEXT PRIMARY KEY, number TEXT NOT NULL);""", cancellationToken);
        foreach (var group in deployments.GroupBy(row => (row.Organization, DateOnly.FromDateTime(row.Created.UtcDateTime))))
        {
            long number = 0;
            // Preserve full timestamp precision and PostgreSQL's UTF-8/C opaque-ID tie ordering.
            foreach (var row in group.OrderBy(row => row.Created.UtcTicks)
                         .ThenBy(row => Convert.ToHexString(Encoding.UTF8.GetBytes(row.Id)), StringComparer.Ordinal))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """INSERT INTO "__sqlite_upgrade_numbers" (id, number) VALUES ($id, $number)""";
                command.Parameters.AddWithValue("$id", row.Id);
                command.Parameters.AddWithValue("$number", group.Key.Item2.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                    + "." + (++number).ToString(CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var counter = connection.CreateCommand();
            counter.Transaction = transaction;
            counter.CommandText = "INSERT INTO deployment_number_counters (organization_id, utc_date, last_value) VALUES ($org, $date, $value)";
            counter.Parameters.AddWithValue("$org", group.Key.Organization);
            counter.Parameters.AddWithValue("$date", group.Key.Item2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            counter.Parameters.AddWithValue("$value", number);
            await counter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
