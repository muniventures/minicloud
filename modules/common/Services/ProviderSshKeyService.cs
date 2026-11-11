using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Services;

public sealed class ProviderSshKeyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ProviderSshKeyService(
    MinicloudDbContext db,
    ProviderAccountClientFactory clients,
    ControlPlaneAuthorization authorization,
    IIdGenerator ids,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<string>> ResolveForAllocationAsync(
        string deploymentId,
        string organizationId,
        string appId,
        CancellationToken ct)
    {
        var deployment = await db.Deployments.SingleAsync(x =>
            x.Id == deploymentId && x.OrganizationId == organizationId && x.AppId == appId, ct);
        if (deployment.SshSelectionMode is null or "none")
        {
            deployment.SshProviderReferencesJson = "[]";
            deployment.SshRegistrationState = "not_registered";
            await db.SaveChangesAsync(ct);
            return [];
        }

        if (deployment.SshProviderAccountId is null || deployment.SshFingerprint is null ||
            deployment.SshKeyType is null || deployment.SshSelectionRevision is null ||
            !SshPublicKeyParser.TryParse(deployment.SshPublicKey, out var validated) ||
            validated!.Fingerprint != deployment.SshFingerprint || validated.KeyType != deployment.SshKeyType)
            throw new ProviderSshKeyException("ssh_key_changed", "The reserved SSH key selection is incomplete or changed.");

        await ValidateSelectingActorAsync(deployment, organizationId, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        ProviderAccount account;
        try { account = await ProviderCatalogService.LockAccountAsync(db, organizationId, deployment.SshProviderAccountId, ct); }
        catch (ProviderCatalogException)
        {
            throw new ProviderSshKeyException("ssh_key_changed", "The reserved SSH provider account is unavailable.");
        }
        if (!string.Equals(deployment.SshCredentialGeneration, account.CredentialGeneration, StringComparison.Ordinal))
            throw new ProviderSshKeyException("ssh_key_changed", "The provider account scope changed after the SSH selection was reserved.");

        IVpsProviderClient vpsClient;
        try { vpsClient = await clients.CreateAsync(organizationId, account.Id, ct); }
        catch (SecretStoreUnavailableException)
        {
            throw new ProviderSshKeyException("ssh_inventory_unavailable", "The reserved provider account credentials are unavailable.");
        }
        if (vpsClient is not IVpsProviderSshKeyClient sshClient)
            throw new ProviderSshKeyException("ssh_inventory_unavailable", "The provider SSH key inventory is unavailable.");

        var externalKeys = await ListKeysAsync(sshClient, ct);
        var external = externalKeys.FirstOrDefault(x =>
            SshPublicKeyParser.TryParse(x.PublicKey, out var candidate) &&
            candidate!.Fingerprint == deployment.SshFingerprint);
        var record = await db.ProviderSshKeys.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ProviderAccountId == account.Id &&
            (x.Id == deployment.SshKeyId || x.Fingerprint == deployment.SshFingerprint), ct);

        if (deployment.SshSelectionMode == "existing")
        {
            if (record is null || external is null || record.Fingerprint != deployment.SshFingerprint)
                await FailSelectionAsync(deployment, appId, "failed", ct, transaction,
                    new ProviderSshKeyException("ssh_key_changed", "The selected provider SSH key is missing or has changed. Select another key."));
            record!.ProviderKeyId = external!.Id;
            record.RegistrationState = "ready";
            record.UpdatedAt = clock.GetUtcNow();
            deployment.SshRegistrationState = "ready";
            deployment.SshProviderReferencesJson = JsonSerializer.Serialize(new[] { external.Id });
            await SaveEnvironmentStateAsync(deployment, appId, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return [external.Id];
        }

        if (deployment.SshSelectionMode != "new" || deployment.SshPublicKey is null ||
            deployment.SshProviderAccountId != account.Id)
            await FailSelectionAsync(deployment, appId, "failed", ct, transaction,
                new ProviderSshKeyException("ssh_key_changed", "The reserved SSH selection cannot be applied to this provider account."));

        var newRecord = record is null;
        record ??= new ProviderSshKey
        {
            Id = ids.NewId("sshkey"),
            OrganizationId = organizationId,
            ProviderAccountId = account.Id,
            Name = deployment.SshKeyName ?? "SSH key",
            PublicKey = validated.PublicKey,
            Fingerprint = validated.Fingerprint,
            KeyType = validated.KeyType,
            RegistrationState = "pending",
            CreatedBy = (await GetSelectingActorAsync(deployment, organizationId, ct)).Describe(),
            CreatedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow()
        };
        if (newRecord)
            db.ProviderSshKeys.Add(record);

        if (external is null)
        {
            record.RegistrationState = "registering";
            record.UpdatedAt = clock.GetUtcNow();
            deployment.SshRegistrationState = "registering";
            await db.SaveChangesAsync(ct);
            await SaveEnvironmentStateAsync(deployment, appId, ct);
            try
            {
                external = await sshClient.RegisterSshKeyAsync(record.Name, validated.PublicKey, ct);
                if (string.IsNullOrWhiteSpace(external.Id) ||
                    (external.PublicKey is not null &&
                     (!SshPublicKeyParser.TryParse(external.PublicKey, out var returned) ||
                      returned!.Fingerprint != deployment.SshFingerprint)))
                    throw new ProviderSshKeyException("ssh_key_changed", "The provider returned a different SSH key.");
            }
            catch (Exception ex) when (ex is ProviderApiException or HttpRequestException or OperationCanceledException)
            {
                external = await TryReconcileAsync(sshClient, deployment.SshFingerprint, ct);
                if (external is null)
                {
                    var definiteRejection = ex is ProviderApiException { StatusCode: HttpStatusCode.BadRequest or
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests };
                    var error = definiteRejection
                        ? new ProviderSshKeyException("ssh_registration_failed", "The provider rejected SSH key registration.")
                        : new ProviderSshKeyException("ssh_registration_unknown", "SSH key registration outcome is unknown; reconcile provider inventory before retrying.");
                    record.RegistrationState = definiteRejection ? "failed" : "unknown";
                    record.UpdatedAt = clock.GetUtcNow();
                    deployment.SshRegistrationState = record.RegistrationState;
                    await SaveEnvironmentStateAsync(deployment, appId, ct);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    throw error;
                }
            }
        }

        record.ProviderKeyId = external.Id;
        record.Name = string.IsNullOrWhiteSpace(external.Name) ? record.Name : external.Name;
        record.PublicKey = validated.PublicKey;
        record.RegistrationState = "ready";
        record.RegisteredAt ??= clock.GetUtcNow();
        record.UpdatedAt = clock.GetUtcNow();
        deployment.SshRegistrationState = "ready";
        deployment.SshProviderReferencesJson = JsonSerializer.Serialize(new[] { external.Id });
        await SaveEnvironmentStateAsync(deployment, appId, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return [external.Id];
    }

    private async Task ValidateSelectingActorAsync(Deployment deployment, string organizationId, CancellationToken ct)
    {
        var actor = await GetSelectingActorAsync(deployment, organizationId, ct);
        var writeApps = await authorization.CanAccessOrganizationAsync(actor, organizationId, OrganizationAction.WriteApps, ct);
        var required = deployment.SshSelectionMode == "new" ? OrganizationAction.ManageProviders : OrganizationAction.ReadProviders;
        if (!writeApps || !await authorization.CanAccessOrganizationAsync(actor, organizationId, required, ct))
            throw new ProviderSshKeyException("ssh_key_changed", "The actor who selected this SSH key no longer has the required permissions.");
    }

    private async Task<ActorIdentity> GetSelectingActorAsync(Deployment deployment, string organizationId, CancellationToken ct)
    {
        var recorded = deployment.SshSelectingActor;
        if (string.IsNullOrWhiteSpace(recorded))
            throw new ProviderSshKeyException("ssh_key_changed", "The actor who selected this SSH key is unavailable.");
        if (recorded.StartsWith("api_key:", StringComparison.Ordinal))
        {
            var id = recorded["api_key:".Length..];
            // Expiry is checked in memory because Sqlite cannot translate DateTimeOffset comparisons.
            var apiKey = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == id && x.OrganizationId == organizationId && x.RevokedAt == null, ct);
            if (apiKey is null || (apiKey.ExpiresAt is not null && apiKey.ExpiresAt <= clock.GetUtcNow()))
                throw new ProviderSshKeyException("ssh_key_changed", "The API key that selected SSH access is no longer active.");
            return new ActorIdentity("api_key", null, id, organizationId,
                (JsonSerializer.Deserialize<string[]>(apiKey.ScopesJson) ?? []).ToHashSet(StringComparer.Ordinal));
        }
        return new ActorIdentity("browser_session", recorded, null, null, new HashSet<string>(StringComparer.Ordinal));
    }

    private async Task SaveEnvironmentStateAsync(Deployment deployment, string appId, CancellationToken ct)
    {
        var environment = await db.Apps.SingleAsync(x => x.Id == appId, ct);
        environment.SshRegistrationState = deployment.SshRegistrationState ?? "unknown";
        environment.UpdatedAt = clock.GetUtcNow();
    }

    private async Task FailSelectionAsync(
        Deployment deployment,
        string appId,
        string state,
        CancellationToken ct,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        ProviderSshKeyException error)
    {
        deployment.SshRegistrationState = state;
        await SaveEnvironmentStateAsync(deployment, appId, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        throw error;
    }

    private static async Task<IReadOnlyList<ProviderSshKeyInfo>> ListKeysAsync(IVpsProviderSshKeyClient client, CancellationToken ct)
    {
        try { return await client.ListSshKeysAsync(ct); }
        catch (Exception ex) when (ex is ProviderApiException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            throw new ProviderSshKeyException("ssh_inventory_unavailable", "The provider SSH key inventory is unavailable.");
        }
    }

    private static async Task<ProviderSshKeyInfo?> TryReconcileAsync(
        IVpsProviderSshKeyClient client, string fingerprint, CancellationToken ct)
    {
        try
        {
            return (await client.ListSshKeysAsync(ct)).FirstOrDefault(x =>
                SshPublicKeyParser.TryParse(x.PublicKey, out var key) && key!.Fingerprint == fingerprint);
        }
        catch (Exception ex) when (ex is ProviderApiException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            return null;
        }
    }
}
