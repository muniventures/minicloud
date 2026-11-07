using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class OpenBaoTokenRenewalTests
{
    [Theory]
    [InlineData(604800, 43200)]
    [InlineData(90, 30)]
    public async Task RenewalUsesSelfEndpointAndSchedulesBeforeExpiry(int leaseSeconds, int delaySeconds)
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://bao.test/v1/auth/token/renew-self", request.RequestUri!.ToString());
            Assert.Equal("test-secret", Assert.Single(request.Headers.GetValues("X-Vault-Token")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { auth = new { client_token = "test-secret", renewable = true, lease_duration = leaseSeconds } }))
            });
        });
        var logger = new TestLogger();
        using var service = Create(handler, logger);

        Assert.Equal(TimeSpan.FromSeconds(delaySeconds), await service.RenewAsync(CancellationToken.None));
        Assert.DoesNotContain("test-secret", string.Join('\n', logger.Messages));
    }

    [Theory]
    [InlineData(403, "test-secret")]
    [InlineData(503, "test-secret")]
    [InlineData(200, "test-secret")]
    [InlineData(200, "{\"auth\":{\"renewable\":false,\"lease_duration\":604800}}")]
    [InlineData(200, "{\"auth\":{\"renewable\":true,\"lease_duration\":0}}")]
    public async Task FailureRetriesWithoutLoggingResponse(int status, string body)
    {
        var logger = new TestLogger();
        using var service = Create(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body)
        })), logger);

        Assert.Equal(TimeSpan.FromMinutes(1), await service.RenewAsync(CancellationToken.None));
        Assert.Contains(logger.Levels, level => level == LogLevel.Error);
        Assert.DoesNotContain("test-secret", string.Join('\n', logger.Messages));
    }

    [Fact]
    public async Task TimeoutRetriesAndShutdownCancellationPropagates()
    {
        var logger = new TestLogger();
        using var service = Create(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        }), logger, TimeSpan.FromMilliseconds(20));

        Assert.Equal(TimeSpan.FromMinutes(1), await service.RenewAsync(CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RenewAsync(canceled.Token));
    }

    [Fact]
    public async Task UnconfiguredStoreDoesNotStartRenewal()
    {
        using var service = new OpenBaoTokenRenewalService(new Factory(new Handler((_, _) =>
            throw new Xunit.Sdk.XunitException("Unexpected HTTP request"))),
            Options.Create(new OpenBaoSecretStoreOptions()), new TestLogger());
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
    }

    private static OpenBaoTokenRenewalService Create(Handler handler, TestLogger logger, TimeSpan? timeout = null) =>
        new(new Factory(handler), Options.Create(new OpenBaoSecretStoreOptions
        {
            Address = "https://bao.test/",
            Token = "test-secret",
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        }), logger);

    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TestLogger : ILogger<OpenBaoTokenRenewalService>
    {
        public List<string> Messages { get; } = [];
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Levels.Add(logLevel);
        }
    }
}
