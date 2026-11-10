using Minicloud.Common.Models;
using Minicloud.Worker.Databases;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class DatabaseCredentialRotationTests
{
    [Fact]
    public void WaitsForEveryBoundApplicationRollout()
    {
        var deployments = new[] { new Deployment { Status = DeploymentStatus.Succeeded }, new Deployment { Status = DeploymentStatus.Deploying } };
        Assert.False(DatabaseOperationProcessor.RotationDeploymentsComplete(deployments));
        deployments[1].Status = DeploymentStatus.Verifying;
        Assert.False(DatabaseOperationProcessor.RotationDeploymentsComplete(deployments));
        deployments[1].Status = DeploymentStatus.Succeeded;
        Assert.True(DatabaseOperationProcessor.RotationDeploymentsComplete(deployments));
    }

    [Theory]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Canceled)]
    public void FailedOrCancelledRolloutCannotReportSuccessfulCredentialRotation(DeploymentStatus status)
    {
        var error = Assert.Throws<InvalidOperationException>(() => DatabaseOperationProcessor.RotationDeploymentsComplete([new Deployment { Status = status }]));
        Assert.Contains("new credentials are retained", error.Message);
    }

    [Fact]
    public void DirectClientDatabaseNeedsNoApplicationRollout()
    {
        Assert.True(DatabaseOperationProcessor.RotationDeploymentsComplete([]));
    }
}
