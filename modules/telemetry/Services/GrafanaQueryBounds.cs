using System.Globalization;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Telemetry.Services;

/// <summary>
/// Shared bounds for Grafana-driven Loki reads. Applied uniformly to Explore
/// queries, metadata/label discovery, and unified-query envelopes.
/// </summary>
public static class GrafanaQueryBounds
{
    public const int MaxQueryChars = 4096;

    public static bool IsRangeAllowed(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        if (to <= from)
        {
            return false;
        }

        if (to - from > TelemetryLimits.MaxQueryRange)
        {
            return false;
        }

        if (to > now + TelemetryLimits.MaxFutureSkew)
        {
            return false;
        }

        return true;
    }

    public static bool TryParseLokiInstant(string? value, out DateTimeOffset instant)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
        {
            if (nanos <= 0)
            {
                return false;
            }

            instant = DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000);
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            instant = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }
}
