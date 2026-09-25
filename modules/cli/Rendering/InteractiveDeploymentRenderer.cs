using System.Diagnostics;

namespace Minicloud.Cli.Rendering;

public sealed class InteractiveDeploymentRenderer : IDeploymentRenderer
{
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private readonly IConsole _console;
    private readonly TimeProvider _timeProvider;
    private readonly bool _noColor;
    private readonly bool _enableSpinnerTimer;
    private readonly object _lock = new();

    private Timer? _spinnerTimer;
    private int _spinnerFrameIndex;
    private long _startTimestamp;
    private int _renderedLineCount;
    private bool _isCompleted;
    private bool _disposed;

    private DeploymentPlan? _plan;
    private DeploymentProgressTracker? _tracker;

    // Stage states: 0 = Pending, 1 = Active, 2 = Completed, 3 = Failed
    private enum StageState { Pending, Active, Completed, Failed }

    // Secrets stage
    private StageState _secretsState = StageState.Pending;
    private string? _secretsSummary;
    private string? _activeSecretService;
    private string? _activeSecretName;
    private int _completedSecretsCount;

    // Artifacts stage
    private StageState _artifactsState = StageState.Pending;
    private string? _artifactsSummary;
    private readonly Dictionary<string, string> _artifactServiceStates = new(StringComparer.Ordinal);
    private int _uploadedArtifactsCount;

    // Deploy stage
    private StageState _deployState = StageState.Pending;
    private string? _deploySummary;
    private string _deployDetail = "creating deployment";
    private bool _deployReconnecting;
    private string? _deployStatus;

    public InteractiveDeploymentRenderer(
        IConsole console,
        TimeProvider? timeProvider = null,
        bool? noColor = null,
        bool enableSpinnerTimer = true)
    {
        _console = console;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _noColor = noColor ?? !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        _enableSpinnerTimer = enableSpinnerTimer;
    }

