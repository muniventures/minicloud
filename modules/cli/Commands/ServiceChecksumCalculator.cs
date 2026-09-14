using System.Security.Cryptography;
using System.Text;
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

internal static class ServiceChecksumCalculator
{
    public static string Compute(string serviceName, MinicloudServiceConfig service)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendString(hash, $"service:{serviceName}\n");
        AppendString(hash, $"port:{service.Port}\n");
        AppendString(hash, $"public:{service.Public}\n");
        AppendString(hash, $"path:{service.Path}\n");
        AppendString(hash, $"healthPath:{service.HealthPath}\n");
        AppendString(hash, $"image:{service.Image ?? string.Empty}\n");

        if (service.Env is { Count: > 0 })
        {
            foreach (var kvp in service.Env.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                AppendString(hash, $"env:{kvp.Key}={kvp.Value}\n");
            }
        }

        if (service.SecretEnv is { Count: > 0 })
        {
            foreach (var kvp in service.SecretEnv.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                AppendString(hash, $"secretEnv:{kvp.Key}={kvp.Value}\n");
            }
        }

        if (!string.IsNullOrWhiteSpace(service.SourcePath))
        {
            var sourceRoot = Path.GetFullPath(service.SourcePath);
            if (Directory.Exists(sourceRoot))
            {
                var files = DeploymentArtifactBundler.SelectFiles(sourceRoot);
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
                    AppendString(hash, $"file:{relativePath}\n");
                    AppendFileContent(hash, file);
                }

                var dockerfilePath = CliApplication.EffectiveDockerfilePath(service);
                if (File.Exists(dockerfilePath))
                {
                    var fullDockerfilePath = Path.GetFullPath(dockerfilePath);
                    AppendString(hash, $"dockerfile:{Path.GetFileName(fullDockerfilePath)}\n");
                    AppendFileContent(hash, fullDockerfilePath);
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
    }

    private static void AppendFileContent(IncrementalHash hash, string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
        }
    }
}
