using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Registry;

public sealed class RegistryClient(HttpClient http, IOptions<RegistryOptions> options, RegistryTokens tokens, TimeProvider clock)
{
    private readonly RegistryOptions _options = options.Value;

    public async Task PingAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "/v2/", "maintenance");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public HttpRequestMessage Request(HttpMethod method, string path, string repository, bool write = false)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(_options.BackendUrl.TrimEnd('/') + "/"), path.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Issue("minicloud-internal", repository, write, clock.GetUtcNow().AddMinutes(5), delete: method == HttpMethod.Delete));
        request.Headers.TryAddWithoutValidation("Accept", RegistryImageValidator.OciManifest + ", " + RegistryImageValidator.DockerManifest);
        return request;
    }

    public async Task DeleteAsync(string repository, string kind, string identity, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Delete, $"/v2/{repository}/{kind}/{identity}", repository, true);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        { throw new RegistryImageValidationException(ImagePublicationErrors.RegistryUnavailable, "Registry cleanup failed; writes remain fenced."); }
    }

    public async Task<byte[]> ReadAsync(string repository, string kind, string identity, int limit, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"/v2/{repository}/{kind}/{identity}", repository);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { throw new RegistryImageValidationException(ImagePublicationErrors.Incomplete, "Registry image content is unavailable."); }
        if (response.Content.Headers.ContentLength > limit) { throw new RegistryImageValidationException(ImagePublicationErrors.Unsupported, "Registry document exceeds its limit."); }
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit) { throw new RegistryImageValidationException(ImagePublicationErrors.Unsupported, "Registry document exceeds its limit."); }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public async Task<(VerifiedRegistryImage Image, byte[] Manifest, byte[] Config)> VerifyAsync(string repository, string digest, string platform, CancellationToken ct)
    {
        if (!ImagePublicationValidation.IsDigest(digest)) { throw new RegistryImageValidationException(ImagePublicationErrors.Unsupported, "Invalid image digest."); }
        var manifest = await ReadAsync(repository, "manifests", digest, ImagePublicationValidation.MaxManifestBytes, ct);
        using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 32 });
        var configDigest = document.RootElement.GetProperty("config").GetProperty("digest").GetString();
        if (!ImagePublicationValidation.IsDigest(configDigest)) { throw new RegistryImageValidationException(ImagePublicationErrors.Unsupported, "Invalid config digest."); }
        var config = await ReadAsync(repository, "blobs", configDigest!, ImagePublicationValidation.MaxConfigBytes, ct);
        var image = RegistryImageValidator.Verify(manifest, config, digest, platform);
        foreach (var layer in image.Layers.DistinctBy(x => x.Digest))
        {
            using var request = Request(HttpMethod.Head, $"/v2/{repository}/blobs/{layer.Digest}", repository);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength != layer.Size)
            {
                throw new RegistryImageValidationException(ImagePublicationErrors.Incomplete, "Image layer is missing or has an incorrect size.");
            }
        }
        return (image, manifest, config);
    }
}
