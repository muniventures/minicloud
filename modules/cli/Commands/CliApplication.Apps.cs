using Minicloud.Cli.Api;
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
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

    private static string DisplayNameFromSlug(string slug)
    {
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return slug;
        }

        return string.Join(" ", words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
