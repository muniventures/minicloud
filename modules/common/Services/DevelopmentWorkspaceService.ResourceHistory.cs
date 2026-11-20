using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

// Short-lived last-hour VM resource history collected from verified heartbeats (#205).
public sealed partial class DevelopmentWorkspaceService
{
    public static readonly TimeSpan ResourceHistoryWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan ResourceHistoryRetention = TimeSpan.FromHours(2);
    public const int ResourceHistoryMaxSamples = 60;
    public const int ResourceHistoryPruneBatchSize = 1000;

    /// <summary>Projects a normalized resource sample (see <see cref="NormalizeResourceSample"/>) into a history row.</summary>
    public static DevelopmentWorkspaceVmResourceSample? ResourceHistorySample(string? normalizedSample, string workspaceId,
        string vmId, long generation)
    {
        if (normalizedSample is null) return null;
        StoredResources? stored;
        try { stored = JsonSerializer.Deserialize<StoredResources>(normalizedSample, StoredJson); }
        catch (JsonException) { return null; }
        if (stored is null) return null;
        var observedAt = stored.ObservedAt.ToUniversalTime();
        var workspaceDisk = stored.Filesystems?.FirstOrDefault(x => x.Paths.Contains(WorkspaceRoot));
        return new()
        {
            WorkspaceId = workspaceId, VmId = vmId, Generation = generation,
            Minute = new DateTimeOffset(observedAt.UtcTicks - observedAt.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero),
            ObservedAt = observedAt,
            CpuUtilizationPercent = stored.Cpu?.UtilizationPercent,
            MemoryTotalBytes = stored.Memory?.TotalBytes, MemoryAvailableBytes = stored.Memory?.AvailableBytes,
            WorkspaceDiskTotalBytes = workspaceDisk?.TotalBytes, WorkspaceDiskAvailableBytes = workspaceDisk?.AvailableBytes,
            ExpiresAt = observedAt + ResourceHistoryRetention
        };
    }

    /// <summary>
    /// Atomically keeps the newest observation per VM generation and UTC minute. Replays of the same observation are
    /// no-ops and an older observation never replaces a newer one.
    /// </summary>
    public Task<int> RecordResourceSampleAsync(DevelopmentWorkspaceVmResourceSample sample, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO development_workspace_vm_resource_samples
                (vm_id, generation, minute, workspace_id, observed_at, cpu_utilization_percent, memory_total_bytes,
                 memory_available_bytes, workspace_disk_total_bytes, workspace_disk_available_bytes, expires_at)
            VALUES ({sample.VmId}, {sample.Generation}, {sample.Minute}, {sample.WorkspaceId}, {sample.ObservedAt},
                {sample.CpuUtilizationPercent}, {sample.MemoryTotalBytes}, {sample.MemoryAvailableBytes},
                {sample.WorkspaceDiskTotalBytes}, {sample.WorkspaceDiskAvailableBytes}, {sample.ExpiresAt})
            ON CONFLICT (vm_id, generation, minute) DO UPDATE SET
                observed_at = excluded.observed_at,
                cpu_utilization_percent = excluded.cpu_utilization_percent,
                memory_total_bytes = excluded.memory_total_bytes,
                memory_available_bytes = excluded.memory_available_bytes,
                workspace_disk_total_bytes = excluded.workspace_disk_total_bytes,
                workspace_disk_available_bytes = excluded.workspace_disk_available_bytes,
                expires_at = excluded.expires_at
            WHERE excluded.observed_at > development_workspace_vm_resource_samples.observed_at
            """, ct);

    /// <summary>Deletes one bounded batch of expired history rows, independent of VM state.</summary>
    public Task<int> PruneResourceHistoryAsync(CancellationToken ct, int batchSize = ResourceHistoryPruneBatchSize)
    {
        // UTC timestamps also order correctly as SQLite text, so one statement serves both providers.
        var now = Now.ToUniversalTime();
        return db.Database.ExecuteSqlAsync($"""
            DELETE FROM development_workspace_vm_resource_samples
            WHERE (vm_id, generation, minute) IN (
                SELECT vm_id, generation, minute FROM development_workspace_vm_resource_samples
                WHERE expires_at <= {now} LIMIT {batchSize})
            """, ct);
    }

    /// <summary>Removes all history for a deleted workspace, or for one of its VMs.</summary>
    public Task<int> DeleteResourceHistoryAsync(string workspaceId, string? vmId, CancellationToken ct) =>
        db.DevelopmentWorkspaceVmResourceSamples
            .Where(x => x.WorkspaceId == workspaceId && (vmId == null || x.VmId == vmId))
            .ExecuteDeleteAsync(ct);

    public async Task<DevelopmentWorkspaceVmResourceHistory> GetResourceHistoryAsync(string workspaceId, string organizationId,
        string vmId, CancellationToken ct)
    {
        if (!await db.DevelopmentWorkspaces.AsNoTracking().AnyAsync(x => x.Id == workspaceId && x.OrganizationId == organizationId, ct))
            throw Invalid("workspace_not_found", "Workspace was not found.");
        var vm = await db.DevelopmentWorkspaceVms.AsNoTracking().Where(x => x.Id == vmId && x.WorkspaceId == workspaceId
                     && x.OrganizationId == organizationId).Select(x => new { x.Id, x.Generation, x.Phase }).SingleOrDefaultAsync(ct)
                 ?? throw Invalid("workspace_vm_not_found", "VM was not found.");
        var windowEnd = Now.ToUniversalTime();
        var windowStart = windowEnd - ResourceHistoryWindow;
        var query = db.DevelopmentWorkspaceVmResourceSamples.AsNoTracking()
            .Where(x => x.VmId == vm.Id && x.Generation == vm.Generation && vm.Phase != "deleted");
        // SQLite cannot compare DateTimeOffset values server-side; retention keeps its client-side set small.
        if (!db.Database.IsSqlite())
            query = query.Where(x => x.ObservedAt > windowStart && x.ObservedAt <= windowEnd && x.ExpiresAt > windowEnd)
                .OrderByDescending(x => x.ObservedAt).Take(ResourceHistoryMaxSamples);
        var rows = (await query.ToListAsync(ct))
            .Where(x => x.ObservedAt > windowStart && x.ObservedAt <= windowEnd && x.ExpiresAt > windowEnd)
            .OrderByDescending(x => x.ObservedAt).Take(ResourceHistoryMaxSamples).OrderBy(x => x.ObservedAt);
        return new(vm.Id, vm.Generation, windowStart, windowEnd, rows.Select(x => new DevelopmentWorkspaceVmResourceHistorySample(
            x.Minute, x.ObservedAt, x.CpuUtilizationPercent,
            x.MemoryTotalBytes - x.MemoryAvailableBytes, x.MemoryTotalBytes,
            x.WorkspaceDiskTotalBytes - x.WorkspaceDiskAvailableBytes, x.WorkspaceDiskTotalBytes)).ToList());
    }
}
