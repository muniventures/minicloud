// SPDX-License-Identifier: MPL-2.0
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minicloud.Cli.Seed;

internal static class SeedLocalSource
{
    internal const string Trust = "operator-pinned-local-source";

    internal static SeedVerifiedRelease Verify(string archive, string expectedDigest, string tag, string directory)
    {
        SeedFiles.Check(archive);
        SeedGuard.Require(Regex.IsMatch(expectedDigest, "^[0-9a-f]{64}$")
            && new FileInfo(archive).Length is > 0 and <= 256 * 1024 * 1024,
            "Local source requires an explicit SHA256 and a bounded protected archive.");
        SeedFiles.Directory(directory);
        var copy = Path.Combine(directory, "source.tar.gz");
        using (var input = File.OpenRead(archive))
        using (var output = SeedFiles.Create(copy)) input.CopyTo(output);
        using (var input = File.OpenRead(copy))
            SeedGuard.Require(Convert.ToHexStringLower(SHA256.HashData(input)) == expectedDigest,
                "Local source archive differs from the operator-approved SHA256.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        byte[]? descriptor = null, runner = null;
        long expanded = 0;
        using var file = File.OpenRead(copy);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            var name = entry.Name.TrimEnd('/');
            expanded += entry.Length;
            SeedGuard.Require(SeedRelease.SafeMember(name) && seen.Add(name)
                && !name.Split('/').Any(x => x is ".git" or ".local")
                && entry.EntryType is TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile
                && entry.Length is >= 0 and <= 64 * 1024 * 1024 && expanded <= 512L * 1024 * 1024
                && seen.Count <= 20000, "Unsafe or oversized local source archive.");
            if (name is not ("minicloud-local-source.json" or "scripts/self-hosting/local_source.py")) continue;
            SeedGuard.Require(entry.EntryType != TarEntryType.Directory && entry.Length <= 1048576,
                "Invalid local source descriptor/runner.");
            using var bytes = new MemoryStream();
            entry.DataStream!.CopyTo(bytes);
            if (name == "minicloud-local-source.json") descriptor = bytes.ToArray();
            else runner = bytes.ToArray();
        }
        SeedGuard.Require(descriptor is not null && runner is not null
            && new[] { "modules/api/Dockerfile", "modules/dashboard/Dockerfile", "modules/worker/Dockerfile",
                "packaging/server/bundle/server_install.py", "packaging/server/release.json" }.All(seen.Contains),
            "Incomplete local source archive.");
        using var metadata = SeedPortable.ReadDocument(System.Text.Encoding.UTF8.GetString(descriptor));
        var root = metadata.RootElement;
        SeedGuard.Require(root.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(
            ["schemaVersion", "kind", "sourceRevision", "serverVersion", "cliCompatibility"])
            && root.GetProperty("schemaVersion").GetInt32() == 1
            && root.GetProperty("kind").GetString() == Trust
            && "server-v" + root.GetProperty("serverVersion").GetString() == tag
            && Regex.IsMatch(root.GetProperty("sourceRevision").GetString() ?? "", "^[0-9a-f]{40}$"),
            "Local source identity/version differs from the selected deployment.");
        SeedRelease.RequireCompatible(root.GetProperty("cliCompatibility"));
        SeedFiles.WriteNew(Path.Combine(directory, "local-source.py"), runner);
        return new(expectedDigest, root.GetProperty("sourceRevision").GetString()!);
    }
}
