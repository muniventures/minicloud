// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Minicloud.Cli.Api;
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

internal static class DeploymentArtifactBundler
{
    public const long MaxArtifactBytes = 250L * 1024L * 1024L;
    internal const string ManifestEntryName = "minicloud-artifact-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".minicloud",
        "node_modules",
        ".next",
        "bin",
        "obj",
        "target",
        "dist",
        "build",
        "out"
    };

    public static DeploymentArtifactBundle Create(
        string appId,
        string serviceName,
        MinicloudServiceConfig service,
        string? commitSha,
        string outputDirectory,
        long maxArtifactBytes = MaxArtifactBytes,
        ServiceInputSnapshot? snapshot = null)
    {
        if (string.IsNullOrWhiteSpace(service.SourcePath))
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"Config error: services.{serviceName}.sourcePath is required for artifact deployment. Add it or pass --no-publish.");
        }

        var originalSourcePath = service.SourcePath;
        using var inputs = snapshot is null ? new DeploymentInputSession() : null;
        snapshot ??= inputs!.Get(serviceName, service);
        service = snapshot.Service;
        var sourceRoot = Path.GetFullPath(service.SourcePath!);
        if (!Directory.Exists(sourceRoot))
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"Config error: services.{serviceName}.sourcePath '{service.SourcePath}' does not exist.");
        }

        var dockerfilePath = CliApplication.EffectiveDockerfilePath(service);
        if (!File.Exists(dockerfilePath))
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"Config error: services.{serviceName}.dockerfile '{dockerfilePath}' was not found.");
        }

        var files = (snapshot is null
                ? SelectFiles(sourceRoot, Path.GetFullPath(dockerfilePath))
                : snapshot.Files.Select(x => Path.Combine(sourceRoot, x.RelativePath)).ToArray())
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => RelativeArchivePath(sourceRoot, path), StringComparer.Ordinal)
            .ToList();

        if (files.Any(path => string.Equals(
                RelativeArchivePath(sourceRoot, path), ManifestEntryName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CliCommandException(
                CliExitCodes.ValidationError,
                $"Artifact source for service '{serviceName}' contains reserved file '{ManifestEntryName}'.");
        }

        var dockerfileFullPath = Path.GetFullPath(dockerfilePath);
        var dockerfileEntryPath = IsUnderDirectory(sourceRoot, dockerfileFullPath)
            ? RelativeArchivePath(sourceRoot, dockerfileFullPath)
            : "minicloud-artifact/Dockerfile";
        if (!IsUnderDirectory(sourceRoot, dockerfileFullPath))
        {
            files.Add(dockerfileFullPath);
        }

        if (files.Count == 0)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"Artifact for service '{serviceName}' has no source files after ignore rules.");
        }

        Directory.CreateDirectory(outputDirectory);
        var zipPath = Path.Combine(outputDirectory, $"{serviceName}-{Guid.NewGuid():N}.zip");
        var sourceBytes = 0L;
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                var entryName = string.Equals(file, dockerfileFullPath, StringComparison.Ordinal) && !IsUnderDirectory(sourceRoot, dockerfileFullPath)
                    ? dockerfileEntryPath
                    : RelativeArchivePath(sourceRoot, file);
                ValidateEntryName(entryName);
                zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                sourceBytes += new FileInfo(file).Length;
            }

            var manifest = new DeploymentArtifactManifest(
                1,
                appId,
                serviceName,
                originalSourcePath!,
                dockerfileEntryPath,
                ".",
                service.Port!.Value,
                service.Public!.Value,
                service.Path!,
                service.HealthPath!,
                commitSha,
                files.Count,
                sourceBytes,
                DateTimeOffset.UtcNow,
                service.BuildEnv);
            var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using var manifestStream = manifestEntry.Open();
            JsonSerializer.Serialize(manifestStream, manifest, JsonOptions);
        }

        var sizeBytes = new FileInfo(zipPath).Length;
        if (sizeBytes > maxArtifactBytes)
        {
            File.Delete(zipPath);
            throw new CliCommandException(CliExitCodes.ValidationError, $"Artifact for service '{serviceName}' is {sizeBytes} bytes, which is above the {maxArtifactBytes} byte limit.");
        }

        var sha256 = ComputeSha256(zipPath);
        using var manifestReadZip = ZipFile.OpenRead(zipPath);
        var manifestJson = manifestReadZip.GetEntry(ManifestEntryName)
            ?? throw new CliCommandException(CliExitCodes.ValidationError, "Artifact manifest was not written.");
        using var stream = manifestJson.Open();
        var manifestObject = JsonSerializer.Deserialize<DeploymentArtifactManifest>(stream, JsonOptions)
            ?? throw new CliCommandException(CliExitCodes.ValidationError, "Artifact manifest could not be read.");

        return new DeploymentArtifactBundle(zipPath, sizeBytes, sha256, manifestObject);
    }

    internal static void ValidateMetadata(string sourceRoot, string path)
    {
        RejectSymlink(sourceRoot, path);
        if (IsForcedExcluded(sourceRoot, path))
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "Build metadata matches a protected artifact exclusion.");
        }
    }

    internal static string? EffectiveIgnoreFile(string sourceRoot, string dockerfile)
    {
        var specific = Path.GetFullPath(dockerfile) + ".dockerignore";
        if (File.Exists(specific)) { return specific; }
        var fallback = Path.Combine(sourceRoot, ".dockerignore");
        return File.Exists(fallback) ? fallback : null;
    }

    internal static IReadOnlyList<string> SelectFiles(string sourceRoot, string? dockerfile = null)
    {
        var ignoreFile = EffectiveIgnoreFile(sourceRoot, dockerfile ?? Path.Combine(sourceRoot, "Dockerfile"));
        if (ignoreFile is not null) { ValidateMetadata(sourceRoot, ignoreFile); }
        var matcher = new DockerIgnoreMatcher(ignoreFile is null ? [] : File.ReadAllLines(ignoreFile));
        return
        SelectCandidateFiles(sourceRoot)
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Select(path => RejectSymlink(sourceRoot, path))
            .Where(path => !IsForcedExcluded(sourceRoot, path))
            .Where(path => !matcher.Excludes(RelativeArchivePath(sourceRoot, path)) || path == dockerfile || path == ignoreFile)
            .OrderBy(path => RelativeArchivePath(sourceRoot, path), StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> SelectCandidateFiles(string sourceRoot)
    {
        var gitFiles = TryGitFiles(sourceRoot);
        if (gitFiles is not null)
        {
            return gitFiles;
        }

        var ignoreRules = ReadIgnoreRules(sourceRoot);
        return Directory.EnumerateFiles(sourceRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            .Where(path => !MatchesIgnoreRule(sourceRoot, path, ignoreRules))
            .ToArray();
    }

    private static IReadOnlyList<string>? TryGitFiles(string sourceRoot)
    {
        try
        {
            var start = new ProcessStartInfo("git", ["-C", sourceRoot, "ls-files", "-z", "--cached", "--others", "--exclude-standard"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }

            return output
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => Path.GetFullPath(Path.Combine(sourceRoot, path)))
                .Where(File.Exists)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsForcedExcluded(string sourceRoot, string path)
    {
        var relative = RelativeArchivePath(sourceRoot, path);
        var fileName = Path.GetFileName(relative);
        if (fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            ((fileName.StartsWith("credentials", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("secrets", StringComparison.OrdinalIgnoreCase)) && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ||
            fileName.Equals("minicloud.secrets.env", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return relative.Split('/').Any(segment => ExcludedSegments.Contains(segment));
    }

    private static string RejectSymlink(string sourceRoot, string path)
    {
        var boundary = sourceRoot;
        while (!IsUnderDirectory(boundary, path) && Path.GetDirectoryName(boundary) is { } parent)
        { boundary = parent; }
        FileSystemInfo? current = new FileInfo(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new CliCommandException(CliExitCodes.ValidationError, $"Artifact file '{RelativeArchivePath(sourceRoot, path)}' is a symlink, which is not supported.");
            }
            if (current.FullName == boundary) { return path; }
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        return path;
    }

    private static IReadOnlyList<string> ReadIgnoreRules(string sourceRoot)
    {
        var gitignore = Path.Combine(sourceRoot, ".gitignore");
        if (!File.Exists(gitignore))
        {
            return [];
        }

        return File.ReadAllLines(gitignore)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal) && !line.StartsWith("!", StringComparison.Ordinal))
            .ToArray();
    }

    private static bool MatchesIgnoreRule(string sourceRoot, string path, IReadOnlyList<string> rules)
    {
        if (rules.Count == 0)
        {
            return false;
        }

        var relative = RelativeArchivePath(sourceRoot, path);
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rule in rules)
        {
            var normalized = rule.Trim('/').Replace('\\', '/');
            if (normalized.Length == 0)
            {
                continue;
            }

            if (rule.EndsWith("/", StringComparison.Ordinal) && parts.Contains(normalized, StringComparer.Ordinal))
            {
                return true;
            }

            if (relative.Equals(normalized, StringComparison.Ordinal) ||
                relative.StartsWith(normalized + "/", StringComparison.Ordinal) ||
                parts.Contains(normalized, StringComparer.Ordinal))
            {
                return true;
            }

            if (normalized.Contains('*', StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.IsMatch(relative, "^" + System.Text.RegularExpressions.Regex.Escape(normalized).Replace("\\*", ".*") + "$"))
            {
                return true;
            }
        }

        return false;
    }

    private static string RelativeArchivePath(string sourceRoot, string path) =>
        Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');

    private static bool IsUnderDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.Ordinal);
    }

    private static void ValidateEntryName(string entryName)
    {
        if (Path.IsPathRooted(entryName) ||
            entryName == ".." ||
            entryName.StartsWith("../", StringComparison.Ordinal) ||
            entryName.Contains("/../", StringComparison.Ordinal))
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"Artifact entry '{entryName}' is unsafe.");
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}

internal sealed record DeploymentArtifactBundle(
    string ZipPath,
    long SizeBytes,
    string Sha256,
    DeploymentArtifactManifest Manifest);
