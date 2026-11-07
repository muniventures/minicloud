// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

internal sealed class DeploymentInputSession : IDisposable
{
    private const long MaxSnapshotBytes = 2L * 1024 * 1024 * 1024;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "minicloud-inputs", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, (string Path, string Digest)> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceInputSnapshot> _services = new(StringComparer.Ordinal);
    private long _bytes;
    private long _capturedBytes;
    private int _entries;
    public int SourceFilesRead => _files.Count;
    public long SnapshotBytes => _bytes;
    public long CapturedBytes => _capturedBytes;

    public ServiceInputSnapshot Get(string name, MinicloudServiceConfig service)
    {
        if (_services.TryGetValue(name, out var existing)) { return existing; }
        service = service with
        {
            Env = service.Env?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            SecretEnv = service.SecretEnv?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            BuildEnv = service.BuildEnv?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            AssetMounts = service.AssetMounts?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
        };
        if (string.IsNullOrWhiteSpace(service.SourcePath))
        {
            return _services[name] = new(service, []);
        }
        var sourceRoot = Path.GetFullPath(service.SourcePath);
        if (!Directory.Exists(sourceRoot)) { throw new CliCommandException(CliExitCodes.ValidationError, $"Source directory for service '{name}' does not exist."); }
        var configuredDockerfile = CliApplication.EffectiveDockerfilePath(service);
        var dockerfile = Path.GetFullPath(configuredDockerfile);
        if (!File.Exists(dockerfile) && !Path.IsPathRooted(configuredDockerfile))
        {
            dockerfile = Path.GetFullPath(Path.Combine(sourceRoot, configuredDockerfile));
        }
        if (!File.Exists(dockerfile))
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"Dockerfile not found for service '{name}'.");
        }
        var destination = Path.Combine(_directory, "services", Guid.NewGuid().ToString("N"));
        var files = new List<SnapshotFile>();
        var selected = DeploymentArtifactBundler.SelectFiles(sourceRoot, dockerfile).ToList();
        var ignore = DeploymentArtifactBundler.EffectiveIgnoreFile(sourceRoot, dockerfile);
        DeploymentArtifactBundler.ValidateMetadata(sourceRoot, dockerfile);
        if (ignore is not null) { DeploymentArtifactBundler.ValidateMetadata(sourceRoot, ignore); }
        if (!selected.Contains(dockerfile, StringComparer.Ordinal)) { selected.Add(dockerfile); }
        if (ignore is not null && !selected.Contains(ignore, StringComparer.Ordinal)) { selected.Add(ignore); }
        string? frozenDockerfile = null;
        foreach (var source in selected.Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(sourceRoot, source).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal))
            {
                relative = source == dockerfile ? "minicloud-artifact/Dockerfile" : "minicloud-artifact/Dockerfile.dockerignore";
            }
            if (!_files.TryGetValue(source, out var frozen))
            {
                var parent = new FileInfo(source).Directory;
                while (parent is not null && (parent.FullName == sourceRoot || parent.FullName.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                {
                    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new CliCommandException(CliExitCodes.ValidationError, "Symlinked source directories are not supported.");
                    }
                    if (parent.FullName == sourceRoot) { break; }
                    parent = parent.Parent;
                }
                PrivateDirectory(_directory);
                var contentPath = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(contentPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (_files.Count >= 100000) { throw new CliCommandException(CliExitCodes.ValidationError, "Deployment snapshot exceeds 100000 files."); }
                    var buffer = new byte[81920];
                    int count;
                    while ((count = input.Read(buffer)) > 0)
                    {
                        if (_bytes + count > MaxSnapshotBytes) { throw new CliCommandException(CliExitCodes.ValidationError, "Deployment snapshot exceeds 2 GiB."); }
                        output.Write(buffer.AsSpan(0, count));
                        _bytes += count;
                        _capturedBytes += count;
                    }
                }
                using var staged = File.OpenRead(contentPath);
                var digest = Convert.ToHexString(SHA256.HashData(staged));
                using var verification = File.OpenRead(source);
                if (Convert.ToHexString(SHA256.HashData(verification)) != digest)
                {
                    throw new CliCommandException(CliExitCodes.ValidationError, "Source changed while preparing the deployment snapshot; retry deployment.");
                }
                if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(contentPath, File.GetUnixFileMode(source)); }
                frozen = (contentPath, digest);
                _files.Add(source, frozen);
            }
            var target = Path.Combine(destination, relative);
            PrivateDirectory(Path.GetDirectoryName(target)!);
            var copyBytes = new FileInfo(frozen.Path).Length;
            if (_bytes + copyBytes > MaxSnapshotBytes || ++_entries > 100000)
            { throw new CliCommandException(CliExitCodes.ValidationError, "Deployment snapshot exceeds its 2 GiB or 100000 selected-file limits."); }
            _bytes += copyBytes;
            File.Copy(frozen.Path, target);
            files.Add(new(relative, frozen.Digest, OperatingSystem.IsWindows() ? 0 : (int)(File.GetUnixFileMode(target) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute))));
            if (source == dockerfile) { frozenDockerfile = target; }
        }
        return _services[name] = new(service with { SourcePath = destination, Dockerfile = frozenDockerfile }, files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray());
    }

    private static void PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
    }
}

internal sealed record SnapshotFile(string RelativePath, string Digest, int ExecuteMode);
internal sealed record ServiceInputSnapshot(MinicloudServiceConfig Service, IReadOnlyList<SnapshotFile> Files);
