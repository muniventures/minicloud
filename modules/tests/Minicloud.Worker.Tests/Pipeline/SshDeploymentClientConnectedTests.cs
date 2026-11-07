using System.IO.Compression;
using System.Text.Json;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class SshDeploymentClientConnectedTests
{
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
