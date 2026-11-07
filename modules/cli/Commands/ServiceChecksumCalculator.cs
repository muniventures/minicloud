// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text.Json;
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

internal static class ServiceChecksumCalculator
{
    public static string Compute(string serviceName, MinicloudServiceConfig service, ServiceInputSnapshot? snapshot = null)
    {
        using var ownedSession = snapshot is null ? new DeploymentInputSession() : null;
        snapshot ??= ownedSession!.Get(serviceName, service);
        service = snapshot.Service;
        var hasSource = !string.IsNullOrWhiteSpace(service.SourcePath);
        string? Relative(string? path) => path is null || !hasSource ? null : Path.GetRelativePath(service.SourcePath!, path).Replace('\\', '/');
        var dockerfile = !hasSource ? null : CliApplication.EffectiveDockerfilePath(service);
        var ignore = dockerfile is null ? null : DeploymentArtifactBundler.EffectiveIgnoreFile(service.SourcePath!, dockerfile);
        var configuration = new
        {
            inputContract = 1,
            serviceName,
            service.Port,
            service.Public,
            service.Path,
            service.HealthPath,
            image = service.Image ?? string.Empty,
            dockerfile = Relative(dockerfile),
            ignore = Relative(ignore),
            env = Ordered(service.Env),
            secretEnv = Ordered(service.SecretEnv),
            buildEnv = Ordered(service.BuildEnv),
            assetMounts = Ordered(service.AssetMounts)
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(configuration));
        if (hasSource && service.BuildMode == Minicloud.Common.Contracts.ServiceBuildModes.Local)
        {
            if (!Minicloud.Common.Validation.ImagePublicationValidation.IsPlatform(service.BuildPlatform))
            { throw new CliCommandException(CliExitCodes.ValidationError, "Local build requires an API-resolved Linux platform."); }
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { service.BuildMode, service.BuildPlatform }));
        }
        foreach (var file in snapshot.Files.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { file.RelativePath, file.Digest, file.ExecuteMode }));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, T>? Ordered<T>(IReadOnlyDictionary<string, T>? values) =>
        values is not { Count: > 0 } ? null : values.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
}
