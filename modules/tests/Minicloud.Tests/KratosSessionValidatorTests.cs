using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;

namespace Minicloud.Tests;

public sealed class KratosSessionValidatorTests
{
    [Fact]
    public async Task ValidationUsesOnlyTheFixedAdminSessionEndpoint()
    {
        var handler = new ResponseHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Query == "?expand=identity"
                ? """{"id":"session-1","active":true,"identity":{"id":"identity-1"}}"""
                : """{"id":"session-1","active":true,"identity":null}""")
        });
        using var client = new HttpClient(handler);
        var validator = new KratosSessionValidator(
            client,
            Options.Create(new KratosOptions { AdminBaseUrl = "http://kratos:4434" }));

        Assert.True(await validator.IsSessionActiveAsync("session-1", "identity-1", CancellationToken.None));
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/admin/sessions/session-1", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("?expand=identity", handler.Request.RequestUri.Query);
        await validator.RevokeSessionAsync("session-1", CancellationToken.None);
        Assert.Equal(HttpMethod.Delete, handler.Request.Method);
        Assert.Equal("", handler.Request.RequestUri!.Query);
        await Assert.ThrowsAsync<KratosUnavailableException>(
            () => validator.IsSessionActiveAsync("../admin", "identity-1", CancellationToken.None));
    }

    [Fact]
    public async Task RedirectsAndDependencyFailuresBecomeSanitizedUnavailableErrors()
    {
        foreach (var response in new[]
                 {
                     new HttpResponseMessage(HttpStatusCode.Redirect),
                     new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                 })
        {
            using var client = new HttpClient(new ResponseHandler(_ => response));
            var validator = new KratosSessionValidator(
                client,
                Options.Create(new KratosOptions { AdminBaseUrl = "http://kratos:4434" }));
            await Assert.ThrowsAsync<KratosUnavailableException>(
                () => validator.IsSessionActiveAsync("session-1", "identity-1", CancellationToken.None));
        }

        using var failingClient = new HttpClient(new ResponseHandler(_ => throw new HttpRequestException("upstream secret body")));
        var failingValidator = new KratosSessionValidator(
            failingClient,
            Options.Create(new KratosOptions { AdminBaseUrl = "http://kratos:4434" }));
        await Assert.ThrowsAsync<KratosUnavailableException>(
            () => failingValidator.IsSessionActiveAsync("session-1", "identity-1", CancellationToken.None));
    }

    [Fact]
    public async Task TenSecondDeadlineAlsoBoundsResponseBodiesAfterHeadersArrive()
    {
        using var sessionClient = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) }));
        using var flowClient = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) }));
        var options = Options.Create(new KratosOptions
        {
            AdminBaseUrl = "http://kratos:4434",
            PublicBaseUrl = "http://kratos:4433",
            DashboardOrigin = "https://dashboard.example.test"
        });
        var validator = new KratosSessionValidator(sessionClient, options);
        var flows = new KratosBrowserFlowClient(flowClient, options);
        var elapsed = Stopwatch.StartNew();
        await Task.WhenAll(
            Assert.ThrowsAsync<KratosUnavailableException>(
                () => validator.IsSessionActiveAsync("session-1", "identity-1", CancellationToken.None)),
            Assert.ThrowsAsync<KratosUnavailableException>(
                () => flows.SendAsync("login", "start", null, "/apps", null, new Dictionary<string, string>(), CancellationToken.None)));
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task OversizedUnadvertisedResponseBodiesAreRejected()
    {
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new ChunkedStream(new byte[64 * 1024 + 1])) }));
        var options = Options.Create(new KratosOptions
        {
            PublicBaseUrl = "http://kratos:4433",
            DashboardOrigin = "https://dashboard.example.test"
        });
        await Assert.ThrowsAsync<KratosUnavailableException>(() => new KratosBrowserFlowClient(client, options)
            .SendAsync("login", "start", null, "/apps", null, new Dictionary<string, string>(), CancellationToken.None));
    }

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(respond(request));
        }
    }
}
