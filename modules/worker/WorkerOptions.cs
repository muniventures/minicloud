namespace Minicloud.Worker;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// Identifier for this worker instance (defaults to hostname/machine name).
    /// </summary>
    public string WorkerId { get; set; } = Environment.MachineName;

    /// <summary>
    /// Root filesystem path where deployment artifacts are stored when accessed locally.
    /// Mounted at /var/lib/minicloud/artifacts in containers.
    /// Falls back to DeploymentArtifacts:RootPath when Worker:ArtifactRootPath is unset (see Program.cs).
    /// </summary>
    public string ArtifactRootPath { get; set; } = "/var/lib/minicloud/artifacts";

    /// <summary>
    /// Polling interval for checking new queued deployments in PostgreSQL.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Interval between heartbeat updates while a deployment pipeline is actively executing.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum duration allowed for a single deployment before timing out.
    /// </summary>
    public TimeSpan DeploymentTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Timeout for acquiring database locks and connections.
    /// </summary>
    public TimeSpan DatabaseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Base URL for the Minicloud API when running in remote worker mode.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// API token / key for authenticating with the Minicloud API when running remotely.
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>Maximum deployment pipelines admitted by this worker.</summary>
    public int MaxConcurrentDeployments { get; set; } = 1;
}
