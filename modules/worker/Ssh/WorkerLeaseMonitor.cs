using System.Diagnostics;
using Renci.SshNet;

namespace Minicloud.Worker.Ssh;

internal sealed class WorkerLeaseOwnershipException() : Exception("Remote ownership lease token no longer matches.");

internal sealed class WorkerLeaseConnection(
    Func<SshClient> createClient,
    string leasePath,
    string token) : IDisposable
{
    private SshClient? client;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (client?.IsConnected != true)
            {
                client?.Dispose();
                client = createClient();
                await client.ConnectAsync(cancellationToken);
            }
            using var command = client.CreateCommand(
                $"flock -x {Quote(leasePath + ".lock")} -c " +
                Quote($"test \"$(cat {Quote(leasePath)} 2>/dev/null)\" = {Quote(token)} && touch {Quote(leasePath)}"));
            await command.ExecuteAsync(cancellationToken);
            if (command.ExitStatus != 0)
            {
                throw new WorkerLeaseOwnershipException();
            }
        }
        catch
        {
            client?.Dispose();
            client = null;
            throw;
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public void Dispose() => client?.Dispose();
}

internal sealed class WorkerLeaseMonitor(
    Func<CancellationToken, Task> refresh,
    TimeSpan? safeWindow = null,
    TimeSpan? refreshInterval = null)
{
    // The remote watchdog permits five seconds. Stop retrying before that fence
    // can expire, including time spent connecting or waiting for flock.
    private readonly TimeSpan window = safeWindow ?? TimeSpan.FromSeconds(4);
    private readonly TimeSpan interval = refreshInterval ?? TimeSpan.FromSeconds(1);
    private long lastRefreshStarted;

    public async Task RefreshBeforeStartAsync(CancellationToken cancellationToken)
    {
        lastRefreshStarted = Stopwatch.GetTimestamp();
        await RefreshWithinDeadlineAsync(cancellationToken);
    }

    public async Task RunAsync(TaskCompletionSource<Exception> failure, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, cancellationToken);
                await RefreshWithinDeadlineAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure.TrySetResult(ex);
        }
    }

    private async Task RefreshWithinDeadlineAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = window - Stopwatch.GetElapsedTime(lastRefreshStarted);
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("Worker ownership lease refresh deadline expired.");
        }
        deadline.CancelAfter(remaining);
        while (true)
        {
            var attemptStarted = Stopwatch.GetTimestamp();
            try
            {
                await refresh(deadline.Token);
                lastRefreshStarted = attemptStarted;
                return;
            }
            catch (WorkerLeaseOwnershipException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException("Worker ownership lease refresh deadline expired.", ex);
                }
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Worker ownership lease refresh deadline expired.", ex);
                }
            }
        }
    }
}
