// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Http.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Common.Contracts;

namespace Minicloud.Tests;

public sealed class LocalBuildDeploymentSessionTests
{
    [Theory]
    [InlineData(false, "failed")]
    [InlineData(true, "canceled")]
    public async Task ClosingUnsubmittedSession_ReportsFailureOrCallerCancellation(bool canceled, string expected)
    {
        using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var session = new LocalBuildDeploymentSession(fixture.Api, "app_test", "dep_test", caller.Token);
        if (canceled) { caller.Cancel(); }
        await session.DisposeAsync();
        var update = Assert.Single(fixture.Handler.Updates);
        Assert.Equal(expected, update.Status);
        Assert.Equal("app_test", update.AppId);
        Assert.Equal("build", update.Stage);
    }

    [Fact]
    public async Task LostSession_CancelsBuildAndPreservesBackendErrorCode()
    {
        using var fixture = new Fixture();
        fixture.Handler.RejectHeartbeat = true;
        await using var session = new LocalBuildDeploymentSession(fixture.Api, "app_test", "dep_test", default, TimeSpan.FromMilliseconds(10));
        await fixture.Handler.Heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, session.Token));
        var error = Assert.Throws<CliCommandException>(session.ThrowIfLost);
        Assert.Contains("preparation_expired", error.Message);
    }

    [Fact]
    public async Task SuccessfulSubmission_StopsHeartbeatAndDoesNotSendFailure()
    {
        using var fixture = new Fixture();
        await using var session = new LocalBuildDeploymentSession(fixture.Api, "app_test", "dep_test", default, TimeSpan.FromMilliseconds(10));
        await fixture.Handler.Heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopHeartbeatAsync();
        session.Submitted();
        Assert.All(fixture.Handler.Updates, x => Assert.Equal("heartbeat", x.Status));
        Assert.False(session.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task UnsubmittedAdmissionFailure_UsesSafeStageWithoutRawErrorText()
    {
        using var fixture = new Fixture();
        var session = new LocalBuildDeploymentSession(fixture.Api, "app_test", "dep_test", default) { Stage = "admission" };
        await session.StopHeartbeatAsync();
        await session.DisposeAsync();
        Assert.Equal("admission", Assert.Single(fixture.Handler.Updates).Stage);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("minicloud-build-session-");
        public SessionHandler Handler { get; } = new();
        public MinicloudApiClient Api { get; }
        public Fixture()
        {
            var environment = CliEnvironment.ForTests("https://api.example", _directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            Api = new MinicloudApiClient(environment, tokens, new HttpClient(Handler));
        }
        public void Dispose() => _directory.Delete(true);
    }

    private sealed class SessionHandler : HttpMessageHandler
    {
        public List<UpdateDeploymentPreparationRequest> Updates { get; } = [];
        public TaskCompletionSource Heartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RejectHeartbeat { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/deployments/preparations/dep_test", request.RequestUri!.AbsolutePath);
            var update = (await request.Content!.ReadFromJsonAsync<UpdateDeploymentPreparationRequest>(ct))!;
            Updates.Add(update);
            if (update.Status == "heartbeat")
            {
                Heartbeat.TrySetResult();
                if (RejectHeartbeat) { return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { error = new { code = "preparation_expired", message = "Heartbeat expired." } }) }; }
            }
            return new(HttpStatusCode.NoContent);
        }
    }
}
