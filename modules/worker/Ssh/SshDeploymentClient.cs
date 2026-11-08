using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace Minicloud.Worker.Ssh;

/// <summary>
/// SSH.NET implementation of <see cref="ISshDeploymentClient"/>.
/// Connects as <c>minicloud@&lt;server-ip&gt;</c>, SFTPs the deployment
/// payload plus remote-deploy.sh, then runs it with stdin closed.
/// </summary>
public sealed class SshDeploymentClient : ISshDeploymentClient
{
    internal const short PrivateDeploymentDirectoryMode = 700;
    internal const short PrivatePayloadFileMode = 600;
    private readonly SshDeploymentOptions _options;
    private readonly ILogger<SshDeploymentClient>? _logger;

    public SshDeploymentClient(
        IOptions<SshDeploymentOptions> options,
        ILogger<SshDeploymentClient>? logger = null)
        : this(options.Value, logger)
    {
    }

    public SshDeploymentClient(
        SshDeploymentOptions options,
        ILogger<SshDeploymentClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async Task<SshDeploymentResult> ExecuteDeploymentAsync(
        SshDeploymentRequest request,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onLogLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PrivateKeyContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RemoteDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PayloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RemoteDeployScriptContent);

        cancellationToken.ThrowIfCancellationRequested();

        var remoteDir = request.RemoteDirectory.TrimEnd('/');
        var remotePayloadPath = $"{remoteDir}/payload.json";
        var remoteScriptPath = $"{remoteDir}/remote-deploy.sh";

        using var buildPayload = JsonDocument.Parse(request.PayloadJson);
        if (buildPayload.RootElement.TryGetProperty("build_artifacts", out var buildArtifacts) &&
            buildArtifacts.ValueKind == JsonValueKind.Array && buildArtifacts.GetArrayLength() > 0)
        {
            const string bootstrapWait = """
                bash -c 'set -euo pipefail
                if command -v cloud-init >/dev/null 2>&1; then
                  status=0; timeout 600 sudo -n cloud-init status --wait >/dev/null 2>&1 || status=$?
                  case "$status" in 0|2) ;; *) exit 41 ;; esac
                fi'
                """;
            var bootstrapExit = await ExecuteCommandAsync(request.Host, request.Username,
                request.PrivateKeyContent, bootstrapWait, (_, _) => Task.CompletedTask, cancellationToken);
            if (bootstrapExit != 0)
            {
                throw new InvalidOperationException("buildkit_unavailable: runtime bootstrap or local BuildKit preflight failed before artifact transfer; use the documented existing-server upgrade.");
            }

            // Bootstrap can update account groups after its SSH session opens.
            // ExecuteCommandAsync opens a fresh connection for the Docker checks.
            const string builderPreflight = """
                bash -c 'set -euo pipefail
                docker buildx version >/dev/null 2>&1
                docker buildx inspect --builder "$(docker context show)" | grep -E "^Driver:[[:space:]]+docker$" >/dev/null'
                """;
            var preflightExit = await ExecuteCommandAsync(request.Host, request.Username,
                request.PrivateKeyContent, builderPreflight, (_, _) => Task.CompletedTask, cancellationToken);
            if (preflightExit != 0)
            {
                throw new InvalidOperationException("buildkit_unavailable: runtime bootstrap or local BuildKit preflight failed before artifact transfer; use the documented existing-server upgrade.");
            }
        }

        var transferStartedAt = DateTimeOffset.UtcNow;
        var transferTimer = Stopwatch.StartNew();
        var configuredTransfers = _options.MaxParallelArtifactTransfers;
        var effectiveTransfers = Math.Min(configuredTransfers, request.Artifacts.Count);
        var artifactBytes = request.Artifacts.Sum(item => item.SizeBytes);
        try
        {
            await UploadPayloadAsync(request, remoteDir, remotePayloadPath, remoteScriptPath, cancellationToken);
            transferTimer.Stop();
            await onLogLine("timing",
                $"TIMING phase=sftp_transfer deployment_id={DeploymentIdFromPayload(request.PayloadJson)} " +
                $"started_at={transferStartedAt:O} duration_ms={transferTimer.ElapsedMilliseconds} " +
                $"artifact_bytes={artifactBytes} artifact_count={request.Artifacts.Count} " +
                $"configured_concurrency={configuredTransfers} effective_concurrency={effectiveTransfers} outcome=succeeded");
        }
        catch
        {
            transferTimer.Stop();
            await onLogLine("timing",
                $"TIMING phase=sftp_transfer deployment_id={DeploymentIdFromPayload(request.PayloadJson)} " +
                $"started_at={transferStartedAt:O} duration_ms={transferTimer.ElapsedMilliseconds} " +
                $"artifact_bytes={artifactBytes} artifact_count={request.Artifacts.Count} " +
                $"configured_concurrency={configuredTransfers} effective_concurrency={effectiveTransfers} outcome=failed");
            throw;
        }

        var commandTimeout = request.CommandTimeout > TimeSpan.Zero
            ? request.CommandTimeout
            : _options.CommandTimeout;

        var remoteProcessPath = $"{remoteDir}/process.pid";
        var ownershipToken = GetPayloadString(request.PayloadJson, "ownership_token");
        var leasePath = $"{remoteDir}/worker.lease";
        var remoteCommand =
            $"chmod 700 {EscapeShell(remoteScriptPath)} || exit $?; " +
            $"setsid bash {EscapeShell(remoteScriptPath)} {EscapeShell(remotePayloadPath)} </dev/null & child=$!; " +
            $"printf '%s\\n' \"$child\" > {EscapeShell(remoteProcessPath)}; " +
            $"wait \"$child\"; status=$?; rm -f {EscapeShell(remoteProcessPath)} {EscapeShell(remotePayloadPath)}; exit $status";
        var outputLines = new List<string>();
        var outputGate = new object();
        string? completionSha = null;
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaseConnection = string.IsNullOrWhiteSpace(ownershipToken)
            ? null
            : new WorkerLeaseConnection(() => CreateClient(request.Host,
                () => new SshClient(BuildConnectionInfo(request.Host, request.Username, request.PrivateKeyContent))),
                leasePath, ownershipToken!);
        var leaseMonitor = leaseConnection is null ? null : new WorkerLeaseMonitor(leaseConnection.RefreshAsync);
        var leaseTask = Task.CompletedTask;
        using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = leaseFailure.Task.ContinueWith(_ => commandCts.Cancel(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        int exitCode;
        try
        {
            if (leaseMonitor is not null)
            {
                // Uploads can exceed the runtime's lease lifetime. Confirm
                // ownership and freshness before starting any remote process.
                await leaseMonitor.RefreshBeforeStartAsync(cancellationToken);
                leaseTask = leaseMonitor.RunAsync(leaseFailure, leaseCts.Token);
            }
            exitCode = await RunRemoteCommandAsync(
            request.Host,
            request.Username,
            request.PrivateKeyContent,
            remoteCommand,
            commandTimeout,
            async (source, line) =>
            {
                var isCompletionMarker = line.StartsWith("__MINICLOUD_DEPLOY_COMPLETE__=", StringComparison.Ordinal);
                lock (outputGate)
                {
                    outputLines.Add(line);
                    if (isCompletionMarker)
                    {
                        completionSha = line["__MINICLOUD_DEPLOY_COMPLETE__=".Length..].Trim();
                    }
                }

                if (!isCompletionMarker)
                {
                    await onLogLine(source, line);
                }
            },
            commandCts.Token,
            remoteProcessPath);
        }
        catch (OperationCanceledException) when (leaseFailure.Task.IsCompletedSuccessfully)
        {
            throw new RemoteQuiescenceException($"Worker lease refresh failed: {(await leaseFailure.Task).Message}");
        }
        finally
        {
            await StopLeaseAndCleanupAsync(leaseCts, leaseTask, () => Task.Run(() =>
            {
                TryDeleteRemoteFile(request, leasePath);
                TryDeleteRemoteFile(request, leasePath + ".lock");
            }, CancellationToken.None));
        }

        return new SshDeploymentResult(
            exitCode,
            completionSha is not null,
            completionSha,
            outputLines);
    }

    internal static async Task StopLeaseAndCleanupAsync(
        CancellationTokenSource leaseCts, Task leaseTask, Func<Task> cleanup)
    {
        // The remote shell must not remove the lease while its refresher is alive:
        // a final refresh can otherwise cancel a successfully completed rollout.
        await leaseCts.CancelAsync();
        try { await leaseTask; } catch (OperationCanceledException) { }
        await cleanup();
    }

    private static string DeploymentIdFromPayload(string payloadJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty("deployment_id", out var id) ? id.GetString() ?? "unknown" : "unknown";
    }

    public async Task<int> ExecuteCommandAsync(
        string host,
        string username,
        string privateKeyContent,
        string command,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(onLogLine);

        return await RunRemoteCommandAsync(
            host,
            username,
            privateKeyContent,
            command,
            _options.CommandTimeout,
            onLogLine,
            cancellationToken,
            remoteProcessPath: null);
    }

    private async Task UploadPayloadAsync(
        SshDeploymentRequest request,
        string remoteDir,
        string remotePayloadPath,
        string remoteScriptPath,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            var connectionInfo = BuildConnectionInfo(request.Host, request.Username, request.PrivateKeyContent);
            using var sftp = CreateClient(request.Host, () => new SftpClient(connectionInfo));
            sftp.ConnectionInfo.Timeout = _options.ConnectionTimeout;
            sftp.Connect();
            try
            {
                if (!sftp.Exists(remoteDir))
                {
                    sftp.CreateDirectory(remoteDir);
                }
                sftp.ChangePermissions(remoteDir, PrivateDeploymentDirectoryMode); // SSH.NET expects octal digits as a decimal argument.

                UploadText(sftp, remotePayloadPath, request.PayloadJson);
                sftp.ChangePermissions(remotePayloadPath, PrivatePayloadFileMode);
                UploadText(sftp, remoteScriptPath, request.RemoteDeployScriptContent);
                var rolloutGatePath = $"{remoteDir}/rollout.ready";
                var preparationReadyPath = $"{remoteDir}/preparation.ready";
                if (sftp.Exists(rolloutGatePath))
                {
                    sftp.DeleteFile(rolloutGatePath);
                }
                if (sftp.Exists(preparationReadyPath))
                {
                    sftp.DeleteFile(preparationReadyPath);
                }

            }
            finally
            {
                sftp.Disconnect();
            }
        }, cancellationToken);

        var transferLimit = Math.Min(_options.MaxParallelArtifactTransfers, Math.Max(1, request.Artifacts.Count));
        await Parallel.ForEachAsync(
            request.Artifacts,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = transferLimit,
            },
            async (artifact, ct) =>
            {
                var artifactConnection = BuildConnectionInfo(request.Host, request.Username, request.PrivateKeyContent);
                using var artifactSftp = CreateClient(request.Host, () => new SftpClient(artifactConnection));
                artifactSftp.ConnectionInfo.Timeout = _options.ConnectionTimeout;
                var remoteArtifactPath = $"{remoteDir}/{artifact.RemoteFileName}";
                try
                {
                    await artifactSftp.ConnectAsync(ct);
                    using var source = await artifact.OpenStream(ct);
                    await artifactSftp.UploadFileAsync(source, remoteArtifactPath, true, null, ct);
                    ct.ThrowIfCancellationRequested();
                    artifactSftp.ChangePermissions(remoteArtifactPath, PrivatePayloadFileMode);
                    _logger?.LogInformation("SFTP uploaded artifact for service {Service} to {RemotePath}", artifact.ServiceName, remoteArtifactPath);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    _ = Task.Run(() => TryDeleteRemoteFile(request, remoteArtifactPath), CancellationToken.None);
                    throw new OperationCanceledException(ct);
                }
                catch
                {
                    TryDeleteRemoteFile(request, remoteArtifactPath);
                    throw;
                }
            });

