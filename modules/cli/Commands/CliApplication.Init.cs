using Minicloud.Cli.Api;
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
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

                        return await RelinkExistingConfigAsync(args, outputPath, cancellationToken);
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

                    return await RelinkExistingConfigAsync(args, outputPath, cancellationToken);
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

    private async Task<int> RelinkExistingConfigAsync(string[] args, string outputPath, CancellationToken cancellationToken)
    {
        var me = await _apiClient.GetMeAsync(cancellationToken);
        var organization = me.Organizations.FirstOrDefault();
        if (organization is null)
        {
            _console.WriteError("Auth error: your token is not associated with an organization.");
            return CliExitCodes.AuthError;
        }

        var app = await PromptAppSelectionAsync(organization, GetOption(args, "--app") ?? FirstPositionalArg(args), cancellationToken);
        var updated = MinicloudConfigWriter.UpdateAppIdentity(File.ReadAllText(outputPath), app.Slug, app.Id);
        File.WriteAllText(outputPath, updated);
        _console.WriteLine($"Updated app identity in {outputPath}; existing service settings preserved.");
        _console.WriteLine($"Next: minicloud deploy --config {outputPath}");
        return CliExitCodes.Success;
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
            config = PromptAdvancedServiceOptions(service.Name, config);
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

    private MinicloudServiceConfig PromptAdvancedServiceOptions(string serviceName, MinicloudServiceConfig config)
    {
        var defaults = DefaultServiceOptions(serviceName);
        var image = PromptOptional($"{ToTitle(serviceName)} pre-built image (optional)", config.Image ?? "none");
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

    private static string ToTitle(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
