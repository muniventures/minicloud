using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class BootstrapKratosIdentityStore(HttpClient client, IOptions<KratosOptions> options)
    : IBootstrapIdentityStore
{
    private const int BodyLimit = 1024 * 1024;
    private Uri Admin(string path)
    {
        if (!Uri.TryCreate(options.Value.AdminBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw BootstrapException.Unavailable();
        return new Uri(uri, path);
    }

    public async Task<bool> HasIdentitiesAsync(CancellationToken cancellationToken)
    {
        await foreach (var _ in ListAsync(cancellationToken)) return true;
        return false;
    }

    public async Task<BootstrapIdentity?> FindOperationAsync(string installationId, string operationId,
        CancellationToken cancellationToken)
    {
        BootstrapIdentity? found = null;
        await foreach (var item in ListAsync(cancellationToken))
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw BootstrapException.Unavailable();
            if (!item.TryGetProperty("metadata_admin", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty("minicloud_bootstrap", out var bootstrap) ||
                bootstrap.ValueKind != JsonValueKind.Object ||
                ReadString(bootstrap, "installationId") != installationId ||
                ReadString(bootstrap, "operationId") != operationId)
                continue;
            if (found is not null) throw BootstrapException.Conflict();
            found = ParseIdentity(item);
        }
        return found;
    }

    private async IAsyncEnumerable<JsonElement> ListAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var first = Admin("admin/identities?page_size=250");
        Uri? next = first;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null)
        {
            if (visited.Count >= 1000 || !visited.Add(next.AbsoluteUri)) throw BootstrapException.Unavailable();
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await SendAsync(request, ct);
            using var document = await ReadAsync(response, ct);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw BootstrapException.Unavailable();
            foreach (var identity in document.RootElement.EnumerateArray())
            {
                if (identity.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(ReadString(identity, "id")))
                    throw BootstrapException.Unavailable();
                yield return identity.Clone();
            }
            next = null;
            if (response.Headers.TryGetValues("Link", out var links))
            {
                foreach (var entry in links.SelectMany(x => x.Split(',')))
                {
                    if (!entry.Contains("rel=\"next\"", StringComparison.Ordinal)) continue;
                    var start = entry.IndexOf('<');
                    var end = entry.IndexOf('>');
                    if (start < 0 || end <= start ||
                        !Uri.TryCreate(first, entry[(start + 1)..end], out var candidate) ||
                        candidate.GetLeftPart(UriPartial.Path) != first.GetLeftPart(UriPartial.Path) ||
                        candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0 || next is not null)
                        throw BootstrapException.Unavailable();
                    next = candidate;
                }
            }
        }
    }

    public async Task<BootstrapIdentity> CreateAsync(string installationId, string operationId,
        string email, string password, string displayName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Admin("admin/identities"))
        {
            Content = JsonContent.Create(new
            {
                schema_id = "user",
                traits = new { email, name = new { first = displayName } },
                credentials = new { password = new { config = new { password } } },
                metadata_admin = new { minicloud_bootstrap = new { installationId, operationId } }
            })
        };
        using var response = await SendAsync(request, cancellationToken);
        using var document = await ReadAsync(response, cancellationToken);
        var root = document.RootElement;
        var result = ParseIdentity(root);
        if (!root.TryGetProperty("metadata_admin", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("minicloud_bootstrap", out var marker) ||
            ReadString(marker, "installationId") != installationId || ReadString(marker, "operationId") != operationId)
            throw BootstrapException.Unavailable();
        return result;
    }

    public async Task<BootstrapIdentity> GetAsync(string identityId, CancellationToken cancellationToken)
    {
        using var document = await ReadIdentityAsync(identityId, cancellationToken);
        var identity = ParseIdentity(document.RootElement);
        if (identity.Id != identityId) throw BootstrapException.Conflict();
        return identity;
    }

    public async Task SetPasswordAsync(string identityId, string password, CancellationToken cancellationToken)
    {
        // PUT replaces identity fields. Preserve traits, private/public metadata and state;
        // never reconstruct these from the email supplied by the operator.
        using var document = await ReadIdentityAsync(identityId, cancellationToken);
        var root = document.RootElement;
        if (ParseIdentity(root).Id != identityId) throw BootstrapException.Conflict();
        if (!root.TryGetProperty("schema_id", out var schema) || schema.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
            throw BootstrapException.Unavailable();
        var body = new Dictionary<string, object?>
        {
            ["schema_id"] = schema.GetString(), ["state"] = state.GetString(),
            ["traits"] = root.GetProperty("traits").Clone(),
            ["credentials"] = new { password = new { config = new { password } } }
        };
        foreach (var field in new[] { "metadata_admin", "metadata_public" })
            if (root.TryGetProperty(field, out var value)) body[field] = value.Clone();
        using var request = new HttpRequestMessage(HttpMethod.Put, Admin("admin/identities/" + EscapeId(identityId)))
        {
            Content = JsonContent.Create(body)
        };
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task RevokeSessionsAsync(string identityId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            Admin("admin/identities/" + EscapeId(identityId) + "/sessions"));
        using var response = await SendAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> ReadIdentityAsync(string identityId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Admin("admin/identities/" + EscapeId(identityId)));
        using var response = await SendAsync(request, ct);
        return await ReadAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var missing = response.StatusCode == HttpStatusCode.NotFound;
                var rejectedCreation = response.StatusCode == HttpStatusCode.BadRequest &&
                    request.Method == HttpMethod.Post;
                response.Dispose();
                if (rejectedCreation) throw new BootstrapIdentityCreationRejectedException();
                if (missing) throw BootstrapException.Conflict();
                throw BootstrapException.Unavailable();
            }
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw BootstrapException.Unavailable();
        }
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (response.Content.Headers.ContentLength > BodyLimit) throw BootstrapException.Unavailable();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            int count;
            while ((count = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + count > BodyLimit) throw BootstrapException.Unavailable();
                buffer.Write(bytes, 0, count);
            }
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
        {
            throw BootstrapException.Unavailable();
        }
    }

    private static BootstrapIdentity ParseIdentity(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw BootstrapException.Unavailable();
        var id = ReadString(root, "id");
        var email = root.TryGetProperty("traits", out var traits) ? ReadString(traits, "email") : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(email)) throw BootstrapException.Unavailable();
        return new(id, email);
    }
    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string EscapeId(string id) =>
        Guid.TryParse(id, out var guid) ? guid.ToString() : throw BootstrapException.Conflict();
}