        await Task.Run(() =>
        {
            var readyConnection = BuildConnectionInfo(request.Host, request.Username, request.PrivateKeyContent);
            using var readySftp = CreateClient(request.Host, () => new SftpClient(readyConnection));
            readySftp.ConnectionInfo.Timeout = _options.ConnectionTimeout;
            readySftp.Connect();
            try
            {
                var ownershipToken = GetPayloadString(request.PayloadJson, "ownership_token");
                if (!string.IsNullOrWhiteSpace(ownershipToken))
                {
                    UploadText(readySftp, $"{remoteDir}/worker.lease", ownershipToken);
                    readySftp.ChangePermissions($"{remoteDir}/worker.lease", PrivatePayloadFileMode);
                }
                var readyPath = $"{remoteDir}/preparation.ready";
                UploadText(readySftp, readyPath, "ready\n");
                readySftp.ChangePermissions(readyPath, PrivatePayloadFileMode);
            }
            finally
            {
                readySftp.Disconnect();
            }
        }, cancellationToken);
    }

    private ConnectionInfo BuildConnectionInfo(string host, string username, string privateKeyContent)
    {
        var keyBytes = Encoding.UTF8.GetBytes(privateKeyContent);
        var keyStream = new MemoryStream(keyBytes);
        PrivateKeyFile keyFile = string.IsNullOrEmpty(_options.PrivateKeyPassphrase)
            ? new PrivateKeyFile(keyStream)
            : new PrivateKeyFile(keyStream, _options.PrivateKeyPassphrase);
        return new PrivateKeyConnectionInfo(host, _options.Port, username, keyFile)
        {
            Timeout = _options.ConnectionTimeout,
        };
    }

    private T CreateClient<T>(string host, Func<T> factory) where T : BaseClient
    {
        if (string.IsNullOrWhiteSpace(_options.HostKeyPinsFile))
        {
            return factory();
        }

        var expected = File.ReadLines(_options.HostKeyPinsFile)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length == 2 && fields[0].Equals(host, StringComparison.OrdinalIgnoreCase) &&
                fields[1].StartsWith("SHA256:", StringComparison.Ordinal))
            .Select(fields => fields[1])
            .ToHashSet(StringComparer.Ordinal);
        if (expected.Count == 0)
        {
            throw new InvalidOperationException($"No trusted SSH host key is configured for {host}. Verify the server key and add it before retrying.");
        }

        var client = factory();
        client.HostKeyReceived += (_, key) => key.CanTrust = expected.Contains("SHA256:" + key.FingerPrintSHA256);
        return client;
    }

    private void TryDeleteRemoteFile(SshDeploymentRequest request, string remotePath)
    {
        try
        {
            var connectionInfo = BuildConnectionInfo(request.Host, request.Username, request.PrivateKeyContent);
            using var cleanup = CreateClient(request.Host, () => new SftpClient(connectionInfo));
            cleanup.ConnectionInfo.Timeout = _options.ConnectionTimeout;
            cleanup.Connect();
            if (cleanup.Exists(remotePath))
            {
                cleanup.DeleteFile(remotePath);
            }
            cleanup.Disconnect();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove partial SFTP artifact {RemotePath}", remotePath);
        }
    }

    private static string? GetPayloadString(string payloadJson, string name)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private async Task<int> RunRemoteCommandAsync(
        string host,
        string username,
        string privateKeyContent,
        string commandText,
        TimeSpan commandTimeout,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken,
        string? remoteProcessPath)
    {
        var connectionInfo = BuildConnectionInfo(host, username, privateKeyContent);

        return await Task.Run(async () =>
        {
            using var client = CreateClient(host, () => new SshClient(connectionInfo));
            client.Connect();
            try
            {
                using var command = client.CreateCommand(commandText);
                command.CommandTimeout = commandTimeout;

                var asyncResult = command.BeginExecute();
                var executeTask = Task.Factory.FromAsync(asyncResult, _ => command.EndExecute(asyncResult));

                var stdoutTask = PumpLinesAsync(
                    new StreamReader(command.OutputStream, Encoding.UTF8),
                    "deploy",
                    onLogLine,
                    cancellationToken);
                var stderrTask = PumpLinesAsync(
                    new StreamReader(command.ExtendedOutputStream, Encoding.UTF8),
                    "deploy",
                    onLogLine,
                    cancellationToken);

                using var timeoutCts = new CancellationTokenSource(commandTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                try
                {
                    await Task.WhenAll(executeTask, stdoutTask, stderrTask).WaitAsync(linkedCts.Token);
                }
                catch (Exception) when (linkedCts.IsCancellationRequested)
                {
                    try
                    {
                        command.CancelAsync();
                    }
                    catch
                    {
                        // Best effort cancel on timeout.
                    }

                    if (!TerminateRemoteProcessGroup(client, remoteProcessPath))
                    {
                        throw new RemoteQuiescenceException(
                            "Remote deployment cancellation could not confirm process-group termination.");
                    }

                    if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Remote SSH command timed out after {commandTimeout.TotalMinutes:F1} minutes.");
                    }

                    throw new OperationCanceledException(cancellationToken);
                }
                catch (Exception) when (remoteProcessPath is not null)
                {
                    if (!TerminateRemoteProcessGroup(client, remoteProcessPath))
                    {
                        throw new RemoteQuiescenceException("SSH execution failed and remote deployment termination could not be confirmed.");
                    }
                    throw;
                }

                return command.ExitStatus ?? -1;
            }
            finally
            {
                client.Disconnect();
            }
        }, cancellationToken);
    }

    private static bool TerminateRemoteProcessGroup(SshClient client, string? remoteProcessPath)
    {
        if (string.IsNullOrWhiteSpace(remoteProcessPath))
        {
            return true;
        }
        try
        {
            var stopCommand =
                $"if test -f {EscapeShell(remoteProcessPath)}; then " +
                $"pid=$(cat {EscapeShell(remoteProcessPath)}); case \"$pid\" in ''|*[!0-9]*) exit 43;; esac; " +
                "kill -TERM -- -$pid 2>/dev/null || true; " +
                "i=0; while kill -0 -- -$pid 2>/dev/null && test $i -lt 120; do sleep 0.1; i=$((i+1)); done; " +
                "kill -KILL -- -$pid 2>/dev/null || true; " +
                "i=0; while kill -0 -- -$pid 2>/dev/null && test $i -lt 20; do sleep 0.1; i=$((i+1)); done; " +
                $"! kill -0 -- -$pid 2>/dev/null; else exit 42; fi";
            using var terminate = client.CreateCommand("bash -c " + EscapeShell(stopCommand));
            terminate.CommandTimeout = TimeSpan.FromSeconds(20);
            return terminate.Execute() == string.Empty && terminate.ExitStatus == 0;
        }
        catch { return false; } // Disconnection is not proof that remote execution stopped.
    }

    private static async Task PumpLinesAsync(
        StreamReader reader,
        string source,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken)
    {
        using (reader)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                await onLogLine(source, line);
            }
        }
    }

    private void UploadText(SftpClient sftp, string remotePath, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        sftp.UploadFile(stream, remotePath, true);
        _logger?.LogDebug("SFTP uploaded {RemotePath} ({Bytes} bytes)", remotePath, bytes.Length);
    }

    private static string EscapeShell(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
}
