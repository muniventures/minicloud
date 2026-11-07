using PlanMapping = Minicloud.Common.Services.ProviderPlanMapping;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Worker.Pipeline;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Models;
using static Minicloud.Common.Providers.ProviderServiceCollectionExtensions;

namespace Minicloud.Worker.Pipeline.Steps;

/// <summary>
/// Step 1: resolve the existing <c>ServerInstance</c> for the app or provision
/// a new VPS via <see cref="IVpsProviderClient"/>. Polls until an IP is
/// allocated and SSH is reachable, then persists the server record.
/// Failure code: <c>provider_provision_failed</c>.
/// </summary>
public sealed class ResolveProvisionServerStep : IPipelineStep
{
    public const string FailureCode = "provider_provision_failed";

    public string Name => "resolve-provision";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VpsProviderClientResolver _providerResolver;
    private readonly DeploymentPipelineOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ResolveProvisionServerStep>? _logger;
    private readonly Func<string, int, CancellationToken, Task<bool>>? _sshProbe;

    public ResolveProvisionServerStep(
        IServiceScopeFactory scopeFactory,
        VpsProviderClientResolver providerResolver,
        IOptions<DeploymentPipelineOptions> options,
        TimeProvider timeProvider,
        ILogger<ResolveProvisionServerStep>? logger = null,
        Func<string, int, CancellationToken, Task<bool>>? sshProbe = null)
        : this(scopeFactory, providerResolver, options.Value, timeProvider, logger, sshProbe)
    {
    }

