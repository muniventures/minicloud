using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Worker.Pipeline.Steps;

/// <summary>
/// Batches streamed SSH output lines into <c>DeploymentLogs</c> rows.
/// Flushes every <see cref="BatchSize"/> lines and on explicit flush.
/// Each instance serves one deployment execution.
/// </summary>
public sealed class DeploymentLogWriter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly string _deploymentId;
    private readonly List<DeploymentLog> _buffer = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public int BatchSize { get; }

    public DeploymentLogWriter(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        string deploymentId,
        int batchSize = 50)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _deploymentId = deploymentId;
        BatchSize = batchSize > 0 ? batchSize : 50;
    }

    public async Task AppendAsync(string source, string content, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            _buffer.Add(new DeploymentLog
            {
                Id = Guid.NewGuid().ToString("N"),
                DeploymentId = _deploymentId,
                Source = string.IsNullOrWhiteSpace(source) ? "deploy" : source,
                Content = content,
                CreatedAt = now,
            });

            if (_buffer.Count >= BatchSize)
            {
                await FlushCoreAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await FlushCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        var batch = _buffer.ToList();
        _buffer.Clear();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        db.DeploymentLogs.AddRange(batch);
        await db.SaveChangesAsync(cancellationToken);
    }
}
