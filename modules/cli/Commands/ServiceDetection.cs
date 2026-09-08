using System.Text.Json;
using System.Text.RegularExpressions;
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

internal sealed record DetectedService(
    string Name,
    string SourcePath,
    string? Dockerfile,
    string ProjectType,
    string Framework,
    int Port,
    string HealthPath,
    bool Public = true)
{
    public MinicloudServiceConfig ToConfig() =>
        new(SourcePath, Dockerfile, null, Port, Public, "/", HealthPath);
}

internal static partial class ServiceDetection
{
    public const int MaxDepth = 5;

    private static readonly ISet<string> ExcludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".hg",
        ".svn",
        ".idea",
        ".vscode",
        ".next",
        ".nuxt",
        ".turbo",
        "bin",
        "obj",
        "node_modules",
        "bower_components",
        "dist",
        "build",
        "out",
        "coverage",
        "target",
        ".gradle",
        ".mvn",
        ".classpath",
        ".settings"
    };

    public static IReadOnlyList<DetectedService> Detect(string rootPath, int maxDepth = MaxDepth)
    {
        var root = Path.GetFullPath(rootPath);
        var services = new List<DetectedService>();
        var serviceNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var directory in EnumerateCandidateDirectories(root, maxDepth))
        {
            var detected = DetectDirectory(root, directory);
            if (detected is null)
            {
                continue;
            }

            services.Add(detected with { Name = UniqueName(detected.Name, serviceNames) });
        }

        return services
            .OrderBy(service => Depth(root, Path.GetFullPath(service.SourcePath)))
            .ThenBy(service => service.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static DetectedService? DetectDirectory(string root, string directory)
    {
        var packageJson = Path.Combine(directory, "package.json");
        if (File.Exists(packageJson))
        {
            return DetectNodeProject(root, directory, packageJson);
        }

        var csproj = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (csproj is not null)
        {
            return DetectDotNetProject(root, directory, csproj);
        }

        if (File.Exists(Path.Combine(directory, "pom.xml")) ||
            File.Exists(Path.Combine(directory, "build.gradle")) ||
            File.Exists(Path.Combine(directory, "build.gradle.kts")))
        {
            var framework = DetectJavaFramework(directory);
            if (framework is null)
            {
                return null;
            }

            return new DetectedService(
                ServiceNameFromPath(null, directory),
                RelativePath(root, directory),
                RelativeDockerfile(root, directory),
                "java",
                framework,
                8080,
                "/health");
        }

        if (FindDockerfile(directory) is not null)
        {
            return new DetectedService(
                ServiceNameFromPath(null, directory),
                RelativePath(root, directory),
                RelativeDockerfile(root, directory),
                "container",
                "dockerfile",
                8080,
                "/");
        }

        return null;
    }

    private static DetectedService? DetectNodeProject(string root, string directory, string packageJson)
    {
        var package = ReadPackageJson(packageJson);
        var dependencies = package.Dependencies;
        string? framework = null;
        var port = 3000;
        var healthPath = "/";

        if (dependencies.Contains("next"))
        {
            framework = "nextjs";
        }
        else if (dependencies.Contains("@remix-run/node") || dependencies.Contains("@remix-run/react"))
        {
            framework = "remix";
        }
        else if (dependencies.Contains("@angular/core"))
        {
            framework = "angular";
            port = 4200;
        }
        else if (dependencies.Contains("vite"))
        {
            framework = dependencies.Contains("react") ? "vite-react" : "vite";
        }
        else if (dependencies.Contains("express") || dependencies.Contains("fastify") || dependencies.Contains("@nestjs/core"))
        {
            framework = dependencies.Contains("@nestjs/core") ? "nestjs" : "node-api";
            healthPath = "/health";
        }

        if (framework is null && FindDockerfile(directory) is null)
        {
            return null;
        }

        return new DetectedService(
            ServiceNameFromPath(package.Name, directory),
            RelativePath(root, directory),
            RelativeDockerfile(root, directory),
            "node",
            framework ?? "node-docker",
            port,
            healthPath);
    }

    private static DetectedService? DetectDotNetProject(string root, string directory, string csproj)
    {
        var text = File.ReadAllText(csproj);
        if (!IsDotNetWebProject(text) && FindDockerfile(directory) is null)
        {
            return null;
        }

        var framework = IsDotNetWebProject(text) ? "dotnet-web" : "dotnet-docker";
        var projectName = Path.GetFileNameWithoutExtension(csproj);

        return new DetectedService(
            ServiceNameFromPath(projectName, directory),
            RelativePath(root, directory),
            RelativeDockerfile(root, directory),
            "dotnet",
            framework,
            8080,
            "/health");
    }

    private static string? DetectJavaFramework(string directory)
    {
        foreach (var fileName in new[] { "pom.xml", "build.gradle", "build.gradle.kts" })
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            if (text.Contains("spring-boot", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("spring-boot-starter-web", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("spring-boot-starter-webflux", StringComparison.OrdinalIgnoreCase)))
            {
                return "springboot";
            }
        }

        return null;
    }

    private static bool IsDotNetWebProject(string projectText) =>
        projectText.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);

    private static PackageJsonInfo ReadPackageJson(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            AddObjectKeys(root, "dependencies", dependencies);
            AddObjectKeys(root, "devDependencies", dependencies);
            return new PackageJsonInfo(name, dependencies);
        }
        catch (JsonException)
        {
            return new PackageJsonInfo(null, new HashSet<string>(StringComparer.Ordinal));
        }
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

    private static IEnumerable<string> EnumerateCandidateDirectories(string root, int maxDepth)
    {
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            yield return directory;
            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var child in SafeEnumerateDirectories(directory).OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                if (ShouldSkipDirectory(child))
                {
                    continue;
                }

                pending.Enqueue((child, depth + 1));
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool ShouldSkipDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return ExcludedDirectories.Contains(name);
    }

    private static string? RelativeDockerfile(string root, string directory)
    {
        var dockerfile = FindDockerfile(directory);
        if (dockerfile is null || string.Equals(Path.GetFileName(dockerfile), "Dockerfile", StringComparison.Ordinal))
        {
            return null;
        }

        return RelativePath(root, dockerfile);
    }

    private static string? FindDockerfile(string directory) =>
        Directory.EnumerateFiles(directory, "Dockerfile*", SearchOption.TopDirectoryOnly)
            .Where(file => string.Equals(Path.GetFileName(file), "Dockerfile", StringComparison.Ordinal) ||
                Path.GetFileName(file).StartsWith("Dockerfile.", StringComparison.Ordinal))
            .OrderBy(file => string.Equals(Path.GetFileName(file), "Dockerfile", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();

    private static string RelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? "." : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string ServiceNameFromPath(string? preferredName, string directory)
    {
        var raw = string.IsNullOrWhiteSpace(preferredName)
            ? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : preferredName;
        if (raw.StartsWith("@", StringComparison.Ordinal) && raw.Contains('/'))
        {
            raw = raw[(raw.LastIndexOf('/') + 1)..];
        }

        raw = raw.Replace(".Api", "-api", StringComparison.OrdinalIgnoreCase)
            .Replace(".Web", "-web", StringComparison.OrdinalIgnoreCase);
        var normalized = ServiceNameRegex().Replace(raw.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "service" : normalized;
    }

    private static string UniqueName(string name, ISet<string> existing)
    {
        if (existing.Add(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{name}-{i}";
            if (existing.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static int Depth(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "."
            ? 0
            : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
    }

    private sealed record PackageJsonInfo(string? Name, ISet<string> Dependencies);

    [GeneratedRegex("[^a-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNameRegex();
}
