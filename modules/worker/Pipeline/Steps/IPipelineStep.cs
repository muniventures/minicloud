using Minicloud.Common.Models;
using Minicloud.Common.Providers;

namespace Minicloud.Worker.Pipeline.Steps;

/// <summary>
/// Mutable state shared across pipeline steps for a single claimed deployment.
/// </summary>
public sealed class PipelineStepContext
{
    public required string DeploymentId { get; init; }

    public required string OrganizationId { get; init; }

    public required string AppId { get; init; }

    public string OwnershipToken { get; init; } = string.Empty;

    public string AppSlug { get; set; } = string.Empty;

    public string OrganizationSlug { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;

    public CloudProvider Provider { get; set; } = CloudProvider.Hetzner;

    public string ServerInstanceId { get; set; } = string.Empty;

    public string ServerIp { get; set; } = string.Empty;

    public string SshUsername { get; set; } = "minicloud";

    public List<string> Hostnames { get; set; } = [];

    public Dictionary<string, string[]> ServiceHostnames { get; } = new(StringComparer.Ordinal);

    public string RemoteDirectory { get; set; } = string.Empty;

    public bool RequireRolloutGate { get; set; }

    public bool DnsHostnamesResolved { get; set; }

    public string PrimaryHostname => Hostnames.Count > 0 ? Hostnames[0] : string.Empty;
}

/// <summary>
/// Single stage of the deployment pipeline.
/// </summary>
public interface IPipelineStep
{
    /// <summary>
    /// Short name used in logs and events (e.g. "resolve-provision").
    /// </summary>
    string Name { get; }

    Task ExecuteAsync(PipelineStepContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Step failure carrying a stable machine-readable failure code
/// (e.g. <c>provider_provision_failed</c>, <c>docker_build_failed</c>,
/// <c>health_check_failed</c>).
/// </summary>
public sealed class PipelineStepException : Exception
{
    public string FailureCode { get; }

    public PipelineStepException(string failureCode, string message)
        : base(message)
    {
        FailureCode = failureCode;
    }

    public PipelineStepException(string failureCode, string message, Exception innerException)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }
}
