using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Minicloud.Cli.Config;

public static partial class MinicloudConfigLoader
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
        if (File.Exists("minicloud.yml"))
        {
            return "minicloud.yml";
        }

        if (File.Exists("minicloudconfig.yml"))
        {
            return "minicloudconfig.yml";
        }

        return "minicloud.yml";
    }

    public static ConfigLoadResult Parse(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = LoadRootMapping(string.Join(Environment.NewLine, lines), diagnostics);
        if (root is null)
        {
            return new ConfigLoadResult(null, diagnostics);
        }

        ValidateSupportedRootShape(root, diagnostics);
        var services = ParseServices(root, diagnostics);
        var config = new MinicloudConfig(
            GetScalar(root, "app", "app", diagnostics) ?? "",
            GetScalar(root, "database", "database", diagnostics),
            GetScalar(root, "commitSha", "commitSha", diagnostics),
            services)
        {
            AppId = GetScalar(root, "appId", "appId", diagnostics)
        };

        diagnostics.AddRange(MinicloudConfigValidator.Validate(config));
        return new ConfigLoadResult(config, diagnostics);
    }

    private static ConfigLoadResult Invalid(ConfigDiagnostic diagnostic) => new(null, [diagnostic]);

    private static YamlMappingNode? LoadRootMapping(string yaml, List<ConfigDiagnostic> diagnostics)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            var root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode : null;

            if (root is null or YamlScalarNode { Value: null or "" })
            {
                diagnostics.Add(new ConfigDiagnostic("config", "Config file is empty."));
                return null;
            }

            if (root is not YamlMappingNode mapping)
            {
                diagnostics.Add(new ConfigDiagnostic("config", "Config root must be a YAML mapping."));
                return null;
            }

            return mapping;
        }
        catch (YamlException ex)
        {
            diagnostics.Add(new ConfigDiagnostic("config", $"Invalid YAML: {ex.Message}"));
            return null;
        }
    }

    private static void ValidateSupportedRootShape(YamlMappingNode root, List<ConfigDiagnostic> diagnostics)
    {
        foreach (var (keyNode, valueNode) in root.Children)
        {
            var key = ScalarKey(keyNode, "config", diagnostics);
            if (key is null)
            {
                continue;
            }

            if (key == "services")
            {
                if (valueNode is not YamlMappingNode)
                {
                    diagnostics.Add(new ConfigDiagnostic("services", "Services must be a YAML mapping."));
                }

                continue;
            }

            if (valueNode is YamlMappingNode or YamlSequenceNode)
            {
                diagnostics.Add(new ConfigDiagnostic(key, "Only the 'services' mapping is supported as a nested section."));
            }
        }
    }

    private static IReadOnlyDictionary<string, MinicloudServiceConfig> ParseServices(YamlMappingNode root, List<ConfigDiagnostic> diagnostics)
    {
        if (!TryGetNode(root, "services", out var servicesNode) || servicesNode is not YamlMappingNode servicesMapping)
        {
            return new Dictionary<string, MinicloudServiceConfig>(StringComparer.Ordinal);
        }

        var services = new Dictionary<string, MinicloudServiceConfig>(StringComparer.Ordinal);
        foreach (var (serviceKeyNode, serviceNode) in servicesMapping.Children)
        {
            var serviceName = ScalarKey(serviceKeyNode, "services", diagnostics);
            if (serviceName is null)
            {
                continue;
            }

            if (serviceNode is not YamlMappingNode serviceMapping)
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}", "Service values must be a YAML mapping."));
                continue;
            }

            services[serviceName] = new MinicloudServiceConfig(
                GetScalar(serviceMapping, "sourcePath", $"services.{serviceName}.sourcePath", diagnostics),
                GetScalar(serviceMapping, "dockerfile", $"services.{serviceName}.dockerfile", diagnostics),
                GetScalar(serviceMapping, "image", $"services.{serviceName}.image", diagnostics),
                GetInt(serviceMapping, "port", $"services.{serviceName}.port", diagnostics),
                GetBool(serviceMapping, "public", $"services.{serviceName}.public", diagnostics),
                GetScalar(serviceMapping, "path", $"services.{serviceName}.path", diagnostics),
                GetScalar(serviceMapping, "healthPath", $"services.{serviceName}.healthPath", diagnostics),
                GetEnvironment(serviceMapping, serviceName, diagnostics));
        }

        return services;
    }

    private static IReadOnlyDictionary<string, string>? GetEnvironment(YamlMappingNode serviceMapping, string serviceName, List<ConfigDiagnostic> diagnostics)
    {
        if (!TryGetNode(serviceMapping, "env", out var envNode))
        {
            return null;
        }

        if (envNode is not YamlMappingNode envMapping)
        {
            diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}.env", "Environment variables must be a YAML mapping."));
            return null;
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (envKeyNode, envValueNode) in envMapping.Children)
        {
            var envKey = ScalarKey(envKeyNode, $"services.{serviceName}.env", diagnostics);
            if (envKey is null)
            {
                continue;
            }

            if (envValueNode is not YamlScalarNode scalar || scalar.Value is null)
            {
                diagnostics.Add(new ConfigDiagnostic($"services.{serviceName}.env.{envKey}", "Environment variable values must be strings."));
                continue;
            }

            env[envKey] = scalar.Value;
        }

        return env.Count > 0 ? env : null;
    }

    private static string? GetScalar(YamlMappingNode mapping, string key, string field, List<ConfigDiagnostic> diagnostics)
    {
        if (!TryGetNode(mapping, key, out var valueNode))
        {
            return null;
        }

        if (valueNode is YamlScalarNode scalar)
        {
            return scalar.Value;
        }

        diagnostics.Add(new ConfigDiagnostic(field, "Value must be a scalar."));
        return null;
    }

    private static int? GetInt(YamlMappingNode mapping, string key, string field, List<ConfigDiagnostic> diagnostics)
    {
        var value = GetScalar(mapping, key, field, diagnostics);
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

    private static bool? GetBool(YamlMappingNode mapping, string key, string field, List<ConfigDiagnostic> diagnostics)
    {
        var value = GetScalar(mapping, key, field, diagnostics);
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

    private static bool TryGetNode(YamlMappingNode mapping, string key, out YamlNode value)
    {
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is YamlScalarNode scalar && scalar.Value == key)
            {
                value = valueNode;
                return true;
            }
        }

        value = new YamlScalarNode();
        return false;
    }

    private static string? ScalarKey(YamlNode keyNode, string field, List<ConfigDiagnostic> diagnostics)
    {
        if (keyNode is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
        {
            return scalar.Value;
        }

        diagnostics.Add(new ConfigDiagnostic(field, "YAML mapping keys must be non-empty scalars."));
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    internal static partial Regex AppNameRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    internal static partial Regex SlugRegex();
}
