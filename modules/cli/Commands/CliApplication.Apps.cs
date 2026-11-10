// SPDX-License-Identifier: MPL-2.0

using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;

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

        var apps = await GetApplicationChoicesAsync(organization.Id, cancellationToken);
        foreach (var app in apps)
        {
            _console.WriteLine($"{app.Slug}\t{app.Id}\t{app.Environments.Count} environments");
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

        var app = await ResolveAppOptionAsync(args.Concat(["--app", appIdOrSlug]).ToArray(), cancellationToken);

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
                foreach (var (name, mount) in service.Storage ?? new Dictionary<string, StorageMountRequest>())
                {
                    _console.WriteLine($"    Storage: {name}  {mount.SizeGb} GB  {mount.MountPath}  uid={mount.Uid} gid={mount.Gid}");
                }
                foreach (var domain in service.Domains.Where(x => x.Status != "disabled").OrderBy(x => x.Hostname, StringComparer.Ordinal))
                {
                    WriteUrlLine($"Service URL ({service.Name})", $"https://{domain.Hostname}");
                }
            }
        }

        return CliExitCodes.Success;
    }

    private async Task<ApplicationResponse> PromptAppSelectionAsync(OrganizationSummaryDto organization, string? requestedApp, CancellationToken cancellationToken)
    {
        var apps = await GetApplicationChoicesAsync(organization.Id, cancellationToken);
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

    private async Task<ApplicationResponse> PromptExistingAppSelectionAsync(OrganizationSummaryDto organization, string? requestedApp, CancellationToken cancellationToken)
    {
        var apps = await GetApplicationChoicesAsync(organization.Id, cancellationToken);
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

    private async Task<ApplicationResponse> CreateAppAsync(
        OrganizationSummaryDto organization,
        string appSlug,
        string database,
        CancellationToken cancellationToken)
    {
        var createSlug = appSlug.ToLowerInvariant();
        var plans = (await _apiClient.GetPlansAsync(organization.Id, cancellationToken)).Where(x => x.Enabled && x.AccountEnabled).ToArray();
        if (plans.Length == 0)
            throw new CliCommandException(CliExitCodes.ValidationError, "No enabled plans are available. Add a provider account and plan in the portal first.");
        var selectedPlan = PromptSingleSelect("Plan", plans.Select(x => (Value: x.Name, Label: $"{x.Name} ({x.Region}, {x.Sku})")).ToArray(), plans[0].Name);
        var request = new CreateApplicationRequest(
            organization.Id,
            DisplayNameFromSlug(createSlug),
            createSlug,
            new CreateInitialEnvironmentRequest("production", selectedPlan, database));

        _console.WriteLine($"Creating app '{createSlug}' in organization '{organization.Name}'...");
        return await _apiClient.CreateAppAsync(request, cancellationToken);
    }

    private static ApplicationResponse? FindApp(IEnumerable<ApplicationResponse> apps, string appIdOrSlug) =>
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

    private async Task<EnvironmentDetailResponse> ResolveAppOptionAsync(string[] args, CancellationToken ct)
    {
        var environmentName = GetOption(args, "--environment");
        if (args.Any(x => x == "--environment" || x.StartsWith("--environment=", StringComparison.Ordinal)) && string.IsNullOrWhiteSpace(environmentName))
            throw new CliCommandException(CliExitCodes.ValidationError, "--environment requires an environment name.");
        ApplicationResponse application;
        var requested = GetOption(args, "--app");
        if (string.IsNullOrWhiteSpace(requested))
        {
            var config = MinicloudConfigLoader.Load(GetOption(args, "--config") ?? MinicloudConfigLoader.ResolveDefaultPath());
            if (!config.IsValid || string.IsNullOrWhiteSpace(config.Config?.AppId))
                throw new CliCommandException(CliExitCodes.ValidationError, "Missing --app <app> and no valid appId was found in minicloud.yml.");
            application = await GetApplicationContextAsync(config.Config.AppId, ct);
        }
        else
        {
            var me = await _apiClient.GetMeAsync(ct);
            var organization = me.Organizations.FirstOrDefault()
                ?? throw new CliCommandException(CliExitCodes.AuthError, "Auth error: your token is not associated with an organization.");
            application = FindApp(await GetApplicationChoicesAsync(organization.Id, ct), requested)
                ?? throw new CliCommandException(CliExitCodes.ValidationError, $"App '{requested}' was not found.");
        }
        var environment = await ResolveEnvironmentAsync(application, environmentName, ct);
        WriteEnvironmentTarget(environment);
        return environment;
    }

    private async Task<ApplicationResponse> GetApplicationContextAsync(string id, CancellationToken ct)
    {
        var application = await _apiClient.GetApplicationAsync(id, ct);
        EnsureApplicationActive(application);
        return application;
    }

    private static void EnsureApplicationActive(ApplicationResponse application)
    {
        if (application.DeletionStatus is not null)
            throw new CliCommandException(CliExitCodes.ValidationError, $"Application '{application.Name}' is being deleted or requires deletion recovery.");
    }

    private Task<IReadOnlyList<ApplicationResponse>> GetApplicationChoicesAsync(string organizationId, CancellationToken ct) =>
        _apiClient.GetApplicationsAsync(organizationId, ct);

    private async Task<EnvironmentDetailResponse> ResolveEnvironmentAsync(ApplicationResponse application, string? name, CancellationToken ct)
    {
        EnsureApplicationActive(application);
        var environments = application.Environments.Where(x => x.Kind == "persistent").ToArray();
        if (environments.Length == 0)
            throw new CliCommandException(CliExitCodes.ValidationError, $"Application '{application.Name}' has no persistent environments. Create one with 'minicloud environments create <name> --plan <plan>' first.");
        ApplicationEnvironmentResponse? selected;
        if (name is null)
        {
            var active = environments.Where(x => x.DeletionStatus is null).ToArray();
            if (active.Length == 0)
                throw new CliCommandException(CliExitCodes.ValidationError, $"Environment '{environments[0].Name}' is being deleted or failed deletion and is blocked until cleanup succeeds.");
            if (active.Length != 1)
                throw new CliCommandException(CliExitCodes.ValidationError, $"Choose an environment with --environment <name>. Available: {string.Join(", ", active.Select(x => x.Name))}");
            selected = active[0];
        }
        else selected = environments.SingleOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            throw new CliCommandException(CliExitCodes.ValidationError, $"Unknown environment '{name}'. Available: {string.Join(", ", environments.Select(x => x.Name))}");
        if (selected.DeletionStatus is not null)
            throw new CliCommandException(CliExitCodes.ValidationError, selected.DeletionStatus == "failed"
                ? $"Environment '{selected.Name}' failed deletion and is blocked until cleanup succeeds."
                : $"Environment '{selected.Name}' is being deleted.");
        var environment = await _apiClient.GetEnvironmentAsync(selected.Id, ct);
        if (environment.ApplicationId != application.Id)
            throw new CliCommandException(CliExitCodes.ValidationError, "The selected environment does not belong to this application.");
        if (environment.DeletionStatus is not null)
            throw new CliCommandException(CliExitCodes.ValidationError, $"Environment '{selected.Name}' is being deleted or requires deletion recovery.");
        return environment with { Name = application.Name };
    }

    private void WriteEnvironmentTarget(EnvironmentDetailResponse app)
    {
        if (app.EnvironmentName is not null)
            _console.WriteLine($"App: {app.Name} · Environment: {app.EnvironmentName} ({app.Id})");
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
