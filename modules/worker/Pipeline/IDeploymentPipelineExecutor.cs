using Minicloud.Common.Models;

namespace Minicloud.Worker.Pipeline;

/// <summary>
/// Executes the steps of the deployment pipeline once claimed from the queue.
/// </summary>
public interface IDeploymentPipelineExecutor
{
    /// <summary>
    /// Executes the deployment pipeline for the claimed deployment.
    /// </summary>
    /// <param name="deployment">The claimed deployment record (in Provisioning status).</param>
    /// <param name="cancellationToken">Cancellation token for aborting the pipeline.</param>
    Task ExecuteAsync(Deployment deployment, CancellationToken cancellationToken);
}
