using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Minicloud.Worker.Databases;

// Session locks survive EF transactions and serialize database mutations on the same host.
internal sealed class DatabaseOperationLease : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly CancellationTokenSource heartbeatStop = new();
    private readonly CancellationTokenSource lost = new();
    private Task heartbeat = Task.CompletedTask;
    public CancellationToken Lost => lost.Token;
    private DatabaseOperationLease(NpgsqlConnection connection) => this.connection = connection;

    internal static long LockKey(string resource)
    {
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("minicloud-database-host:" + resource)));
    }

    public static async Task<DatabaseOperationLease?> AcquireAsync(string connectionString, IEnumerable<string> resources, string id, string token, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var lease = new DatabaseOperationLease(connection);
        try
        {
            foreach (var resource in resources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
                command.Parameters.AddWithValue("key", LockKey(resource));
                if (!Equals(await command.ExecuteScalarAsync(ct), true))
                {
                    await lease.DisposeAsync();
                    return null;
                }
            }
            lease.heartbeat = lease.HeartbeatAsync(id, token);
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    private async Task HeartbeatAsync(string id, string token)
    {
        try
        {
            while (!heartbeatStop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), heartbeatStop.Token);
                await using var command = new NpgsqlCommand("UPDATE database_operations SET lease_expires_at = CURRENT_TIMESTAMP + interval '35 minutes' WHERE id = @id AND ownership_token = @token AND status = 'running'", connection);
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("token", token);
                if (await command.ExecuteNonQueryAsync(heartbeatStop.Token) != 1) { lost.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
        catch { lost.Cancel(); }
    }

    public async ValueTask DisposeAsync()
    {
        heartbeatStop.Cancel();
        await heartbeat;
        await connection.DisposeAsync();
        heartbeatStop.Dispose();
        lost.Dispose();
    }
}
