using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Tests.DevelopmentWorkspaces;

// Last-hour VM resource history (#205).
public sealed partial class DevelopmentWorkspaceProcessorTests
{
    [Fact]
    public async Task HeartbeatRecordsNewestSamplePerMinuteIdempotentlyAndPreservesUnknownMetrics()
    {
        var (_, vm, _) = await Seed("session");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        runtime.Capabilities = ["resources-v1"];
        runtime.Resources = ResourceSample(vm.Id, 1, utilization: 12.5);

        Assert.True(await Processor().ProcessNextAsync(default));

        var recorded = await db.DevelopmentWorkspaceVmResourceSamples.AsNoTracking().SingleAsync();
        Assert.Equal((vm.WorkspaceId, vm.Id, 1L), (recorded.WorkspaceId, recorded.VmId, recorded.Generation));
        Assert.Equal(12.5, recorded.CpuUtilizationPercent);
        Assert.Equal((8L << 30, 6L << 30), (recorded.MemoryTotalBytes, recorded.MemoryAvailableBytes));
        Assert.Equal((40L << 30, 28L << 30), (recorded.WorkspaceDiskTotalBytes, recorded.WorkspaceDiskAvailableBytes));
        Assert.Equal(0, recorded.Minute.Second + recorded.Minute.Millisecond);
        Assert.Equal(recorded.Minute, recorded.ObservedAt.AddTicks(-(recorded.ObservedAt.UtcTicks % TimeSpan.TicksPerMinute)));

        var now = clock.GetUtcNow();
        var minute = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(-3);
        var first = HistorySample(vm, minute.AddSeconds(20), 40);
        await lifecycle.RecordResourceSampleAsync(first, default);
        await lifecycle.RecordResourceSampleAsync(HistorySample(vm, minute.AddSeconds(20), 40), default);
        Assert.Equal(40, (await HistoryRow(vm.Id, minute)).CpuUtilizationPercent);

        await lifecycle.RecordResourceSampleAsync(HistorySample(vm, minute.AddSeconds(20.5), 50), default);
        await lifecycle.RecordResourceSampleAsync(HistorySample(vm, minute.AddSeconds(10), 99), default);
        await lifecycle.RecordResourceSampleAsync(HistorySample(vm, minute.AddSeconds(20), 98), default);
        var newest = await HistoryRow(vm.Id, minute);
        Assert.Equal(50, newest.CpuUtilizationPercent);
        Assert.Equal(minute.AddSeconds(20.5), newest.ObservedAt);

        await lifecycle.RecordResourceSampleAsync(HistorySample(vm, minute.AddSeconds(65), null, workspaceDisk: false), default);
        var warming = await HistoryRow(vm.Id, minute.AddMinutes(1));
        Assert.Null(warming.CpuUtilizationPercent);
        Assert.Null(warming.WorkspaceDiskTotalBytes);
        Assert.Null(warming.WorkspaceDiskAvailableBytes);
        Assert.Equal(8L << 30, warming.MemoryTotalBytes);
        Assert.Equal(3, await db.DevelopmentWorkspaceVmResourceSamples.CountAsync());

        var history = await lifecycle.GetResourceHistoryAsync(vm.WorkspaceId, "org", vm.Id, default);
        var gap = history.Samples.Single(x => x.Minute == minute.AddMinutes(1));
        Assert.Null(gap.CpuUtilizationPercent);
        Assert.Null(gap.WorkspaceDiskUsedBytes);
        Assert.Equal(2L << 30, gap.MemoryUsedBytes);
    }

    [Fact]
    public async Task HistoryWriteFailureLeavesLatestStatusHeartbeatAndRoutePublicationIntact()
    {
        var (_, vm, _) = await Seed("session");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        runtime.Capabilities = ["resources-v1"];
        runtime.Resources = ResourceSample(vm.Id, 1, utilization: 12.5);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE development_workspace_vm_resource_samples");

        Assert.True(await Processor().ProcessNextAsync(default));

        var stored = await CurrentVm(vm.Id);
        Assert.Equal("current", lifecycle.ToVmResponse(stored).Resources!.State);
        Assert.Equal(clock.GetUtcNow(), stored.LastHeartbeatAt);
        Assert.Equal("ready", stored.Phase);
        Assert.True(stored.GatewayReady);
        Assert.Null((await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync(x => x.Service == "t3")).RevokedAt);
        Assert.Contains(diagnostics.Entries, x => x.Message.Contains("resource history write failed"));
    }

