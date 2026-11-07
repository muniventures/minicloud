using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Minicloud.RuntimeAgent;

public sealed class RuntimeApiClient(HttpClient httpClient, AgentConfig config, string token)
{
    public async Task<RuntimeLogIngestResponse> SendLogsAsync(IReadOnlyList<AgentLogRecord> records, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.ApiBaseUrl.TrimEnd('/')}/v1/runtime/ingest/logs")
        {
            Content = JsonContent.Create(new RuntimeLogIngestRequest(records), options: JsonDefaults.Options)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RuntimeLogIngestResponse>(JsonDefaults.Options, cancellationToken)
            ?? new RuntimeLogIngestResponse(0, records.Count, new Dictionary<string, string>());
    }

    public async Task SendHeartbeatAsync(string agentVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.ApiBaseUrl.TrimEnd('/')}/v1/runtime/heartbeat")
        {
            Content = JsonContent.Create(new RuntimeHeartbeatRequest(agentVersion, DateTimeOffset.UtcNow), options: JsonDefaults.Options)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendStatusAsync(RuntimeStatusIngestRequest status, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.ApiBaseUrl.TrimEnd('/')}/v1/runtime/ingest/status")
        {
            Content = JsonContent.Create(status, options: JsonDefaults.Options)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<RuntimeDesiredRoutingResponse> GetRoutingAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.ApiBaseUrl.TrimEnd('/')}/v1/runtime/routing");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RuntimeDesiredRoutingResponse>(JsonDefaults.Options, cancellationToken)
            ?? new RuntimeDesiredRoutingResponse("", config.AppId, config.ComposeProject, []);
    }

    public async Task SendRoutingApplyResultsAsync(RuntimeRoutingApplyResultsRequest results, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.ApiBaseUrl.TrimEnd('/')}/v1/runtime/routing/apply-results")
        {
            Content = JsonContent.Create(results, options: JsonDefaults.Options)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

public sealed record RuntimeLogIngestRequest(IReadOnlyList<AgentLogRecord> Records);
public sealed record RuntimeLogIngestResponse(int Accepted, int Rejected, IReadOnlyDictionary<string, string> LastAcceptedCursorBySource);
public sealed record RuntimeHeartbeatRequest(string AgentVersion, DateTimeOffset ObservedAt);
public sealed record RuntimeDesiredRoutingResponse(string Version, string AppId, string ComposeProject, IReadOnlyList<RuntimeDesiredRoute> Routes);
public sealed record RuntimeDesiredRoute(string BindingId, string Hostname, string PathPrefix, string ServiceName, int ServicePort);
public sealed record RuntimeRoutingApplyResultsRequest(string Version, IReadOnlyList<RuntimeRoutingApplyResult> Results);
public sealed record RuntimeRoutingApplyResult(string BindingId, string ApplyStatus, string SslStatus, string? FailureCode, string? FailureMessage, DateTimeOffset ObservedAt);
