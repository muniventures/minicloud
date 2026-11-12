// SPDX-License-Identifier: MPL-2.0
using System.Formats.Tar;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Minicloud.Cli.Commands;

namespace Minicloud.Cli.Seed;

internal sealed record SeedVerifiedRelease(string Digest, string Source);
internal sealed class SeedRelease
{
    internal static string[] Names(string tag) => ["server-manifest.json", $"minicloud-server-{tag[8..]}.tar.gz",
        "install-server.sh", "verify-server-release.py", "checksums.txt", "checksums.txt.sigstore.json"];

    public async Task<SeedVerifiedRelease> VerifyAsync(string tag, string directory, CancellationToken ct)
    {
        SeedFiles.Directory(directory);
        foreach (var name in Names(tag))
            await DownloadAsync(new Uri($"https://github.com/muniventures/minicloud/releases/download/{tag}/{name}"),
                Path.Combine(directory, name), name.EndsWith(".tar.gz") ? 2L * 1024 * 1024 * 1024 : 8 * 1024 * 1024, ct);
        var cosign = await SeedProcess.RequireAsync("cosign", ["version", "--json"], null, ct);
        SeedGuard.Require(cosign.Contains("v2.6.5", StringComparison.Ordinal), "Seed requires independently installed Cosign 2.6.5.");
        await SeedProcess.RequireAsync("cosign", ["verify-blob", "--new-bundle-format", "--bundle",
            Path.Combine(directory, "checksums.txt.sigstore.json"), "--certificate-identity",
            $"https://github.com/muniventures/minicloud/.github/workflows/server-release.yml@refs/tags/{tag}",
            "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com", Path.Combine(directory, "checksums.txt")], null, ct);
        VerifyInventory(directory, Names(tag).Take(4).ToHashSet(StringComparer.Ordinal));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "server-manifest.json")));
        var root = manifest.RootElement;
        SeedGuard.Require(root.GetProperty("schemaVersion").GetInt32() == 1
            && root.GetProperty("configurationSchemaVersion").GetInt32() == 1 && root.GetProperty("identitySchemaVersion").GetInt32() == 1
            && root.GetProperty("releaseTag").GetString() == tag && root.GetProperty("serverVersion").GetString() == tag[8..]
            && Regex.IsMatch(root.GetProperty("sourceRevision").GetString()!, "^[0-9a-f]{40}$")
            && root.GetProperty("architectures").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["linux/amd64", "linux/arm64"]),
            "Signed server release has unsupported schema, architecture or identity.");
        RequireCompatible(root.GetProperty("cliCompatibility"));
        var bundle = root.GetProperty("bundle");
        SeedGuard.Require(bundle.GetProperty("name").GetString() == Names(tag)[1], "Signed bundle belongs to a different release.");
        using (var stream = File.OpenRead(Path.Combine(directory, Names(tag)[1])))
            SeedGuard.Require(Convert.ToHexStringLower(SHA256.HashData(stream)) == bundle.GetProperty("sha256").GetString(), "Signed bundle digest mismatch.");
        VerifyArchive(directory, root);
        return new(SeedGuard.Hash(root.GetRawText()), root.GetProperty("sourceRevision").GetString()!);
    }

    internal static void VerifyInventory(string directory, ISet<string> expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(directory, "checksums.txt")))
        {
            var match = Regex.Match(line, @"^([0-9a-f]{64})  ([a-zA-Z0-9._-]+)$");
            SeedGuard.Require(match.Success && expected.Contains(match.Groups[2].Value) && seen.Add(match.Groups[2].Value), "Invalid/duplicate signed release inventory.");
            var path = Path.Combine(directory, match.Groups[2].Value);
            SeedFiles.Check(path);
            using var stream = File.OpenRead(path);
            SeedGuard.Require(Convert.ToHexStringLower(SHA256.HashData(stream)) == match.Groups[1].Value, "Signed release asset checksum mismatch.");
        }
        SeedGuard.Require(seen.SetEquals(expected), "Signed release inventory is incomplete.");
    }

    private static void VerifyArchive(string directory, JsonElement manifest)
    {
        var expected = manifest.GetProperty("files").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.Ordinal);
        SeedGuard.Require(expected.Count is > 0 and <= 10000, "Server archive inventory exceeds safety bounds.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long expanded = 0;
        using var file = File.OpenRead(Path.Combine(directory, manifest.GetProperty("bundle").GetProperty("name").GetString()!));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            expanded += entry.Length;
            SeedGuard.Require(entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                && SafeMember(entry.Name) && seen.Add(entry.Name) && expected.ContainsKey(entry.Name)
                && entry.Length is >= 0 and <= 64 * 1024 * 1024 && expanded <= 512L * 1024 * 1024,
                "Unsafe, duplicate or unlisted server archive member.");
            using var data = new MemoryStream();
            entry.DataStream!.CopyTo(data);
            var bytes = data.ToArray();
            SeedGuard.Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == expected[entry.Name], "Signed server archive member digest mismatch.");
            if (entry.Name == "server-release.json")
            {
                using var core = JsonDocument.Parse(bytes);
                var fields = manifest.EnumerateObject().Where(x => x.Name is not ("files" or "bundle"));
                SeedGuard.Require(core.RootElement.EnumerateObject().Count() == fields.Count()
                    && fields.All(x => core.RootElement.TryGetProperty(x.Name, out var value) && JsonElement.DeepEquals(x.Value, value)),
                    "Runtime manifest differs from signed release.");
            }
        }
        SeedGuard.Require(seen.SetEquals(expected.Keys) && seen.Contains("server-release.json") && seen.Contains("server_install.py"), "Incomplete signed archive.");
    }

    internal static bool SafeMember(string name) => !string.IsNullOrEmpty(name) && !name.Contains('\\') && !name.Contains(':')
        && name.Split('/').All(x => x is not ("" or "." or "..") && x.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));

    internal static void RequireCompatible(JsonElement compatibility)
    {
        SeedGuard.Require(compatibility.GetProperty("protocolVersion").GetInt32() == 1, "Unsupported server CLI protocol.");
        var informational = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var revision = Regex.Match(informational, @"(?:sha\.|\+)([0-9a-f]{40})$").Groups[1].Value;
        var sources = compatibility.GetProperty("sourceRevisions").EnumerateArray().Select(x => x.GetString()).ToArray();
        var minimum = compatibility.GetProperty("minimumVersion");
        var maximum = compatibility.GetProperty("maximumExclusiveVersion");
        SeedGuard.Require(minimum.ValueKind == maximum.ValueKind && (minimum.ValueKind is JsonValueKind.Null or JsonValueKind.String)
            && sources.All(x => x is not null && Regex.IsMatch(x, "^[0-9a-f]{40}$")) && sources.Distinct().Count() == sources.Length,
            "Invalid signed compatibility declaration.");
        var range = minimum.ValueKind == JsonValueKind.String;
        SeedGuard.Require(!range || Compare(minimum.GetString()!, maximum.GetString()!) < 0, "Invalid CLI compatibility interval.");
        SeedGuard.Require((revision.Length == 40 && sources.Contains(revision)) ||
            (range && Compare(CliApplication.GetVersion(), minimum.GetString()!) >= 0 && Compare(CliApplication.GetVersion(), maximum.GetString()!) < 0),
            "This CLI version/source is not allowed by the signed server release. Select a qualified compatible CLI; no source-build/server fallback.");
    }

    private static int Compare(string a, string b)
    {
        var pattern = @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*))?$";
        SeedGuard.Require(Regex.IsMatch(a, pattern) && Regex.IsMatch(b, pattern), "Invalid compatibility semantic version.");
        var aa = a.Split('-', 2); var bb = b.Split('-', 2);
        var ac = aa[0].Split('.'); var bc = bb[0].Split('.');
        for (var i = 0; i < 3; i++)
        {
            var diff = BigInteger.Parse(ac[i]).CompareTo(BigInteger.Parse(bc[i]));
            if (diff != 0) return diff;
        }
        if (aa.Length != bb.Length) return aa.Length == 1 ? 1 : -1;
        if (aa.Length == 1) return 0;
        var ap = aa[1].Split('.'); var bp = bb[1].Split('.');
        for (var i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            var an = ap[i].All(char.IsAsciiDigit); var bn = bp[i].All(char.IsAsciiDigit);
            SeedGuard.Require((!an || ap[i].Length == 1 || ap[i][0] != '0') && (!bn || bp[i].Length == 1 || bp[i][0] != '0'), "Invalid prerelease numeric identifier.");
            var diff = an && bn ? BigInteger.Parse(ap[i]).CompareTo(BigInteger.Parse(bp[i]))
                : an != bn ? an ? -1 : 1 : string.CompareOrdinal(ap[i], bp[i]);
            if (diff != 0) return diff;
        }
        return ap.Length.CompareTo(bp.Length);
    }

    internal static async Task DownloadAsync(Uri uri, string target, long limit, CancellationToken ct)
    {
        if (File.Exists(target)) { SeedFiles.Check(target); return; }
        var partial = target + ".partial";
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        using var output = SeedFiles.Create(partial);
        for (var count = 0; count <= 8; count++)
        {
            SeedGuard.Require(uri.Scheme == "https" && uri.UserInfo.Length == 0, "Release redirect is not public HTTPS.");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { uri = new Uri(uri, location); continue; }
            SeedGuard.Require(response.IsSuccessStatusCode, "Exact release asset is absent/unavailable. No server release is currently qualified; no resources were created by release verification.");
            using var input = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[65536]; long total = 0; int size;
            while ((size = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += size;
                SeedGuard.Require(total <= limit, "Release asset exceeds its safety bound.");
                await output.WriteAsync(buffer.AsMemory(0, size), ct);
            }
            output.Flush(true);
            output.Dispose();
            File.Move(partial, target, false);
            return;
        }
        throw new Commands.CliCommandException(CliExitCodes.ValidationError, "Too many release redirects; partial download retained.");
    }
}
