using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Worker.Pipeline.Steps;

/// <summary>
/// Shared EF Core helpers for pipeline steps. All multi-entity state
/// transitions use the execution-strategy transaction pattern from
/// docs/architecture/agent-instructions.md.
/// </summary>
internal static class PipelineDb
{
    private static readonly SemaphoreSlim SqliteTransactionGate = new(1, 1);

    public static async Task ExecuteInTransactionAsync(
        IServiceScopeFactory scopeFactory,
        Func<MinicloudDbContext, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var serializeSqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        if (serializeSqlite)
        {
            await SqliteTransactionGate.WaitAsync(cancellationToken);
        }

        try
        {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await operation(db, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
        }
        finally
        {
            if (serializeSqlite)
            {
                SqliteTransactionGate.Release();
            }
        }
    }

    public static void AddEvent(
        MinicloudDbContext db,
        string deploymentId,
        EventLevel level,
        string message,
        DateTimeOffset now,
        string? metadataJson = null)
    {
        db.DeploymentEvents.Add(new DeploymentEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            DeploymentId = deploymentId,
            Level = level,
            Message = message,
            MetadataJson = metadataJson,
            CreatedAt = now,
        });
    }

    public static void Touch(Deployment deployment, DateTimeOffset now)
    {
        deployment.UpdatedAt = now;
        if (deployment.StartedAt is null)
        {
            deployment.StartedAt = now;
        }
    }
}
