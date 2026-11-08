using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;

namespace Minicloud.Common.Services;

public sealed class ProviderAccountService(MinicloudDbContext db, ISecretStore secrets,
    ProviderAccountClientFactory clients, IIdGenerator ids, TimeProvider clock, IOptions<OpenBaoSecretStoreOptions> options)
{
    public async Task<ProviderAccount> CreateAsync(CreateProviderRequest request, CancellationToken ct)
    {
        ValidateName(request.Name);
        if (!TryParseType(request.ProviderType, out var type))
            throw new ProviderCatalogException("provider_type_unsupported", "Select Hetzner, DigitalOcean or Vultr.");
        if (request.DefaultOsId is <= 0 || request.ProjectId is { Length: > 128 } || request.ProjectId?.Any(char.IsControl) == true)
            throw new ProviderCatalogException("provider_settings_invalid", "The provider account settings are invalid.");
        var now = clock.GetUtcNow();
        var account = new ProviderAccount
        {
            Id = ids.NewId("provider"), OrganizationId = request.OrganizationId, Name = request.Name.Trim(), ProviderType = type,
            ProjectId = request.ProjectId, DefaultOsId = request.DefaultOsId ?? (type == CloudProvider.Vultr ? 2284 : null),
            CreatedAt = now, UpdatedAt = now
        };
        var reference = await WriteCandidateAsync(account, request.Credential, ct);
        await ProbeAsync(clients.CreateCandidate(account, request.Credential), ct);
        await RejectKnownScopeAliasAsync(account, clients.CreateCandidate(account, request.Credential), ct);
        AssignCredential(account, reference);
        account.ValidationStatus = "valid"; account.LastValidatedAt = now;
        db.ProviderAccounts.Add(account);
        return account;
    }

    public void Update(ProviderAccount account, UpdateProviderRequest request)
    {
        if (request.Revision != account.Revision) throw ProviderCatalogService.Stale();
        if (account.ValidationStatus == "deleting")
            throw new ProviderCatalogException("provider_deleting", "Provider credential cleanup is pending. Retry deletion.");
        if (request.Name is not null) { ValidateName(request.Name); account.Name = request.Name.Trim(); }
        account.Enabled = request.Enabled ?? account.Enabled;
        account.Revision++; account.UpdatedAt = clock.GetUtcNow();
    }

    public async Task ReplaceCredentialsAsync(ProviderAccount account, ReplaceProviderCredentialRequest request, CancellationToken ct)
    {
        if (request.Revision != account.Revision) throw ProviderCatalogService.Stale();
        if (account.ValidationStatus == "deleting") throw new ProviderCatalogException("provider_deleting", "Credential cleanup is pending.");
        var reference = await WriteCandidateAsync(account, request.Credential, ct);
        var generation = account.CredentialGeneration;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProviderCatalogService.LockAccountAsync(db, account.OrganizationId, account.Id, ct);
        if (request.Revision != account.Revision) throw ProviderCatalogService.Stale();
        if (account.ValidationStatus == "deleting") throw new ProviderCatalogException("provider_deleting", "Credential cleanup is pending.");
        var candidate = clients.CreateCandidate(account, request.Credential);
        await ProbeAsync(candidate, ct);
        await VerifyExistingScopeAsync(account, candidate, ct);
        await RejectKnownScopeAliasAsync(account, candidate, ct);
        AssignCredential(account, reference);
        account.CredentialGeneration = generation;
        account.ValidationStatus = "valid"; account.LastValidatedAt = clock.GetUtcNow();
        account.Revision++; account.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<ProviderValidationResponse> ValidateAsync(ProviderAccount account, CancellationToken ct)
    {
        if (account.ValidationStatus == "deleting")
            throw new ProviderCatalogException("provider_deleting", "Provider credential cleanup is pending. Retry deletion.");
        try
        {
            var client = await clients.CreateAsync(account.OrganizationId, account.Id, ct);
            await ProbeAsync(client, ct);
            await VerifyExistingScopeAsync(account, client, ct);
            account.ValidationStatus = "valid";
        }
        catch (ProviderCatalogException ex) when (ex.Code == "provider_scope_unverified") { account.ValidationStatus = "scope_unverified"; }
        catch (HttpRequestException) { account.ValidationStatus = "invalid_or_unavailable"; }
        catch (SecretStoreUnavailableException) { account.ValidationStatus = "credentials_unavailable"; }
        account.LastValidatedAt = clock.GetUtcNow(); account.Revision++; account.UpdatedAt = clock.GetUtcNow();
        return new ProviderValidationResponse(account.ValidationStatus, account.LastValidatedAt.Value);
    }

    public async Task DeleteAsync(ProviderAccount account, CancellationToken ct)
    {
        await EnsureUnusedAsync(account, ct);
        account.Enabled = false; account.ValidationStatus = "deleting"; account.Revision++;
        await db.SaveChangesAsync(ct);
        // The disabled deletion intent is committed before touching OpenBao, so a crash
        // cannot leave an enabled account referring to removed credentials.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProviderCatalogService.LockAccountAsync(db, account.OrganizationId, account.Id, ct);
        await EnsureUnusedAsync(account, ct);
        var cleanup = await db.ProviderCredentialCleanups.Where(x => x.ProviderAccountId == account.Id).ToListAsync(ct);
        foreach (var item in cleanup) await secrets.DeleteAsync(new StoredSecretReference(item.StoreProvider, item.Path, item.Version), ct);
        if (!cleanup.Any(x => x.Path == account.CredentialPath))
            await secrets.DeleteAsync(new StoredSecretReference(account.CredentialStoreProvider, account.CredentialPath, account.CredentialVersion), ct);
        db.ProviderCredentialCleanups.RemoveRange(cleanup);
        db.ProviderAccounts.Remove(account);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Filter dates in memory as SQLite is used by development installations/tests.
        var pending = (await db.ProviderCredentialCleanups.AsNoTracking().ToListAsync(ct)).Where(x => x.NotBefore <= now).Take(50);
        foreach (var item in pending)
        {
            if (await db.ProviderAccounts.AnyAsync(x => x.CredentialPath == item.Path, ct)) continue;
            await secrets.DeleteAsync(new StoredSecretReference(item.StoreProvider, item.Path, item.Version), ct);
            db.ProviderCredentialCleanups.Remove(item);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<StoredSecretReference> WriteCandidateAsync(ProviderAccount account, string credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 8192 || credential.Any(char.IsControl))
            throw new ProviderCatalogException("provider_credential_invalid", "Enter a provider API credential without control characters.");
        var generation = Guid.NewGuid().ToString("N");
        var path = OpenBaoSecretStore.ProviderPathFor(options.Value.EnvironmentPrefix, account.OrganizationId, account.Id, generation);
        db.ProviderCredentialCleanups.Add(new ProviderCredentialCleanup
        {
            Id = ids.NewId("credential_cleanup"), OrganizationId = account.OrganizationId, ProviderAccountId = account.Id,
            Path = path, NotBefore = clock.GetUtcNow().AddDays(1)
        });
        // Write cleanup intent first. It is safe to delete an absent generation.
        await db.SaveChangesAsync(ct);
        var reference = await secrets.PutAsync(new SecretStoreWriteRequest(account.OrganizationId, "", "", "credential", credential)
            { ProviderAccountId = account.Id, CredentialGeneration = generation }, ct);
        account.CredentialGeneration = generation;
        return reference;
    }

    private static void AssignCredential(ProviderAccount account, StoredSecretReference reference)
    {
        account.CredentialStoreProvider = reference.Provider; account.CredentialPath = reference.Path; account.CredentialVersion = reference.Version;
    }

    private static Task ProbeAsync(IVpsProviderClient client, CancellationToken ct) =>
        client.FindServerAsync("minicloud-credential-check-" + Guid.NewGuid().ToString("N"), ct);

    private async Task VerifyExistingScopeAsync(ProviderAccount account, IVpsProviderClient candidate, CancellationToken ct)
    {
        var servers = await db.ServerInstances.Include(x => x.App).ThenInclude(x => x.Organization)
            .Where(x => x.ProviderAccountId == account.Id).ToListAsync(ct);
        var runtimes = await db.RuntimeServers.Where(x => x.ProviderAccountId == account.Id).ToListAsync(ct);
        if (runtimes.Any(r => !servers.Any(s => s.ProviderServerId == r.ProviderServerId))) throw ScopeUnverified();
        foreach (var server in servers)
        {
            var name = ServerName(server.App.Organization.Slug, server.App.Slug);
            var observed = await candidate.FindServerAsync(name, ct);
            if (observed is null || observed.Id != server.ProviderServerId || observed.Name != name) throw ScopeUnverified();
        }
        var volumes = await db.AppStorageVolumes.Where(x => x.ProviderAccountId == account.Id).ToListAsync(ct);
        if (volumes.Count > 0 && candidate is not IStorageVolumeProvider) throw ScopeUnverified();
        foreach (var stored in volumes)
        {
            if (stored.ProviderVolumeId is null) throw ScopeUnverified();
            var observed = await ((IStorageVolumeProvider)candidate).GetVolumeAsync(stored.ProviderVolumeId, ct);
            if (observed is null || observed.Name != AppStorageService.VolumeName(stored.AppId, stored.Name)
                || observed.Region != stored.Region || !observed.Labels.TryGetValue("app-id", out var appId) || appId != stored.AppId) throw ScopeUnverified();
        }
        if (await db.Deployments.AnyAsync(x => x.ProviderAccountId == account.Id && x.ProviderCreationAttempted
            && !db.ServerInstances.Any(s => s.ProviderAccountId == account.Id && s.AppId == x.AppId), ct)) throw ScopeUnverified();
        if (servers.Count == 0 && volumes.Count == 0
            && (await db.Deployments.AnyAsync(x => x.ProviderAccountId == account.Id, ct)
                || await db.ProviderPlans.AnyAsync(x => x.ProviderAccountId == account.Id && x.ProvisioningLockedAt != null, ct)))
            throw ScopeUnverified();
    }

    private async Task RejectKnownScopeAliasAsync(ProviderAccount account, IVpsProviderClient candidate, CancellationToken ct)
    {
        var otherServers = await db.ServerInstances.Include(x => x.App).ThenInclude(x => x.Organization)
            .Where(x => x.ProviderAccountId != null && x.ProviderAccountId != account.Id && x.Provider == account.ProviderType.ToString()).ToListAsync(ct);
        foreach (var server in otherServers)
        {
            var name = ServerName(server.App.Organization.Slug, server.App.Slug);
            var observed = await candidate.FindServerAsync(name, ct);
            if (observed?.Name == name && observed.Id == server.ProviderServerId)
                throw new ProviderCatalogException("provider_scope_already_registered", "This cloud scope already contains resources registered through another provider entry. Use that entry instead.");
        }
    }

    private async Task EnsureUnusedAsync(ProviderAccount account, CancellationToken ct)
    {
        var plans = await db.ProviderPlans.CountAsync(x => x.ProviderAccountId == account.Id, ct);
        var servers = await db.ServerInstances.CountAsync(x => x.ProviderAccountId == account.Id, ct);
        var runtimes = await db.RuntimeServers.CountAsync(x => x.ProviderAccountId == account.Id, ct);
        var volumes = await db.AppStorageVolumes.CountAsync(x => x.ProviderAccountId == account.Id, ct);
        var reservations = await db.Deployments.CountAsync(x => x.ProviderAccountId == account.Id, ct);
        if (plans + servers + runtimes + volumes + reservations > 0)
            throw new ProviderCatalogException("provider_in_use", $"Provider has dependencies: {plans} plans, {servers} servers, {runtimes} runtimes, {volumes} retained volumes, {reservations} reservations. Disable it instead.");
    }

    public static bool TryParseType(string value, out CloudProvider type)
    {
        type = value?.ToLowerInvariant() switch { "hetzner" => CloudProvider.Hetzner, "digital-ocean" or "digitalocean" => CloudProvider.DigitalOcean, "vultr" => CloudProvider.Vultr, _ => (CloudProvider)(-1) };
        return Enum.IsDefined(type);
    }
    public static string WireType(CloudProvider type) => type switch { CloudProvider.Hetzner => "hetzner", CloudProvider.DigitalOcean => "digital-ocean", CloudProvider.Vultr => "vultr", _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    private static string ServerName(string orgSlug, string appSlug) => $"minicloud-{orgSlug}-production-{appSlug}".ToLowerInvariant();
    private static ProviderCatalogException ScopeUnverified() => new("provider_scope_unverified", "Credentials could not be verified against all existing resources. Restore access to the original cloud scope before replacing credentials.");
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || name.Any(char.IsControl))
            throw new ProviderCatalogException("provider_name_invalid", "Use a provider name of 1 to 128 characters without control characters.");
    }
}
