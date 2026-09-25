using System.Diagnostics;

namespace Minicloud.Cli.Rendering;

public sealed class PlainTextDeploymentRenderer : IDeploymentRenderer
{
    private readonly IConsole _console;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    private DeploymentPlan? _plan;
    private DeploymentProgressTracker? _tracker;
    private long _startTimestamp;
    private int _lastReportedPercentage = -1;

    private bool _secretsStarted;
    private int _syncedSecretsCount;

    private bool _artifactsStarted;
    private readonly HashSet<string> _uploadedServices = new(StringComparer.Ordinal);

    private bool _deployStarted;
    private string? _lastDeployStatus;
    private bool _completed;

    public PlainTextDeploymentRenderer(IConsole console, TimeProvider? timeProvider = null)
    {
        _console = console;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Initialize(DeploymentPlan plan)
    {
        lock (_lock)
        {
            _plan = plan;
            _tracker = new DeploymentProgressTracker(plan);
            _startTimestamp = _timeProvider.GetTimestamp();
        }
    }

    public void SecretStarted(string serviceName, string secretName)
    {
        lock (_lock)
        {
            if (!_secretsStarted)
            {
                _secretsStarted = true;
                _console.WriteLine("Secrets: syncing local secrets...");
            }
        }
    }

    public void SecretCompleted(string serviceName, string secretName)
    {
        lock (_lock)
        {
            _syncedSecretsCount++;
            _console.WriteLine($"Secrets: synced {serviceName}/{secretName}");
            _tracker?.OnSecretCompleted();
            CheckProgress();

            if (_plan != null && _syncedSecretsCount >= _plan.TotalSecrets)
            {
                _console.WriteLine($"Secrets: {_syncedSecretsCount} synced");
            }
        }
    }

    public void ArtifactPackageStarted(string serviceName)
    {
        lock (_lock)
        {
            EnsureArtifactsStarted();
        }
    }

    public void ArtifactPackageCompleted(string serviceName, long byteCount)
    {
        lock (_lock)
        {
            EnsureArtifactsStarted();
            _console.WriteLine($"Artifacts: bundled {serviceName}");
            _tracker?.OnArtifactOpCompleted();
            CheckProgress();
        }
    }

    public void ArtifactUploadStarted(string serviceName, long byteCount)
    {
        lock (_lock)
        {
            EnsureArtifactsStarted();
        }
    }

    public void ArtifactUploadCompleted(string serviceName, long byteCount)
    {
        lock (_lock)
        {
            EnsureArtifactsStarted();
            _uploadedServices.Add(serviceName);
            var sizeStr = TerminalTextHelper.FormatByteCount(byteCount);
            _console.WriteLine($"Artifacts: uploaded {serviceName} ({sizeStr})");
            _tracker?.OnArtifactOpCompleted();
            CheckProgress();

            if (_plan != null && _uploadedServices.Count >= _plan.SourceServices.Count)
            {
                _console.WriteLine($"Artifacts: {_uploadedServices.Count} uploaded");
            }
        }
    }

    public void DeploymentCreationStarted()
    {
        lock (_lock)
        {
            if (!_deployStarted)
            {
                _deployStarted = true;
                _console.WriteLine("Deploy: creating deployment...");
            }
        }
    }

    public void DeploymentCreated(string deploymentId, string status)
    {
        lock (_lock)
        {
            _deployStarted = true;
            _lastDeployStatus = status;
            _console.WriteLine($"Deploy: created deployment {deploymentId} (status: {status})");
            _tracker?.OnDeploymentCreated();
            CheckProgress();
        }
    }

    public void DeploymentStatusUpdated(string deploymentId, string status, string? consoleUrl = null)
    {
        lock (_lock)
        {
            if (status != _lastDeployStatus)
            {
                _lastDeployStatus = status;
                _console.WriteLine($"Deploy: status {status}");
            }
        }
    }

    public void DeploymentReconnecting()
    {
        lock (_lock)
        {
            _console.WriteLine("Deploy: network connection lost, waiting to reconnect...");
        }
    }

    public void DeploymentReconnected(string deploymentId, string status)
    {
        lock (_lock)
        {
            _console.WriteLine("Deploy: connection restored");
            _lastDeployStatus = status;
        }
    }

    public void DeploymentSucceeded(DeploymentSuccessResult result)
    {
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;

            _console.WriteLine("Deploy: succeeded");
            _tracker?.OnDeploymentSucceeded();
            ReportFinalProgress(100);

            var lines = new List<string>
            {
                "Deployment complete",
                $"{result.ServicesCount} service(s) deployed successfully"
            };

            if (!string.IsNullOrWhiteSpace(result.ConsoleUrl))
            {
                lines.Add($"Console: {result.ConsoleUrl}");
            }

            foreach (var (serviceName, url) in result.ServiceUrls.OrderBy(x => x.ServiceName, StringComparer.Ordinal).ThenBy(x => x.Url, StringComparer.Ordinal))
            {
                lines.Add($"Service URL ({serviceName}): {url}");
            }

            var box = TerminalTextHelper.FormatBox(lines, isSuccess: true, _console.WindowWidth, isInteractive: false, supportsAnsi: false, noColor: true);
            foreach (var line in box)
            {
                _console.WriteLine(line);
            }
        }
    }

