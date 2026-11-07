// SPDX-License-Identifier: MPL-2.0

using System.Text;
using System.Text.Json;

namespace Minicloud.Cli.Rendering;

internal sealed class DockerBuildProgress(Action<string> activity, Action<string> log, Func<string, string> redact)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (Decoder Decoder, StringBuilder Pending)> _streams = [];

    public void Process(string line)
    {
        lock (_lock)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { log(redact(line)); return; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { log(redact(line)); return; }
                if (root.TryGetProperty("vertexes", out var vertices) && vertices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var vertex in vertices.EnumerateArray())
                    {
                        if (vertex.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                            vertex.TryGetProperty("started", out _) && !vertex.TryGetProperty("completed", out _))
                        { activity(redact(name.GetString()!)); }
                        if (vertex.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                        { log(redact(error.GetString()!)); }
                    }
                }
                if (root.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                {
                    foreach (var status in statuses.EnumerateArray())
                    {
                        if (status.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                            id.GetString()!.Contains("pushing", StringComparison.OrdinalIgnoreCase))
                        { activity("Pushing image layers"); }
                    }
                }
                if (root.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in logs.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) { continue; }
                        byte[] bytes;
                        try { bytes = data.GetBytesFromBase64(); }
                        catch (FormatException) { log("Docker emitted an unreadable log event."); continue; }
                        if (!entry.TryGetProperty("vertex", out var vertex) || vertex.ValueKind != JsonValueKind.String ||
                            !entry.TryGetProperty("stream", out var channel) || !channel.TryGetInt32(out var channelNumber))
                        { continue; }
                        var key = $"{vertex.GetString()}:{channelNumber}";
                        if (!_streams.TryGetValue(key, out var stream))
                        { stream = (Encoding.UTF8.GetDecoder(), new StringBuilder()); _streams.Add(key, stream); }
                        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                        var count = stream.Decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
                        stream.Pending.Append(chars, 0, count);
                        Drain(stream.Pending);
                    }
                }
                if (root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
                {
                    foreach (var warning in warnings.EnumerateArray())
                    {
                        if (warning.TryGetProperty("short", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            try { log("Warning: " + redact(Encoding.UTF8.GetString(text.GetBytesFromBase64()))); }
                            catch (FormatException) { log("Warning: " + redact(text.GetString()!)); }
                        }
                    }
                }
            }
        }
    }

    private void Drain(StringBuilder pending)
    {
        while (pending.Length > 0)
        {
            var text = pending.ToString();
            var end = text.IndexOfAny(['\n', '\r']);
            if (end < 0) { break; }
            if (end > 0) { log(redact(text[..end])); }
            pending.Remove(0, end + 1);
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            foreach (var stream in _streams.Values)
            {
                if (stream.Pending.Length > 0) { log(redact(stream.Pending.ToString())); stream.Pending.Clear(); }
            }
        }
    }
}
