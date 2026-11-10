using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Models;
using Minicloud.Worker.Pipeline.Steps;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class DeploymentLogWriterTests(WorkerSqliteTestHarness harness) : IClassFixture<WorkerSqliteTestHarness>
{
    [Theory]
    [InlineData("===== phase: verify_local_health =====")]
    [InlineData("TIMING phase=sftp_transfer outcome=succeeded")]
    [InlineData("ERROR: health_check_failed")]
    [InlineData("__MINICLOUD_DEPLOY_FAILURE__=health_check_failed")]
    public async Task PhaseBoundaryPersistsSparseOutput(string boundary)
    {
        var id = await SeedAsync();
        var writer = new DeploymentLogWriter(harness.CreateScopeFactory(), TimeProvider.System, id);
        await writer.AppendAsync("deploy", "last received output", default);
        await using (var before = harness.CreateDbContext())
        {
            Assert.Empty(await before.DeploymentLogs.Where(x => x.DeploymentId == id).ToListAsync());
        }
        await writer.AppendAsync("deploy", boundary, default);
        await using var db = harness.CreateDbContext();
        Assert.Equal(2, await db.DeploymentLogs.CountAsync(x => x.DeploymentId == id));
        Assert.Contains(await db.DeploymentLogs.Where(x => x.DeploymentId == id).Select(x => x.Content).ToListAsync(),
            x => x == "last received output");
    }

    [Fact]
    public async Task FailedPersistenceKeepsTheBatchForFinalFlush()
    {
        var id = await SeedAsync();
        var factory = new FailOnceScopeFactory(harness.CreateScopeFactory());
        var writer = new DeploymentLogWriter(factory, TimeProvider.System, id);
        await writer.AppendAsync("deploy", "received before failure", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.FlushAsync(default));
        await writer.FlushAsync(default);
        await writer.FlushAsync(default);
        await using var db = harness.CreateDbContext();
        Assert.Equal("received before failure", (await db.DeploymentLogs.SingleAsync(x => x.DeploymentId == id)).Content);
    }

    [Fact]
    public async Task ConcurrentAppendAndFlushPersistsEachRecordOnce()
    {
        var id = await SeedAsync();
        var writer = new DeploymentLogWriter(harness.CreateScopeFactory(), TimeProvider.System, id, 7);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            await writer.AppendAsync("deploy", "line-" + i, default);
            if (i % 3 == 0) { await writer.FlushAsync(default); }
        }));
        await writer.FlushAsync(default);
        await using var db = harness.CreateDbContext();
        var lines = await db.DeploymentLogs.Where(x => x.DeploymentId == id).Select(x => x.Content).ToListAsync();
        Assert.Equal(40, lines.Count);
        Assert.Equal(40, lines.Distinct().Count());
    }

    private async Task<string> SeedAsync()
    {
        var name = "log-" + Guid.NewGuid().ToString("N");
        var (org, app) = await harness.SeedOrgAndAppAsync(name);
        var id = "dep-" + name;
        await using var db = harness.CreateDbContext();
        db.Deployments.Add(new Deployment
        {
            Id = id, OrganizationId = org.Id, AppId = app.Id,
            Status = DeploymentStatus.Deploying, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class FailOnceScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private bool _failed;
        public IServiceScope CreateScope()
        {
            if (!_failed) { _failed = true; throw new InvalidOperationException("Synthetic persistence failure."); }
            return inner.CreateScope();
        }
    }
}
