using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

internal static class FrameworkDeploymentValidator
{
    private const string MinicloudSuffix = "app.muni.dev";

    private static readonly string[] ViteConfigFiles =
    [
        "vite.config.ts",
        "vite.config.js",
        "vite.config.mts",
        "vite.config.mjs",
        "vite.config.cts",
        "vite.config.cjs"
    ];

    public static IReadOnlyList<ConfigDiagnostic> ValidatePublicHostCompatibility(
        string serviceName,
        MinicloudServiceConfig service,
        string organizationSlug,
        string appSlug)
    {
        if (service.Public != true || string.IsNullOrWhiteSpace(service.SourcePath) || !Directory.Exists(service.SourcePath))
        {
            return [];
        }

        var dependencies = ReadPackageDependencies(Path.Combine(service.SourcePath, "package.json"));
        if (dependencies.Count == 0)
        {
            return [];
        }

        var diagnostics = new List<ConfigDiagnostic>();
        var hostname = DefaultHostname(organizationSlug, appSlug, serviceName);

        if (dependencies.Contains("vite"))
        {
            ValidateVitePreviewHost(serviceName, service.SourcePath, hostname, diagnostics);
        }

        if (dependencies.Contains("next"))
        {
            ValidateNextStartCommand(serviceName, service, diagnostics);
        }

        return diagnostics;
    }

    private static void ValidateVitePreviewHost(
        string serviceName,
        string sourcePath,
        string hostname,
        List<ConfigDiagnostic> diagnostics)
    {
        var configPath = ViteConfigFiles
            .Select(fileName => Path.Combine(sourcePath, fileName))
            .FirstOrDefault(File.Exists);
        if (configPath is null)
        {
            diagnostics.Add(ViteDiagnostic(serviceName, hostname));
            return;
        }

        var configText = File.ReadAllText(configPath);
        if (!ConfigAllowsHost(configText, hostname))
        {
            diagnostics.Add(ViteDiagnostic(serviceName, hostname));
        }
    }

    private static ConfigDiagnostic ViteDiagnostic(string serviceName, string hostname) =>
        new(
            $"services.{serviceName}.sourcePath",
            $"Vite preview blocks the Minicloud host '{hostname}'. Add preview.allowedHosts: ['.{MinicloudSuffix}'] or the exact hostname to vite.config.* before deploying.");

    private static bool ConfigAllowsHost(string configText, string hostname)
    {
        var compact = RemoveWhitespace(configText);
        return compact.Contains("allowedHosts:true", StringComparison.Ordinal) ||
            configText.Contains($".{MinicloudSuffix}", StringComparison.Ordinal) ||
            configText.Contains(hostname, StringComparison.Ordinal);
    }

    private static void ValidateNextStartCommand(
        string serviceName,
        MinicloudServiceConfig service,
        List<ConfigDiagnostic> diagnostics)
    {
        var dockerfilePath = CliApplication.EffectiveDockerfilePath(service);
        if (!File.Exists(dockerfilePath))
        {
            return;
        }

        var dockerfile = File.ReadAllText(dockerfilePath);
        if (ContainsAdjacentTokens(dockerfile, "next", "dev"))
        {
            diagnostics.Add(new ConfigDiagnostic(
                $"services.{serviceName}.dockerfile",
                "Next.js public deployments must not run the development server. Use 'next start' or a production Node server in Dockerfile CMD/ENTRYPOINT."));
        }
    }

    private static HashSet<string> ReadPackageDependencies(string packageJsonPath)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(packageJsonPath))
        {
            return dependencies;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            AddObjectKeys(document.RootElement, "dependencies", dependencies);
            AddObjectKeys(document.RootElement, "devDependencies", dependencies);
        }
        catch (JsonException)
        {
            return [];
        }

        return dependencies;
    }

    private static void AddObjectKeys(JsonElement root, string propertyName, ISet<string> keys)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            keys.Add(property.Name);
        }
    }

    private static string DefaultHostname(string organizationSlug, string appSlug, string serviceName) =>
        $"{DefaultLabel(organizationSlug, appSlug, serviceName)}.{MinicloudSuffix}";

    private static string DefaultLabel(string organizationSlug, string appSlug, string serviceName)
    {
        var label = $"{organizationSlug}-{appSlug}-{serviceName}".ToLowerInvariant();
        if (label.Length <= 63)
        {
            return label;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(label)))[..8].ToLowerInvariant();
        return $"{label[..54].TrimEnd('-')}-{hash}";
    }

    private static string RemoveWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool ContainsAdjacentTokens(string value, string first, string second)
    {
        var tokens = value.Split(
            [' ', '\t', '\r', '\n', '"', '\'', ',', '[', ']', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (tokens[index].Equals(first, StringComparison.OrdinalIgnoreCase) &&
                tokens[index + 1].Equals(second, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
