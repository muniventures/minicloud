using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;
using Minicloud.Common.Providers.Dns;

namespace Minicloud.Worker.Pipeline.Steps;

/// <summary>
/// Step 2: compute public hostnames and upsert Cloudflare DNS records
/// pointing at the provisioned server IP, then wait for propagation.
/// DNS failures surface as <c>provider_provision_failed</c> since they
/// block the provisioning phase.
/// </summary>
public sealed class DnsSetupStep : IPipelineStep
{
    public string Name => "dns-setup";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICloudflareDnsClient _dnsClient;
    private readonly DeploymentPipelineOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DnsSetupStep>? _logger;
    private readonly ManagedDomainOptions _managedDomains;

    public DnsSetupStep(
        IServiceScopeFactory scopeFactory,
        ICloudflareDnsClient dnsClient,
        IOptions<DeploymentPipelineOptions> options,
        TimeProvider timeProvider,
        ILogger<DnsSetupStep>? logger = null,
        IOptions<ManagedDomainOptions>? managedDomains = null)
        : this(scopeFactory, dnsClient, options.Value, timeProvider, logger, managedDomains?.Value)
    {
    }

    public DnsSetupStep(
        IServiceScopeFactory scopeFactory,
        ICloudflareDnsClient dnsClient,
        DeploymentPipelineOptions options,
        TimeProvider timeProvider,
        ILogger<DnsSetupStep>? logger = null,
        ManagedDomainOptions? managedDomains = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(dnsClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _dnsClient = dnsClient;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _managedDomains = managedDomains ?? new();
    }

    public async Task ExecuteAsync(PipelineStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.ServerIp))
        {
            throw new PipelineStepException(
                ResolveProvisionServerStep.FailureCode,
                "DNS setup requires a provisioned server IP, but none was resolved.");
        }

        var hostnames = context.DnsHostnamesResolved
            ? context.Hostnames.ToList()
            : await ResolveAndAssignHostnamesAsync(context, cancellationToken);
        if (hostnames.Count == 0)
        {
            _logger?.LogInformation("Deployment {DeploymentId} has no public services; skipping DNS setup", context.DeploymentId);
            await RecordEventAsync(context.DeploymentId, context.OwnershipToken, EventLevel.Info,
                "No public services; DNS setup skipped.", cancellationToken);
            return;
        }

        try
        {
            await Parallel.ForEachAsync(
                hostnames.Where(host => _managedDomains.Contains(host)),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Min(_options.MaxParallelNetworkOperations, hostnames.Count),
                },
                async (hostname, ct) =>
                {
                    _logger?.LogInformation("Upserting DNS record {Hostname} -> {Ip}", hostname, context.ServerIp);
                    await _dnsClient.UpsertDnsRecordAsync(hostname, context.ServerIp, ct);
                    await _dnsClient.WaitForPropagationAsync(hostname, context.ServerIp, _options.DnsPropagationTimeout, ct);
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PipelineStepException(
                ResolveProvisionServerStep.FailureCode,
                $"DNS setup failed: {ex.Message}", ex);
        }

        await PersistDnsStateAsync(context, hostnames, cancellationToken);
        await RecordEventAsync(context.DeploymentId, context.OwnershipToken, EventLevel.Info,
            $"DNS ready: {string.Join(", ", hostnames)} -> {context.ServerIp}.", cancellationToken);
    }

