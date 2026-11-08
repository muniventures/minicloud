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
using Minicloud.Common.Services;
using Minicloud.Common.Providers.Common;
using System.Net;
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

        var (orgSlug, appSlug, mapping) = await LoadAppScopeAsync(context, cancellationToken);
        context.OrganizationSlug = orgSlug;
        context.AppSlug = appSlug;

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
            if (context.ProviderAccountId != existing.ProviderAccountId)
            {
                throw new PipelineStepException(FailureCode, "Registered server account differs from the reserved placement; refusing migration.");
            }
            if (string.IsNullOrWhiteSpace(existing.ProviderServerId))
            {
                throw new PipelineStepException(FailureCode, "Registered server has no provider ID; refusing replacement provisioning.");
            }

            var registeredClient = await ResolveClientAsync(context, registeredProvider, cancellationToken);
            var status = await registeredClient
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

        var client = await ResolveClientAsync(context, mapping.Provider, cancellationToken);
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
                if (context.ProviderAccountId is not null)
                {
                    // A discovered resource is real even if readiness/SSH later
                    // fails or the caller is canceled. Preserve its original scope.
                    using var registration = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await MarkDiscoveredServerAsync(context, registration.Token);
                    await SaveServerInstanceAsync(context, mapping, found, registration.Token, ready: false);
                }
                server = found;
                _logger?.LogInformation("Found provider server {Name} ({Id})", serverName, found.Id);
            }
            else
            {
                await EnsureNewAppAsync(context, cancellationToken);
                await EnsureAllocationEnabledAsync(context, cancellationToken);
                _logger?.LogInformation(
                    "Provisioning new {Provider} server {Name} (plan {Plan}, region {Region})",
                    mapping.Provider, serverName, mapping.Sku, mapping.Region);
                await RecordEventAsync(context.DeploymentId, EventLevel.Info,
                    $"Provisioning new {mapping.Provider} server {serverName} (plan {mapping.Sku}, region {mapping.Region}).",
                    cancellationToken);

                await MarkCreationAttemptAsync(context, cancellationToken);
                try
                {
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
                    [$"minicloud-customer-{orgSlug}", $"minicloud-app-{appSlug}", "minicloud-env-production"],
                    Image: context.ProviderImage),
                        cancellationToken);
                }
                catch (ProviderApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
                    HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests)
                {
                    // A definite rejection did not allocate a server. Transport/5xx outcomes remain unresolved.
                    await ClearRejectedCreationAsync(context);
                    throw;
                }
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
                $"VPS provisioning failed for server {serverName}. Check the selected provider account and quota.");
        }

        var instanceId = await SaveServerInstanceAsync(context, mapping, server, cancellationToken);
        context.ServerInstanceId = instanceId;
        context.ServerIp = server.Ipv4Address;
        await RecordEventAsync(context.DeploymentId, EventLevel.Info,
            $"Server ready: {server.Id} ({server.Ipv4Address}).", cancellationToken);
    }

    private async Task<(string OrgSlug, string AppSlug, PlanMapping Mapping)> LoadAppScopeAsync(
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

        context.PlanId = app.PlanId;
        if (app.PlanId is null)
        {
            return (app.Organization.Slug, app.Slug, PlanMapping.FromAppPlan(app.Plan, _options));
        }
        var deployment = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == context.DeploymentId &&
            d.AppId == app.Id && d.OrganizationId == app.OrganizationId, cancellationToken);
        if (deployment?.PlanId != app.PlanId || string.IsNullOrWhiteSpace(deployment.ProviderAccountId) ||
            !Enum.TryParse<CloudProvider>(deployment.ProviderType, true, out var provider) || !Enum.IsDefined(provider) ||
            string.IsNullOrWhiteSpace(deployment.ProviderRegion) || string.IsNullOrWhiteSpace(deployment.ProviderSku))
        {
            throw new PipelineStepException(FailureCode, "Deployment has no complete provider-account reservation; refusing default-account provisioning.");
        }
        context.ProviderAccountId = deployment.ProviderAccountId;
        context.ProviderImage = deployment.ProviderImage;
        return (app.Organization.Slug, app.Slug, new PlanMapping(provider, deployment.ProviderRegion, deployment.ProviderSku));
    }

    private async Task<IVpsProviderClient> ResolveClientAsync(PipelineStepContext context, CloudProvider provider, CancellationToken ct)
    {
        if (context.ProviderAccountId is null) { return _providerResolver(provider); }
        await using var scope = _scopeFactory.CreateAsyncScope();
        var client = await scope.ServiceProvider.GetRequiredService<ProviderAccountClientFactory>()
            .CreateAsync(context.OrganizationId, context.ProviderAccountId, ct);
        if (client.Provider != provider) { throw new PipelineStepException(FailureCode, "Reserved provider type does not match its account."); }
        return client;
    }

    private async Task EnsureAllocationEnabledAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.ProviderAccountId is null) { return; }
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        if (!await db.ProviderAccounts.AnyAsync(x => x.Id == context.ProviderAccountId && x.OrganizationId == context.OrganizationId && x.Enabled, ct) ||
            !await db.ProviderPlans.AnyAsync(x => x.Id == context.PlanId && x.OrganizationId == context.OrganizationId && x.ProviderAccountId == context.ProviderAccountId && x.Enabled, ct))
        { throw new PipelineStepException(FailureCode, "The selected provider account or plan is disabled for allocation."); }
    }

    private async Task MarkCreationAttemptAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.ProviderAccountId is null) { return; }
        await PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, token) =>
        {
            if (db.Database.IsNpgsql())
            { await db.Apps.FromSqlInterpolated($"SELECT * FROM apps WHERE id = {context.AppId} FOR UPDATE").AsNoTracking().SingleAsync(token); }
            if (await db.Deployments.AnyAsync(x => x.AppId == context.AppId && x.ProviderCreationAttempted, token))
            { throw new PipelineStepException(FailureCode, "A prior server allocation is unresolved; reconcile the original provider account before retrying creation."); }
            var deployment = await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId && x.AppId == context.AppId &&
                x.OrganizationId == context.OrganizationId && x.ProviderAccountId == context.ProviderAccountId, token);
            if (deployment.CancellationRequestedAt is not null ||
                (!string.IsNullOrEmpty(context.OwnershipToken) && deployment.OwnershipToken != context.OwnershipToken))
            { throw new PipelineStepException(FailureCode, "Deployment no longer owns server provisioning."); }
            deployment.ProviderCreationAttempted = true;
        }, ct);
    }

    private async Task ClearRejectedCreationAsync(PipelineStepContext context)
    {
        if (context.ProviderAccountId is null) { return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
        {
            var deployment = await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId && x.AppId == context.AppId &&
                x.OrganizationId == context.OrganizationId && x.ProviderAccountId == context.ProviderAccountId, ct);
            deployment.ProviderCreationAttempted = false;
        }, timeout.Token);
    }

    private Task MarkDiscoveredServerAsync(PipelineStepContext context, CancellationToken ct) =>
        PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, token) =>
        {
            var deployment = await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId && x.AppId == context.AppId &&
                x.OrganizationId == context.OrganizationId && x.ProviderAccountId == context.ProviderAccountId, token);
            deployment.ProviderCreationAttempted = true;
        }, ct);

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
        if (context.PlanId is not null && servers.Count == 0 && runtimes.Count > 0)
        { throw new PipelineStepException(FailureCode, "Registered server placement is missing; reconcile the provider account and region before deploying."); }
        var identities = servers.Select(s => (s.ProviderAccountId, s.Provider.ToLowerInvariant(), s.ProviderServerId))
            .Concat(runtimes.Select(s => (s.ProviderAccountId, s.Provider.ToLowerInvariant(), s.ProviderServerId)))
            .Distinct().ToList();
        if (identities.Count > 1 || identities.Any(x => string.IsNullOrWhiteSpace(x.Item3)))
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
                s.ProviderAccountId == identity.Item1 && s.Provider.ToLower() == identity.Item2 && s.ProviderServerId == identity.Item3 &&
                (s.OrganizationId != context.OrganizationId || s.AppId != context.AppId), cancellationToken) ||
            await db.RuntimeServers.AnyAsync(s =>
                s.ProviderAccountId == identity.Item1 && s.Provider.ToLower() == identity.Item2 && s.ProviderServerId == identity.Item3 &&
                (s.OrganizationId != context.OrganizationId || s.AppId != context.AppId), cancellationToken)))
        {
            throw new PipelineStepException(FailureCode, "Registered provider server belongs to another app; refusing deployment.");
        }

        return servers.SingleOrDefault() ?? runtimes.Select(s => new ServerInstance
        {
            ProviderAccountId = s.ProviderAccountId,
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
            if (await db.ServerInstances.AnyAsync(s => s.ProviderAccountId == context.ProviderAccountId && s.Provider.ToLower() == mapping.Provider.ToString().ToLowerInvariant() &&
                    s.ProviderServerId == server.Id && (s.AppId != context.AppId || s.OrganizationId != context.OrganizationId), ct) ||
                await db.RuntimeServers.AnyAsync(s => s.ProviderAccountId == context.ProviderAccountId && s.Provider.ToLower() == mapping.Provider.ToString().ToLowerInvariant() &&
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
                if (existing.ProviderAccountId != context.ProviderAccountId || existing.ProviderServerId != server.Id)
                { throw new PipelineStepException(FailureCode, "Server identity changed during provisioning; refusing replacement."); }
                existing.ProviderAccountId = context.ProviderAccountId;
                existing.PlanId = context.PlanId;
                existing.Sku ??= mapping.Sku;
                existing.Image ??= context.ProviderImage;
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
                    ProviderAccountId = context.ProviderAccountId,
                    PlanId = context.PlanId,
                    Sku = mapping.Sku,
                    Image = context.ProviderImage,
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