    public void DeploymentFailed(DeploymentFailureResult result)
    {
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;

            _console.WriteLine("Deploy: failed");
            _tracker?.OnDeploymentFailed();
            ReportFinalProgress(_tracker?.CurrentPercentage ?? 0);

            var lines = new List<string>
            {
                "Deployment failed"
            };

            if (!string.IsNullOrWhiteSpace(result.FailureCode))
            {
                lines.Add(result.FailureCode);
            }

            if (!string.IsNullOrWhiteSpace(result.FailureMessage))
            {
                lines.Add(result.FailureMessage);
            }

            if (!string.IsNullOrWhiteSpace(result.DeploymentId))
            {
                lines.Add($"Logs: minicloud logs {result.DeploymentId}");
            }
            else
            {
                lines.Add("Logs: minicloud logs");
            }

            if (!string.IsNullOrWhiteSpace(result.ConsoleUrl))
            {
                lines.Add($"Console: {result.ConsoleUrl}");
            }

            var box = TerminalTextHelper.FormatBox(lines, isSuccess: false, _console.WindowWidth, isInteractive: false, supportsAnsi: false, noColor: true);
            foreach (var line in box)
            {
                _console.WriteLine(line);
            }
        }
    }

    private void EnsureArtifactsStarted()
    {
        if (!_artifactsStarted)
        {
            _artifactsStarted = true;
            _console.WriteLine("Artifacts: packaging and uploading artifacts...");
        }
    }

    private void CheckProgress()
    {
        if (_tracker == null) return;
        var current = _tracker.CurrentPercentage;
        if (_lastReportedPercentage < 0)
        {
            if (current >= 10)
            {
                ReportProgress(current);
            }
        }
        else if (current - _lastReportedPercentage >= 10)
        {
            ReportProgress(current);
        }
    }

    private void ReportProgress(int percentage)
    {
        _lastReportedPercentage = percentage;
        _console.WriteLine($"Overall: {percentage}% ({FormatElapsed()})");
    }

    private void ReportFinalProgress(int percentage)
    {
        if (_lastReportedPercentage != percentage)
        {
            _lastReportedPercentage = percentage;
            _console.WriteLine($"Overall: {percentage}% ({FormatElapsed()})");
        }
    }

    private string FormatElapsed()
    {
        var elapsed = _timeProvider.GetElapsedTime(_startTimestamp);
        var totalSec = (int)elapsed.TotalSeconds;
        if (totalSec < 60)
        {
            return $"{totalSec}s";
        }
        var mins = totalSec / 60;
        var secs = totalSec % 60;
        return $"{mins}m {secs}s";
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
