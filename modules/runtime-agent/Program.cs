using System.Runtime.InteropServices;
using Selfrun.RuntimeAgent;

const string defaultConfigPath = "/opt/selfrun/agent/config.json";
var configPath = args.Length > 0 ? args[0] : defaultConfigPath;
var config = AgentConfig.Load(configPath);
var token = (await File.ReadAllTextAsync(config.TokenPath)).Trim();
var cursorStore = new CursorStore(config.StatePath);
await cursorStore.LoadAsync(CancellationToken.None);

using var shutdownCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdownCts.Cancel();
};
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    shutdownCts.Cancel();
});

await using var profiler = RuntimeAgentProfiler.Start(config.Profiling);

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(15)
};
var api = new RuntimeApiClient(httpClient, config, token);
var processRunner = new ProcessRunner();
var routing = new RoutingApplier(config, processRunner);
var statusPublisher = new RuntimeStatusPublisher();
var configuredHostnames = Array.Empty<string>();
var lastHttpRequests = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

while (!shutdownCts.IsCancellationRequested)
{
    // Finish the active cycle cleanly so all started Activity spans emit stop markers before exit.
    await RunCycleAsync(CancellationToken.None);
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(15), shutdownCts.Token);
    }
    catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
    {
        break;
    }
}

if (profiler.IsEnabled)
{
    profiler.FlushSummary();
}

async Task RunCycleAsync(CancellationToken cancellationToken)
{
    const string agentVersion = "0.2.0";
    using var collector = new RuntimeCollector(config, processRunner);
    await AgentOperations.TryAsync("heartbeat", () => api.SendHeartbeatAsync(agentVersion, cancellationToken));
    RuntimeDesiredRoutingResponse? desired = null;
    await AgentOperations.TryAsync("routing", async () =>
    {
        try
        {
            desired = await api.GetRoutingAsync(cancellationToken);
            configuredHostnames = desired.Routes.Select(route => route.Hostname).ToArray();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            routing.Invalidate();
            throw;
        }
    });
    await AgentOperations.TryAsync("status", async () =>
    {
        var status = await collector.CollectStatusAsync(agentVersion, cancellationToken, configuredHostnames);
        foreach (var observation in status.HttpRequests ?? [])
        {
            if (!lastHttpRequests.TryGetValue(observation.Hostname, out var previous) || observation.LastRequestAt > previous)
            {
                lastHttpRequests[observation.Hostname] = observation.LastRequestAt;
            }
        }
        var currentHostnames = configuredHostnames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var hostname in lastHttpRequests.Keys.Where(hostname => !currentHostnames.Contains(hostname)).ToArray())
        {
            lastHttpRequests.Remove(hostname);
        }
        status = status with
        {
            HttpRequests = lastHttpRequests
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new RuntimeHttpRequestObservation(x.Key, x.Value))
                .ToArray()
        };
        await statusPublisher.PublishAsync(status, api.SendStatusAsync, cancellationToken);
    });
    if (desired is not null)
    {
        var desiredRoutes = desired;
        await AgentOperations.TryAsync("routing",
            () => routing.ReconcileAsync(desiredRoutes, api.SendRoutingApplyResultsAsync, cancellationToken),
            results => results.Any(result => result.ApplyStatus == "failed"));
    }
    await AgentOperations.TryAsync("logs", async () =>
    {
        var logs = await collector.CollectLogsAsync(cancellationToken);
        if (logs.Count > 0)
        {
            var response = await api.SendLogsAsync(logs, cancellationToken);
            await cursorStore.SaveAsync(response.LastAcceptedCursorBySource, cancellationToken);
        }
    });
}
