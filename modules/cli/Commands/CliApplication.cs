using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
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

        if (args[0] == "--env")
        {
            PrintEnvironment();
            return CliExitCodes.Success;
        }

        if (args.Skip(1).Any(a => a is "-h" or "--help"))
        {
            var commandPrefix = string.Join(" ", args.TakeWhile(a => a is not ("-h" or "--help")));
            return PrintCommandHelp(commandPrefix);
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
        _console.WriteLine($"minicloud version {GetVersion()}");
    }

    private void PrintHelp()
    {
        _console.WriteLine($"Minicloud CLI version {GetVersion()}");
        _console.WriteLine("Deploy and manage containerized services on your private infrastructure.");
        _console.WriteLine();
        _console.WriteLine("Usage:");
        _console.WriteLine("  minicloud <command> [arguments] [options]");
        _console.WriteLine();
        _console.WriteLine("Commands:");
        _console.WriteLine("  init            Initialize a new application and generate minicloud.yml");
        _console.WriteLine("  add-service     Add a new service configuration to an existing application");
        _console.WriteLine("  deploy          Deploy changed services to Minicloud (or 'all' / '--all' to deploy all)");
        _console.WriteLine("  branch          Manage preview environments (deploy branch, branch destroy)");
        _console.WriteLine("  status          Show deployment status and service URLs (--watch to stream updates)");
        _console.WriteLine("  logs            View and tail logs for an application or deployment");
        _console.WriteLine("  apps            List or inspect applications");
        _console.WriteLine("  domains         Manage custom domains and service subdomains");
        _console.WriteLine("  secrets         Manage encrypted environment secrets");
        _console.WriteLine("  login           Log in to Minicloud using an API token");
        _console.WriteLine("  token           Set active API authentication token");
        _console.WriteLine("  help            Show help for Minicloud commands (or 'minicloud help <command>')");
        _console.WriteLine("  version         Show the current CLI version (-v, --version)");
        _console.WriteLine();
        _console.WriteLine("Options:");
        _console.WriteLine("  -h, --help      Show help information");
        _console.WriteLine("  -v, --version   Show the current CLI version");
        _console.WriteLine("  --env           Show active CLI environment configuration");
        _console.WriteLine();
        _console.WriteLine("Run 'minicloud help <command>' or 'minicloud <command> --help' for details on a command.");
        _console.WriteLine("Documentation: https://github.com/muniventures/minicloud");
    }

    private int PrintCommandHelp(string command)
    {
        var normalized = command.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "deploy":
                _console.WriteLine("Deploy services to Minicloud");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud deploy [service ...] [--all] [--config minicloud.yml] [--database db] [--pgpassword password] [--no-publish]");
                _console.WriteLine("  minicloud deploy branch [--config minicloud.yml] [--database db] [--pgpassword password] [--no-publish]");
                _console.WriteLine();
                _console.WriteLine("Details:");
                _console.WriteLine("  - Automatically checks SHA-256 checksums of each service against active API state and deploys only changed services.");
                _console.WriteLine("  - Specify service names (e.g. 'minicloud deploy api') to deploy only those services.");
                _console.WriteLine("  - 'minicloud deploy all' or '--all' deploys all configured services unconditionally.");
                _console.WriteLine("  - 'minicloud deploy branch' deploys all configured services into an isolated preview branch environment.");
                _console.WriteLine();
                _console.WriteLine("Options:");
                _console.WriteLine("  --all                   Deploy all configured services unconditionally");
                _console.WriteLine("  --config <path>         Path to config file (default: minicloud.yml)");
                _console.WriteLine("  --database <type>       Provision database (e.g. postgres, none)");
                _console.WriteLine("  --pgpassword <pwd>      Postgres database password");
                _console.WriteLine("  --no-publish            Deploy pre-built images without building or publishing");
                return CliExitCodes.Success;

            case "deploy branch":
                _console.WriteLine("Deploy all services to an isolated branch environment");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud deploy branch [--config minicloud.yml] [--database db] [--pgpassword password] [--no-publish]");
                _console.WriteLine();
                _console.WriteLine("Details:");
                _console.WriteLine("  Creates or reuses an isolated child preview app for the current Git branch and deploys all services.");
                return CliExitCodes.Success;

            case "branch":
            case "branch destroy":
                _console.WriteLine("Manage branch environments");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud deploy branch [--config minicloud.yml] [--database db] [--pgpassword password] [--no-publish]");
                _console.WriteLine("  minicloud branch destroy [branch] [--config minicloud.yml]");
                _console.WriteLine();
                _console.WriteLine("Details:");
                _console.WriteLine("  'minicloud branch destroy' tears down the preview environment and VPS for the specified or current branch.");
                return CliExitCodes.Success;

            case "status":
                _console.WriteLine("Show deployment status and service URLs");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud status [deployment-id] [--watch]");
                _console.WriteLine();
                _console.WriteLine("Options:");
                _console.WriteLine("  --watch                 Continuously watch and stream deployment status until finished");
                return CliExitCodes.Success;

            case "logs":
                _console.WriteLine("View and tail logs for an application or deployment");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud logs [app|deployment-id] [--service service] [--source source] [--tail count] [--since duration]");
                _console.WriteLine();
                _console.WriteLine("Options:");
                _console.WriteLine("  --service <name>        Filter logs by service name");
                _console.WriteLine("  --source <name>         Filter by log source (e.g. docker, system)");
                _console.WriteLine("  --tail <count>          Number of log lines to show (default: 100)");
                _console.WriteLine("  --since <duration>      Show logs newer than duration (e.g. 30m, 1h)");
                return CliExitCodes.Success;

            case "init":
                _console.WriteLine("Initialize a new Minicloud application");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud init [--advanced] [--config minicloud.yml] [--force]");
                _console.WriteLine();
                _console.WriteLine("Options:");
                _console.WriteLine("  --advanced              Interactive prompt for advanced routing, ports, and domains");
                _console.WriteLine("  --config <path>         Output configuration file path (default: minicloud.yml)");
                _console.WriteLine("  --force                 Overwrite existing configuration file without prompting");
                return CliExitCodes.Success;

            case "add-service":
                _console.WriteLine("Add a new service to an existing Minicloud application");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud add-service [app] [--app app] [--advanced] [--config minicloud.service.yml] [--force]");
                _console.WriteLine();
                _console.WriteLine("Options:");
                _console.WriteLine("  --app <app>             App slug or ID (defaults to interactive selection)");
                _console.WriteLine("  --advanced              Interactive prompt for advanced routing and domains");
                _console.WriteLine("  --config <path>         Output configuration file path (default: minicloud.service.yml)");
                _console.WriteLine("  --force                 Overwrite existing service configuration without prompting");
                return CliExitCodes.Success;

            case "login":
                _console.WriteLine("Log in to Minicloud");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud login --token <token>");
                return CliExitCodes.Success;

            case "token":
            case "token set":
                _console.WriteLine("Set active API authentication token");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud token set <token>");
                return CliExitCodes.Success;

            case "apps":
            case "apps list":
            case "apps inspect":
                _console.WriteLine("Manage Minicloud applications");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud apps list");
                _console.WriteLine("  minicloud apps inspect <app>");
                return CliExitCodes.Success;

            case "domains":
            case "domains list":
            case "domains add-subdomain":
            case "domains disable":
            case "domains delete":
                _console.WriteLine("Manage custom domains and service subdomains");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud domains list --app <app>");
                _console.WriteLine("  minicloud domains add-subdomain --app <app> --service <service> [--label label]");
                _console.WriteLine("  minicloud domains disable --app <app> --hostname <host>");
                _console.WriteLine("  minicloud domains delete --app <app> --hostname <host>");
                return CliExitCodes.Success;

            case "secrets":
            case "secrets list":
            case "secrets set":
            case "secrets remove":
                _console.WriteLine("Manage application environment secrets");
                _console.WriteLine();
                _console.WriteLine("Usage:");
                _console.WriteLine("  minicloud secrets list [--app app]");
                _console.WriteLine("  minicloud secrets set [--app app] <NAME> [--value value]");
                _console.WriteLine("  minicloud secrets remove [--app app] <NAME>");
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
        _console.WriteLine("Minicloud CLI environment");
        _console.WriteLine($"Environment: {CliEnvironment.ApiUrlEnvironmentVariable} defaults to {_environment.ApiBaseUrl}");
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
