using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class SshDeploymentClientLeaseTests
{
    [Fact]
    public async Task CleanupWaitsForInFlightLeaseMonitorToStop()
    {
        using var cancellation = new CancellationTokenSource();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, cancellation.Token); }
            catch (OperationCanceledException) { stopping.SetResult(); }
            await stopped.Task;
        });
        var cleaned = false;
        var shutdown = SshDeploymentClient.StopLeaseAndCleanupAsync(cancellation, monitor, () =>
        {
            Assert.True(monitor.IsCompleted);
            cleaned = true;
            return Task.CompletedTask;
        });
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(cleaned);
        stopped.SetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleaned);
    }
    [Fact]
    public async Task RemoteStartWaitsForFreshLeaseEvenAfterSlowPreparation()
    {
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = false;
        var monitor = new WorkerLeaseMonitor(async ct =>
        {
            await acknowledged.Task.WaitAsync(ct);
            refreshed = true;
        });
        // Artifact preparation finished with an expired lease. Startup must wait
        // for a new ownership-checked acknowledgement, not launch the process.
        var startup = monitor.RefreshBeforeStartAsync(CancellationToken.None);
        Assert.False(startup.IsCompleted);
        acknowledged.SetResult();
        await startup;
        Assert.True(refreshed);
    }

    [Fact]
    public async Task TransientConnectionResetRecoversWithinExistingLeaseDeadline()
    {
        var calls = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var monitor = new WorkerLeaseMonitor(_ =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 2) throw new IOException("Connection reset by peer");
            if (call == 3) recovered.TrySetResult();
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
        await monitor.RefreshBeforeStartAsync(cancellation.Token);
        var failure = new TaskCompletionSource<Exception>();
        var running = monitor.RunAsync(failure, cancellation.Token);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(failure.Task.IsCompleted);
        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task PersistentConnectionFailureStopsBeforeRemoteFenceExpires()
    {
        var calls = 0;
        var monitor = new WorkerLeaseMonitor(_ =>
        {
            if (Interlocked.Increment(ref calls) > 1) throw new IOException("Unavailable");
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));
        await monitor.RefreshBeforeStartAsync(CancellationToken.None);
        var failure = new TaskCompletionSource<Exception>();
        await monitor.RunAsync(failure, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<TimeoutException>(await failure.Task);
    }

    [Fact]
    public async Task ChangedOwnershipFailsImmediatelyWithoutRetry()
    {
        var calls = 0;
        var monitor = new WorkerLeaseMonitor(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new WorkerLeaseOwnershipException();
        });
        await Assert.ThrowsAsync<WorkerLeaseOwnershipException>(() =>
            monitor.RefreshBeforeStartAsync(CancellationToken.None));
        Assert.Equal(1, calls);
    }

}
