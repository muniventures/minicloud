using System.Net;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers.Dns;

namespace Minicloud.Common.Services;

public sealed class CustomDomainVerificationService(
    MinicloudDbContext db, ICustomDomainDnsResolver dns, TimeProvider clock, AuditEventService? audit = null)
{
    public async Task VerifyAsync(Domain binding, CancellationToken cancellationToken)
    {
        if (binding.Kind != DomainBindingService.KindCustomDomain || binding.Status == DomainBindingService.StatusDisabled)
        {
            return;
        }
        if (await db.Deployments.AnyAsync(x => x.AppId == binding.AppId && x.OwnershipToken != null, cancellationToken))
        {
            return;
        }
        var servers = await db.ServerInstances.AsNoTracking()
            .Where(x => x.AppId == binding.AppId && x.OrganizationId == binding.OrganizationId).ToListAsync(cancellationToken);
        var server = servers.Count == 1 ? servers[0] : null;
        var target = server?.IpAddress;
        var failure = "server_pending";
        var message = "Deploy the app before configuring DNS.";
        var verified = false;
        if (server is not null && !string.IsNullOrWhiteSpace(server.ProviderServerId) && IPAddress.TryParse(target, out var expected) &&
            !await db.ServerInstances.AnyAsync(x => x.ProviderAccountId == server.ProviderAccountId && x.Provider.ToLower() == server.Provider.ToLower() && x.ProviderServerId == server.ProviderServerId &&
                (x.AppId != binding.AppId || x.OrganizationId != binding.OrganizationId), cancellationToken) &&
            !await db.RuntimeServers.AnyAsync(x => x.ProviderAccountId == server.ProviderAccountId && x.Provider.ToLower() == server.Provider.ToLower() && x.ProviderServerId == server.ProviderServerId &&
                (x.AppId != binding.AppId || x.OrganizationId != binding.OrganizationId), cancellationToken))
        {
            try
            {
                var registration = await db.CustomDomainRegistrations.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.AppId == binding.AppId && x.OrganizationId == binding.OrganizationId && x.VerificationToken == binding.VerificationToken, cancellationToken);
                var txt = registration is null ? Array.Empty<string>() : await dns.QueryAsync("_minicloud-verification." + registration.Hostname, 16, cancellationToken);
                var a = await dns.QueryAsync(binding.Hostname, 1, cancellationToken);
                var aaaa = await dns.QueryAsync(binding.Hostname, 28, cancellationToken);
                var addresses = a.Concat(aaaa).ToArray();
                var ownership = binding.VerificationToken is not null && txt.Any(x => x.Trim('"') == binding.VerificationToken);
                verified = ownership && addresses.Length > 0 && addresses.All(x => IPAddress.TryParse(x, out var ip) && ip.Equals(expected));
                failure = ownership ? "dns_address_mismatch" : "dns_ownership_pending";
                message = ownership
                    ? "Point this hostname to the displayed server address. Remove conflicting A/AAAA records and disable DNS proxying."
                    : "Add the exact TXT verification record shown below and wait for DNS propagation.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failure = "dns_lookup_failed";
                message = "DNS lookup could not complete. Try verification again shortly.";
            }
        }
        // Do not overwrite deletion, disable, or another verification while DNS was in flight.
        var originalUpdatedAt = binding.UpdatedAt;
        await db.Entry(binding).ReloadAsync(cancellationToken);
        if (db.Entry(binding).State == EntityState.Detached || binding.Status == DomainBindingService.StatusDisabled || binding.UpdatedAt != originalUpdatedAt ||
            await db.Apps.AnyAsync(x => x.Id == binding.AppId && x.DeletionStatus != null, cancellationToken) ||
            await db.Deployments.AnyAsync(x => x.AppId == binding.AppId && x.OwnershipToken != null, cancellationToken))
        {
            return;
        }
        if (verified && !await db.ServerInstances.AnyAsync(x => x.Id == server!.Id && x.ProviderAccountId == server.ProviderAccountId && x.ProviderServerId == server.ProviderServerId && x.IpAddress == target && x.AppId == binding.AppId, cancellationToken))
        {
            verified = false;
        }
        var changed = binding.DnsStatus != (verified ? DomainBindingService.DnsVerified : DomainBindingService.DnsPending) || binding.TargetIp != target;
        var diagnosticChanged = !verified && binding.FailureCode != failure;
        binding.TargetIp = target;
        binding.LastCheckedAt = clock.GetUtcNow();
        binding.DnsStatus = verified ? DomainBindingService.DnsVerified : DomainBindingService.DnsPending;
        if (changed || !verified)
        {
            binding.FailureCode = verified ? null : failure;
            binding.FailureMessage = verified ? null : message;
            binding.ApplyStatus = DomainBindingService.ApplyPending;
            binding.SslStatus = DomainBindingService.SslPending;
            binding.Status = verified ? DomainBindingService.StatusPendingApply : DomainBindingService.StatusPendingDns;
            binding.UpdatedAt = clock.GetUtcNow();
        }
        if ((changed || diagnosticChanged) && audit is not null)
        {
            await audit.AppendAsync(binding.OrganizationId, binding.AppId, "system", verified ? "domain.dns_verified" : "domain.dns_failed", binding.Id, cancellationToken);
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
