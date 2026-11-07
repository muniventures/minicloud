using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Maintenance;

namespace Minicloud.Worker.Tests;

public sealed class RuntimeLogRetentionTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;

    public RuntimeLogRetentionTests(WorkerSqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task IdleAppsLoseExpiredLogsWhileBoundaryAndRecentRecordsSurvive()
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var cutoff = now.AddDays(-2);
        await using (var db = _harness.CreateDbContext())
        {
            foreach (var (id, app, observed) in new[]
            {
                ("expired-a", "app-a", cutoff.AddTicks(-1)),
                ("expired-b", "app-b", now.AddDays(-10)),
                ("boundary", "app-a", cutoff),
                ("recent", "app-b", now.AddHours(-1))
            })
            {
                db.RuntimeLogRecords.Add(new RuntimeLogRecord
                {
                    Id = id, AppId = app, OrganizationId = "org", RuntimeServerId = "server",
                    Source = "service:api", Stream = "stdout", Cursor = id, Content = "log",
                    ObservedAt = observed, ReceivedAt = now
                });
            }
            await db.SaveChangesAsync();
        }
        var worker = new RuntimeLogRetentionWorker(_harness.CreateScopeFactory(), new FixedClock(now),
            NullLogger<RuntimeLogRetentionWorker>.Instance);
        await worker.PruneAsync(default);
        await worker.PruneAsync(default);
        await using var check = _harness.CreateDbContext();
        Assert.Equal(new[] { "boundary", "recent" }, await check.RuntimeLogRecords.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync());
        Assert.Equal(TimeSpan.FromDays(2), RuntimeIngestionLimits.LogRetention);
        Assert.Equal(TimeSpan.FromDays(2), TelemetryLimits.Retention);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
