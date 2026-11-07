// SPDX-License-Identifier: MPL-2.0

using Minicloud.Common.Contracts;
using Minicloud.Common.Validation;
using YamlDotNet.RepresentationModel;

namespace Minicloud.Cli.Config;

public static partial class MinicloudConfigLoader
{
    private static IReadOnlyDictionary<string, MinicloudAssetConfig> ParseAssets(YamlMappingNode root, List<ConfigDiagnostic> diagnostics)
    {
        var result = new Dictionary<string, MinicloudAssetConfig>(StringComparer.Ordinal);
        if (!TryGetNode(root, "assets", out var node) || node is not YamlMappingNode assets) { return result; }
        foreach (var (key, value) in assets.Children)
        {
            var name = ScalarKey(key, "assets", diagnostics);
            if (name is null) { continue; }
            var field = "assets." + name;
            if (!AssetValidation.SafeName(name) || value is not YamlMappingNode mapping)
            { diagnostics.Add(new(field, "Asset must have a valid name and mapping.")); continue; }
            foreach (var input in mapping.Children.Keys.OfType<YamlScalarNode>())
            {
                if (input.Value is not ("sourcePath" or "dockerfile" or "outputPath")) { diagnostics.Add(new(field, "Unknown asset field.")); }
            }
            var source = GetScalar(mapping, "sourcePath", field + ".sourcePath", diagnostics);
            var dockerfile = GetScalar(mapping, "dockerfile", field + ".dockerfile", diagnostics);
            var output = GetScalar(mapping, "outputPath", field + ".outputPath", diagnostics);
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dockerfile) || output is null || !AssetValidation.SafeMountPath(output))
            { diagnostics.Add(new(field, "Asset requires sourcePath, dockerfile and a safe absolute outputPath.")); continue; }
            result.Add(name, new(source, dockerfile, output));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, AssetMountRequest>? ParseAssetMounts(YamlMappingNode root, string service, List<ConfigDiagnostic> diagnostics)
    {
        if (!TryGetNode(root, "assetMounts", out var node)) { return null; }
        var result = new Dictionary<string, AssetMountRequest>(StringComparer.Ordinal);
        if (node is not YamlMappingNode mounts) { diagnostics.Add(new($"services.{service}.assetMounts", "Asset mounts must be a mapping.")); return result; }
        foreach (var (key, value) in mounts.Children)
        {
            var name = ScalarKey(key, "assetMounts", diagnostics);
            if (name is null) { continue; }
            var field = $"services.{service}.assetMounts.{name}";
            if (!AssetValidation.SafeName(name) || value is not YamlMappingNode mapping)
            { diagnostics.Add(new(field, "Invalid asset mount.")); continue; }
            foreach (var input in mapping.Children.Keys.OfType<YamlScalarNode>())
            {
                if (input.Value is not ("releaseId" or "mountPath" or "protocolVersion")) { diagnostics.Add(new(field, "Unknown asset mount field.")); }
            }
            var release = GetScalar(mapping, "releaseId", field + ".releaseId", diagnostics);
            var path = GetScalar(mapping, "mountPath", field + ".mountPath", diagnostics);
            var protocol = GetInt(mapping, "protocolVersion", field + ".protocolVersion", diagnostics) ?? 1;
            if ((release is not null && string.IsNullOrWhiteSpace(release)) || path is null || !AssetValidation.SafeMountPath(path) || protocol != 1)
            { diagnostics.Add(new(field, "Mount requires a safe absolute mountPath and supported protocolVersion (1); releaseId is optional.")); continue; }
            result.Add(name, new(release, path, protocol));
        }
        return result;
    }
}
