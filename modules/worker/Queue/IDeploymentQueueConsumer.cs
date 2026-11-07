using Minicloud.Common.Models;

namespace Minicloud.Worker.Queue;

public interface IDeploymentQueueConsumer
{
    /// <summary>
    /// Atomically claims the oldest queued deployment using SELECT ... FOR UPDATE SKIP LOCKED.
    /// Transitions status to Provisioning within the database transaction and returns the claimed deployment.
    /// Returns null if no queued deployment was available.
    /// </summary>
    Task<Deployment?> TryClaimNextDeploymentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Executes the deployment pipeline for the claimed deployment, keeping heartbeats active until completion.
    /// </summary>
    Task ProcessDeploymentAsync(Deployment deployment, CancellationToken cancellationToken);
}
