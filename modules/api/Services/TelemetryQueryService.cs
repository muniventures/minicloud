using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

/// <summary>
/// Typed application-log query filters. All values are validated and length-bounded;
/// free text is matched as an escaped LogQL literal, never spliced into a query.
/// </summary>
public sealed record TelemetryLogFilters(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? Service,
    string? Severity,
    string? Text,
    string? TraceId,
    int? Limit);

public abstract record TelemetryQueryOutcome;
public sealed record TelemetryQueryOk(TelemetryLogsResponse Response) : TelemetryQueryOutcome;
public sealed record TelemetryQueryInvalid(string Code, string Message) : TelemetryQueryOutcome;
public sealed record TelemetryQueryUnavailable(string Code, string Message, TimeSpan? RetryAfter) : TelemetryQueryOutcome;

/// <summary>
/// T30 control-plane read path for OTLP application logs. Authorization uses
/// ReadApplicationLogs against the app's organization resolved from storage; the
/// Loki tenant header is always selected server-side from telemetry settings.
/// Severity and trace IDs are applied as typed post-filters over the bounded Loki
/// result set because they live in structured metadata whose indexed representation
/// varies by Loki version; service and text filters are pushed into LogQL as
/// escaped literals. Returns null for unknown/inaccessible apps so the controller
/// can emit one consistent non-disclosing response.
/// </summary>
public sealed class TelemetryQueryService(
    MinicloudDbContext db,
    ControlPlaneAuthorization authorization,
    IHttpClientFactory httpClients,
    IOptions<TelemetryIngestionOptions> options,
    TimeProvider timeProvider,
    TelemetryQueryLimiter limiter,
    AuditEventService audit,
    ILogger<TelemetryQueryService> logger)
{
    internal static readonly ISet<string> Severities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"
    };

    private const int DefaultLimit = 200;
    private const int MaxServiceLength = 100;
    private const int MaxTextLength = 500;
    private const int MaxBodyBytes = 16 * 1024;

    public async Task<TelemetryQueryOutcome?> QueryAsync(
        ControlPlanePrincipal principal,
        string appId,
        TelemetryLogFilters filters,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var end = filters.End ?? now;
        var start = filters.Start ?? end.AddMinutes(-15);
        if (start >= end)
        {
            return new TelemetryQueryInvalid("validation_failed", "The start of the query window must be before its end.");
        }

        if (end - start > TelemetryLimits.MaxQueryRange)
        {
            return new TelemetryQueryInvalid("query_range_too_large", "Query windows are limited to seven days.");
        }

        var retentionCutoff = now.Subtract(TelemetryLimits.Retention);
        if (start < retentionCutoff)
        {
            return new TelemetryQueryInvalid("start_before_retention", "Query windows cannot start before the seven-day retention cutoff.");
        }

        var limit = filters.Limit ?? DefaultLimit;
        if (limit < 1 || limit > TelemetryLimits.MaxQueryEntries)
        {
            return new TelemetryQueryInvalid("validation_failed", $"Result limits must be between 1 and {TelemetryLimits.MaxQueryEntries}.");
        }

        var service = string.IsNullOrWhiteSpace(filters.Service) ? null : filters.Service.Trim();
        if (service is not null && service.Length > MaxServiceLength)
        {
            return new TelemetryQueryInvalid("validation_failed", "The service filter is too long.");
        }

        string? severity = null;
        if (!string.IsNullOrWhiteSpace(filters.Severity))
        {
            severity = filters.Severity.Trim().ToUpperInvariant();
            if (!Severities.Contains(severity))
            {
                return new TelemetryQueryInvalid("validation_failed", "Severity must be one of TRACE, DEBUG, INFO, WARN, ERROR, FATAL.");
            }
        }

        var text = string.IsNullOrWhiteSpace(filters.Text) ? null : filters.Text;
        if (text is not null && text.Length > MaxTextLength)
        {
            return new TelemetryQueryInvalid("validation_failed", "The text filter is too long.");
        }

        var traceId = string.IsNullOrWhiteSpace(filters.TraceId) ? null : filters.TraceId.Trim();
        if (traceId is not null && (traceId.Length > 32 || !IsHex(traceId)))
        {
            return new TelemetryQueryInvalid("validation_failed", "The trace ID filter must be up to 32 hexadecimal characters.");
        }

        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null || !await authorization.CanAccessOrganizationAsync(
                principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ReadApplicationLogs, cancellationToken))
        {
            return null;
        }

        var settings = await db.AppTelemetrySettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AppId == appId, cancellationToken);
        if (settings is null)
        {
            return new TelemetryQueryOk(new TelemetryLogsResponse(
                "not_configured", [], false, null, retentionCutoff, null));
        }

        if (settings.Deleting || !settings.Enabled)
        {
            return new TelemetryQueryOk(new TelemetryLogsResponse(
                "disabled", [], false, null, retentionCutoff, settings.LastAcceptedAt));
        }

        if (string.Equals(settings.ProvisioningStatus, "provisioning", StringComparison.OrdinalIgnoreCase))
        {
            return new TelemetryQueryOk(new TelemetryLogsResponse(
                "provisioning", [], false, null, retentionCutoff, settings.LastAcceptedAt));
        }

        if (string.Equals(settings.ProvisioningStatus, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return new TelemetryQueryUnavailable(
                "telemetry_unavailable", "Log storage provisioning failed; reads are unavailable.", TimeSpan.FromSeconds(30));
        }

        if (!limiter.TryEnter(appId))
        {
            return new TelemetryQueryUnavailable(
                "query_rate_limited", "Too many concurrent log queries; retry with backoff.", TimeSpan.FromSeconds(5));
        }

        try
        {
            return await QueryLokiAsync(
                app.OrganizationId, appId, principal, settings.TenantId, settings.LastAcceptedAt,
                start, end, service, severity, text, traceId, limit, retentionCutoff, cancellationToken);
        }
        finally
        {
            limiter.Release(appId);
        }
    }

    private async Task<TelemetryQueryOutcome> QueryLokiAsync(
        string organizationId,
        string appId,
        ControlPlanePrincipal principal,
        string tenantId,
        DateTimeOffset? lastAcceptedAt,
        DateTimeOffset start,
        DateTimeOffset end,
        string? service,
        string? severity,
        string? text,
        string? traceId,
        int limit,
        DateTimeOffset retentionCutoff,
        CancellationToken cancellationToken)
    {
        Uri endpoint;
        try
        {
            endpoint = new Uri(options.Value.LokiBaseAddress.TrimEnd('/') + "/");
        }
        catch (UriFormatException ex)
        {
            logger.LogError(ex, "Telemetry Loki base address is misconfigured; denying query as retryable.");
            return new TelemetryQueryUnavailable(
                "telemetry_unavailable", "Log storage is temporarily unavailable; retry with backoff.", TimeSpan.FromSeconds(5));
        }

        var query = BuildLogQuery(service, text);
        var queryUrl = new Uri(endpoint, "loki/api/v1/query_range?query=" + Uri.EscapeDataString(query)
            + "&start=" + ToUnixNanoseconds(start).ToString(CultureInfo.InvariantCulture)
            + "&end=" + ToUnixNanoseconds(end).ToString(CultureInfo.InvariantCulture)
            + "&limit=" + (limit + 1).ToString(CultureInfo.InvariantCulture)
            + "&direction=backward");

        using var timeout = new CancellationTokenSource(TelemetryLimits.QueryTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        byte[] body;
        try
        {
            var client = httpClients.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, queryUrl);
            // Tenant identity is selected server-side from authenticated state only.
            // No caller-supplied tenant header, app ID, or URL is trusted here.
            request.Headers.TryAddWithoutValidation("X-Scope-OrgID", tenantId);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Loki query rejected: status={Status} app={AppId}", (int)response.StatusCode, appId);
                return new TelemetryQueryUnavailable(
                    "telemetry_unavailable", "Log storage is temporarily unavailable; retry with backoff.", TimeSpan.FromSeconds(5));
            }

            body = await ReadBoundedAsync(response.Content, linked.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Loki query timed out for app {AppId}.", appId);
            return new TelemetryQueryUnavailable(
                "telemetry_unavailable", "Log storage timed out; retry with backoff.", TimeSpan.FromSeconds(5));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Loki query transport failure for app {AppId}.", appId);
            return new TelemetryQueryUnavailable(
                "telemetry_unavailable", "Log storage is unreachable; retry with backoff.", TimeSpan.FromSeconds(5));
        }

        List<LokiStream> streams;
        try
        {
            streams = ParseStreams(body);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Loki query returned an unreadable body for app {AppId}.", appId);
            return new TelemetryQueryUnavailable(
                "telemetry_unavailable", "Log storage returned an unreadable response; retry with backoff.", TimeSpan.FromSeconds(5));
        }

        var response_ = MapEntries(streams, service, severity, text, traceId, limit, retentionCutoff, lastAcceptedAt);
        await AuditQueryAsync(organizationId, appId, principal, start, end, response_, cancellationToken);
        return new TelemetryQueryOk(response_);
    }

    private TelemetryLogsResponse MapEntries(
        List<LokiStream> streams,
        string? service,
        string? severity,
        string? text,
        string? traceId,
        int limit,
        DateTimeOffset retentionCutoff,
        DateTimeOffset? lastAcceptedAt)
    {
        var raw = new List<TelemetryLogEntryResponse>();
        foreach (var stream in streams)
        {
            foreach (var value in stream.Values)
            {
                var entryService = stream.Labels.TryGetValue("service_name", out var labeled) && !string.IsNullOrWhiteSpace(labeled)
                    ? labeled
                    : service ?? string.Empty;
                var entrySeverity = ExtractField(stream.Metadata, value.Line, ["severity", "level"])?.ToUpperInvariant() ?? string.Empty;
                var entryTraceId = ExtractField(stream.Metadata, value.Line, ["trace_id", "traceId", "traceID"]);
                raw.Add(new TelemetryLogEntryResponse(
                    value.Timestamp, entryService, entrySeverity, entryTraceId,
                    TruncateBody(value.Line), null));
            }
        }

        var truncated = false;
        string? reason = null;
        if (raw.Count > limit)
        {
            raw.RemoveRange(limit, raw.Count - limit);
            truncated = true;
            reason = "entry_limit";
        }

        var kept = new List<TelemetryLogEntryResponse>(raw.Count);
        var filteredOut = 0;
        foreach (var entry in raw)
        {
            if (!MatchesPostFilter(entry, severity, text, traceId))
            {
                filteredOut++;
                continue;
            }

            kept.Add(entry);
        }

        if (filteredOut > 0 && truncated)
        {
            reason = "filter_limit";
        }

        var entries = new List<TelemetryLogEntryResponse>(kept.Count);
        long bytes = 0;
        foreach (var entry in kept)
        {
            bytes += entry.Body.Length + entry.Service.Length + entry.Severity.Length + (entry.TraceId?.Length ?? 0);
            if (bytes > TelemetryLimits.MaxQueryResponseBytes)
            {
                truncated = true;
                reason = "response_bytes";
                break;
            }

            entries.Add(entry);
        }

        return new TelemetryLogsResponse("ok", entries, truncated, reason, retentionCutoff, lastAcceptedAt);
    }

    private async Task AuditQueryAsync(
        string organizationId,
        string appId,
        ControlPlanePrincipal principal,
        DateTimeOffset start,
        DateTimeOffset end,
        TelemetryLogsResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            // Search text and trace IDs are excluded: audit records actor, app,
            // window, and outcome only.
            await audit.AppendAsync(
                organizationId,
                appId,
                AuditEventService.ActorFor(principal.ToActorIdentity()),
                "telemetry.logs_queried",
                $"start={start:o} end={end:o} entries={response.Entries.Count} truncated={response.Truncated}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit telemetry query for app {AppId}; returning the result.", appId);
        }
    }

    /// <summary>
    /// Builds the LogQL selector from allowlisted label names only. Customer values
    /// are embedded exclusively as escaped double-quoted literals.
    /// </summary>
    public static string BuildLogQuery(string? service, string? text)
    {
        var builder = new StringBuilder("{minicloud_source=\"otlp\"");
        if (service is not null)
        {
            builder.Append(", service_name=\"").Append(EscapeLogQlLiteral(service)).Append('"');
        }

        builder.Append('}');
        if (text is not null)
        {
            builder.Append(" |= \"").Append(EscapeLogQlLiteral(text)).Append('"');
        }

        return builder.ToString();
    }

    internal static string EscapeLogQlLiteral(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    private static bool MatchesPostFilter(TelemetryLogEntryResponse entry, string? severity, string? text, string? traceId)
    {
        if (severity is not null && !string.Equals(entry.Severity, severity, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text is not null && !entry.Body.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (traceId is not null && !string.Equals(entry.TraceId, traceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string? ExtractField(
        IReadOnlyDictionary<string, string> metadata, string line, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (metadata.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            foreach (var candidate in candidates)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(candidate, out var property)
                    && property.ValueKind == JsonValueKind.String)
                {
                    var value = property.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Plain-text bodies carry no extractable fields.
        }

        return null;
    }

    private static string TruncateBody(string line) =>
        line.Length <= MaxBodyBytes ? line : line[..MaxBodyBytes];

    private static List<LokiStream> ParseStreams(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var streams = new List<LokiStream>();
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array)
        {
            return streams;
        }

        foreach (var item in result.EnumerateArray())
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in stream.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        labels[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.TryGetProperty("structuredMetadata", out var structured)
                && structured.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in structured.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        metadata[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }
            }

            var values = new List<LokiValue>();
            if (item.TryGetProperty("values", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var pair in entries.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2)
                    {
                        continue;
                    }

                    var timestamp = ParseLokiTimestamp(pair[0].GetString());
                    var line = pair[1].GetString() ?? string.Empty;
                    values.Add(new LokiValue(timestamp, line));
                }
            }

            streams.Add(new LokiStream(labels, metadata, values));
        }

        return streams;
    }

    private static DateTimeOffset ParseLokiTimestamp(string? nanoseconds)
    {
        if (long.TryParse(nanoseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000);
        }

        return DateTimeOffset.UtcNow;
    }

    private static long ToUnixNanoseconds(DateTimeOffset value) =>
        value.ToUnixTimeMilliseconds() * 1_000_000;

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var total = 0L;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > TelemetryLimits.MaxQueryResponseBytes)
            {
                break;
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private sealed record LokiStream(
        Dictionary<string, string> Labels,
        Dictionary<string, string> Metadata,
        List<LokiValue> Values);

    private sealed record LokiValue(DateTimeOffset Timestamp, string Line);
}
