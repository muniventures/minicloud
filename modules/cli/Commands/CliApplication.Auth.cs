using System.Diagnostics;
using System.Runtime.InteropServices;
using Minicloud.Cli.Api;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunTokenAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "set")
        {
            _console.WriteError("Usage: minicloud token set <token>");
            return CliExitCodes.ValidationError;
        }

        var token = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(token))
        {
            _console.WriteError("Usage: minicloud token set <token>");
            return CliExitCodes.ValidationError;
        }

        await _apiClient.GetMeWithTokenAsync(token, cancellationToken);
        _tokenStore.SaveToken(token);
        _console.WriteLine("Token stored.");
        return CliExitCodes.Success;
    }

    private async Task<int> RunLoginAsync(string[] args, CancellationToken cancellationToken)
    {
        var token = GetOption(args, "--token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            var me = await _apiClient.GetMeWithTokenAsync(token, cancellationToken);
            _tokenStore.SaveToken(token);
            _console.WriteLine($"Logged in as {me.Email}");
            var organization = me.Organizations.FirstOrDefault();
            if (organization is not null)
            {
                _console.WriteLine($"Organization: {organization.Name}");
            }

            return CliExitCodes.Success;
        }

        var noBrowser = args.Contains("--no-browser", StringComparer.Ordinal);
        var session = await _apiClient.CreateCliLoginSessionAsync(cancellationToken);
        if (!noBrowser && TryOpenBrowser(session.LoginUrl))
        {
            _console.WriteLine("Opening browser for Minicloud login...");
        }
        else
        {
            _console.WriteLine("Open this URL to finish Minicloud login:");
            _console.WriteLine(session.LoginUrl);
        }

        var exchange = await PollCliLoginExchangeAsync(session.SessionId, session.ExpiresAt, cancellationToken);
        _tokenStore.SaveToken(exchange.Token);
        _console.WriteLine($"Logged in as {exchange.Email}");
        _console.WriteLine($"Organization: {exchange.Organization.Name}");
        _console.WriteLine($"Token: {MaskToken(exchange.Token)}");
        return CliExitCodes.Success;
    }

    private async Task<CliLoginSessionExchangeResponse> PollCliLoginExchangeAsync(string sessionId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            CliLoginSessionExchangeResponse? exchange;
            try
            {
                exchange = await _apiClient.ExchangeCliLoginSessionAsync(sessionId, cancellationToken);
            }
            catch (Exception ex) when (IsTransientNetworkException(ex, cancellationToken))
            {
                continue;
            }

            if (exchange is not null)
            {
                return exchange;
            }

            _console.WriteLine("Waiting for browser approval...");
        }

        throw new ApiException(401, "cli_login_session_expired", "CLI login session expired before approval.");
    }

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            Process.Start("xdg-open", url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string MaskToken(string token)
    {
        var visibleLength = Math.Min("mc_live_123456".Length, token.Length);
        return token[..visibleLength] + "...";
    }
}
