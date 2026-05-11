using System.Runtime.InteropServices;

namespace Minicloud.Cli.Auth;

public sealed class TokenStore
{
    private readonly CliEnvironment _environment;

    public TokenStore(CliEnvironment environment)
    {
        _environment = environment;
    }

    public string? GetToken()
    {
        var environmentToken = Environment.GetEnvironmentVariable(CliEnvironment.TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return environmentToken.Trim();
        }

        if (!File.Exists(_environment.TokenFilePath))
        {
            return null;
        }

        var token = File.ReadAllText(_environment.TokenFilePath).Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public void SaveToken(string token)
    {
        Directory.CreateDirectory(_environment.ConfigHome);
        File.WriteAllText(_environment.TokenFilePath, token.Trim() + Environment.NewLine);
        RestrictPermissions(_environment.TokenFilePath);
    }

    private static void RestrictPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
