using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed record GrafanaGatewayResolution(string AppId, string OrganizationId, string TenantId, string DataSourceUid);
public sealed record GrafanaGatewayOutcome(int StatusCode, byte[]? Body, string? ContentType, string? ErrorCode, string? ErrorMessage);

/// <summary>
/// T20 private Loki query proxy. Each request authenticates with the app-bound
/// internal read credential stored server-side in the Grafana data source and
/// resolves exactly ONE live app from storage. App IDs, tenant headers, and URLs
/// supplied by Grafana callers are never trusted. Only the Loki read operations
/// required by the pinned Grafana release (11.6.5 against Loki 3.5.2) are
/// allowlisted; tenant, time, result, and concurrency bounds apply to Explore
/// queries and metadata discovery alike. Push, delete, ruler, config, federation,
/// and arbitrary upstreams are denied.
/// </summary>
public sealed class GrafanaQueryGateway(
    MinicloudDbContext db,
    TimeProvider timeProvider,
    IHttpClientFactory httpClients,
    IOptions<TelemetryIngestionOptions> options,
    ILogger<GrafanaQueryGateway> logger)
{
    private static readonly Regex QueryPath = new(@"^/loki/api/v1/(query|query_range|labels|label|series)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LabelValuesPath = new(@"^/loki/api/v1/label/[^/]+/values$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ForwardableParams = new(StringComparer.Ordinal)
    {
        "query", "start", "end", "time", "step", "interval", "interval-factor",
        "limit", "direction", "since", "match[]", "match"
    };

    private static readonly Regex ShardParam = new(@"(^|[&?])shards=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SemaphoreSlim _global = new(TelemetryLimits.MaxConcurrentQueriesTotal, TelemetryLimits.MaxConcurrentQueriesTotal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perApp = new(StringComparer.Ordinal);

    public async Task<GrafanaGatewayOutcome> ExecuteAsync(
        string method,
        string lokiPath,
        string queryString,
        string? contentType,
        byte[]? body,
        string? authorizationHeader,
        int authorizationHeaderCount,
        CancellationToken cancellationToken)
    {
        if (authorizationHeaderCount != 1)
        {
            return Deny(StatusCodes.Status401Unauthorized, "query_unauthorized", "Exactly one Authorization header is required.");
        }

        var resolution = await ResolveAsync(authorizationHeader, cancellationToken);
        if (resolution is null)
        {
            return Deny(StatusCodes.Status401Unauthorized, "query_unauthorized", "Unknown, revoked, or expired query credential.");
        }

        var normalizedPath = "/" + (lokiPath ?? string.Empty).Trim().TrimStart('/').Split('?', 2)[0];
        var upperMethod = method.ToUpperInvariant();
        if (!IsOperationAllowed(upperMethod, normalizedPath))
        {
            return Deny(StatusCodes.Status404NotFound, "query_not_found", "The requested operation is not available.");
        }

        var parameters = ParseParameters(queryString);
        if (upperMethod == "POST" && body is { Length: > 0 } && IsFormContent(contentType))
        {
            if (body.Length > 64 * 1024)
            {
                return Deny(StatusCodes.Status413PayloadTooLarge, "query_too_large", "Query body exceeds the allowed size.");
            }

            var formText = Encoding.UTF8.GetString(body);
            if (ShardParam.IsMatch("?" + formText))
            {
                return Deny(StatusCodes.Status400BadRequest, "query_forbidden", "Sharded/federated queries are not supported.");
            }

            foreach (var (key, value) in ParseParameters(formText))
            {
                parameters.Add((key, value));
            }
        }

        if (ShardParam.IsMatch("?" + (queryString ?? string.Empty)))
        {
            return Deny(StatusCodes.Status400BadRequest, "query_forbidden", "Sharded/federated queries are not supported.");
        }

        // Caller tenant signals are never trusted; exactly one tenant is applied below.
        parameters.RemoveAll(x => string.Equals(x.Key, "X-Scope-OrgID", StringComparison.OrdinalIgnoreCase));
        if (parameters.Any(x => x.Key.StartsWith("X-Scope", StringComparison.OrdinalIgnoreCase)))
        {
            return Deny(StatusCodes.Status400BadRequest, "query_forbidden", "Tenant federation is not supported.");
        }

        var now = timeProvider.GetUtcNow();
        var bound = ApplyBounds(normalizedPath, parameters, now);
        if (bound is not null)
        {
            return bound;
        }

        var appGate = _perApp.GetOrAdd(resolution.AppId, _ => new SemaphoreSlim(
            TelemetryLimits.MaxConcurrentQueriesPerApp, TelemetryLimits.MaxConcurrentQueriesPerApp));
        if (!await _global.WaitAsync(0, cancellationToken))
        {
            return DeniedWithRetry("Query concurrency exhausted; retry with backoff.");
        }

        var enteredApp = false;
        try
        {
            if (!await appGate.WaitAsync(0, cancellationToken))
            {
                return DeniedWithRetry("Query concurrency exhausted for this app; retry with backoff.");
            }

            enteredApp = true;
            return await ForwardAsync(resolution, upperMethod, normalizedPath, parameters, cancellationToken);
        }
        finally
        {
            if (enteredApp)
            {
                appGate.Release();
            }

            _global.Release();
        }
    }

    public async Task<GrafanaGatewayResolution?> ResolveAsync(string? authorizationHeader, CancellationToken cancellationToken)
    {
        const string bearer = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorizationHeader) || !authorizationHeader.StartsWith(bearer, StringComparison.Ordinal))
        {
            return null;
        }

        var token = authorizationHeader[bearer.Length..].Trim();
        var segments = token.Split('_');
        if (segments.Length != 5 || $"{segments[0]}_{segments[1]}_{segments[2]}" != GrafanaProvisioningService.ReadTokenPrefixRoot)
        {
            return null;
        }

        var prefix = $"{segments[0]}_{segments[1]}_{segments[2]}_{segments[3]}";
        var candidates = await db.GrafanaDataSourceBindings
            .Where(x => x.DeletedAt == null)
            .ToListAsync(cancellationToken);
        var matches = candidates.Where(x => x.CredentialReference.StartsWith(prefix + "|", StringComparison.Ordinal)).ToList();

        GrafanaDataSourceBinding? verified = null;
        foreach (var candidate in matches)
        {
            var parts = candidate.CredentialReference.Split('|');
            if (parts.Length == 2 && GrafanaProvisioningService.VerifyToken(token, parts[1]))
            {
                verified = candidate;
                break;
            }
        }

        if (verified is null)
        {
            return null;
        }

        var liveCount = candidates.Count(x => x.AppId == verified.AppId);
        if (liveCount != 1)
        {
            logger.LogWarning("Query credential for app {AppId} resolves to {Count} live bindings; denying.", verified.AppId, liveCount);
            return null;
        }

        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == verified.AppId, cancellationToken);
        var settings = await db.AppTelemetrySettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AppId == verified.AppId, cancellationToken);
        if (app is null || settings is null || !settings.Enabled || settings.Deleting
            || app.OrganizationId != verified.OrganizationId || settings.OrganizationId != verified.OrganizationId)
        {
            return null;
        }

        return new GrafanaGatewayResolution(app.Id, app.OrganizationId, settings.TenantId, verified.GrafanaUid);
    }

    private static bool IsOperationAllowed(string method, string path)
    {
        if (LabelValuesPath.IsMatch(path))
        {
            return method == "GET";
        }

        if (QueryPath.IsMatch(path))
        {
            var operation = path.Split('/').Last().ToLowerInvariant();
            return operation switch
            {
                "query" => method is "GET" or "POST",
                "query_range" => method is "GET" or "POST",
                "labels" or "label" => method == "GET",
                "series" => method == "GET",
                _ => false
            };
        }

        return false;
    }

    private static List<(string Key, string Value)> ParseParameters(string? queryString)
    {
        var result = new List<(string Key, string Value)>();
        if (string.IsNullOrEmpty(queryString))
        {
            return result;
        }

        foreach (var pair in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var rawKey = equals < 0 ? pair : pair[..equals];
            var rawValue = equals < 0 ? string.Empty : pair[(equals + 1)..];
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            if (!ForwardableParams.Contains(key))
            {
                continue;
            }

            result.Add((key, Uri.UnescapeDataString(rawValue.Replace('+', ' '))));
        }

        return result;
    }

    private static bool IsFormContent(string? contentType) =>
        (contentType?.Split(';', 2)[0].Trim() ?? string.Empty).Equals(
            "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    private static GrafanaGatewayOutcome? ApplyBounds(
        string path, List<(string Key, string Value)> parameters, DateTimeOffset now)
    {
        string? First(string name) => parameters.FirstOrDefault(x => x.Key == name).Value;

        var limit = First("limit");
        if (limit is not null)
        {
            if (!int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed <= 0 || parsed > TelemetryLimits.MaxQueryEntries)
            {
                return Deny(StatusCodes.Status400BadRequest, "query_limit_exceeded",
                    $"Result limit must be between 1 and {TelemetryLimits.MaxQueryEntries}.");
            }
        }

        var isMetadata = path.EndsWith("/labels", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/label", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/series", StringComparison.OrdinalIgnoreCase)
            || LabelValuesPath.IsMatch(path);

        foreach (var (key, value) in parameters.Where(x => x.Key is "query" or "match[]" or "match"))
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > GrafanaQueryBounds.MaxQueryChars)
            {
                return Deny(StatusCodes.Status400BadRequest, "query_too_large",
                    $"Query text must be between 1 and {GrafanaQueryBounds.MaxQueryChars} characters.");
            }
        }

        if (!isMetadata && !parameters.Any(x => x.Key is "query" or "match[]" or "match"))
        {
            return Deny(StatusCodes.Status400BadRequest, "query_invalid", "A query expression is required.");
        }

        var start = First("start");
        var end = First("end");
        var time = First("time");
        if (start is not null || end is not null)
        {
            if (!GrafanaQueryBounds.TryParseLokiInstant(start, out var from)
                || !GrafanaQueryBounds.TryParseLokiInstant(end, out var to))
            {
                return Deny(StatusCodes.Status400BadRequest, "query_invalid", "Query start/end must be valid timestamps.");
            }

            if (!GrafanaQueryBounds.IsRangeAllowed(from, to, now))
            {
                return Deny(StatusCodes.Status400BadRequest, "query_range_exceeded",
                    "Query time range exceeds the 7-day maximum or extends into the future.");
            }
        }
        else if (time is not null)
        {
            if (!GrafanaQueryBounds.TryParseLokiInstant(time, out var instant)
                || instant < now - TelemetryLimits.MaxQueryRange - TimeSpan.FromHours(1)
                || instant > now + TelemetryLimits.MaxFutureSkew)
            {
                return Deny(StatusCodes.Status400BadRequest, "query_range_exceeded", "Query instant is outside the allowed window.");
            }
        }
        else
        {
            return Deny(StatusCodes.Status400BadRequest, "query_invalid", "A query time window is required.");
        }

        return null;
    }

    private async Task<GrafanaGatewayOutcome> ForwardAsync(
        GrafanaGatewayResolution resolution,
        string method,
        string path,
        List<(string Key, string Value)> parameters,
        CancellationToken cancellationToken)
    {
        Uri endpoint;        try
        {
            endpoint = new Uri(new Uri(options.Value.LokiBaseAddress.TrimEnd('/') + "/"), path.TrimStart('/') + BuildQuery(parameters));
        }
        catch (UriFormatException)
        {
            return Deny(StatusCodes.Status503ServiceUnavailable, "query_unavailable", "Log storage is temporarily unavailable.");
        }

        using var timeout = new CancellationTokenSource(TelemetryLimits.QueryTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var client = httpClients.CreateClient();
            using var request = new HttpRequestMessage(
                string.Equals(method, "POST", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation("X-Scope-OrgID", resolution.TenantId);
            if (string.Equals(method, "POST", StringComparison.Ordinal))
            {
                var form = BuildQuery(parameters).TrimStart('?');
                request.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var body = await ReadBoundedAsync(response.Content, linked.Token);
            if (body is null)
            {
                return Deny(StatusCodes.Status502BadGateway, "query_too_large",
                    $"Query response exceeded {TelemetryLimits.MaxQueryResponseBytes} bytes.");
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            return new GrafanaGatewayOutcome((int)response.StatusCode, body, contentType, null, null);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Loki query timed out for app {AppId}.", resolution.AppId);
            return Deny(StatusCodes.Status504GatewayTimeout, "query_timeout", "The query timed out; narrow the time range and retry.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Loki query transport failure for app {AppId}.", resolution.AppId);
            return Deny(StatusCodes.Status503ServiceUnavailable, "query_unavailable", "Log storage is temporarily unavailable.");
        }
    }

    private static string BuildQuery(List<(string Key, string Value)> parameters)
    {
        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("?");
        var first = true;
        foreach (var (key, value) in parameters)
        {
            if (!first)
            {
                builder.Append('&');
            }

            first = false;
            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var total = 0L;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > TelemetryLimits.MaxQueryResponseBytes)
            {
                return null;
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static GrafanaGatewayOutcome Deny(int statusCode, string code, string message) =>
        new(statusCode, null, null, code, message);

    private static GrafanaGatewayOutcome DeniedWithRetry(string message) =>
        new(StatusCodes.Status429TooManyRequests, null, null, "query_concurrency_exhausted", message);
}
