using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Telemetry.Otlp;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using Minicloud.Telemetry.Data;

namespace Minicloud.Telemetry.Services;

public sealed class OtlpRequestValidationException(string message) : Exception(message);

public sealed record OtlpNormalizationResult(
    OtlpExportRequest ForwardRequest,
    int TotalRecords,
    int AcceptedRecords,
    int RejectedRecords,
    long NormalizedBytes,
    string? Notice);

public sealed class OtlpNormalizationService(
    TelemetryDbContext db,
    IOptions<TelemetryOptions> options)
{
    public const string OrgIdKey = "minicloud.organization.id";
    public const string AppIdKey = "minicloud.app.id";
    public const string EnvironmentKey = "minicloud.platform.environment";
    public const string SourceKey = "minicloud.source";
    public const string ServiceNameKey = "service.name";
    public const string DeploymentIdKey = "minicloud.deployment.id";
    public const string SourceValue = "otlp";

    private static readonly HashSet<string> CanonicalKeys = new(StringComparer.Ordinal)
    {
        OrgIdKey, AppIdKey, EnvironmentKey, SourceKey, ServiceNameKey, DeploymentIdKey,
    };

    private static readonly HashSet<string> CanonicalNormalizedKeys = new(StringComparer.Ordinal)
    {
        RuntimeLogRedactor.NormalizeAttributeName(OrgIdKey),
        RuntimeLogRedactor.NormalizeAttributeName(AppIdKey),
        RuntimeLogRedactor.NormalizeAttributeName(EnvironmentKey),
        RuntimeLogRedactor.NormalizeAttributeName(SourceKey),
        RuntimeLogRedactor.NormalizeAttributeName(ServiceNameKey),
        RuntimeLogRedactor.NormalizeAttributeName(DeploymentIdKey),
    };

    private static readonly HashSet<string> IndexLabelKeys = new(StringComparer.Ordinal)
    {
        ServiceNameKey, SourceKey, EnvironmentKey,
    };

    public async Task<OtlpNormalizationResult> NormalizeAsync(
        OtlpExportRequest request,
        TelemetryAuthSnapshot auth,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var total = request.RecordCount();
        if (total > TelemetryLimits.MaxRecordsPerRequest)
        {
            throw new OtlpRequestValidationException(
                $"Request contains {total} log records; at most {TelemetryLimits.MaxRecordsPerRequest} are accepted per request.");
        }

        var environment = options.Value.Environment;
        var forward = new OtlpExportRequest();
        var accepted = 0;
        var rejected = 0;
        var deploymentCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var resourceLogs in request.ResourceLogs)
        {
            var resourceAttrs = SanitizeAttributeList(resourceLogs.Resource?.Attributes, out _);
            var resourceDeploymentId = await FirstValidatedDeploymentIdAsync(
                resourceLogs.Resource?.Attributes, auth, deploymentCache, cancellationToken);

            var relocated = new List<OtlpKeyValue>(resourceAttrs.Count);
            foreach (var attribute in resourceAttrs)
            {
                if (IndexLabelKeys.Contains(attribute.Key))
                {
                    continue;
                }

                relocated.Add(attribute);
            }

            var forwardResourceLogs = new OtlpResourceLogs
            {
                Resource = new OtlpResource(),
                SchemaUrl = Truncate(resourceLogs.SchemaUrl, 512),
            };
            forwardResourceLogs.Resource.Attributes.Add(StringAttribute(ServiceNameKey, auth.ServiceName));
            forwardResourceLogs.Resource.Attributes.Add(StringAttribute(SourceKey, SourceValue));
            forwardResourceLogs.Resource.Attributes.Add(StringAttribute(EnvironmentKey, environment));

            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                var scope = scopeLogs.Scope ?? new OtlpScope();
                var scopeAttrs = SanitizeAttributeList(scope.Attributes, out var scopeDropped);
                var scopeDeploymentId = await FirstValidatedDeploymentIdAsync(
                    scope.Attributes, auth, deploymentCache, cancellationToken) ?? resourceDeploymentId;

                var forwardScope = new OtlpScopeLogs
                {
                    Scope = new OtlpScope
                    {
                        Name = Truncate(scope.Name, 256),
                        Version = Truncate(scope.Version, 256),
                        DroppedAttributesCount = scope.DroppedAttributesCount + (uint)scopeDropped,
                    },
                    SchemaUrl = Truncate(scopeLogs.SchemaUrl, 512),
                };

                foreach (var record in scopeLogs.LogRecords)
                {
                    var deploymentId = await ResolveRecordDeploymentIdAsync(
                        record.Attributes, scopeDeploymentId, auth, deploymentCache, cancellationToken);
                    var forwardRecord = TryNormalizeRecord(
                        record, relocated, scopeAttrs, auth, environment, deploymentId, receivedAt);
                    if (forwardRecord is null)
                    {
                        rejected++;
                        continue;
                    }

                    forwardScope.LogRecords.Add(forwardRecord);
                    accepted++;
                }

                if (forwardScope.LogRecords.Count > 0)
                {
                    forwardResourceLogs.ScopeLogs.Add(forwardScope);
                }
            }

            if (forwardResourceLogs.ScopeLogs.Count > 0)
            {
                forward.ResourceLogs.Add(forwardResourceLogs);
            }
        }

        string? notice = rejected > 0
            ? $"{rejected} of {total} log records were rejected by gateway validation."
            : null;

        return new OtlpNormalizationResult(
            forward, total, accepted, rejected, forward.CalculateSize(), notice);
    }

    private OtlpLogRecord? TryNormalizeRecord(
        OtlpLogRecord record,
        List<OtlpKeyValue> relocated,
        List<OtlpKeyValue> scopeAttrs,
        TelemetryAuthSnapshot auth,
        string environment,
        string? deploymentId,
        DateTimeOffset receivedAt)
    {
        var body = record.Body;
        if (body is not null)
        {
            RuntimeLogRedactor.RedactOtlpValue(body, null);
            if (body.CalculateSize() > TelemetryLimits.MaxBodyBytesPerRecord)
            {
                return null;
            }
        }

        var merged = new List<OtlpKeyValue>(
            relocated.Count + scopeAttrs.Count + record.Attributes.Count + 3);
        var seenExact = new HashSet<string>(StringComparer.Ordinal);
        var seenNormalized = new HashSet<string>(StringComparer.Ordinal);
        var dropped = (long)record.DroppedAttributesCount;

        void AddTenantAttribute(string key, string value)
        {
            var normalized = RuntimeLogRedactor.NormalizeAttributeName(key);
            merged.Add(new OtlpKeyValue
            {
                Key = key,
                Value = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = value },
            });
            seenExact.Add(key);
            seenNormalized.Add(normalized);
        }

        AddTenantAttribute(OrgIdKey, auth.OrganizationId);
        AddTenantAttribute(AppIdKey, auth.AppId);
        if (deploymentId is not null)
        {
            AddTenantAttribute(DeploymentIdKey, deploymentId);
        }

        foreach (var source in (IEnumerable<List<OtlpKeyValue>>)[relocated, scopeAttrs])
        {
            foreach (var attribute in source)
            {
                if (!TryAddAttribute(attribute, merged, seenExact, seenNormalized, ref dropped, out var conflict))
                {
                    if (conflict)
                    {
                        return null;
                    }
                }
            }
        }

        var recordAttrs = SanitizeAttributeList(record.Attributes, out var recordDropped);
        dropped += recordDropped;
        foreach (var attribute in recordAttrs)
        {
            if (!TryAddAttribute(attribute, merged, seenExact, seenNormalized, ref dropped, out var conflict))
            {
                if (conflict)
                {
                    return null;
                }
            }
        }

        if (merged.Count > TelemetryLimits.MaxAttributeCountPerRecord)
        {
            return null;
        }

        var attributesSize = 0;
        foreach (var attribute in merged)
        {
            attributesSize += attribute.CalculateSize();
        }

        if (attributesSize > TelemetryLimits.MaxAttributesBytesPerRecord)
        {
            return null;
        }

        if (!TryResolveTimestamp(record, receivedAt, out var timestampNanos))
        {
            return null;
        }

        var forward = new OtlpLogRecord
        {
            TimeUnixNano = timestampNanos,
            ObservedTimeUnixNano = record.ObservedTimeUnixNano,
            SeverityNumber = record.SeverityNumber,
            SeverityText = Truncate(record.SeverityText, 512),
            Body = body,
            Attributes = merged,
            DroppedAttributesCount = dropped > uint.MaxValue ? uint.MaxValue : (uint)dropped,
            Flags = record.Flags,
            TraceId = IsValidTraceId(record.TraceId) ? record.TraceId : [],
            SpanId = IsValidSpanId(record.SpanId) ? record.SpanId : [],
            EventName = Truncate(record.EventName, 256),
        };

        return forward;
    }

    private static bool TryAddAttribute(
        OtlpKeyValue attribute,
        List<OtlpKeyValue> merged,
        HashSet<string> seenExact,
        HashSet<string> seenNormalized,
        ref long dropped,
        out bool conflict)
    {
        conflict = false;
        if (!seenExact.Add(attribute.Key))
        {
            dropped++;
            return false;
        }

        var normalized = RuntimeLogRedactor.NormalizeAttributeName(attribute.Key);
        if (!seenNormalized.Add(normalized))
        {
            conflict = true;
            return false;
        }

        merged.Add(attribute);
        return true;
    }

    private bool TryResolveTimestamp(OtlpLogRecord record, DateTimeOffset receivedAt, out ulong timestampNanos)
    {
        timestampNanos = 0;
        var chosen = record.TimeUnixNano != 0
            ? record.TimeUnixNano
            : record.ObservedTimeUnixNano;

        DateTimeOffset chosenTime;
        if (chosen == 0)
        {
            chosenTime = receivedAt;
        }
        else
        {
            try
            {
                chosenTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(chosen / 1_000_000));
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        var oldest = receivedAt.Subtract(TelemetryLimits.Retention);
        var newest = receivedAt.Add(TelemetryLimits.MaxFutureSkew);
        if (chosenTime < oldest || chosenTime > newest)
        {
            return false;
        }

        timestampNanos = chosen == 0 ? ToUnixNanos(receivedAt) : chosen;
        return true;
    }

    private static ulong ToUnixNanos(DateTimeOffset value)
    {
        var millis = value.ToUnixTimeMilliseconds();
        return millis < 0 ? 0 : (ulong)millis * 1_000_000;
    }

    private static bool IsValidTraceId(byte[] traceId)
    {
        if (traceId.Length == 0)
        {
            return false;
        }

        return traceId.Length == 16 && traceId.Any(b => b != 0);
    }

    private static bool IsValidSpanId(byte[] spanId)
    {
        if (spanId.Length == 0)
        {
            return false;
        }

        return spanId.Length == 8 && spanId.Any(b => b != 0);
    }

    private async Task<string?> FirstValidatedDeploymentIdAsync(
        IEnumerable<OtlpKeyValue>? attributes,
        TelemetryAuthSnapshot auth,
        Dictionary<string, bool> cache,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in CollectDeploymentCandidates(attributes))
        {
            var validated = await ValidateDeploymentIdAsync(candidate, auth, cache, cancellationToken);
            if (validated is not null)
            {
                return validated;
            }
        }

        return null;
    }

    private async Task<string?> ResolveRecordDeploymentIdAsync(
        IEnumerable<OtlpKeyValue>? recordAttributes,
        string? inheritedDeploymentId,
        TelemetryAuthSnapshot auth,
        Dictionary<string, bool> cache,
        CancellationToken cancellationToken)
    {
        var candidates = CollectDeploymentCandidates(recordAttributes);
        if (candidates.Count == 0)
        {
            return inheritedDeploymentId;
        }

        foreach (var candidate in candidates)
        {
            var validated = await ValidateDeploymentIdAsync(candidate, auth, cache, cancellationToken);
            if (validated is not null)
            {
                return validated;
            }
        }

        return null;
    }

    private static List<string> CollectDeploymentCandidates(IEnumerable<OtlpKeyValue>? attributes)
    {
        var candidates = new List<string>();
        if (attributes is null)
        {
            return candidates;
        }

        foreach (var attribute in attributes)
        {
            if (!string.Equals(attribute.Key, DeploymentIdKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.Value.Kind == OtlpValueKind.String &&
                !string.IsNullOrWhiteSpace(attribute.Value.StringValue) &&
                attribute.Value.StringValue.Length <= 128)
            {
                candidates.Add(attribute.Value.StringValue);
            }
        }

        return candidates;
    }

    private async Task<string?> ValidateDeploymentIdAsync(
        string candidate,
        TelemetryAuthSnapshot auth,
        Dictionary<string, bool> cache,
        CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(candidate, out var valid))
        {
            valid = await db.DeploymentReplicas.AsNoTracking()
                .AnyAsync(x => x.Id == candidate && x.AppId == auth.AppId, cancellationToken);
            cache[candidate] = valid;
        }

        return valid ? candidate : null;
    }

    private static List<OtlpKeyValue> SanitizeAttributeList(
        IEnumerable<OtlpKeyValue>? attributes,
        out int dropped)
    {
        dropped = 0;
        var result = new List<OtlpKeyValue>();
        if (attributes is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            if (string.IsNullOrEmpty(attribute.Key) || attribute.Key.Length > 256)
            {
                dropped++;
                continue;
            }

            if (CanonicalKeys.Contains(attribute.Key) ||
                CanonicalNormalizedKeys.Contains(RuntimeLogRedactor.NormalizeAttributeName(attribute.Key)))
            {
                dropped++;
                continue;
            }

            if (!IsValidAttributeName(RuntimeLogRedactor.NormalizeAttributeName(attribute.Key)))
            {
                dropped++;
                continue;
            }

            if (!seen.Add(attribute.Key))
            {
                dropped++;
                continue;
            }

            RuntimeLogRedactor.RedactOtlpValue(attribute.Value, attribute.Key);
            result.Add(attribute);
        }

        return result;
    }

    private static bool IsValidAttributeName(string normalized)
    {
        if (normalized.Length == 0 || normalized.Length > 256)
        {
            return false;
        }

        var first = normalized[0];
        if (!char.IsLetter(first) && first != '_' && first != ':')
        {
            return false;
        }

        foreach (var character in normalized)
        {
            if (!char.IsLetterOrDigit(character) && character != '_' && character != ':')
            {
                return false;
            }
        }

        return true;
    }

    private static OtlpKeyValue StringAttribute(string key, string value) =>
        new()
        {
            Key = key,
            Value = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = value },
        };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
