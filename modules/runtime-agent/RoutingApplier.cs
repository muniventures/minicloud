using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minicloud.RuntimeAgent;

public sealed class RoutingApplier(AgentConfig config, IProcessRunner processRunner, IHttpsProbe? httpsProbe = null)
{
    public async Task<IReadOnlyList<RuntimeRoutingApplyResult>> ApplyAsync(RuntimeDesiredRoutingResponse desired, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (desired.AppId != config.AppId || desired.ComposeProject != config.ComposeProject ||
                desired.Routes.Count > 100 || desired.Routes.Any(route => route.Hostname.Length > 253 ||
                    !Regex.IsMatch(route.Hostname, @"\A(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?\z") || route.PathPrefix != "/"))
            {
                throw new InvalidOperationException("Invalid routing identity, hostname, or path.");
            }
            var requestPath = Path.Combine(Path.GetDirectoryName(config.StatePath)!, "routing-request.json");
            var temporary = requestPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(desired, JsonDefaults.Options), cancellationToken);
            File.Move(temporary, requestPath, overwrite: true);
            var apply = await processRunner.RunAsync("sudo", ["-n", "/opt/minicloud/agent/apply-minicloud-routing"], cancellationToken);
            if (apply.ExitCode != 0)
            {
                return desired.Routes.Select(route => new RuntimeRoutingApplyResult(route.BindingId, "failed", "pending", "routing_apply_failed",
                    string.IsNullOrWhiteSpace(apply.Stdout) ? "Routing installation failed. Check the runtime routing helper and permissions." : RuntimeLogRedactor.Redact(apply.Stdout.Trim()[..Math.Min(apply.Stdout.Trim().Length, 500)]), now)).ToArray();
            }
            var probe = httpsProbe ?? new HttpsProbe();
            return await Task.WhenAll(desired.Routes.Select(async route =>
            {
                var ready = await probe.IsReadyAsync(route.Hostname, cancellationToken);
                return new RuntimeRoutingApplyResult(route.BindingId, "applied", ready ? "ready" : "pending", ready ? null : "https_not_ready",
                    ready ? null : "The route is applied; a valid HTTPS certificate is not available yet.", now);
            }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return desired.Routes.Select(route => new RuntimeRoutingApplyResult(route.BindingId, "failed", "pending", "routing_apply_failed",
                "Routing reconciliation failed. Check agent configuration and file permissions.", now)).ToArray();
        }
    }
}

public interface IHttpsProbe
{
    Task<bool> IsReadyAsync(string hostname, CancellationToken cancellationToken);
}

public sealed class HttpsProbe : IHttpsProbe
{
    public async Task<bool> IsReadyAsync(string hostname, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", 443, timeout.Token);
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = hostname }, timeout.Token);
            return tls.IsAuthenticated;
        }
        catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException or SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
