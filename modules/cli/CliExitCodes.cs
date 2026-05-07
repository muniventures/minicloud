namespace Municloud.Cli;

public static class CliExitCodes
{
    public const int Success = 0;
    public const int DeploymentFailed = 1;
    public const int ValidationError = 2;
    public const int AuthError = 3;
    public const int NetworkOrApiUnavailable = 4;
}
