using Minicloud.Cli.Config;

namespace Minicloud.Cli.Commands;

internal static class LocalSecretsFile
{
    public const string FileName = "minicloud.secrets.env";

    public static IReadOnlyDictionary<string, string> Parse(string path)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new CliCommandException(CliExitCodes.ValidationError, $"Invalid local secrets line in '{path}'. Expected NAME=value.");
            }

            var name = line[..separator].Trim();
            if (!MinicloudConfigValidator.IsEnvironmentVariableKey(name))
            {
                throw new CliCommandException(CliExitCodes.ValidationError, $"Invalid local secret name '{name}' in '{path}'. Secret names must match ^[A-Za-z_][A-Za-z0-9_]*$.");
            }

            secrets[name] = Unquote(line[(separator + 1)..].Trim());
        }

        return secrets;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
