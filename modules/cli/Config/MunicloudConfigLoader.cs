using System.Text.RegularExpressions;

namespace Municloud.Cli.Config;

public static partial class MunicloudConfigLoader
{
    public static ConfigLoadResult Load(string path)
    {
        if (!File.Exists(path))
        {
            return Invalid(new ConfigDiagnostic("config", $"Config file '{path}' was not found."));
        }

        return Parse(File.ReadAllLines(path));
    }

    public static string ResolveDefaultPath()
    {
        if (File.Exists("municloud.yml"))
        {
            return "municloud.yml";
        }

        if (File.Exists("municloudconfig.yml"))
        {
            return "municloudconfig.yml";
        }

        return "municloud.yml";
    }

    public static ConfigLoadResult Parse(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = new Dictionary<string, string>(StringComparer.Ordinal);
        var services = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var serviceEnvs = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        string? currentSection = null;
        string? currentService = null;
        string? currentServiceSection = null;

        for (var index = 0; index < lines.Count; index++)
        {
            var rawLine = StripComment(lines[index]);
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var indent = rawLine.TakeWhile(char.IsWhiteSpace).Count();
            var line = rawLine.Trim();
            var parts = line.Split(':', 2);
            if (parts.Length != 2)
            {
                diagnostics.Add(new ConfigDiagnostic($"line {index + 1}", "Expected 'key: value'."));
                continue;
            }

            var key = parts[0].Trim();
            var value = Unquote(parts[1].Trim());

            if (indent == 0)
            {
                currentService = null;
                currentServiceSection = null;
                if (string.IsNullOrEmpty(value))
                {
                    currentSection = key;
                    if (currentSection != "services")
                    {
                        diagnostics.Add(new ConfigDiagnostic(key, "Only the 'services' mapping is supported as a nested section."));
                    }

                    continue;
                }

                currentSection = null;
                root[key] = value;
                continue;
            }

            if (currentSection != "services")
            {
                diagnostics.Add(new ConfigDiagnostic($"line {index + 1}", "Nested values are only supported under 'services'."));
                continue;
            }

            if (indent == 2 && string.IsNullOrEmpty(value))
            {
                currentService = key;
                currentServiceSection = null;
                services[currentService] = new Dictionary<string, string>(StringComparer.Ordinal);
                serviceEnvs[currentService] = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            if (indent == 4 && currentService is not null && string.IsNullOrEmpty(value) && string.Equals(key, "env", StringComparison.Ordinal))
            {
                currentServiceSection = "env";
                continue;
            }

            if (indent >= 6 && currentService is not null && string.Equals(currentServiceSection, "env", StringComparison.Ordinal))
            {
                serviceEnvs[currentService][key] = value;
                continue;
            }

            if (indent >= 4 && currentService is not null)
            {
                currentServiceSection = null;
                services[currentService][key] = value;
                continue;
            }

            diagnostics.Add(new ConfigDiagnostic($"line {index + 1}", "Service values must be nested under a service name."));
        }

        var config = new MunicloudConfig(
            root.GetValueOrDefault("app") ?? "",
            root.GetValueOrDefault("environment"),
            root.GetValueOrDefault("deploymentType"),
            root.GetValueOrDefault("database"),
            root.GetValueOrDefault("commitSha"),
            services.ToDictionary(
                x => x.Key,
                x => new MunicloudServiceConfig(
                    x.Value.GetValueOrDefault("sourcePath"),
                    x.Value.GetValueOrDefault("dockerfile"),
                    x.Value.GetValueOrDefault("image"),
                    ParseInt(x.Value.GetValueOrDefault("port"), diagnostics, $"services.{x.Key}.port"),
                    ParseBool(x.Value.GetValueOrDefault("public"), diagnostics, $"services.{x.Key}.public"),
                    x.Value.GetValueOrDefault("path"),
                    x.Value.GetValueOrDefault("healthPath"),
                    serviceEnvs.TryGetValue(x.Key, out var env) && env.Count > 0
                        ? new Dictionary<string, string>(env, StringComparer.Ordinal)
                        : null),
                StringComparer.Ordinal));

        diagnostics.AddRange(MunicloudConfigValidator.Validate(config));
        return new ConfigLoadResult(config, diagnostics);
    }

    private static ConfigLoadResult Invalid(ConfigDiagnostic diagnostic) => new(null, [diagnostic]);

    private static int? ParseInt(string? value, List<ConfigDiagnostic> diagnostics, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        diagnostics.Add(new ConfigDiagnostic(field, "Port must be an integer."));
        return null;
    }

    private static bool? ParseBool(string? value, List<ConfigDiagnostic> diagnostics, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        diagnostics.Add(new ConfigDiagnostic(field, "Public must be true or false."));
        return null;
    }

    private static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] is '"' or '\'')
            {
                inQuote = !inQuote;
            }

            if (!inQuote && line[i] == '#')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    internal static partial Regex SlugRegex();
}
