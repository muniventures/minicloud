using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.DevelopmentWorkspaces;

// Folder size and confirmed deletion jobs run in the dev environment as the unprivileged coding user (#156).
public sealed partial class DevelopmentWorkspaceProcessor
{
    private static readonly TimeSpan FilesystemScanBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FilesystemDeleteBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FilesystemGrace = TimeSpan.FromSeconds(30);

    public async Task FilesystemNextAsync(CancellationToken ct)
    {
        await lifecycle.PruneFilesystemJobsAsync(ct);
        try { await lifecycle.PruneResourceHistoryAsync(ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("Dev environment resource history expiry failed ({ErrorType}); the next pass will retry.", error.GetType().Name);
        }
        foreach (var job in await lifecycle.RunningFilesystemJobsAsync(ct))
            await PollFilesystemJobAsync(job, ct);
        var claimed = await lifecycle.ClaimFilesystemJobAsync(2, ct);
        if (claimed is not null) await StartFilesystemJobAsync(claimed, ct);
    }

    private async Task StartFilesystemJobAsync(DevelopmentWorkspaceFilesystemJob job, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var vm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.VmId, ct);
        if (vm is null || vm.Generation != job.Generation)
        {
            await lifecycle.RecordFilesystemJobAsync(job.Id, null, "not_started", ct);
            return;
        }
        try
        {
            var report = Result(await runtime.CallAsync(Target(vm), job.Id, "fs-job-start", StartArguments(job), ct));
            await lifecycle.RecordFilesystemJobAsync(job.Id, report, null, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is not DevelopmentWorkspaceException)
        {
            // The runtime records the job ID before launching, so a lost reply is resolved by polling, never by relaunching.
            logger?.LogInformation("Folder job start reply was not received ({ErrorType}).", error.GetType().Name);
        }
    }

    private async Task PollFilesystemJobAsync(DevelopmentWorkspaceFilesystemJob job, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var vm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.VmId, ct);
        if (vm is null || vm.Generation != job.Generation || vm.Phase != "ready")
        {
            // The runtime stops folder jobs before it stops the dev environment.
            await lifecycle.RecordFilesystemJobAsync(job.Id, null, "dev_environment_stopped", ct);
            return;
        }
        var budget = job.Kind == DevelopmentWorkspaceFilesystemJobKinds.DirectoryDeletion ? FilesystemDeleteBudget : FilesystemScanBudget;
        var overdue = job.StartedAt is { } started && started.Add(budget + FilesystemGrace) < Now;
        var cancel = job.CancelRequested || overdue || vm.DesiredState != "running";
        try
        {
            var report = Result(await runtime.CallAsync(Target(vm), job.Id, cancel ? "fs-job-cancel" : "fs-job-status",
                new { jobId = job.Id }, ct));
            await lifecycle.RecordFilesystemJobAsync(job.Id, report, null, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is not DevelopmentWorkspaceException)
        {
            if (job.StartedAt is { } started2 && started2.Add(budget + FilesystemGrace * 4) < Now)
                await lifecycle.RecordFilesystemJobAsync(job.Id, null, "runtime_unreachable", ct);
        }
    }

    private static object StartArguments(DevelopmentWorkspaceFilesystemJob job) => new
    {
        jobId = job.Id,
        mode = job.Kind == DevelopmentWorkspaceFilesystemJobKinds.DirectoryDeletion ? "delete" : "scan",
        path = JsonSerializer.Deserialize<string[]>(job.PathJson) ?? [],
        expect = job.TargetDevice is { } dev && job.TargetInode is { } ino ? new { dev, ino } : null
    };
}
