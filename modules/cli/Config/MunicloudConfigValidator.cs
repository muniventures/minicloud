namespace Municloud.Cli.Config;

public static class MunicloudConfigValidator
{
    public const int MaxDeploymentServices = 5;

    private static readonly ISet<string> DeploymentTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "backend_only",
        "frontend_only",
        "backend_frontend",
        "custom"
    };

    private static readonly ISet<string> Databases = new HashSet<string>(StringComparer.Ordinal)
    {
        "sqlite",
        "postgres"
    };

    public static IReadOnlyList<ConfigDiagnostic> Validate(MunicloudConfig config)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        ValidateSlug(diagnostics, config.App, "app", "App is required and must be a slug.");

        if (!string.IsNullOrWhiteSpace(config.Environment))
        {
            ValidateSlug(diagnostics, config.Environment, "environment", "Environment must be a slug.");
        }

        if (!string.IsNullOrWhiteSpace(config.DeploymentType) && !DeploymentTypes.Contains(config.DeploymentType))
        {
            diagnostics.Add(new ConfigDiagnostic("deploymentType", "Deployment type must be backend_only, frontend_only, backend_frontend, or custom."));
        }

        if (!string.IsNullOrWhiteSpace(config.Database) && !Databases.Contains(config.Database))
        {
            diagnostics.Add(new ConfigDiagnostic("database", "Database must be sqlite or postgres."));
        }

        if (config.Services.Count == 0)
        {
            diagnostics.Add(new ConfigDiagnostic("services", "At least one service is required."));
            return diagnostics;
        }

        if (config.Services.Count > MaxDeploymentServices)
        {
            diagnostics.Add(new ConfigDiagnostic("services", $"At most {MaxDeploymentServices} services are supported."));
        }

        var publicServices = 0;
        foreach (var (name, service) in config.Services)
        {
            ValidateSlug(diagnostics, name, $"services.{name}", "Service name must be a slug.");

            if (service.Port is null)
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{name}.port", "Port is required."));
            }
            else if (service.Port is < 1 or > 65535)
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{name}.port", "Port must be between 1 and 65535."));
            }

            if (service.Public is null)
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{name}.public", "Public is required."));
            }
            else if (service.Public.Value)
            {
                publicServices++;
            }

            if (string.IsNullOrWhiteSpace(service.Path) || !service.Path.StartsWith("/", StringComparison.Ordinal))
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{name}.path", "Path is required and must start with '/'."));
            }

            if (string.IsNullOrWhiteSpace(service.HealthPath) || !service.HealthPath.StartsWith("/", StringComparison.Ordinal))
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{name}.healthPath", "Health path is required and must start with '/'."));
            }

            if (service.Env is not null)
            {
                foreach (var (envKey, envValue) in service.Env)
                {
                    if (!IsEnvironmentVariableKey(envKey))
                    {
                        diagnostics.Add(new ConfigDiagnostic($"services.{name}.env.{envKey}", "Environment variable names must match ^[A-Za-z_][A-Za-z0-9_]*$."));
                    }

                    if (envValue is null)
                    {
                        diagnostics.Add(new ConfigDiagnostic($"services.{name}.env.{envKey}", "Environment variable values must be strings."));
                    }
                }
            }
        }

        if (publicServices == 0)
        {
            diagnostics.Add(new ConfigDiagnostic("services", "At least one public service is required."));
        }

        ValidateServiceShape(config, diagnostics);
        return diagnostics;
    }

    private static void ValidateServiceShape(MunicloudConfig config, List<ConfigDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(config.DeploymentType))
        {
            return;
        }

        var names = config.Services.Keys.ToHashSet(StringComparer.Ordinal);
        switch (config.DeploymentType)
        {
            case "backend_only" when names.Count != 1 || !names.Contains("backend"):
                diagnostics.Add(new ConfigDiagnostic("services", "backend_only requires one service named 'backend'."));
                break;
            case "frontend_only" when names.Count != 1 || !names.Contains("frontend"):
                diagnostics.Add(new ConfigDiagnostic("services", "frontend_only requires one service named 'frontend'."));
                break;
            case "backend_frontend" when names.Count != 2 || !names.SetEquals(["frontend", "backend"]):
                diagnostics.Add(new ConfigDiagnostic("services", "backend_frontend requires exactly 'frontend' and 'backend' services."));
                break;
            case "custom":
                break;
        }
    }

    private static void ValidateSlug(List<ConfigDiagnostic> diagnostics, string? value, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || !MunicloudConfigLoader.SlugRegex().IsMatch(value))
        {
            diagnostics.Add(new ConfigDiagnostic(field, message));
        }
    }

    private static bool IsEnvironmentVariableKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (!(char.IsLetter(key[0]) || key[0] == '_'))
        {
            return false;
        }

        for (var i = 1; i < key.Length; i++)
        {
            var character = key[i];
            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
