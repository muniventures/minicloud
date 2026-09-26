using Minicloud.Cli.Api;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunStatusAsync(string[] args, CancellationToken cancellationToken)
    {
        var watch = args.Contains("--watch", StringComparer.Ordinal) || args.Contains("-w", StringComparer.Ordinal);
        var deploymentId = args.FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(deploymentId))
        {
            var me = await _apiClient.GetMeAsync(cancellationToken);
            var organization = me.Organizations.FirstOrDefault();
            if (organization is null)
            {
                _console.WriteError("Auth error: your token is not associated with an organization.");
                return CliExitCodes.AuthError;
            }

            var deployments = await _apiClient.GetDeploymentsAsync(organization.Id, appId: null, cancellationToken);
            var latest = deployments.FirstOrDefault();
            if (latest is null)
            {
                _console.WriteError($"No deployments found in organization '{organization.Name}'.");
                return CliExitCodes.ValidationError;
            }

            deploymentId = latest.Id;
        }

        var deployment = await _apiClient.GetDeploymentAsync(deploymentId, cancellationToken);
        if (!TerminalStatuses.Contains(deployment.Status))
        {
            if (watch)
            {
                PrintDeployment(deployment);
                deployment = await PollDeploymentAsync(deployment.Id, deployment.Status, cancellationToken);
                _console.WriteLine();
                PrintDeployment(deployment);
                return deployment.Status == "failed" ? CliExitCodes.DeploymentFailed : CliExitCodes.Success;
            }

            deployment = await _apiClient.RefreshDeploymentAsync(deploymentId, cancellationToken);
        }

        PrintDeployment(deployment);
        return deployment.Status == "failed" ? CliExitCodes.DeploymentFailed : CliExitCodes.Success;
    }

    private async Task<int> RunLogsAsync(string[] args, CancellationToken cancellationToken)
    {
        var appOrDeployment = args.FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal));
        var source = GetOption(args, "--source");
        var service = GetOption(args, "--service");
        var since = GetOption(args, "--since");
        var tailValue = GetOption(args, "--tail");
        var tail = int.TryParse(tailValue, out var parsedTail) ? parsedTail : 100;
        if (string.IsNullOrWhiteSpace(appOrDeployment))
        {
            var meForLatest = await _apiClient.GetMeAsync(cancellationToken);
            var organizationForLatest = meForLatest.Organizations.FirstOrDefault();
            if (organizationForLatest is null)
            {
                _console.WriteError("Auth error: your token is not associated with an organization.");
                return CliExitCodes.AuthError;
            }

            var deployments = await _apiClient.GetDeploymentsAsync(organizationForLatest.Id, appId: null, cancellationToken);
            var latest = deployments.FirstOrDefault();
            if (latest is null)
            {
                _console.WriteError($"No deployments found in organization '{organizationForLatest.Name}'.");
                return CliExitCodes.ValidationError;
            }

            appOrDeployment = latest.Id;
        }

        if (appOrDeployment.StartsWith("dep_", StringComparison.Ordinal))
        {
            var logs = await _apiClient.GetDeploymentLogsAsync(appOrDeployment, cancellationToken);
            foreach (var log in logs.Where(x => source is null || x.Source == source).TakeLast(tail))
            {
                _console.WriteLine($"{log.CreatedAt:O} [{log.Source}] {log.Content}");
            }

            return CliExitCodes.Success;
        }

        var me = await _apiClient.GetMeAsync(cancellationToken);
        var organization = me.Organizations.FirstOrDefault();
        if (organization is null)
        {
            _console.WriteError("Auth error: your token is not associated with an organization.");
            return CliExitCodes.AuthError;
        }

        var apps = await _apiClient.GetAppsAsync(organization.Id, cancellationToken);
        var app = FindApp(apps, appOrDeployment);
        if (app is null)
        {
            _console.WriteError($"App '{appOrDeployment}' was not found in organization '{organization.Name}'.");
            return CliExitCodes.ValidationError;
        }

        var runtimeLogs = await _apiClient.GetRuntimeLogsAsync(app.Id, source, service, tail, since, cancellationToken);
        foreach (var log in runtimeLogs)
        {
            _console.WriteLine($"{log.ObservedAt:O} [{log.Source}/{log.Stream}] {log.Content}");
        }

        return CliExitCodes.Success;
    }

    private async Task<DeploymentResponse> PollDeploymentAsync(
        string deploymentId,
        string initialStatus,
        CancellationToken cancellationToken,
        Rendering.IDeploymentRenderer? renderer = null,
        int totalServices = 0)
    {
        var lastStatus = initialStatus;
        string? lastConsoleUrl = null;
        var reportedInterruption = false;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            DeploymentResponse deployment;
            try
            {
                deployment = await _apiClient.RefreshDeploymentAsync(deploymentId, cancellationToken);
                if (reportedInterruption)
                {
                    if (renderer != null)
                    {
                        renderer.DeploymentReconnected(deploymentId, deployment.Status);
                    }
                    else
                    {
                        _console.WriteLine("Connection restored.");
                    }
                    reportedInterruption = false;
                }
            }
            catch (Exception ex) when (IsTransientNetworkException(ex, cancellationToken))
            {
                if (!reportedInterruption)
                {
                    if (renderer != null)
                    {
                        renderer.DeploymentReconnecting();
                    }
                    else
                    {
                        _console.WriteLine("Network connection lost. Waiting to reconnect...");
                    }
                    reportedInterruption = true;
                }

                continue;
            }

            if (deployment.Status != lastStatus)
            {
                if (renderer != null)
                {
                    renderer.DeploymentStatusUpdated(deployment.Id, deployment.Status, deployment.ConsoleUrl);
                }
                else
                {
                    _console.WriteLine($"Status: {deployment.Status}");
                }
                lastStatus = deployment.Status;
            }

            if (!TerminalStatuses.Contains(deployment.Status))
            {
                try
                {
                    var (phase, detail) = await InspectDeploymentActivityAsync(deployment.Id, deployment.Status, totalServices, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(phase))
                    {
                        if (renderer != null)
                        {
                            renderer.DeploymentActivityUpdated(deployment.Id, phase, detail);
                        }
                    }
                }
                catch
                {
                    // Activity inspection is best-effort and must not fail deployment polling.
                }
            }

            if (!string.IsNullOrWhiteSpace(deployment.ConsoleUrl) && deployment.ConsoleUrl != lastConsoleUrl)
            {
                if (renderer == null)
                {
                    WriteUrlLine("Console", deployment.ConsoleUrl);
                }
                lastConsoleUrl = deployment.ConsoleUrl;
            }

            if (TerminalStatuses.Contains(deployment.Status))
            {
                return deployment;
            }
        }
    }

    private async Task<(string? Phase, string? Detail)> InspectDeploymentActivityAsync(
        string deploymentId,
        string deploymentStatus,
        int totalServices,
        CancellationToken cancellationToken)
    {
        string? phase = deploymentStatus switch
        {
            "provisioning" => "Provisioning server",
            "verifying" => "Verifying health checks",
            _ => null
        };
        string? detail = null;

        try
        {
            var events = await _apiClient.GetDeploymentEventsAsync(deploymentId, cancellationToken);
            if (events is { Count: > 0 })
            {
                var lastEvent = events[^1];
                var msg = lastEvent.Message;
                if (msg.Contains("health check", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("health-finalize", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("verifying", StringComparison.OrdinalIgnoreCase))
                {
                    phase = "Verifying health checks";
                }
                else if (msg.Contains("Transferring deployment payload", StringComparison.OrdinalIgnoreCase))
                {
                    phase = "Transferring payload to server";
                }
                else if (msg.Contains("DNS", StringComparison.OrdinalIgnoreCase))
                {
                    phase = "Configuring DNS records";
                }
                else if (msg.Contains("Provisioning", StringComparison.OrdinalIgnoreCase) ||
                         msg.Contains("Resolved registered server", StringComparison.OrdinalIgnoreCase))
                {
                    phase = "Provisioning server";
                }
            }
        }
        catch
        {
            // Ignore event query errors
        }

        if (deploymentStatus == "deploying")
        {
            try
            {
                var logs = await _apiClient.GetDeploymentLogsAsync(deploymentId, cancellationToken);
                if (logs is { Count: > 0 })
                {
                    var builtServices = new HashSet<string>(StringComparer.Ordinal);
                    string? currentRemotePhase = null;
                    string? currentBuildingService = null;
                    string? latestBuildStep = null;
                    string? latestServiceLine = null;

                    foreach (var log in logs)
                    {
                        var content = log.Content.Trim();

                        if (content.StartsWith("===== phase: ", StringComparison.Ordinal) && content.EndsWith(" =====", StringComparison.Ordinal))
                        {
                            currentRemotePhase = content["===== phase: ".Length..^" =====".Length].Trim();
                            continue;
                        }

                        if (content.StartsWith("TIMING phase=service_build", StringComparison.Ordinal) && content.Contains("outcome=succeeded", StringComparison.Ordinal))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(content, @"\bservice=([^\s]+)");
                            if (match.Success)
                            {
                                builtServices.Add(match.Groups[1].Value);
                            }
                        }

                        if (content.StartsWith("Building Docker image", StringComparison.Ordinal))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(content, @"\bfor service ([^\s\.]+)");
                            if (match.Success)
                            {
                                currentBuildingService = match.Groups[1].Value;
                            }
                        }

                        if (content.StartsWith("[build:", StringComparison.Ordinal))
                        {
                            var endIdx = content.IndexOf(']');
                            if (endIdx > 7)
                            {
                                var svc = content[7..endIdx];
                                var subline = content[(endIdx + 1)..].Trim();
                                currentBuildingService = svc;
                                if (subline.StartsWith("Step ", StringComparison.Ordinal) ||
                                    subline.StartsWith("#", StringComparison.Ordinal) ||
                                    subline.Contains("RUN ", StringComparison.Ordinal) ||
                                    subline.Contains("COPY ", StringComparison.Ordinal))
                                {
                                    latestBuildStep = $"{svc}: {subline}";
                                }
                            }
                        }

                        if (content.StartsWith("Creating minicloud-", StringComparison.Ordinal) ||
                            content.StartsWith("Starting minicloud-", StringComparison.Ordinal))
                        {
                            latestServiceLine = content;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(currentRemotePhase))
                    {
                        switch (currentRemotePhase)
                        {
                            case "preflight":
                            case "acquire_lock":
                            case "prepare_paths":
                            case "render_next_configs":
                            case "validate_next_configs":
                                phase = "Preparing host environment";
                                break;

                            case "build_artifacts":
                                var buildCount = builtServices.Count;
                                phase = totalServices > 0
                                    ? $"Building Docker images ({buildCount}/{totalServices})"
                                    : "Building Docker images";
                                detail = latestBuildStep ?? (currentBuildingService != null ? $"building image for {currentBuildingService}" : null);
                                break;

                            case "wait_for_rollout_gate":
                                phase = "Waiting for rollout gate";
                                break;

                            case "migrate_postgres_if_needed":
                            case "ensure_postgres_data_owner":
                                phase = "Preparing database";
                                break;

                            case "start_infra":
                            case "sync_postgres_password":
                            case "configure_openbao":
                            case "start_observability":
                                phase = "Starting infrastructure";
                                break;

                            case "start_app_services":
                                phase = "Starting application services";
                                detail = latestServiceLine;
                                break;

                            case "verify_local_health":
                                phase = "Verifying container health";
                                break;

                            case "activate_caddy":
                                phase = "Configuring traffic routing";
                                break;

                            case "record_deploy":
                            case "complete":
                                phase = "Finalizing deployment";
                                break;
                        }
                    }
                }
            }
            catch
            {
                // Ignore log query errors
            }
        }

        return (phase, detail);
    }

    private static string FormatBranchUrls(AppBranchResponse branch) =>
        branch.Urls is { Count: > 0 } ? string.Join(", ", branch.Urls) : "no service URLs";

    private void PrintServiceUrls(IReadOnlyList<DeploymentServiceResponse> services)
    {
        foreach (var service in services.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            foreach (var url in (service.Urls ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                WriteUrlLine($"Service URL ({service.Name})", url);
            }
        }
    }

    private void PrintDeployment(DeploymentResponse deployment)
    {
        _console.WriteLine($"Deployment: {deployment.Id}");
        _console.WriteLine($"App: {deployment.AppId}");
        _console.WriteLine($"Status: {deployment.Status}");
        PrintServiceUrls(deployment.Services);
        if (!string.IsNullOrWhiteSpace(deployment.ConsoleUrl))
        {
            WriteUrlLine("Console", deployment.ConsoleUrl);
        }

        _console.WriteLine($"Created: {deployment.CreatedAt:O}");
        if (deployment.CompletedAt is not null)
        {
            _console.WriteLine($"Completed: {deployment.CompletedAt:O}");
        }
    }
}
