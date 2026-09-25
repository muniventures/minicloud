namespace Minicloud.Cli.Rendering;

public interface IDeploymentTimingSink
{
    void RecordTiming(string line);
    IReadOnlyList<string> Records { get; }
}

public sealed class DeploymentTimingSink : IDeploymentTimingSink
{
    private readonly List<string> _records = [];
    private readonly object _lock = new();
    private readonly string? _filePath;

    public DeploymentTimingSink(string? filePath = null)
    {
        _filePath = filePath ?? Environment.GetEnvironmentVariable("MINICLOUD_CLI_TIMING_TRACE_OUTPUT");
    }

    public void RecordTiming(string line)
    {
        lock (_lock)
        {
            _records.Add(line);
            if (!string.IsNullOrWhiteSpace(_filePath))
            {
                try
                {
                    File.AppendAllLines(_filePath, [line]);
                }
                catch
                {
                    // Diagnostic sink must not fail execution
                }
            }
        }
    }

    public IReadOnlyList<string> Records
    {
        get
        {
            lock (_lock)
            {
                return _records.ToArray();
            }
        }
    }
}
