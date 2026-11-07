using Minicloud.RuntimeAgent;

const string defaultConfigPath = "/opt/minicloud/agent/config.json";
var configPath = args.Length > 0 ? args[0] : defaultConfigPath;
var config = AgentConfig.Load(configPath);
var token = (await File.ReadAllTextAsync(config.TokenPath)).Trim();
var cursorStore = new CursorStore(config.StatePath);
await cursorStore.LoadAsync(CancellationToken.None);

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(15)
};
var api = new RuntimeApiClient(httpClient, config, token);
var collector = new RuntimeCollector(config, new ProcessRunner());
var routing = new RoutingApplier(config, new ProcessRunner());

while (true)
{
    const string agentVersion = "0.2.0";
    await AgentOperations.TryAsync("heartbeat", () => api.SendHeartbeatAsync(agentVersion, CancellationToken.None));
    await AgentOperations.TryAsync("status", async () =>
    {
        var status = await collector.CollectStatusAsync(agentVersion, CancellationToken.None);
        await api.SendStatusAsync(status, CancellationToken.None);
    });
    await AgentOperations.TryAsync("routing", async () =>
    {
        var desired = await api.GetRoutingAsync(CancellationToken.None);
        var results = await routing.ApplyAsync(desired, CancellationToken.None);
        await api.SendRoutingApplyResultsAsync(new RuntimeRoutingApplyResultsRequest(desired.Version, results), CancellationToken.None);
    });
    await AgentOperations.TryAsync("logs", async () =>
    {
        var logs = await collector.CollectLogsAsync(CancellationToken.None);
        if (logs.Count > 0)
        {
            var response = await api.SendLogsAsync(logs, CancellationToken.None);
            await cursorStore.SaveAsync(response.LastAcceptedCursorBySource, CancellationToken.None);
        }
    });
    await Task.Delay(TimeSpan.FromSeconds(15));
}
