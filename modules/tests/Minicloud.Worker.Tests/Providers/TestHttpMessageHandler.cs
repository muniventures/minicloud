using System.Net;

namespace Minicloud.Worker.Tests.Providers;

public sealed class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly List<RecordedRequest> _recordedRequests = new();

    public IReadOnlyList<RecordedRequest> RecordedRequests => _recordedRequests;

    public void QueueResponse(HttpResponseMessage response)
    {
        _responses.Enqueue(_ => response);
    }

    public void QueueResponse(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _responses.Enqueue(responseFactory);
    }

    public void QueueJsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        QueueResponse(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Headers.Authorization?.ToString(),
            request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null);

        _recordedRequests.Add(recorded);

        if (_responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var factory = _responses.Dequeue();
        return factory(request);
    }
}

public record RecordedRequest(
    HttpMethod Method,
    string Uri,
    string? AuthorizationHeader,
    string? Body);
