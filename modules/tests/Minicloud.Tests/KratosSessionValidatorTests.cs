using System.Net;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;

namespace Minicloud.Tests;

public sealed class KratosSessionValidatorTests
{
    [Fact]
    public async Task ValidationUsesOnlyTheFixedAdminSessionEndpoint()
    {
        var handler = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"session-1","active":true,"identity":{"id":"identity-1"}}""")
        });
        using var client = new HttpClient(handler);
        var validator = new KratosSessionValidator(
            client,
            Options.Create(new KratosOptions { AdminBaseUrl = "http://kratos:4434" }));

        Assert.True(await validator.IsSessionActiveAsync("session-1", "identity-1", CancellationToken.None));
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/admin/sessions/session-1", handler.Request.RequestUri!.AbsolutePath);
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
