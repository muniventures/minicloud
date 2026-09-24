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

    private async Task<DeploymentResponse> PollDeploymentAsync(string deploymentId, string initialStatus, CancellationToken cancellationToken)
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
                    _console.WriteLine("Connection restored.");
                    reportedInterruption = false;
                }
            }
            catch (Exception ex) when (IsTransientNetworkException(ex, cancellationToken))
            {
                if (!reportedInterruption)
                {
                    _console.WriteLine("Network connection lost. Waiting to reconnect...");
                    reportedInterruption = true;
                }

                continue;
            }

            if (deployment.Status != lastStatus)
            {
                _console.WriteLine($"Status: {deployment.Status}");
                lastStatus = deployment.Status;
            }
            if (!string.IsNullOrWhiteSpace(deployment.ConsoleUrl) && deployment.ConsoleUrl != lastConsoleUrl)
            {
                WriteUrlLine("Console", deployment.ConsoleUrl);
                lastConsoleUrl = deployment.ConsoleUrl;
            }

            if (TerminalStatuses.Contains(deployment.Status))
            {
                return deployment;
            }
        }
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
