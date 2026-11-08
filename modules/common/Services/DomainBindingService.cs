using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class DomainBindingService(
    MinicloudDbContext db,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IOptions<ManagedDomainOptions>? managedDomains = null)
{
    private readonly ManagedDomainOptions _managedDomains = managedDomains?.Value ?? new();
    public const string MinicloudSuffix = "app.muni.dev";
    public const string KindCustomDomain = "custom_domain";
    public const string StatusPendingDns = "pending_dns";
    public const string KindMinicloudSubdomain = "minicloud_subdomain";
    public const string DnsPending = "pending";
    public const string DnsVerified = "verified";
    public const string ApplyPending = "pending";
    public const string ApplyApplied = "applied";
    public const string ApplyFailed = "failed";
    public const string SslPending = "pending";
    public const string SslReady = "ready";
    public const string StatusPendingApply = "pending_apply";
    public const string StatusSslPending = "ssl_pending";
    public const string StatusActive = "active";
    public const string StatusFailed = "failed";
    public const string StatusDisabled = "disabled";

    public async Task<IReadOnlyList<Domain>> EnsureDefaultBindingsAsync(
        Organization organization,
        MinicloudApp app,
        IReadOnlyList<DeploymentServiceRequest> services,
        CancellationToken cancellationToken)
    {
        var publicServices = services.Where(x => x.Public).ToArray();
        var serviceNames = publicServices.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var requestedNames = services.Select(x => x.Name).ToArray();
        var existing = await db.Domains
            .Where(x => x.AppId == app.Id && requestedNames.Contains(x.ServiceName))
            .ToListAsync(cancellationToken);
        var byService = existing.Select(x => x.ServiceName).ToHashSet(StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        foreach (var binding in existing.Where(x => !serviceNames.Contains(x.ServiceName) && x.Status != StatusDisabled))
        {
            binding.Status = StatusDisabled;
            binding.ApplyStatus = ApplyPending;
            binding.UpdatedAt = now;
        }

        var parentSlug = app.ParentAppId is null
            ? null
            : await db.Apps.Where(x => x.Id == app.ParentAppId).Select(x => x.Slug).SingleAsync(cancellationToken);

        foreach (var service in publicServices)
        {
            if (byService.Contains(service.Name))
            {
                continue;
            }

            var hostname = app.BranchName is null
                ? DefaultHostname(organization.Slug, app.Slug, service.Name, _managedDomains.Suffix)
                : DefaultBranchHostname(organization.Slug, parentSlug!, service.Name, app.BranchName!, _managedDomains.Suffix);
            if (await db.Domains.AnyAsync(x => x.Hostname == hostname, cancellationToken))
            {
                throw new DomainBindingConflictException("domain_hostname_taken", "Default service subdomain is already assigned.");
            }

            var binding = CreateBinding(organization.Id, app.Id, service.Name, hostname, now, isPrimary: true);
            db.Domains.Add(binding);
            existing.Add(binding);
        }

        return existing
            .Where(x => serviceNames.Contains(x.ServiceName))
            .OrderBy(x => x.ServiceName, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<DomainBindingCreateResult> CreateSubdomainBindingAsync(
        MinicloudApp app,
        string serviceName,
        string? label,
        IReadOnlyList<DeploymentServiceRequest> activeServices,
        CancellationToken cancellationToken)
    {
        var service = activeServices.SingleOrDefault(x => string.Equals(x.Name, serviceName, StringComparison.Ordinal));
        if (service is null)
        {
            return DomainBindingCreateResult.Validation("serviceName", "Service does not exist in the latest deployed service set.");
        }

        if (!service.Public)
        {
            return DomainBindingCreateResult.Validation("serviceName", "Private services cannot receive subdomains.");
        }

        var normalizedLabel = string.IsNullOrWhiteSpace(label)
            ? DefaultLabel(app.Organization.Slug, app.Slug, service.Name)
            : label.Trim().ToLowerInvariant();
        var availability = await CheckSubdomainAvailabilityAsync(normalizedLabel, cancellationToken);
        if (!availability.IsValid)
        {
            return DomainBindingCreateResult.Validation("label", "Subdomain label is invalid.");
        }

        if (!availability.Available)
        {
            return DomainBindingCreateResult.Conflict("domain_hostname_taken", "Subdomain is already assigned.");
        }

        var now = timeProvider.GetUtcNow();
        var binding = CreateBinding(app.OrganizationId, app.Id, service.Name, availability.Hostname, now);
        db.Domains.Add(binding);
        return DomainBindingCreateResult.Created(binding);
    }

    public async Task<DomainBindingCreateResult> CreateCustomBindingAsync(
        MinicloudApp app, string serviceName, string hostname,
        IReadOnlyList<DeploymentServiceRequest> activeServices, CancellationToken cancellationToken)
    {
        if (!activeServices.Any(x => x.Name == serviceName && x.Public))
        {
            return DomainBindingCreateResult.Validation("serviceName", "Choose an existing public service.");
        }
        var normalized = NormalizeCustomHostname(hostname);
        if (normalized is null || _managedDomains.IsReserved(normalized))
        {
            return DomainBindingCreateResult.Validation("hostname", "Enter an apex domain or subdomain, without a URL, port, path or wildcard. Minicloud domains are reserved.");
        }
        if (await db.Domains.AnyAsync(x => x.Hostname == normalized, cancellationToken))
        {
            return DomainBindingCreateResult.Conflict("domain_hostname_taken", "Domain is already assigned.");
        }
        var registered = await db.CustomDomainRegistrations
            .Where(x => (x.Hostname == normalized || normalized.EndsWith("." + x.Hostname)))
            .OrderByDescending(x => x.Hostname.Length).FirstOrDefaultAsync(cancellationToken);
        if (registered?.AppId != app.Id || registered.OrganizationId != app.OrganizationId || registered.Status != DnsVerified || registered.LastCheckedAt is null || registered.LastCheckedAt < timeProvider.GetUtcNow().AddMinutes(-5))
        {
            return DomainBindingCreateResult.Validation("hostname", "Verify domain ownership before connecting it to a service.");
        }
        var binding = CreateBinding(app.OrganizationId, app.Id, serviceName, normalized, timeProvider.GetUtcNow());
        binding.CustomDomainRegistrationId = registered.Id;
        binding.Kind = KindCustomDomain;
        binding.VerificationToken = registered.VerificationToken;
        binding.Status = StatusPendingDns;
        binding.TargetIp = await db.ServerInstances.Where(x => x.AppId == app.Id && x.OrganizationId == app.OrganizationId)
            .Select(x => x.IpAddress).FirstOrDefaultAsync(cancellationToken);
        db.Domains.Add(binding);
        return DomainBindingCreateResult.Created(binding);
    }

    public static string? NormalizeCustomHostname(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string hostname;
        try
        {
            var input = value.Trim();
            hostname = new System.Globalization.IdnMapping().GetAscii(input.EndsWith('.') ? input[..^1] : input).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
        var labels = hostname.Split('.');
        return ("_minicloud-verification." + hostname).Length <= 253 && labels.Length >= 2 && labels.All(IsValidLabel) &&
            char.IsAsciiLetterLower(labels[^1][0]) && !System.Net.IPAddress.TryParse(hostname, out _)
            ? hostname : null;
    }

    public async Task<DomainAvailabilityResult> CheckSubdomainAvailabilityAsync(string? label, CancellationToken cancellationToken)
    {
        var normalizedLabel = label?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidLabel(normalizedLabel))
        {
            return new DomainAvailabilityResult(normalizedLabel, string.Empty, false, false, "Subdomain label is invalid.");
        }

        var hostname = $"{normalizedLabel}.{_managedDomains.Suffix}";
        var assigned = await db.Domains.AnyAsync(x => x.Hostname == hostname, cancellationToken);
        return new DomainAvailabilityResult(
            normalizedLabel,
            hostname,
            !assigned,
            true,
            assigned ? "Subdomain is already assigned." : null);
    }

    public static DomainBindingResponse ToResponse(Domain binding) =>
        new(
            binding.Id,
            binding.AppId,
            binding.ServiceName,
            binding.Hostname,
            binding.Kind,
            binding.PathPrefix,
            binding.DnsStatus,
            binding.ApplyStatus,
            binding.SslStatus,
            binding.Status,
            binding.IsPrimary,
            binding.FailureCode,
            binding.FailureMessage,
            binding.LastAppliedAt,
            binding.CreatedAt,
            binding.UpdatedAt,
            binding.TargetIp,
            binding.VerificationToken,
            binding.LastCheckedAt);

    public static string DefaultHostname(string organizationSlug, string appSlug, string serviceName, string suffix = MinicloudSuffix) =>
        $"{DefaultLabel(organizationSlug, appSlug, serviceName)}.{suffix}";

    public static string DefaultBranchHostname(string organizationSlug, string appSlug, string serviceName, string branchName, string suffix = MinicloudSuffix) =>
        $"{CompactLabel($"{organizationSlug}-{appSlug}-{serviceName}-{branchName}")}.{suffix}";

    private Domain CreateBinding(string organizationId, string appId, string serviceName, string hostname, DateTimeOffset now, bool isPrimary = false) =>
        new()
        {
            Id = ids.NewId("dom"),
            OrganizationId = organizationId,
            AppId = appId,
            ServiceName = serviceName,
            Hostname = hostname,
            Kind = KindMinicloudSubdomain,
            PathPrefix = "/",
            DnsStatus = DnsPending,
            ApplyStatus = ApplyPending,
            SslStatus = SslPending,
            Status = StatusPendingApply,
            IsPrimary = isPrimary,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static string DefaultLabel(string organizationSlug, string appSlug, string serviceName)
    {
        return CompactLabel($"{organizationSlug}-{appSlug}-{serviceName}");
    }

    private static string CompactLabel(string value)
    {
        var label = value.ToLowerInvariant();
        if (label.Length <= 63)
        {
            return label;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(label)))[..8].ToLowerInvariant();
        return $"{label[..54].TrimEnd('-')}-{hash}";
    }

    private static bool IsValidLabel(string label)
    {
        if (label.Length is < 1 or > 63 || label.StartsWith("-", StringComparison.Ordinal) || label.EndsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        return label.All(character => char.IsAsciiLetterLower(character) || char.IsDigit(character) || character == '-');
    }
}

public sealed class DomainBindingConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record DomainBindingCreateResult(
    Domain? Binding,
    string? ErrorField,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Binding is not null;
    public bool IsConflict => ErrorCode is not null;

    public static DomainBindingCreateResult Created(Domain binding) =>
        new(binding, null, null, null);

    public static DomainBindingCreateResult Validation(string field, string message) =>
        new(null, field, null, message);

    public static DomainBindingCreateResult Conflict(string code, string message) =>
        new(null, null, code, message);
}

public sealed record DomainAvailabilityResult(
    string Label,
    string Hostname,
    bool Available,
    bool IsValid,
    string? Message);