    public void Initialize(DeploymentPlan plan)
    {
        lock (_lock)
        {
            _plan = plan;
            _tracker = new DeploymentProgressTracker(plan);
            _startTimestamp = _timeProvider.GetTimestamp();

            if (plan.HasSecretsStage && plan.TotalSecrets > 0)
            {
                _secretsState = StageState.Active;
            }
            else if (plan.HasArtifactsStage && plan.SourceServices.Count > 0)
            {
                _artifactsState = StageState.Active;
                foreach (var service in plan.SourceServices)
                {
                    _artifactServiceStates[service] = "bundling";
                }
            }
            else if (plan.HasDeployStage)
            {
                _deployState = StageState.Active;
            }

            // Hide cursor in interactive mode
            _console.Write("\x1b[?25l");

            if (_enableSpinnerTimer)
            {
                _spinnerTimer = new Timer(OnSpinnerTick, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
            }

            Redraw();
        }
    }

    public void SecretStarted(string serviceName, string secretName)
    {
        lock (_lock)
        {
            _secretsState = StageState.Active;
            _activeSecretService = serviceName;
            _activeSecretName = secretName;
            Redraw();
        }
    }

    public void SecretCompleted(string serviceName, string secretName)
    {
        lock (_lock)
        {
            _completedSecretsCount++;
            _tracker?.OnSecretCompleted();

            if (_plan != null && _completedSecretsCount >= _plan.TotalSecrets)
            {
                _secretsState = StageState.Completed;
                _secretsSummary = $"{_completedSecretsCount} synced";
                _activeSecretService = null;
                _activeSecretName = null;

                // Move next stage to active
                if (_plan.HasArtifactsStage && _plan.SourceServices.Count > 0)
                {
                    _artifactsState = StageState.Active;
                    foreach (var s in _plan.SourceServices)
                    {
                        if (!_artifactServiceStates.ContainsKey(s))
                        {
                            _artifactServiceStates[s] = "bundling";
                        }
                    }
                }
                else if (_plan.HasDeployStage)
                {
                    _deployState = StageState.Active;
                }
            }

            Redraw();
        }
    }

    public void ArtifactPackageStarted(string serviceName)
    {
        lock (_lock)
        {
            _artifactsState = StageState.Active;
            _artifactServiceStates[serviceName] = "bundling";
            Redraw();
        }
    }

    public void ArtifactPackageCompleted(string serviceName, long byteCount)
    {
        lock (_lock)
        {
            _artifactsState = StageState.Active;
            _artifactServiceStates[serviceName] = "bundled";
            _tracker?.OnArtifactOpCompleted();
            Redraw();
        }
    }

    public void ArtifactUploadStarted(string serviceName, long byteCount)
    {
        lock (_lock)
        {
            _artifactsState = StageState.Active;
            _artifactServiceStates[serviceName] = "uploading";
            Redraw();
        }
    }

    public void ArtifactUploadCompleted(string serviceName, long byteCount)
    {
        lock (_lock)
        {
            _uploadedArtifactsCount++;
            var sizeStr = TerminalTextHelper.FormatByteCount(byteCount);
            _artifactServiceStates[serviceName] = $"uploaded ({sizeStr})";
            _tracker?.OnArtifactOpCompleted();

            if (_plan != null && _uploadedArtifactsCount >= _plan.SourceServices.Count)
            {
                _artifactsState = StageState.Completed;
                _artifactsSummary = $"{_uploadedArtifactsCount} uploaded";

                if (_plan.HasDeployStage)
                {
                    _deployState = StageState.Active;
                }
            }

            Redraw();
        }
    }

    public void DeploymentCreationStarted()
    {
        lock (_lock)
        {
            _deployState = StageState.Active;
            _deployDetail = "creating deployment";
            Redraw();
        }
    }

    public void DeploymentCreated(string deploymentId, string status)
    {
        lock (_lock)
        {
            _deployState = StageState.Active;
            _deployStatus = status;
            _deployDetail = $"{deploymentId}  {status}";
            _tracker?.OnDeploymentCreated();
            Redraw();
        }
    }

    public void DeploymentStatusUpdated(string deploymentId, string status, string? consoleUrl = null)
    {
        lock (_lock)
        {
            _deployStatus = status;
            _deployReconnecting = false;
            _deployDetail = $"{deploymentId}  {status}";
            Redraw();
        }
    }

    public void DeploymentReconnecting()
    {
        lock (_lock)
        {
            _deployReconnecting = true;
            Redraw();
        }
    }

    public void DeploymentReconnected(string deploymentId, string status)
    {
        lock (_lock)
        {
            _deployReconnecting = false;
            _deployStatus = status;
            _deployDetail = $"{deploymentId}  {status}";
            Redraw();
        }
    }

    public void DeploymentSucceeded(DeploymentSuccessResult result)
    {
        lock (_lock)
        {
            if (_isCompleted) return;
            _isCompleted = true;
            StopTimer();

            _deployState = StageState.Completed;
            _deploySummary = "succeeded";
            _tracker?.OnDeploymentSucceeded();

            // Final render of live area (collapsed)
            Redraw(isFinal: true);

            // Restore cursor
            _console.Write("\x1b[?25h");
            _console.WriteLine();

            // Render success box
            var lines = new List<string>
            {
                "Deployment complete",
                $"{result.ServicesCount} service(s) deployed successfully"
            };

            if (!string.IsNullOrWhiteSpace(result.ConsoleUrl))
            {
                var link = TerminalTextHelper.FormatTerminalLink(result.ConsoleUrl, result.ConsoleUrl, _console.SupportsAnsi);
                lines.Add($"Console: {link}");
            }

            foreach (var (serviceName, url) in result.ServiceUrls.OrderBy(x => x.ServiceName, StringComparer.Ordinal).ThenBy(x => x.Url, StringComparer.Ordinal))
            {
                var link = TerminalTextHelper.FormatTerminalLink(url, url, _console.SupportsAnsi);
                lines.Add($"Service URL ({serviceName}): {link}");
            }

            var boxLines = TerminalTextHelper.FormatBox(
                lines,
                isSuccess: true,
                _console.WindowWidth,
                isInteractive: true,
                supportsAnsi: _console.SupportsAnsi,
                noColor: _noColor);

            foreach (var line in boxLines)
            {
                _console.WriteLine(line);
            }
        }
    }

    public void DeploymentFailed(DeploymentFailureResult result)
    {
        lock (_lock)
        {
            if (_isCompleted) return;
            _isCompleted = true;
            StopTimer();

            _deployState = StageState.Failed;
            _deploySummary = result.FailureCode ?? "failed";
            _tracker?.OnDeploymentFailed();

            // Final render of live area (collapsed)
            Redraw(isFinal: true);

            // Restore cursor
            _console.Write("\x1b[?25h");
            _console.WriteLine();

            // Render failure box
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
                var link = TerminalTextHelper.FormatTerminalLink(result.ConsoleUrl, result.ConsoleUrl, _console.SupportsAnsi);
                lines.Add($"Console: {link}");
            }

            var boxLines = TerminalTextHelper.FormatBox(
                lines,
                isSuccess: false,
                _console.WindowWidth,
                isInteractive: true,
                supportsAnsi: _console.SupportsAnsi,
                noColor: _noColor);

            foreach (var line in boxLines)
            {
                _console.WriteLine(line);
            }
        }
    }

    private void OnSpinnerTick(object? state)
    {
        lock (_lock)
        {
            if (_isCompleted || _disposed) return;
            _spinnerFrameIndex = (_spinnerFrameIndex + 1) % SpinnerFrames.Length;
            Redraw();
        }
    }

