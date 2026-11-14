// SPDX-License-Identifier: MPL-2.0
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Seed;

namespace Minicloud.Tests;

public sealed class SeedLocalSourceTests : IDisposable
{
    private readonly string home = Path.GetFullPath(Path.Combine(".local", "artifacts", "local-source-" + Guid.NewGuid().ToString("N")));
    public void Dispose() { if (Directory.Exists(home)) Directory.Delete(home, true); }

    private (string Path, string Digest) Archive(string? extra = null, bool duplicate = false, string version = "1.2.3-local.1")
    {
        SeedFiles.Directory(home);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
        using (var tar = new TarWriter(gzip))
        {
            var descriptor = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, kind = SeedLocalSource.Trust, sourceRevision = new string('a', 40), serverVersion = version,
                cliCompatibility = new { protocolVersion = 1, minimumVersion = "0.0.0", maximumExclusiveVersion = "999.0.0", sourceRevisions = Array.Empty<string>() }
            });
            Write("minicloud-local-source.json", descriptor);
            foreach (var name in new[] { "modules/api/Dockerfile", "modules/dashboard/Dockerfile", "modules/worker/Dockerfile",
                "packaging/server/bundle/server_install.py", "packaging/server/release.json", "scripts/self-hosting/local_source.py" })
                Write(name, "synthetic");
            if (extra is not null) Write(extra, "untrusted");
            if (duplicate) Write("minicloud-local-source.json", descriptor);
            void Write(string name, string text)
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
            }
        }
        var path = System.IO.Path.Combine(home, "source.tar.gz");
        var bytes = buffer.ToArray();
        SeedFiles.WriteNew(path, bytes);
        return (path, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Explicit_local_archive_is_copied_and_verified_without_network_or_cosign()
    {
        var archive = Archive();
        var release = SeedLocalSource.Verify(archive.Path, archive.Digest, "server-v1.2.3-local.1", Path.Combine(home, "verified"));
        Assert.Equal(archive.Digest, release.Digest);
        Assert.Equal(new string('a', 40), release.Source);
        Assert.Equal(File.ReadAllBytes(archive.Path), File.ReadAllBytes(Path.Combine(home, "verified", "source.tar.gz")));
        Assert.Equal("synthetic", File.ReadAllText(Path.Combine(home, "verified", "local-source.py")));
    }

    [Theory]
    [InlineData("../outside", false)]
    [InlineData(".git/config", false)]
    [InlineData(".local/artifacts/secret", false)]
    [InlineData(null, true)]
    public void Unsafe_or_duplicate_source_is_never_executed(string? extra, bool duplicate)
    {
        var archive = Archive(extra, duplicate);
        Assert.Throws<CliCommandException>(() => SeedLocalSource.Verify(archive.Path, archive.Digest,
            "server-v1.2.3-local.1", Path.Combine(home, "verified")));
        Assert.False(File.Exists(Path.Combine(home, "verified", "local-source.py")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wrong_hash_or_version_fails_before_runner_promotion(bool wrongHash)
    {
        var archive = Archive();
        Assert.Throws<CliCommandException>(() => SeedLocalSource.Verify(archive.Path,
            wrongHash ? new('b', 64) : archive.Digest, wrongHash ? "server-v1.2.3-local.1" : "server-v1.2.3",
            Path.Combine(home, "verified")));
        Assert.False(File.Exists(Path.Combine(home, "verified", "local-source.py")));
    }
}
