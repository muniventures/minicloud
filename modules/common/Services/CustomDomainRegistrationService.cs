using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers.Dns;

namespace Minicloud.Common.Services;

public sealed class CustomDomainRegistrationService(
    MinicloudDbContext db, IIdGenerator ids, ICustomDomainDnsResolver dns, TimeProvider clock, AuditEventService audit,
    IOptions<ManagedDomainOptions>? managedDomains = null)
{
    public async Task<CustomDomainRegistration> RegisterAsync(string organizationId, string appId, string hostname, CancellationToken ct, string actor = "system")
    {
        if (!await db.Apps.AnyAsync(x => x.Id == appId && x.OrganizationId == organizationId && x.DeletionStatus == null, ct))
        {
            throw new ArgumentException("App is unavailable.");
        }
        var normalized = DomainBindingService.NormalizeCustomHostname(hostname);
        if (normalized is null || managedDomains?.Value.Contains(normalized) == true)
        {
            throw new ArgumentException("Enter a domain without a URL, port, path or wildcard.");
        }
        var existing = await db.CustomDomainRegistrations.SingleOrDefaultAsync(x => x.Hostname == normalized, ct);
        if (existing is not null)
        {
            if (existing.OrganizationId == organizationId && existing.AppId is null)
            {
                if (await db.Domains.AnyAsync(x => x.VerificationToken == existing.VerificationToken &&
                    (x.AppId != appId || x.OrganizationId != organizationId), ct))
                {
                    throw new DomainBindingConflictException("domain_assignment_conflict", "Resolve this domain's existing service assignments before assigning it to an app.");
                }
                var claimed = await db.CustomDomainRegistrations.Where(x => x.Id == existing.Id && x.AppId == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.AppId, appId)
                        .SetProperty(x => x.Status, "pending")
                        .SetProperty(x => x.LastCheckedAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.FailureMessage, "Verify ownership before assigning service hostnames.")
                        .SetProperty(x => x.UpdatedAt, clock.GetUtcNow()), ct);
                if (claimed != 1)
                {
                    throw new DomainBindingConflictException("domain_hostname_taken", "Domain is already registered.");
                }
                await db.Entry(existing).ReloadAsync(ct);
                await audit.AppendAsync(organizationId, appId, actor, "domain.ownership_assigned", existing.Id, ct);
                return existing;
            }
            if (existing.OrganizationId != organizationId || existing.AppId != appId)
            {
                throw new DomainBindingConflictException("domain_hostname_taken", "Domain is already registered.");
            }
            return existing;
        }
        if (await db.Domains.AnyAsync(x => x.Hostname == normalized && (x.OrganizationId != organizationId || x.AppId != appId), ct))
        {
            throw new DomainBindingConflictException("domain_hostname_taken", "Domain is already assigned.");
        }
        var row = new CustomDomainRegistration
        {
            Id = ids.NewId("cdr"), OrganizationId = organizationId, AppId = appId, Hostname = normalized,
            VerificationToken = "mc-domain-verification=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow()
        };
        db.CustomDomainRegistrations.Add(row);
        await audit.AppendAsync(organizationId, appId, actor, "domain.registered", row.Id, ct);
        return row;
    }

    public async Task<bool> RemoveAsync(string appId, string id, string actor, CancellationToken ct)
    {
        var row = await db.CustomDomainRegistrations.SingleOrDefaultAsync(x => x.Id == id && x.AppId == appId, ct);
        if (row is null)
        {
            return false;
        }
        if (await db.Domains.AnyAsync(x => x.CustomDomainRegistrationId == row.Id || x.VerificationToken == row.VerificationToken, ct))
        {
            throw new DomainBindingConflictException("domain_in_use", "Remove this domain's service hostnames before removing it from the app.");
        }
        db.CustomDomainRegistrations.Remove(row);
        try
        {
            await audit.AppendAsync(row.OrganizationId, appId, actor, "domain.registration_removed", row.Id, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23503" } ||
            ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 787 })
        {
            // The foreign key also protects assignments created after the check.
            throw new DomainBindingConflictException("domain_in_use", "The domain changed or is in use. Refresh and remove its service hostnames first.");
        }
        return true;
    }

    public async Task VerifyAsync(CustomDomainRegistration row, CancellationToken ct, string actor = "system")
    {
        if (row.AppId is null)
        {
            return;
        }
        var verified = false;
        var failure = "The TXT record is not visible yet. Check its name and value, then try again.";
        try
        {
            var answers = await dns.QueryAsync("_minicloud-verification." + row.Hostname, 16, ct);
            verified = answers.Any(x => x.Trim('"') == row.VerificationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failure = "DNS lookup could not complete. Try again shortly.";
        }
        var originalUpdatedAt = row.UpdatedAt;
        await db.Entry(row).ReloadAsync(ct);
        if (db.Entry(row).State == EntityState.Detached || row.UpdatedAt != originalUpdatedAt)
        {
            return;
        }
        var changed = row.Status != (verified ? "verified" : "pending");
        row.Status = verified ? "verified" : "pending";
        row.FailureMessage = verified ? null : failure;
        row.LastCheckedAt = clock.GetUtcNow();
        row.UpdatedAt = clock.GetUtcNow();
        if (!verified)
        {
            var bindings = await db.Domains.Where(x => x.OrganizationId == row.OrganizationId && x.AppId == row.AppId &&
                x.Kind == DomainBindingService.KindCustomDomain && x.VerificationToken == row.VerificationToken).ToListAsync(ct);
            foreach (var binding in bindings.Where(x => x.Status != DomainBindingService.StatusDisabled))
            {
                binding.DnsStatus = DomainBindingService.DnsPending;
                binding.Status = DomainBindingService.StatusPendingDns;
                binding.ApplyStatus = DomainBindingService.ApplyPending;
                binding.SslStatus = DomainBindingService.SslPending;
                binding.FailureCode = "dns_ownership_pending";
                binding.FailureMessage = "The domain ownership TXT record is missing or could not be verified. Restore it and verify ownership again.";
                binding.UpdatedAt = clock.GetUtcNow();
            }
        }
        if (changed)
        {
            await audit.AppendAsync(row.OrganizationId, row.AppId, actor, verified ? "domain.ownership_verified" : "domain.ownership_lost", row.Id, ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }
    }

    public static CustomDomainRegistrationResponse ToResponse(CustomDomainRegistration row) =>
        new(row.Id, row.AppId, row.Hostname, row.VerificationToken, row.Status, row.FailureMessage, row.LastCheckedAt);
}
