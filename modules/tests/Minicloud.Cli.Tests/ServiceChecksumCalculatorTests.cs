using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;

namespace Minicloud.Tests;

public sealed class ServiceChecksumCalculatorTests
{
    [Fact]
    public void Compute_is_deterministic_for_identical_files_and_config()
    {
        var tempDir = Directory.CreateTempSubdirectory("minicloud-checksum-test-");
        try
        {
            var srcDir = Path.Combine(tempDir.FullName, "src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "index.js"), "console.log('hello');");
            File.WriteAllText(Path.Combine(srcDir, "Dockerfile"), "FROM node:20\nCMD [\"node\", \"index.js\"]");

            var config1 = new MinicloudServiceConfig(
                SourcePath: srcDir,
                Dockerfile: "Dockerfile",
                Image: null,
                Port: 3000,
                Public: true,
                Path: "/",
                HealthPath: "/health",
                Env: new Dictionary<string, string> { ["NODE_ENV"] = "production" });

            var config2 = new MinicloudServiceConfig(
                SourcePath: srcDir,
                Dockerfile: "Dockerfile",
                Image: null,
                Port: 3000,
                Public: true,
                Path: "/",
                HealthPath: "/health",
                Env: new Dictionary<string, string> { ["NODE_ENV"] = "production" });

            var hash1 = ServiceChecksumCalculator.Compute("web", config1);
            var hash2 = ServiceChecksumCalculator.Compute("web", config2);

            Assert.Equal(hash1, hash2);
            Assert.Equal(64, hash1.Length);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void Compute_changes_when_source_code_changes()
    {
        var tempDir = Directory.CreateTempSubdirectory("minicloud-checksum-test-");
        try
        {
            var srcDir = Path.Combine(tempDir.FullName, "src");
            Directory.CreateDirectory(srcDir);
            var indexFile = Path.Combine(srcDir, "index.js");
            File.WriteAllText(indexFile, "console.log('version 1');");
            File.WriteAllText(Path.Combine(srcDir, "Dockerfile"), "FROM node:20\nCMD [\"node\", \"index.js\"]");

            var config = new MinicloudServiceConfig(
                SourcePath: srcDir,
                Dockerfile: "Dockerfile",
                Image: null,
                Port: 3000,
                Public: true,
                Path: "/",
                HealthPath: "/health");

            var hash1 = ServiceChecksumCalculator.Compute("web", config);

            File.WriteAllText(indexFile, "console.log('version 2');");
            var hash2 = ServiceChecksumCalculator.Compute("web", config);

            Assert.NotEqual(hash1, hash2);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void Compute_changes_when_config_changes()
    {
        var tempDir = Directory.CreateTempSubdirectory("minicloud-checksum-test-");
        try
        {
            var srcDir = Path.Combine(tempDir.FullName, "src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "index.js"), "console.log('hello');");
            File.WriteAllText(Path.Combine(srcDir, "Dockerfile"), "FROM node:20\nCMD [\"node\", \"index.js\"]");

            var config1 = new MinicloudServiceConfig(
                SourcePath: srcDir,
                Dockerfile: "Dockerfile",
                Image: null,
                Port: 3000,
                Public: true,
                Path: "/",
                HealthPath: "/health");

            var config2 = config1 with { Port = 8080 };
            var config3 = config1 with { Env = new Dictionary<string, string> { ["NEW_VAR"] = "val" } };

            var hash1 = ServiceChecksumCalculator.Compute("web", config1);
            var hash2 = ServiceChecksumCalculator.Compute("web", config2);
            var hash3 = ServiceChecksumCalculator.Compute("web", config3);

            Assert.NotEqual(hash1, hash2);
            Assert.NotEqual(hash1, hash3);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void Compute_ignores_gitignored_and_excluded_files()
    {
        var tempDir = Directory.CreateTempSubdirectory("minicloud-checksum-test-");
        try
        {
            var srcDir = Path.Combine(tempDir.FullName, "src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "index.js"), "console.log('hello');");
            File.WriteAllText(Path.Combine(srcDir, "Dockerfile"), "FROM node:20\nCMD [\"node\", \"index.js\"]");
            File.WriteAllText(Path.Combine(srcDir, ".gitignore"), "temp.txt\n");

            var config = new MinicloudServiceConfig(
                SourcePath: srcDir,
                Dockerfile: "Dockerfile",
                Image: null,
                Port: 3000,
                Public: true,
                Path: "/",
                HealthPath: "/health");

            var hash1 = ServiceChecksumCalculator.Compute("web", config);

            // Add gitignored file
            File.WriteAllText(Path.Combine(srcDir, "temp.txt"), "ignored contents");
            // Add forced excluded directory (e.g. node_modules)
            var nodeModules = Directory.CreateDirectory(Path.Combine(srcDir, "node_modules"));
            File.WriteAllText(Path.Combine(nodeModules.FullName, "pkg.js"), "package code");

            var hash2 = ServiceChecksumCalculator.Compute("web", config);

            Assert.Equal(hash1, hash2);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void Compute_hashes_image_backed_services()
    {
        var config1 = new MinicloudServiceConfig(
            SourcePath: null,
            Dockerfile: null,
            Image: "redis:7-alpine",
            Port: 6379,
            Public: false,
            Path: "/",
            HealthPath: "/");

        var config2 = config1 with { Image = "redis:8-alpine" };

        var hash1 = ServiceChecksumCalculator.Compute("cache", config1);
        var hash2 = ServiceChecksumCalculator.Compute("cache", config2);

        Assert.Equal(64, hash1.Length);
        Assert.NotEqual(hash1, hash2);
    }
}
