using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private static readonly ISet<string> TerminalStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "succeeded",
        "failed",
        "canceled"
    };

    private readonly IConsole _console;
    private readonly CliEnvironment _environment;
    private readonly TokenStore _tokenStore;
    private readonly MinicloudApiClient _apiClient;
    private readonly RegistryImageMapper _registryImageMapper;

    public CliApplication(IConsole console, CliEnvironment environment, TokenStore tokenStore, MinicloudApiClient apiClient)
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
                "init" => await RunInitAsync(args.Skip(1).ToArray(), cancellationToken),
                "add-service" => await RunAddServiceAsync(args.Skip(1).ToArray(), cancellationToken),
                "login" => await RunLoginAsync(args.Skip(1).ToArray(), cancellationToken),
                "deploy" => await RunDeployAsync(args.Skip(1).ToArray(), cancellationToken),
                "branch" => await RunBranchAsync(args.Skip(1).ToArray(), cancellationToken),
                "status" => await RunStatusAsync(args.Skip(1).ToArray(), cancellationToken),
                "logs" => await RunLogsAsync(args.Skip(1).ToArray(), cancellationToken),
                "apps" => await RunAppsAsync(args.Skip(1).ToArray(), cancellationToken),
                "domains" => await RunDomainsAsync(args.Skip(1).ToArray(), cancellationToken),
                "secrets" => await RunSecretsAsync(args.Skip(1).ToArray(), cancellationToken),
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
            _console.WriteError("Usage: minicloud token set <token>");
            return CliExitCodes.ValidationError;
        }

        var token = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(token))
        {
            _console.WriteError("Usage: minicloud token set <token>");
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
            _console.WriteLine("Opening browser for Minicloud login...");
        }
        else
        {
            _console.WriteLine("Open this URL to finish Minicloud login:");
            _console.WriteLine(session.LoginUrl);
        }

        var exchange = await PollCliLoginExchangeAsync(session.SessionId, session.ExpiresAt, cancellationToken);
        _tokenStore.SaveToken(exchange.Token);
        _console.WriteLine($"Logged in as {exchange.Email}");
        _console.WriteLine($"Organization: {exchange.Organization.Name}");
        _console.WriteLine($"Token: {MaskToken(exchange.Token)}");
        return CliExitCodes.Success;
    }

    private async Task<int> RunInitAsync(string[] args, CancellationToken cancellationToken)
    {
        _console.WriteLine("Minicloud init");
        _console.WriteLine();

        var outputPath = GetOption(args, "--config") ?? SuggestedInitConfigPath();
        if (File.Exists(outputPath) && !args.Contains("--force", StringComparer.Ordinal))
        {
            var existingResult = MinicloudConfigLoader.Load(outputPath);
            if (!existingResult.IsValid)
            {
                _console.WriteError($"Config file '{outputPath}' already exists but is not valid.");
                PrintDiagnostics(existingResult.Diagnostics);
                if (!Confirm("Replace it now?", defaultValue: false))
                {
                    _console.WriteLine("Init canceled.");
                    return CliExitCodes.Success;
                }

                return await RunServiceConfigWizardAsync(
                    args,
                    allowCreateApp: true,
                    createdVerb: "Updated",
                    skipExistingFileConfirm: true,
                    cancellationToken);
            }
            else
            {
                var existingConfig = existingResult.Config!;
                try
                {
                    var app = await _apiClient.GetAppAsync(existingConfig.AppId!, cancellationToken);
                    if (!string.Equals(app.Slug, existingConfig.App, StringComparison.OrdinalIgnoreCase))
                    {
                        _console.WriteError($"Config file '{outputPath}' points to appId '{existingConfig.AppId}' ({app.Slug}), but app is '{existingConfig.App}'.");
                        if (!Confirm($"Update '{outputPath}' now?", defaultValue: false))
                        {
                            _console.WriteLine("Init canceled.");
                            return CliExitCodes.Success;
                        }

                        return await RunServiceConfigWizardAsync(
                            args,
                            allowCreateApp: true,
                            createdVerb: "Updated",
                            skipExistingFileConfirm: true,
                            cancellationToken);
                    }
                    else
                    {
                        _console.WriteLine($"Using existing {outputPath}");
                        _console.WriteLine($"App: {app.Name} ({app.Slug}, {app.Id})");
                        _console.WriteLine($"Services: {string.Join(", ", existingConfig.Services.Keys)}");
                        _console.WriteLine($"Next: minicloud deploy --config {outputPath}");
                        return CliExitCodes.Success;
                    }
                }
                catch (ApiException ex) when (ex.StatusCode is 403 or 404)
                {
                    _console.WriteError($"Config file '{outputPath}' points to appId '{existingConfig.AppId}', but this token cannot access that app.");
                    if (!Confirm($"Update '{outputPath}' now?", defaultValue: false))
                    {
                        _console.WriteLine("Init canceled.");
                        return CliExitCodes.Success;
                    }

                    return await RunServiceConfigWizardAsync(
                        args,
                        allowCreateApp: true,
                        createdVerb: "Updated",
                        skipExistingFileConfirm: true,
                        cancellationToken);
                }
            }
        }

        return await RunServiceConfigWizardAsync(
            args,
            allowCreateApp: true,
            createdVerb: "Created",
            skipExistingFileConfirm: false,
            cancellationToken);
    }

    private async Task<int> RunAddServiceAsync(string[] args, CancellationToken cancellationToken)
    {
        _console.WriteLine("Minicloud add-service");
        _console.WriteLine();

        return await RunServiceConfigWizardAsync(
            args,
            allowCreateApp: false,
            createdVerb: "Created service config",
            skipExistingFileConfirm: false,
            cancellationToken);
    }

    private async Task<int> RunServiceConfigWizardAsync(
        string[] args,
        bool allowCreateApp,
        string createdVerb,
        bool skipExistingFileConfirm,
        CancellationToken cancellationToken)
    {
        var configuredOutputPath = GetOption(args, "--config");
        var advanced = args.Contains("--advanced", StringComparer.Ordinal);
        var requestedApp = GetOption(args, "--app") ?? FirstPositionalArg(args);

        var me = await _apiClient.GetMeAsync(cancellationToken);
        var organization = me.Organizations.FirstOrDefault();
        if (organization is null)
        {
            _console.WriteError("Auth error: your token is not associated with an organization.");
            return CliExitCodes.AuthError;
        }

        var app = allowCreateApp
            ? await PromptAppSelectionAsync(organization, requestedApp, cancellationToken)
            : await PromptExistingAppSelectionAsync(organization, requestedApp, cancellationToken);
        var database = app.Database;

        var serviceDrafts = PromptServiceDefinitions(app.Slug, advanced);
        if (serviceDrafts.Count == 0)
        {
            _console.WriteError("Usage error: select at least one service.");
            return CliExitCodes.ValidationError;
        }

        var services = serviceDrafts.ToDictionary(x => x.Name, x => x.Config, StringComparer.Ordinal);

        var config = new MinicloudConfig(app.Slug, database, null, services)
        {
            AppId = app.Id
        };
        var diagnostics = MinicloudConfigValidator.Validate(config);
        if (diagnostics.Count > 0)
        {
            PrintDiagnostics(diagnostics);
            return CliExitCodes.ValidationError;
        }

        var outputPath = configuredOutputPath ?? SuggestedInitConfigPath();
        if (File.Exists(outputPath) && !skipExistingFileConfirm && !args.Contains("--force", StringComparer.Ordinal))
        {
            if (!Confirm($"'{outputPath}' already exists. Overwrite it?", defaultValue: false))
            {
                _console.WriteLine("Init canceled.");
                return CliExitCodes.Success;
            }
        }

        File.WriteAllText(outputPath, MinicloudConfigWriter.Write(config));
        _console.WriteLine();
        _console.WriteLine($"{createdVerb} {outputPath}");
        _console.WriteLine($"App: {app.Name} ({app.Slug}, {app.Id})");
        _console.WriteLine($"Services: {string.Join(", ", serviceDrafts.Select(x => x.Name))}");
        if (!advanced)
        {
            _console.WriteLine("Used Dockerfile ports where available and defaults for remaining service options.");
            _console.WriteLine("Run 'minicloud init --advanced' to customize every option.");
        }
        _console.WriteLine($"Next: minicloud deploy --config {outputPath}");
        return CliExitCodes.Success;
    }

    private async Task<int> RunDeployAsync(string[] args, CancellationToken cancellationToken)
    {
        var branchDeploy = args.FirstOrDefault() == "branch";
        if (branchDeploy)
        {
            args = args.Skip(1).ToArray();
        }

        var configPath = GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath();
        var databaseOverride = GetOption(args, "--database");
        var postgresPassword = GetOption(args, "--pgpassword");
        var noPublish = args.Contains("--no-publish", StringComparer.Ordinal);
        var deployAll = branchDeploy || args.Contains("--all", StringComparer.Ordinal);
        var requestedServiceNames = DeployServiceNamesFromArgs(args);
        if (args.Contains("--publish-only", StringComparer.Ordinal))
        {
            _console.WriteError("Usage error: --publish-only has been removed.");
            return CliExitCodes.ValidationError;
        }

        if (deployAll && requestedServiceNames.Count > 0)
        {
            _console.WriteError("Usage error: --all cannot be combined with service names.");
            return CliExitCodes.ValidationError;
        }

        var configResult = MinicloudConfigLoader.Load(configPath);
        if (!configResult.IsValid || configResult.Config is null)
        {
            PrintDiagnostics(configResult.Diagnostics);
            return CliExitCodes.ValidationError;
        }

        var config = configResult.Config;
        if (branchDeploy && requestedServiceNames.Count > 0)
        {
            _console.WriteError("Usage error: minicloud deploy branch deploys all configured services and does not accept service names.");
            return CliExitCodes.ValidationError;
        }
        var selectedServiceNames = ResolveDeployServiceNames(config, requestedServiceNames, deployAll);
        if (selectedServiceNames.Count == 0)
        {
            _console.WriteError("Usage error: select at least one service to deploy.");
            return CliExitCodes.ValidationError;
        }

        config = FilterConfigServices(config, selectedServiceNames);

        var me = await _apiClient.GetMeAsync(cancellationToken);
        var app = await _apiClient.GetAppAsync(config.AppId!, cancellationToken);
        var organization = me.Organizations.FirstOrDefault(x => x.Id == app.OrganizationId);
        if (organization is null)
        {
            _console.WriteError($"Auth error: your token is not associated with app '{config.AppId}'.");
            return CliExitCodes.AuthError;
        }

        if (branchDeploy)
        {
            var gitBranch = CurrentGitBranch(Environment.CurrentDirectory);
            _console.WriteLine($"Branch: {gitBranch}");
            app = await _apiClient.EnsureBranchAsync(app.Id, new EnsureAppBranchRequest(gitBranch), cancellationToken);
            config = config with { AppId = app.Id };
            _console.WriteLine($"Branch app: {app.Name} ({app.Slug})");
        }

        await SyncLocalSecretsAsync(app, config, cancellationToken);

        IReadOnlyDictionary<string, string> serviceArtifactIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!noPublish)
        {
            var deploymentModeDiagnostics = ValidateDeploymentSources(config);
            if (deploymentModeDiagnostics.Count > 0)
            {
                PrintDiagnostics(deploymentModeDiagnostics);
                return CliExitCodes.ValidationError;
            }

            serviceArtifactIds = await BundleAndUploadDeploymentArtifactsAsync(config, organization.Slug, app.Slug, cancellationToken);
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
            ResolveDeploymentDatabase(databaseOverride, config),
            config.CommitSha,
            config.Services.Select(x => new DeploymentServiceRequest(
                x.Key,
                noPublish || !serviceArtifactIds.ContainsKey(x.Key)
                    ? DeploymentImageForService(x.Value, organization.Slug)
                    : null,
                x.Value.Port!.Value,
                x.Value.Public!.Value,
                x.Value.Path!,
                x.Value.HealthPath!,
                x.Value.Env,
                x.Value.SecretEnv,
                serviceArtifactIds.TryGetValue(x.Key, out var artifactId) ? artifactId : null)).ToArray(),
            postgresPassword);

        var created = await _apiClient.CreateDeploymentAsync(request, cancellationToken);
        _console.WriteLine($"Minicloud deployment {created.Id}");
        _console.WriteLine($"Status: {created.Status}");
        if (!string.IsNullOrWhiteSpace(created.PostgresPassword))
        {
            _console.WriteLine($"Postgres password: {created.PostgresPassword}");
        }

        var finalDeployment = await PollDeploymentAsync(created.Id, created.Status, cancellationToken);
        if (finalDeployment.Status == "succeeded")
        {
            PrintServiceUrls(finalDeployment.Services);

            return CliExitCodes.Success;
        }

        _console.WriteLine($"Failure: {finalDeployment.FailureCode ?? finalDeployment.Status}");
        if (!string.IsNullOrWhiteSpace(finalDeployment.FailureMessage))
        {
            _console.WriteLine($"Message: {finalDeployment.FailureMessage}");
        }

        _console.WriteLine($"Logs: minicloud logs {finalDeployment.Id}");
        return CliExitCodes.DeploymentFailed;
    }

    private async Task<int> RunBranchAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.FirstOrDefault() != "destroy")
        {
            _console.WriteError("Usage: minicloud branch destroy [branch] [--config minicloud.yml]");
            return CliExitCodes.ValidationError;
        }

        var configPath = GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath();
        var configResult = MinicloudConfigLoader.Load(configPath);
        if (!configResult.IsValid || string.IsNullOrWhiteSpace(configResult.Config?.AppId))
        {
            PrintDiagnostics(configResult.Diagnostics);
            return CliExitCodes.ValidationError;
        }

        var mainApp = await _apiClient.GetAppAsync(configResult.Config.AppId, cancellationToken);
        if (mainApp.ParentAppId is not null)
        {
            _console.WriteError("Config error: branch destroy must use the main app's minicloud.yml.");
            return CliExitCodes.ValidationError;
        }

        if (mainApp.Branches.Count == 0)
        {
            _console.WriteError($"App '{mainApp.Name}' has no branch deployments.");
            return CliExitCodes.ValidationError;
        }

        var requestedBranch = FirstPositionalArg(args.Skip(1).ToArray());
        AppBranchResponse branch;
        if (!string.IsNullOrWhiteSpace(requestedBranch))
        {
            branch = mainApp.Branches.FirstOrDefault(x =>
                    string.Equals(x.BranchName, requestedBranch, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Id, requestedBranch, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Branch deployment '{requestedBranch}' was not found.");
        }
        else if (mainApp.Branches.Count == 1)
        {
            branch = mainApp.Branches[0];
        }
        else
        {
            var selected = PromptSingleSelect(
                "Branch deployment",
                mainApp.Branches.OrderBy(x => x.BranchName, StringComparer.Ordinal)
                    .Select(x => (x.Id, $"{x.BranchName} ({FormatBranchUrls(x)})"))
                    .ToArray(),
                mainApp.Branches.OrderBy(x => x.BranchName, StringComparer.Ordinal).First().Id);
            branch = mainApp.Branches.Single(x => x.Id == selected);
        }

        if (!Confirm($"Destroy branch '{branch.BranchName}' and its Vultr VPS?", defaultValue: false))
        {
            _console.WriteLine("Branch destroy canceled.");
            return CliExitCodes.Success;
        }

        await _apiClient.DestroyBranchAsync(mainApp.Id, branch.Id, cancellationToken);
        _console.WriteLine($"Branch destroy queued: {branch.BranchName}");
        return CliExitCodes.Success;
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

    private async Task<int> RunAppsAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _console.WriteError("Usage: minicloud apps list|inspect <app>");
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
            _console.WriteLine($"{app.Slug}\t{app.Database}");
        }

        return CliExitCodes.Success;
    }

    private async Task<int> RunAppsInspectAsync(string[] args, CancellationToken cancellationToken)
    {
        var appIdOrSlug = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(appIdOrSlug))
        {
            _console.WriteError("Usage: minicloud apps inspect <app>");
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
        _console.WriteLine($"Database: {app.Database}");
        if (app.LatestDeployment is not null)
        {
            _console.WriteLine($"Latest deployment: {app.LatestDeployment.Id} ({app.LatestDeployment.Status})");
        }
        if (app.Branches.Count > 0)
        {
            _console.WriteLine("Branches:");
            foreach (var branch in app.Branches.OrderBy(x => x.BranchName, StringComparer.Ordinal))
            {
                _console.WriteLine($"  {branch.BranchName}\t{branch.Plan}\t{FormatBranchUrls(branch)}");
            }
        }

        var services = await _apiClient.GetAppServicesAsync(app.Id, cancellationToken);
        if (services.Count > 0)
        {
            _console.WriteLine("Services:");
            foreach (var service in services.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var visibility = service.Public ? "public" : "private";
                var runtime = service.Runtime is null
                    ? "runtime=unknown"
                    : $"runtime={service.Runtime.State}{(string.IsNullOrWhiteSpace(service.Runtime.Health) ? string.Empty : $"/{service.Runtime.Health}")}";
                var domains = service.Domains.Count == 0
                    ? "domains=-"
                    : $"domains={string.Join(",", service.Domains.OrderBy(x => x.Hostname, StringComparer.Ordinal).Select(FormatDomainSummary))}";
                _console.WriteLine($"  {service.Name}  {visibility}  port={service.Port}  {runtime}  {domains}");
                foreach (var domain in service.Domains.Where(x => x.Status != "disabled").OrderBy(x => x.Hostname, StringComparer.Ordinal))
                {
                    WriteUrlLine($"Service URL ({service.Name})", $"https://{domain.Hostname}");
                }
            }
        }

        return CliExitCodes.Success;
    }

    private async Task<int> RunDomainsAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _console.WriteError("Usage: minicloud domains <list|add-subdomain|disable|delete>");
            return CliExitCodes.ValidationError;
        }

        return args[0] switch
        {
            "list" => await RunDomainsListAsync(args.Skip(1).ToArray(), cancellationToken),
            "add-subdomain" => await RunDomainsAddSubdomainAsync(args.Skip(1).ToArray(), cancellationToken),
            "disable" => await RunDomainsDisableAsync(args.Skip(1).ToArray(), cancellationToken),
            "delete" => await RunDomainsDeleteAsync(args.Skip(1).ToArray(), cancellationToken),
            _ => UnknownCommand($"domains {args[0]}")
        };
    }

    private async Task<int> RunDomainsListAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var domains = await _apiClient.GetDomainsAsync(app.Id, cancellationToken);
        if (domains.Count == 0)
        {
            _console.WriteLine($"No domains for {app.Name}.");
            return CliExitCodes.Success;
        }

        foreach (var domain in domains.OrderBy(x => x.ServiceName, StringComparer.Ordinal).ThenBy(x => x.Hostname, StringComparer.Ordinal))
        {
            var lastApplied = domain.LastAppliedAt is null ? "-" : domain.LastAppliedAt.Value.ToString("O");
            _console.WriteLine($"{domain.Hostname}  service={domain.ServiceName}  status={domain.Status}  apply={domain.ApplyStatus}  ssl={domain.SslStatus}  lastApplied={lastApplied}  updated={domain.UpdatedAt:O}");
        }

        return CliExitCodes.Success;
    }

    private async Task<int> RunDomainsAddSubdomainAsync(string[] args, CancellationToken cancellationToken)
    {
        var service = GetOption(args, "--service");
        if (string.IsNullOrWhiteSpace(service))
        {
            _console.WriteError("Usage: minicloud domains add-subdomain --app <app> --service <service> [--label <label>]");
            return CliExitCodes.ValidationError;
        }

        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var label = GetOption(args, "--label");
        var domain = await _apiClient.CreateDomainAsync(app.Id, new CreateDomainBindingRequest(service, label), cancellationToken);
        _console.WriteLine($"Created {domain.Hostname}");
        _console.WriteLine($"Service: {domain.ServiceName}");
        _console.WriteLine($"Status: {domain.Status}");
        return CliExitCodes.Success;
    }

    private async Task<int> RunDomainsDisableAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var domain = await ResolveDomainOptionAsync(app.Id, args, cancellationToken);
        var updated = await _apiClient.UpdateDomainAsync(app.Id, domain.Id, new UpdateDomainBindingRequest(true), cancellationToken);
        _console.WriteLine($"Disabled {updated.Hostname}");
        _console.WriteLine($"Status: {updated.Status}");
        return CliExitCodes.Success;
    }

    private async Task<int> RunDomainsDeleteAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var domain = await ResolveDomainOptionAsync(app.Id, args, cancellationToken);
        await _apiClient.DeleteDomainAsync(app.Id, domain.Id, cancellationToken);
        _console.WriteLine($"Deleted {domain.Hostname}");
        return CliExitCodes.Success;
    }

    private async Task<int> RunSecretsAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _console.WriteError("Usage: minicloud secrets <list|set|remove>");
            return CliExitCodes.ValidationError;
        }

        return args[0] switch
        {
            "list" => await RunSecretsListAsync(args.Skip(1).ToArray(), cancellationToken),
            "set" => await RunSecretsSetAsync(args.Skip(1).ToArray(), cancellationToken),
            "remove" or "delete" => await RunSecretsRemoveAsync(args.Skip(1).ToArray(), cancellationToken),
            _ => UnknownCommand($"secrets {args[0]}")
        };
    }

    private async Task<int> RunSecretsListAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var service = GetOption(args, "--service");
        var secrets = await _apiClient.GetSecretsAsync(app.Id, service, cancellationToken);
        if (secrets.Count == 0)
        {
            _console.WriteLine($"No secrets for {app.Name}.");
            return CliExitCodes.Success;
        }

        foreach (var secret in secrets.OrderBy(x => x.ServiceName, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            var scope = string.IsNullOrWhiteSpace(secret.ServiceName) ? "app" : secret.ServiceName;
            _console.WriteLine($"{scope}  {secret.Name}  status={secret.Status}  updated={secret.UpdatedAt:O}");
        }

        return CliExitCodes.Success;
    }

    private async Task<int> RunSecretsSetAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var service = GetOption(args, "--service");
        var name = FirstPositional(args);
        var value = GetOption(args, "--value");
        if (string.IsNullOrWhiteSpace(name))
        {
            _console.WriteError("Usage: minicloud secrets set [--app <app>] <NAME> [--value value]");
            return CliExitCodes.ValidationError;
        }

        if (value is null)
        {
            value = ReadSecretValue($"Value for {app.Slug}/{name}: ");
        }

        var secret = await _apiClient.SetSecretAsync(app.Id, new SetAppServiceSecretRequest(service, name, value), cancellationToken);
        _console.WriteLine($"Secret saved: {secret.Name}");
        return CliExitCodes.Success;
    }

    private async Task<int> RunSecretsRemoveAsync(string[] args, CancellationToken cancellationToken)
    {
        var app = await ResolveAppOptionAsync(args, cancellationToken);
        var service = GetOption(args, "--service");
        var name = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(name))
        {
            _console.WriteError("Usage: minicloud secrets remove [--app <app>] <NAME>");
            return CliExitCodes.ValidationError;
        }

        var secrets = await _apiClient.GetSecretsAsync(app.Id, service, cancellationToken);
        var matches = secrets
            .Where(x => string.Equals(x.Name, name, StringComparison.Ordinal) || string.Equals(x.Id, name, StringComparison.Ordinal))
            .ToArray();
        var secret = matches.Length switch
        {
            0 => throw new CliCommandException(CliExitCodes.ValidationError, $"Secret '{name}' was not found."),
            1 => matches[0],
            _ => matches.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.ServiceName))
                ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Secret '{name}' exists in multiple service scopes. Pass --service <service> to remove a service-scoped secret.")
        };
        await _apiClient.DeleteSecretAsync(app.Id, secret.Id, cancellationToken);
        _console.WriteLine($"Secret removed: {secret.Name}");
        return CliExitCodes.Success;
    }

    private async Task<AppResponse> PromptAppSelectionAsync(OrganizationSummary organization, string? requestedApp, CancellationToken cancellationToken)
    {
        var apps = await _apiClient.GetAppsAsync(organization.Id, cancellationToken);
        if (!string.IsNullOrWhiteSpace(requestedApp))
        {
            var app = FindApp(apps, requestedApp);
            if (app is not null)
            {
                return app;
            }

            _console.WriteError($"App '{requestedApp}' was not found in organization '{organization.Name}'.");
        }

        var choices = apps
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .Select(app => (Value: app.Id, Label: $"{app.Name} ({app.Slug})"))
            .Append((Value: "__create__", Label: "Create new app"))
            .ToArray();

        var selected = PromptSingleSelect("App", choices, choices[0].Value);
        if (selected != "__create__")
        {
            return apps.Single(app => app.Id == selected);
        }

        var appSlug = PromptAppName();
        var database = PromptSingleSelect("Database", DatabaseChoices(), "sqlite");
        return await CreateAppAsync(organization, appSlug, database, cancellationToken);
    }

    private async Task<AppResponse> PromptExistingAppSelectionAsync(OrganizationSummary organization, string? requestedApp, CancellationToken cancellationToken)
    {
        var apps = await _apiClient.GetAppsAsync(organization.Id, cancellationToken);
        if (!string.IsNullOrWhiteSpace(requestedApp))
        {
            var app = FindApp(apps, requestedApp);
            if (app is not null)
            {
                return app;
            }

            throw new CliCommandException(CliExitCodes.ValidationError, $"App '{requestedApp}' was not found in organization '{organization.Name}'.");
        }

        if (apps.Count == 0)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"No apps found in organization '{organization.Name}'. Run 'minicloud init' to create an app first.");
        }

        var choices = apps
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .Select(app => (Value: app.Id, Label: $"{app.Name} ({app.Slug})"))
            .ToArray();

        var selected = PromptSingleSelect("App", choices, choices[0].Value);
        return apps.Single(app => app.Id == selected);
    }

    private async Task<AppResponse> CreateAppAsync(
        OrganizationSummary organization,
        string appSlug,
        string database,
        CancellationToken cancellationToken)
    {
        var createSlug = appSlug.ToLowerInvariant();
        var request = new CreateAppRequest(
            organization.Id,
            DisplayNameFromSlug(createSlug),
            createSlug,
            "p0",
            database);

        _console.WriteLine($"Creating app '{createSlug}' in organization '{organization.Name}'...");
        return await _apiClient.CreateAppAsync(request, cancellationToken);
    }

    private static AppResponse? FindApp(IEnumerable<AppResponse> apps, string appIdOrSlug) =>
        apps.FirstOrDefault(x =>
            string.Equals(x.Slug, appIdOrSlug, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Id, appIdOrSlug, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<(string Value, string Label)> DatabaseChoices() =>
    [
        ("sqlite", "SQLite - instance inside the VPS. Not backed up"),
        ("postgres", "Postgres - instance inside the VPS. Not backed up"),
        ("none", "None/Manual - no database or manual set up - pick this if you want to use Firebase for example")
    ];

    internal static string ResolveDeploymentDatabase(string? databaseOverride, MinicloudConfig config) =>
        databaseOverride ?? config.Database ?? "none";

    private async Task<AppResponse> ResolveAppOptionAsync(string[] args, CancellationToken cancellationToken)
    {
        var appIdOrSlug = GetOption(args, "--app");
        if (string.IsNullOrWhiteSpace(appIdOrSlug))
        {
            var configPath = GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath();
            var configResult = MinicloudConfigLoader.Load(configPath);
            if (configResult.IsValid && !string.IsNullOrWhiteSpace(configResult.Config?.AppId))
            {
                return await _apiClient.GetAppAsync(configResult.Config.AppId, cancellationToken);
            }

            throw new CliCommandException(CliExitCodes.ValidationError, "Missing --app <app> and no valid appId was found in minicloud.yml.");
        }

        var me = await _apiClient.GetMeAsync(cancellationToken);
        var organization = me.Organizations.FirstOrDefault();
        if (organization is null)
        {
            throw new CliCommandException(CliExitCodes.AuthError, "Auth error: your token is not associated with an organization.");
        }

        var apps = await _apiClient.GetAppsAsync(organization.Id, cancellationToken);
        return FindApp(apps, appIdOrSlug)
            ?? throw new CliCommandException(CliExitCodes.ValidationError, $"App '{appIdOrSlug}' was not found.");
    }

    private async Task<DomainBindingResponse> ResolveDomainOptionAsync(string appId, string[] args, CancellationToken cancellationToken)
    {
        var hostname = GetOption(args, "--hostname");
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "Missing --hostname <host>.");
        }

        var domains = await _apiClient.GetDomainsAsync(appId, cancellationToken);
        return domains.FirstOrDefault(x => string.Equals(x.Hostname, hostname, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Id, hostname, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Domain '{hostname}' was not found.");
    }

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

    private void PrintDiagnostics(IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            _console.WriteError($"{diagnostic.Field}: {diagnostic.Message}");
        }
    }

    private static string FormatDomainSummary(DomainBindingResponse domain)
    {
        var lastApplied = domain.LastAppliedAt is null ? "-" : domain.LastAppliedAt.Value.ToString("O");
        return $"{domain.Hostname}({domain.Status},apply={domain.ApplyStatus},ssl={domain.SslStatus},lastApplied={lastApplied})";
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
        _console.WriteLine("Minicloud CLI");
        _console.WriteLine();
        _console.WriteLine("Usage:");
        _console.WriteLine("  minicloud login --token <token>");
        _console.WriteLine("  minicloud init [--advanced] [--config minicloud.yml] [--force]");
        _console.WriteLine("  minicloud add-service [app] [--app app] [--advanced] [--config minicloud.service.yml] [--force]");
        _console.WriteLine("  minicloud token set <token>");
        _console.WriteLine("  minicloud deploy [service ...] [--all] [--config minicloud.yml] [--database db] [--pgpassword password] [--no-publish]");
        _console.WriteLine("  minicloud deploy branch [--config minicloud.yml] [--database db] [--pgpassword password] [--no-publish]");
        _console.WriteLine("  minicloud branch destroy [branch] [--config minicloud.yml]");
        _console.WriteLine("  minicloud status [deployment-id]");
        _console.WriteLine("  minicloud logs [app|deployment-id] [--service service] [--source source] [--tail count] [--since 30m]");
        _console.WriteLine("  minicloud apps list");
        _console.WriteLine("  minicloud apps inspect <app>");
        _console.WriteLine("  minicloud domains list --app <app>");
        _console.WriteLine("  minicloud domains add-subdomain --app <app> --service <service> [--label label]");
        _console.WriteLine("  minicloud domains disable --app <app> --hostname <host>");
        _console.WriteLine("  minicloud domains delete --app <app> --hostname <host>");
        _console.WriteLine("  minicloud secrets list [--app app]");
        _console.WriteLine("  minicloud secrets set [--app app] <NAME> [--value value]");
        _console.WriteLine("  minicloud secrets remove [--app app] <NAME>");
        _console.WriteLine("  minicloud --env");
    }

    private void PrintEnvironment()
    {
        _console.WriteLine("Minicloud CLI environment");
        _console.WriteLine($"Environment: {CliEnvironment.ApiUrlEnvironmentVariable} defaults to {_environment.ApiBaseUrl}");
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

    private static string? FirstPositional(IReadOnlyList<string> args)
    {
        var optionsWithValues = new HashSet<string>(StringComparer.Ordinal)
        {
            "--app",
            "--service",
            "--value",
            "--config",
            "--hostname",
            "--label"
        };

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (optionsWithValues.Contains(arg))
            {
                i++;
                continue;
            }

            if (optionsWithValues.Any(option => arg.StartsWith(option + "=", StringComparison.Ordinal)))
            {
                continue;
            }

            if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                return arg;
            }
        }

        return null;
    }

    private string ReadSecretValue(string prompt)
    {
        _console.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            var key = _console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                _console.WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }

    internal static string? FirstPositionalArg(IReadOnlyList<string> args)
    {
        var optionsWithValues = new HashSet<string>(StringComparer.Ordinal)
        {
            "--app",
            "--config"
        };

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (optionsWithValues.Contains(arg))
            {
                i++;
                continue;
            }

            if (optionsWithValues.Any(option => arg.StartsWith(option + "=", StringComparison.Ordinal)))
            {
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            return arg;
        }

        return null;
    }

    internal static IReadOnlyList<string> DeployServiceNamesFromArgs(IReadOnlyList<string> args)
    {
        var names = new List<string>();
        var optionsWithValues = new HashSet<string>(StringComparer.Ordinal)
        {
            "--config",
            "--database",
            "--pgpassword",
            "--tag"
        };

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (optionsWithValues.Contains(arg))
            {
                i++;
                continue;
            }

            if (optionsWithValues.Any(option => arg.StartsWith(option + "=", StringComparison.Ordinal)))
            {
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            names.Add(arg);
        }

        return names;
    }

    internal static string CurrentGitBranch(string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList =
            {
                "rev-parse",
                "--abbrev-ref",
                "HEAD"
            }
        });
        if (process is null)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "Git error: could not inspect the current branch.");
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"Git error: {error}");
        }

        if (string.IsNullOrWhiteSpace(output) || output == "HEAD")
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "Git error: branch deployments require a checked-out branch, not detached HEAD.");
        }

        return output;
    }

    private IReadOnlyList<string> ResolveDeployServiceNames(
        MinicloudConfig config,
        IReadOnlyList<string> requestedServiceNames,
        bool deployAll)
    {
        if (deployAll || config.Services.Count == 1)
        {
            return config.Services.Keys.ToArray();
        }

        if (requestedServiceNames.Count > 0)
        {
            var missing = requestedServiceNames
                .Where(name => !config.Services.ContainsKey(name))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new CliCommandException(
                    CliExitCodes.ValidationError,
                    $"Config error: service '{missing[0]}' was not found in the selected config.");
            }

            return requestedServiceNames.Distinct(StringComparer.Ordinal).ToArray();
        }

        return PromptServiceMultiSelect(config.Services.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    internal static MinicloudConfig FilterConfigServices(MinicloudConfig config, IReadOnlyList<string> selectedServiceNames)
    {
        var selected = selectedServiceNames.ToHashSet(StringComparer.Ordinal);
        return new MinicloudConfig(
            config.App,
            config.Database,
            config.CommitSha,
            config.Services.Where(x => selected.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal))
        {
            AppId = config.AppId
        };
    }

    private string PromptSingleSelect(string label, IReadOnlyList<(string Value, string Label)> choices, string defaultValue)
    {
        var selectedIndex = Math.Max(0, choices.ToList().FindIndex(x => x.Value == defaultValue));
        const int StaticLineCount = 2;
        var rendered = false;

        while (true)
        {
            RenderSingleSelect(label, choices, selectedIndex, rendered ? StaticLineCount + choices.Count : 0);
            rendered = true;

            var key = _console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    selectedIndex = selectedIndex == 0 ? choices.Count - 1 : selectedIndex - 1;
                    break;
                case ConsoleKey.DownArrow:
                    selectedIndex = selectedIndex == choices.Count - 1 ? 0 : selectedIndex + 1;
                    break;
                case ConsoleKey.Enter:
                    _console.WriteLine();
                    return choices[selectedIndex].Value;
            }
        }
    }

    private IReadOnlyList<string> PromptServiceMultiSelect(IReadOnlyList<string> services)
    {
        var selectedIndex = 0;
        var selected = new HashSet<string>(services, StringComparer.Ordinal);
        const int StaticLineCount = 2;
        var rendered = false;

        while (true)
        {
            RenderServiceMultiSelect(services, selected, selectedIndex, rendered ? StaticLineCount + services.Count + 1 : 0);
            rendered = true;

            var key = _console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    selectedIndex = selectedIndex == 0 ? services.Count : selectedIndex - 1;
                    break;
                case ConsoleKey.DownArrow:
                    selectedIndex = selectedIndex == services.Count ? 0 : selectedIndex + 1;
                    break;
                case ConsoleKey.Spacebar:
                    if (selectedIndex == 0)
                    {
                        if (selected.Count == services.Count)
                        {
                            selected.Clear();
                        }
                        else
                        {
                            selected = new HashSet<string>(services, StringComparer.Ordinal);
                        }
                    }
                    else
                    {
                        var service = services[selectedIndex - 1];
                        if (!selected.Add(service))
                        {
                            selected.Remove(service);
                        }
                    }
                    break;
                case ConsoleKey.Enter:
                    if (selected.Count == 0)
                    {
                        _console.WriteError("Select at least one service.");
                        break;
                    }

                    _console.WriteLine();
                    return services.Where(selected.Contains).ToArray();
            }
        }
    }

    private IReadOnlyList<ServiceConfigDraft> PromptServiceDefinitions(string appSlug, bool advanced)
    {
        var detected = ServiceDetection.Detect(Directory.GetCurrentDirectory());
        if (detected.Count == 0)
        {
            _console.WriteLine("No services detected. Define a custom service.");
            return [PromptCustomServiceDefinition(appSlug, advanced, new HashSet<string>(StringComparer.Ordinal))];
        }

        var selected = PromptDetectedServiceMultiSelect(detected);
        return selected
            .Select(service => ToServiceConfigDraft(appSlug, service, advanced))
            .ToArray();
    }

    private IReadOnlyList<DetectedService> PromptDetectedServiceMultiSelect(IReadOnlyList<DetectedService> services)
    {
        var selectedIndex = 0;
        var selected = new HashSet<int>();
        const int StaticLineCount = 3;
        var rendered = false;

        while (true)
        {
            RenderDetectedServiceMultiSelect(services, selected, selectedIndex, rendered ? StaticLineCount + services.Count + 1 : 0);
            rendered = true;
            var key = _console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    selectedIndex = selectedIndex == 0 ? services.Count : selectedIndex - 1;
                    break;
                case ConsoleKey.DownArrow:
                    selectedIndex = selectedIndex == services.Count ? 0 : selectedIndex + 1;
                    break;
                case ConsoleKey.Spacebar when selectedIndex < services.Count:
                    if (!selected.Add(selectedIndex))
                    {
                        selected.Remove(selectedIndex);
                    }
                    break;
                case ConsoleKey.Enter when selectedIndex == services.Count:
                    if (rendered)
                    {
                        _console.WriteLine();
                    }

                    var existingNames = selected.Select(index => services[index].Name).ToHashSet(StringComparer.Ordinal);
                    var custom = PromptCustomDetectedService(existingNames);
                    return selected.Select(index => services[index]).Append(custom).ToArray();
                case ConsoleKey.Enter:
                    if (selected.Count == 0)
                    {
                        _console.WriteError("Select at least one service with Space, or press Enter on Custom.");
                        break;
                    }

                    return selected.Order().Select(index => services[index]).ToArray();
            }
        }
    }

    private void RenderDetectedServiceMultiSelect(IReadOnlyList<DetectedService> services, ISet<int> selected, int selectedIndex, int previousLineCount)
    {
        ClearPreviousInteractiveRender(previousLineCount);

        _console.WriteLine("Services:");
        _console.WriteLine("Use Up/Down arrows, Space to select services, and Enter to save. Press Enter on Custom to define one manually.");
        var nameWidth = Math.Min(Math.Max("Name".Length, services.Max(service => service.Name.Length)), 28);
        var frameworkWidth = Math.Min(Math.Max("Framework".Length, services.Max(service => service.Framework.Length)), 16);
        var pathWidth = Math.Min(Math.Max("Path".Length, services.Max(service => service.SourcePath.Length)), 48);
        var dockerfileWidth = Math.Min(Math.Max("Dockerfile".Length, services.Max(service => service.Dockerfile?.Length ?? 1)), 28);
        _console.WriteLine($"  {"".PadRight(3)} {Pad("Name", nameWidth)}  {Pad("Framework", frameworkWidth)}  {Pad("Path", pathWidth)}  {"Port".PadLeft(5)}  {Pad("Dockerfile", dockerfileWidth)}");
        for (var i = 0; i < services.Count; i++)
        {
            var marker = selectedIndex == i ? ">" : " ";
            var checkedValue = selected.Contains(i) ? "x" : " ";
            var service = services[i];
            _console.WriteLine($"{marker} [{checkedValue}] {Pad(service.Name, nameWidth)}  {Pad(service.Framework, frameworkWidth)}  {Pad(service.SourcePath, pathWidth)}  {service.Port,5}  {Pad(service.Dockerfile ?? "-", dockerfileWidth)}");
        }

        var customMarker = selectedIndex == services.Count ? ">" : " ";
        _console.WriteLine($"{customMarker}     Custom");
    }

    private static string Pad(string value, int width)
    {
        if (value.Length <= width)
        {
            return value.PadRight(width);
        }

        return width <= 3 ? value[..width] : value[..(width - 3)] + "...";
    }

    private ServiceConfigDraft ToServiceConfigDraft(string appSlug, DetectedService service, bool advanced)
    {
        var config = service.ToConfig() with
        {
            Public = Confirm($"Expose {service.Name} publicly with a URL?", defaultValue: service.Public)
        };
        WriteMissingDockerfileWarning(service.Name, config);
        if (advanced)
        {
            config = PromptAdvancedServiceOptions(appSlug, service.Name, config);
        }

        if (service.ExposedPorts.Count > 1 && !advanced)
        {
            _console.WriteLine($"{service.Name} exposes TCP ports: {string.Join(", ", service.ExposedPorts)}.");
            config = config with { Port = PromptPort($"{ToTitle(service.Name)} routed port", service.Port) };
        }
        while (service.ExposedPorts.Count > 0 && !service.ExposedPorts.Contains(config.Port ?? 0))
        {
            _console.WriteError($"Choose an exposed TCP port: {string.Join(", ", service.ExposedPorts)}.");
            config = config with { Port = PromptPort($"{ToTitle(service.Name)} routed port", service.Port) };
        }
        return new ServiceConfigDraft(service.Name, config);
    }

    private ServiceConfigDraft PromptCustomServiceDefinition(string appSlug, bool advanced, ISet<string> existingNames)
    {
        var detected = PromptCustomDetectedService(existingNames);
        return ToServiceConfigDraft(appSlug, detected, advanced);
    }

    private DetectedService PromptCustomDetectedService(ISet<string> existingNames)
    {
        _console.WriteLine("Custom service");
        var serviceName = PromptServiceName(existingNames);
        _console.WriteLine();
        _console.WriteLine($"{ToTitle(serviceName)} service");
        var sourcePath = PromptDirectory($"{ToTitle(serviceName)} service folder");
        var dockerfile = PromptDockerfile(sourcePath);
        var defaults = DefaultServiceOptions(serviceName);
        var service = new DetectedService(serviceName, sourcePath, dockerfile, "custom", "custom", defaults.Port, defaults.HealthPath);
        return ServiceDetection.WithDockerfilePorts(service, EffectiveDockerfilePath(service.ToConfig()));
    }

    private MinicloudServiceConfig PromptAdvancedServiceOptions(string appSlug, string serviceName, MinicloudServiceConfig config)
    {
        var defaults = DefaultServiceOptions(serviceName);
        var imageDefault = $"{_environment.RegistryHost}/{appSlug}/{serviceName}:latest";
        var image = PromptOptional($"{ToTitle(serviceName)} push image", imageDefault);
        var port = PromptPort($"{ToTitle(serviceName)} port", config.Port ?? defaults.Port);
        var routePath = PromptPath($"{ToTitle(serviceName)} public path", config.Path ?? defaults.Path);
        var healthPath = PromptPath($"{ToTitle(serviceName)} health path", config.HealthPath ?? defaults.HealthPath);
        return config with
        {
            Image = image,
            Port = port,
            Path = routePath,
            HealthPath = healthPath
        };
    }

    private void RenderServiceMultiSelect(IReadOnlyList<string> services, ISet<string> selected, int selectedIndex, int previousLineCount)
    {
        ClearPreviousInteractiveRender(previousLineCount);

        _console.WriteLine("Services:");
        _console.WriteLine("Use Up/Down, Space to toggle, Enter to deploy.");
        var allMarker = selectedIndex == 0 ? ">" : " ";
        var allChecked = selected.Count == services.Count ? "x" : " ";
        _console.WriteLine($"{allMarker} [{allChecked}] all");
        for (var i = 0; i < services.Count; i++)
        {
            var marker = selectedIndex == i + 1 ? ">" : " ";
            var checkedValue = selected.Contains(services[i]) ? "x" : " ";
            _console.WriteLine($"{marker} [{checkedValue}] {services[i]}");
        }
    }

    private void RenderSingleSelect(string label, IReadOnlyList<(string Value, string Label)> choices, int selectedIndex, int previousLineCount)
    {
        ClearPreviousInteractiveRender(previousLineCount);

        _console.WriteLine($"{label}:");
        _console.WriteLine("Use Up/Down arrows and Enter to select.");
        for (var i = 0; i < choices.Count; i++)
        {
            var marker = i == selectedIndex ? ">" : " ";
            _console.WriteLine($"{marker} {choices[i].Label}");
        }
    }

    private void ClearPreviousInteractiveRender(int previousLineCount)
    {
        if (previousLineCount <= 0)
        {
            return;
        }

        if (_console.SupportsAnsi)
        {
            _console.Write($"\u001b[{previousLineCount}A\r\u001b[J");
            return;
        }

        _console.WriteLine();
    }

    private string PromptSlug(string label, string defaultValue)
    {
        while (true)
        {
            var value = PromptRequired(label, defaultValue).Trim().ToLowerInvariant();
            if (MinicloudConfigLoader.SlugRegex().IsMatch(value))
            {
                return value;
            }

            _console.WriteError($"{label} must use lowercase letters, numbers, dashes, and underscores.");
        }
    }

    private string PromptAppName()
    {
        while (true)
        {
            var value = PromptRequired("App name").Trim();
            if (MinicloudConfigLoader.AppNameRegex().IsMatch(value))
            {
                return value.ToLowerInvariant();
            }

            _console.WriteError("App name must use letters, numbers, dashes, and underscores.");
        }
    }

    private string PromptDirectory(string label)
    {
        while (true)
        {
            var value = PromptRequired(label).Trim();
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

    private string PromptRequired(string label)
    {
        while (true)
        {
            _console.WriteLine($"{label}:");
            var value = _console.ReadLine();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            _console.WriteError($"{label} is required.");
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

    private string PromptServiceName() => PromptSlug("Service name", "backend");

    private string PromptServiceName(ISet<string> existingNames)
    {
        while (true)
        {
            var serviceName = PromptServiceName();
            if (existingNames.Add(serviceName))
            {
                return serviceName;
            }

            _console.WriteError($"Service '{serviceName}' is already selected.");
        }
    }

    private string? PromptDockerfile(string sourcePath)
    {
        _console.WriteLine("Dockerfile path [Dockerfile]:");
        var value = _console.ReadLine();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var dockerfile = value.Trim();
        var fullPath = Path.IsPathRooted(dockerfile)
            ? dockerfile
            : Path.Combine(sourcePath, dockerfile);
        if (!File.Exists(fullPath))
        {
            _console.WriteError($"Warning: Dockerfile '{dockerfile}' does not exist yet.");
        }

        return Path.GetRelativePath(Environment.CurrentDirectory, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private void WriteMissingDockerfileWarning(string serviceName, MinicloudServiceConfig service)
    {
        if (HasDockerfileForService(service))
        {
            return;
        }

        var sourcePath = service.SourcePath ?? ".";
        _console.WriteError($"Warning: {serviceName} does not have a Dockerfile at '{Path.Combine(sourcePath, "Dockerfile")}'.");
        _console.WriteError("Minicloud needs a Dockerfile in the service folder before it can deploy this service.");
        _console.WriteError("Init will continue, but deploy will fail until the Dockerfile exists.");
    }

    internal static bool HasDefaultDockerfile(string sourcePath) =>
        File.Exists(Path.Combine(sourcePath, "Dockerfile"));

    private static bool HasDockerfileForService(MinicloudServiceConfig service)
    {
        return !string.IsNullOrWhiteSpace(service.SourcePath) && File.Exists(EffectiveDockerfilePath(service));
    }

    internal static string SuggestedInitConfigPath() => "minicloud.yml";

    private (int Port, string Path, string HealthPath) DefaultServiceOptions(string serviceName) =>
        serviceName switch
        {
            "frontend" or "dashboard" => (3000, "/", "/"),
            "backend" or "api" => (8080, "/", "/health"),
            "registry" => (8080, "/", "/health"),
            _ => (8080, "/", "/")
        };

    private sealed record ServiceConfigDraft(string Name, MinicloudServiceConfig Config);

    private async Task SyncLocalSecretsAsync(AppResponse app, MinicloudConfig config, CancellationToken cancellationToken)
    {
        foreach (var (serviceName, service) in config.Services)
        {
            if (string.IsNullOrWhiteSpace(service.SourcePath))
            {
                continue;
            }

            var secretsPath = Path.Combine(service.SourcePath, LocalSecretsFile.FileName);
            if (!File.Exists(secretsPath))
            {
                continue;
            }

            var secrets = LocalSecretsFile.Parse(secretsPath);
            if (secrets.Count == 0)
            {
                continue;
            }

            _console.WriteLine($"Syncing local secrets for {serviceName}: {secrets.Count}");
            foreach (var (name, value) in secrets)
            {
                await _apiClient.SetSecretAsync(app.Id, new SetAppServiceSecretRequest(serviceName, name, value), cancellationToken);
                _console.WriteLine($"Secret saved: {serviceName}/{name}");
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> BundleAndUploadDeploymentArtifactsAsync(
        MinicloudConfig config,
        string organizationSlug,
        string appSlug,
        CancellationToken cancellationToken)
    {
        var artifactIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var outputDirectory = Path.Combine(Path.GetTempPath(), "minicloud-artifacts");
        foreach (var (serviceName, service) in config.Services)
        {
            if (string.IsNullOrWhiteSpace(service.SourcePath))
            {
                continue;
            }

            _console.WriteLine($"Bundling artifact: {serviceName}");
            if (!File.Exists(EffectiveDockerfilePath(service)))
            {
                if (DockerfileGenerator.TryWriteDockerfile(service, out var generatedDockerfilePath, out var generationReason))
                {
                    _console.WriteLine($"Generated Dockerfile for {serviceName}: {generatedDockerfilePath}");
                }
                else if (!string.IsNullOrWhiteSpace(generationReason))
                {
                    _console.WriteError($"Unable to generate Dockerfile for {serviceName}: {generationReason}.");
                }
            }

            var dockerfileDiagnostics = ValidateDockerfileForService(serviceName, service);
            if (dockerfileDiagnostics.Count > 0)
            {
                PrintDiagnostics(dockerfileDiagnostics);
                throw new CliCommandException(CliExitCodes.ValidationError, "Dockerfile validation failed.");
            }

            var frameworkDiagnostics = FrameworkDeploymentValidator.ValidatePublicHostCompatibility(
                serviceName,
                service,
                organizationSlug,
                appSlug);
            if (frameworkDiagnostics.Count > 0)
            {
                PrintDiagnostics(frameworkDiagnostics);
                throw new CliCommandException(CliExitCodes.ValidationError, "Framework deployment validation failed.");
            }

            var bundle = DeploymentArtifactBundler.Create(config.AppId!, serviceName, service, config.CommitSha, outputDirectory);
            try
            {
                _console.WriteLine($"Uploading artifact: {serviceName} ({bundle.SizeBytes} bytes, sha256 {bundle.Sha256})");
                var createRequest = new CreateDeploymentArtifactRequest(
                    config.AppId!,
                    serviceName,
                    Path.GetFileName(bundle.ZipPath),
                    "application/zip",
                    bundle.SizeBytes,
                    bundle.Sha256,
                    bundle.Manifest);
                var created = await _apiClient.CreateDeploymentArtifactAsync(createRequest, cancellationToken);
                var uploaded = await _apiClient.UploadDeploymentArtifactContentAsync(created.Id, created.UploadUrl, bundle.ZipPath, bundle.Sha256, bundle.SizeBytes, cancellationToken);
                artifactIds[serviceName] = uploaded.Id;
            }
            finally
            {
                TryDeleteFile(bundle.ZipPath);
            }
        }

        return artifactIds;
    }

    private IReadOnlyList<ConfigDiagnostic> ValidateDeploymentSources(MinicloudConfig config)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        foreach (var (serviceName, service) in config.Services)
        {
            var hasSource = !string.IsNullOrWhiteSpace(service.SourcePath);
            var hasImage = !string.IsNullOrWhiteSpace(service.Image);
            if (!hasSource && !hasImage)
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}", "Service must define sourcePath or image."));
            }
        }

        return diagnostics;
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    internal static IReadOnlyList<ConfigDiagnostic> ValidateDockerfileForService(string serviceName, MinicloudServiceConfig service)
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

    internal static string EffectiveDockerfilePath(MinicloudServiceConfig service) =>
        string.IsNullOrWhiteSpace(service.Dockerfile)
            ? Path.Combine(service.SourcePath!, "Dockerfile")
            : service.Dockerfile;

    private static bool DockerfileContainsInstruction(IEnumerable<string> dockerfile, string instruction) =>
        dockerfile.Any(line => DockerfileInstruction(line).Equals(instruction, StringComparison.OrdinalIgnoreCase));

    private static bool DockerfileExposesPort(IEnumerable<string> dockerfile, int port) =>
        DockerfilePorts.Read(dockerfile).Contains(port);

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

    private IReadOnlyList<ConfigDiagnostic> ValidateExplicitImagesForNoPublish(MinicloudConfig config)
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

    private string DeploymentImageForService(MinicloudServiceConfig service, string organizationSlug) =>
        _registryImageMapper.RuntimeImageForDeployment(service.Image!, organizationSlug);

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

}
