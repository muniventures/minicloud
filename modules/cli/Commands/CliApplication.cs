// SPDX-License-Identifier: MPL-2.0

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using Selfrun.Cli.Api;
using Selfrun.Cli.Auth;
using Selfrun.Cli.Config;

namespace Selfrun.Cli.Commands;

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
    private readonly SelfrunApiClient _apiClient;
    private readonly Rendering.IDeploymentTimingSink _timingSink;

    public Rendering.IDeploymentTimingSink TimingSink => _timingSink;

    public CliApplication(
        IConsole console,
        CliEnvironment environment,
        TokenStore tokenStore,
        SelfrunApiClient apiClient,
        Rendering.IDeploymentTimingSink? timingSink = null)
    {
        _console = console;
        _environment = environment;
        _tokenStore = tokenStore;
        _apiClient = apiClient;
        _timingSink = timingSink ?? new Rendering.DeploymentTimingSink();
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return CliExitCodes.Success;
        }

        if (args[0] is "-v" or "--version" or "version")
        {
            PrintVersion();
            return CliExitCodes.Success;
        }

        if (args[0] == "help")
        {
            var targetCommand = string.Join(" ", args.Skip(1).TakeWhile(a => a is not ("-h" or "--help")));
            if (!string.IsNullOrWhiteSpace(targetCommand))
            {
                return PrintCommandHelp(targetCommand);
            }

            PrintHelp();
            return CliExitCodes.Success;
        }

        if (args.Skip(1).Any(a => a is "-h" or "--help"))
        {
            var commandPrefix = string.Join(" ", args.TakeWhile(a => a is not ("-h" or "--help")));
            return PrintCommandHelp(commandPrefix);
        }

        try
        {
            if (args[0] == "--env")
            {
                PrintEnvironment();
                return CliExitCodes.Success;
            }
            if (args[0] == "instance") return await RunInstanceAsync(args.Skip(1).ToArray(), cancellationToken);
            if (args[0] == "seed") return await RunSeedAsync(args.Skip(1).ToArray(), cancellationToken);
            if (args[0] is "token" or "init" or "add-service" or "login" or "assets" or "deploy" or "branch" or "status" or "logs" or "apps" or "domains" or "secrets" or "environments" or "plans" or "providers" or "github")
                await EnsureInstanceAsync(cancellationToken);
            return args[0] switch
            {
                "token" => await RunTokenAsync(args.Skip(1).ToArray(), cancellationToken),
                "init" => await RunInitAsync(args.Skip(1).ToArray(), cancellationToken),
                "add-service" => await RunAddServiceAsync(args.Skip(1).ToArray(), cancellationToken),
                "login" => await RunLoginAsync(args.Skip(1).ToArray(), cancellationToken),
                "assets" => await RunAssetsAsync(args.Skip(1).ToArray(), cancellationToken),
                "deploy" => await RunDeployAsync(args.Skip(1).ToArray(), cancellationToken),
                "environments" => await RunEnvironmentsAsync(args.Skip(1).ToArray(), cancellationToken),
                "plans" => await RunPlansAsync(args.Skip(1).ToArray(), cancellationToken),
                "providers" => await RunProvidersAsync(args.Skip(1).ToArray(), cancellationToken),
                "github" => await RunGitHubAsync(args.Skip(1).ToArray(), cancellationToken),
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
        catch (ApiException ex) when (ex.Code == "missing_service_secret")
        {
            _console.WriteError($"API error ({ex.Code}): {ex.Message}");
            return CliExitCodes.ValidationError;
        }
        catch (ApiException ex) when (ex.Code == "validation_failed")
        {
            _console.WriteError($"API error ({ex.Code}): {ex.Message}");
            foreach (var detail in ex.Details)
            {
                _console.WriteError($"{detail.Field}: {detail.Message}");
            }
            return CliExitCodes.ValidationError;
        }
        catch (ApiException ex) when (ex.StatusCode is 400 or 409 && (ex.Code.StartsWith("plan_", StringComparison.Ordinal)
            || ex.Code.StartsWith("provider_", StringComparison.Ordinal) || ex.Code == "revision_conflict"))
        {
            _console.WriteError($"API error ({ex.Code}): {ex.Message}");
            return CliExitCodes.ValidationError;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _console.WriteLine("Operation canceled.");
            return CliExitCodes.Success;
        }
        catch (TaskCanceledException)
        {
            _console.WriteError("Network error: request timed out.");
            return CliExitCodes.NetworkOrApiUnavailable;
        }
        catch (CliCommandException ex)
        {
            _console.WriteError(ex.Message);
            return ex.ExitCode;
        }
    }

    public static string GetVersion()
    {
        var assembly = typeof(CliApplication).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+');
            var version = plusIndex > 0 ? informational[..plusIndex] : informational;
            if (!string.IsNullOrWhiteSpace(version) && version != "0.0.0" && version != "1.0.0.0")
            {
                return version;
            }
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion != null && assemblyVersion != new Version(0, 0, 0, 0) && assemblyVersion != new Version(1, 0, 0, 0))
        {
            return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        }

        return "1.0.1";
    }

    private void PrintVersion()
    {
        _console.WriteLine($"selfrun version {GetVersion()}");
        var source = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var match = System.Text.RegularExpressions.Regex.Match(source ?? "", @"(?:sha\.|\+)([0-9a-f]{40})(?:$|\.)");
        if (match.Success) _console.WriteLine("source " + match.Groups[1].Value);
    }

    private void PrintHelp()
    {
        _console.WriteLine($"Selfrun CLI version {GetVersion()}");
        _console.WriteLine("Deploy and manage containerized services on your private infrastructure.");
        _console.WriteLine();
        _console.WriteLine("Usage:");
        _console.WriteLine("  selfrun <command> [arguments] [options]");
        _console.WriteLine();
        _console.WriteLine("Commands:");
        _console.WriteLine("  github          Set up and inspect your organization-owned GitHub connection");
        _console.WriteLine("  seed            Provision the first control plane without an instance/login");
        _console.WriteLine("  init            Initialize a new application and generate selfrun.yml");
        _console.WriteLine("  add-service     Add a new service configuration to an existing application");
        _console.WriteLine("  deploy          Deploy changed services to Selfrun (or 'all' / '--all' to deploy all)");
        _console.WriteLine("  environments    List, create, update, sleep, wake, or rename application environments");
        _console.WriteLine("  plans           List named plans in an organization");
        _console.WriteLine("  providers       Add provider accounts, list SSH keys, or import legacy bindings");
        _console.WriteLine("  branch          Manage preview environments (deploy branch, branch destroy)");
        _console.WriteLine("  assets          Publish immutable customer assets on the assigned VPS");
        _console.WriteLine("  status          Show deployment status and service URLs (--watch to stream updates)");
        _console.WriteLine("  logs            View and tail logs for an application or deployment");
        _console.WriteLine("  apps            List or inspect applications");
        _console.WriteLine("  domains         Manage custom domains and service subdomains");
        _console.WriteLine("  secrets         Manage encrypted environment secrets");
        _console.WriteLine("  login           Log in to Selfrun using an API token");
        _console.WriteLine("  token           Set active API authentication token");
        _console.WriteLine("  instance        Add, list, or switch Selfrun instances");
        _console.WriteLine("  help            Show help for Selfrun commands (or 'selfrun help <command>')");
        _console.WriteLine("  version         Show the current CLI version (-v, --version)");
        _console.WriteLine();
        _console.WriteLine("Options:");
        _console.WriteLine("  -h, --help      Show help information");
        _console.WriteLine("  -v, --version   Show the current CLI version");
        _console.WriteLine("  --env           Show active CLI environment configuration");
        _console.WriteLine();
        _console.WriteLine("Run 'selfrun help <command>' or 'selfrun <command> --help' for details on a command.");
        _console.WriteLine("Documentation: https://selfrun.dev (source: https://github.com/muniperez/selfrun)");
    }

    private int PrintCommandHelp(string command)
    {
        var normalized = command.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "seed":
            case "seed status":
            case "seed resume":
            case "seed cleanup":
            case "seed export":
            case "seed register-provider":
                PrintSeedHelp();
                return CliExitCodes.Success;
            case "instance":
            case "instance add":
            case "instance list":
            case "instance use":
                _console.WriteLine("Manage Selfrun instances (add validates and activates the instance)");
                _console.WriteLine("  selfrun instance add <name> <url>");
                _console.WriteLine("  selfrun instance list");
                _console.WriteLine("  selfrun instance use <name>");
                _console.WriteLine("URL is the main service hostname, e.g. cloud.example.com; the API is api.cloud.example.com.");
                return CliExitCodes.Success;
            case "assets":
            case "assets publish":
                _console.WriteLine("Usage: selfrun assets publish <name> [--config selfrun.yml]");
                _console.WriteLine("Publishes an assets.<name> source definition through the deployment queue. Builds stay on the customer VPS.");
                _console.WriteLine("Activation requires an explicit service assetMounts.<name>.releaseId pin.");
                return CliExitCodes.Success;
            case "deploy cancel":
                _console.WriteLine("Usage: selfrun deploy cancel <deployment-number> [--organization <id>]");
                _console.WriteLine("Stops further deployment work. Changes already applied are not restored.");
                _console.WriteLine("Deployment numbers look like 20261110.1 and are scoped to an organization.");
                return CliExitCodes.Success;
            case "deploy":
                _console.WriteLine("Deploy services to Selfrun");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun deploy [service ...] [--all] [--config selfrun.yml] [--database db] [--pgpassword password] [--build-mode source|local] [--build-jobs N] [--no-publish]");
                _console.WriteLine("  selfrun deploy cancel <deployment-number> [--organization <id>]");
                _console.WriteLine("  selfrun deploy branch [--config selfrun.yml] [--database db] [--pgpassword password] [--build-mode source|local] [--build-jobs N] [--no-publish]");
                _console.WriteLine();
                _console.WriteLine("Details:");
                _console.WriteLine("  - Automatically checks SHA-256 checksums of each service against active API state and deploys only changed services.");
                _console.WriteLine("  - Specify service names (e.g. 'selfrun deploy api') to deploy only those services.");
                _console.WriteLine("  - Local image builds are the default for sourcePath services; buildMode: source opts into server builds.");
                _console.WriteLine("  - --build-mode source|local overrides selected services; local requires a local Docker buildx builder.");
                _console.WriteLine("  - 'selfrun deploy all' or '--all' deploys all configured services unconditionally.");
                _console.WriteLine("  - --environment <name> selects a named environment; required when more than one exists.");
                _console.WriteLine("  - --plan <name> overrides YAML plan, then the selected environment's saved plan. No provider argument is needed.");
                _console.WriteLine("  - 'selfrun deploy branch' deploys all configured services into an isolated preview branch environment.");
                _console.WriteLine();
                _console.WriteLine("Options:");
                _console.WriteLine("  --all                   Deploy all configured services unconditionally");
                _console.WriteLine("  --config <path>         Path to config file (default: selfrun.yml)");
                _console.WriteLine("  --database <type>       Provision database (e.g. postgres, none)");
                _console.WriteLine("  --pgpassword <pwd>      Postgres database password");
                _console.WriteLine($"  --build-jobs N          Run 1 to {SelfrunConfigValidator.MaxDeploymentServices} local builds at once. Overrides buildJobs in selfrun.yml (default: {DefaultBuildJobs})");
                _console.WriteLine("  --no-publish            Deploy pre-built images without building or publishing");
                return CliExitCodes.Success;

            case "environments":
            case "environments sleep":
            case "environments wake":
                _console.WriteLine("selfrun environments list [--config path]");
                _console.WriteLine($"selfrun environments create <name> --plan <plan> [--database sqlite|postgres] {SshOptionsUsage} [--config path]");
                _console.WriteLine($"selfrun environments update <name> [--sleep-after on|off|<duration>] [--traffic-activity on|off] {SshOptionsUsage} [--config path]");
                _console.WriteLine("selfrun environments sleep <name> [--no-wait] [--config path]");
                _console.WriteLine("selfrun environments wake <name> [--keep-awake <duration>] [--no-wait] [--config path]");
                _console.WriteLine("selfrun environments rename <name> <new-name> [--config path]");
                _console.WriteLine("SSH options select a personal key installed when the environment's VPS is first provisioned:");
                _console.WriteLine("  --ssh-key <key-id>            Existing key from 'selfrun providers ssh-keys --plan <plan>' (providers:read)");
                _console.WriteLine("  --ssh-public-key-file <path>  New OpenSSH public key (.pub) registered at provisioning (providers:write)");
                _console.WriteLine("  --ssh-key-name <name>         Name for the new key (default: key comment or file name)");
                _console.WriteLine("Sleep timeout durations use m, h, or d units (30m-30d); enabling without a duration defaults to 2h.");
                _console.WriteLine("Sleep and wake wait for completion and report progress by default; --no-wait prints the operation ID.");
                _console.WriteLine("Omitting SSH options keeps Selfrun-only access on create and the current selection on update. Selections are immutable after server allocation.");
                _console.WriteLine("Sleep-only updates do not require SSH options.");
                return CliExitCodes.Success;

            case "plans":
                _console.WriteLine("selfrun plans list [--organization <id>] [--config path]");
                return CliExitCodes.Success;
            case "providers":
                _console.WriteLine("selfrun providers add --organization <id> --name <name> --provider hetzner|digital-ocean|vultr [--project-id <id>] [--os-id <id>]");
                _console.WriteLine("Reads the cloud token from the provider environment variable; tokens are never command arguments.");
                _console.WriteLine("selfrun providers import-legacy --manifest <path>");
                _console.WriteLine("selfrun providers ssh-keys <provider-account-id> | --plan <plan> [--organization <id>]");
                return CliExitCodes.Success;
            case "github":
                _console.WriteLine("selfrun github setup [--organization <id>] [--no-browser]");
                _console.WriteLine("selfrun github status [--organization <id>] [--json]");
                _console.WriteLine("selfrun github repositories|disconnect|reconnect|reconcile [--organization <id>]");
                _console.WriteLine("Setup opens the dashboard; approve App creation and installation separately. No App credentials are returned to the CLI.");
                return CliExitCodes.Success;
            case "deploy branch":
                _console.WriteLine("Deploy all services to an isolated branch environment");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun deploy branch [--config selfrun.yml] [--database db] [--pgpassword password] [--build-mode source|local] [--build-jobs N] [--no-publish]");
                _console.WriteLine();
                _console.WriteLine("Details:");
                _console.WriteLine("  Creates or reuses an isolated child preview app for the current Git branch and deploys all services.");
                return CliExitCodes.Success;

            case "branch":
            case "branch destroy":
                _console.WriteLine("Manage branch environments");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun deploy branch [--config selfrun.yml] [--database db] [--pgpassword password] [--build-mode source|local] [--build-jobs N] [--no-publish]");
                _console.WriteLine("  selfrun branch destroy [branch] [--config selfrun.yml]");
                _console.WriteLine();
                _console.WriteLine("Details:");
                _console.WriteLine("  'selfrun branch destroy' tears down the preview environment and VPS for the specified or current branch.");
                return CliExitCodes.Success;

            case "status":
                _console.WriteLine("Show deployment status and service URLs");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun status [deployment-number] [--watch] [--organization <id>] [--diagnostics]");
                _console.WriteLine();
                _console.WriteLine("Details:");
                _console.WriteLine("  Deployment numbers look like 20261110.1 and are scoped to an organization.");
                _console.WriteLine("  Without a number, shows the latest deployment in the selected organization.");
                _console.WriteLine();
                _console.WriteLine("Options:");
                _console.WriteLine("  --watch                 Continuously watch and stream deployment status until finished");
                _console.WriteLine("  --organization <id>     Organization to resolve the number in (default: the token's only organization, or the app in selfrun.yml)");
                _console.WriteLine("  --diagnostics           Also print the internal deployment ID for support correlation");
                return CliExitCodes.Success;

            case "logs":
                _console.WriteLine("View and tail logs for an application or deployment");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun logs [app|deployment-number] [--service service] [--source source] [--tail count] [--since duration] [--organization <id>]");
                _console.WriteLine();
                _console.WriteLine("Details:");
                _console.WriteLine("  A deployment number such as 20261110.1 shows that deployment's logs; any other value is an app name or ID.");
                _console.WriteLine("  Without an argument, shows logs of the latest deployment in the selected organization.");
                _console.WriteLine();
                _console.WriteLine("Options:");
                _console.WriteLine("  --organization <id>     Organization for deployment numbers (default: the token's only organization, or the app in selfrun.yml)");
                _console.WriteLine("  --service <name>        Filter logs by service name");
                _console.WriteLine("  --source <name>         Filter by log source (e.g. docker, system)");
                _console.WriteLine("  --tail <count>          Number of log lines to show (default: 100)");
                _console.WriteLine("  --since <duration>      Show logs newer than duration (e.g. 30m, 1h)");
                return CliExitCodes.Success;

            case "init":
                _console.WriteLine("Initialize a new Selfrun application");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine($"  selfrun init [--advanced] [--config selfrun.yml] [--force] {SshOptionsUsage}");
                _console.WriteLine();
                _console.WriteLine("Options:");
                _console.WriteLine("  --advanced              Interactive prompt for advanced routing, ports, and domains");
                _console.WriteLine("  --config <path>         Output configuration file path (default: selfrun.yml)");
                _console.WriteLine("  --force                 Overwrite existing configuration file without prompting");
                _console.WriteLine("  --ssh-key <key-id>      Existing provider SSH key for a newly created app's first environment");
                _console.WriteLine("  --ssh-public-key-file <path>  New OpenSSH public key for a newly created app; never a private key");
                _console.WriteLine("  --ssh-key-name <name>   Name for the new public key");
                return CliExitCodes.Success;

            case "add-service":
                _console.WriteLine("Add a new service to an existing Selfrun application");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun add-service [app] [--app app] [--advanced] [--config selfrun.service.yml] [--force]");
                _console.WriteLine();
                _console.WriteLine("Options:");
                _console.WriteLine("  --app <app>             App slug or ID (defaults to interactive selection)");
                _console.WriteLine("  --advanced              Interactive prompt for advanced routing and domains");
                _console.WriteLine("  --config <path>         Output configuration file path (default: selfrun.service.yml)");
                _console.WriteLine("  --force                 Overwrite existing service configuration without prompting");
                return CliExitCodes.Success;

            case "login":
                _console.WriteLine("Log in to Selfrun");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun login --token <token>");
                return CliExitCodes.Success;

            case "token":
            case "token set":
                _console.WriteLine("Set active API authentication token");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun token set <token>");
                return CliExitCodes.Success;

            case "apps":
            case "apps list":
            case "apps inspect":
                _console.WriteLine("Manage Selfrun applications");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun apps list");
                _console.WriteLine("  selfrun apps inspect <app>");
                return CliExitCodes.Success;

            case "domains":
            case "domains list":
            case "domains add-subdomain":
            case "domains disable":
            case "domains delete":
                _console.WriteLine("Manage custom domains and service subdomains");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun domains list --app <app>");
                _console.WriteLine("  selfrun domains add-subdomain --app <app> --service <service> [--label label]");
                _console.WriteLine("  selfrun domains disable --app <app> --hostname <host>");
                _console.WriteLine("  selfrun domains delete --app <app> --hostname <host>");
                return CliExitCodes.Success;

            case "secrets":
            case "secrets list":
            case "secrets set":
            case "secrets remove":
                _console.WriteLine("Manage application environment secrets");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  selfrun secrets list [--app app]");
                _console.WriteLine("  selfrun secrets set [--app app] <NAME> [--value value]");
                _console.WriteLine("  selfrun secrets remove [--app app] <NAME>");
                return CliExitCodes.Success;

            case "help":
                PrintHelp();
                return CliExitCodes.Success;

            case "version":
                PrintVersion();
                return CliExitCodes.Success;

            default:
                _console.WriteError($"Unknown command '{command}'.");
                _console.WriteLine();
                PrintHelp();
                return CliExitCodes.ValidationError;
        }
    }

    private void PrintEnvironment()
    {
        _console.WriteLine("Selfrun CLI environment");
        _environment.LoadActiveInstance();
        _console.WriteLine($"Active instance: {_environment.ActiveInstanceName ?? "none"}");
        _console.WriteLine($"API URL: {(string.IsNullOrEmpty(_environment.ApiBaseUrl) ? "not configured" : _environment.ApiBaseUrl)}");
        _console.WriteLine($"Application domain: {_environment.AppDomain}");
    }

    private int UnknownCommand(string command)
    {
        _console.WriteError($"Unknown command '{command}'.");
        PrintHelp();
        return CliExitCodes.ValidationError;
    }

    private void PrintDiagnostics(IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            _console.WriteError($"{diagnostic.Field}: {diagnostic.Message}");
        }
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
            "--environment",
            "--service",
            "--source",
            "--tail",
            "--since",
            "--value",
            "--config",
            "--hostname",
            "--label",
            "--organization"
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

    private void WriteUrlLine(string label, string url) =>
        _console.WriteLine($"{label}: {FormatTerminalLink(url, url)}");

    private string FormatTerminalLink(string url, string text)
    {
        if (!_console.SupportsAnsi || string.IsNullOrWhiteSpace(url))
        {
            return text;
        }

        var safeUrl = StripAnsiControlCharacters(url);
        var safeText = StripAnsiControlCharacters(text);
        return $"\x1b]8;;{safeUrl}\x1b\\{safeText}\x1b]8;;\x1b\\";
    }

    private static string StripAnsiControlCharacters(string value) =>
        new(value.Where(character => !char.IsControl(character) || character is '\t').ToArray());

    private static bool IsTransientNetworkException(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (ex is OperationCanceledException or TaskCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (ex is HttpRequestException or TimeoutException or IOException or SocketException)
        {
            return true;
        }

        if (ex is ApiException apiEx)
        {
            return apiEx.StatusCode is 408 or 429 or 500 or 502 or 503 or 504;
        }

        if (ex is AggregateException agg)
        {
            return agg.InnerExceptions.Count > 0 && agg.InnerExceptions.All(inner => IsTransientNetworkException(inner, cancellationToken));
        }

        return ex.InnerException is not null && IsTransientNetworkException(ex.InnerException, cancellationToken);
    }
}
