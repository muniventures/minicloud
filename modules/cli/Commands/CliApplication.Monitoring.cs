// SPDX-License-Identifier: MPL-2.0

using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunStatusAsync(string[] args, CancellationToken cancellationToken)
    {
        var watch = args.Contains("--watch", StringComparer.Ordinal) || args.Contains("-w", StringComparer.Ordinal);
        var diagnostics = args.Contains("--diagnostics", StringComparer.Ordinal);
        var deploymentNumber = FirstPositional(args);
        if (deploymentNumber is not null)
        {
            ValidateDeploymentNumberArgument(deploymentNumber);
        }

        var organization = await ResolveDeploymentOrganizationAsync(args, cancellationToken);
        deploymentNumber ??= await GetLatestDeploymentNumberAsync(organization, cancellationToken);

        var deployment = await _apiClient.GetDeploymentAsync(organization.Id, deploymentNumber, cancellationToken);
        if (!TerminalStatuses.Contains(deployment.Status))
        {
            if (watch)
            {
                PrintDeployment(deployment, diagnostics);
                deployment = await PollDeploymentAsync(organization.Id, deployment.Number, deployment.Status, cancellationToken);
                _console.WriteLine();
                PrintDeployment(deployment, diagnostics);
                return deployment.Status is "failed" or "canceled" ? CliExitCodes.DeploymentFailed : CliExitCodes.Success;
            }

            deployment = await _apiClient.RefreshDeploymentAsync(organization.Id, deployment.Number, cancellationToken);
        }

        PrintDeployment(deployment, diagnostics);
        return deployment.Status is "failed" or "canceled" ? CliExitCodes.DeploymentFailed : CliExitCodes.Success;
    }

    private async Task<int> RunLogsAsync(string[] args, CancellationToken cancellationToken)
    {
        var appOrDeployment = FirstPositional(args);
        var source = GetOption(args, "--source");
        var service = GetOption(args, "--service");
        var since = GetOption(args, "--since");
        var tailValue = GetOption(args, "--tail");
        var tail = int.TryParse(tailValue, out var parsedTail) ? parsedTail : 100;
        if (appOrDeployment is null || LooksLikeDeploymentReference(appOrDeployment))
        {
            if (appOrDeployment is not null)
            {
                ValidateDeploymentNumberArgument(appOrDeployment);
            }

            var organization = await ResolveDeploymentOrganizationAsync(args, cancellationToken);
            var deploymentNumber = appOrDeployment ?? await GetLatestDeploymentNumberAsync(organization, cancellationToken);
            var deployment = await _apiClient.GetDeploymentAsync(organization.Id, deploymentNumber, cancellationToken);
            PrintDeploymentFailure(deployment);
            var logs = await _apiClient.GetDeploymentLogsAsync(organization.Id, deployment.Number, cancellationToken);
            foreach (var log in logs.Where(x => source is null || x.Source == source).TakeLast(tail))
            {
                _console.WriteLine($"{log.CreatedAt:O} [{log.Source}] {log.Content}");
            }

            return CliExitCodes.Success;
        }

        var appArgs = args.Concat(["--app", appOrDeployment]).ToArray();
        var app = await ResolveAppOptionAsync(appArgs, cancellationToken);

        var runtimeLogs = await _apiClient.GetRuntimeLogsAsync(app.Id, source, service, tail, since, cancellationToken);
        foreach (var log in runtimeLogs)
        {
            _console.WriteLine($"{log.ObservedAt:O} [{log.Source}/{log.Stream}] {log.Content}");
        }

        return CliExitCodes.Success;
    }

    // App slugs never contain '.', so dotted numeric input and legacy opaque IDs are deployment references.
    internal static bool LooksLikeDeploymentReference(string value) =>
        value.StartsWith("dep_", StringComparison.Ordinal) ||
        (value.Contains('.') && value.All(x => char.IsAsciiDigit(x) || x == '.'));

    private static void ValidateDeploymentNumberArgument(string value)
    {
        if (!DeploymentNumber.IsValid(value))
        {
            throw new CliCommandException(
                CliExitCodes.ValidationError,
                $"'{value}' is not a deployment number. Use the number shown by Minicloud, for example 20261110.1.");
        }
    }

    private async Task<OrganizationSummaryDto> ResolveDeploymentOrganizationAsync(string[] args, CancellationToken cancellationToken)
    {
        var requested = GetOption(args, "--organization");
        if (args.Any(x => x == "--organization" || x.StartsWith("--organization=", StringComparison.Ordinal)) && string.IsNullOrWhiteSpace(requested))
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "--organization requires an organization ID.");
        }

        var me = await _apiClient.GetMeAsync(cancellationToken);
        if (me.Organizations.Count == 0)
        {
            throw new CliCommandException(CliExitCodes.AuthError, "Auth error: your token is not associated with an organization.");
        }

        if (requested is not null)
        {
            return me.Organizations.FirstOrDefault(x => string.Equals(x.Id, requested, StringComparison.Ordinal))
                ?? throw new CliCommandException(CliExitCodes.AuthError, "The selected organization is not available to this token.");
        }

        if (me.Organizations.Count == 1)
        {
            return me.Organizations[0];
        }

        var path = GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath();
        if (File.Exists(path))
        {
            var config = MinicloudConfigLoader.Load(path);
            if (config.IsValid && !string.IsNullOrWhiteSpace(config.Config?.AppId))
            {
                var application = await GetApplicationContextAsync(config.Config.AppId, cancellationToken);
                return me.Organizations.FirstOrDefault(x => x.Id == application.OrganizationId)
                    ?? throw new CliCommandException(CliExitCodes.AuthError, $"Auth error: your token is not associated with app '{config.Config.AppId}'.");
            }
        }

        throw new CliCommandException(
            CliExitCodes.ValidationError,
            "Deployment numbers are scoped to an organization. Select one with --organization <id> or run the command next to an application's minicloud.yml.");
    }

    private async Task<string> GetLatestDeploymentNumberAsync(OrganizationSummaryDto organization, CancellationToken cancellationToken)
    {
        var deployments = await _apiClient.GetDeploymentsAsync(organization.Id, appId: null, cancellationToken);
        return deployments.FirstOrDefault()?.Number
            ?? throw new CliCommandException(CliExitCodes.ValidationError, $"No deployments found in organization '{organization.Name}'.");
    }

    private async Task<DeploymentResponse> PollDeploymentAsync(
        string organizationId,
        string deploymentNumber,
        string initialStatus,
        CancellationToken cancellationToken,
        Rendering.IDeploymentRenderer? renderer = null,
        int totalServices = 0)
    {
        var lastStatus = initialStatus;
        string? lastConsoleUrl = null;
        var reportedInterruption = false;
        var reportedCancellation = false;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            DeploymentResponse deployment;
            try
            {
                deployment = await _apiClient.RefreshDeploymentAsync(organizationId, deploymentNumber, cancellationToken);
                if (reportedInterruption)
                {
                    if (renderer != null)
                    {
                        renderer.DeploymentReconnected(deployment.Number, deployment.Status);
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

            if (!reportedCancellation && deployment.CancellationRequestedAt is not null && !TerminalStatuses.Contains(deployment.Status))
            {
                _console.WriteLine("Cancellation requested. Waiting for deployment execution to stop...");
                reportedCancellation = true;
            }
            if (deployment.Status != lastStatus)
            {
                if (renderer != null)
                {
                    renderer.DeploymentStatusUpdated(deployment.Number, deployment.Status, deployment.ConsoleUrl);
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
                    var (phase, detail) = await InspectDeploymentActivityAsync(organizationId, deployment.Number, deployment.Status, totalServices, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(phase))
                    {
                        if (renderer != null)
                        {
                            renderer.DeploymentActivityUpdated(deployment.Number, phase, detail);
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
        string organizationId,
        string deploymentNumber,
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
            var events = await _apiClient.GetDeploymentEventsAsync(organizationId, deploymentNumber, cancellationToken);
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
                var logs = await _apiClient.GetDeploymentLogsAsync(organizationId, deploymentNumber, cancellationToken);
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

    private void PrintDeploymentFailure(DeploymentResponse deployment)
    {
        if (!string.IsNullOrWhiteSpace(deployment.FailureCode))
        {
            _console.WriteError($"Failure code: {deployment.FailureCode}");
        }
        if (!string.IsNullOrWhiteSpace(deployment.FailureMessage))
        {
            _console.WriteError($"Failure: {deployment.FailureMessage}");
        }
    }

    private void PrintDeployment(DeploymentResponse deployment, bool diagnostics = false)
    {
        _console.WriteLine($"Deployment: {deployment.Number}");
        if (diagnostics)
        {
            _console.WriteLine($"Deployment ID (diagnostic correlation): {deployment.Id}");
        }
        _console.WriteLine($"App: {deployment.AppId}");
        _console.WriteLine($"Status: {deployment.Status}");
        PrintDeploymentFailure(deployment);
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
