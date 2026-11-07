using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public interface IAppSecretService
{
    Task<AppSecretValue> EnsurePostgresPasswordAsync(MinicloudApp app, string? requestedPassword, CancellationToken cancellationToken);
}

public sealed record AppSecretValue(string Value, bool ShouldUpdateDatabasePassword, bool WasGenerated);

public sealed class AppSecretService(
    MinicloudDbContext db,
    SecretEncryptionService encryption,
    IIdGenerator ids,
    TimeProvider timeProvider) : IAppSecretService
{
    public const string PostgresPasswordSecretName = "postgres_password";

    public async Task<AppSecretValue> EnsurePostgresPasswordAsync(MinicloudApp app, string? requestedPassword, CancellationToken cancellationToken)
    {
        var secret = await db.AppSecrets.SingleOrDefaultAsync(
            x => x.AppId == app.Id && x.Name == PostgresPasswordSecretName,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(requestedPassword))
        {
            var updatedAt = timeProvider.GetUtcNow();
            if (secret is null)
            {
                await UpsertSecretAsync(
                    app,
                    requestedPassword,
                    shouldUpdateExisting: true,
                    updatedAt,
                    cancellationToken);
            }
            else
            {
                secret.EncryptedValue = encryption.Encrypt(requestedPassword);
                secret.UpdatedAt = updatedAt;
            }

            return new AppSecretValue(requestedPassword, ShouldUpdateDatabasePassword: true, WasGenerated: false);
        }

        if (secret is not null)
        {
            return new AppSecretValue(encryption.Decrypt(secret.EncryptedValue), ShouldUpdateDatabasePassword: false, WasGenerated: false);
        }

        var password = GeneratePassword();
        var now = timeProvider.GetUtcNow();
        var inserted = await UpsertSecretAsync(
            app,
            password,
            shouldUpdateExisting: false,
            now,
            cancellationToken);
        if (inserted)
        {
            return new AppSecretValue(password, ShouldUpdateDatabasePassword: true, WasGenerated: true);
        }

        secret = await db.AppSecrets.AsNoTracking().SingleOrDefaultAsync(
            x => x.AppId == app.Id && x.Name == PostgresPasswordSecretName,
            cancellationToken);
        if (secret is null)
        {
            throw new InvalidOperationException("Postgres password secret was not created and no existing secret was found.");
        }

        return new AppSecretValue(encryption.Decrypt(secret.EncryptedValue), ShouldUpdateDatabasePassword: false, WasGenerated: false);
    }

    private async Task<bool> UpsertSecretAsync(
        MinicloudApp app,
        string value,
        bool shouldUpdateExisting,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var encryptedValue = encryption.Encrypt(value);
        var rows = shouldUpdateExisting
            ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO app_secrets (id, organization_id, app_id, name, encrypted_value, created_at, updated_at)
                VALUES ({ids.NewId("sec")}, {app.OrganizationId}, {app.Id}, {PostgresPasswordSecretName}, {encryptedValue}, {updatedAt}, {updatedAt})
                ON CONFLICT (app_id, name) DO UPDATE SET encrypted_value = excluded.encrypted_value, updated_at = excluded.updated_at
                """, cancellationToken)
            : await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO app_secrets (id, organization_id, app_id, name, encrypted_value, created_at, updated_at)
                VALUES ({ids.NewId("sec")}, {app.OrganizationId}, {app.Id}, {PostgresPasswordSecretName}, {encryptedValue}, {updatedAt}, {updatedAt})
                ON CONFLICT (app_id, name) DO NOTHING
                """, cancellationToken);

        return rows > 0;
    }

    private static string GeneratePassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
