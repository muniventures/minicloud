using System.Diagnostics;
using Minicloud.Worker.Pipeline.Steps;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class CloudInitPackageBootstrapTests
{
    [Theory]
    [InlineData("debian")]
    [InlineData("ubuntu")]
    public async Task GeneratedPackageSetupUsesTheDistributionPackageSource(string distribution)
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await RunPackageSetupAsync(distribution);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("completed", result.Calls);
        Assert.Contains("e2fsprogs|util-linux", result.Calls);
        if (distribution == "debian")
        {
            Assert.Contains("apt-get|install|-y|docker-ce|docker-ce-cli|containerd.io|docker-buildx-plugin|docker-compose-plugin", result.Calls);
            Assert.DoesNotContain("|docker.io|", result.Calls);
            Assert.DoesNotContain("|docker-compose-v2", result.Calls);
            Assert.Contains("https://download.docker.com/linux/debian/gpg", result.Calls);
            Assert.Contains("URIs: https://download.docker.com/linux/debian\nSuites: bookworm\nComponents: stable\nArchitectures: amd64\nSigned-By:", result.Repository);
            Assert.Contains("/keyrings/docker.asc", result.Repository);
            Assert.True(result.KeyExists);
        }
        else
        {
            Assert.Contains("apt-get|install|-y|python3|ca-certificates|curl|gnupg|jq|ufw|caddy|docker.io|docker-compose-v2|docker-buildx|e2fsprogs|util-linux", result.Calls);
            Assert.DoesNotContain("docker-ce", result.Calls);
            Assert.Null(result.Repository);
            Assert.False(result.KeyExists);
        }
    }

    [Theory]
    [InlineData("common", 43)]
    [InlineData("docker", 42)]
    public async Task GeneratedDebianSetupStopsWhenPackageInstallationFails(string failure, int expectedExit)
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await RunPackageSetupAsync("debian", failure);

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.DoesNotContain("completed", result.Calls);
        if (failure == "common")
        {
            Assert.DoesNotContain("docker-ce", result.Calls);
            Assert.Null(result.Repository);
        }
    }

    private static async Task<ShellResult> RunPackageSetupAsync(string distribution, string failure = "")
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Cloud-init package setup requires a Unix shell.");
        }
        var root = Path.Combine(Path.GetTempPath(), "minicloud-cloud-init-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var binary = Path.Combine(root, "bin");
            Directory.CreateDirectory(binary);
            Directory.CreateDirectory(Path.Combine(root, "sources.list.d"));
            var osRelease = Path.Combine(root, "os-release");
            await File.WriteAllTextAsync(osRelease, $"ID={distribution}\nVERSION_CODENAME=bookworm\n");
            const string stub = """
                #!/bin/sh
                command_name="${0##*/}"
                printf '%s' "$command_name" >> "$BOOTSTRAP_LOG"
                for argument do printf '|%s' "$argument" >> "$BOOTSTRAP_LOG"; done
                printf '\n' >> "$BOOTSTRAP_LOG"
                case "$command_name" in
                  apt-get)
                    case "$*" in
                      'install -y python3 '*) [ "$BOOTSTRAP_FAIL" != common ] || exit 43 ;;
                      'install -y docker-ce '*) [ "$BOOTSTRAP_FAIL" != docker ] || exit 42 ;;
                    esac ;;
                  curl)
                    while [ "$#" -gt 0 ]; do
                      if [ "$1" = --output ]; then shift; printf 'fixture signing key\n' > "$1"; fi
                      shift
                    done ;;
                  dpkg) printf 'amd64\n' ;;
                esac
                """;
            foreach (var command in new[] { "apt-get", "curl", "dpkg" })
            {
                var path = Path.Combine(binary, command);
                await File.WriteAllTextAsync(path, stub);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var cloudInit = ResolveProvisionServerStep.BuildCloudInit("fixture", "packages");
            var lines = cloudInit.Split('\n');
            var start = Array.FindIndex(lines, line => line == "    set -eu");
            var end = Array.FindIndex(lines, start, line => line == "  - usermod -aG docker minicloud");
            Assert.True(start > 0 && end > start);
            var script = string.Join('\n', lines[start..end].Select(line => line[4..]))
                .Replace("/etc/os-release", Quote(osRelease), StringComparison.Ordinal)
                .Replace("/etc/apt/keyrings", Path.Combine(root, "keyrings"), StringComparison.Ordinal)
                .Replace("/etc/apt/sources.list.d", Path.Combine(root, "sources.list.d"), StringComparison.Ordinal)
                + "\nprintf 'completed\\n' >> \"$BOOTSTRAP_LOG\"\n";
            var scriptPath = Path.Combine(root, "packages.sh");
            await File.WriteAllTextAsync(scriptPath, script);
            var calls = Path.Combine(root, "calls.log");
            var startInfo = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.Environment["PATH"] = binary + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            startInfo.Environment["BOOTSTRAP_LOG"] = calls;
            startInfo.Environment["BOOTSTRAP_FAIL"] = failure;
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            await output;
            Assert.Equal("", await errors);
            var repositoryPath = Path.Combine(root, "sources.list.d/docker.sources");
            return new ShellResult(process.ExitCode, await File.ReadAllTextAsync(calls),
                File.Exists(repositoryPath) ? await File.ReadAllTextAsync(repositoryPath) : null,
                File.Exists(Path.Combine(root, "keyrings/docker.asc")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private sealed record ShellResult(int ExitCode, string Calls, string? Repository, bool KeyExists);
}
