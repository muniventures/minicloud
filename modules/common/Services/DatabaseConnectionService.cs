using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;

namespace Minicloud.Common.Services;

public sealed class DatabaseConnectionService(MinicloudDbContext db, ControlPlaneAuthorization authorization, SecretEncryptionService encryption, AuditEventService audit)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DatabaseConnectionResponse> RevealAsync(string databaseId, ActorIdentity actor, CancellationToken ct)
    {
        var database = await db.ManagedDatabases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == databaseId, ct)
            ?? throw new KeyNotFoundException("Database not found.");
        if (!await authorization.CanAccessOrganizationAsync(actor, database.OrganizationId, OrganizationAction.ManageDatabases, ct) ||
            !await authorization.CanAccessOrganizationAsync(actor, database.OrganizationId, OrganizationAction.ReadSecrets, ct))
        {
            throw new UnauthorizedAccessException();
        }
        if (database.Engine != "postgres" || database.Status != "ready" || database.EncryptedCredentialsJson is null)
        {
            throw new InvalidOperationException("Database connection credentials are unavailable.");
        }
        DatabaseConnectionCredentials credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(database.EncryptedCredentialsJson), JsonOptions)
                ?? throw new InvalidOperationException();
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException or FormatException or InvalidOperationException)
        {
            throw new InvalidOperationException("Database credential custody is unavailable.");
        }
        if (string.IsNullOrWhiteSpace(credentials.DatabaseName) || string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password) || credentials.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Database credential custody is unavailable.");
        }
        await audit.AppendAsync(database.OrganizationId, database.AppId, actor.Describe(), "database.credentials.revealed", JsonSerializer.Serialize(new { databaseId }), ct);
        return new(database.Id, database.Engine, database.AccessMode, credentials.DatabaseName, credentials.Username, credentials.Password, credentials.Host, credentials.Port, credentials.SslMode, !string.IsNullOrWhiteSpace(credentials.Host), credentials.CaCertificatePem);
    }
    public async Task<DatabaseBackupRecoveryMaterialResponse> ExportRecoveryMaterialAsync(string databaseId, ActorIdentity actor, CancellationToken ct)
    {
        var database = await db.ManagedDatabases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == databaseId, ct) ?? throw new KeyNotFoundException("Database not found.");
        if (!await authorization.CanAccessOrganizationAsync(actor, database.OrganizationId, OrganizationAction.ManageDatabases, ct) ||
            !await authorization.CanAccessOrganizationAsync(actor, database.OrganizationId, OrganizationAction.ReadSecrets, ct)) throw new UnauthorizedAccessException();
        var policy = await db.DatabaseBackupPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId, ct);
        if (policy?.EncryptedKeyMaterialJson is null) throw new InvalidOperationException("Backup recovery material is unavailable.");
        string key;
        string keyVersion;
        try
        {
            using var material = JsonDocument.Parse(encryption.Decrypt(policy.EncryptedKeyMaterialJson));
            key = material.RootElement.GetProperty("encryptionKey").GetString() ?? "";
            keyVersion = material.RootElement.GetProperty("keyVersion").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(keyVersion) || keyVersion != policy.KeyVersion)
                throw new InvalidOperationException();
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Backup recovery material is unavailable.");
        }
        await audit.AppendAsync(database.OrganizationId, null, actor.Describe(), "database.backup.recovery-material.exported", JsonSerializer.Serialize(new { databaseId, keyVersion }), ct);
        return new(database.Id, database.Engine, keyVersion, key);
    }
}
