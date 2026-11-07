using Minicloud.Common.Redaction;

namespace Minicloud.RuntimeAgent;

public static class RuntimeLogRedactor
{
    public static string Redact(string value) => LogRedactor.Redact(value);
}