    private void Redraw(bool isFinal = false)
    {
        if (_plan == null) return;

        var termWidth = _console.WindowWidth;
        var useColor = !_noColor && _console.SupportsAnsi;

        var gray = useColor ? "\x1b[90m" : "";
        var cyan = useColor ? "\x1b[36m" : "";
        var green = useColor ? "\x1b[32m" : "";
        var amber = useColor ? "\x1b[33m" : "";
        var red = useColor ? "\x1b[31m" : "";
        var bold = useColor ? "\x1b[1m" : "";
        var reset = useColor ? "\x1b[0m" : "";

        var lines = new List<string>();

        // 1. Overall progress bar
        var percentage = _tracker?.CurrentPercentage ?? 0;
        var elapsedStr = FormatElapsed();

        var barWidth = Math.Max(10, Math.Min(20, termWidth - 30));
        var filledCount = (int)Math.Round((percentage / 100.0) * barWidth);
        filledCount = Math.Clamp(filledCount, 0, barWidth);
        var unfilledCount = barWidth - filledCount;

        var filledBlocks = new string('█', filledCount);
        var unfilledBlocks = new string('░', unfilledCount);

        var barStr = $"{bold}Overall{reset}  {gray}[{reset}{green}{filledBlocks}{reset}{gray}{unfilledBlocks}]{reset}  {cyan}{percentage}%{reset}  {gray}({elapsedStr}){reset}";
        lines.Add(barStr);

        var spinner = SpinnerFrames[_spinnerFrameIndex % SpinnerFrames.Length];

        // 2. Stage rows
        // Secrets stage
        if (_plan.HasSecretsStage && _plan.TotalSecrets > 0)
        {
            switch (_secretsState)
            {
                case StageState.Pending:
                    lines.Add($"  {gray}○{reset} Secrets  {gray}({_plan.TotalSecrets}){reset}");
                    break;
                case StageState.Active when !isFinal:
                    lines.Add($"  {cyan}{spinner}{reset} Secrets  {gray}{_completedSecretsCount}/{_plan.TotalSecrets}{reset}");
                    if (!string.IsNullOrWhiteSpace(_activeSecretService) && !string.IsNullOrWhiteSpace(_activeSecretName))
                    {
                        lines.Add($"      {_activeSecretService}  {_activeSecretName}");
                    }
                    break;
                case StageState.Completed:
                case StageState.Active when isFinal:
                    lines.Add($"  {green}✓{reset} Secrets  {gray}{_secretsSummary ?? $"{_completedSecretsCount} synced"}{reset}");
                    break;
                case StageState.Failed:
                    lines.Add($"  {red}✗{reset} Secrets  {gray}{_secretsSummary ?? "failed"}{reset}");
                    break;
            }
        }

        // Artifacts stage
        if (_plan.HasArtifactsStage && _plan.SourceServices.Count > 0)
        {
            switch (_artifactsState)
            {
                case StageState.Pending:
                    lines.Add($"  {gray}○{reset} Artifacts  {gray}({_plan.SourceServices.Count}){reset}");
                    break;
                case StageState.Active when !isFinal:
                    lines.Add($"  {cyan}{spinner}{reset} Artifacts  {gray}{_uploadedArtifactsCount}/{_plan.SourceServices.Count}{reset}");
                    foreach (var service in _plan.SourceServices.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var status = _artifactServiceStates.GetValueOrDefault(service, "bundling");
                        lines.Add($"      {service}  {status}");
                    }
                    break;
                case StageState.Completed:
                case StageState.Active when isFinal:
                    lines.Add($"  {green}✓{reset} Artifacts  {gray}{_artifactsSummary ?? $"{_uploadedArtifactsCount} uploaded"}{reset}");
                    break;
                case StageState.Failed:
                    lines.Add($"  {red}✗{reset} Artifacts  {gray}{_artifactsSummary ?? "failed"}{reset}");
                    break;
            }
        }

        // Deploy stage
        if (_plan.HasDeployStage)
        {
            switch (_deployState)
            {
                case StageState.Pending:
                    lines.Add($"  {gray}○{reset} Deploy");
                    break;
                case StageState.Active when !isFinal:
                    lines.Add($"  {cyan}{spinner}{reset} Deploy");
                    if (_deployReconnecting)
                    {
                        lines.Add($"      {amber}Network connection lost. Waiting to reconnect...{reset}");
                    }
                    else
                    {
                        lines.Add($"      {_deployDetail}");
                    }
                    break;
                case StageState.Completed:
                    lines.Add($"  {green}✓{reset} Deploy  {gray}{_deploySummary ?? "succeeded"}{reset}");
                    break;
                case StageState.Failed:
                    lines.Add($"  {red}✗{reset} Deploy  {gray}{_deploySummary ?? "failed"}{reset}");
                    break;
            }
        }

        // Render lines with cursor positioning
        if (_renderedLineCount > 0)
        {
            _console.Write($"\r\x1b[{_renderedLineCount}A\x1b[0J");
        }

        foreach (var line in lines)
        {
            _console.WriteLine(TerminalTextHelper.Truncate(line, termWidth));
        }

        _renderedLineCount = lines.Count;
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

    private void StopTimer()
    {
        _spinnerTimer?.Dispose();
        _spinnerTimer = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            StopTimer();

            // Always restore cursor on disposal
            _console.Write("\x1b[?25h");

            if (!_isCompleted && _renderedLineCount > 0)
            {
                _console.WriteLine();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
