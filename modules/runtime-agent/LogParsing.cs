namespace Minicloud.RuntimeAgent;

public sealed record AgentLogRecord(
    string Source,
    string? Service,
    string Stream,
    DateTimeOffset ObservedAt,
    string Cursor,
    string Message);

public static class LogParsing
{
    public static IReadOnlyList<AgentLogRecord> ParseDockerLine(string serviceName, string cursorPrefix, long sequence, string line, DateTimeOffset fallbackTimestamp)
    {
        var stream = "stdout";
        var observedAt = fallbackTimestamp;
        var content = line;

        var parts = line.Split(' ', 3, StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && DateTimeOffset.TryParse(parts[0], out var parsedTimestamp) && parts[1] is "stdout" or "stderr")
        {
            observedAt = parsedTimestamp.ToUniversalTime();
            stream = parts[1];
            content = parts[2];
        }

        return SplitLogicalLines($"service:{serviceName}", serviceName, stream, observedAt, cursorPrefix, sequence, content);
    }

    public static IReadOnlyList<AgentLogRecord> ParseJournalLine(string source, string cursorPrefix, long sequence, string line, DateTimeOffset fallbackTimestamp)
    {
        var observedAt = fallbackTimestamp;
        var content = line;
        var separator = line.IndexOf(' ');
        if (separator > 0 && DateTimeOffset.TryParse(line[..separator], out var parsedTimestamp))
        {
            observedAt = parsedTimestamp.ToUniversalTime();
            content = line[(separator + 1)..];
        }

        return SplitLogicalLines(source, null, "stdout", observedAt, cursorPrefix, sequence, content);
    }

    private static IReadOnlyList<AgentLogRecord> SplitLogicalLines(
        string source,
        string? service,
        string stream,
        DateTimeOffset observedAt,
        string cursorPrefix,
        long sequence,
        string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var records = new List<AgentLogRecord>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            records.Add(new AgentLogRecord(
                source,
                service,
                stream,
                observedAt,
                $"{cursorPrefix}:{sequence + i:000000000000}",
                RuntimeLogRedactor.Redact(Truncate(lines[i]))));
        }

        return records;
    }

    private static string Truncate(string value) =>
        value.Length <= AgentRuntimeLimits.MaxLineLength ? value : value[..AgentRuntimeLimits.MaxLineLength];
}

public static class AgentRuntimeLimits
{
    public const int MaxLineLength = 16 * 1024;
    public const int MaxBatchRecords = 500;
}