    [Fact]
    public async Task HistoryReadIsScopedBoundedOrderedAndLimitedToTheCurrentGeneration()
    {
        var (workspace, vm, _) = await Seed("ready");
        var other = await lifecycle.CreateAsync(new("org", "Other fixture", "image", "standard", "plan", 20, "other"), "actor", default);
        var now = clock.GetUtcNow();
        var minute = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        clock.Advance(minute.AddSeconds(50) - now);
        now = clock.GetUtcNow();
        // The window (now - 60 min, now] spans 61 UTC minutes; only the 60 most recent samples are returned.
        AddHistory(vm, 1, minute.AddSeconds(10));
        for (var age = 1; age <= 62; age++) AddHistory(vm, 1, minute.AddMinutes(-age).AddSeconds(55));
        AddHistory(vm, 1, minute.AddMinutes(1).AddSeconds(5));
        AddHistory(vm, 2, now.AddMinutes(-5));
        await db.SaveChangesAsync();

        var history = await lifecycle.GetResourceHistoryAsync(workspace.Id, "org", vm.Id, default);
        Assert.Equal((vm.Id, 1L, now, now.AddHours(-1)), (history.VmId, history.Generation, history.WindowEnd, history.WindowStart));
        Assert.Equal(60, history.Samples.Count);
        Assert.Equal(history.Samples.OrderBy(x => x.ObservedAt), history.Samples);
        Assert.Equal(minute.AddMinutes(-59).AddSeconds(55), history.Samples[0].ObservedAt);
        Assert.Equal(minute.AddMinutes(-59), history.Samples[0].Minute);
        Assert.Equal(minute.AddSeconds(10), history.Samples[^1].ObservedAt);
        Assert.Equal((4L << 30, 8L << 30, 30L << 30, 40L << 30), (history.Samples[0].MemoryUsedBytes,
            history.Samples[0].MemoryTotalBytes, history.Samples[0].WorkspaceDiskUsedBytes, history.Samples[0].WorkspaceDiskTotalBytes));

        var newest = await db.DevelopmentWorkspaceVmResourceSamples.ToListAsync()
            .ContinueWith(rows => rows.Result.Single(x => x.ObservedAt == minute.AddSeconds(10)));
        newest.ExpiresAt = now.AddSeconds(-1);
        await db.SaveChangesAsync();
        history = await lifecycle.GetResourceHistoryAsync(workspace.Id, "org", vm.Id, default);
        Assert.Equal(60, history.Samples.Count);
        Assert.Equal(minute.AddMinutes(-60).AddSeconds(55), history.Samples[0].ObservedAt);
        Assert.Equal(minute.AddMinutes(-1).AddSeconds(55), history.Samples[^1].ObservedAt);
        Assert.All(history.Samples, x => Assert.True(x.ObservedAt > history.WindowStart && x.ObservedAt <= now));

        await db.Database.ExecuteSqlAsync($"UPDATE development_workspace_vms SET generation = 2, phase = 'stopped' WHERE id = {vm.Id}");
        var restarted = await lifecycle.GetResourceHistoryAsync(workspace.Id, "org", vm.Id, default);
        Assert.Equal(2, restarted.Generation);
        Assert.Equal(now.AddMinutes(-5), Assert.Single(restarted.Samples).ObservedAt);
        await db.Database.ExecuteSqlAsync($"UPDATE development_workspace_vms SET generation = 3 WHERE id = {vm.Id}");
        Assert.Empty((await lifecycle.GetResourceHistoryAsync(workspace.Id, "org", vm.Id, default)).Samples);

        foreach (var (workspaceId, organizationId, vmId, code) in new[]
        {
            (workspace.Id, "other-org", vm.Id, "workspace_not_found"),
            (other.Id, "org", vm.Id, "workspace_vm_not_found"),
            (workspace.Id, "org", other.PrimaryVmId, "workspace_vm_not_found"),
            (workspace.Id, "org", "vm_missing", "workspace_vm_not_found")
        })
        {
            var error = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
                lifecycle.GetResourceHistoryAsync(workspaceId, organizationId, vmId, default));
            Assert.Equal(code, error.Code);
        }
    }

    [Fact]
    public async Task ExpiredHistoryIsPrunedInBatchesWhileVmsAreStopped()
    {
        var workspace = await SeedRetainedVolume();
        var vm = await CurrentVm(workspace.PrimaryVmId);
        Assert.Equal("stopped", vm.Phase);
        var now = clock.GetUtcNow();
        now = now.AddTicks(-(now.UtcTicks % TimeSpan.TicksPerSecond));
        for (var i = 0; i < 5; i++) AddHistory(vm, 1, now.AddHours(-3).AddMinutes(i));
        AddHistory(vm, 1, now.AddMinutes(-1));
        await db.SaveChangesAsync();

        Assert.Equal(2, await lifecycle.PruneResourceHistoryAsync(default, batchSize: 2));
        Assert.Equal(4, await db.DevelopmentWorkspaceVmResourceSamples.CountAsync());
        await Processor().FilesystemNextAsync(default);
        Assert.Equal(now.AddMinutes(-1), (await db.DevelopmentWorkspaceVmResourceSamples.AsNoTracking().SingleAsync()).ObservedAt);

        clock.Advance(DevelopmentWorkspaceService.ResourceHistoryRetention);
        await Processor().FilesystemNextAsync(default);
        Assert.False(await db.DevelopmentWorkspaceVmResourceSamples.AnyAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscardCompletionRemovesOnlyThatVmsHistory(bool discard)
    {
        var (workspace, vm, operation) = await SeedStop("cleanup", ephemeral: true);
        cloud.Servers.Remove("server-owned");
        var tracked = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
        if (discard)
        {
            tracked.DiscardGeneration = vm.Generation; tracked.DiscardOperationId = operation.Id;
            tracked.DiscardRequestedAt = clock.GetUtcNow(); tracked.DiscardRequestedBy = "actor";
        }
        AddHistory(vm, 1, clock.GetUtcNow().AddMinutes(-2));
        AddHistory(await CurrentVm(workspace.PrimaryVmId), 1, clock.GetUtcNow().AddMinutes(-2));
        await db.SaveChangesAsync();

        await Hop("stopped");

        Assert.Equal("stopped", (await CurrentVm(vm.Id)).Phase);
        Assert.Equal(!discard, await db.DevelopmentWorkspaceVmResourceSamples.AnyAsync(x => x.VmId == vm.Id));
        Assert.True(await db.DevelopmentWorkspaceVmResourceSamples.AnyAsync(x => x.VmId == workspace.PrimaryVmId));
    }

    private DevelopmentWorkspaceVmResourceSample HistorySample(DevelopmentWorkspaceVm vm, DateTimeOffset observedAt, double? cpu,
        bool workspaceDisk = true)
    {
        var status = JsonSerializer.SerializeToElement(new { resources = new
        {
            schemaVersion = 1, vmId = vm.Id, generation = vm.Generation, observedAt = observedAt.ToUnixTimeMilliseconds() / 1000.0,
            cpu = new { logicalCount = 4, utilizationPercent = cpu, windowSeconds = cpu is null ? (double?)null : 30.0 },
            memory = new { totalBytes = 8L << 30, availableBytes = 6L << 30 },
            filesystems = new[] { new { paths = workspaceDisk ? new[] { "/", "/workspace" } : ["/"], totalBytes = 40L << 30,
                freeBytes = 30L << 30, availableBytes = 28L << 30 } },
            unavailable = cpu is null ? new Dictionary<string, string> { ["cpu.utilizationPercent"] = "warming_up" } : []
        } }, Json);
        var normalized = DevelopmentWorkspaceService.NormalizeResourceSample(status, vm.Id, vm.Generation, clock.GetUtcNow());
        return DevelopmentWorkspaceService.ResourceHistorySample(normalized, vm.WorkspaceId, vm.Id, vm.Generation)!;
    }

    private Task<DevelopmentWorkspaceVmResourceSample> HistoryRow(string vmId, DateTimeOffset minute) =>
        db.DevelopmentWorkspaceVmResourceSamples.AsNoTracking().ToListAsync()
            .ContinueWith(rows => rows.Result.Single(x => x.VmId == vmId && x.Minute == minute));

    internal void AddHistory(DevelopmentWorkspaceVm vm, long generation, DateTimeOffset observedAt, DateTimeOffset? expiresAt = null) =>
        db.DevelopmentWorkspaceVmResourceSamples.Add(new()
        {
            WorkspaceId = vm.WorkspaceId, VmId = vm.Id, Generation = generation,
            Minute = new DateTimeOffset(observedAt.UtcTicks - observedAt.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero),
            ObservedAt = observedAt, CpuUtilizationPercent = 25, MemoryTotalBytes = 8L << 30, MemoryAvailableBytes = 4L << 30,
            WorkspaceDiskTotalBytes = 40L << 30, WorkspaceDiskAvailableBytes = 10L << 30,
            ExpiresAt = expiresAt ?? observedAt + DevelopmentWorkspaceService.ResourceHistoryRetention
        });
}
