namespace Minicloud.Cli.Rendering;

public static class DeploymentRendererFactory
{
    public static IDeploymentRenderer Create(
        IConsole console,
        TimeProvider? timeProvider = null,
        bool? forceInteractive = null,
        bool? noColor = null,
        bool enableSpinnerTimer = true)
    {
        var isInteractive = forceInteractive ?? console.SupportsAnsi;
        if (isInteractive)
        {
            return new InteractiveDeploymentRenderer(console, timeProvider, noColor, enableSpinnerTimer);
        }

        return new PlainTextDeploymentRenderer(console, timeProvider);
    }
}