    public ResolveProvisionServerStep(
        IServiceScopeFactory scopeFactory,
        VpsProviderClientResolver providerResolver,
        DeploymentPipelineOptions options,
        TimeProvider timeProvider,
        ILogger<ResolveProvisionServerStep>? logger = null,
        Func<string, int, CancellationToken, Task<bool>>? sshProbe = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(providerResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _providerResolver = providerResolver;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _sshProbe = sshProbe;
    }

    public async Task ExecuteAsync(PipelineStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (orgSlug, appSlug, plan) = await LoadAppScopeAsync(context, cancellationToken);
        context.OrganizationSlug = orgSlug;
        context.AppSlug = appSlug;

        var mapping = PlanMapping.FromAppPlan(plan, _options);
        context.Provider = mapping.Provider;

        var existing = await FindExistingServerAsync(context, cancellationToken);
        if (existing is not null)
        {
            if (!Enum.TryParse<CloudProvider>(existing.Provider, true, out var registeredProvider))
            {
                throw new PipelineStepException(FailureCode, "Registered server has an unknown provider; refusing replacement provisioning.");
            }

            mapping = mapping with { Provider = registeredProvider, Region = existing.Region };
            context.Provider = registeredProvider;
            if (string.IsNullOrWhiteSpace(existing.ProviderServerId))
            {
                throw new PipelineStepException(FailureCode, "Registered server has no provider ID; refusing replacement provisioning.");
            }

            var status = await _providerResolver(registeredProvider)
                .GetServerStatusAsync(existing.ProviderServerId, cancellationToken);
            if (!status.IsRunning || string.IsNullOrWhiteSpace(status.Ipv4Address))
            {
                throw new PipelineStepException(FailureCode, "Registered server is not ready; refusing replacement provisioning.");
            }

            context.ServerInstanceId = await SaveServerInstanceAsync(context, mapping,
                new ProviderServerInfo(existing.ProviderServerId, "", status.Ipv4Address, status.Status), cancellationToken);
            context.ServerIp = status.Ipv4Address;
            await RecordEventAsync(context.DeploymentId, EventLevel.Info,
                $"Resolved registered server {existing.ProviderServerId} ({status.Ipv4Address}).", cancellationToken);
            return;
        }

        var client = _providerResolver(mapping.Provider);
        var serverName = BuildServerName(orgSlug, appSlug);

        ProviderServerInfo server;
        try
        {
            var found = await client.FindServerAsync(serverName, cancellationToken);
            if (found is not null)
            {
                if (!string.Equals(found.Name, serverName, StringComparison.Ordinal) ||
                    (mapping.Provider == CloudProvider.Hetzner &&
                     (found.Labels is null ||
                      !found.Labels.TryGetValue("managed-by", out var manager) || manager != "minicloud" ||
                      !found.Labels.TryGetValue("customer", out var customer) || customer != orgSlug ||
                      !found.Labels.TryGetValue("app", out var appLabel) || appLabel != appSlug ||
                      !found.Labels.TryGetValue("env", out var environment) || environment != "production")))
                {
                    throw new PipelineStepException(FailureCode, "Discovered server identity does not match this app; refusing deployment.");
                }
                server = found;
                _logger?.LogInformation("Found provider server {Name} ({Id})", serverName, found.Id);
            }
            else
            {
                await EnsureNewAppAsync(context, cancellationToken);
                _logger?.LogInformation(
                    "Provisioning new {Provider} server {Name} (plan {Plan}, region {Region})",
                    mapping.Provider, serverName, mapping.Sku, mapping.Region);
                await RecordEventAsync(context.DeploymentId, EventLevel.Info,
                    $"Provisioning new {mapping.Provider} server {serverName} (plan {mapping.Sku}, region {mapping.Region}).",
                    cancellationToken);

                server = await client.CreateServerAsync(new CreateServerRequest(
                    serverName,
                    mapping.Sku,
                    mapping.Region,
                    // The public key is installed for minicloud by cloud-init, not a provider key ID.
                    string.Empty,
                    BuildCloudInit(orgSlug, appSlug, _options.SshPublicKey),
                    new Dictionary<string, string>
                    {
                        ["managed-by"] = "minicloud",
                        ["customer"] = orgSlug,
                        ["app"] = appSlug,
                        ["env"] = "production",
                    },
                    [$"minicloud-customer-{orgSlug}", $"minicloud-app-{appSlug}", "minicloud-env-production"]),
                    cancellationToken);
                // Preserve the provider identity even when the deployment is canceled while waiting for readiness.
                using (var registration = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    await SaveServerInstanceAsync(context, mapping, server, registration.Token, ready: false);
                }
            }

            server = await WaitForServerReadyAsync(client, server, cancellationToken);
            await WaitForSshAsync(server.Ipv4Address, cancellationToken);
        }
        catch (PipelineStepException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PipelineStepException(FailureCode,
                $"VPS provisioning failed for server {serverName}: {ex.Message}", ex);
        }

        var instanceId = await SaveServerInstanceAsync(context, mapping, server, cancellationToken);
        context.ServerInstanceId = instanceId;
        context.ServerIp = server.Ipv4Address;
        await RecordEventAsync(context.DeploymentId, EventLevel.Info,
            $"Server ready: {server.Id} ({server.Ipv4Address}).", cancellationToken);
    }

    private async Task<(string OrgSlug, string AppSlug, AppPlan Plan)> LoadAppScopeAsync(
        PipelineStepContext context, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var app = await db.Apps
            .AsNoTracking()
            .Include(a => a.Organization)
            .FirstOrDefaultAsync(a => a.Id == context.AppId, cancellationToken);
        if (app is null)
        {
            throw new PipelineStepException(FailureCode, $"App {context.AppId} not found.");
        }

        if (!string.Equals(app.OrganizationId, context.OrganizationId, StringComparison.Ordinal))
        {
            throw new PipelineStepException(FailureCode,
                $"App {context.AppId} does not belong to organization {context.OrganizationId}.");
        }

        return (app.Organization.Slug, app.Slug, app.Plan);
    }

    private async Task<ServerInstance?> FindExistingServerAsync(
        PipelineStepContext context, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var servers = await db.ServerInstances
            .AsNoTracking()
            .Where(s => s.OrganizationId == context.OrganizationId && s.AppId == context.AppId)
            .ToListAsync(cancellationToken);

        var runtimes = await db.RuntimeServers.AsNoTracking()
            .Where(s => s.OrganizationId == context.OrganizationId && s.AppId == context.AppId)
            .ToListAsync(cancellationToken);
        var identities = servers.Select(s => (s.Provider.ToLowerInvariant(), s.ProviderServerId))
            .Concat(runtimes.Select(s => (s.Provider.ToLowerInvariant(), s.ProviderServerId)))
            .Distinct().ToList();
        if (identities.Count > 1 || identities.Any(x => string.IsNullOrWhiteSpace(x.Item2)))
        {
            throw new PipelineStepException(FailureCode,
                "Server registrations conflict or lack a provider ID; reconcile the app's server association before deploying.");
        }

        if (servers.Count > 1)
        {
            throw new PipelineStepException(FailureCode, "Multiple server records exist for this app; reconcile them before deploying.");
        }

        var identity = identities.FirstOrDefault();
        if (identities.Count == 1 && (await db.ServerInstances.AnyAsync(s =>
                s.Provider == identity.Item1 && s.ProviderServerId == identity.Item2 &&
                (s.OrganizationId != context.OrganizationId || s.AppId != context.AppId), cancellationToken) ||
            await db.RuntimeServers.AnyAsync(s =>
                s.Provider == identity.Item1 && s.ProviderServerId == identity.Item2 &&
                (s.OrganizationId != context.OrganizationId || s.AppId != context.AppId), cancellationToken)))
        {
            throw new PipelineStepException(FailureCode, "Registered provider server belongs to another app; refusing deployment.");
        }

        return servers.SingleOrDefault() ?? runtimes.Select(s => new ServerInstance
        {
            Provider = s.Provider,
            ProviderServerId = s.ProviderServerId,
            Region = ""
        }).FirstOrDefault();
    }

    private async Task EnsureNewAppAsync(PipelineStepContext context, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        if (await db.Deployments.AnyAsync(d => d.AppId == context.AppId &&
                d.Status == DeploymentStatus.Succeeded, cancellationToken) ||
            await db.ServerInstances.AnyAsync(s => s.AppId == context.AppId, cancellationToken) ||
            await db.RuntimeServers.AnyAsync(s => s.AppId == context.AppId, cancellationToken))
        {
            throw new PipelineStepException(FailureCode,
                "Existing app infrastructure could not be resolved; refusing to create a replacement server. Reconcile its server registration first.");
        }
    }

    private async Task<ProviderServerInfo> WaitForServerReadyAsync(
        IVpsProviderClient client, ProviderServerInfo server, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(server.Ipv4Address) &&
            (string.Equals(server.Status, "running", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(server.Status, "active", StringComparison.OrdinalIgnoreCase)))
        {
            return server;
        }

        var deadline = DateTimeOffset.UtcNow + _options.ProvisionTimeout;
        var current = server;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            var status = await client.GetServerStatusAsync(current.Id, cancellationToken);
            if (!string.IsNullOrWhiteSpace(status.Ipv4Address) && status.IsRunning)
            {
                return new ProviderServerInfo(
                    current.Id, current.Name, status.Ipv4Address!, status.Status,
                    status.Ipv6Address, current.Labels);
            }

            if (!string.IsNullOrWhiteSpace(status.Ipv4Address))
            {
                current = new ProviderServerInfo(
                    current.Id, current.Name, status.Ipv4Address!, status.Status,
                    status.Ipv6Address, current.Labels);
            }
        }

        throw new PipelineStepException(FailureCode,
            $"Server {current.Id} did not become ready within {_options.ProvisionTimeout.TotalMinutes:F0} minutes.");
    }

