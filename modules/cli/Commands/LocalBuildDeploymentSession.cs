// SPDX-License-Identifier: MPL-2.0

using Minicloud.Cli.Api;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

internal sealed class LocalBuildDeploymentSession : IAsyncDisposable
{
    private readonly MinicloudApiClient _api;
    private readonly string _appId;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _work;
    private readonly Task _heartbeat;
    private readonly TimeSpan _heartbeatInterval;
    private Exception? _heartbeatFailure;
    private bool _submitted;
    public string Stage { get; set; } = "build";
    public string Id { get; }
    public CancellationToken Token => _work.Token;

    public LocalBuildDeploymentSession(MinicloudApiClient api, string appId, string id, CancellationToken ct, TimeSpan? heartbeatInterval = null)
    {
        _api = api;
        _appId = appId;
        Id = id;
        _callerToken = ct;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
        _work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _heartbeat = HeartbeatAsync();
    }

    public void Submitted() => _submitted = true;

    public async Task StopHeartbeatAsync()
    {
        await _stop.CancelAsync();
        await _heartbeat;
        ThrowIfLost();
    }

    public void ThrowIfLost()
    {
        if (_heartbeatFailure is not null)
        { throw new CliCommandException(CliExitCodes.DeploymentFailed, "local_build_status_lost: Minicloud could not retain this local build session. Run deploy again. " + (_heartbeatFailure is ApiException api ? api.Code + ": " : "") + _heartbeatFailure.Message); }
    }

    private async Task HeartbeatAsync()
    {
        var lastSuccess = DateTimeOffset.UtcNow;
        try
        {
            using var timer = new PeriodicTimer(_heartbeatInterval);
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await _api.UpdateDeploymentPreparationAsync(Id, new(_appId, "heartbeat"), timeout.Token);
                    lastSuccess = DateTimeOffset.UtcNow;
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    if ((ex is ApiException api && api.StatusCode is >= 400 and < 500 and not (408 or 429)) ||
                        DateTimeOffset.UtcNow - lastSuccess > TimeSpan.FromMinutes(4))
                    {
                        _heartbeatFailure = ex;
                        await _work.CancelAsync();
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _heartbeat;
        if (!_submitted)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _api.UpdateDeploymentPreparationAsync(Id, new(_appId, _callerToken.IsCancellationRequested ? "canceled" : "failed", Stage), timeout.Token); }
            catch { /* Server expiry closes an unreachable or terminated client session. */ }
        }
        _stop.Dispose();
        _work.Dispose();
    }
}
