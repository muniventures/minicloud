using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minicloud.RuntimeAgent;

public sealed class RuntimeCollector(AgentConfig config, IProcessRunner processRunner)
{
    private async Task<JsonDocument> InspectAsync(CancellationToken cancellationToken)
    {
        var list = await processRunner.RunAsync("docker", ["ps", "-aq", "--filter", $"label=com.docker.compose.project={config.ComposeProject}"], cancellationToken);
        RequireSuccess(list, "container discovery");
        var ids = Lines(list.Stdout).ToArray();
        if (ids.Length == 0)
        {
            return JsonDocument.Parse("[]");
        }
        var inspect = await processRunner.RunAsync("docker", new[] { "inspect" }.Concat(ids).ToArray(), cancellationToken);
        RequireSuccess(inspect, "container inspection");
        return JsonDocument.Parse(inspect.Stdout);
    }

    public async Task<IReadOnlyList<AgentLogRecord>> CollectLogsAsync(CancellationToken cancellationToken)
    {
        var records = new List<AgentLogRecord>();
        var now = DateTimeOffset.UtcNow;
        using var containers = await InspectAsync(cancellationToken);
        foreach (var container in containers.RootElement.EnumerateArray())
        {
            var service = ServiceName(container);
            var logs = await processRunner.RunAsync("docker", ["logs", "--timestamps", "--since", "20s", "--tail", "100", container.GetProperty("Id").GetString()!], cancellationToken);
            if (logs.ExitCode != 0)
            {
                records.Add(SelfLog("minicloud-agent", $"Log collection failed for {service}.", now));
                continue;
            }
            foreach (var (output, stream) in new[] { (logs.Stdout, "stdout"), (logs.Stderr, "stderr") })
            {
                foreach (var line in Lines(output))
                {
                    var separator = line.IndexOf(' ');
                    var tagged = separator > 0 ? line.Insert(separator + 1, stream + " ") : line;
                    records.AddRange(LogParsing.ParseDockerLine(service, $"service:{service}", StableSequence(tagged), tagged, now));
                }
            }
        }
        var caddyLogs = await processRunner.RunAsync("journalctl", ["-u", "caddy", "--since", "20 seconds ago", "--output", "short-iso", "--no-pager"], cancellationToken);
        foreach (var line in Lines(caddyLogs.ExitCode == 0 ? caddyLogs.Stdout : caddyLogs.Stderr))
        {
            records.AddRange(LogParsing.ParseJournalLine("caddy", "caddy", StableSequence(line), line, now));
        }
        return records.GroupBy(x => new { x.Source, x.Cursor, x.ObservedAt }).Select(x => x.First()).Take(AgentRuntimeLimits.MaxBatchRecords).ToArray();
    }

    public async Task<RuntimeStatusIngestRequest> CollectStatusAsync(string agentVersion, CancellationToken cancellationToken)
    {
        using var containers = await InspectAsync(cancellationToken);
        var statuses = containers.RootElement.EnumerateArray().Select(container =>
        {
            var state = container.GetProperty("State");
            return new RuntimeServiceStatusDto(ServiceName(container), container.GetProperty("Name").GetString()?.TrimStart('/'),
                state.GetProperty("Status").GetString()!,
                state.TryGetProperty("Health", out var health) ? health.GetProperty("Status").GetString() : null,
                container.GetProperty("Config").GetProperty("Image").GetString(), container.GetProperty("RestartCount").GetInt32());
        }).ToArray();
        var caddy = await processRunner.RunAsync("systemctl", ["is-active", "caddy"], cancellationToken);
        return new RuntimeStatusIngestRequest(agentVersion, DateTimeOffset.UtcNow, config.ComposeProject, statuses,
            new RuntimeCaddyStatusDto(caddy.ExitCode == 0 && caddy.Stdout.Trim() == "active"));
    }

    private static string ServiceName(JsonElement container) => container.GetProperty("Config").GetProperty("Labels").GetProperty("com.docker.compose.service").GetString()!;
    private static void RequireSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker {operation} failed (exit {result.ExitCode}).");
        }
    }
    private static IEnumerable<string> Lines(string value) => value.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static AgentLogRecord SelfLog(string source, string message, DateTimeOffset observedAt) => new(source, null, "stderr", observedAt, $"{source}:{StableSequence(message):000000000000}", RuntimeLogRedactor.Redact(message));
    private static long StableSequence(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Math.Abs(BitConverter.ToInt64(hash, 0) % 1_000_000_000_000);
    }
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken);
}

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            throw;
        }
    }
}

public sealed record RuntimeStatusIngestRequest(
    string AgentVersion,
    DateTimeOffset ObservedAt,
    string ComposeProject,
    IReadOnlyList<RuntimeServiceStatusDto> Services,
    RuntimeCaddyStatusDto Caddy);

public sealed record RuntimeServiceStatusDto(
    string Name,
    string? Container,
    string State,
    string? Health,
    string? Image,
    int? RestartCount);

public sealed record RuntimeCaddyStatusDto(bool Active);
