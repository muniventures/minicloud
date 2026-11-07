using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Minicloud.Worker.Ssh;
using Renci.SshNet;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class SshDeploymentClientFailureTests
{
    [SftpFact]
    public async Task RealSftpPermissionFailureDoesNotOpenArtifactOrRemoteExecutionWhenConfigured()
    {
        var keyPath = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SFTP_KEY");
        var portText = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SFTP_PORT");
        Assert.False(string.IsNullOrWhiteSpace(keyPath));
        Assert.True(int.TryParse(portText, out var port));

        var artifactOpened = false;
        var client = new SshDeploymentClient(new SshDeploymentOptions
        {
            Port = port,
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            CommandTimeout = TimeSpan.FromSeconds(5),
        }, NullLogger<SshDeploymentClient>.Instance);
        var request = new SshDeploymentRequest(
            "127.0.0.1", "minicloud", await File.ReadAllTextAsync(keyPath), "/forbidden/minicloud-test",
            "{}", "#!/bin/sh\necho should-not-run", [new SshDeploymentArtifactUpload(
                "web", "web.zip", _ =>
                {
                    artifactOpened = true;
                    return Task.FromResult<Stream>(new MemoryStream("zip"u8.ToArray()));
                })], TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<Exception>(() => client.ExecuteDeploymentAsync(
            request, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.False(artifactOpened);
    }

    [SftpFact]
    public async Task RealSftpOnlyHostRejectsBuildBeforeOpeningArtifacts()
    {
        var keyPath = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SFTP_KEY")!;
        var port = int.Parse(Environment.GetEnvironmentVariable("MINICLOUD_TEST_SFTP_PORT")!);
        var opened = false;
        var client = new SshDeploymentClient(new SshDeploymentOptions
        {
            Port = port,
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            CommandTimeout = TimeSpan.FromSeconds(5),
        });
        var request = new SshDeploymentRequest(
            "127.0.0.1", "minicloud", await File.ReadAllTextAsync(keyPath), "/upload/no-buildkit",
            "{\"build_artifacts\":[{\"service_name\":\"web\"}]}", "#!/bin/sh\nexit 99",
            [new SshDeploymentArtifactUpload("web", "web.zip", _ =>
            {
                opened = true;
                return Task.FromResult<Stream>(new MemoryStream([1]));
            })], TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteDeploymentAsync(
            request, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("buildkit_unavailable", error.Message);
        Assert.False(opened);
    }

    [Fact]
    public async Task SshPreparationFailureDoesNotOpenRemoteExecutionConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptedConnections = 0;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(serverCancellation.Token);
            Interlocked.Increment(ref acceptedConnections);
            await using var stream = connection.GetStream();
            await stream.WriteAsync("SSH-2.0-minicloud-failure-injection\r\n"u8.ToArray(), serverCancellation.Token);
            connection.Close();
        }, serverCancellation.Token);

        using var rsa = RSA.Create(2048);
        var client = new SshDeploymentClient(
            new SshDeploymentOptions
            {
                Port = port,
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                CommandTimeout = TimeSpan.FromSeconds(2),
            },
            NullLogger<SshDeploymentClient>.Instance);
        var artifactOpened = false;
        var request = new SshDeploymentRequest(
            "127.0.0.1", "minicloud", rsa.ExportPkcs8PrivateKeyPem(), "/tmp/minicloud-test",
            "{}", "#!/bin/sh\necho should-not-run", [new SshDeploymentArtifactUpload(
                "web", "web.zip", _ =>
                {
                    artifactOpened = true;
                    return Task.FromResult<Stream>(new MemoryStream("zip"u8.ToArray()));
                })], TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<Exception>(() => client.ExecuteDeploymentAsync(
            request, (_, _) => Task.CompletedTask, CancellationToken.None));
        await server;

        Assert.Equal(1, Volatile.Read(ref acceptedConnections));
        Assert.False(artifactOpened);
    }

    [SftpFact]
    public async Task RealSftpStalledUploadCancellationCompletesWithinBoundWhenConfigured()
    {
        var keyPath = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SFTP_KEY");
        var portText = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SFTP_PORT");
        Assert.False(string.IsNullOrWhiteSpace(keyPath));
        Assert.True(int.TryParse(portText, out var port));

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new SshDeploymentClient(new SshDeploymentOptions
        {
            Port = port, ConnectionTimeout = TimeSpan.FromSeconds(3), CommandTimeout = TimeSpan.FromSeconds(3),
        }, NullLogger<SshDeploymentClient>.Instance);
        var largePayload = new byte[64 * 1024 * 1024];
        var request = new SshDeploymentRequest(
            "127.0.0.1", "minicloud", await File.ReadAllTextAsync(keyPath), "/upload/minicloud-cancel",
            "{}", "#!/bin/sh\ntouch /upload/remote-executed", [new SshDeploymentArtifactUpload(
                "web", "web.zip", _ =>
                {
                    opened.TrySetResult();
                    return Task.FromResult<Stream>(new MemoryStream(largePayload));
                }, largePayload.Length)], TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var execution = client.ExecuteDeploymentAsync(request, (_, _) => Task.CompletedTask, cancellation.Token);
        var first = await Task.WhenAny(opened.Task, execution).WaitAsync(TimeSpan.FromSeconds(15));
        if (first == execution)
        {
            await execution;
        }
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"Cancellation took {stopwatch.Elapsed}.");

        await Task.Delay(250);
        using var keyStream = File.OpenRead(keyPath!);
        using var sftp = new SftpClient(new PrivateKeyConnectionInfo(
            "127.0.0.1", port, "minicloud", new PrivateKeyFile(keyStream)));
        sftp.Connect();
        Assert.False(sftp.Exists("/upload/minicloud-cancel/web.zip"));
        Assert.False(sftp.Exists("/upload/remote-executed"));
        sftp.Disconnect();
    }
}

public sealed class SftpFactAttribute : FactAttribute
{
    public SftpFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MINICLOUD_TEST_SFTP_KEY")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MINICLOUD_TEST_SFTP_PORT")))
        {
            Skip = "Run scripts/github-actions/minicloud-deploy/test-sftp-failure.sh for the real SFTP integration cases.";
        }
    }
}
