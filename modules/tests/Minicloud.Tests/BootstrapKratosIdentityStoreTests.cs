using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class BootstrapKratosIdentityStoreTests
{
    private const string IdentityId = "9a446cea-b8de-446e-834f-2c2e3bf6be0c";
    private static object Identity(string installation = "installation", string operation = "operation") => new
    {
        id = IdentityId, schema_id = "default", state = "active",
        traits = new { email = "operator@example.test", name = new { first = "Original" } },
        metadata_admin = new { preserved = "private", minicloud_bootstrap = new { installationId = installation, operationId = operation } },
        metadata_public = new { preserved = "public" }
    };

    [Fact]
    public async Task ReconciliationTraversesPagesAndMatchesBothPrivateIdsOnly()
    {
        var calls = 0;
        var handler = new Handler(request =>
        {
            calls++;
            if (calls == 1)
            {
                var response = Json(new[] { Identity("other-installation") });
                response.Headers.Add("Link", """<http://kratos.test/admin/identities?page_size=250&page_token=next>; rel="next" """);
                return response;
            }
            Assert.Contains("page_token=next", request.RequestUri!.Query);
            return Json(new[] { Identity() });
        });
        var result = await Store(handler).FindOperationAsync("installation", "operation", CancellationToken.None);
        Assert.Equal(IdentityId, result!.Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SameEmailOrPublicMetadataNeverAdmitsAnUnmarkedIdentity()
    {
        var store = Store(new Handler(_ => Json(new[]
        {
            new
            {
                id = IdentityId, traits = new { email = "operator@example.test" },
                metadata_public = new { minicloud_bootstrap = new { installationId = "installation", operationId = "operation" } }
            }
        })));
        Assert.Null(await store.FindOperationAsync("installation", "operation", CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateExactMatchesFailClosed()
    {
        var store = Store(new Handler(_ => Json(new[] { Identity(), Identity() })));
        var error = await Assert.ThrowsAsync<BootstrapException>(() =>
            store.FindOperationAsync("installation", "operation", CancellationToken.None));
        Assert.Equal("setup_state_conflict", error.Code);
    }

    [Theory]
    [InlineData("http://attacker.test/admin/identities?page_token=next")]
    [InlineData("http://kratos.test/admin/identities/secret")]
    [InlineData("http://kratos.test/admin/identities?page_size=250")]
    public async Task PaginationCannotEscapeAdminEndpointOrLoop(string next)
    {
        var calls = 0;
        var store = Store(new Handler(_ =>
        {
            calls++;
            var response = Json(Array.Empty<object>());
            response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
            return response;
        }));
        var error = await Assert.ThrowsAsync<BootstrapException>(() =>
            store.FindOperationAsync("installation", "operation", CancellationToken.None));
        Assert.Equal("auth_unavailable", error.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CreationIncludesExactPrivateMetadataAndPasswordOnlyInBody()
    {
        const string password = "Synthetic-password-123!";
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/admin/identities", request.RequestUri!.AbsolutePath);
            Assert.DoesNotContain(password, request.RequestUri.ToString());
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("user", body.RootElement.GetProperty("schema_id").GetString());
            Assert.Equal(password, body.RootElement.GetProperty("credentials").GetProperty("password").GetProperty("config").GetProperty("password").GetString());
            Assert.Equal("installation", body.RootElement.GetProperty("metadata_admin").GetProperty("minicloud_bootstrap").GetProperty("installationId").GetString());
            Assert.Equal("operation", body.RootElement.GetProperty("metadata_admin").GetProperty("minicloud_bootstrap").GetProperty("operationId").GetString());
            return Json(Identity());
        });
        var result = await Store(handler).CreateAsync("installation", "operation", "operator@example.test",
            password, "Operator", CancellationToken.None);
        Assert.Equal(IdentityId, result.Id);
    }

    [Fact]
    public async Task PasswordUpdatePreservesTraitsMetadataAndStateThenRevokesOnlyBoundIdentitySessions()
    {
        var calls = 0;
        var store = Store(new Handler(request =>
        {
            calls++;
            if (request.Method == HttpMethod.Get) return Json(Identity());
            if (request.Method == HttpMethod.Put)
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var root = body.RootElement;
                Assert.Equal("Original", root.GetProperty("traits").GetProperty("name").GetProperty("first").GetString());
                Assert.Equal("private", root.GetProperty("metadata_admin").GetProperty("preserved").GetString());
                Assert.Equal("public", root.GetProperty("metadata_public").GetProperty("preserved").GetString());
                Assert.Equal("active", root.GetProperty("state").GetString());
                return Json(Identity());
            }
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal($"/admin/identities/{IdentityId}/sessions", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        await store.SetPasswordAsync(IdentityId, "New-synthetic-password!", CancellationToken.None);
        await store.RevokeSessionsAsync(IdentityId, CancellationToken.None);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("not-json")]
    public async Task MalformedInventoryIsSanitizedAsUnavailable(string body)
    {
        var store = Store(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        }));
        var error = await Assert.ThrowsAsync<BootstrapException>(() =>
            store.FindOperationAsync("installation", "operation", CancellationToken.None));
        Assert.Equal("auth_unavailable", error.Code);
        Assert.DoesNotContain(body, error.Message);
    }

    [Fact]
    public async Task UpstreamFailureDoesNotExposeSecretResponseOrTransportMessage()
    {
        foreach (var transportFailure in new[] { false, true })
        {
            var store = Store(new Handler(_ =>
            {
                if (transportFailure) throw new HttpRequestException("password=secret");
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("password=secret")
                };
            }));
            var error = await Assert.ThrowsAsync<BootstrapException>(() =>
                store.HasIdentitiesAsync(CancellationToken.None));
            Assert.Equal("auth_unavailable", error.Code);
            Assert.DoesNotContain("secret", error.ToString());
        }
    }

    [Fact]
    public async Task OnlyDefinitiveCreationRejectionAllowsACorrectedAttempt()
    {
        var store = Store(new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("synthetic-secret-upstream-error")
        }));
        var error = await Assert.ThrowsAsync<BootstrapIdentityCreationRejectedException>(() =>
            store.CreateAsync("installation", "operation", "operator@example.test",
                "Synthetic-password!", "Operator", CancellationToken.None));
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    private static BootstrapKratosIdentityStore Store(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(new KratosOptions { AdminBaseUrl = "http://kratos.test" }));
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
