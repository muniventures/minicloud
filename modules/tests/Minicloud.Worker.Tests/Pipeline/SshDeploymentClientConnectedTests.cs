using System.IO.Compression;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class SshDeploymentClientConnectedTests
{
    [SourcePreflightTheory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task SourceBuildPreflightReconnectsAfterBootstrapAndRejectsFailuresBeforeTransfer(
        int bootstrapStatus, bool dockerFails)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var fixture = Directory.CreateTempSubdirectory("minicloud-preflight-");
        var container = "minicloud-preflight-" + Guid.NewGuid().ToString("N");
        var containerStarted = false;
        try
        {
            var keyPath = Path.Combine(fixture.FullName, "key");
            using var key = RSA.Create(2048);
            var privateKey = key.ExportRSAPrivateKeyPem();
            await File.WriteAllTextAsync(keyPath, privateKey);
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await File.WriteAllTextAsync(keyPath + ".pub", await RunFixtureCommand("ssh-keygen", "-y", "-f", keyPath));
            var bin = Directory.CreateDirectory(Path.Combine(fixture.FullName, "bin"));
            async Task Script(string path, string content)
            {
                await File.WriteAllTextAsync(path, content);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            await Script(Path.Combine(fixture.FullName, "session"), """
                #!/bin/bash
                set -eu
                export SESSION_DOCKER_READY=0
                if test -f /fixture/account-ready; then export SESSION_DOCKER_READY=1; fi
                case "$SSH_ORIGINAL_COMMAND" in
                  *sftp*) echo transfer >> /fixture/events; exit 79 ;;
                esac
                export PATH=/fixture/bin:$PATH
                exec bash -c "$SSH_ORIGINAL_COMMAND"
                """);
            await Script(Path.Combine(bin.FullName, "cloud-init"), $"""
                #!/bin/bash
                echo bootstrap >> /fixture/events
                touch /fixture/account-ready
                exit {bootstrapStatus}
                """);
            await Script(Path.Combine(bin.FullName, "sudo"), "#!/bin/bash\nshift\nexec \"$@\"\n");
            await Script(Path.Combine(bin.FullName, "docker"), """
                #!/bin/bash
                echo "docker:$SESSION_DOCKER_READY" >> /fixture/events
                test "$SESSION_DOCKER_READY" = 1 || exit 13
                """ + (dockerFails ? "\nexit 17\n" : "\ncase \"$*\" in\n  'context show') echo default ;;\n  'buildx inspect '*) echo 'Driver: docker' ;;\nesac\n"));

            await RunFixtureCommand("docker", "run", "--rm", "-d", "--name", container,
                "-p", "127.0.0.1::22", "-v", fixture.FullName + ":/fixture",
                "-v", keyPath + ".pub:/root/.ssh/authorized_keys:ro", "--entrypoint", "/bin/bash",
                Environment.GetEnvironmentVariable("MINICLOUD_TEST_PREFLIGHT_SSH_IMAGE")!, "-c",
                "ssh-keygen -A; mkdir -p /run/sshd; exec /usr/sbin/sshd -D -e -o PasswordAuthentication=no -o PermitRootLogin=prohibit-password -o StrictModes=no -o ForceCommand=/fixture/session");
            containerStarted = true;
            var binding = await RunFixtureCommand("docker", "port", container, "22/tcp");
            var port = int.Parse(binding.Trim().Split(':')[^1]);
            using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync("127.0.0.1", port, readyTimeout.Token);
                    break;
                }
                catch (SocketException) { await Task.Delay(50, readyTimeout.Token); }
            }
            var opened = false;
            var client = new SshDeploymentClient(new SshDeploymentOptions
            {
                Port = port, ConnectionTimeout = TimeSpan.FromSeconds(5), CommandTimeout = TimeSpan.FromSeconds(5),
            });
            var error = await Assert.ThrowsAnyAsync<Exception>(() => client.ExecuteDeploymentAsync(
                new SshDeploymentRequest("127.0.0.1", "root", privateKey, "/tmp/preflight",
                    "{\"build_artifacts\":[{\"service_name\":\"web\"}]}", "#!/bin/bash\nexit 99",
                    [new SshDeploymentArtifactUpload("web", "web.zip", _ =>
                    {
                        opened = true;
                        return Task.FromResult<Stream>(new MemoryStream([1]));
                    })], TimeSpan.FromSeconds(5)), (_, _) => Task.CompletedTask, CancellationToken.None));
            var events = await File.ReadAllLinesAsync(Path.Combine(fixture.FullName, "events"));
            Assert.False(opened);
            if (bootstrapStatus == 1)
            {
                Assert.IsType<InvalidOperationException>(error);
                Assert.Equal(["bootstrap"], events);
            }
            else if (dockerFails)
            {
                Assert.IsType<InvalidOperationException>(error);
                Assert.Equal(["bootstrap", "docker:1"], events);
            }
            else
            {
                // Reaching the deliberately rejected SFTP subsystem proves both
                // preflight sessions succeeded before any artifact was opened.
                Assert.Equal(["bootstrap", "docker:1", "docker:1", "docker:1", "transfer"], events);
            }
        }
        finally
        {
            if (containerStarted) await RunFixtureCommand("docker", "rm", "-f", container);
            fixture.Delete(recursive: true);
        }
    }

    private static async Task<string> RunFixtureCommand(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    [Fact]
    public async Task CancelStopsRemoteBuildGroupAndWaitsForTermCleanup()
    {
        var port = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SSH_PORT");
        var key = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SSH_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(port) || string.IsNullOrWhiteSpace(key)) { return; }
        var directory = "/tmp/deployment-cancel-" + Guid.NewGuid().ToString("N");
        var script = """
            #!/bin/bash
            set -eu
            base="$(dirname "$1")"
            set -m
            bash -c 'trap "" TERM; while true; do sleep 1; done' &
            build=$!
            printf '%s\n' "$build" > "$base/build.pid"
            cleanup() {
              trap - TERM INT
              # Exercise a cleanup longer than the former five-second parent grace.
              sleep 6
              kill -KILL -- -"$build" 2>/dev/null || true
              wait "$build" 2>/dev/null || true
              touch "$base/cleanup.done"
              exit 143
            }
            trap cleanup TERM INT
            echo CANCEL_READY
            while true; do sleep 1; done
            touch "$base/activated"
            """;
        var client = new SshDeploymentClient(new SshDeploymentOptions { Port = int.Parse(port), ConnectionTimeout = TimeSpan.FromSeconds(10) });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var username = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SSH_USERNAME") ?? "minicloud";
        var privateKey = await File.ReadAllTextAsync(key);
        var execution = client.ExecuteDeploymentAsync(new SshDeploymentRequest("127.0.0.1", username, privateKey,
            directory, "{}", script, [], TimeSpan.FromSeconds(40)),
            (_, line) => { if (line == "CANCEL_READY") { ready.TrySetResult(); } return Task.CompletedTask; }, cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(25)));
        var check = await client.ExecuteCommandAsync("127.0.0.1", username, privateKey,
            $"test -f {directory}/cleanup.done && ! test -f {directory}/activated && ! kill -0 -- -$(cat {directory}/build.pid) 2>/dev/null",
            (_, _) => Task.CompletedTask, default);
        Assert.Equal(0, check);
    }

    [Fact]
    public async Task LocalSsh_RunsUploadedRuntimeScriptAndStreamsTimingTrace()
    {
        var portText = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SSH_PORT");
        var privateKeyPath = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SSH_PRIVATE_KEY");
        var scriptPath = Environment.GetEnvironmentVariable("MINICLOUD_TEST_RUNTIME_SCRIPT");
        if (string.IsNullOrWhiteSpace(portText) || string.IsNullOrWhiteSpace(privateKeyPath) ||
            string.IsNullOrWhiteSpace(scriptPath))
        {
            return;
        }

        var deploymentId = "dep_connected_trace";
        var localArchive = Path.Combine(Path.GetTempPath(), $"{deploymentId}.zip");
        await using (var archiveStream = File.Create(localArchive))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
        {
            var dockerfile = archive.CreateEntry("Dockerfile");
            await using var writer = new StreamWriter(dockerfile.Open());
            await writer.WriteAsync("FROM alpine\nRUN echo connected\n");
        }

        try
        {
            var remoteDirectory = $"/tmp/{deploymentId}";
            var remoteArchive = $"{remoteDirectory}/web.zip";
            var payload = JsonSerializer.Serialize(new
            {
                app_name = "timing-app",
                customer = "local",
                app_env = "production",
                deployment_id = deploymentId,
                database = "none",
                max_parallel_builds = 1,
                max_parallel_network_operations = 1,
                build_artifacts = new[] { new { service_name = "web", artifact_archive = remoteArchive, image_tag = $"minicloud/timing-app-web:{deploymentId}" } },
                services = new[] { new { name = "web", image = "placeholder", port = 8080, @public = true, path = "/" } }
            });
            var lines = new List<string>();
            var client = new SshDeploymentClient(new SshDeploymentOptions
            {
                Port = int.Parse(portText),
                ConnectionTimeout = TimeSpan.FromSeconds(20),
                CommandTimeout = TimeSpan.FromMinutes(2),
                MaxParallelArtifactTransfers = 1,
            });
            var result = await client.ExecuteDeploymentAsync(
                new SshDeploymentRequest(
                    "127.0.0.1", "minicloud", await File.ReadAllTextAsync(privateKeyPath),
                    remoteDirectory, payload, await File.ReadAllTextAsync(scriptPath),
                    [new SshDeploymentArtifactUpload("web", "web.zip", _ => Task.FromResult<Stream>(File.OpenRead(localArchive)))],
                    TimeSpan.FromMinutes(2)),
                (_, line) => { lock (lines) { lines.Add(line); } return Task.CompletedTask; },
                CancellationToken.None);

            Assert.True(result.ExitCode == 0, string.Join(Environment.NewLine, result.OutputLines));
            Assert.True(result.CompletionMarkerFound);
            Assert.Equal(deploymentId, result.CompletionSha);
            Assert.Contains(lines, line => line.Contains($"phase=service_build deployment_id={deploymentId}", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains($"phase=complete deployment_id={deploymentId}", StringComparison.Ordinal));
            var tracePath = Environment.GetEnvironmentVariable("MINICLOUD_SSH_TIMING_TRACE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(tracePath))
            {
                await File.WriteAllLinesAsync(tracePath, lines);
            }
        }
        finally
        {
            File.Delete(localArchive);
        }
    }
    [Fact]
    public async Task SlowArtifactUploadStartsWithFreshLeaseAndKeepsItRefreshed()
    {
        var port = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SSH_PORT");
        var key = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SSH_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(port) || string.IsNullOrWhiteSpace(key)) return;
        var directory = "/tmp/lease-upload-" + Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new { ownership_token = "connected-owner" });
        var script = """
            #!/bin/bash
            set -euo pipefail
            lease="$(dirname "$1")/worker.lease"
            test "$(cat "$lease")" = connected-owner
            age=$(( $(date +%s) - $(stat -c %Y "$lease") ))
            test "$age" -le 5
            initial=$(stat -c %Y "$lease")
            sleep 3
            test "$(stat -c %Y "$lease")" -gt "$initial"
            echo __MINICLOUD_DEPLOY_COMPLETE__=lease-upload
            """;
        var client = new SshDeploymentClient(new SshDeploymentOptions
        {
            Port = int.Parse(port), ConnectionTimeout = TimeSpan.FromSeconds(10),
        });
        var result = await client.ExecuteDeploymentAsync(new SshDeploymentRequest(
            "127.0.0.1", Environment.GetEnvironmentVariable("MINICLOUD_TEST_SSH_USERNAME") ?? "minicloud",
            await File.ReadAllTextAsync(key), directory, payload, script,
            [new SshDeploymentArtifactUpload("web", "web.zip", async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
                return new MemoryStream([1, 2, 3]);
            })], TimeSpan.FromSeconds(30)), (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.True(result.ExitCode == 0, string.Join(Environment.NewLine, result.OutputLines));
        Assert.True(result.CompletionMarkerFound);
        Assert.Equal("lease-upload", result.CompletionSha);
    }
}

public sealed class SourcePreflightTheoryAttribute : TheoryAttribute
{
    public SourcePreflightTheoryAttribute()
    {
        if (OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MINICLOUD_TEST_PREFLIGHT_SSH_IMAGE")))
        {
            Skip = "Set MINICLOUD_TEST_PREFLIGHT_SSH_IMAGE to a local Linux image with bash and OpenSSH server for the disposable SSH regression.";
        }
    }
}
