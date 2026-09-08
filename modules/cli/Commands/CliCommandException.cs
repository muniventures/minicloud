namespace Minicloud.Cli.Commands;

internal sealed class CliCommandException : Exception
{
    public CliCommandException(int exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
