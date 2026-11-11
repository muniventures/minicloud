// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Cli.Rendering;

public interface IDeploymentRenderer : IDisposable, IAsyncDisposable
{
    void Initialize(DeploymentPlan plan);

    void SecretStarted(string serviceName, string secretName);
    void SecretCompleted(string serviceName, string secretName);

    void ArtifactPackageStarted(string serviceName);
    void ArtifactPackageCompleted(string serviceName, long byteCount);
    void ArtifactUploadStarted(string serviceName, long byteCount);
    void ArtifactUploadCompleted(string serviceName, long byteCount);

    void DeploymentCreationStarted();
    void DeploymentCreated(string deploymentNumber, string status);
    void DeploymentStatusUpdated(string deploymentNumber, string status, string? consoleUrl = null);
    void DeploymentActivityUpdated(string deploymentNumber, string phase, string? detail = null);
    void DeploymentReconnecting();
    void DeploymentReconnected(string deploymentNumber, string status);

    void DeploymentSucceeded(DeploymentSuccessResult result);
    void DeploymentFailed(DeploymentFailureResult result);
}
