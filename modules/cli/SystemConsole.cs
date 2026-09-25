namespace Minicloud.Cli;

public interface IConsole
{
    bool SupportsAnsi { get; }
    int WindowWidth => 80;
    void Write(string message);
    void WriteLine(string message = "");
    void WriteError(string message);
    string? ReadLine();
    ConsoleKeyInfo ReadKey(bool intercept);
}

public sealed class SystemConsole : IConsole
{
    public int WindowWidth
    {
        get
        {
            try
            {
                if (Console.IsOutputRedirected)
                {
                    return 80;
                }

                var width = Console.WindowWidth;
                return width > 0 ? width : 80;
            }
            catch
            {
                return 80;
            }
        }
    }

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
    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);
}
