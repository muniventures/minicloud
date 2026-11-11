using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;

namespace Minicloud.Common.Services;

internal sealed class BootstrapLock : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly MinicloudDbContext db;
    private SemaphoreSlim? gate;
    private FileStream? file;
    private bool postgres;
    private bool gateHeld;

    private BootstrapLock(MinicloudDbContext db) => this.db = db;

    public static async Task<BootstrapLock> AcquireAsync(MinicloudDbContext db, CancellationToken ct)
    {
        var result = new BootstrapLock(db);
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(5577006791947779411)", ct);
                result.postgres = true;
            }
            else if (db.Database.IsSqlite())
            {
                var source = db.Database.GetDbConnection().DataSource;
                var key = source == ":memory:" || source.Length == 0 ? ":memory:" : Path.GetFullPath(source);
                result.gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1));
                await result.gate.WaitAsync(ct);
                result.gateHeld = true;
                if (key != ":memory:")
                {
                    // This process-independent sidecar lock survives commits, unlike SQLite's
                    // transaction lock. Never unlink it: waiters must lock the same inode.
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None
                    };
                    if (!OperatingSystem.IsWindows())
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        try { result.file = new FileStream(key + ".bootstrap.lock", options); break; }
                        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 11 or 32 or 33)
                        {
                            await Task.Delay(50, ct);
                        }
                    }
                }
            }
            else
                throw BootstrapException.Conflict();
            db.ChangeTracker.Clear();
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (postgres)
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(5577006791947779411)", CancellationToken.None);
        }
        finally
        {
            file?.Dispose();
            if (gateHeld) gate!.Release();
            await db.Database.CloseConnectionAsync();
        }
    }
}
