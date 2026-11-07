using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;

namespace Minicloud.Common.Data;

public static class SqliteDatabaseConfigurator
{
    public static string ConfigureConnectionString(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            DefaultTimeout = Math.Max(new SqliteConnectionStringBuilder(connectionString).DefaultTimeout, 30),
            Pooling = false
        };

        return builder.ToString();
    }

    public static async Task ConfigureDatabaseAsync(MinicloudDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            var connection = db.Database.GetDbConnection();
            await using var busyTimeoutCommand = connection.CreateCommand();
            busyTimeoutCommand.CommandText = "PRAGMA busy_timeout = 30000;";
            await busyTimeoutCommand.ExecuteNonQueryAsync();

            await using var journalModeCommand = connection.CreateCommand();
            journalModeCommand.CommandText = "PRAGMA journal_mode = WAL;";
            await journalModeCommand.ExecuteNonQueryAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
