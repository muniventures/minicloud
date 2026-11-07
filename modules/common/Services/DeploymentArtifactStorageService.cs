using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class DeploymentArtifactStorageOptions
{
    public const string SectionName = "DeploymentArtifacts";
    public string RootPath { get; set; } = Path.Combine(Path.GetTempPath(), "minicloud-artifacts");
    public long MaxArtifactBytes { get; set; } = 250L * 1024L * 1024L;
    public long MaxDeploymentBytes { get; set; } = 750L * 1024L * 1024L;
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(1);
}

public sealed class DeploymentArtifactStorageService(
    IOptions<DeploymentArtifactStorageOptions> options,
    TimeProvider timeProvider)
{
    private readonly DeploymentArtifactStorageOptions _options = options.Value;

    public long MaxArtifactBytes => _options.MaxArtifactBytes;
    public TimeSpan Retention => _options.Retention;

    public string CreateStoragePath(string organizationId, string appId, string artifactId) =>
        Path.Combine($"org_{CleanSegment(organizationId)}", $"app_{CleanSegment(appId)}", $"art_{CleanSegment(artifactId)}", "service.zip");

    public string AbsolutePath(DeploymentArtifact artifact) =>
        SafeAbsolutePath(artifact.StoragePath);

    public void DeleteContent(DeploymentArtifact artifact)
    {
        var path = AbsolutePath(artifact);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public async Task<StoredArtifactResult> StoreAsync(
        DeploymentArtifact artifact,
        Stream content,
        long declaredSize,
        string declaredSha256,
        CancellationToken cancellationToken)
    {
        if (declaredSize < 1 || declaredSize > _options.MaxArtifactBytes)
        {
            return StoredArtifactResult.Failed("artifact_size_invalid", $"Artifact size must be between 1 and {_options.MaxArtifactBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }

        if (!string.Equals(declaredSha256, artifact.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return StoredArtifactResult.Failed("artifact_sha_mismatch", "Declared artifact checksum does not match the artifact metadata.");
        }

        var finalPath = AbsolutePath(artifact);
        var finalDirectory = Path.GetDirectoryName(finalPath) ?? _options.RootPath;
        Directory.CreateDirectory(finalDirectory);
        Directory.CreateDirectory(Path.Combine(_options.RootPath, "tmp"));
        var tempPath = Path.Combine(_options.RootPath, "tmp", $"{artifact.Id}.{Guid.NewGuid():N}.upload");

        long size = 0;
        await using var output = File.Create(tempPath);
        using var sha = SHA256.Create();
        var buffer = new byte[1024 * 128];
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            size += read;
            if (size > _options.MaxArtifactBytes)
            {
                output.Close();
                File.Delete(tempPath);
                return StoredArtifactResult.Failed("artifact_too_large", $"Artifact exceeds the {_options.MaxArtifactBytes.ToString(CultureInfo.InvariantCulture)} byte limit.");
            }

            sha.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        sha.TransformFinalBlock([], 0, 0);
        await output.DisposeAsync();

        var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (size != declaredSize || !string.Equals(hash, declaredSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempPath);
            return StoredArtifactResult.Failed("artifact_integrity_mismatch", "Uploaded artifact size or checksum does not match the declared metadata.");
        }

        if (File.Exists(finalPath))
        {
            File.Delete(tempPath);
            return StoredArtifactResult.Failed("artifact_already_exists", "Artifact content already exists.");
        }

        File.Move(tempPath, finalPath);
        return StoredArtifactResult.Ready(size, hash, timeProvider.GetUtcNow());
    }

    private static string CleanSegment(string value) =>
        new(value.Where(character => char.IsLetterOrDigit(character) || character is '_' or '-').ToArray());

    private string SafeAbsolutePath(string relativePath)
    {
        var root = Path.GetFullPath(_options.RootPath);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Artifact storage path escaped the configured root.");
        }

        return path;
    }
}

public sealed record StoredArtifactResult(
    bool IsReady,
    long? SizeBytes,
    string? Sha256,
    DateTimeOffset? UploadedAt,
    string? FailureCode,
    string? FailureMessage)
{
    public static StoredArtifactResult Ready(long sizeBytes, string sha256, DateTimeOffset uploadedAt) =>
        new(true, sizeBytes, sha256, uploadedAt, null, null);

    public static StoredArtifactResult Failed(string code, string message) =>
        new(false, null, null, null, code, message);
}
