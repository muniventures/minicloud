using System.Text.Json;

namespace Minicloud.Telemetry.Services;

/// <summary>
/// Auth-proxy enforcement decisions. The proxy strips every caller-supplied
/// credential/identity/tenant signal and forwards only the trusted identity
/// derived from a validated Minicloud proxy session. Public login, basic-auth,
/// admin and mutation routes are denied; only the pinned-version read paths
/// required by Explore plus the validated unified-query endpoint are forwarded.
/// </summary>
public static class GrafanaProxyGuard
{
    public const string IdentityHeader = "X-Minicloud-Grafana-Identity";
    public const string EmailHeader = "X-Minicloud-Grafana-Email";
    public const string NameHeader = "X-Minicloud-Grafana-Name";
    public const string WebAuthHeader = "X-WEBAUTH-USER";

    private static readonly HashSet<string> StrippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "X-Grafana-Org-Id",
        "X-Minicloud-Grafana-Identity",
        "X-Minicloud-Grafana-Email",
        "X-Minicloud-Grafana-Name",
        "X-WEBAUTH-USER",
        "X-Forwarded-User",
        "X-Scope-OrgID",
        "X-DS-Authorization",
        "grafana_session",
    };

    public enum RouteDecision
    {
        AllowForward,
        DsQuery,
        Deny
    }

    public static string IdentityFor(string minicloudUserId, string organizationId) =>
        $"mc-{minicloudUserId}-{organizationId}";

    public static Dictionary<string, string> StripUntrustedHeaders(IEnumerable<KeyValuePair<string, string>> incoming)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in incoming)
        {
            if (StrippedHeaders.Contains(key))
            {
                continue;
            }

            if (key.StartsWith("X-Minicloud-Grafana-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kept[key] = value;
        }

        return kept;
    }

    public static Dictionary<string, string> TrustedIdentityHeaders(string grafanaLogin, string? email = null, string? displayName = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [IdentityHeader] = grafanaLogin,
            [WebAuthHeader] = grafanaLogin
        };
        if (!string.IsNullOrWhiteSpace(email))
        {
            headers[EmailHeader] = email;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            headers[NameHeader] = displayName;
        }

        return headers;
    }

    public static RouteDecision DecideRoute(string method, string path)
    {
        var normalized = path.Split('?', 2)[0].TrimEnd('/');
        if (normalized.Length == 0)
        {
            normalized = "/";
        }

        var upper = method.ToUpperInvariant();

        if (IsDeniedPath(normalized, upper))
        {
            return RouteDecision.Deny;
        }

        if (upper == "POST" && string.Equals(normalized, "/api/ds/query", StringComparison.OrdinalIgnoreCase))
        {
            return RouteDecision.DsQuery;
        }

        if (upper is "GET" or "POST")
        {
            return RouteDecision.AllowForward;
        }

        if (upper is "PUT" or "PATCH" or "DELETE")
        {
            return RouteDecision.Deny;
        }

        return RouteDecision.Deny;
    }

    private static bool IsDeniedPath(string path, string method)
    {
        static bool Segment(string p, string s) =>
            p.Equals(s, StringComparison.OrdinalIgnoreCase) || p.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase);

        if (Segment(path, "/login") || Segment(path, "/logout") || Segment(path, "/admin"))
        {
            return true;
        }

        if (Segment(path, "/api/login") || Segment(path, "/api/auth") || Segment(path, "/api/admin")
            || Segment(path, "/api/serviceaccounts") || Segment(path, "/api/access-control")
            || Segment(path, "/api/plugins") || Segment(path, "/api/org/invites")
            || Segment(path, "/api/user/auth-tokens") || Segment(path, "/api/auth/keys"))
        {
            return true;
        }

        // Direct datasource-proxy access never carries envelope validation.
        if (Segment(path, "/api/datasources/proxy"))
        {
            return true;
        }

        if (Segment(path, "/api/ds/query"))
        {
            return method != "POST";
        }

        if (Segment(path, "/api/datasources"))
        {
            return !string.Equals(method, "GET", StringComparison.Ordinal);
        }

        if (Segment(path, "/api/org/users") || Segment(path, "/api/orgs"))
        {
            return !string.Equals(method, "GET", StringComparison.Ordinal);
        }

        return false;
    }

    public static bool TryValidateDsQueryEnvelope(
        string jsonBody,
        string expectedDataSourceUid,
        out string? denialReason)
    {
        denialReason = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jsonBody);
        }
        catch (JsonException)
        {
            denialReason = "malformed query envelope";
            return false;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("queries", out var queries)
                || queries.ValueKind != JsonValueKind.Array
                || queries.GetArrayLength() == 0)
            {
                denialReason = "query envelope has no queries";
                return false;
            }

            DateTimeOffset? from = null;
            DateTimeOffset? to = null;
            if (document.RootElement.TryGetProperty("from", out var fromProp) && fromProp.ValueKind == JsonValueKind.String)
            {
                DateTimeOffset.TryParse(fromProp.GetString(), out var parsedFrom);
                from = parsedFrom;
            }

            if (document.RootElement.TryGetProperty("to", out var toProp) && toProp.ValueKind == JsonValueKind.String)
            {
                DateTimeOffset.TryParse(toProp.GetString(), out var parsedTo);
                to = parsedTo;
            }

            if (from.HasValue && to.HasValue)
            {
                if (!GrafanaQueryBounds.IsRangeAllowed(from.Value, to.Value, DateTimeOffset.UtcNow))
                {
                    denialReason = "query time range exceeds bounds";
                    return false;
                }
            }

            foreach (var query in queries.EnumerateArray())
            {
                if (!query.TryGetProperty("datasource", out var datasource)
                    || !datasource.TryGetProperty("uid", out var uidProp)
                    || !string.Equals(uidProp.GetString(), expectedDataSourceUid, StringComparison.Ordinal))
                {
                    denialReason = "query targets an unexpected data source";
                    return false;
                }

                var expression = query.TryGetProperty("expr", out var expr) ? expr.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(expression) || expression.Length > GrafanaQueryBounds.MaxQueryChars)
                {
                    denialReason = "query expression is missing or too long";
                    return false;
                }

                if (query.TryGetProperty("queryType", out var queryType)
                    && queryType.ValueKind == JsonValueKind.String
                    && (queryType.GetString()?.Contains("delete", StringComparison.OrdinalIgnoreCase) == true
                        || queryType.GetString()?.Contains("admin", StringComparison.OrdinalIgnoreCase) == true))
                {
                    denialReason = "unsupported query type";
                    return false;
                }
            }
        }

        return true;
    }
}
