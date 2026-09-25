namespace Minicloud.Cli.Rendering;

public sealed record DeploymentPlan(
    bool HasSecretsStage,
    int TotalSecrets,
    bool HasArtifactsStage,
    IReadOnlyList<string> SourceServices,
    bool HasDeployStage,
    int SelectedServicesCount);

public sealed record DeploymentSuccessResult(
    int ServicesCount,
    string? ConsoleUrl,
    IReadOnlyList<(string ServiceName, string Url)> ServiceUrls);

public sealed record DeploymentFailureResult(
    string? DeploymentId,
    string? FailureCode,
    string? FailureMessage,
    string? ConsoleUrl);
