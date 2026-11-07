using System.Text.RegularExpressions;
using Minicloud.Common.Redaction;

namespace Minicloud.Telemetry.Otlp;

public static partial class RuntimeLogRedactor
{
    private const string Redacted = "[REDACTED]";

    public static string Redact(string value) => LogRedactor.Redact(value);

    /// <summary>
    /// True when an attribute/field name is secret-bearing and its value must be
    /// replaced wholesale rather than string-scrubbed.
    /// </summary>
    public static bool IsSecretFieldName(string? name) => LogRedactor.IsSecretFieldName(name);

    /// <summary>
    /// Redacts one attribute value: secret-bearing keys are replaced wholesale,
    /// other strings keep the standard bearer/JWT/assignment scrub.
    /// </summary>
    public static string RedactAttributeValue(string? key, string? value) =>
        LogRedactor.RedactAttributeValue(key, value);

    /// <summary>
    /// Loki normalizes OTLP attribute names by replacing dots (and other unsupported
    /// characters) with underscores. Generated queries use names such as service_name.
    /// </summary>
    public static string NormalizeAttributeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        return AttributeNameSanitizeRegex().Replace(name, "_");
    }

    /// <summary>
    /// Recursively redacts an OTLP attribute value tree in place. Secret-bearing keys
    /// collapse to [REDACTED]; all other strings keep the standard scrub.
    /// </summary>
    public static void RedactOtlpValue(OtlpAnyValue value, string? keyHint)
    {
        switch (value.Kind)
        {
            case OtlpValueKind.String:
                value.StringValue = RedactAttributeValue(keyHint, value.StringValue);
                break;
            case OtlpValueKind.Array:
                foreach (var item in value.ArrayValues)
                {
                    RedactOtlpValue(item, keyHint);
                }

                break;
            case OtlpValueKind.KvList:
                foreach (var pair in value.KvValues)
                {
                    if (IsSecretFieldName(pair.Key))
                    {
                        pair.Value = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = Redacted };
                    }
                    else
                    {
                        RedactOtlpValue(pair.Value, pair.Key);
                    }
                }

                break;
        }
    }

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex AttributeNameSanitizeRegex();
}
