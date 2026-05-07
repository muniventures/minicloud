namespace Municloud.Cli;

public interface IConsole
{
    bool SupportsAnsi { get; }
    void Write(string message);
    void WriteLine(string message = "");
    void WriteError(string message);
    string? ReadLine();
}

public sealed class SystemConsole : IConsole
{
    public bool SupportsAnsi
    {
        get
        {
            if (Console.IsOutputRedirected)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                return true;
            }

            var term = Environment.GetEnvironmentVariable("TERM");
            return !string.IsNullOrWhiteSpace(term) && !term.Equals("dumb", StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Write(string message) => Console.Write(message);
    public void WriteLine(string message = "") => Console.WriteLine(message);
    public void WriteError(string message) => Console.Error.WriteLine(message);
    public string? ReadLine() => Console.ReadLine();
}
