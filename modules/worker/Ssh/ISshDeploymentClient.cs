namespace Minicloud.Worker.Ssh;

public sealed class RemoteQuiescenceException : Exception
{
    public RemoteQuiescenceException(string message) : base(message) { }
}

/// <summary>
/// Options for SSH deployment connections. Bound from configuration
/// (<c>Worker:Ssh</c>) or environment in <c>SshDeploymentClient</c>.
/// </summary>
public sealed class SshDeploymentOptions
{
    public const string SectionName = "Worker:Ssh";

    /// <summary>
    /// Remote username for deployment operations (default: minicloud).
    /// </summary>
    public string Username { get; set; } = "minicloud";

    public int Port { get; set; } = 22;

    // Each line contains a host followed by a trusted OpenSSH SHA256 fingerprint.
    public string HostKeyPinsFile { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the Minicloud deployment private key on the worker host.
    /// </summary>
    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Inline private key content. Takes precedence over <see cref="PrivateKeyPath"/>
    /// when set. Prefer file-based keys in production.
    /// </summary>
    public string PrivateKeyContent { get; set; } = string.Empty;

    /// <summary>
    /// Passphrase for an encrypted private key, if any.
    /// </summary>
    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    /// <summary>
    /// Timeout for the remote-deploy.sh execution.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Timeout for connect + SFTP operations.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxParallelArtifactTransfers { get; set; } = 1;
}

/// <summary>
/// Single artifact archive to upload to the target VPS.
/// </summary>
public sealed record SshDeploymentArtifactUpload(
    string ServiceName,
    string RemoteFileName,
    Func<CancellationToken, Task<Stream>> OpenStream,
    long SizeBytes = 0);

/// <summary>
/// Input for a full VPS deployment over SSH/SFTP.
/// </summary>
public sealed record SshDeploymentRequest(
    string Host,
    string Username,
    string PrivateKeyContent,
    string RemoteDirectory,
    string PayloadJson,
    string RemoteDeployScriptContent,
    IReadOnlyList<SshDeploymentArtifactUpload> Artifacts,
    TimeSpan CommandTimeout);

/// <summary>
/// Result of the remote-deploy.sh execution.
/// </summary>
public sealed record SshDeploymentResult(
    int ExitCode,
    bool CompletionMarkerFound,
    string? CompletionSha,
    IReadOnlyList<string> OutputLines);

/// <summary>
/// SSH/SFTP deployment client for target VPS operations (SSH.NET).
/// </summary>
public interface ISshDeploymentClient
{
    /// <summary>
    /// Uploads payload.json, remote-deploy.sh and service zips, then executes
    /// remote-deploy.sh over SSH. Invokes <paramref name="onLogLine"/> for each
    /// streamed stdout/stderr line with a source label.
    /// </summary>
    Task<SshDeploymentResult> ExecuteDeploymentAsync(
        SshDeploymentRequest request,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs an ad-hoc command over SSH (health probes, rollback) and streams output.
    /// </summary>
    Task<int> ExecuteCommandAsync(
        string host,
        string username,
        string privateKeyContent,
        string command,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken);
}
