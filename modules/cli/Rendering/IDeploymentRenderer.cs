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
    void DeploymentCreated(string deploymentId, string status);
    void DeploymentStatusUpdated(string deploymentId, string status, string? consoleUrl = null);
    void DeploymentReconnecting();
    void DeploymentReconnected(string deploymentId, string status);

    void DeploymentSucceeded(DeploymentSuccessResult result);
    void DeploymentFailed(DeploymentFailureResult result);
}
