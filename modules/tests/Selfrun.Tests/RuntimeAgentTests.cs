using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Selfrun.RuntimeAgent;

namespace Selfrun.Tests;

public sealed class RuntimeAgentTests
{
    [Fact]
    public void ParseDockerLine_SplitsMultilineContentAndPersistsCursorShape()
    {
        var observedAt = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);
        var records = LogParsing.ParseDockerLine("api", "service:api", 7, "2026-05-04T10:00:01Z stderr first line\nsecond line", observedAt);

        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal("service:api", record.Source));
        Assert.All(records, record => Assert.Equal("api", record.Service));
        Assert.All(records, record => Assert.Equal("stderr", record.Stream));
        Assert.Equal("service:api:000000000007", records[0].Cursor);
        Assert.Equal("service:api:000000000008", records[1].Cursor);
        Assert.Equal("first line", records[0].Message);
        Assert.Equal("second line", records[1].Message);
    }

    [Theory]
    [InlineData("2026-05-04T12:00:01+02:00 stderr \tfirst\r\n\r\nlast \t", "stderr", true, "first", "", "last")]
    [InlineData("2026-05-04T10:00:01Z  stderr message", "stdout", false, "2026-05-04T10:00:01Z  stderr message")]
    [InlineData("invalid stderr first\r\nsecond\n", "stdout", false, "invalid stderr first", "second", "")]
    [InlineData("2026-05-04T10:00:01Z stdout", "stdout", false, "2026-05-04T10:00:01Z stdout")]
    [InlineData("", "stdout", false, "")]
    public void DockerParsingPreservesWhitespaceFallbackAndEmptyLogicalLines(string input, string stream, bool timestamped, params string[] messages)
    {
        var fallback = DateTimeOffset.UnixEpoch;
        var records = LogParsing.ParseDockerLine("api", "service:api", 7, input, fallback);
        Assert.Equal(messages, records.Select(x => x.Message));
        Assert.All(records, x => Assert.Equal(stream, x.Stream));
        Assert.All(records, x => Assert.Equal(timestamped ? new DateTimeOffset(2026, 5, 4, 10, 0, 1, TimeSpan.Zero) : fallback, x.ObservedAt));
        Assert.Equal(Enumerable.Range(7, messages.Length).Select(n => $"service:api:{n:000000000000}"), records.Select(x => x.Cursor));
    }

    [Fact]
    public void JournalParsingPreservesBareCarriageReturnsAndTruncatesBeforeRedaction()
    {
        var longLine = new string('x', AgentRuntimeLimits.MaxLineLength - 8) + " token=example-token";
        var records = LogParsing.ParseJournalLine("caddy", "caddy", 10,
            $"2026-05-04T10:00:01Z {longLine}\r\nnext\rvalue\r\n", DateTimeOffset.UnixEpoch);
        Assert.Equal(3, records.Count);
        Assert.Equal(RuntimeLogRedactor.Redact(longLine[..AgentRuntimeLimits.MaxLineLength]), records[0].Message);
        Assert.Equal("next\rvalue", records[1].Message);
        Assert.Equal("", records[2].Message);
        Assert.Equal(["caddy:000000000010", "caddy:000000000011", "caddy:000000000012"], records.Select(x => x.Cursor));
    }

    [Fact]
    public void JournalParsingSuppressesNoEntriesSentinel()
    {
        var records = LogParsing.ParseJournalLine("caddy", "caddy", 10,
            "-- No entries --", DateTimeOffset.UnixEpoch);

        Assert.Empty(records);
    }

    [Fact]
    public async Task CursorStore_ReloadsAcceptedCursors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"selfrun-agent-state-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CursorStore(path);
            await store.SaveAsync(new Dictionary<string, string> { ["service:api"] = "service:api:000000000010" }, CancellationToken.None);

            var reloaded = new CursorStore(path);
            await reloaded.LoadAsync(CancellationToken.None);

            Assert.Equal("service:api:000000000010", reloaded.Cursors["service:api"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CursorStore_SkipsUnchangedAcknowledgementsAfterRestart()
    {
        var root = Directory.CreateTempSubdirectory("selfrun-agent-state-");
        try
        {
            var path = Path.Combine(root.FullName, "state.json");
            var initial = new Dictionary<string, string> { ["service:api"] = "api:2", ["caddy"] = "caddy:1" };
            await new CursorStore(path).SaveAsync(initial, CancellationToken.None);
            var store = new CursorStore(path);
            await store.LoadAsync(CancellationToken.None);

            var temp = Directory.CreateDirectory(path + ".tmp");
            await store.SaveAsync(new Dictionary<string, string> { ["service:api"] = "api:2" }, CancellationToken.None);
            await store.SaveAsync(new Dictionary<string, string>(), CancellationToken.None);
            temp.Delete();

            await store.SaveAsync(new Dictionary<string, string> { ["service:api"] = "api:3" }, CancellationToken.None);
            var reloaded = new CursorStore(path);
            await reloaded.LoadAsync(CancellationToken.None);
            Assert.Equal("api:3", reloaded.Cursors["service:api"]);
            Assert.Equal("caddy:1", reloaded.Cursors["caddy"]);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task CursorStore_RetriesFailedWritesEvenWhenNextAcknowledgementIsUnchanged()
    {
        var root = Directory.CreateTempSubdirectory("selfrun-agent-state-");
        try
        {
            var path = Path.Combine(root.FullName, "state.json");
            var store = new CursorStore(path);
            var acknowledgement = new Dictionary<string, string> { ["service:api"] = "api:1" };
            var temp = Directory.CreateDirectory(path + ".tmp");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync(acknowledgement, CancellationToken.None));
            Assert.False(File.Exists(path));
            temp.Delete();

            await store.SaveAsync(acknowledgement, CancellationToken.None);
            var reloaded = new CursorStore(path);
            await reloaded.LoadAsync(CancellationToken.None);
            Assert.Equal("api:1", reloaded.Cursors["service:api"]);
            Assert.Equal(1, store.WriteCount);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task CursorStore_TracksWritesSkipsByteIdenticalPayloadsAndPrunesInactiveSources()
    {
        var root = Directory.CreateTempSubdirectory("selfrun-agent-cursor-opt-");
        try
        {
            var path = Path.Combine(root.FullName, "state.json");
            var store = new CursorStore(path);

            await store.SaveAsync(new Dictionary<string, string>
            {
                ["service:web"] = "web:1",
                ["caddy"] = "caddy:1",
                ["service:old-gen"] = "old:9"
            }, CancellationToken.None);
            Assert.Equal(1, store.WriteCount);
            Assert.Equal(0, store.SkippedWriteCount);
            var bytesAfterFirst = await File.ReadAllBytesAsync(path);
            Assert.Equal(bytesAfterFirst.Length, store.BytesWritten);

            // Identical acknowledgement in a different key order must skip disk write.
            await store.SaveAsync(new Dictionary<string, string>
            {
                ["caddy"] = "caddy:1",
                ["service:old-gen"] = "old:9",
                ["service:web"] = "web:1"
            }, CancellationToken.None);
            Assert.Equal(1, store.WriteCount);
            Assert.Equal(1, store.SkippedWriteCount);

            // Prune stale churn source and verify file shrinks cleanly while active cursors survive.
            var pruned = store.PruneInactiveSources(new HashSet<string>(StringComparer.Ordinal) { "service:web", "caddy" });
            Assert.Equal(1, pruned);
            Assert.Equal(0, store.PruneInactiveSources(new HashSet<string>(StringComparer.Ordinal) { "service:web", "caddy" }));

            await store.SaveAsync(new Dictionary<string, string>(), CancellationToken.None);
            Assert.Equal(2, store.WriteCount);
            var bytesAfterPrune = await File.ReadAllBytesAsync(path);
            Assert.True(bytesAfterPrune.Length < bytesAfterFirst.Length);

            var reloaded = new CursorStore(path);
            await reloaded.LoadAsync(CancellationToken.None);
            Assert.Equal(2, reloaded.Cursors.Count);
            Assert.Equal("web:1", reloaded.Cursors["service:web"]);
            Assert.Equal("caddy:1", reloaded.Cursors["caddy"]);
            Assert.False(reloaded.Cursors.ContainsKey("service:old-gen"));

            // Reloaded store must also skip identical acknowledgements.
            await reloaded.SaveAsync(new Dictionary<string, string> { ["caddy"] = "caddy:1" }, CancellationToken.None);
            Assert.Equal(0, reloaded.WriteCount);
            Assert.Equal(1, reloaded.SkippedWriteCount);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void Redactor_RemovesCommonSecretShapesBeforeEgress()
    {
        var redacted = RuntimeLogRedactor.Redact("Authorization: Bearer abc.def password=hunter2 token=secret api_key=key");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=key", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    private const string Containers = """
        [{"Id":"abc","Name":"/selfrun-demo-api-1","Config":{"Image":"api:v1","Labels":{"com.docker.compose.service":"api"}},"State":{"Status":"running","Health":{"Status":"healthy"}},"RestartCount":2},
         {"Id":"def","Name":"/selfrun-demo-worker-1","Config":{"Image":"worker:v1","Labels":{"com.docker.compose.service":"worker"}},"State":{"Status":"exited"},"RestartCount":0}]
        """;
    private const string DockerPs = "docker ps -aq --filter label=com.docker.compose.project=selfrun-demo";
    private const string RoutingHelper = "sudo -n /opt/selfrun/agent/apply-selfrun-routing";

    [Fact]
    public async Task CollectorReadsPrivateAndStoppedContainersWithoutComposeFile()
    {
        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing/app.compose.json", "/tmp/token", "/tmp/state");
        var runner = new RecordingProcessRunner(new Dictionary<string, ProcessResult>
        {
            [DockerPs] = new(0, "abc\ndef", ""),
            ["docker inspect abc def"] = new(0, Containers, ""),
            ["systemctl is-active caddy"] = new(0, "active", ""),
            ["docker logs --timestamps --since 20s --tail 100 abc"] = new(0, "2026-05-04T10:00:01Z booted", ""),
            ["docker logs --timestamps --since 20s --tail 100 def"] = new(0, "", ""),
            ["journalctl -u caddy --since 20 seconds ago --output short-iso --no-pager"] = new(0, "", "")
        });
        using var collector = new RuntimeCollector(config, runner);
        var status = await collector.CollectStatusAsync("0.2.0", CancellationToken.None);
        Assert.Equal(2, status.Services.Count);
        Assert.Equal("healthy", status.Services[0].Health);
        Assert.Equal(2, status.Services[0].RestartCount);
        Assert.Equal("exited", status.Services[1].State);
        Assert.Null(status.Services[1].Health);
        Assert.True(status.Caddy.Active);
        var logs = await collector.CollectLogsAsync(CancellationToken.None);
        Assert.Contains(logs, log => log.Service == "api" && log.Message == "booted");
        Assert.Single(runner.Calls, call => call == DockerPs);
        Assert.Single(runner.Calls, call => call == "docker inspect abc def");
    }

    [Fact]
    public async Task CollectorReportsOnlyConfiguredNonProbeHostnamesAndSendsNoRequestDetails()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var older = observedAt.AddSeconds(-5);
        var newest = observedAt.AddSeconds(-1);
        static string AccessLog(DateTimeOffset time, string host, string path, string address, bool probe = false)
        {
            var headers = probe
                ? new Dictionary<string, string[]> { ["X-Selfrun-Health-Probe"] = ["true"] }
                : new Dictionary<string, string[]>();
            var json = JsonSerializer.Serialize(new
            {
                level = "info",
                ts = time.ToUnixTimeMilliseconds() / 1000d,
                logger = "http.log.access",
                request = new { host, uri = path, remote_ip = address, headers }
            });
            return $"2026-11-28T14:00:00Z caddy[1]: {json}";
        }

        var caddyAccessLogs = string.Join('\n', new[]
        {
            AccessLog(older, "allowed.example.com:443", "/private?token=secret", "192.0.2.44"),
            AccessLog(newest, "allowed.example.com", "/health", "192.0.2.45"),
            AccessLog(observedAt, "unknown.example.com", "/unknown", "192.0.2.46"),
            AccessLog(observedAt, "203.0.113.4", "/ip", "192.0.2.47"),
            AccessLog(observedAt, "allowed.example.com", "/health", "192.0.2.48", probe: true)
        });
        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", "/tmp/state");
        var runner = new RecordingProcessRunner(new Dictionary<string, ProcessResult>
        {
            [DockerPs] = new(0, "", ""),
            ["systemctl is-active caddy"] = new(0, "active", ""),
            ["journalctl -u caddy --since 20 seconds ago --output short-iso --no-pager"] = new(0, caddyAccessLogs, "")
        });
        using var collector = new RuntimeCollector(config, runner);

        var status = await collector.CollectStatusAsync("0.2.0", CancellationToken.None, ["allowed.example.com"]);
        var observation = Assert.Single(status.HttpRequests!);
        Assert.Equal("allowed.example.com", observation.Hostname);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(newest.ToUnixTimeMilliseconds()), observation.LastRequestAt);

        var payload = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("/private?token=secret", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Selfrun-Health-Probe", payload, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(payload);
        var serializedObservation = Assert.Single(document.RootElement.GetProperty("httpRequests").EnumerateArray());
        Assert.Equal(["hostname", "lastRequestAt"], serializedObservation.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));

        var logs = await collector.CollectLogsAsync(CancellationToken.None);
        Assert.DoesNotContain(logs, record => record.Message.Contains("http.log.access", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, 0, "")]
    [InlineData(false, 1, "")]
    [InlineData(false, 0, "{}")]
    public async Task DiscoveryCachesEmptyHostsButRetriesFailedInspection(bool emptyHost, int exitCode, string inspection)
    {
        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", "/tmp/state");
        var responses = new Dictionary<string, ProcessResult>
        {
            [DockerPs] = new(0, emptyHost ? "" : "abc", ""),
            ["docker inspect abc"] = new(exitCode, inspection, ""),
            ["systemctl is-active caddy"] = new(0, "active", ""),
            ["journalctl -u caddy --since 20 seconds ago --output short-iso --no-pager"] = new(0, "journal line", "")
        };
        var runner = new RecordingProcessRunner(responses);
        using var collector = new RuntimeCollector(config, runner);
        if (emptyHost)
        {
            Assert.Empty((await collector.CollectStatusAsync("0.2.0", CancellationToken.None)).Services);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => collector.CollectStatusAsync("0.2.0", CancellationToken.None));
            responses[DockerPs] = new(0, "", "");
        }

        Assert.Equal("journal line", Assert.Single(await collector.CollectLogsAsync(CancellationToken.None)).Message);
        Assert.Equal(emptyHost ? 1 : 2, runner.Calls.Count(call => call == DockerPs));
        Assert.Equal(emptyHost ? 0 : 1, runner.Calls.Count(call => call.StartsWith("docker inspect", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DiscoveryRefreshesOnNextCycleAfterContainerChurn()
    {
        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", "/tmp/state");
        var responses = new Dictionary<string, ProcessResult>
        {
            [DockerPs] = new(0, "abc\ndef", ""),
            ["docker inspect abc def"] = new(0, Containers, ""),
            ["systemctl is-active caddy"] = new(0, "active", ""),
            ["docker logs --timestamps --since 20s --tail 100 abc"] = new(1, "", "No such container"),
            ["docker logs --timestamps --since 20s --tail 100 def"] = new(0, "2026-05-04T10:00:01Z worker alive", ""),
            ["journalctl -u caddy --since 20 seconds ago --output short-iso --no-pager"] = new(0, "journal alive", "")
        };
        var runner = new RecordingProcessRunner(responses);
        using (var collector = new RuntimeCollector(config, runner))
        {
            Assert.Equal(2, (await collector.CollectStatusAsync("0.2.0", CancellationToken.None)).Services.Count);
            responses[DockerPs] = new(0, "", "");
            var logs = await collector.CollectLogsAsync(CancellationToken.None);
            Assert.Equal(["selfrun-agent", "service:worker", "caddy"], logs.Select(log => log.Source));
        }

        using var next = new RuntimeCollector(config, runner);
        Assert.Empty((await next.CollectStatusAsync("0.2.0", CancellationToken.None)).Services);
        Assert.Equal(2, runner.Calls.Count(call => call == DockerPs));
    }

    [Fact]
    public async Task OverlappingLogReadsKeepSameTimestampRecordsAndStreamIdentity()
    {
        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", "/tmp/state");
        const string first = "2026-05-04T10:00:01Z shared token=some-value";
        const string second = "2026-05-04T10:00:01Z another event";
        var runner = new FakeProcessRunner(new Dictionary<string, ProcessResult>
        {
            [DockerPs] = new(0, "abc\ndef", ""),
            ["docker inspect abc def"] = new(0, Containers, ""),
            ["docker logs --timestamps --since 20s --tail 100 abc"] = new(0, $"{first}\n{second}\n{first}", first),
            ["docker logs --timestamps --since 20s --tail 100 def"] = new(0, "", ""),
            ["journalctl -u caddy --since 20 seconds ago --output short-iso --no-pager"] = new(0, "", "")
        });
        using var firstCycle = new RuntimeCollector(config, runner);
        using var secondCycle = new RuntimeCollector(config, runner);
        var firstRead = await firstCycle.CollectLogsAsync(CancellationToken.None);
        var secondRead = await secondCycle.CollectLogsAsync(CancellationToken.None);

        Assert.Equal(3, firstRead.Count);
        Assert.Equal(["stdout", "stdout", "stderr"], firstRead.Select(log => log.Stream));
        Assert.Equal(3, firstRead.Select(log => log.Cursor).Distinct().Count());
        Assert.All(firstRead, log => Assert.DoesNotContain("some-value", log.Message, StringComparison.Ordinal));
        Assert.Equal(firstRead.AsEnumerable(), secondRead);
    }

    [Fact]
    public async Task CollectorCapsFirstDistinctRecordsButStillReadsLaterSources()
    {
        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", "/tmp/state");
        var output = string.Join("\r\n", Enumerable.Range(1, 250).Select(n => $"  2026-05-04T10:00:01Z line {n}  "));
        var runner = new RecordingProcessRunner(new Dictionary<string, ProcessResult>
        {
            [DockerPs] = new(0, " \r\nabc\r\ndef\r\n ", ""),
            ["docker inspect abc def"] = new(0, Containers, ""),
            ["docker logs --timestamps --since 20s --tail 100 abc"] = new(0, output + "\n2026-05-04T10:00:01Z line 1\n \t\n", output),
            ["docker logs --timestamps --since 20s --tail 100 def"] = new(0, "2026-05-04T10:00:01Z later service", ""),
            ["journalctl -u caddy --since 20 seconds ago --output short-iso --no-pager"] = new(0, "2026-05-04T10:00:01Z later journal", "")
        });
        using var collector = new RuntimeCollector(config, runner);
        var records = await collector.CollectLogsAsync(CancellationToken.None);
        Assert.Equal(500, records.Count);
        Assert.All(records, x => Assert.Equal("service:api", x.Source));
        Assert.Equal(Enumerable.Range(1, 250).Select(n => $"line {n}"), records.Take(250).Select(x => x.Message));
        Assert.Equal(records.Take(250).Select(x => x.Message), records.Skip(250).Select(x => x.Message));
        Assert.All(records.Take(250), x => Assert.Equal("stdout", x.Stream));
        Assert.All(records.Skip(250), x => Assert.Equal("stderr", x.Stream));
        Assert.Equal(500, records.Select(x => x.Cursor).Distinct().Count());
        Assert.Contains("docker logs --timestamps --since 20s --tail 100 def", runner.Calls);
        Assert.Contains("journalctl -u caddy --since 20 seconds ago --output short-iso --no-pager", runner.Calls);
    }

    [Fact]
    public async Task CollectionFailureDoesNotBecomeEmptySuccessfulSnapshot()
    {
        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", "/tmp/state");
        using var collector = new RuntimeCollector(config, new FakeProcessRunner(new Dictionary<string, ProcessResult>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.CollectStatusAsync("0.2.0", CancellationToken.None));
        var reached = false;
        await AgentOperations.TryAsync("routing", () => throw new IOException("Denied"));
        await AgentOperations.TryAsync("heartbeat", () => { reached = true; return Task.CompletedTask; });
        Assert.True(reached);
    }

    [Fact]
    public async Task DiagnosticsRecordBoundedOutcomesAndSyntheticCollectionMetrics()
    {
        var points = new ConcurrentBag<MetricPoint>();
        using var listener = CreateListener(points, RuntimeAgentDiagnostics.MeterName);

        await AgentOperations.TryAsync("heartbeat", () => Task.CompletedTask);
        await AgentOperations.TryAsync("status", () => throw new IOException("failed"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await AgentOperations.TryAsync("routing", () => Task.FromCanceled(cancelled.Token));

        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", "/tmp/state");
        var runner = new FakeProcessRunner(new Dictionary<string, ProcessResult>
        {
            ["docker ps -aq --filter label=com.docker.compose.project=selfrun-demo"] = new(0, "abc", ""),
            ["docker inspect abc"] = new(0, Containers, ""),
            ["docker logs --timestamps --since 20s --tail 100 abc"] = new(0, "2026-05-04T10:00:01Z booted", ""),
            ["docker logs --timestamps --since 20s --tail 100 def"] = new(0, "", ""),
            ["journalctl -u caddy --since 20 seconds ago --output short-iso --no-pager"] = new(0, "", "")
        });
        using var collector = new RuntimeCollector(config, runner);
        await AgentOperations.TryAsync("logs", () => collector.CollectLogsAsync(CancellationToken.None), _ => false);

        Assert.Contains(points, point => point.Name == "selfrun.runtime.agent.operation.calls" && point.Operation == "heartbeat" && point.Outcome == "success");
        Assert.Contains(points, point => point.Name == "selfrun.runtime.agent.operation.calls" && point.Operation == "status" && point.Outcome == "failure");
        Assert.Contains(points, point => point.Name == "selfrun.runtime.agent.operation.calls" && point.Operation == "routing" && point.Outcome == "cancelled");
        Assert.Contains(points, point => point.Name == "selfrun.runtime.agent.operation.duration" && point.Operation == "logs" && point.Value is double duration && duration >= 0);
        Assert.Contains(points, point => point.Name == "selfrun.runtime.agent.child.launches" && point.Operation == "docker.inspect");
        Assert.Contains(points, point => point.Name == "selfrun.runtime.agent.bytes" && point.Operation == "docker.inspect" && (long)point.Value > 0);
        Assert.Contains(points, point => point.Name == "selfrun.runtime.agent.records" && point.Operation == "logs" && (long)point.Value > 0);
        Assert.All(points.Where(point => point.Operation is not null), point =>
            Assert.Contains(point.Operation, new[] { "heartbeat", "status", "routing", "logs", "docker.ps", "docker.inspect", "docker.logs", "routing.apply", "journal.logs", "systemctl.status" }));
    }

    [Fact]
    public async Task DiagnosticsClassifyReturnedRoutingFailuresAndEmitActivities()
    {
        var points = new ConcurrentBag<MetricPoint>();
        var activities = new ConcurrentBag<Activity>();
        using var listener = CreateListener(points, RuntimeAgentDiagnostics.MeterName);
        using var activityListener = CreateActivityListener(activities, RuntimeAgentDiagnostics.ActivitySourceName);
        var root = Path.Combine(Path.GetTempPath(), $"routing-metrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", Path.Combine(root, "state.json"));
            var runner = new FakeProcessRunner(new Dictionary<string, ProcessResult>
            {
                [RoutingHelper] = new(1, "", "")
            });
            var desired = new RuntimeDesiredRoutingResponse("v1", "app_1", "selfrun-demo", [new("dom", "demo.app.muni.dev", "/", "api", 8080)]);
            var routing = new RoutingApplier(config, runner, new FakeProbe(false));

            await AgentOperations.TryAsync("routing", () => routing.ReconcileAsync(desired, IgnoreResults, CancellationToken.None),
                results => results.Any(result => result.ApplyStatus == "failed"));

            Assert.Contains(points, point => point.Name == "selfrun.runtime.agent.operation.calls" && point.Operation == "routing" && point.Outcome == "failure");
            Assert.Contains(points, point => point.Name == "selfrun.runtime.agent.operation.calls" && point.Operation == "routing.apply" && point.Outcome == "failure");
            Assert.Contains(activities, activity => activity.OperationName == "routing" && activity.GetTagItem("outcome")?.ToString() == "failure");
            Assert.Contains(activities, activity => activity.OperationName == "routing.apply" && activity.GetTagItem("outcome")?.ToString() == "failure");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(0, true, "applied", "ready", "demo.app.muni.dev")]
    [InlineData(0, true, "applied", "ready", "example.com")]
    [InlineData(0, true, "applied", "ready", "api.example.com")]
    [InlineData(0, false, "applied", "pending")]
    [InlineData(1, true, "failed", "pending")]
    public async Task RoutingReportsApplyAndCertificateResults(int exitCode, bool ready, string applyStatus, string sslStatus, string hostname = "demo.app.muni.dev")
    {
        var root = Path.Combine(Path.GetTempPath(), $"routing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", Path.Combine(root, "state.json"));
            var runner = new FakeProcessRunner(new Dictionary<string, ProcessResult>
            {
                [RoutingHelper] = new(exitCode, "", "")
            });
            var desired = new RuntimeDesiredRoutingResponse("v1", "app_1", "selfrun-demo", [new("dom", hostname, "/", "api", 8080)]);
            var result = Assert.Single(await new RoutingApplier(config, runner, new FakeProbe(ready)).ReconcileAsync(desired, IgnoreResults, CancellationToken.None));
            Assert.Equal(applyStatus, result.ApplyStatus);
            Assert.Equal(sslStatus, result.SslStatus);
            Assert.Equal(applyStatus == "failed" ? "routing_apply_failed" : ready ? null : "https_not_ready", result.FailureCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RoutingRetriesPendingTlsAndReconcilesUnchangedRoutesAfterOneMinute()
    {
        var root = Directory.CreateTempSubdirectory("selfrun-agent-routing-");
        try
        {
            var config = RoutingConfig(root);
            var clock = new ManualClock();
            var commands = new Dictionary<string, ProcessResult> { [RoutingHelper] = new(0, "", "") };
            var runner = new RecordingProcessRunner(commands);
            var probe = new FakeProbe(false);
            var routing = new RoutingApplier(config, runner, probe, clock);
            var desired = new RuntimeDesiredRoutingResponse("v1", config.AppId, config.ComposeProject, [new("dom", "example.com", "/", "api", 8080)]);
            var reports = new List<RuntimeRoutingApplyResultsRequest>();
            Task Report(RuntimeRoutingApplyResultsRequest request, CancellationToken _) { reports.Add(request); return Task.CompletedTask; }
            Task Cycle() => routing.ReconcileAsync(desired, Report, CancellationToken.None);

            await Cycle();
            Assert.Equal("pending", Assert.Single(Assert.Single(reports).Results).SslStatus);

            clock.Advance(15);
            desired = desired with { Version = "v2" };
            await Cycle();
            Assert.Single(runner.Calls);
            Assert.Single(reports);
            Assert.Equal(2, probe.Calls);

            clock.Advance(15);
            probe.Ready = true;
            await Cycle();
            Assert.Equal("ready", Assert.Single(reports[1].Results).SslStatus);
            Assert.Equal("v2", reports[1].Version);
            Assert.Single(runner.Calls);

            clock.Advance(29);
            await Cycle();
            Assert.Equal(3, probe.Calls);
            Assert.Equal(2, reports.Count);

            clock.Advance(1);
            probe.Ready = false;
            await Cycle();
            Assert.Equal(2, runner.Calls.Count);
            Assert.Equal(4, probe.Calls);
            Assert.Equal("pending", Assert.Single(reports[2].Results).SslStatus);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task RoutingReappliesOnPortChangeInvalidationAndFailureWithoutWaitingForRefresh()
    {
        var root = Directory.CreateTempSubdirectory("selfrun-agent-routing-");
        try
        {
            var config = RoutingConfig(root);
            var commands = new Dictionary<string, ProcessResult> { [RoutingHelper] = new(0, "", "") };
            var runner = new RecordingProcessRunner(commands);
            var routing = new RoutingApplier(config, runner, new FakeProbe(true), new ManualClock());
            var desired = new RuntimeDesiredRoutingResponse("v1", config.AppId, config.ComposeProject, [new("dom", "example.com", "/", "api", 8080)]);

            await routing.ReconcileAsync(desired, IgnoreResults, CancellationToken.None);
            desired = desired with { Routes = [desired.Routes[0] with { ServicePort = 8081 }] };
            commands[RoutingHelper] = new(1, "", "");
            var failed = await routing.ReconcileAsync(desired, IgnoreResults, CancellationToken.None);
            Assert.Equal("failed", Assert.Single(failed).ApplyStatus);
            Assert.Equal(2, runner.Calls.Count);

            commands[RoutingHelper] = new(0, "", "");
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                routing.ReconcileAsync(desired, (_, _) => throw new HttpRequestException(), CancellationToken.None));
            var reports = new List<RuntimeRoutingApplyResultsRequest>();
            await routing.ReconcileAsync(desired with { Version = "v2" }, (request, _) =>
            {
                reports.Add(request);
                return Task.CompletedTask;
            }, CancellationToken.None);
            Assert.Equal("v2", Assert.Single(reports).Version);
            Assert.Equal(3, runner.Calls.Count);

            routing.Invalidate();
            await routing.ReconcileAsync(desired, IgnoreResults, CancellationToken.None);
            Assert.Equal(4, runner.Calls.Count);

            desired = desired with { Routes = [] };
            commands[RoutingHelper] = new(1, "", "");
            await Assert.ThrowsAsync<InvalidOperationException>(() => routing.ReconcileAsync(desired, IgnoreResults, CancellationToken.None));
            commands[RoutingHelper] = new(0, "", "");
            Assert.Empty(await routing.ReconcileAsync(desired, IgnoreResults, CancellationToken.None));
            Assert.Equal(6, runner.Calls.Count);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task StatusPublishesChangesAndRefreshesOnlyAfterSuccessfulDelivery()
    {
        var clock = new ManualClock();
        var publisher = new RuntimeStatusPublisher(clock);
        var status = new RuntimeStatusIngestRequest("agent", clock.GetUtcNow(), "selfrun-demo", [
            new("api", "/api", "running", "healthy", "api:1", 0)], new(true));
        var sent = new List<RuntimeStatusIngestRequest>();
        Task Send(RuntimeStatusIngestRequest request, CancellationToken _) { sent.Add(request); return Task.CompletedTask; }
        Task Cycle() => publisher.PublishAsync(status with { ObservedAt = clock.GetUtcNow() }, Send, CancellationToken.None);

        await Cycle();
        clock.Advance(59);
        await Cycle();
        Assert.Single(sent);

        clock.Advance(1);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            publisher.PublishAsync(status, (_, _) => throw new HttpRequestException(), CancellationToken.None));
        await Cycle();
        Assert.Equal(2, sent.Count);
        Assert.Equal(clock.GetUtcNow(), sent[1].ObservedAt);

        clock.Advance(15);
        status = status with { Services = [status.Services[0] with { RestartCount = 1 }] };
        await Cycle();
        Assert.Equal(1, Assert.Single(sent[2].Services).RestartCount);
        status = status with { Caddy = new(false) };
        await Cycle();
        Assert.False(sent[3].Caddy.Active);

        status = status with { HttpRequests = [new("app.example.com", clock.GetUtcNow())] };
        await Cycle();
        Assert.Single(sent[4].HttpRequests!);
    }

    [Theory]
    [InlineData("{\"accepted\":0}")]
    [InlineData("{}")]
    public async Task RoutingApiDoesNotTreatPartialAcknowledgementAsSuccess(string response)
    {
        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", "/tmp/state");
        using var http = new HttpClient(new RoutingResponseHandler(response));
        var api = new RuntimeApiClient(http, config, "synthetic");
        var request = new RuntimeRoutingApplyResultsRequest("v1", [new("dom", "applied", "pending", null, null, DateTimeOffset.UtcNow)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.SendRoutingApplyResultsAsync(request, CancellationToken.None));
    }

    private sealed class RoutingResponseHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
    }

    private static AgentConfig RoutingConfig(DirectoryInfo root) =>
        new("srv", "org", "app_1", "https://api.example", "selfrun-demo", "/missing", "/tmp/token", Path.Combine(root.FullName, "state.json"));

    private static Task IgnoreResults(RuntimeRoutingApplyResultsRequest _, CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class ManualClock : TimeProvider
    {
        private long _seconds;
        public void Advance(long seconds) => _seconds += seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);
    }

    private sealed class FakeProbe(bool ready) : IHttpsProbe
    {
        public bool Ready { get; set; } = ready;
        public int Calls { get; private set; }

        public Task<bool> IsReadyAsync(string hostname, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Ready);
        }
    }

    private sealed class FakeProcessRunner(IReadOnlyDictionary<string, ProcessResult> responses) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            var key = $"{fileName} {string.Join(" ", args)}";
            return Task.FromResult(responses.TryGetValue(key, out var response) ? response : new ProcessResult(127, "", key));
        }
    }

    private sealed class RecordingProcessRunner(IReadOnlyDictionary<string, ProcessResult> responses) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            var key = $"{fileName} {string.Join(" ", args)}";
            Calls.Add(key);
            return Task.FromResult(responses.TryGetValue(key, out var response) ? response : new ProcessResult(127, "", key));
        }
    }

    private static MeterListener CreateListener(ConcurrentBag<MetricPoint> points, string meterName)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == meterName)
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            points.Add(new MetricPoint(instrument.Name, measurement, GetTag(tags, "operation"), GetTag(tags, "outcome"))));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            points.Add(new MetricPoint(instrument.Name, measurement, GetTag(tags, "operation"), GetTag(tags, "outcome"))));
        listener.Start();
        return listener;
    }

    private static string? GetTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == name)
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private static ActivityListener CreateActivityListener(ConcurrentBag<Activity> activities, string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed record MetricPoint(string Name, object Value, string? Operation, string? Outcome);
}