    private async Task WaitForSshAsync(string ipAddress, CancellationToken cancellationToken)
    {
        if (_options.SshReadyTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow + _options.SshReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_sshProbe is not null)
                {
                    if (await _sshProbe(ipAddress, 22, cancellationToken))
                    {
                        return;
                    }
                }
                else
                {
                    using var tcp = new TcpClient();
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    await tcp.ConnectAsync(ipAddress, 22, linked.Token);
                    if (tcp.Connected)
                    {
                        return;
                    }
                }
            }
            catch
            {
                // Retry until timeout; cloud-init/SSH may still be booting.
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var wait = remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10);
            await Task.Delay(wait, cancellationToken);
        }

        throw new PipelineStepException(FailureCode,
            $"SSH on {ipAddress}:22 did not become reachable within {_options.SshReadyTimeout.TotalMinutes:F0} minutes.");
    }

    private async Task<string> SaveServerInstanceAsync(
        PipelineStepContext context, PlanMapping mapping, ProviderServerInfo server,
        CancellationToken cancellationToken, bool ready = true)
    {
        var now = _timeProvider.GetUtcNow();
        var instanceId = string.Empty;
        await PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
        {
            if (await db.ServerInstances.AnyAsync(s => s.Provider == mapping.Provider.ToString().ToLowerInvariant() &&
                    s.ProviderServerId == server.Id && (s.AppId != context.AppId || s.OrganizationId != context.OrganizationId), ct) ||
                await db.RuntimeServers.AnyAsync(s => s.Provider == mapping.Provider.ToString().ToLowerInvariant() &&
                    s.ProviderServerId == server.Id && (s.AppId != context.AppId || s.OrganizationId != context.OrganizationId), ct))
            {
                throw new PipelineStepException(FailureCode, "Provider server belongs to another app; refusing deployment.");
            }
            var servers = await db.ServerInstances
                .Where(s => s.OrganizationId == context.OrganizationId && s.AppId == context.AppId)
                .ToListAsync(ct);
            var existing = servers
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefault();
            if (existing is not null)
            {
                existing.Provider = mapping.Provider.ToString().ToLowerInvariant();
                existing.ProviderServerId = server.Id;
                existing.IpAddress = server.Ipv4Address;
                existing.Region = mapping.Region;
                existing.Status = ready ? "active" : "provisioning";
                existing.BootstrapStatus = ready ? "ready" : "pending";
                existing.UpdatedAt = now;
                instanceId = existing.Id;
            }
            else
            {
                var created = new ServerInstance
                {
                    Id = Guid.NewGuid().ToString("N"),
                    OrganizationId = context.OrganizationId,
                    AppId = context.AppId,
                    Provider = mapping.Provider.ToString().ToLowerInvariant(),
                    ProviderServerId = server.Id,
                    IpAddress = server.Ipv4Address,
                    Region = mapping.Region,
                    Status = ready ? "active" : "provisioning",
                    BootstrapStatus = ready ? "ready" : "pending",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.ServerInstances.Add(created);
                instanceId = created.Id;
            }

            var deployment = await db.Deployments
                .FirstOrDefaultAsync(d => d.Id == context.DeploymentId, ct);
            if (deployment is not null)
            {
                PipelineDb.Touch(deployment, now);
            }

            await Task.CompletedTask;
        }, cancellationToken);
        return instanceId;
    }

    private Task RecordEventAsync(
        string deploymentId, EventLevel level, string message, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
        {
            PipelineDb.AddEvent(db, deploymentId, level, message, now);
            var deployment = await db.Deployments
                .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
            if (deployment is not null)
            {
                PipelineDb.Touch(deployment, now);
            }

            await Task.CompletedTask;
        }, cancellationToken);
    }

    internal static string BuildServerName(string orgSlug, string appSlug)
    {
        var name = $"minicloud-{orgSlug}-production-{appSlug}".ToLowerInvariant();
        if (name.Length > 63)
        {
            throw new PipelineStepException(FailureCode, "Server name exceeds 63 characters; refusing ambiguous server discovery.");
        }

        return name;
    }

    internal static string BuildCloudInit(string orgSlug, string appSlug, string? sshPublicKey = null)
    {
        var lines = new List<string>
        {
            "#cloud-config",
            "package_update: true",
            "package_upgrade: false",
            $"hostname: minicloud-{orgSlug}-{appSlug}",
            "users:",
            "  - default",
            "  - name: minicloud",
            "    groups: sudo",
            "    shell: /bin/bash",
            "    sudo: ALL=(ALL) NOPASSWD:ALL",
            "    lock_passwd: true"
        };

        if (!string.IsNullOrWhiteSpace(sshPublicKey))
        {
            lines.Add("    ssh_authorized_keys:");
            lines.Add($"      - {sshPublicKey.Trim()}");
        }

        lines.AddRange(new[]
        {
            "runcmd:",
            "  - |",
            "    if [ ! -f /swapfile ]; then",
            "      fallocate -l 2G /swapfile || dd if=/dev/zero of=/swapfile bs=1M count=2048",
            "      chmod 600 /swapfile",
            "      mkswap /swapfile",
            "      swapon /swapfile",
            "      echo '/swapfile none swap sw 0 0' >> /etc/fstab",
            "      sysctl vm.swappiness=10",
            "      echo 'vm.swappiness=10' >> /etc/sysctl.d/99-minicloud-swap.conf",
            "    fi",
            "  - apt-get update",
            "  - apt-get install -y python3 ca-certificates curl gnupg jq ufw caddy docker.io docker-compose-v2 docker-buildx e2fsprogs util-linux",
            "  - usermod -aG docker minicloud",
            "  - mkdir -p /opt/minicloud/bin /opt/minicloud/caddy/apps /opt/minicloud/apps /opt/minicloud/logs",
            "  - printf 'import /opt/minicloud/caddy/apps/*.caddy\\n' > /etc/caddy/Caddyfile",
            "  - chown -R minicloud:minicloud /opt/minicloud/apps /opt/minicloud/logs",
            "  - ufw allow OpenSSH",
            "  - ufw allow 80/tcp",
            "  - ufw allow 443/tcp",
            "  - ufw --force enable",
            "  - systemctl enable --now docker",
            "  - systemctl enable --now caddy",
            "  - caddy validate --config /etc/caddy/Caddyfile",
            "  - systemctl reload caddy"
        });

        return string.Join("\n", lines);
    }

}
