using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Registry;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Api.Controllers;

[ApiController]
[AllowAnonymous]
public sealed class RegistryGatewayController(
    MinicloudDbContext db, ImagePublicationService publications, RegistryTokens tokens, RegistryClient registry,
    IOptions<RegistryOptions> options, IHttpClientFactory clients, IServiceScopeFactory scopes, TimeProvider clock) : ControllerBase
{
    private static readonly SemaphoreSlim Transfers = new(32, 32);
    private readonly RegistryOptions _options = options.Value;

    [HttpGet("api/registry/token")]
    public Task<IResult> Token(CancellationToken ct) => ImagePublicationsController.Handle(async () =>
    {
        publications.RequireEnabled();
        Response.Headers.CacheControl = "no-store";
        if (Request.Query["service"] != _options.Audience) { return Results.Unauthorized(); }
        var basic = Request.Headers.Authorization.ToString();
        if (!basic.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) || basic.Length > 2048) { return Results.Unauthorized(); }
        string[] parts;
        try { parts = Encoding.UTF8.GetString(Convert.FromBase64String(basic[6..])).Split(':', 2); }
        catch (FormatException) { return Results.Unauthorized(); }
        if (parts.Length != 2) { return Results.Unauthorized(); }
        var session = await publications.ActiveSessionAsync(parts[0], ct);
        if (session is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(RegistryTokens.Hash(parts[1])), Encoding.ASCII.GetBytes(session.CredentialHash))) { return Results.Unauthorized(); }
        var requested = Request.Query["scope"].SelectMany(x => (x ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var ownScopes = requested.Where(x => x.StartsWith($"repository:{session.ImagePublication!.Repository}:", StringComparison.Ordinal)).ToArray();
        var push = session.Kind == RegistrySessionKind.Push && ownScopes.Any(x => x.Split(':').Last().Split(',').Contains("push"));
        var pull = ownScopes.Any(x => x.Split(':').Last().Split(',').Contains("pull"));
        var bearer = tokens.Issue(session.Id, session.ImagePublication!.Repository, push, session.ExpiresAt, push || pull, pull: pull);
        return Results.Ok(new { token = bearer, access_token = bearer, expires_in = Math.Min(300, (int)(session.ExpiresAt - clock.GetUtcNow()).TotalSeconds), issued_at = clock.GetUtcNow() });
    });

    [Route("v2/{**path}")]
    [AcceptVerbs("GET", "HEAD", "POST", "PATCH", "PUT", "DELETE")]
    [DisableRequestSizeLimit]
    public async Task Gateway(string? path, CancellationToken ct)
    {
        Response.Headers["Docker-Distribution-Api-Version"] = "registry/2.0";
        if (!_options.Enabled) { Response.StatusCode = 503; return; }
        var auth = Request.Headers.Authorization.ToString();
        var id = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? tokens.Verify(auth[7..]) : null;
        var session = id is null ? null : await publications.ActiveSessionAsync(id, ct);
        if (session is null)
        {
            Response.StatusCode = 401;
            Response.Headers.WWWAuthenticate = $"Bearer realm=\"{_options.PublicUrl.TrimEnd('/')}/api/registry/token\",service=\"{_options.Audience}\"";
            var segments = (path ?? "").Split('/');
            if (segments.Length >= 7 && segments[0] == "org" && segments[2] == "app" && segments[4] == "service")
            {
                try
                {
                    var repository = ImagePublicationValidation.Repository(segments[1], segments[3], segments[5]);
                    Response.Headers.WWWAuthenticate += $",scope=\"repository:{repository}:{(Request.Method is "GET" or "HEAD" ? "pull" : "pull,push")}\"";
                }
                catch (ArgumentException) { }
            }
            return;
        }
        if (string.IsNullOrEmpty(path)) { Response.StatusCode = Request.Method is "GET" or "HEAD" ? 200 : 405; return; }
        var publication = session.ImagePublication!;
        var prefix = publication.Repository + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Contains('%') || path.Contains("..", StringComparison.Ordinal)) { Response.StatusCode = 403; return; }
        var tail = path[prefix.Length..];
        var write = Request.Method is "POST" or "PATCH" or "PUT" or "DELETE";
        var manifest = tail.StartsWith("manifests/", StringComparison.Ordinal);
        var blob = tail.StartsWith("blobs/sha256:", StringComparison.Ordinal) && ImagePublicationValidation.IsDigest(tail[6..]);
        var upload = tail.StartsWith("blobs/uploads/", StringComparison.Ordinal);
        if (!(manifest || blob || upload) || (write && session.Kind != RegistrySessionKind.Push) || (write && !(manifest || upload))) { Response.StatusCode = 403; return; }
        if (manifest && (tail[10..].Contains('/') || tail[10..].Length > 128)) { Response.StatusCode = 403; return; }
        if (session.Kind == RegistrySessionKind.Pull && !CanPull(publication, tail)) { Response.StatusCode = 403; return; }
        if (Request.Query.Keys.Any(x => x is not ("digest" or "_state" or "mount" or "from"))) { Response.StatusCode = 403; return; }
        if ((Request.Query.ContainsKey("mount") || Request.Query.ContainsKey("from")) &&
            (!upload || tail != "blobs/uploads/" || Request.Method != "POST" || !ImagePublicationValidation.IsDigest(Request.Query["mount"].ToString())))
        { Response.StatusCode = 403; return; }
        if (Request.Query.TryGetValue("digest", out var digest) && !ImagePublicationValidation.IsDigest(digest.ToString())) { Response.StatusCode = 400; return; }
        if (upload && tail != "blobs/uploads/")
        {
            var uuid = tail[14..];
            if (!Guid.TryParseExact(uuid, "D", out _) || !await db.RegistryUploads.AnyAsync(x => x.Id == uuid && x.SessionId == session.Id, ct)) { Response.StatusCode = 403; return; }
        }
        if (upload && tail == "blobs/uploads/" && Request.Method != "POST") { Response.StatusCode = 405; return; }
        if (write)
        {
            try { await publications.CheckMaintenanceAsync(ct); }
            catch (RegistryImageValidationException) { Response.StatusCode = 503; Response.Headers.RetryAfter = "30"; return; }
        }
        if (!await Transfers.WaitAsync(0, ct)) { Response.StatusCode = 429; Response.Headers.RetryAfter = "2"; return; }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = MonitorAsync(session.Id, cancel);
        try
        {
            // Decline cross-repository mounting without probing the source: 202 starts a normal upload.
            var query = QueryString.Create(Request.Query.Where(x => x.Key is not ("mount" or "from")));
            using var request = new HttpRequestMessage(new HttpMethod(Request.Method), new Uri(new Uri(_options.BackendUrl.TrimEnd('/') + "/"), "v2/" + path + query));
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(auth);
            foreach (var name in new[] { "Accept", "Range", "If-None-Match" })
            { if (Request.Headers.TryGetValue(name, out var value)) { request.Headers.TryAddWithoutValidation(name, value.ToArray()); } }
            if (write && Request.Method != "DELETE")
            {
                if (manifest)
                {
                    if (Request.Method != "PUT") { Response.StatusCode = 405; return; }
                    var bytes = await ReadManifestAsync(session.Id, cancel.Token);
                    var identity = tail[10..];
                    var actual = RegistryImageValidator.Digest(bytes);
                    if (identity != publication.CandidateTag && identity != actual) { Response.StatusCode = 403; return; }
                    using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                    var configDigest = doc.RootElement.GetProperty("config").GetProperty("digest").GetString();
                    if (!ImagePublicationValidation.IsDigest(configDigest)) { Response.StatusCode = 400; return; }
                    var config = await registry.ReadAsync(publication.Repository, "blobs", configDigest!, ImagePublicationValidation.MaxConfigBytes, cancel.Token);
                    RegistryImageValidator.Verify(bytes, config, actual, publication.Platform);
                    request.Content = new ByteArrayContent(bytes);
                }
                else { request.Content = new UploadContent(Request.Body, Request.ContentLength, session.Id, publications, cancel.Token); }
                if (Request.ContentType is not null) { request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(Request.ContentType); }
            }
            using var response = await clients.CreateClient("registry-gateway").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key is "Transfer-Encoding" or "Connection" or "Location" or "Server") { continue; }
                Response.Headers[header.Key] = header.Value.ToArray();
            }
            if (response.Headers.Location is not null)
            {
                var location = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location.PathAndQuery : response.Headers.Location.OriginalString;
                if (!location.StartsWith("/v2/" + prefix, StringComparison.Ordinal)) { Response.StatusCode = 502; return; }
                Response.Headers.Location = _options.PublicUrl.TrimEnd('/') + location;
                if (upload && Request.Method == "POST" && response.StatusCode == System.Net.HttpStatusCode.Accepted)
                {
                    var uuid = location.Split('?')[0].Split('/').Last();
                    if (!Guid.TryParseExact(uuid, "D", out _)) { Response.StatusCode = 502; return; }
                    db.RegistryUploads.Add(new RegistryUpload { Id = uuid, SessionId = session.Id, CreatedAt = clock.GetUtcNow() });
                    await db.SaveChangesAsync(cancel.Token);
                }
            }
            if (Request.Method != "HEAD") { await response.Content.CopyToAsync(Response.Body, cancel.Token); }
        }
        catch (RegistryImageValidationException ex)
        {
            if (!Response.HasStarted) { Response.StatusCode = ex.Code == ImagePublicationErrors.QuotaExceeded ? 429 : 400; }
            else { HttpContext.Abort(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (!Response.HasStarted) { Response.StatusCode = 502; }
            else { HttpContext.Abort(); }
        }
        finally { cancel.Cancel(); await monitor; Transfers.Release(); }
    }

    private static bool CanPull(ImagePublication publication, string tail)
    {
        if (tail == "manifests/" + publication.ManifestDigest) { return true; }
        if (!tail.StartsWith("blobs/", StringComparison.Ordinal) || publication.ManifestJson is null) { return false; }
        using var doc = JsonDocument.Parse(publication.ManifestJson);
        return doc.RootElement.GetProperty("layers").EnumerateArray().Append(doc.RootElement.GetProperty("config"))
            .Any(x => tail == "blobs/" + x.GetProperty("digest").GetString());
    }

    private async Task MonitorAsync(string id, CancellationTokenSource cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancel.Token);
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<ImagePublicationService>().ActiveSessionAsync(id, cancel.Token) is null) { cancel.Cancel(); }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch { cancel.Cancel(); }
    }

    private async Task<byte[]> ReadManifestAsync(string sessionId, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (stream.Length + count > ImagePublicationValidation.MaxManifestBytes) { throw new RegistryImageValidationException(ImagePublicationErrors.Unsupported, "Manifest exceeds its limit."); }
            await publications.ChargeAsync(sessionId, count, ct);
            stream.Write(buffer, 0, count);
        }
        return stream.ToArray();
    }

    private sealed class UploadContent(Stream source, long? length, string sessionId, ImagePublicationService service, CancellationToken cancellation) : HttpContent
    {
        protected override bool TryComputeLength(out long value) { value = length ?? 0; return length.HasValue; }
        protected override async Task SerializeToStreamAsync(Stream destination, System.Net.TransportContext? context)
        {
            var buffer = new byte[1024 * 1024];
            int count;
            while ((count = await source.ReadAtLeastAsync(buffer, buffer.Length, false, cancellation)) > 0)
            {
                await service.ChargeAsync(sessionId, count, cancellation);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellation);
            }
        }
    }
}
