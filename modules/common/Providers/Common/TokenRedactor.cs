using System.Text.RegularExpressions;

namespace Minicloud.Common.Providers.Common;

public static partial class TokenRedactor
{
    private static readonly Regex BearerRegex = new(@"Bearer\s+[A-Za-z0-9_\-\.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KeyPatternRegex = new(@"(?i)(token|key|secret|password|authorization)[\"":= ]+([A-Za-z0-9_\-\.]{8,})", RegexOptions.Compiled);

    public static string Redact(string? input, params string?[] explicitTokens)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var result = input;

        foreach (var token in explicitTokens)
        {
            if (!string.IsNullOrWhiteSpace(token) && token.Length > 3)
            {
                result = result.Replace(token, "[REDACTED]");
            }
        }

        result = BearerRegex.Replace(result, "Bearer [REDACTED]");
        result = KeyPatternRegex.Replace(result, "$1=[REDACTED]");

        return result;
    }
}
