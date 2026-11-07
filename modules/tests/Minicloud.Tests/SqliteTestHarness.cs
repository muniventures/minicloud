using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;

namespace Minicloud.Tests;

public sealed class SqliteTestHarness : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<MinicloudDbContext>? _options;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<MinicloudDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public MinicloudDbContext CreateDbContext()
    {
        if (_options is null)
        {
            throw new InvalidOperationException("SQLite test harness has not been initialized.");
        }

        return new MinicloudDbContext(_options);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
