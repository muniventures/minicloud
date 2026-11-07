using System.Text.RegularExpressions;

namespace Minicloud.Common.Redaction;

/// <summary>
/// Canonical core log redaction shared by backend, runtime agent, and CLI.
/// Dependency-free string scrubbing: bearer credentials, ingest/API tokens,
/// secret assignments, and JWTs. OTLP tree walking stays with the telemetry
/// stack; hosts needing only string scrubbing must not pull OTLP services.
/// </summary>
public static partial class LogRedactor
{
    private const string Redacted = "[REDACTED]";

    public static string Redact(string value)
    {
        var redacted = AuthorizationBearerRegex().Replace(value, $"Authorization: Bearer {Redacted}");
        redacted = IngestTokenRegex().Replace(redacted, Redacted);
        redacted = SecretAssignmentRegex().Replace(redacted, match => $"{match.Groups[1].Value}={Redacted}");
        redacted = JwtRegex().Replace(redacted, Redacted);
        return redacted;
    }

    /// <summary>
    /// True when an attribute/field name is secret-bearing and its value must be
    /// replaced wholesale rather than string-scrubbed.
    /// </summary>
    public static bool IsSecretFieldName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return SecretFieldNameRegex().IsMatch(name);
    }

    /// <summary>
    /// Redacts one attribute value: secret-bearing keys are replaced wholesale,
    /// other strings keep the standard bearer/JWT/assignment scrub.
    /// </summary>
    public static string RedactAttributeValue(string? key, string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return IsSecretFieldName(key) ? Redacted : Redact(value);
    }

    [GeneratedRegex(@"Authorization:\s*Bearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationBearerRegex();

    [GeneratedRegex(@"\b(?:mcl_ingest|mc_live|mrt)_[A-Za-z0-9_]+")]
    private static partial Regex IngestTokenRegex();

    [GeneratedRegex(@"(?i)\b(password|token|api_key)=([^\s&;]+)")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)(password|passwd|secret|token|api[_-]?key|auth|credential|private[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|session[_-]?(id|key)|cookie|ssn|credit[_-]?card)")]
    private static partial Regex SecretFieldNameRegex();
}
