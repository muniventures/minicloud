// SPDX-License-Identifier: MPL-2.0
using Minicloud.Common.Contracts;
using YamlDotNet.RepresentationModel;

namespace Minicloud.Cli.Config;

public static partial class MinicloudConfigLoader
{
    private static IReadOnlyDictionary<string, StorageMountRequest>? ParseStorage(YamlMappingNode service, string name, List<ConfigDiagnostic> diagnostics)
    {
        if (!TryGetNode(service, "storage", out var node)) { return null; }
        var result = new Dictionary<string, StorageMountRequest>(StringComparer.Ordinal);
        if (node is not YamlMappingNode mounts) { diagnostics.Add(new($"services.{name}.storage", "Storage must be a mapping.")); return result; }
        foreach (var (key, value) in mounts.Children)
        {
            var volume = ScalarKey(key, "storage", diagnostics);
            if (volume is null) { continue; }
            var field = $"services.{name}.storage.{volume}";
            if (value is not YamlMappingNode mount) { diagnostics.Add(new(field, "Storage mount must be a mapping.")); continue; }
            int Number(string key, int fallback)
            {
                if (!TryGetNode(mount, key, out var n)) { return fallback; }
                if (n is YamlScalarNode s && int.TryParse(s.Value, out var i)) { return i; }
                diagnostics.Add(new(field + "." + key, "Value must be an integer.")); return fallback;
            }
            foreach (var entry in mount.Children.Keys)
            {
                if ((entry as YamlScalarNode)?.Value is not ("sizeGb" or "mountPath" or "uid" or "gid"))
                { diagnostics.Add(new(field, "Supported fields: sizeGb, mountPath, uid, gid.")); }
            }
            result.Add(volume, new(Number("sizeGb", 0), GetScalar(mount, "mountPath", field + ".mountPath", diagnostics) ?? "", Number("uid", 0), Number("gid", 0)));
        }
        return result;
    }
}
