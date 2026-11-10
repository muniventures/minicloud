using System.Net.Http.Headers;

namespace Minicloud.Worker.Storage;

public sealed class ApiHttpArtifactRetriever : IArtifactRetriever
{
    private readonly HttpClient _httpClient;

    public ApiHttpArtifactRetriever(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<Stream> OpenArtifactStreamAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);

        var requestUri = $"/api/artifacts/{Uri.EscapeDataString(artifactId)}/file";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }
}
