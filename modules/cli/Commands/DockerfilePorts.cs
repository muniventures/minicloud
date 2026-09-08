using System.Globalization;
using System.Text.RegularExpressions;

namespace Minicloud.Cli.Commands;

internal static class DockerfilePorts
{
    public static IReadOnlyList<int> Read(IEnumerable<string> lines)
    {
        var stages = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var ports = new HashSet<int>();
        var pending = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            pending += line;
            if (pending.EndsWith('\\'))
            {
                pending = pending[..^1] + " ";
                continue;
            }
            var parts = Regex.Split(pending, @"\s+");
            pending = "";
            if (parts[0].Equals("FROM", StringComparison.OrdinalIgnoreCase))
            {
                var arguments = parts.Skip(1).Where(part => !part.StartsWith("--", StringComparison.Ordinal)).ToArray();
                ports = arguments.Length > 0 && stages.TryGetValue(arguments[0], out var inherited)
                    ? new HashSet<int>(inherited) : [];
                if (arguments.Length >= 3 && arguments[1].Equals("AS", StringComparison.OrdinalIgnoreCase))
                {
                    stages[arguments[2]] = ports;
                }
            }
            else if (parts[0].Equals("EXPOSE", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var argument in parts.Skip(1))
                {
                    var value = argument.Split('/');
                    if (value.Length == 2 && value[1].Equals("udp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (value.Length > 2 || (value.Length == 2 && !value[1].Equals("tcp", StringComparison.OrdinalIgnoreCase)) ||
                        !int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                    {
                        throw new CliCommandException(CliExitCodes.ValidationError,
                            "Dockerfile EXPOSE must use literal ports between 1 and 65535 (optionally /tcp or /udp). Resolve variables before initializing or deploying.");
                    }
                    ports.Add(port);
                }
            }
        }
        return ports.Order().ToArray();
    }
}