    public async Task<List<string>> ResolveAndAssignHostnamesAsync(
        PipelineStepContext context, CancellationToken cancellationToken)
    {
        var serviceHostnames = await ResolveHostnamesAsync(context, cancellationToken);
        context.ServiceHostnames.Clear();
        foreach (var (serviceName, hostname) in serviceHostnames)
        {
            context.ServiceHostnames[serviceName] = hostname;
        }

        var hostnames = serviceHostnames.Values.SelectMany(hosts => hosts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        context.Hostnames.Clear();
        context.Hostnames.AddRange(hostnames);
        context.DnsHostnamesResolved = true;
        return hostnames;
    }

    private async Task<Dictionary<string, string[]>> ResolveHostnamesAsync(
        PipelineStepContext context, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();

        var deployment = await db.Deployments
            .AsNoTracking()
            .Include(d => d.Services)
            .FirstOrDefaultAsync(d => d.Id == context.DeploymentId &&
                d.OwnershipToken == context.OwnershipToken, cancellationToken);
        if (deployment is null)
        {
            throw new PipelineStepException(
                ResolveProvisionServerStep.FailureCode,
                $"Deployment {context.DeploymentId} not found.");
        }

        if (!string.Equals(deployment.OrganizationId, context.OrganizationId, StringComparison.Ordinal) ||
            !string.Equals(deployment.AppId, context.AppId, StringComparison.Ordinal))
        {
            throw new PipelineStepException(
                ResolveProvisionServerStep.FailureCode,
                "Deployment organization/app scope mismatch.");
        }

        var publicServices = deployment.Services
            .Where(s => s.Public)
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        if (publicServices.Count == 0)
        {
            return new Dictionary<string, string[]>(StringComparer.Ordinal);
        }

        var serviceNames = publicServices.Select(s => s.Name).ToList();
        var bindings = await db.Domains
            .AsNoTracking()
            .Where(x => x.AppId == context.AppId && serviceNames.Contains(x.ServiceName))
            .ToListAsync(cancellationToken);

        var hostnames = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var service in publicServices)
        {
            var serviceBindings = bindings.Where(binding =>
                string.Equals(binding.ServiceName, service.Name, StringComparison.Ordinal)).ToArray();
            var enabledHosts = serviceBindings
                .Where(binding => binding.Status != DomainBindingService.StatusDisabled &&
                    (binding.Kind == DomainBindingService.KindMinicloudSubdomain ||
                     (binding.Kind == DomainBindingService.KindCustomDomain && binding.DnsStatus == DomainBindingService.DnsVerified && binding.TargetIp == context.ServerIp)) &&
                    !string.IsNullOrWhiteSpace(binding.Hostname))
                .Select(binding => binding.Hostname)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (enabledHosts.Length > 0)
            {
                hostnames[service.Name] = enabledHosts;
            }
            else if (serviceBindings.Length == 0)
            {
                hostnames[service.Name] = [DomainBindingService.DefaultHostname(
                    context.OrganizationSlug, context.AppSlug, service.Name, _managedDomains.Suffix)];
            }
        }

        return hostnames;
    }

    private Task PersistDnsStateAsync(
        PipelineStepContext context, List<string> hostnames, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
        {
            var deployment = await db.Deployments
                .FirstOrDefaultAsync(d => d.Id == context.DeploymentId &&
                    d.OwnershipToken == context.OwnershipToken, ct);
            if (deployment is null)
            {
                return;
            }

            var domains = await db.Domains
                .Where(x => x.AppId == context.AppId && x.Kind == DomainBindingService.KindMinicloudSubdomain && hostnames.Contains(x.Hostname))
                .ToListAsync(ct);
            foreach (var domain in domains)
            {
                domain.TargetIp = context.ServerIp;
                domain.DnsStatus = DomainBindingService.DnsVerified;
                domain.UpdatedAt = now;
            }

            if (string.IsNullOrWhiteSpace(deployment.Domain))
            {
                deployment.Domain = hostnames[0];
            }
            PipelineDb.Touch(deployment, now);

            await Task.CompletedTask;
        }, cancellationToken);
    }

    private Task RecordEventAsync(
        string deploymentId, string ownershipToken, EventLevel level, string message, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
        {
            var deployment = await db.Deployments
                .FirstOrDefaultAsync(d => d.Id == deploymentId &&
                    d.OwnershipToken == ownershipToken, ct);
            if (deployment is not null)
            {
                PipelineDb.AddEvent(db, deploymentId, level, message, now);
                PipelineDb.Touch(deployment, now);
            }

            await Task.CompletedTask;
        }, cancellationToken);
    }
}
