// SPDX-License-Identifier: MPL-2.0

using System.IO.Compression;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;

namespace Minicloud.Tests;

public sealed class DeploymentInputSessionTests
{
    [Theory]
    [InlineData("**foo", "somefoo", true)]
    [InlineData("**foo", "somefoobar", false)]
    [InlineData("**\n!projects/api/**", "projects/portal/index.ts", true)]
    [InlineData("**\n!projects/api/**", "projects/api/index.cs", false)]
    [InlineData("?.cs", "💻.cs", true)]
    [InlineData("[💻].cs", "💻.cs", true)]
    [InlineData("[^a].cs", "💻.cs", true)]
    [InlineData("[a-c].cs", "b.cs", true)]
    [InlineData("[a-c].cs", "d.cs", false)]
    [InlineData("[[:alpha:]].txt", "a.txt", true)]
    [InlineData("[[:alpha:]].txt", "1.txt", false)]
    [InlineData(@"[\d].txt", "a.txt", false)]
    [InlineData("*.md", "notes.md", true)]
    [InlineData("*.md", "docs/notes.md", false)]
    [InlineData("**/*.md", "docs/notes.md", true)]
    [InlineData("**/*.md", "notes.md", true)]
    [InlineData("docs", "docs/nested/a.txt", true)]
    [InlineData("docs\n!docs/keep.md", "docs/keep.md", false)]
    [InlineData("docs\n!docs/keep.md\ndocs/keep.md", "docs/keep.md", true)]
    [InlineData(" /docs/ ", "docs/a.txt", true)]
    [InlineData("a?.[ch]", "ab.c", true)]
    [InlineData("# comment\n.", "anything", false)]
    [InlineData("\\#file", "#file", true)]
    public void Docker_ignore_matches_ordered_context_rules(string rules, string path, bool excluded)
    {
        Assert.Equal(excluded, new DockerIgnoreMatcher(rules.Split('\n')).Excludes(path));
    }

    [Fact]
    public void Docker_ignore_backslash_rules_follow_host_path_semantics()
    {
        // Docker treats backslashes as path separators on Windows, escapes on Unix.
        var escaped = !OperatingSystem.IsWindows();
        Assert.Equal(escaped, new DockerIgnoreMatcher([@"\a.txt"]).Excludes("\a.txt"));
        Assert.Equal(escaped, new DockerIgnoreMatcher([@"[\d].txt"]).Excludes("1.txt"));
    }

    [Fact]
    public void Specific_ignore_snapshot_and_archive_use_identical_frozen_bytes()
    {
        var root = Directory.CreateTempSubdirectory("minicloud-snapshot-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Dockerfile"), "FROM scratch");
            File.WriteAllText(Path.Combine(root.FullName, ".dockerignore"), "keep.txt\n");
            File.WriteAllText(Path.Combine(root.FullName, "Dockerfile.dockerignore"), "omit.txt\n.env\n!.env\n");
            var source = Path.Combine(root.FullName, "keep.txt");
            File.WriteAllText(source, "before");
            File.WriteAllText(Path.Combine(root.FullName, "omit.txt"), "ignored");
            File.WriteAllText(Path.Combine(root.FullName, ".env"), "secret");
            var build = new Dictionary<string, string> { ["MODE"] = "before" };
            var config = new MinicloudServiceConfig(root.FullName, null, null, 8080, true, "/", "/health", BuildEnv: build, BuildMode: "source");
            using var session = new DeploymentInputSession();
            var snapshot = session.Get("api", config);
            var hash = ServiceChecksumCalculator.Compute("api", config, snapshot);
            var reads = session.SourceFilesRead;
            session.Get("worker", config);
            Assert.Equal(reads, session.SourceFilesRead);
            File.WriteAllText(source, "after!");
            build["MODE"] = "after";
            var output = Directory.CreateDirectory(Path.Combine(root.FullName, "output"));
            var bundle = DeploymentArtifactBundler.Create("app_test", "api", config, null, output.FullName, snapshot: snapshot);
            using var archive = ZipFile.OpenRead(bundle.ZipPath);
            Assert.Null(archive.GetEntry("omit.txt"));
            Assert.Null(archive.GetEntry(".env"));
            using var reader = new StreamReader(archive.GetEntry("keep.txt")!.Open());
            Assert.Equal("before", reader.ReadToEnd());
            Assert.Equal(hash, ServiceChecksumCalculator.Compute("api", config, snapshot));
            Assert.Equal(snapshot.Files.Count + 1, archive.Entries.Count);
            var replay = Path.Combine(root.FullName, "replay");
            ZipFile.ExtractToDirectory(bundle.ZipPath, replay);
            File.Delete(Path.Combine(replay, DeploymentArtifactBundler.ManifestEntryName));
            var replayConfig = config with { SourcePath = replay, Dockerfile = Path.Combine(replay, "Dockerfile"), BuildEnv = snapshot.Service.BuildEnv };
            Assert.Equal(hash, ServiceChecksumCalculator.Compute("api", replayConfig));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("!")]
    [InlineData("[a-]")]
    [InlineData("[")]
    [InlineData("[z-a]")]
    public void Invalid_ignore_patterns_fail_before_packaging(string pattern)
    {
        Assert.Throws<CliCommandException>(() => new DockerIgnoreMatcher([pattern]));
    }

    [Fact]
    public void Ignored_change_does_not_select_service_but_ignore_change_does()
    {
        var root = Directory.CreateTempSubdirectory("minicloud-input-ignore-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Dockerfile"), "FROM scratch");
            var ignore = Path.Combine(root.FullName, ".dockerignore");
            File.WriteAllText(ignore, "portal/**\n");
            Directory.CreateDirectory(Path.Combine(root.FullName, "portal"));
            var source = Path.Combine(root.FullName, "portal", "index.ts");
            File.WriteAllText(source, "v1");
            var config = new MinicloudServiceConfig(root.FullName, null, null, 8080, true, "/", "/", BuildMode: "source");
            var original = ServiceChecksumCalculator.Compute("worker", config);
            File.WriteAllText(source, "v2");
            Assert.Equal(original, ServiceChecksumCalculator.Compute("worker", config));
            File.WriteAllText(ignore, "# include portal now\n");
            Assert.NotEqual(original, ServiceChecksumCalculator.Compute("worker", config));
        }
        finally { root.Delete(true); }
    }
}
