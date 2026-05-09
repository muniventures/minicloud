using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Municloud.Cli.Api;
using Municloud.Cli.Auth;
using Municloud.Cli.Config;

namespace Municloud.Cli.Commands;

public sealed partial class CliApplication
{
    internal const string DeploymentImagePlatform = "linux/amd64";
    private const int PublishConcurrency = 2;

    private static readonly ISet<string> TerminalStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "succeeded",
        "failed",
        "canceled"
    };

    private readonly IConsole _console;
    private readonly CliEnvironment _environment;
    private readonly TokenStore _tokenStore;
    private readonly MunicloudApiClient _apiClient;
    private readonly RegistryImageMapper _registryImageMapper;

    public CliApplication(IConsole console, CliEnvironment environment, TokenStore tokenStore, MunicloudApiClient apiClient)
    {
        _console = console;
        _environment = environment;
        _tokenStore = tokenStore;
        _apiClient = apiClient;
        _registryImageMapper = new RegistryImageMapper(environment);
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return CliExitCodes.Success;
        }

        if (args[0] == "--env")
        {
            PrintEnvironment();
            return CliExitCodes.Success;
        }

        try
        {
            return args[0] switch
            {
                "token" => await RunTokenAsync(args.Skip(1).ToArray(), cancellationToken),
                "init" => RunInit(args.Skip(1).ToArray()),
                "login" => await RunLoginAsync(args.Skip(1).ToArray(), cancellationToken),
                "deploy" => await RunDeployAsync(args.Skip(1).ToArray(), cancellationToken),
                "status" => await RunStatusAsync(args.Skip(1).ToArray(), cancellationToken),
                "logs" => await RunLogsAsync(args.Skip(1).ToArray(), cancellationToken),
                "apps" => await RunAppsAsync(args.Skip(1).ToArray(), cancellationToken),
                _ => UnknownCommand(args[0])
            };
        }
        catch (ApiException ex) when (ex.StatusCode is 401 or 403 || ex.Code == "missing_token")
        {
            _console.WriteError($"Auth error: {ex.Message}");
            return CliExitCodes.AuthError;
        }
        catch (ApiException ex)
        {
            _console.WriteError($"API error ({ex.Code}): {ex.Message}");
            return CliExitCodes.NetworkOrApiUnavailable;
        }
        catch (HttpRequestException ex)
        {
            _console.WriteError($"Network error: {ex.Message}");
            return CliExitCodes.NetworkOrApiUnavailable;
        }
        catch (TaskCanceledException)
        {
            _console.WriteError("Network error: request timed out or was canceled.");
            return CliExitCodes.NetworkOrApiUnavailable;
        }
        catch (CliCommandException ex)
        {
            _console.WriteError(ex.Message);
            return ex.ExitCode;
        }
    }

    private async Task<int> RunTokenAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "set")
        {
            _console.WriteError("Usage: municloud token set <token>");
            return CliExitCodes.ValidationError;
        }

        var token = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(token))
        {
            _console.WriteError("Usage: municloud token set <token>");
            return CliExitCodes.ValidationError;
        }

        await _apiClient.GetMeWithTokenAsync(token, cancellationToken);
        _tokenStore.SaveToken(token);
        _console.WriteLine("Token stored.");
        return CliExitCodes.Success;
    }

    private async Task<int> RunLoginAsync(string[] args, CancellationToken cancellationToken)
    {
        var token = GetOption(args, "--token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            var me = await _apiClient.GetMeWithTokenAsync(token, cancellationToken);
            _tokenStore.SaveToken(token);
            _console.WriteLine($"Logged in as {me.Email}");
            var organization = me.Organizations.FirstOrDefault();
            if (organization is not null)
            {
                _console.WriteLine($"Organization: {organization.Name}");
            }

            return CliExitCodes.Success;
        }

        var noBrowser = args.Contains("--no-browser", StringComparer.Ordinal);
        var session = await _apiClient.CreateCliLoginSessionAsync(cancellationToken);
        if (!noBrowser && TryOpenBrowser(session.LoginUrl))
        {
            _console.WriteLine("Opening browser for Municloud login...");
        }
        else
        {
            _console.WriteLine("Open this URL to finish Municloud login:");
            _console.WriteLine(session.LoginUrl);
        }

        var exchange = await PollCliLoginExchangeAsync(session.SessionId, session.ExpiresAt, cancellationToken);
        _tokenStore.SaveToken(exchange.Token);
        _console.WriteLine($"Logged in as {exchange.Email}");
        _console.WriteLine($"Organization: {exchange.Organization.Name}");
        _console.WriteLine($"Token: {MaskToken(exchange.Token)}");
        return CliExitCodes.Success;
    }

    private int RunInit(string[] args)
    {
        var outputPath = GetOption(args, "--config") ?? "municloud.yml";
        var advanced = args.Contains("--advanced", StringComparer.Ordinal);
        if (File.Exists(outputPath) && !args.Contains("--force", StringComparer.Ordinal))
        {
            if (!Confirm($"'{outputPath}' already exists. Overwrite it?", defaultValue: false))
            {
                _console.WriteLine("Init canceled.");
                return CliExitCodes.Success;
            }
        }

        _console.WriteLine("Municloud init");
        _console.WriteLine();

        var app = PromptSlug("App slug", DefaultSlugFromDirectory());
        var deploymentType = PromptDeploymentType();
        var database = PromptChoice("Database", [("sqlite", "SQLite"), ("postgres", "Postgres")], "sqlite");
        var environment = advanced ? PromptSlug("Environment", "staging") : "staging";

        var services = new Dictionary<string, MunicloudServiceConfig>(StringComparer.Ordinal);
        foreach (var serviceName in PromptServiceNamesFor(deploymentType))
        {
            _console.WriteLine();
            _console.WriteLine($"{ToTitle(serviceName)} service");
            var sourcePath = PromptDirectory($"{ToTitle(serviceName)} source folder", DefaultSourcePath(serviceName));
            var defaults = DefaultServiceOptions(app, serviceName, deploymentType);
            var image = advanced ? PromptOptional($"{ToTitle(serviceName)} push image", defaults.Image) : null;
            var port = advanced ? PromptPort($"{ToTitle(serviceName)} port", defaults.Port) : defaults.Port;
            var routePath = advanced ? PromptPath($"{ToTitle(serviceName)} public path", defaults.Path) : defaults.Path;
            var healthPath = advanced ? PromptPath($"{ToTitle(serviceName)} health path", defaults.HealthPath) : defaults.HealthPath;

            services[serviceName] = new MunicloudServiceConfig(sourcePath, null, image, port, true, routePath, healthPath);
        }

        var config = new MunicloudConfig(app, environment, deploymentType, database, null, services);
        var diagnostics = MunicloudConfigValidator.Validate(config);
        if (diagnostics.Count > 0)
        {
            PrintDiagnostics(diagnostics);
            return CliExitCodes.ValidationError;
        }

        File.WriteAllText(outputPath, MunicloudConfigWriter.Write(config));
        _console.WriteLine();
        _console.WriteLine($"Created {outputPath}");
        if (!advanced)
        {
            _console.WriteLine("Used defaults for environment, registry image refs, ports, routes, and health checks.");
            _console.WriteLine("Run 'municloud init --advanced' to customize every option.");
        }
        _console.WriteLine($"Next: municloud deploy --config {outputPath}");
        return CliExitCodes.Success;
    }

    private async Task<int> RunDeployAsync(string[] args, CancellationToken cancellationToken)
    {
        var configPath = GetOption(args, "--config") ?? MunicloudConfigLoader.ResolveDefaultPath();
        var appOverride = GetOption(args, "--app");
        var environmentOverride = GetOption(args, "--environment");
        var deploymentTypeOverride = GetOption(args, "--deployment-type");
        var databaseOverride = GetOption(args, "--database");
        var postgresPassword = GetOption(args, "--pgpassword");
        var imageTag = GetOption(args, "--tag") ?? "latest";
        var noPublish = args.Contains("--no-publish", StringComparer.Ordinal);
        var publishOnly = args.Contains("--publish-only", StringComparer.Ordinal);
        var verbose = args.Contains("--verbose", StringComparer.Ordinal);
        var configResult = MunicloudConfigLoader.Load(configPath);
        if (!configResult.IsValid || configResult.Config is null)
        {
            PrintDiagnostics(configResult.Diagnostics);
            return CliExitCodes.ValidationError;
        }

        var config = configResult.Config;
        if (publishOnly)
        {
            if (noPublish)
            {
                _console.WriteError("Usage error: --publish-only cannot be combined with --no-publish.");
                return CliExitCodes.ValidationError;
            }

            await PublishServiceImagesAsync(config, imageTag, verbose, cancellationToken);
            _console.WriteLine("Published images:");
            foreach (var (serviceName, service) in config.Services)
            {
                var pushImage = PushImageForService(config, serviceName, service, imageTag);
                _console.WriteLine($"  {serviceName}: {_registryImageMapper.RuntimeImageForDeployment(pushImage, _environment.LocalOrganizationSlug)}");
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

        var appSlug = appOverride ?? config.App;
        var apps = await _apiClient.GetAppsAsync(organization.Id, cancellationToken);
        var app = FindApp(apps, appSlug);
        if (app is null)
        {
            app = await CreateAppFromConfigAsync(config, organization, appSlug, environmentOverride, deploymentTypeOverride, databaseOverride, cancellationToken);
        }

        if (!noPublish)
        {
            await PublishServiceImagesAsync(config, imageTag, verbose, cancellationToken);
        }
        else
        {
            var imageDiagnostics = ValidateExplicitImagesForNoPublish(config);
            if (imageDiagnostics.Count > 0)
            {
                PrintDiagnostics(imageDiagnostics);
                return CliExitCodes.ValidationError;
            }
        }

        var request = new CreateDeploymentRequest(
            app.Id,
            environmentOverride ?? config.Environment ?? app.DefaultEnvironment,
            deploymentTypeOverride ?? config.DeploymentType ?? app.DeploymentType,
            databaseOverride ?? config.Database ?? app.Database,
            config.CommitSha,
            config.Services.Select(x => new DeploymentServiceRequest(
                x.Key,
                DeploymentImageForService(config, x.Key, x.Value, organization.Slug, noPublish, imageTag),
                x.Value.Port!.Value,
                x.Value.Public!.Value,
                x.Value.Path!,
                x.Value.HealthPath!,
                x.Value.Env)).ToArray(),
            postgresPassword);

        var created = await _apiClient.CreateDeploymentAsync(request, cancellationToken);
        _console.WriteLine($"Municloud deployment {created.Id}");
        _console.WriteLine($"Status: {created.Status}");
        if (!string.IsNullOrWhiteSpace(created.PostgresPassword))
        {
            _console.WriteLine($"Postgres password: {created.PostgresPassword}");
        }

        var finalDeployment = await PollDeploymentAsync(created.Id, created.Status, cancellationToken);
        if (finalDeployment.Status == "succeeded")
        {
            if (!string.IsNullOrWhiteSpace(finalDeployment.WebsiteUrl))
            {
                WriteUrlLine("Website URL", finalDeployment.WebsiteUrl);
            }

            return CliExitCodes.Success;
        }

        _console.WriteLine($"Failure: {finalDeployment.FailureCode ?? finalDeployment.Status}");
        if (!string.IsNullOrWhiteSpace(finalDeployment.FailureMessage))
        {
            _console.WriteLine($"Message: {finalDeployment.FailureMessage}");
        }

        _console.WriteLine($"Logs: municloud logs {finalDeployment.Id}");
        return CliExitCodes.DeploymentFailed;
    }

    private async Task<int> RunStatusAsync(string[] args, CancellationToken cancellationToken)
    {
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
        var environment = GetOption(args, "--environment");
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

        var runtimeLogs = await _apiClient.GetRuntimeLogsAsync(app.Id, environment, source, service, tail, since, cancellationToken);
        foreach (var log in runtimeLogs)
        {
            _console.WriteLine($"{log.ObservedAt:O} [{log.Source}/{log.Stream}] {log.Content}");
        }

        return CliExitCodes.Success;
    }

    private async Task<int> RunAppsAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _console.WriteError("Usage: municloud apps list|inspect <app>");
            return CliExitCodes.ValidationError;
        }

        return args[0] switch
        {
            "list" => await RunAppsListAsync(cancellationToken),
            "inspect" => await RunAppsInspectAsync(args.Skip(1).ToArray(), cancellationToken),
            _ => UnknownCommand($"apps {args[0]}")
        };
    }

    private async Task<int> RunAppsListAsync(CancellationToken cancellationToken)
    {
        var me = await _apiClient.GetMeAsync(cancellationToken);
        var organization = me.Organizations.FirstOrDefault();
        if (organization is null)
        {
            _console.WriteError("Auth error: your token is not associated with an organization.");
            return CliExitCodes.AuthError;
        }

        var apps = await _apiClient.GetAppsAsync(organization.Id, cancellationToken);
        foreach (var app in apps)
        {
            _console.WriteLine($"{app.Slug}\t{app.DefaultEnvironment}\t{app.DeploymentType}\t{app.Database}");
        }

        return CliExitCodes.Success;
    }

    private async Task<int> RunAppsInspectAsync(string[] args, CancellationToken cancellationToken)
    {
        var appIdOrSlug = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(appIdOrSlug))
        {
            _console.WriteError("Usage: municloud apps inspect <app>");
            return CliExitCodes.ValidationError;
        }

        var me = await _apiClient.GetMeAsync(cancellationToken);
        var organization = me.Organizations.FirstOrDefault();
        if (organization is null)
        {
            _console.WriteError("Auth error: your token is not associated with an organization.");
            return CliExitCodes.AuthError;
        }

        var apps = await _apiClient.GetAppsAsync(organization.Id, cancellationToken);
        var app = FindApp(apps, appIdOrSlug);
        if (app is null)
        {
            _console.WriteError($"App '{appIdOrSlug}' was not found.");
            return CliExitCodes.ValidationError;
        }

        _console.WriteLine($"App: {app.Name}");
        _console.WriteLine($"Slug: {app.Slug}");
        _console.WriteLine($"Environment: {app.DefaultEnvironment}");
        _console.WriteLine($"Deployment type: {app.DeploymentType}");
        _console.WriteLine($"Database: {app.Database}");
        if (app.LatestDeployment is not null)
        {
            _console.WriteLine($"Latest deployment: {app.LatestDeployment.Id} ({app.LatestDeployment.Status})");
            if (!string.IsNullOrWhiteSpace(app.LatestDeployment.WebsiteUrl))
            {
                WriteUrlLine("Website URL", app.LatestDeployment.WebsiteUrl);
            }
        }

        return CliExitCodes.Success;
    }

    private async Task<AppResponse> CreateAppFromConfigAsync(
        MunicloudConfig config,
        OrganizationSummary organization,
        string appSlug,
        string? environmentOverride,
        string? deploymentTypeOverride,
        string? databaseOverride,
        CancellationToken cancellationToken)
    {
        var createSlug = appSlug.ToLowerInvariant();
        var request = new CreateAppRequest(
            organization.Id,
            DisplayNameFromSlug(createSlug),
            createSlug,
            environmentOverride ?? config.Environment ?? "staging",
            "p0",
            deploymentTypeOverride ?? config.DeploymentType ?? "custom",
            databaseOverride ?? config.Database ?? "sqlite");

        _console.WriteLine($"App '{createSlug}' was not found in organization '{organization.Name}'. Creating it from municloud.yml...");
        return await _apiClient.CreateAppAsync(request, cancellationToken);
    }

    private static AppResponse? FindApp(IEnumerable<AppResponse> apps, string appIdOrSlug) =>
        apps.FirstOrDefault(x =>
            string.Equals(x.Slug, appIdOrSlug, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Id, appIdOrSlug, StringComparison.OrdinalIgnoreCase));

    private static string DisplayNameFromSlug(string slug)
    {
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return slug;
        }

        return string.Join(" ", words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private async Task<DeploymentResponse> PollDeploymentAsync(string deploymentId, string initialStatus, CancellationToken cancellationToken)
    {
        var lastStatus = initialStatus;
        string? lastConsoleUrl = null;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var deployment = await _apiClient.RefreshDeploymentAsync(deploymentId, cancellationToken);
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

    private async Task<CliLoginSessionExchangeResponse> PollCliLoginExchangeAsync(string sessionId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var exchange = await _apiClient.ExchangeCliLoginSessionAsync(sessionId, cancellationToken);
            if (exchange is not null)
            {
                return exchange;
            }

            _console.WriteLine("Waiting for browser approval...");
        }

        throw new ApiException(401, "cli_login_session_expired", "CLI login session expired before approval.");
    }

    private void PrintDeployment(DeploymentResponse deployment)
    {
        _console.WriteLine($"Deployment: {deployment.Id}");
        _console.WriteLine($"App: {deployment.AppId}");
        _console.WriteLine($"Environment: {deployment.Environment}");
        _console.WriteLine($"Status: {deployment.Status}");
        if (!string.IsNullOrWhiteSpace(deployment.WebsiteUrl))
        {
            WriteUrlLine("Website URL", deployment.WebsiteUrl);
        }
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

    private void PrintDiagnostics(IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            _console.WriteError($"{diagnostic.Field}: {diagnostic.Message}");
        }
    }

    private void WriteUrlLine(string label, string url) =>
        _console.WriteLine($"{label}: {FormatTerminalLink(url, url)}");

    private string FormatTerminalLink(string url, string text)
    {
        if (!_console.SupportsAnsi)
        {
            return text;
        }

        var safeUrl = StripAnsiControlCharacters(url);
        var safeText = StripAnsiControlCharacters(text);
        return $"\x1b]8;;{safeUrl}\x1b\\{safeText}\x1b]8;;\x1b\\";
    }

    private static string StripAnsiControlCharacters(string value) =>
        new(value.Where(character => !char.IsControl(character) || character is '\t').ToArray());

    private int UnknownCommand(string command)
    {
        _console.WriteError($"Unknown command '{command}'.");
        PrintHelp();
        return CliExitCodes.ValidationError;
    }

    private void PrintHelp()
    {
        _console.WriteLine("Municloud CLI");
        _console.WriteLine();
        _console.WriteLine("Usage:");
        _console.WriteLine("  municloud login --token <token>");
        _console.WriteLine("  municloud init [--advanced] [--config municloud.yml] [--force]");
        _console.WriteLine("  municloud token set <token>");
        _console.WriteLine("  municloud deploy [--config municloud.yml] [--app app] [--environment env] [--deployment-type type] [--database db] [--pgpassword password] [--tag tag] [--no-publish] [--publish-only] [--verbose]");
        _console.WriteLine("  municloud status [deployment-id]");
        _console.WriteLine("  municloud logs [app|deployment-id] [--environment env] [--service service] [--source source] [--tail count] [--since 30m]");
        _console.WriteLine("  municloud apps list");
        _console.WriteLine("  municloud apps inspect <app>");
        _console.WriteLine("  municloud --env");
    }

    private void PrintEnvironment()
    {
        _console.WriteLine("Municloud CLI environment");
        _console.WriteLine($"Environment: {CliEnvironment.ApiUrlEnvironmentVariable} defaults to {_environment.ApiBaseUrl}");
        _console.WriteLine($"Registry: {CliEnvironment.RegistryHostEnvironmentVariable} defaults to {_environment.RegistryHost}");
        _console.WriteLine($"Runtime registry owner: {CliEnvironment.RegistryGhcrOwnerEnvironmentVariable} defaults to {_environment.RegistryGhcrOwner}");
        _console.WriteLine($"Runtime registry prefix: {CliEnvironment.RuntimeRegistryPrefixEnvironmentVariable} defaults to {_environment.RuntimeRegistryPrefix}");
    }

    private static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == name)
            {
                return i + 1 < args.Count ? args[i + 1] : null;
            }

            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }

    private string PromptDeploymentType()
    {
        _console.WriteLine("Deployment type:");
        _console.WriteLine("  1. backend");
        _console.WriteLine("  2. frontend");
        _console.WriteLine("  3. both");
        _console.WriteLine("  4. custom");

        while (true)
        {
            var value = PromptRequired("Choose deployment type", "both").Trim().ToLowerInvariant();
            switch (value)
            {
                case "1":
                case "backend":
                case "backend_only":
                    return "backend_only";
                case "2":
                case "frontend":
                case "frontend_only":
                    return "frontend_only";
                case "3":
                case "both":
                case "backend_frontend":
                    return "backend_frontend";
                case "4":
                case "custom":
                    return "custom";
            }

            _console.WriteError("Choose backend, frontend, both, or custom.");
        }
    }

    private string PromptChoice(string label, IReadOnlyList<(string Value, string Label)> choices, string defaultValue)
    {
        _console.WriteLine($"{label}: {string.Join(", ", choices.Select(x => x.Value))}");
        while (true)
        {
            var value = PromptRequired(label, defaultValue).Trim().ToLowerInvariant();
            if (choices.Any(x => x.Value == value))
            {
                return value;
            }

            _console.WriteError($"{label} must be one of: {string.Join(", ", choices.Select(x => x.Value))}.");
        }
    }

    private string PromptSlug(string label, string defaultValue)
    {
        while (true)
        {
            var value = PromptRequired(label, defaultValue).Trim().ToLowerInvariant();
            if (MunicloudConfigLoader.SlugRegex().IsMatch(value))
            {
                return value;
            }

            _console.WriteError($"{label} must use lowercase letters, numbers, and dashes.");
        }
    }

    private string PromptDirectory(string label, string defaultValue)
    {
        while (true)
        {
            var value = PromptRequired(label, defaultValue).Trim();
            if (Directory.Exists(value))
            {
                return value;
            }

            _console.WriteError($"Directory '{value}' does not exist.");
        }
    }

    private int PromptPort(string label, int defaultValue)
    {
        while (true)
        {
            var value = PromptRequired(label, defaultValue.ToString()).Trim();
            if (int.TryParse(value, out var port) && port is >= 1 and <= 65535)
            {
                return port;
            }

            _console.WriteError($"{label} must be a port between 1 and 65535.");
        }
    }

    private string PromptPath(string label, string defaultValue)
    {
        while (true)
        {
            var value = PromptRequired(label, defaultValue).Trim();
            if (value.StartsWith("/", StringComparison.Ordinal))
            {
                return value;
            }

            _console.WriteError($"{label} must start with '/'.");
        }
    }

    private string PromptRequired(string label, string defaultValue)
    {
        while (true)
        {
            _console.WriteLine($"{label} [{defaultValue}]:");
            var value = _console.ReadLine();
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return value.Trim();
        }
    }

    private string? PromptOptional(string label, string defaultValue)
    {
        _console.WriteLine($"{label} [{defaultValue}]:");
        var value = _console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private bool Confirm(string label, bool defaultValue)
    {
        var suffix = defaultValue ? "Y/n" : "y/N";
        _console.WriteLine($"{label} [{suffix}]:");
        var value = _console.ReadLine();
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ||
            value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ServiceNamesFor(string deploymentType) =>
        deploymentType switch
        {
            "backend_only" => ["backend"],
            "frontend_only" => ["frontend"],
            "custom" => [],
            _ => ["frontend", "backend"]
        };

    private IReadOnlyList<string> PromptServiceNamesFor(string deploymentType)
    {
        if (deploymentType != "custom")
        {
            return ServiceNamesFor(deploymentType);
        }

        var count = PromptServiceCount();
        var names = new List<string>();
        while (names.Count < count)
        {
            var serviceName = PromptSlug($"Service {names.Count + 1} name", names.Count == 0 ? "web" : $"service-{names.Count + 1}");
            if (names.Contains(serviceName, StringComparer.Ordinal))
            {
                _console.WriteError("Service names must be unique.");
                continue;
            }

            names.Add(serviceName);
        }

        return names;
    }

    private int PromptServiceCount()
    {
        while (true)
        {
            var value = PromptRequired("Number of services", "3").Trim();
            if (int.TryParse(value, out var count) && count is >= 1 and <= MunicloudConfigValidator.MaxDeploymentServices)
            {
                return count;
            }

            _console.WriteError($"Service count must be between 1 and {MunicloudConfigValidator.MaxDeploymentServices}.");
        }
    }

    private static string DefaultSlugFromDirectory()
    {
        var name = new DirectoryInfo(Environment.CurrentDirectory).Name.ToLowerInvariant();
        var slug = string.Concat(name.Select(character => char.IsLetterOrDigit(character) ? character : '-')).Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "my-app" : slug;
    }

    private static string DefaultSourcePath(string serviceName)
    {
        var candidates = serviceName switch
        {
            "frontend" or "dashboard" => new[] { "frontend", "dashboard", "web", "client", "modules/frontend", "modules/dashboard", "modules/web", "modules/ui" },
            "backend" or "api" => new[] { "backend", "api", "server", "modules/backend", "modules/api", "modules/server" },
            "registry" => new[] { "registry", "modules/registry" },
            _ => [serviceName, $"modules/{serviceName}", "."]
        };

        return candidates.FirstOrDefault(Directory.Exists) ?? ".";
    }

    private (string Image, int Port, string Path, string HealthPath) DefaultServiceOptions(string app, string serviceName, string deploymentType) =>
        serviceName switch
        {
            "frontend" or "dashboard" => ($"{_environment.RegistryHost}/{app}/{serviceName}:latest", 3000, "/", "/"),
            "backend" or "api" => ($"{_environment.RegistryHost}/{app}/{serviceName}:latest", 8080, deploymentType == "backend_frontend" || serviceName == "api" ? "/api" : "/", "/health"),
            "registry" => ($"{_environment.RegistryHost}/{app}/registry:latest", 8080, "/", "/health"),
            _ => ($"{_environment.RegistryHost}/{app}/{serviceName}:latest", 8080, "/", "/")
        };

    private async Task PublishServiceImagesAsync(MunicloudConfig config, string imageTag, bool verbose, CancellationToken cancellationToken)
    {
        var publishJobs = new List<ServicePublishJob>();
        foreach (var (serviceName, service) in config.Services)
        {
            var pushImage = PushImageForService(config, serviceName, service, imageTag);
            if (!_registryImageMapper.UsesMunicloudRegistry(pushImage))
            {
                throw new CliCommandException(
                    CliExitCodes.ValidationError,
                    $"Config error: services.{serviceName}.image must start with '{_environment.RegistryHost}/' when publishing. Current value is '{pushImage}'. Remove image to let the CLI derive it, update municloud.yml, or pass --no-publish to deploy an already-published image.");
            }

            if (string.IsNullOrWhiteSpace(service.SourcePath))
            {
                throw new CliCommandException(CliExitCodes.ValidationError, $"Config error: services.{serviceName}.sourcePath is required for image publishing. Add it or pass --no-publish.");
            }

            if (!Directory.Exists(service.SourcePath))
            {
                throw new CliCommandException(CliExitCodes.ValidationError, $"Config error: services.{serviceName}.sourcePath '{service.SourcePath}' does not exist.");
            }

            var dockerfileDiagnostics = ValidateDockerfileForService(serviceName, service);
            if (dockerfileDiagnostics.Count > 0)
            {
                PrintDiagnostics(dockerfileDiagnostics);
                throw new CliCommandException(CliExitCodes.ValidationError, "Dockerfile validation failed.");
            }

            publishJobs.Add(new ServicePublishJob(
                serviceName,
                service,
                pushImage,
                BuildCacheImageFor(pushImage),
                ComputeServiceFingerprint(service)));
        }

        if (publishJobs.Count > 0)
        {
            var token = _tokenStore.GetToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new CliCommandException(CliExitCodes.AuthError, "Auth error: run 'municloud login' before publishing images to the Municloud registry.");
            }

            _console.WriteLine($"Logging in to {_environment.RegistryHost}...");
            await RunProcessAsync(
                "docker",
                ["login", _environment.RegistryHost, "-u", "municloud", "--password-stdin"],
                Environment.CurrentDirectory,
                cancellationToken,
                standardInput: token + Environment.NewLine,
                verbose: verbose,
                progressLabel: "Authenticating with registry");
        }

        using var progressView = verbose ? null : new PublishProgressView(_console, publishJobs);
        var publishCache = PublishCache.Load(_environment);
        var publishCacheLock = new object();
        var jobsToPublish = new List<ServicePublishJob>();
        foreach (var job in publishJobs)
        {
            if (publishCache.IsCurrent(config.App, job.ServiceName, job.PushImage, DeploymentImagePlatform, job.Fingerprint))
            {
                if (progressView is null)
                {
                    _console.WriteLine($"Publishing services: skipped {job.ServiceName} - unchanged");
                }
                else
                {
                    progressView.MarkSkipped(job.ServiceName);
                }

                continue;
            }

            jobsToPublish.Add(job);
        }

        if (jobsToPublish.Count == 0)
        {
            if (progressView is null)
            {
                _console.WriteLine("Publishing services: all services unchanged");
            }
            else
            {
                progressView.Complete();
            }

            return;
        }

        var started = 0;
        using var semaphore = new SemaphoreSlim(PublishConcurrency);
        var publishTasks = jobsToPublish.Select(async job =>
        {
            await semaphore.WaitAsync(cancellationToken);
            var serviceIndex = Interlocked.Increment(ref started);
            try
            {
                await PublishServiceImageAsync(job, serviceIndex, jobsToPublish.Count, verbose, progressView, cancellationToken);
                lock (publishCacheLock)
                {
                    publishCache.MarkCurrent(config.App, job.ServiceName, job.PushImage, DeploymentImagePlatform, job.Fingerprint);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(publishTasks);
            progressView?.Complete();
        }
        catch
        {
            progressView?.Complete();
            throw;
        }

        publishCache.Save(_environment);
    }

    private async Task PublishServiceImageAsync(ServicePublishJob job, int serviceIndex, int serviceCount, bool verbose, PublishProgressView? progressView, CancellationToken cancellationToken)
    {
        progressView?.MarkPublishing(job.ServiceName);
        if (progressView is null)
        {
            _console.WriteLine($"Publishing services: {serviceIndex} of {serviceCount} - {job.ServiceName}");
            _console.WriteLine($"  Image: {job.PushImage} ({DeploymentImagePlatform})");
            _console.WriteLine($"  Cache: {job.CacheImage}");
        }

        try
        {
            var buildArgs = DockerBuildxBuildArgumentsForService(job.Service, job.PushImage, job.CacheImage);
            await RunProcessAsync(
                "docker",
                buildArgs,
                Environment.CurrentDirectory,
                cancellationToken,
                verbose: verbose,
                progressLabel: progressView is null ? $"  {job.ServiceName}" : job.ServiceName,
                progressView: progressView);
            progressView?.MarkDone(job.ServiceName);
        }
        catch
        {
            progressView?.MarkFailed(job.ServiceName);
            throw;
        }
    }

    internal static IReadOnlyList<string> DockerBuildxBuildArgumentsForService(MunicloudServiceConfig service, string pushImage, string? cacheImage = null)
    {
        var buildArgs = new List<string> { "buildx", "build", "--progress", "plain", "--platform", DeploymentImagePlatform, "--push", "-t", pushImage };
        if (!string.IsNullOrWhiteSpace(cacheImage))
        {
            buildArgs.Add("--cache-from");
            buildArgs.Add($"type=registry,ref={cacheImage}");
            buildArgs.Add("--cache-to");
            buildArgs.Add($"type=registry,ref={cacheImage},mode=max");
        }

        if (!string.IsNullOrWhiteSpace(service.Dockerfile))
        {
            buildArgs.Add("-f");
            buildArgs.Add(service.Dockerfile);
        }

        buildArgs.Add(service.SourcePath!);
        return buildArgs;
    }

    internal static IReadOnlyList<ConfigDiagnostic> ValidateDockerfileForService(string serviceName, MunicloudServiceConfig service)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var dockerfilePath = EffectiveDockerfilePath(service);
        if (!File.Exists(dockerfilePath))
        {
            diagnostics.Add(new ConfigDiagnostic(
                $"services.{serviceName}.dockerfile",
                $"Dockerfile '{dockerfilePath}' was not found. Add a Dockerfile or set services.{serviceName}.dockerfile."));
            return diagnostics;
        }

        var dockerfile = File.ReadAllLines(dockerfilePath);
        if (!DockerfileContainsInstruction(dockerfile, "FROM"))
        {
            diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}.dockerfile", "Dockerfile must contain at least one FROM instruction."));
        }

        if (!DockerfileContainsInstruction(dockerfile, "CMD") && !DockerfileContainsInstruction(dockerfile, "ENTRYPOINT"))
        {
            diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}.dockerfile", "Dockerfile must define CMD or ENTRYPOINT so the service starts when deployed."));
        }

        if (service.Port is { } port && !DockerfileExposesPort(dockerfile, port))
        {
            diagnostics.Add(new ConfigDiagnostic(
                $"services.{serviceName}.port",
                $"Dockerfile must include EXPOSE {port} to match services.{serviceName}.port."));
        }

        return diagnostics;
    }

    private static string EffectiveDockerfilePath(MunicloudServiceConfig service) =>
        string.IsNullOrWhiteSpace(service.Dockerfile)
            ? Path.Combine(service.SourcePath!, "Dockerfile")
            : service.Dockerfile;

    private static bool DockerfileContainsInstruction(IEnumerable<string> dockerfile, string instruction) =>
        dockerfile.Any(line => DockerfileInstruction(line).Equals(instruction, StringComparison.OrdinalIgnoreCase));

    private static bool DockerfileExposesPort(IEnumerable<string> dockerfile, int port) =>
        dockerfile
            .Where(line => DockerfileInstruction(line).Equals("EXPOSE", StringComparison.OrdinalIgnoreCase))
            .SelectMany(DockerfileArguments)
            .Select(argument => argument.Split('/', 2)[0])
            .Any(exposedPort => exposedPort == port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string DockerfileInstruction(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            return "";
        }

        var separatorIndex = trimmed.IndexOfAny([' ', '\t']);
        return separatorIndex < 0 ? trimmed : trimmed[..separatorIndex];
    }

    private static IEnumerable<string> DockerfileArguments(string line)
    {
        var trimmed = line.TrimStart();
        var separatorIndex = trimmed.IndexOfAny([' ', '\t']);
        if (separatorIndex < 0)
        {
            return [];
        }

        return trimmed[(separatorIndex + 1)..]
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(argument => argument.Trim());
    }

    private IReadOnlyList<ConfigDiagnostic> ValidateExplicitImagesForNoPublish(MunicloudConfig config)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        foreach (var (serviceName, service) in config.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Image))
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}.image", "Image is required when using --no-publish."));
            }
        }

        return diagnostics;
    }

    private string DeploymentImageForService(MunicloudConfig config, string serviceName, MunicloudServiceConfig service, string organizationSlug, bool noPublish, string imageTag)
    {
        var image = noPublish ? service.Image! : PushImageForService(config, serviceName, service, imageTag);
        return _registryImageMapper.RuntimeImageForDeployment(image, organizationSlug);
    }

    private string PushImageForService(MunicloudConfig config, string serviceName, MunicloudServiceConfig service, string imageTag) =>
        string.IsNullOrWhiteSpace(service.Image)
            ? $"{_environment.RegistryHost}/{config.App}/{serviceName}:{RegistryImageMapper.NormalizeImageSegment(imageTag)}"
            : service.Image;

    private static string BuildCacheImageFor(string pushImage)
    {
        var tagIndex = pushImage.LastIndexOf(':');
        var slashIndex = pushImage.LastIndexOf('/');
        if (tagIndex > slashIndex)
        {
            return pushImage[..tagIndex] + ":buildcache";
        }

        return pushImage + ":buildcache";
    }

    internal static string ComputeServiceFingerprint(MunicloudServiceConfig service)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, "platform", DeploymentImagePlatform);
        AppendHash(hash, "sourcePath", Path.GetFullPath(service.SourcePath!));
        AppendHash(hash, "dockerfile", Path.GetFullPath(EffectiveDockerfilePath(service)));

        foreach (var filePath in EnumerateFingerprintFiles(service))
        {
            var relativePath = Path.GetRelativePath(service.SourcePath!, filePath).Replace(Path.DirectorySeparatorChar, '/');
            AppendHash(hash, "file", relativePath);
            var info = new FileInfo(filePath);
            AppendHash(hash, "length", info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var stream = File.OpenRead(filePath);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateFingerprintFiles(MunicloudServiceConfig service)
    {
        var sourceRoot = Path.GetFullPath(service.SourcePath!);
        var dockerfilePath = Path.GetFullPath(EffectiveDockerfilePath(service));
        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(file => !IsIgnoredFingerprintPath(sourceRoot, file))
            .Append(dockerfilePath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        return files;
    }

    private static bool IsIgnoredFingerprintPath(string sourceRoot, string filePath)
    {
        var relativeParts = Path.GetRelativePath(sourceRoot, filePath)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return relativeParts.Any(part => part is ".git" or ".svn" or ".hg" or "node_modules" or "bin" or "obj" or ".next" or ".nuxt" or "dist" or "build" or "coverage");
    }

    private static void AppendHash(IncrementalHash hash, string label, string value)
    {
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(label));
        hash.AppendData([0]);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private async Task RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        string? standardInput = null,
        bool verbose = false,
        string? progressLabel = null,
        PublishProgressView? progressView = null)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardInput = standardInput is not null;
        process.StartInfo.UseShellExecute = false;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable, $"Unable to start '{fileName}'. Make sure Docker is installed and running.");
        }

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        var outputTail = new Queue<string>();
        var outputLock = new object();
        var progress = verbose || string.IsNullOrWhiteSpace(progressLabel) ? null : new DockerProgressReporter(_console, progressLabel, progressView);
        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (verbose)
                {
                    _console.WriteLine(line);
                }
                else
                {
                    RememberOutputLine(outputTail, outputLock, line);
                    progress?.Observe(line);
                }
            }
        }, cancellationToken);
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                if (verbose)
                {
                    _console.WriteError(line);
                }
                else
                {
                    RememberOutputLine(outputTail, outputLock, line);
                    progress?.Observe(line);
                }
            }
        }, cancellationToken);

        await WaitForProcessWithProgressAsync(process, progressLabel, verbose, progress is not null, progressView is not null, cancellationToken);
        await Task.WhenAll(stdout, stderr);

        if (process.ExitCode != 0)
        {
            if (!verbose)
            {
                if (progressView is not null && !string.IsNullOrWhiteSpace(progressLabel))
                {
                    progressView.MarkFailed(progressLabel);
                }

                progressView?.Complete();
                var tail = SnapshotOutputTail(outputTail, outputLock);
                if (tail.Count > 0)
                {
                    _console.WriteError("Command output:");
                    foreach (var line in tail)
                    {
                        _console.WriteError($"  {line}");
                    }
                }
            }

            throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable, $"'{fileName} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}.");
        }

        progress?.Complete();
    }

    private async Task WaitForProcessWithProgressAsync(Process process, string? progressLabel, bool verbose, bool outputDrivenProgress, bool liveProgress, CancellationToken cancellationToken)
    {
        var waitTask = process.WaitForExitAsync(cancellationToken);
        if (verbose || string.IsNullOrWhiteSpace(progressLabel))
        {
            await waitTask;
            return;
        }

        if (outputDrivenProgress)
        {
            if (!liveProgress)
            {
                _console.WriteLine($"{progressLabel}: starting");
            }

            await waitTask;
            return;
        }

        _console.Write(progressLabel);
        while (await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)) != waitTask)
        {
            _console.Write(".");
        }

        await waitTask;
        _console.WriteLine(process.ExitCode == 0 ? " done" : " failed");
    }

    private static void RememberOutputLine(Queue<string> outputTail, object outputLock, string line)
    {
        lock (outputLock)
        {
            outputTail.Enqueue(line);
            while (outputTail.Count > 40)
            {
                outputTail.Dequeue();
            }
        }
    }

    private static IReadOnlyList<string> SnapshotOutputTail(Queue<string> outputTail, object outputLock)
    {
        lock (outputLock)
        {
            return outputTail.ToArray();
        }
    }

    private static string ToTitle(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            Process.Start("xdg-open", url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string MaskToken(string token)
    {
        var visibleLength = Math.Min("mc_live_123456".Length, token.Length);
        return token[..visibleLength] + "...";
    }

    private sealed class PublishProgressView : IDisposable
    {
        private readonly IConsole _console;
        private readonly object _lock = new();
        private readonly Dictionary<string, PublishProgressService> _services;
        private readonly bool _live;
        private int _renderedLines;
        private bool _cursorHidden;
        private bool _completed;
        private string? _lastRender;

        public PublishProgressView(IConsole console, IReadOnlyList<ServicePublishJob> jobs)
        {
            _console = console;
            _live = console.SupportsAnsi;
            _services = jobs.ToDictionary(
                job => job.ServiceName,
                job => new PublishProgressService(job.ServiceName, job.PushImage, job.CacheImage),
                StringComparer.Ordinal);

            Render();
        }

        public void MarkPublishing(string serviceName) =>
            Update(serviceName, service =>
            {
                service.Status = "publishing";
            });

        public void UpdateDockerSteps(string serviceName, int current, int total) =>
            Update(serviceName, service =>
            {
                service.Status = "building";
                service.DockerCurrent = Math.Max(service.DockerCurrent, current);
                service.DockerTotal = Math.Max(service.DockerTotal, total);
            });

        public void UpdatePushLayers(string serviceName, int done, int total) =>
            Update(serviceName, service =>
            {
                service.Status = done >= total && total > 0 ? "pushed" : "pushing";
                service.PushDone = Math.Max(service.PushDone, done);
                service.PushTotal = Math.Max(service.PushTotal, total);
            });

        public void MarkSkipped(string serviceName) =>
            Update(serviceName, service =>
            {
                service.Status = "skipped";
                service.IsTerminal = true;
            });

        public void MarkDone(string serviceName) =>
            Update(serviceName, service =>
            {
                service.Status = "done";
                service.IsTerminal = true;
            });

        public void MarkFailed(string serviceName) =>
            Update(serviceName, service =>
            {
                service.Status = "failed";
                service.IsTerminal = true;
            });

        public void Complete()
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                RenderLocked(force: true);
                RestoreCursorLocked();
            }
        }

        public void Dispose() => Complete();

        private void Update(string serviceName, Action<PublishProgressService> apply)
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                if (!_services.TryGetValue(serviceName, out var service))
                {
                    return;
                }

                apply(service);
                RenderLocked(force: false);
            }
        }

        private void Render()
        {
            lock (_lock)
            {
                RenderLocked(force: false);
            }
        }

        private void RenderLocked(bool force)
        {
            var lines = BuildLines();
            var render = string.Join(Environment.NewLine, lines);
            if (!force && render == _lastRender)
            {
                return;
            }

            _lastRender = render;
            if (!_live)
            {
                foreach (var service in _services.Values)
                {
                    var summary = ServiceSummary(service);
                    if (summary == service.LastLineSummary)
                    {
                        continue;
                    }

                    service.LastLineSummary = summary;
                    _console.WriteLine(summary);
                }

                return;
            }

            if (!_cursorHidden)
            {
                _console.Write("\x1b[?25l");
                _cursorHidden = true;
            }

            if (_renderedLines > 0)
            {
                _console.Write($"\x1b[{_renderedLines}F\x1b[J");
            }

            _console.WriteLine(render);
            _renderedLines = lines.Count;
        }

        private IReadOnlyList<string> BuildLines()
        {
            var complete = _services.Values.Count(service => service.IsTerminal);
            var lines = new List<string>
            {
                "Municloud publish",
                $"Services: {complete}/{_services.Count} complete"
            };

            foreach (var service in _services.Values.OrderBy(service => service.Name, StringComparer.Ordinal))
            {
                lines.Add(ServiceSummary(service));
                lines.Add($"  image: {service.Image} ({DeploymentImagePlatform})");
                lines.Add($"  cache: {service.CacheImage}");
            }

            return lines;
        }

        private static string ServiceSummary(PublishProgressService service)
        {
            var docker = service.DockerTotal > 0 ? $"{service.DockerCurrent}/{service.DockerTotal}" : "-";
            var push = service.PushTotal > 0 ? $"{service.PushDone}/{service.PushTotal}" : "-";
            return $"- {service.Name}: {service.Status} | docker {docker} | push {push}";
        }

        private void RestoreCursorLocked()
        {
            if (!_live || !_cursorHidden)
            {
                return;
            }

            _console.Write("\x1b[?25h");
            _cursorHidden = false;
        }

        private sealed class PublishProgressService
        {
            public PublishProgressService(string name, string image, string cacheImage)
            {
                Name = name;
                Image = image;
                CacheImage = cacheImage;
            }

            public string Name { get; }
            public string Image { get; }
            public string CacheImage { get; }
            public string Status { get; set; } = "waiting";
            public int DockerCurrent { get; set; }
            public int DockerTotal { get; set; }
            public int PushDone { get; set; }
            public int PushTotal { get; set; }
            public bool IsTerminal { get; set; }
            public string? LastLineSummary { get; set; }
        }
    }

    private sealed class DockerProgressReporter
    {
        private readonly IConsole _console;
        private readonly string _label;
        private readonly PublishProgressView? _progressView;
        private readonly object _lock = new();
        private readonly HashSet<int> _seenDockerSteps = [];
        private readonly HashSet<int> _completedDockerSteps = [];
        private readonly Dictionary<string, string> _pushLayers = new(StringComparer.Ordinal);
        private string? _lastMessage;

        public DockerProgressReporter(IConsole console, string label, PublishProgressView? progressView = null)
        {
            _console = console;
            _label = label;
            _progressView = progressView;
        }

        public void Observe(string line)
        {
            lock (_lock)
            {
                if (TryObserveBuildKitStep(line))
                {
                    return;
                }

                TryObservePushLayer(line);
            }
        }

        public void Complete()
        {
            lock (_lock)
            {
                WriteIfChanged($"{_label}: done");
            }
        }

        private bool TryObserveBuildKitStep(string line)
        {
            var match = BuildKitStepRegex().Match(line);
            if (!match.Success)
            {
                return false;
            }

            var step = int.Parse(match.Groups["step"].Value, System.Globalization.CultureInfo.InvariantCulture);
            _seenDockerSteps.Add(step);
            if (line.Contains("DONE", StringComparison.OrdinalIgnoreCase) || line.Contains("CACHED", StringComparison.OrdinalIgnoreCase))
            {
                _completedDockerSteps.Add(step);
            }

            var zeroBased = _seenDockerSteps.Contains(0);
            var total = _seenDockerSteps.Count == 0 ? 1 : _seenDockerSteps.Max() + (zeroBased ? 1 : 0);
            var currentStep = step + (zeroBased ? 1 : 0);
            var current = Math.Max(currentStep, _completedDockerSteps.Count);
            _progressView?.UpdateDockerSteps(_label, current, total);
            WriteIfChanged($"{_label}: docker steps {current} of {total}");
            return true;
        }

        private void TryObservePushLayer(string line)
        {
            var match = PushLayerRegex().Match(line);
            if (!match.Success)
            {
                return;
            }

            _pushLayers[match.Groups["layer"].Value] = match.Groups["state"].Value;
            var total = _pushLayers.Count;
            var done = _pushLayers.Values.Count(state =>
                state.Equals("Pushed", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("Layer already exists", StringComparison.OrdinalIgnoreCase));
            _progressView?.UpdatePushLayers(_label, done, total);
            WriteIfChanged($"{_label}: push layers {done} of {total}");
        }

        private void WriteIfChanged(string message)
        {
            if (_progressView is not null)
            {
                return;
            }

            if (message == _lastMessage)
            {
                return;
            }

            _lastMessage = message;
            _console.WriteLine(message);
        }
    }

    [GeneratedRegex("^#(?<step>\\d+)\\s+")]
    private static partial Regex BuildKitStepRegex();

    [GeneratedRegex("^(?<layer>[a-f0-9]{12,64}):\\s+(?<state>Waiting|Preparing|Pushing|Pushed|Layer already exists)$", RegexOptions.IgnoreCase)]
    private static partial Regex PushLayerRegex();

    private sealed record ServicePublishJob(
        string ServiceName,
        MunicloudServiceConfig Service,
        string PushImage,
        string CacheImage,
        string Fingerprint);

    private sealed class PublishCache
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        public Dictionary<string, PublishCacheEntry> Entries { get; init; } = new(StringComparer.Ordinal);

        public static PublishCache Load(CliEnvironment environment)
        {
            var path = CachePath(environment);
            if (!File.Exists(path))
            {
                return new PublishCache();
            }

            try
            {
                var cache = JsonSerializer.Deserialize<PublishCache>(File.ReadAllText(path), JsonOptions);
                return cache ?? new PublishCache();
            }
            catch (JsonException)
            {
                return new PublishCache();
            }
        }

        public bool IsCurrent(string app, string serviceName, string pushImage, string platform, string fingerprint)
        {
            var key = Key(app, serviceName, pushImage, platform);
            return Entries.TryGetValue(key, out var entry) && entry.Fingerprint == fingerprint;
        }

        public void MarkCurrent(string app, string serviceName, string pushImage, string platform, string fingerprint)
        {
            Entries[Key(app, serviceName, pushImage, platform)] = new PublishCacheEntry(fingerprint, DateTimeOffset.UtcNow);
        }

        public void Save(CliEnvironment environment)
        {
            Directory.CreateDirectory(environment.ConfigHome);
            File.WriteAllText(CachePath(environment), JsonSerializer.Serialize(this, JsonOptions));
        }

        private static string CachePath(CliEnvironment environment) =>
            Path.Combine(environment.ConfigHome, "publish-cache.json");

        private static string Key(string app, string serviceName, string pushImage, string platform) =>
            $"{app}|{serviceName}|{pushImage}|{platform}";
    }

    private sealed record PublishCacheEntry(string Fingerprint, DateTimeOffset PublishedAt);

    private sealed class CliCommandException : Exception
    {
        public CliCommandException(int exitCode, string message)
            : base(message)
        {
            ExitCode = exitCode;
        }

        public int ExitCode { get; }
    }
}
