// SPDX-License-Identifier: MPL-2.0
using System.Text.RegularExpressions;
using Minicloud.Common.Contracts;

namespace Minicloud.Common.Validation;

public static partial class StorageValidation
{
    public const int MaxVolumes = 16;
    public const int MaxSizeGb = 10000;

    public static ValidationResult Validate(IEnumerable<(string Name, IReadOnlyDictionary<string, StorageMountRequest>? Storage, IEnumerable<string> AssetPaths)> services, bool sqlite = false)
    {
        var result = new ValidationResult();
        var volumes = new Dictionary<string, StorageMountRequest>(StringComparer.Ordinal);
        foreach (var (service, storage, assetPaths) in services)
        {
            var paths = assetPaths.ToList();
            if (sqlite) { paths.Add("/data/sqlite"); }
            foreach (var (name, mount) in storage ?? new Dictionary<string, StorageMountRequest>())
            {
                var field = $"services.{service}.storage.{name}";
                if (!NamePattern().IsMatch(name)) { result.Add(field, "Storage name must use 1–32 lowercase letters, numbers or dashes, starting with a letter or number."); }
                if (mount is null) { result.Add(field, "Storage mount is required."); continue; }
                if (mount.SizeGb is < 10 or > MaxSizeGb) { result.Add(field + ".sizeGb", "Size must be from 10 to 10000 GB."); }
                if (!IsMountPath(mount.MountPath)) { result.Add(field + ".mountPath", "Use an absolute, normalized container path outside reserved system directories."); }
                if (mount.Uid < 0 || mount.Gid < 0) { result.Add(field, "UID and GID must be nonnegative integers."); }
                if (paths.Any(p => Overlaps(p, mount.MountPath))) { result.Add(field + ".mountPath", "Storage and asset mounts must not overlap."); }
                paths.Add(mount.MountPath);
                if (volumes.TryGetValue(name, out var previous) && (previous.SizeGb != mount.SizeGb || previous.Uid != mount.Uid || previous.Gid != mount.Gid))
                { result.Add(field, "Shared storage must use the same size, UID and GID across services."); }
                volumes[name] = mount;
            }
        }
        if (volumes.Count > MaxVolumes || volumes.Values.Sum(x => (long)x.SizeGb) > MaxSizeGb)
        { result.Add("storage", "An app supports at most 16 volumes and 10000 GB total."); }
        return result;
    }

    public static bool IsMountPath(string? path) => path is not null && PathPattern().IsMatch(path)
        && !path.Split('/').Any(x => x is "." or "..")
        && !new[] { "/dev", "/proc", "/sys", "/etc", "/run", "/var/run" }.Any(p => Overlaps(p, path));

    private static bool Overlaps(string? a, string? b) => a is not null && b is not null &&
        (a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal));

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex NamePattern();
    [GeneratedRegex("^/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+$")]
    private static partial Regex PathPattern();
}
