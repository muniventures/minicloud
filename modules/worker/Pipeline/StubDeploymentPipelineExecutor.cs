using Microsoft.Extensions.Logging;
using Minicloud.Common.Models;

namespace Minicloud.Worker.Pipeline;

/// <summary>
/// Stub pipeline executor for foundation testing before full pipeline implementation (T40).
/// </summary>
public sealed class StubDeploymentPipelineExecutor : IDeploymentPipelineExecutor
{
    private readonly ILogger<StubDeploymentPipelineExecutor>? _logger;

    public StubDeploymentPipelineExecutor(ILogger<StubDeploymentPipelineExecutor>? logger = null)
    {
        _logger = logger;
    }

    public Task ExecuteAsync(Deployment deployment, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Stub deployment pipeline executed for deployment {DeploymentId}", deployment.Id);
        return Task.CompletedTask;
    }
}
