namespace Minicloud.Cli.Config;

public static class MinicloudConfigValidator
{
    public const int MaxDeploymentServices = 10;

    private static readonly ISet<string> Databases = new HashSet<string>(StringComparer.Ordinal)
    {
        "sqlite",
        "postgres",
        "none"
    };

    public static IReadOnlyList<ConfigDiagnostic> Validate(MinicloudConfig config)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        ValidateSlug(diagnostics, config.App, "app", "App is required and must use lowercase letters, numbers, dashes, and underscores.");

        if (string.IsNullOrWhiteSpace(config.AppId))
        {
            diagnostics.Add(new ConfigDiagnostic("appId", "App ID is required. Run 'minicloud init' to select or create an app."));
        }

        if (!string.IsNullOrWhiteSpace(config.Database) && !Databases.Contains(config.Database))
        {
            diagnostics.Add(new ConfigDiagnostic("database", "Database must be sqlite, postgres, or none."));
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

        foreach (var (name, service) in config.Services)
        {
            ValidateSlug(diagnostics, name, $"services.{name}", "Service name must use lowercase letters, numbers, dashes, and underscores.");

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

            if (service.Path != "/")
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{name}.path", "Path must be '/'."));
            }

            if (string.IsNullOrWhiteSpace(service.HealthPath) || !service.HealthPath.StartsWith("/", StringComparison.Ordinal))
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{name}.healthPath", "Health path is required and must start with '/'."));
            }

            if (service.Env is not null)
            {
                foreach (var (envKey, envValue) in service.Env)
                {
                    ValidateEnvironmentEntry(diagnostics, $"services.{name}.env", envKey, envValue, "Environment variable");
                }
            }

            if (service.SecretEnv is not null)
            {
                foreach (var (envKey, secretName) in service.SecretEnv)
                {
                    ValidateEnvironmentEntry(diagnostics, $"services.{name}.secretEnv", envKey, secretName, "Secret environment variable");
                    if (!IsEnvironmentVariableKey(secretName))
                    {
                        diagnostics.Add(new ConfigDiagnostic($"services.{name}.secretEnv.{envKey}", "Secret names must match ^[A-Za-z_][A-Za-z0-9_]*$."));
                    }
                }
            }

            if (service.Env is not null && service.SecretEnv is not null)
            {
                foreach (var duplicate in service.Env.Keys.Intersect(service.SecretEnv.Keys, StringComparer.Ordinal))
                {
                    diagnostics.Add(new ConfigDiagnostic($"services.{name}.secretEnv.{duplicate}", "Environment variables cannot be defined in both env and secretEnv."));
                }
            }
        }

        return diagnostics;
    }

    private static void ValidateSlug(List<ConfigDiagnostic> diagnostics, string? value, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || !MinicloudConfigLoader.SlugRegex().IsMatch(value))
        {
            diagnostics.Add(new ConfigDiagnostic(field, message));
        }
    }

    internal static bool IsEnvironmentVariableKey(string key)
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

    private static void ValidateEnvironmentEntry(
        List<ConfigDiagnostic> diagnostics,
        string fieldPrefix,
        string envKey,
        string? value,
        string label)
    {
        if (!IsEnvironmentVariableKey(envKey))
        {
            diagnostics.Add(new ConfigDiagnostic($"{fieldPrefix}.{envKey}", $"{label} names must match ^[A-Za-z_][A-Za-z0-9_]*$."));
        }

        if (value is null)
        {
            diagnostics.Add(new ConfigDiagnostic($"{fieldPrefix}.{envKey}", $"{label} values must be strings."));
        }
    }
}
