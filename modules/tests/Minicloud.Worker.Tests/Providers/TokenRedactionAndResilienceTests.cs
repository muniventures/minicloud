using System.Net;
using Microsoft.Extensions.Logging;
using Minicloud.Common.Providers.Common;
using Xunit;

namespace Minicloud.Worker.Tests.Providers;

public class TokenRedactionAndResilienceTests
{
    [Fact]
    public void TokenRedactor_RedactsBearerTokensAndExplicitKeys()
    {
        const string secretToken = "super-secret-token-abcdef123456";
        var rawMessage = $"Connecting with Bearer {secretToken} and api_key=\"{secretToken}\" to endpoint";

        var redacted = TokenRedactor.Redact(rawMessage, secretToken);

        Assert.DoesNotContain(secretToken, redacted);
        Assert.Contains("Bearer [REDACTED]", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public async Task ResilientHttpRetryHandler_When429Returned_RetriesAndSucceeds()
    {
        // Arrange
        var testHandler = new TestHttpMessageHandler();
        // Return 429 twice, then 200 OK
        testHandler.QueueResponse(new HttpResponseMessage((HttpStatusCode)429));
        testHandler.QueueResponse(new HttpResponseMessage((HttpStatusCode)429));
        testHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"ok"}""")
        });

        var retryHandler = new ResilientHttpRetryHandler(testHandler, maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(5));
        var client = new HttpClient(retryHandler);

        // Act
        var response = await client.GetAsync("https://example.com/test");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, testHandler.RecordedRequests.Count);
    }

    [Fact]
    public async Task ResilientHttpRetryHandler_When503Returned_RetriesAndSucceeds()
    {
        // Arrange
        var testHandler = new TestHttpMessageHandler();
        testHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        testHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"healthy"}""")
        });

        var retryHandler = new ResilientHttpRetryHandler(testHandler, maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(5));
        var client = new HttpClient(retryHandler);

        // Act
        var response = await client.GetAsync("https://example.com/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, testHandler.RecordedRequests.Count);
    }

    [Fact]
    public async Task ResilientHttpRetryHandler_When404Returned_DoesNotRetry()
    {
        // Arrange
        var testHandler = new TestHttpMessageHandler();
        testHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        var retryHandler = new ResilientHttpRetryHandler(testHandler, maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(5));
        var client = new HttpClient(retryHandler);

        // Act
        var response = await client.GetAsync("https://example.com/not-found");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(testHandler.RecordedRequests);
    }

    [Fact]
    public async Task ResilientHttpRetryHandler_LogsWithRedactedUriAndNoTokenLeak()
    {
        // Arrange
        var testHandler = new TestHttpMessageHandler();
        testHandler.QueueResponse(new HttpResponseMessage((HttpStatusCode)429));
        testHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));

        var testLogger = new TestLogger<ResilientHttpRetryHandler>();
        var retryHandler = new ResilientHttpRetryHandler(
            testHandler,
            maxRetries: 2,
            baseDelay: TimeSpan.FromMilliseconds(5),
            logger: testLogger);

        var client = new HttpClient(retryHandler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api?token=secret12345");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "top-secret-token");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(testLogger.LoggedMessages);
        foreach (var msg in testLogger.LoggedMessages)
        {
            Assert.DoesNotContain("top-secret-token", msg);
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> LoggedMessages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LoggedMessages.Add(formatter(state, exception));
        }
    }
}
