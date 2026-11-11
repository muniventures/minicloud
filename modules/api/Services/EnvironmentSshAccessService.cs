using System.Net;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;
using Minicloud.Common.Contracts;

namespace Minicloud.Api.Services;

public sealed class SshAccessException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class EnvironmentSshAccessService(
    MinicloudDbContext db,
    ControlPlaneAuthorization authorization,
    ProviderAccountClientFactory providerClients,
    IIdGenerator ids,
    TimeProvider clock)
{
    public async Task ApplySelectionAsync(
        MinicloudApp environment,
        string? providerAccountId,
        SshAccessRequest request,
        ActorIdentity actor,
        bool isPatch,
        CancellationToken ct)
    {
        ValidateRequestShape(request, isPatch);
        if (isPatch)
        {
            if (request.ExpectedRevision is null || request.ExpectedRevision < 0)
                throw Invalid("PATCH SSH access requires a nonnegative expectedRevision.");
            if (request.ExpectedRevision != environment.SshRevision)
                throw new SshAccessException("ssh_selection_revision_conflict", "The SSH selection changed. Reload the environment before saving.", 409);
        }

        var mode = request.Mode;
        string? keyId = null;
        string? name = null;
        string? publicKey = null;
        string? fingerprint = null;
        string? keyType = null;
        string state = "not_registered";
        string? selectionAccountId = null;

        if (mode == "existing")
        {
            if (string.IsNullOrWhiteSpace(providerAccountId))
                throw new SshAccessException("ssh_key_not_found", "Select an existing key from the environment's provider account.", 404);
            if (!await CanAsync(actor, environment.OrganizationId, OrganizationAction.ReadProviders, ct))
                throw new SshAccessException("forbidden", "The providers:read permission is required to select an existing SSH key.", 403);
            var stored = await db.ProviderSshKeys.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == request.KeyId && x.OrganizationId == environment.OrganizationId &&
                x.ProviderAccountId == providerAccountId, ct);
            if (stored is null || stored.RegistrationState is not ("discovered" or "ready"))
                throw new SshAccessException("ssh_key_not_found", "The selected SSH key was not found in this provider account. Refresh the key inventory.", 404);
            if (!SshPublicKeyParser.TryParse(stored.PublicKey, out var validated) ||
                validated!.Fingerprint != stored.Fingerprint || validated.KeyType != stored.KeyType)
                throw new SshAccessException("ssh_key_changed", "The selected SSH key material is no longer valid. Refresh the key inventory.", 409);
            keyId = stored.Id;
            name = stored.Name;
            publicKey = validated.PublicKey;
            fingerprint = stored.Fingerprint;
            keyType = stored.KeyType;
            state = stored.RegistrationState;
            selectionAccountId = providerAccountId;
        }
        else if (mode == "new")
        {
            if (string.IsNullOrWhiteSpace(providerAccountId))
                throw new SshAccessException("ssh_key_not_found", "Select a provider account before adding a new SSH key.", 404);
            if (!await CanAsync(actor, environment.OrganizationId, OrganizationAction.ManageProviders, ct))
                throw new SshAccessException("forbidden", "The providers:write permission is required to register an SSH key.", 403);
            name = NormalizeName(request.Name);
            if (!SshPublicKeyParser.TryParse(request.PublicKey, out var validated))
                throw Invalid("Provide one valid OpenSSH Ed25519 or RSA (3072-bit minimum) public key.");
            publicKey = validated!.PublicKey;
            fingerprint = validated.Fingerprint;
            keyType = validated.KeyType;
            selectionAccountId = providerAccountId;
        }

        var unchanged = environment.SshAccessMode == mode &&
            environment.SshProviderAccountId == selectionAccountId &&
            environment.SshKeyId == keyId &&
            environment.SshKeyName == name &&
            environment.SshPublicKey == publicKey &&
            environment.SshFingerprint == fingerprint &&
            environment.SshKeyType == keyType;
        if (unchanged)
            return;

        environment.SshAccessMode = mode;
        environment.SshProviderAccountId = selectionAccountId;
        environment.SshKeyId = keyId;
        environment.SshKeyName = name;
        environment.SshPublicKey = publicKey;
        environment.SshFingerprint = fingerprint;
        environment.SshKeyType = keyType;
        environment.SshRegistrationState = state;
        environment.SshRevision++;
        environment.SshSelectedBy = actor.Describe();
        environment.SshSelectedAt = clock.GetUtcNow();
        environment.UpdatedAt = clock.GetUtcNow();
    }

    public async Task<IReadOnlyList<ProviderSshKeyResponse>> ListKeysAsync(
        string organizationId,
        string providerAccountId,
        ActorIdentity actor,
        CancellationToken ct)
    {
        if (!await CanAsync(actor, organizationId, OrganizationAction.ReadProviders, ct))
            throw new SshAccessException("forbidden", "The providers:read permission is required to list SSH keys.", 403);

        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == providerAccountId && x.OrganizationId == organizationId, ct);
        if (account is null)
            throw new SshAccessException("provider_not_found", "Provider account was not found in this organization.", 404);

        IVpsProviderClient client;
        try { client = await providerClients.CreateAsync(organizationId, providerAccountId, ct); }
        catch (ProviderCatalogException ex)
        {
            throw new SshAccessException(ex.Code, ex.Message, ex.Code == "provider_not_found" ? 404 : 503);
        }
        if (client is not IVpsProviderSshKeyClient sshClient)
            throw InventoryUnavailable();

        IReadOnlyList<ProviderSshKeyInfo> providerKeys;
        try { providerKeys = await sshClient.ListSshKeysAsync(ct); }
        catch (ProviderApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new SshAccessException("forbidden", "The provider account does not have permission to list SSH keys.", 403);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            throw InventoryUnavailable();
        }

        var now = clock.GetUtcNow();
        var result = new List<ProviderSshKeyResponse>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql())
            await db.ProviderAccounts.FromSqlInterpolated($"SELECT * FROM provider_accounts WHERE id = {providerAccountId} AND organization_id = {organizationId} FOR UPDATE").AsNoTracking().SingleAsync(ct);

        foreach (var providerKey in providerKeys)
        {
            if (string.IsNullOrWhiteSpace(providerKey.Id) ||
                !SshPublicKeyParser.TryParse(providerKey.PublicKey, out var validated))
                continue;

            var local = await db.ProviderSshKeys.SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.ProviderAccountId == providerAccountId &&
                (x.ProviderKeyId == providerKey.Id || x.Fingerprint == validated!.Fingerprint), ct);
            if (local is null)
            {
                local = new ProviderSshKey
                {
                    Id = ids.NewId("sshkey"),
                    OrganizationId = organizationId,
                    ProviderAccountId = providerAccountId,
                    ProviderKeyId = providerKey.Id,
                    Name = string.IsNullOrWhiteSpace(providerKey.Name) ? "SSH key" : providerKey.Name,
                    PublicKey = validated!.PublicKey,
                    Fingerprint = validated.Fingerprint,
                    KeyType = validated.KeyType,
                    RegistrationState = "discovered",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.ProviderSshKeys.Add(local);
            }
            else if (!string.Equals(local.Fingerprint, validated!.Fingerprint, StringComparison.Ordinal))
            {
                local.RegistrationState = "unknown";
                local.UpdatedAt = now;
            }
            else
            {
                local.ProviderKeyId = providerKey.Id;
                local.Name = string.IsNullOrWhiteSpace(providerKey.Name) ? local.Name : providerKey.Name;
                local.PublicKey = validated.PublicKey;
                if (local.RegistrationState is "pending" or "registering" or "unknown" or "failed")
                {
                    local.RegistrationState = "ready";
                    local.RegisteredAt ??= now;
                }
                local.UpdatedAt = now;
            }

            result.Add(new ProviderSshKeyResponse(local.Id, local.ProviderAccountId, local.Name,
                local.Fingerprint, local.KeyType, local.RegistrationState));
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Fingerprint, StringComparer.Ordinal).ToArray();
    }

    private Task<bool> CanAsync(ActorIdentity actor, string organizationId, OrganizationAction action, CancellationToken ct) =>
        authorization.CanAccessOrganizationAsync(actor, organizationId, action, ct);

    private static void ValidateRequestShape(SshAccessRequest request, bool isPatch)
    {
        if (string.IsNullOrWhiteSpace(request.Mode))
            throw Invalid("SSH access mode is required.");
        if (request.ExpectedRevision is not null && !isPatch)
            throw Invalid("expectedRevision is only accepted when updating an environment.");

        var shapeValid = request.Mode switch
        {
            "none" => request.KeyId is null && request.Name is null && request.PublicKey is null,
            "existing" => !string.IsNullOrWhiteSpace(request.KeyId) && request.Name is null && request.PublicKey is null,
            "new" => request.KeyId is null && !string.IsNullOrWhiteSpace(request.Name) && !string.IsNullOrWhiteSpace(request.PublicKey),
            _ => false
        };
        if (!shapeValid)
            throw Invalid("SSH access must be exactly one of none, existing with keyId, or new with name and publicKey.");
    }

    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || name.Any(char.IsControl))
            throw Invalid("SSH key names must contain 1 to 128 characters without control characters.");
        return name.Trim();
    }

    private static SshAccessException Invalid(string message) => new("ssh_key_invalid", message, 400);
    private static SshAccessException InventoryUnavailable() =>
        new("ssh_inventory_unavailable", "The provider SSH key inventory is temporarily unavailable.", 503);
}
